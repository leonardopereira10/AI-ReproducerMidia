using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Tests.Playback;

/// <summary>
/// In-memory <see cref="IVideoDecoder"/> fake. Emits a pre-built list of software
/// BGRA frames then returns <c>null</c> (end of stream). Optionally throws on read
/// to exercise the engine's error path. No FFmpeg/GPU involved.
/// </summary>
internal sealed class FakeVideoDecoder : IVideoDecoder
{
    private readonly object _gate = new();
    private readonly Queue<VideoFrame> _frames = new();
    private readonly List<AudioTrack> _audioTracks = new();

    public VideoMetadata Metadata { get; set; } = new(
        Duration: TimeSpan.FromSeconds(10),
        Fps: 30,
        Width: 4,
        Height: 4,
        VideoCodec: "fake",
        AudioCodec: "fake-audio",
        Title: "fake-title",
        IsHardwareAccelerated: false);

    public IReadOnlyList<AudioTrack> AudioTracks => _audioTracks;

    public bool IsHardwareAccelerated => false;

    public bool Opened { get; private set; }

    public string? OpenedPath { get; private set; }

    public List<TimeSpan> SeekCalls { get; } = new();

    public Exception? ThrowOnRead { get; set; }

    public int FramesRead { get; private set; }

    public void AddTrack(AudioTrack track) => _audioTracks.Add(track);

    /// <summary>Queues <paramref name="count"/> tiny software frames with rising PTS.</summary>
    public void AddFrames(int count, TimeSpan? duration = null)
    {
        if (duration is not null)
        {
            Metadata = Metadata with { Duration = duration.Value };
        }

        lock (_gate)
        {
            for (int i = 0; i < count; i++)
            {
                byte[] bgra = new byte[Metadata.Width * Metadata.Height * 4];
                TimeSpan pts = TimeSpan.FromMilliseconds(i * 10);
                _frames.Enqueue(VideoFrame.CreateSoftware(pts, Metadata.Width, Metadata.Height, bgra));
            }
        }
    }

    public Task OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        Opened = true;
        OpenedPath = filePath;
        return Task.CompletedTask;
    }

    public VideoFrame? ReadVideoFrame()
    {
        if (ThrowOnRead is not null)
        {
            throw ThrowOnRead;
        }

        lock (_gate)
        {
            FramesRead++;
            return _frames.Count > 0 ? _frames.Dequeue() : null;
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            SeekCalls.Add(position);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            while (_frames.Count > 0)
            {
                _frames.Dequeue().Dispose();
            }
        }
    }
}

/// <summary>
/// In-memory <see cref="IAudioDecoder"/> fake. Emits a fixed number of float32
/// buffers (or an endless stream when <see cref="Infinite"/> is set) then <c>null</c>.
/// </summary>
internal sealed class FakeAudioDecoder : IAudioDecoder
{
    private readonly object _gate = new();

    public int SampleRate { get; set; } = 48000;

    public int Channels { get; set; } = 2;

    /// <summary>Number of buffers left to emit before returning null (ignored if Infinite).</summary>
    public int RemainingBuffers { get; set; }

    /// <summary>Samples per emitted buffer.</summary>
    public int SamplesPerBuffer { get; set; } = 480;

    /// <summary>When true, never reaches end of stream (used to hold the engine in Playing).</summary>
    public bool Infinite { get; set; }

    public bool Opened { get; private set; }

    public List<TimeSpan> SeekCalls { get; } = new();

    public void Open(string filePath, int audioStreamIndex = -1)
    {
        Opened = true;
    }

    public AudioFrame? ReadSamples()
    {
        lock (_gate)
        {
            if (!Infinite)
            {
                if (RemainingBuffers <= 0)
                {
                    return null;
                }

                RemainingBuffers--;
            }

            float[] samples = new float[SamplesPerBuffer * Channels];
            return new AudioFrame(samples, samples.Length, Channels, TimeSpan.Zero);
        }
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            SeekCalls.Add(position);
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>Recording <see cref="IVideoRenderer"/> fake (no GPU).</summary>
internal sealed class FakeVideoRenderer : IVideoRenderer
{
    private readonly object _gate = new();

    public bool IsInitialized { get; private set; }

    public IntPtr InitializedWindow { get; private set; }

    public int InitializedWidth { get; private set; }

    public int InitializedHeight { get; private set; }

    public int PresentCount { get; private set; }

    public int ClearCount { get; private set; }

    public List<(int Width, int Height)> ResizeCalls { get; } = new();

    public void Initialize(IntPtr windowHandle, int width, int height)
    {
        lock (_gate)
        {
            IsInitialized = true;
            InitializedWindow = windowHandle;
            InitializedWidth = width;
            InitializedHeight = height;
        }
    }

    public void Present(VideoFrame frame)
    {
        lock (_gate)
        {
            PresentCount++;
        }
    }

    public void Resize(int width, int height)
    {
        lock (_gate)
        {
            ResizeCalls.Add((width, height));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            ClearCount++;
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Recording <see cref="IAudioRenderer"/> fake (no audio device). Tracks consumed
/// samples so <see cref="Position"/> advances like the master clock reference.
/// </summary>
internal sealed class FakeAudioRenderer : IAudioRenderer
{
    private readonly object _gate = new();

    private int _sampleRate = 48000;
    private int _channels = 2;
    private long _framesConsumed;
    private float _volume = 1f;

    public bool Initialized { get; private set; }

    public int PlayCount { get; private set; }

    public int PauseCount { get; private set; }

    public int StopCount { get; private set; }

    public int FlushCount { get; private set; }

    public long SamplesWritten { get; private set; }

    public float Volume
    {
        get { lock (_gate) { return _volume; } }
        set { lock (_gate) { _volume = value; } }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                if (_sampleRate <= 0)
                {
                    return TimeSpan.Zero;
                }

                return TimeSpan.FromSeconds((double)_framesConsumed / _sampleRate);
            }
        }
    }

    public void Initialize(int sampleRate, int channels)
    {
        lock (_gate)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            Initialized = true;
        }
    }

    public void Write(float[] samples, int count)
    {
        lock (_gate)
        {
            SamplesWritten += count;
            if (_channels > 0)
            {
                _framesConsumed += count / _channels;
            }
        }
    }

    public void Play()
    {
        lock (_gate) { PlayCount++; }
    }

    public void Pause()
    {
        lock (_gate) { PauseCount++; }
    }

    public void Stop()
    {
        lock (_gate) { StopCount++; }
    }

    public void Flush()
    {
        lock (_gate)
        {
            FlushCount++;
            _framesConsumed = 0;
        }
    }

    public void Dispose()
    {
    }
}
