using CATRA.Core.Enums;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Result of resolving which physical file the player / DLNA caster should use
/// for an episode (ST-20, RF-05, RF-06). When a usable processed file exists it
/// is preferred; otherwise the resolver falls back to the original source.
/// </summary>
/// <param name="FilePath">Absolute path of the file to play/stream (processed or original).</param>
/// <param name="IsProcessed"><c>true</c> when <paramref name="FilePath"/> is a processed output.</param>
/// <param name="Profile">Profile of the processed file, or <c>null</c> when original.</param>
/// <param name="DisplayLabel">UI label, e.g. "🖥 1080p135", "📺 4K55" or "📄 Original".</param>
public record ResolvedMedia(
    string FilePath,
    bool IsProcessed,
    ProcessProfile? Profile,
    string DisplayLabel)
{
    /// <summary>
    /// <c>true</c> when a processed record existed for the episode/profile but was
    /// unusable (RN-09 stale hash, or the file vanished from disk) and resolution
    /// fell back to the original. The UI uses this to decide whether the
    /// "arquivo processado indisponível" fallback dialog applies — a plain
    /// never-processed episode (<c>false</c>) plays the original silently.
    /// </summary>
    public bool FellBackFromProcessed { get; init; }
}

/// <summary>
/// Resolves which file (processed output or original source) the player and the
/// DLNA caster must use for an episode under a given <see cref="ProcessProfile"/>
/// (ST-20). Encapsulates the processed-file lookup, the RN-09 stale detection
/// (source hash mismatch → fallback) and the missing-file fallback.
/// </summary>
public interface IMediaFileResolver
{
    /// <summary>
    /// Resolves the media file to use for <paramref name="episodeId"/> under
    /// <paramref name="profile"/>. Prefers a usable processed file; falls back to
    /// the original when none exists, the file is missing, or the source is stale.
    /// </summary>
    Task<ResolvedMedia> ResolveAsync(int episodeId, ProcessProfile profile);
}
