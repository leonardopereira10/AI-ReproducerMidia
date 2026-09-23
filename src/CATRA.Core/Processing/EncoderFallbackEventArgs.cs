namespace CATRA.Core.Processing;

/// <summary>
/// Typed event emitted when the encoder cascade falls back from one encoder to
/// another (subtask 02). The UI (Story 04) subscribes to
/// <c>ProcessingPipeline.EncoderFallback</c> to display deduplicated fallback
/// notifications to the user.
/// </summary>
public sealed class EncoderFallbackEventArgs : EventArgs
{
    /// <summary>Creates the fallback event.</summary>
    /// <param name="from">Encoder that failed or was unavailable (e.g. "AMF", "hevc_nvenc").</param>
    /// <param name="to">Encoder being tried next (e.g. "FFmpeg cascade", "libx265").</param>
    /// <param name="reason">Human-readable reason for the fallback.</param>
    public EncoderFallbackEventArgs(string from, string to, string reason)
    {
        From = from;
        To = to;
        Reason = reason;
    }

    /// <summary>Encoder that failed or was unavailable (e.g. "AMF", "hevc_nvenc").</summary>
    public string From { get; }

    /// <summary>Encoder being tried next (e.g. "FFmpeg cascade", "hevc_qsv", "libx265").</summary>
    public string To { get; }

    /// <summary>Human-readable reason for the fallback (includes error codes).</summary>
    public string Reason { get; }
}
