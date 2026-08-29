using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Processing;

/// <summary>
/// Production <see cref="IProcessedFileUsage"/> (ST-18, RF-04). Reports a processed
/// file as in use while local playback is active, a DLNA stream is in progress, or
/// a browser streaming session is serving the file, so window rotation never deletes
/// a file that could be being played or served.
/// </summary>
/// <remarks>
/// The playback engine and casting service do not (yet) expose the exact file path in
/// use, so those checks are deliberately conservative: while any playback/cast session
/// is active, processed files are treated as in use. The streaming check is
/// file-specific via <see cref="IStreamService.GetActiveStreamingFiles"/>.
/// The sliding window's reactive <see cref="System.IO.IOException"/> retry remains
/// the authoritative guard.
/// </remarks>
public sealed class ProcessedFileUsage : IProcessedFileUsage
{
    private readonly IPlaybackEngine _playback;
    private readonly ICastingService _casting;
    private readonly IStreamService? _streamService;

    /// <summary>Creates the probe over the playback, casting, and (optionally) streaming services.</summary>
    /// <param name="playback">Local playback engine (required).</param>
    /// <param name="casting">DLNA casting service (required).</param>
    /// <param name="streamService">
    /// Browser streaming service (optional). When <c>null</c>, streaming-file checks are skipped.
    /// </param>
    public ProcessedFileUsage(IPlaybackEngine playback, ICastingService casting, IStreamService? streamService = null)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _casting = casting ?? throw new ArgumentNullException(nameof(casting));
        _streamService = streamService;
    }

    /// <inheritdoc />
    public bool IsInUse(string filePath)
    {
        CastingState casting = _casting.State;
        if (casting is CastingState.Streaming or CastingState.Connecting)
        {
            return true;
        }

        PlaybackState playback = _playback.State;
        if (playback is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Seeking)
        {
            return true;
        }

        // D7: browser streaming — file-specific check
        IReadOnlySet<string>? activeStreamingFiles = _streamService?.GetActiveStreamingFiles();
        if (activeStreamingFiles is not null && activeStreamingFiles.Contains(filePath))
        {
            return true;
        }

        return false;
    }
}
