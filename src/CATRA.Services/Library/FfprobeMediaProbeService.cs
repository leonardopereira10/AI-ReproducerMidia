using System.ComponentModel;
using System.Diagnostics;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;

namespace CATRA.Services.Library;

/// <summary>
/// Production <see cref="IMediaProbeService"/>: shells out to the ffprobe binary
/// and parses its JSON output via <see cref="FfprobeOutputParser"/>.
/// <para>
/// ENVIRONMENT NOTE: ffprobe is not installed/bundled yet. This implementation
/// returns <c>null</c> (never throws) when the binary is missing, so scanning
/// works without metadata. ST-05 will bundle FFmpeg (FFmpeg.AutoGen) and may
/// replace this Process-based probe with an in-process implementation.
/// </para>
/// </summary>
public sealed class FfprobeMediaProbeService : IMediaProbeService
{
    private readonly string _ffprobePath;

    /// <summary>
    /// Creates the service. <paramref name="ffprobePath"/> defaults to
    /// <c>ffprobe</c> resolved from PATH.
    /// </summary>
    public FfprobeMediaProbeService(string? ffprobePath = null)
    {
        _ffprobePath = string.IsNullOrWhiteSpace(ffprobePath) ? "ffprobe" : ffprobePath;
    }

    /// <inheritdoc />
    public async Task<MediaProbeResult?> ProbeAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffprobePath,
            Arguments = $"-v quiet -print_format json -show_format -show_streams \"{filePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdout = await process.StandardOutput
                .ReadToEndAsync(cancellationToken)
                .ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return process.ExitCode == 0
                ? FfprobeOutputParser.Parse(stdout)
                : null;
        }
        catch (Win32Exception)
        {
            // Binary not installed / not on PATH — metadata simply unavailable.
            return null;
        }
    }
}
