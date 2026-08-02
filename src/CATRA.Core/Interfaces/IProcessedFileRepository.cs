using CATRA.Core.Enums;
using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Repository for <see cref="ProcessedFile"/> domain models.
/// </summary>
public interface IProcessedFileRepository : IRepository<ProcessedFile>
{
    /// <summary>Returns the processed file for an episode and profile, or <c>null</c>.</summary>
    ProcessedFile? GetByEpisodeAndProfile(int episodeId, ProcessProfile profile);

    /// <summary>Returns all processed files for an episode.</summary>
    IReadOnlyList<ProcessedFile> GetByEpisode(int episodeId);
}
