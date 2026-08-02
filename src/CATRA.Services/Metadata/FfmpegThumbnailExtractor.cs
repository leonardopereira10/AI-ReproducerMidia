using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CATRA.Core.Interfaces;

namespace CATRA.Services.Metadata;

/// <summary>
/// Production <see cref="IThumbnailExtractor"/>: shells out to the ffmpeg CLI
/// to grab a single scaled JPEG frame (RF-08).
/// <para>
/// Command: <c>ffmpeg -ss {ts} -i {input} -frames:v 1 -q:v 2 -vf "scale=480:-1" {output.jpg}</c>
/// (480px wide, proportional height, quality 2).
/// </para>
/// <para>
/// ENVIRONMENT NOTE: ffmpeg is not installed/bundled yet (only the download
/// script exists). When the binary is missing this returns <c>false</c> (never
/// throws), so the UI degrades to a placeholder. Real extraction is validated
/// manually once the binary and sample media are available.
/// </para>
/// </summary>
public sealed class FfmpegThumbnailExtractor : IThumbnailExtractor
{
    private readonly string _ffmpegPath;

    /// <summary>
    /// Creates the extractor. <paramref name="ffmpegPath"/> defaults to
    /// <c>ffmpeg</c> resolved from PATH.
    /// </summary>
    public FfmpegThumbnailExtractor(string? ffmpegPath = null)
    {
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
    }

    /// <inheritdoc />
    public async Task<bool> ExtractFrameAsync(
        string inputPath,
        double timestampSec,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (!File.Exists(inputPath))
        {
            return false;
        }

        var timestamp = timestampSec.ToString("0.###", CultureInfo.InvariantCulture);
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments =
                $"-y -ss {timestamp} -i \"{inputPath}\" -frames:v 1 -q:v 2 -vf \"scale=480:-1\" \"{outputPath}\"",
            // ffmpeg writes the frame to the output file and logs to stderr;
            // stdout carries nothing useful, so leave it unredirected and drain
            // only stderr to avoid pipe deadlocks.
            RedirectStandardOutput = false,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            // Drain stderr (ffmpeg logs there) to avoid pipe deadlocks.
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);

            return process.ExitCode == 0 && File.Exists(outputPath);
        }
        catch (Win32Exception)
        {
            // Binary not installed / not on PATH — thumbnail simply unavailable.
            return false;
        }
        catch (OperationCanceledException)
        {
            // Timed out: kill the process and report failure.
            try
            {
                process?.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // Already exited or inaccessible — nothing to do.
            }

            return false;
        }
        finally
        {
            process?.Dispose();
        }
    }
}
