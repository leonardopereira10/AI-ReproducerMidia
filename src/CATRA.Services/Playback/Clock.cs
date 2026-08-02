using System.Diagnostics;

namespace CATRA.Services.Playback;

/// <summary>
/// Monotonic media clock used for A/V sync (ST-05). Audio is the master clock:
/// the audio pipeline periodically calls <see cref="Set"/> with the position of the
/// samples it has actually handed to the device, and the video pipeline reads
/// <see cref="Current"/> to decide when a frame is due.
/// </summary>
/// <remarks>
/// Backed by a <see cref="Stopwatch"/> so time keeps advancing between audio
/// updates without drifting with the wall clock. Thread-safe: the audio thread
/// writes while the video/UI threads read.
/// </remarks>
public sealed class Clock
{
    private readonly Stopwatch _stopwatch = new();
    private readonly object _gate = new();
    private TimeSpan _anchor;

    /// <summary>Whether the clock is currently advancing.</summary>
    public bool IsRunning
    {
        get { lock (_gate) { return _stopwatch.IsRunning; } }
    }

    /// <summary>The current media position.</summary>
    public TimeSpan Current
    {
        get
        {
            lock (_gate)
            {
                return _anchor + (_stopwatch.IsRunning ? _stopwatch.Elapsed : TimeSpan.Zero);
            }
        }
    }

    /// <summary>
    /// Resets the clock to <paramref name="position"/> and keeps the current
    /// running/stopped status. Used after a seek and when the audio pipeline
    /// re-synchronises the master clock.
    /// </summary>
    public void Set(TimeSpan position)
    {
        lock (_gate)
        {
            _anchor = position;
            _stopwatch.Restart();
            if (!_shouldBeRunning)
            {
                _stopwatch.Stop();
            }
        }
    }

    private bool _shouldBeRunning;

    /// <summary>Starts (or resumes) the clock from its current position.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _shouldBeRunning = true;
            _stopwatch.Start();
        }
    }

    /// <summary>Freezes the clock at its current position.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            _anchor += _stopwatch.Elapsed;
            _shouldBeRunning = false;
            _stopwatch.Reset();
        }
    }

    /// <summary>Stops and rewinds the clock to zero.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _anchor = TimeSpan.Zero;
            _shouldBeRunning = false;
            _stopwatch.Reset();
        }
    }
}
