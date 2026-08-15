using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
/// A/V SYNC (bugfix_06): the encoded video is a raw H.265 Annex B stream with NO
/// container timestamps, and the AMF encoder's VUI timing is unreliable (observed:
/// VUI parsing as 135/1 fps for a 25 fps encode). Muxing it as-is makes ffmpeg
/// guess the frame rate, so the synthesized video timestamps — and therefore the
/// video track duration — drift away from the audio. The mux therefore runs in two
/// steps: (1) rewrite the stream's VUI timing to the exact encoder fps via the
/// <c>hevc_metadata</c> bitstream filter (<c>tick_rate</c> + CFR signalling via
/// <c>num_ticks_poc_diff_one=1</c>; the BSF has no <c>fixed_frame_rate_flag</c>
/// option), (2) mux that corrected stream with the source audio using an explicit
/// <c>-f hevc -framerate</c> input so the MP4 muxer synthesizes correct PTS/DTS
/// for every frame. Both steps are stream copies — no re-encode.
/// </para>
/// <para>
/// ENVIRONMENT NOTE: ffmpeg/ffprobe are resolved from PATH (not bundled yet). When a
/// binary is missing this throws <see cref="IOException"/>, which the pipeline surfaces
/// as a failed <c>ProcessResult</c>. Real muxing (A/V sync) is covered by the
/// AudioMuxer integration tests when binaries are available.
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
    public void Mux(string sourcePath, string encodedVideoPath, string finalOutputPath, double videoFps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedVideoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalOutputPath);

        if (!File.Exists(encodedVideoPath))
        {
            throw new IOException($"Encoded video not found: {encodedVideoPath}");
        }

        // bugfix_06: the caller (pipeline) always passes the exact encoder fps; the
        // probe is only a best-effort fallback for direct callers that do not know it.
        double fps = videoFps > 0 ? videoFps : ProbeVideoFps(encodedVideoPath);
        string rate = FormatRate(fps);

        // The intermediate file holding the VUI-corrected stream. It lives next to the
        // encoded file and is always removed (finally), success or failure.
        string correctedVideoPath = encodedVideoPath + ".vui.tmp";

        try
        {
            // Step 1 — normalize the VUI timing (stream copy, no re-encode). The raw
            // stream carries no container timestamps; every downstream timestamp is
            // synthesized from this VUI rate, so it MUST equal the real encode fps.
            Run(_ffmpegPath,
                $"-y -f hevc -i \"{encodedVideoPath}\" -c:v copy " +
                $"-bsf:v hevc_metadata=tick_rate={rate}:num_ticks_poc_diff_one=1 " +
                $"-f hevc \"{correctedVideoPath}\"",
                "video timing normalization");

            bool sourceIsAac = ProbeIsAac(sourcePath);
            string audioCodec = sourceIsAac ? "-c:a copy" : "-c:a aac";

            // Step 2 — map video from the corrected file (stream 0) and audio from the
            // source (stream 1); copy the H.265 video untouched, copy/re-encode the
            // audio as decided above. -f hevc + -framerate pin the input rate so the
            // MP4 muxer assigns correct PTS/DTS (the raw demuxer sets none itself).
            string arguments =
                $"-y -f hevc -framerate {rate} -i \"{correctedVideoPath}\" -i \"{sourcePath}\" " +
                $"-map 0:v:0 -map 1:a:0 -c:v copy {audioCodec} \"{finalOutputPath}\"";

            Run(_ffmpegPath, arguments, "audio mux");

            if (!File.Exists(finalOutputPath))
            {
                throw new IOException($"Audio mux did not produce an output file: {finalOutputPath}");
            }
        }
        finally
        {
            TryDelete(correctedVideoPath);
        }
    }

    /// <summary>
    /// Best-effort fallback when no explicit fps is given: probes the raw HEVC stream's
    /// own rate. Unreliable for AMF output (its VUI can parse to a wrong fps — the very
    /// defect this muxer works around), but never worse than the pre-fix behaviour.
    /// </summary>
    private double ProbeVideoFps(string encodedVideoPath)
    {
        try
        {
            string output = Run(
                _ffprobePath,
                $"-v quiet -f hevc -select_streams v:0 -show_entries stream=r_frame_rate -of csv=p=0 \"{encodedVideoPath}\"",
                "video fps probe");
            string rate = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?
                .Trim() ?? string.Empty;
            if (TryParseRate(rate, out double fps) && fps > 0)
            {
                return fps;
            }
        }
        catch (IOException)
        {
            // Probe unavailable or failed → fall through to the error below.
        }

        throw new IOException(
            $"Unable to determine the video fps of '{encodedVideoPath}' and no explicit fps was provided.");
    }

    /// <summary>Parses an ffprobe rate (<c>num/den</c> or decimal) into a double.</summary>
    private static bool TryParseRate(string rate, out double fps)
    {
        fps = 0;
        if (string.IsNullOrWhiteSpace(rate) ||
            string.Equals(rate, "N/A", StringComparison.OrdinalIgnoreCase) ||
            rate == "0/0")
        {
            return false;
        }

        string[] parts = rate.Split('/');
        try
        {
            if (parts.Length == 2)
            {
                double num = double.Parse(parts[0], CultureInfo.InvariantCulture);
                double den = double.Parse(parts[1], CultureInfo.InvariantCulture);
                if (den > 0)
                {
                    fps = num / den;
                    return fps > 0;
                }

                return false;
            }

            fps = double.Parse(rate, CultureInfo.InvariantCulture);
            return fps > 0;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>Formats a fps value for ffmpeg arguments (invariant culture, rational-safe).</summary>
    private static string FormatRate(double fps) => fps.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Best-effort delete of a temporary artefact.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; the pipeline reports failures through the mux result.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
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
