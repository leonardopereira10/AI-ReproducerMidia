using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Playback;

/// <summary>
/// Orchestrates decoding, rendering and audio for local video playback (ST-05).
/// </summary>
/// <remarks>
/// <para>
/// The four collaborators (<see cref="IVideoDecoder"/>, <see cref="IAudioDecoder"/>,
/// <see cref="IVideoRenderer"/>, <see cref="IAudioRenderer"/>) are supplied as
/// factories so the engine is fully unit-testable with fakes — no FFmpeg binaries,
/// GPU or audio device required. A dedicated decode task pumps audio and video;
/// audio is the master clock (the engine periodically re-anchors a <see cref="Clock"/>
/// to the audio renderer's consumed-sample position) and video frames are presented
/// once the clock reaches their presentation timestamp.
/// </para>
/// <para>
/// Decoder mutations (seek) are serialised against the decode loop through
/// <c>_loopGate</c> so a seek never races an in-flight read.
/// </para>
/// </remarks>
public sealed class PlaybackEngine : IPlaybackEngine
{
    private static readonly TimeSpan DefaultPositionInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<IVideoDecoder> _videoDecoderFactory;
    private readonly Func<IAudioDecoder> _audioDecoderFactory;
    private readonly Func<IVideoRenderer> _videoRendererFactory;
    private readonly Func<IAudioRenderer> _audioRendererFactory;
    private readonly Clock _clock;
    private readonly TimeSpan _positionInterval;

    private readonly object _gate = new();
    private readonly object _loopGate = new();

    private IVideoDecoder? _videoDecoder;
    private IAudioDecoder? _audioDecoder;
    private IAudioRenderer? _audioRenderer;
    private IVideoRenderer? _videoRenderer;

    private VideoMetadata? _metadata;
    private List<AudioTrack> _audioTracks = new();

    private PlaybackState _state = PlaybackState.Stopped;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Timer? _positionTimer;

    private TimeSpan _baseOffset = TimeSpan.Zero;
    private IntPtr _windowHandle;
    private int _outputWidth;
    private int _outputHeight;
    private float _volume = 1.0f;
    private bool _disposed;

    public PlaybackEngine(
        Func<IVideoDecoder> videoDecoderFactory,
        Func<IAudioDecoder> audioDecoderFactory,
        Func<IVideoRenderer> videoRendererFactory,
        Func<IAudioRenderer> audioRendererFactory,
        Clock? clock = null,
        TimeSpan? positionReportInterval = null)
    {
        _videoDecoderFactory = videoDecoderFactory ?? throw new ArgumentNullException(nameof(videoDecoderFactory));
        _audioDecoderFactory = audioDecoderFactory ?? throw new ArgumentNullException(nameof(audioDecoderFactory));
        _videoRendererFactory = videoRendererFactory ?? throw new ArgumentNullException(nameof(videoRendererFactory));
        _audioRendererFactory = audioRendererFactory ?? throw new ArgumentNullException(nameof(audioRendererFactory));
        _clock = clock ?? new Clock();
        _positionInterval = positionReportInterval ?? DefaultPositionInterval;
    }

    /// <inheritdoc />
    public PlaybackState State
    {
        get { lock (_gate) { return _state; } }
    }

    /// <inheritdoc />
    public VideoMetadata? Metadata
    {
        get { lock (_gate) { return _metadata; } }
    }

    /// <inheritdoc />
    public IReadOnlyList<AudioTrack> AudioTracks
    {
        get { lock (_gate) { return _audioTracks; } }
    }

    /// <inheritdoc />
    public event EventHandler<TimeSpan>? PositionChanged;

    /// <inheritdoc />
    public event EventHandler<PlaybackState>? StateChanged;

    /// <inheritdoc />
    public event EventHandler? MediaEnded;

    /// <inheritdoc />
    public event EventHandler<PlaybackErrorEventArgs>? Error;

