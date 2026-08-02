using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace CATRA.Services.Playback;

/// <summary>
/// Thrown when an FFmpeg call returns a negative error code (ST-05). The numeric
/// code is decoded into a human-readable message via <c>av_strerror</c>.
/// </summary>
public sealed class FfmpegException : Exception
{
    /// <summary>The negative FFmpeg/errno error code.</summary>
    public int ErrorCode { get; }

    public FfmpegException(int errorCode, string context)
        : base(FormatMessage(errorCode, context))
    {
        ErrorCode = errorCode;
    }

    private static unsafe string FormatMessage(int errorCode, string context)
    {
        const int bufferSize = 1024;
        byte* buffer = stackalloc byte[bufferSize];
        buffer[0] = 0;
        ffmpeg.av_strerror(errorCode, buffer, bufferSize);
        string detail = Marshal.PtrToStringAnsi((IntPtr)buffer) ?? "unknown ffmpeg error";
        return $"{context} failed: {detail} (code {errorCode}).";
    }
}
