namespace CATRA.Core.Models;

/// <summary>
/// Payload for <see cref="Interfaces.IPlaybackEngine.Error"/> (ST-05).
/// </summary>
public sealed class PlaybackErrorEventArgs : EventArgs
{
    /// <summary>Human-readable description of the failure.</summary>
    public string Message { get; }

    /// <summary>The underlying exception, when one was caught.</summary>
    public Exception? Exception { get; }

    public PlaybackErrorEventArgs(string message, Exception? exception = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Message = message;
        Exception = exception;
    }
}