    /// <inheritdoc />
    public async Task<VideoMetadata> OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != PlaybackState.Stopped)
            {
                throw new InvalidOperationException("Stop the current media before opening another.");
            }
        }

        IVideoDecoder videoDecoder = _videoDecoderFactory();
        IAudioDecoder audioDecoder = _audioDecoderFactory();
        IAudioRenderer audioRenderer = _audioRendererFactory();

        try
        {
            await videoDecoder.OpenAsync(filePath, cancellationToken).ConfigureAwait(false);
            audioDecoder.Open(filePath);
            audioRenderer.Initialize(audioDecoder.SampleRate, audioDecoder.Channels);
            audioRenderer.Volume = _volume;
        }
        catch
        {
            videoDecoder.Dispose();
            audioDecoder.Dispose();
            audioRenderer.Dispose();
            throw;
        }

        lock (_gate)
        {
            _videoDecoder = videoDecoder;
            _audioDecoder = audioDecoder;
            _audioRenderer = audioRenderer;
            _metadata = videoDecoder.Metadata;
            _audioTracks = new List<AudioTrack>(videoDecoder.AudioTracks);
            _baseOffset = TimeSpan.Zero;
            _clock.Reset();
            return _metadata;
        }
    }

    /// <inheritdoc />
    public void SetOutputWindow(IntPtr windowHandle)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _windowHandle = windowHandle;

            // The handle may arrive after Play() already created the renderer
            // unbound; bind it as soon as it becomes available so Resize and
            // Present never hit an uninitialized renderer.
            if (_videoRenderer is { IsInitialized: false })
            {
                EnsureRendererInitialized();
            }
        }
    }

    /// <inheritdoc />
    public void ResizeOutput(int width, int height)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _outputWidth = width;
            _outputHeight = height;

            // A resize can race ahead of Initialize (the window handle arrives
            // later via SetOutputWindow); bind the pending size instead of
            // resizing an uninitialized renderer.
            if (_videoRenderer is { IsInitialized: false })
            {
                EnsureRendererInitialized();
            }

            if (_videoRenderer is { IsInitialized: true })
            {
                _videoRenderer.Resize(width, height);
            }
        }
    }

    /// <inheritdoc />
    public void Play()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureOpen();

            if (_state == PlaybackState.Playing)
            {
                return;
            }

            EnsureRendererInitialized();

            if (_state == PlaybackState.Stopped)
            {
                _cts = new CancellationTokenSource();
                CancellationToken token = _cts.Token;
                _loopTask = Task.Run(() => DecodeLoopAsync(token));
                _positionTimer = new Timer(ReportPosition, null, _positionInterval, _positionInterval);
            }

            _audioRenderer!.Play();
            _clock.Start();
            SetState(PlaybackState.Playing);
        }
    }

    /// <inheritdoc />
    public void Pause()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != PlaybackState.Playing)
            {
                return;
            }

            _clock.Pause();
            _audioRenderer!.Pause();
            SetState(PlaybackState.Paused);
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        Task? loopToAwait;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            loopToAwait = BeginStop();
        }

        // Await the decode loop outside the lock so it can finish its iteration.
        loopToAwait?.GetAwaiter().GetResult();

        lock (_gate)
        {
            FinishStop(resetClock: true);
        }
    }

    /// <inheritdoc />
    public void Seek(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(position), "Seek position cannot be negative.");
        }

        PlaybackState resumeState;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureOpen();
            if (_state == PlaybackState.Stopped)
            {
                return;
            }

            resumeState = _state == PlaybackState.Playing ? PlaybackState.Playing : PlaybackState.Paused;
            SetState(PlaybackState.Seeking);
        }

        TimeSpan target = ClampToDuration(position);

        // Serialise against the decode loop so no read is in flight during the flush.
        lock (_loopGate)
        {
            _videoDecoder!.Seek(target);
            _audioDecoder!.Seek(target);
            _audioRenderer!.Flush();
            _baseOffset = target;
            _clock.Set(target);
        }

        lock (_gate)
        {
            if (resumeState == PlaybackState.Playing)
            {
                // Flush() rebuilt the WASAPI output in the Stopped state; restart it,
                // otherwise audio stays silent after the seek while the master clock
                // drifts forward (position grows with no samples being consumed).
                _audioRenderer!.Play();
                _clock.Start();
            }
            else
            {
                _clock.Pause();
            }

            SetState(resumeState);
        }
    }

    /// <inheritdoc />
    public void SetVolume(float volume)
    {
        float clamped = ClampVolume(volume);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _volume = clamped;
            if (_audioRenderer is not null)
            {
                _audioRenderer.Volume = clamped;
            }
        }
    }

    /// <inheritdoc />
    public TimeSpan GetPosition()
    {
        lock (_gate)
        {
            return _clock.Current;
        }
    }

    private async Task DecodeLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Cooperative pause: freeze without tearing down the loop so resume is instant.
                if (State == PlaybackState.Paused || State == PlaybackState.Seeking)
                {
                    await Task.Delay(10, token).ConfigureAwait(false);
                    continue;
                }

                AudioFrame? audio;
                VideoFrame? video;
                lock (_loopGate)
                {
                    audio = _audioDecoder!.ReadSamples();
                    if (audio is not null)
                    {
                        _audioRenderer!.Write(audio.Samples, audio.Count);
                        // Audio is master: re-anchor the clock to consumed samples.
                        _clock.Set(_baseOffset + _audioRenderer.Position);
                    }

                    video = _videoDecoder!.ReadVideoFrame();
                }

                if (video is not null)
                {
                    // Gate video on the master clock only while audio is still flowing;
                    // once audio is exhausted present remaining frames as fast as possible.
                    if (audio is not null)
                    {
                        await WaitUntilDueAsync(video.PresentationTime, token).ConfigureAwait(false);
                    }

                    lock (_loopGate)
                    {
                        // Drop frames until the renderer is bound to a window;
                        // presenting an uninitialized renderer would crash the loop.
                        if (_videoRenderer is { IsInitialized: true })
                        {
                            _videoRenderer.Present(video);
                        }
                    }

                    video.Dispose();
                }

                if (audio is null && video is null)
                {
                    break; // natural end of stream
                }

                await Task.Yield();
            }

            if (!token.IsCancellationRequested)
            {
                HandleMediaEnded();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal teardown via Stop()/Dispose().
        }
        catch (Exception ex)
        {
            HandleError("Playback failed.", ex);
        }
    }

    private async Task WaitUntilDueAsync(TimeSpan presentationTime, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (State != PlaybackState.Playing)
            {
                return; // paused/seeking/stopped: stop blocking the loop
            }

            if (_clock.Current >= presentationTime)
            {
                return;
            }

            await Task.Delay(1, token).ConfigureAwait(false);
        }
    }

    private void HandleMediaEnded()
    {
        // Running on the loop task itself: it must not be awaited here, so tear
        // down inline under a single lock acquisition.
        lock (_gate)
        {
            BeginStop();
            FinishStop(resetClock: false);
        }

        MediaEnded?.Invoke(this, EventArgs.Empty);
    }

    private void HandleError(string message, Exception exception)
    {
        // Running on the loop task itself: it must not be awaited here, so tear
        // down inline under a single lock acquisition.
        lock (_gate)
        {
            BeginStop();
            FinishStop(resetClock: true);
        }

        Error?.Invoke(this, new PlaybackErrorEventArgs(message, exception));
    }

    /// <summary>Cancels the decode loop and position timer. Caller holds <c>_gate</c>.</summary>
    private Task? BeginStop()
    {
        _positionTimer?.Dispose();
        _positionTimer = null;

        if (_cts is not null)
        {
            _cts.Cancel();
        }

        Task? loop = _loopTask;
        return loop;
    }

    /// <summary>Releases per-media resources and moves to <c>Stopped</c>. Caller holds <c>_gate</c>.</summary>
    private void FinishStop(bool resetClock)
    {
        _cts?.Dispose();
        _cts = null;
        _loopTask = null;

        _audioRenderer?.Stop();
        _videoDecoder?.Dispose();
        _audioDecoder?.Dispose();
        _audioRenderer?.Dispose();

        _videoDecoder = null;
        _audioDecoder = null;
        _audioRenderer = null;
        _metadata = null;
        _audioTracks = new List<AudioTrack>();
        _baseOffset = TimeSpan.Zero;

        if (resetClock)
        {
            _clock.Reset();
        }

        SetState(PlaybackState.Stopped);
    }

    private void EnsureRendererInitialized()
    {
        if (_videoRenderer is null)
        {
            _videoRenderer = _videoRendererFactory();
        }

        if (_windowHandle != IntPtr.Zero && !_videoRenderer.IsInitialized)
        {
            int width = _outputWidth > 0 ? _outputWidth : _metadata!.Width;
            int height = _outputHeight > 0 ? _outputHeight : _metadata!.Height;
            _videoRenderer.Initialize(_windowHandle, width, height);
            _videoRenderer.Clear();
        }
    }

    private TimeSpan ClampToDuration(TimeSpan position)
    {
        TimeSpan duration = _metadata?.Duration ?? TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration)
        {
            return duration;
        }

        return position;
    }

    private void ReportPosition(object? _)
    {
        TimeSpan position;
        lock (_gate)
        {
            if (_state != PlaybackState.Playing)
            {
                return;
            }

            position = _clock.Current;
        }

        PositionChanged?.Invoke(this, position);
    }

    private void SetState(PlaybackState newState)
    {
        if (_state == newState)
        {
            return;
        }

        _state = newState;
        StateChanged?.Invoke(this, newState);
    }

    private void EnsureOpen()
    {
        if (_metadata is null || _videoDecoder is null)
        {
            throw new InvalidOperationException("No media is open. Call OpenAsync first.");
        }
    }

    private static float ClampVolume(float value)
    {
        if (float.IsNaN(value))
        {
            return 0f;
        }

        return Math.Clamp(value, 0f, 1f);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Task? loopToAwait;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            loopToAwait = BeginStop();
        }

        try
        {
            loopToAwait?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected during teardown.
        }

        lock (_gate)
        {
            _videoRenderer?.Dispose();
            _videoRenderer = null;
            _videoDecoder?.Dispose();
            _audioDecoder?.Dispose();
            _audioRenderer?.Dispose();
            _videoDecoder = null;
            _audioDecoder = null;
            _audioRenderer = null;
        }
    }
}
