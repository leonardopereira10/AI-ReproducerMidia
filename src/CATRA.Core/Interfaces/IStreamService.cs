using System.Collections.Generic;

namespace CATRA.Core.Interfaces;

using CATRA.Core.Models;

/// <summary>
/// Coordinates episodic streaming: registers concrete files under opaque
/// tokens/URLs and resolves the best available stream for a requested profile.
/// </summary>
public interface IStreamService
{
    /// <summary>
    /// Registers <paramref name="filePath"/> for streaming and returns an opaque
    /// token. The file must stay readable for the duration of the session.
    /// </summary>
    string RegisterForStreaming(string filePath, string contentType);

    /// <summary>
    /// Resolves the stream for <paramref name="episodeId"/> under <paramref name="profile"/>
    /// (lowercase identifier; see <c>ProcessProfile</c>), or <c>null</c> when none is available.
    /// </summary>
    StreamResolution? ResolveEpisode(int episodeId, string profile);

    /// <summary>Lists the profiles currently available for an episode.</summary>
    List<ProfileInfo> GetAvailableProfiles(int episodeId);

    /// <summary>Unregisters a previously issued streaming token.</summary>
    void Unregister(string token);

    /// <summary>Set of absolute file paths that currently have active streaming sessions.</summary>
    IReadOnlySet<string> GetActiveStreamingFiles();
}