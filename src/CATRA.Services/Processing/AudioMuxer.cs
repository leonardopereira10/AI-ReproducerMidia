using System.ComponentModel;
using System.Diagnostics;
using CATRA.Core.Interfaces;

namespace CATRA.Services.Processing;

/// <summary>
/// Production <see cref="IAudioMuxer"/> (ST-17): shells out to the ffmpeg CLI to
/// multiplex the source audio into the encoded (video-only) file, producing the final
/// MP4. 2-pass per the spec: the pipeline writes the H.265 video first, then this
/// combines it with the audio.
/// </summary>
/// <remarks>
/// <para>
/// Audio codec handling: when the source audio is already AAC it is stream-copied
/// (<c>-c:a copy</c>); otherwise it is re-encoded to AAC (<c>-c:a aac</c>). The source
/// codec is probed with ffprobe; if probing is unavailable the safe re-encode path is
/// used.
/// </para>
/// <para>
/// ENVIRONMENT NOTE: ffmpeg/ffprobe are resolved from PATH (not bundled yet). When a
/// binary is missing this throws <see cref="IOException"/>, which the pipeline surfaces
/// as a failed <c>ProcessResult</c>. Real muxing (A/V sync) is validated manually.
/// </para>
/// </remarks>
public sealed class AudioMuxer : IAudioMuxer
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;

    /// <summary>Creates the muxer. Paths default to <c>ffmpeg</c>/<c>ffprobe</c> from PATH.</summary>
    public AudioMuxer(string? ffmpegPath = null, string? ffprobePath = null)
    {
        _ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath;
        _ffprobePath = string.IsNullOrWhiteSpace(ffprobePath) ? "ffprobe" : ffprobePath;
    }

    /// <inheritdoc />
    public void Mux(string sourcePath, string encodedVideoPath, string finalOutputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedVideoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalOutputPath);

        if (!File.Exists(encodedVideoPath))
        {
            throw new IOException($"Encoded video not found: {encodedVideoPath}");
        }

        bool sourceIsAac = ProbeIsAac(sourcePath);
        string audioCodec = sourceIsAac ? "-c:a copy" : "-c:a aac";

        // Map video from the encoded file (stream 0) and audio from the source (stream 1);
        // copy the H.265 video untouched, copy/re-encode the audio as decided above.
        string arguments =
            $"-y -i \"{encodedVideoPath}\" -i \"{sourcePath}\" " +
            $"-map 0:v:0 -map 1:a:0 -c:v copy {audioCodec} \"{finalOutputPath}\"";

        Run(_ffmpegPath, arguments, "audio mux");

        if (!File.Exists(finalOutputPath))
        {
            throw new IOException($"Audio mux did not produce an output file: {finalOutputPath}");
        }
    }

    /// <summary>Probes the first audio stream codec; returns <c>true</c> when it is AAC.</summary>
    private bool ProbeIsAac(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            return false;
        }

        try
        {
            string output = Run(
                _ffprobePath,
                $"-v quiet -select_streams a:0 -show_entries stream=codec_name -of csv=p=0 \"{sourcePath}\"",
                "audio probe");
            string codec = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?
                .Trim() ?? string.Empty;
            return string.Equals(codec, "aac", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            // Probe unavailable → fall back to the always-safe re-encode path.
            return false;
        }
    }

    /// <summary>Runs a CLI tool synchronously, draining stderr to avoid pipe deadlocks.</summary>
    private static string Run(string fileName, string arguments, string operation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
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
                throw new IOException($"Failed to start {fileName} for {operation}.");
            }

            // Drain both pipes concurrently to avoid deadlocks, then wait for exit.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                throw new IOException(
                    $"ffmpeg {operation} failed with exit code {process.ExitCode}: {stderr.Trim()}");
            }

            return stdout;
        }
        catch (Win32Exception ex)
        {
            // Binary not installed / not on PATH.
            throw new IOException($"The '{fileName}' binary is not available for {operation}.", ex);
        }
        finally
        {
            process?.Dispose();
        }
    }
}
