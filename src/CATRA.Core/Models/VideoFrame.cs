namespace CATRA.Core.Models;

/// <summary>
/// A single decoded video frame handed from <see cref="Interfaces.IVideoDecoder"/>
/// to <see cref="Interfaces.IVideoRenderer"/> (ST-05).
/// </summary>
/// <remarks>
/// Two flavours exist:
/// <list type="bullet">
/// <item><b>Hardware (D3D11VA):</b> <see cref="IsHardwareFrame"/> is <c>true</c>;
/// <see cref="TextureHandle"/> is an <c>ID3D11Texture2D*</c> and
/// <see cref="SubResourceIndex"/> the texture-array slice. The underlying
/// <c>AVFrame</c> owns a COM reference to that texture, so the frame carries a
/// <c>releaser</c> callback that unrefs the <c>AVFrame</c> on <see cref="Dispose"/>.</item>
/// <item><b>Software fallback:</b> <see cref="IsHardwareFrame"/> is <c>false</c>;
/// the decoder already converted the picture to packed BGRA stored in
/// <see cref="BgraData"/> (row pitch = <see cref="Width"/> * 4). No native lifetime
/// is involved, so <see cref="Dispose"/> is a no-op.</item>
/// </list>
/// Kept free of any FFmpeg/D3D dependency so Core stays a leaf and the playback
/// engine can be unit-tested with fake frames.
/// </remarks>
public sealed class VideoFrame : IDisposable
{
    private readonly Action? _releaser;
    private int _disposed;

    /// <summary>Presentation timestamp relative to the start of the media.</summary>
    public TimeSpan PresentationTime { get; }

    /// <summary>Coded width in pixels.</summary>
    public int Width { get; }

    /// <summary>Coded height in pixels.</summary>
    public int Height { get; }

    /// <summary><c>true</c> for a D3D11VA GPU frame; <c>false</c> for a software BGRA frame.</summary>
    public bool IsHardwareFrame { get; }

    /// <summary>Native <c>ID3D11Texture2D*</c> (hardware frames only).</summary>
    public IntPtr TextureHandle { get; }

    /// <summary>Texture-array slice / sub-resource index (hardware frames only).</summary>
    public int SubResourceIndex { get; }

    /// <summary>Packed BGRA pixels, row pitch = <see cref="Width"/> * 4 (software frames only).</summary>
    public byte[]? BgraData { get; }

    private VideoFrame(
        TimeSpan presentationTime,
        int width,
        int height,
        bool isHardwareFrame,
        IntPtr textureHandle,
        int subResourceIndex,
        byte[]? bgraData,
        Action? releaser)
    {
        PresentationTime = presentationTime;
        Width = width;
        Height = height;
        IsHardwareFrame = isHardwareFrame;
        TextureHandle = textureHandle;
        SubResourceIndex = subResourceIndex;
        BgraData = bgraData;
        _releaser = releaser;
    }

    /// <summary>Creates a hardware (D3D11VA) frame backed by a native texture.</summary>
    public static VideoFrame CreateHardware(
        TimeSpan presentationTime,
        int width,
        int height,
        IntPtr textureHandle,
        int subResourceIndex,
        Action releaser)
    {
        ArgumentNullException.ThrowIfNull(releaser);
        if (textureHandle == IntPtr.Zero)
        {
            throw new ArgumentException("Hardware frame requires a non-null texture pointer.", nameof(textureHandle));
        }

        return new VideoFrame(presentationTime, width, height, true, textureHandle, subResourceIndex, null, releaser);
    }

    /// <summary>Creates a software BGRA frame (fallback path).</summary>
    public static VideoFrame CreateSoftware(
        TimeSpan presentationTime,
        int width,
        int height,
        byte[] bgraData)
    {
        ArgumentNullException.ThrowIfNull(bgraData);
        var expected = (long)width * height * 4;
        if (bgraData.LongLength < expected)
        {
            throw new ArgumentException(
                $"BGRA buffer too small: {bgraData.LongLength} bytes, expected at least {expected}.",
                nameof(bgraData));
        }

        return new VideoFrame(presentationTime, width, height, false, IntPtr.Zero, 0, bgraData, null);
    }

    /// <summary>
    /// Releases the native resources owned by this frame (unrefs the backing
    /// <c>AVFrame</c> for hardware frames). Idempotent and thread-safe.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _releaser?.Invoke();
    }
}
