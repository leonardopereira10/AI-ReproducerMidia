using CATRA.Core.Interfaces;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CATRA.Services.Playback;

/// <summary>
/// WASAPI audio output (ST-05) built on NAudio <see cref="WasapiOut"/> fed by a
/// <see cref="BufferedWaveProvider"/>. Accepts interleaved 32-bit float samples and
/// targets ~200 ms of latency.
/// </summary>
/// <remarks>
/// Not unit-tested directly (requires an audio device); validated manually. The
/// playback engine depends on <see cref="IAudioRenderer"/> and is tested with fakes.
/// </remarks>
public sealed class AudioRenderer : IAudioRenderer
{
    // ~200 ms WASAPI latency as required by the spec.
    private const int WasapiLatencyMs = 200;

    // Half a second of provider buffering absorbs decode jitter without adding
    // noticeable latency on top of the WASAPI buffer.
    private const double ProviderBufferSeconds = 0.5;

    private const int BytesPerFloatSample = 4;

    private readonly object _gate = new();

    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private WaveFormat? _waveFormat;
    private int _sampleRate;
    private int _channels;
    private long _bytesWritten;
    private float _volume = 1.0f;
    private bool _initialized;
    private bool _disposed;

    /// <inheritdoc />
    public float Volume
    {
        get
        {
            lock (_gate)
            {
                return _volume;
            }
        }

        set
        {
            lock (_gate)
            {
                _volume = ClampVolume(value);
                // Volume is applied by multiplying samples in Write(), not via WasapiOut.Volume
                // which would alter the system volume.
            }
        }
    }

    /// <inheritdoc />
    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                if (_sampleRate <= 0 || _channels <= 0)
                {
                    return TimeSpan.Zero;
                }

                long buffered = _buffer?.BufferedBytes ?? 0;
                long consumed = Math.Max(0, _bytesWritten - buffered);
                int bytesPerFrame = _channels * BytesPerFloatSample;
                double seconds = (double)consumed / bytesPerFrame / _sampleRate;
                return TimeSpan.FromSeconds(seconds);
            }
        }
    }

    /// <inheritdoc />
    public void Initialize(int sampleRate, int channels)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
            }

            if (channels <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channels), "Channel count must be positive.");
            }

            if (_initialized)
            {
                return;
            }

            _sampleRate = sampleRate;
            _channels = channels;
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

            RebuildOutput();
            _initialized = true;
        }
    }

    /// <summary>
    /// Tears down and recreates the WASAPI output plus its backing buffer. NAudio's
    /// <see cref="BufferedWaveProvider"/> exposes no Clear/flush API, so the only reliable
    /// way to discard buffered samples (e.g. on seek) is to build a fresh provider and
    /// re-initialize a fresh <see cref="WasapiOut"/> against it.
    /// </summary>
    private void RebuildOutput()
    {
        _output?.Dispose();
        _output = null;
        _buffer = null;

        _buffer = new BufferedWaveProvider(_waveFormat!)
        {
            BufferLength = (int)(_sampleRate * _channels * BytesPerFloatSample * ProviderBufferSeconds),
            DiscardOnBufferOverflow = true,
        };

        _output = new WasapiOut(AudioClientShareMode.Shared, WasapiLatencyMs);
        _output.Init(_buffer);
        // Do NOT set _output.Volume here - it alters the system volume.
        // Volume is applied by multiplying samples in Write().
    }

    /// <inheritdoc />
    public void Write(float[] samples, int count)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (count < 0 || count > samples.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        lock (_gate)
        {
            EnsureInitialized();

            if (count == 0)
            {
                return;
            }

            // Apply volume by multiplying samples (does not affect system volume)
            float[] adjustedSamples = samples;
            if (_volume != 1.0f)
            {
                adjustedSamples = new float[count];
                for (int i = 0; i < count; i++)
                {
                    adjustedSamples[i] = samples[i] * _volume;
                }
            }

            byte[] bytes = new byte[count * BytesPerFloatSample];
            Buffer.BlockCopy(adjustedSamples, 0, bytes, 0, bytes.Length);
            _buffer!.AddSamples(bytes, 0, bytes.Length);
            _bytesWritten += bytes.Length;
        }
    }

    /// <inheritdoc />
    public void Play()
    {
        lock (_gate)
        {
            EnsureInitialized();
            if (_output!.PlaybackState != PlaybackState.Playing)
            {
                _output.Play();
            }
        }
    }

    /// <inheritdoc />
    public void Pause()
    {
        lock (_gate)
        {
            if (_output is not null && _output.PlaybackState == PlaybackState.Playing)
            {
                _output.Pause();
            }
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate)
        {
            if (_output is not null && _output.PlaybackState != PlaybackState.Stopped)
            {
                _output.Stop();
            }
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        lock (_gate)
        {
            _bytesWritten = 0;
            if (_initialized)
            {
                // BufferedWaveProvider has no Clear(); rebuild to drop queued samples.
                RebuildOutput();
            }
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

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("Audio renderer has not been initialized.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _output?.Dispose();
            _output = null;
            _buffer = null;
        }
    }
}
