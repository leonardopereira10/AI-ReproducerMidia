using System.Diagnostics;
using CATRA.Services.Processing;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Processing;

/// <summary>
/// bugfix_06 regression tests: audio and video must have the same duration after the
/// 2-pass mux. They exercise the REAL <see cref="AudioMuxer"/> against a deliberately
/// broken raw H.265 stream — the AMF encoder has been observed writing VUI timing that
/// ffprobe parses as 135/1 fps for a 25 fps encode, which made ffmpeg guess the video
/// rate during muxing and drift the video track away from the audio ("áudio termina
/// antes do vídeo"). The fix rewrites the VUI via the <c>hevc_metadata</c> BSF and pins
/// the input rate with <c>-f hevc -framerate</c>.
/// </summary>
/// <remarks>
/// Require ffmpeg/ffprobe (bundled in <c>lib/ffmpeg/</c> or on PATH); each test no-ops
/// when no binaries are available (real muxing is then validated manually, per the
/// IAudioMuxer contract).
/// </remarks>
public sealed class AudioMuxerIntegrationTests : IDisposable
{
    /// <summary>Acceptance tolerance from the bug report: ±100 ms between A and V.</summary>
    private const double DurationToleranceSeconds = 0.1;

    private readonly string _workDir =
        Path.Combine(Path.GetTempPath(), "catra_avsync_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the scratch folder.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void Mux_MissingEncodedVideo_ThrowsIOException()
    {
        // Pure guard check — no ffmpeg involved.
        var muxer = new AudioMuxer();
        Action act = () => muxer.Mux(
            Path.Combine(_workDir, "src.mp4"),
            Path.Combine(_workDir, "does_not_exist.video.tmp"),
            Path.Combine(_workDir, "out.mp4"),
            videoFps: 25);

        act.Should().Throw<IOException>();
    }

    [Fact]
    public void Mux_RawHevcWithBrokenVui_AudioAndVideoDurationsMatch()
    {
        (string? ffmpeg, string? ffprobe) = ResolveFfmpegTools();
        if (ffmpeg is null || ffprobe is null)
        {
            return; // no binaries on this machine → validated manually
        }

        Directory.CreateDirectory(_workDir);
        string source = Path.Combine(_workDir, "src.mp4");
        string rawVideo = Path.Combine(_workDir, "enc.video.tmp");
        string brokenVideo = Path.Combine(_workDir, "enc_broken.video.tmp");
        string output = Path.Combine(_workDir, "out.mp4");

        // 1. A real 5 s source: 24 fps H.264 + AAC audio (proper container timing).
        Run(ffmpeg,
            "-hide_banner -loglevel error -y " +
            "-f lavfi -i testsrc2=duration=5:size=320x240:rate=24 " +
            "-f lavfi -i sine=frequency=440:duration=5 " +
            $"-c:v libx264 -pix_fmt yuv420p -c:a aac \"{source}\"",
            "source generation");

        // 2. Simulate the pipeline's encoder output: raw H.265 Annex B at the doubled
        //    rate (48 fps, 240 frames, no B-frames — matches AMF/VCN output).
        Run(ffmpeg,
            $"-hide_banner -loglevel error -y -i \"{source}\" -an -r 48 " +
            $"-c:v libx265 -x265-params bframes=0 -f hevc \"{rawVideo}\"",
            "raw hevc generation");

        // 3. Simulate the AMF defect: rewrite the VUI timing to a WRONG rate (135 fps).
        //    Before the fix, muxing this file made ffmpeg timestamp the video at the
        //    VUI rate, desyncing it from the audio.
        Run(ffmpeg,
            $"-hide_banner -loglevel error -y -f hevc -i \"{rawVideo}\" -c:v copy " +
            $"-bsf:v hevc_metadata=tick_rate=135 -f hevc \"{brokenVideo}\"",
            "VUI corruption");

        var muxer = new AudioMuxer(ffmpeg, ffprobe);
        muxer.Mux(source, brokenVideo, output, videoFps: 48);

        File.Exists(output).Should().BeTrue("the mux must produce the final MP4");
        double videoDuration = ProbeStreamDuration(ffprobe, output, "v:0");
        double audioDuration = ProbeStreamDuration(ffprobe, output, "a:0");

        // 240 frames / 48 fps = 5 s — the broken VUI must NOT stretch/shrink the video.
        videoDuration.Should().BeApproximately(5.0, DurationToleranceSeconds,
            "video duration must reflect the real encode fps, not the bogus VUI rate");

        // The acceptance criterion from the bug report: A and V within ±100 ms.
        Math.Abs(videoDuration - audioDuration).Should().BeLessThanOrEqualTo(DurationToleranceSeconds,
            "audio and video must have the same duration after processing");
    }

    [Fact]
    public void Mux_NoExplicitFps_ProbesStreamRate_AndStillSyncs()
    {
        (string? ffmpeg, string? ffprobe) = ResolveFfmpegTools();
        if (ffmpeg is null || ffprobe is null)
        {
            return; // no binaries on this machine → validated manually
        }

        Directory.CreateDirectory(_workDir);
        string source = Path.Combine(_workDir, "src.mp4");
        string rawVideo = Path.Combine(_workDir, "enc.video.tmp");
        string output = Path.Combine(_workDir, "out.mp4");

        Run(ffmpeg,
            "-hide_banner -loglevel error -y " +
            "-f lavfi -i testsrc2=duration=5:size=320x240:rate=24 " +
            "-f lavfi -i sine=frequency=440:duration=5 " +
            $"-c:v libx264 -pix_fmt yuv420p -c:a aac \"{source}\"",
            "source generation");

        // x265 writes correct VUI timing, so the muxer's ffprobe fallback can recover
        // the 48 fps rate on its own (videoFps <= 0 path).
        Run(ffmpeg,
            $"-hide_banner -loglevel error -y -i \"{source}\" -an -r 48 " +
            $"-c:v libx265 -x265-params bframes=0 -f hevc \"{rawVideo}\"",
            "raw hevc generation");

        var muxer = new AudioMuxer(ffmpeg, ffprobe);
        muxer.Mux(source, rawVideo, output, videoFps: 0);

        double videoDuration = ProbeStreamDuration(ffprobe, output, "v:0");
        double audioDuration = ProbeStreamDuration(ffprobe, output, "a:0");

        videoDuration.Should().BeApproximately(5.0, DurationToleranceSeconds);
        Math.Abs(videoDuration - audioDuration).Should().BeLessThanOrEqualTo(DurationToleranceSeconds);
    }

    // --- helpers -----------------------------------------------------------

    /// <summary>
    /// Locates ffmpeg/ffprobe: first the repository's bundled <c>lib/ffmpeg</c> (walks
    /// up from the test bin folder), then PATH. Returns nulls when nothing is found.
    /// </summary>
    private static (string? Ffmpeg, string? Ffprobe) ResolveFfmpegTools()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int depth = 0; dir is not null && depth < 10; depth++, dir = dir.Parent)
        {
            string ffmpeg = Path.Combine(dir.FullName, "lib", "ffmpeg", "ffmpeg.exe");
            string ffprobe = Path.Combine(dir.FullName, "lib", "ffmpeg", "ffprobe.exe");
            if (File.Exists(ffmpeg) && File.Exists(ffprobe))
            {
                return (ffmpeg, ffprobe);
            }
        }

        string? pathFfmpeg = FindOnPath("ffmpeg.exe");
        string? pathFfprobe = FindOnPath("ffprobe.exe");
        return pathFfmpeg is not null && pathFfprobe is not null
            ? (pathFfmpeg, pathFfprobe)
            : ((string?)null, (string?)null);
    }

    private static string? FindOnPath(string fileName)
    {
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry — skip it.
            }
        }

        return null;
    }

    /// <summary>Probes one stream's duration (seconds) out of a container.</summary>
    private static double ProbeStreamDuration(string ffprobe, string file, string streamSpec)
    {
        string output = Run(ffprobe,
            $"-v error -select_streams {streamSpec} -show_entries stream=duration -of csv=p=0 \"{file}\"",
            "duration probe");
        string value = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim() ?? string.Empty;
        return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
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

        using Process process = Process.Start(startInfo)
            ?? throw new IOException($"Failed to start {fileName} for {operation}.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new IOException($"{fileName} {operation} failed (exit {process.ExitCode}): {stderr.Trim()}");
        }

        return stdout;
    }
}
