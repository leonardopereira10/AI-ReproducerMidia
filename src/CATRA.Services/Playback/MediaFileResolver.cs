using System.Diagnostics;
using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Playback;

/// <summary>
/// Default <see cref="IMediaFileResolver"/> (ST-20, RF-05, RF-06): looks up the
/// processed output for an episode/profile and prefers it when the file still
/// exists and its <see cref="ProcessedFile.SourceHash"/> matches the current
/// <see cref="Episode.FileHash"/>. Otherwise it falls back to the original
/// source file. The hash check is the RN-09 stale detection (a re-recorded or
/// replaced source invalidates the processed output).
/// </summary>
/// <remarks>
/// Resolution is synchronous under the hood (two cheap repository reads plus a
/// <see cref="File.Exists"/> stat) but exposed as a <see cref="Task"/> to keep
/// the call sites awaitable and the fake testable.
/// </remarks>
public sealed class MediaFileResolver : IMediaFileResolver
{
    /// <summary>Label for the original (unprocessed) source file.</summary>
    public const string OriginalLabel = "📄 Original";

    /// <summary>Label for the local playback profile (1080p 135fps).</summary>
    public const string LocalLabel = "🖥 1080p135";

    /// <summary>Label for the DLNA streaming profile (4K 55fps).</summary>
    public const string DlnaLabel = "📺 4K55";

    private readonly IProcessedFileRepository _processedFiles;
    private readonly IEpisodeRepository _episodes;

    /// <summary>Creates the resolver bound to the processed-file and episode repositories.</summary>
    public MediaFileResolver(IProcessedFileRepository processedFiles, IEpisodeRepository episodes)
    {
        _processedFiles = processedFiles ?? throw new ArgumentNullException(nameof(processedFiles));
        _episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
    }

    /// <inheritdoc />
    public Task<ResolvedMedia> ResolveAsync(int episodeId, ProcessProfile profile)
    {
        var episode = _episodes.GetById(episodeId)
            ?? throw new InvalidOperationException($"Episódio {episodeId} não encontrado.");

        var processed = _processedFiles.GetByEpisodeAndProfile(episodeId, profile);

        if (processed is not null && File.Exists(processed.FilePath))
        {
            // RN-09: the source changed since processing → the output is stale.
            if (!string.Equals(processed.SourceHash, episode.FileHash, StringComparison.Ordinal))
            {
                Trace.WriteLine(
                    $"[MediaFileResolver] Processed file stale for episode {episodeId} " +
                    $"(profile {profile}): source hash mismatch; falling back to original.");
                return Task.FromResult(Original(episode, fellBackFromProcessed: true));
            }

            return Task.FromResult(
                new ResolvedMedia(processed.FilePath, true, profile, LabelFor(profile)));
        }

        // No usable processed file: a record that points at a missing file is a
        // fallback the user should be told about; a never-processed episode is not.
        bool fellBack = processed is not null;
        if (fellBack)
        {
            Trace.WriteLine(
                $"[MediaFileResolver] Processed file missing on disk for episode {episodeId} " +
                $"(profile {profile}); falling back to original.");
        }

        return Task.FromResult(Original(episode, fellBackFromProcessed: fellBack));
    }

    /// <summary>Fixed display label for a profile (ST-20 indicator).</summary>
    public static string LabelFor(ProcessProfile profile) => profile switch
    {
        ProcessProfile.Local => LocalLabel,
        ProcessProfile.Dlna => DlnaLabel,
        _ => OriginalLabel,
    };

    private static ResolvedMedia Original(Episode episode, bool fellBackFromProcessed) =>
        new(episode.FilePath, false, null, OriginalLabel)
        {
            FellBackFromProcessed = fellBackFromProcessed,
        };
}
