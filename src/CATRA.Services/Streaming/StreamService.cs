using System.Collections.Concurrent;
using CATRA.Core.Enums;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Streaming;

/// <summary>
/// Coordinates episodic streaming: registers concrete files under opaque
/// tokens/URLs via <see cref="IMediaHttpServer"/> and resolves the best
/// available stream for a requested profile.
/// </summary>
public sealed class StreamService : IStreamService, IDisposable
{
    private readonly IMediaHttpServer _mediaHttpServer;
    private readonly IEpisodeRepository _episodes;
    private readonly IProcessedFileRepository _processedFiles;
    private readonly ConcurrentDictionary<string, StreamToken> _activeTokens = new(); // token → StreamToken

    /// <summary>Tracks a registered streaming token with its file path and last-access time.</summary>
    private sealed record StreamToken(string FilePath, DateTime LastAccessed);

    /// <summary>Periodic timer that evicts tokens idle for more than 5 minutes.</summary>
    private PeriodicTimer? _cleanupTimer;

    /// <summary>Background task running the cleanup loop.</summary>
    private Task? _cleanupTask;

    /// <summary>Cancellation source to stop the cleanup loop on dispose.</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Extension → MIME content-type mapping for common video containers.</summary>
    private static readonly Dictionary<string, string> MimeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".mkv"] = "video/x-matroska",
        [".webm"] = "video/webm",
        [".ts"] = "video/mp2t",
    };

    public StreamService(
        IMediaHttpServer mediaHttpServer,
        IEpisodeRepository episodes,
        IProcessedFileRepository processedFiles)
    {
        _mediaHttpServer = mediaHttpServer ?? throw new ArgumentNullException(nameof(mediaHttpServer));
        _episodes = episodes ?? throw new ArgumentNullException(nameof(episodes));
        _processedFiles = processedFiles ?? throw new ArgumentNullException(nameof(processedFiles));

        StartCleanupTimer();
    }

    /// <summary>Starts the periodic background cleanup of stale tokens.</summary>
    private void StartCleanupTimer()
    {
        _cleanupTimer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        _cleanupTask = Task.Run(async () =>
        {
            try
            {
                while (await _cleanupTimer.WaitForNextTickAsync(_cts.Token))
                {
                    var cutoff = DateTime.UtcNow.AddMinutes(-5);
                    foreach (var kvp in _activeTokens)
                    {
                        if (kvp.Value.LastAccessed < cutoff)
                        {
                            if (_activeTokens.TryRemove(kvp.Key, out _))
                            {
                                _mediaHttpServer.UnregisterFile(kvp.Key);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on dispose — exit cleanly.
            }
        });
    }

    /// <inheritdoc />
    public string RegisterForStreaming(string filePath, string contentType)
    {
        var token = _mediaHttpServer.RegisterFile(filePath, contentType);
        _activeTokens[token] = new StreamToken(filePath, DateTime.UtcNow);
        return token;
    }

    /// <inheritdoc />
    public StreamResolution? ResolveEpisode(int episodeId, string profile)
    {
        var episode = _episodes.GetById(episodeId);
        if (episode is null) return null;

        string filePath;
        int width, height;
        double fps;
        long? fileSize;

        if (profile.Equals("original", StringComparison.OrdinalIgnoreCase))
        {
            filePath = episode.FilePath;
            width = episode.SourceWidth ?? 0;
            height = episode.SourceHeight ?? 0;
            fps = episode.SourceFps ?? 0.0;
            fileSize = episode.FileSizeBytes;
        }
        else
        {
            if (!TryParseProfile(profile, out var processProfile))
                return null;

            var processed = _processedFiles.GetByEpisodeAndProfile(episodeId, processProfile);
            if (processed is null) return null;

            filePath = processed.FilePath;
            width = processed.TargetWidth;
            height = processed.TargetHeight;
            fps = processed.TargetFps;
            fileSize = processed.FileSizeBytes;
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var contentType = MimeMap.GetValueOrDefault(ext, "application/octet-stream");
        var token = _mediaHttpServer.RegisterFile(filePath, contentType);
        _activeTokens[token] = new StreamToken(filePath, DateTime.UtcNow);
        var streamUrl = _mediaHttpServer.GetMediaUrl(token);

        return new StreamResolution
        {
            FilePath = filePath,
            ContentType = contentType,
            StreamUrl = streamUrl,
            Token = token,
            Width = width,
            Height = height,
            Fps = fps,
            FileSizeBytes = fileSize,
            Profile = profile.ToLowerInvariant(),
        };
    }

    /// <inheritdoc />
    public List<ProfileInfo> GetAvailableProfiles(int episodeId)
    {
        var episode = _episodes.GetById(episodeId);
        if (episode is null) return new();

        var profiles = new List<ProfileInfo>
        {
            new()
            {
                Name = "original",
                Label = $"Original ({episode.SourceWidth}x{episode.SourceHeight} {episode.SourceFps:F2}fps)",
                Width = episode.SourceWidth ?? 0,
                Height = episode.SourceHeight ?? 0,
                Fps = episode.SourceFps ?? 0.0,
                FileSizeBytes = episode.FileSizeBytes,
                IsProcessed = true,
            },
        };

        var processedFiles = _processedFiles.GetByEpisode(episodeId);
        foreach (var pf in processedFiles)
        {
            var profileName = pf.Profile.ToString().ToLowerInvariant();
            profiles.Add(new ProfileInfo
            {
                Name = profileName,
                Label = $"{pf.Profile} ({pf.TargetWidth}x{pf.TargetHeight} {pf.TargetFps:F2}fps)",
                Width = pf.TargetWidth,
                Height = pf.TargetHeight,
                Fps = pf.TargetFps,
                FileSizeBytes = pf.FileSizeBytes,
                IsProcessed = true,
            });
        }

        return profiles;
    }

    /// <inheritdoc />
    public void Unregister(string token)
    {
        _mediaHttpServer.UnregisterFile(token);
        _activeTokens.TryRemove(token, out _);
    }

    /// <summary>
    /// Refreshes the last-accessed timestamp for a token, preventing its eviction
    /// by the cleanup timer. Called implicitly when a token is resolved.
    /// </summary>
    private void TouchToken(string token)
    {
        if (_activeTokens.TryGetValue(token, out var existing))
        {
            _activeTokens[token] = existing with { LastAccessed = DateTime.UtcNow };
        }
    }

    /// <inheritdoc />
    public IReadOnlySet<string> GetActiveStreamingFiles()
        => new HashSet<string>(_activeTokens.Values.Select(t => t.FilePath), StringComparer.Ordinal);

    /// <summary>Stops the cleanup timer and releases resources.</summary>
    public void Dispose()
    {
        _cts.Cancel();
        _cleanupTimer?.Dispose();
        _cleanupTimer = null;

        try
        {
            _cleanupTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Swallow cancellation exceptions during shutdown.
        }

        _cts.Dispose();
    }

    /// <summary>
    /// Tries to parse a lowercase profile name (e.g. "local", "dlna") into
    /// the <see cref="ProcessProfile"/> enum.
    /// </summary>
    private static bool TryParseProfile(string profile, out ProcessProfile result)
    {
        if (Enum.TryParse<ProcessProfile>(profile, ignoreCase: true, out result))
            return true;

        result = default;
        return false;
    }
}
