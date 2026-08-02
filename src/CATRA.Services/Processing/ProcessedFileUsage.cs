using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Processing;

/// <summary>
/// Production <see cref="IProcessedFileUsage"/> (ST-18, RF-04). Reports a processed
/// file as in use while local playback is active or a DLNA stream is in progress, so
/// window rotation never deletes a file that could be being played or served.
/// </summary>
/// <remarks>
/// The playback engine and casting service do not (yet) expose the exact file path in
/// use, so this is deliberately conservative: while any playback/cast session is
/// active, processed files are treated as in use. This is safe — reclamation simply
/// waits until playback stops — and the sliding window's reactive
/// <see cref="System.IO.IOException"/> retry remains the authoritative guard.
/// </remarks>
public sealed class ProcessedFileUsage : IProcessedFileUsage
{
    private readonly IPlaybackEngine _playback;
    private readonly ICastingService _casting;

    /// <summary>Creates the probe over the playback and casting services.</summary>
    public ProcessedFileUsage(IPlaybackEngine playback, ICastingService casting)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _casting = casting ?? throw new ArgumentNullException(nameof(casting));
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
        return playback is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Seeking;
    }
}
