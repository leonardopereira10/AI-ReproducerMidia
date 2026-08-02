using System.Runtime.InteropServices;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using FFmpeg.AutoGen;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
// FFmpeg.AutoGen also declares COM interop structs with these names.
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace CATRA.Services.Playback;

/// <summary>
/// D3D11/DXGI video renderer (ST-05). Creates a feature-level-11 device plus a
/// windowed swap chain bound to the WPF <c>HwndHost</c> child HWND and presents
/// decoded <see cref="VideoFrame"/>s into it with VSync (<c>Present(1, 0)</c>).
/// </summary>
/// <remarks>
/// <para>
/// Frames are letterboxed: the picture is scaled preserving its aspect ratio into
/// the largest rect that fits the back buffer and centred, with the surrounding
/// bars cleared to black before every present. When the video already matches the
/// target rect exactly, hardware (D3D11VA) frames are copied straight to the back
/// buffer with <c>CopySubresourceRegion</c> (no CPU round-trip); otherwise the
/// picture is scaled to packed BGRA with libswscale and uploaded with
/// <c>UpdateSubresource</c> into the destination rect. Hardware frames that need
/// scaling are first read back through a cached CPU-readable staging texture
/// (NV12/P010/P016 → BGRA) — correct but slower than a GPU video processor, which
/// is left as a future optimisation.
/// </para>
/// <para>
/// Not unit-tested directly (requires a real GPU + display); the letterbox and
/// scaling paths must be validated manually. The playback engine depends on
/// <see cref="IVideoRenderer"/> and is tested with fakes.
/// </para>
/// </remarks>
public sealed unsafe class VideoRenderer : IVideoRenderer
{
    private readonly object _gate = new();

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain? _swapChain;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11RenderTargetView? _renderTargetView;

    // Cached software scaler; rebuilt whenever source or target geometry changes.
    private SwsContext* _swsContext;
    private int _swsSourceWidth;
    private int _swsSourceHeight;
    private AVPixelFormat _swsSourceFormat;
    private int _swsTargetWidth;
    private int _swsTargetHeight;

    // Cached CPU-readable staging texture for hardware-frame readback; rebuilt
    // whenever the decoded texture geometry/format changes.
    private ID3D11Texture2D? _stagingTexture;
    private int _stagingWidth;
    private int _stagingHeight;
    private Format _stagingFormat;

    private IntPtr _windowHandle;
    private int _width;
    private int _height;
    private bool _initialized;
    private bool _disposed;

    /// <inheritdoc />
    public bool IsInitialized
    {
        get { lock (_gate) { return _initialized; } }
    }

    /// <inheritdoc />
    public void Initialize(IntPtr windowHandle, int width, int height)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("A non-null window handle is required.", nameof(windowHandle));
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Output size must be positive.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
            {
                return;
            }

            _windowHandle = windowHandle;
            _width = width;
            _height = height;

            CreateDeviceAndSwapChain();
            CreateBackBufferViews();
            _initialized = true;

            // Paint black so the host shows a clean surface before the first frame.
            ClearCore();
        }
    }

    /// <inheritdoc />
    public void Present(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            EnsureInitialized();
            PresentCore(frame);
            _swapChain!.Present(1, PresentFlags.None);
        }
    }

    /// <inheritdoc />
    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Output size must be positive.");
        }

        lock (_gate)
        {
            EnsureInitialized();
            if (width == _width && height == _height)
            {
                return;
            }

            _width = width;
            _height = height;

            // Back-buffer views must be released before ResizeBuffers can succeed.
            ReleaseBackBufferViews();
            _swapChain!.ResizeBuffers(2, width, height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
            CreateBackBufferViews();
            ClearCore();
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            EnsureInitialized();
            ClearCore();
        }
    }

    private void CreateDeviceAndSwapChain()
    {
        var swapChainDescription = new SwapChainDescription
        {
            BufferCount = 2,
            BufferDescription = new ModeDescription
            {
                Width = _width,
                Height = _height,
                Format = Format.B8G8R8A8_UNorm,
                RefreshRate = new Rational(0, 1),
                Scaling = ModeScaling.Unspecified,
                ScanlineOrdering = ModeScanlineOrder.Unspecified,
            },
            BufferUsage = Usage.RenderTargetOutput,
            OutputWindow = _windowHandle,
            SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.Discard,
            Windowed = true,
        };

        FeatureLevel[] featureLevels = { FeatureLevel.Level_11_0 };

        Result result = D3D11.D3D11CreateDeviceAndSwapChain(
            adapter: null,
            DriverType.Hardware,
            DeviceCreationFlags.None,
            featureLevels,
            swapChainDescription,
            out _swapChain,
            out _device,
            out _,
            out _context);

        if (result.Failure)
        {
            throw new InvalidOperationException(
                $"D3D11 device and swap chain creation failed: {result.Description} " +
                $"(HRESULT 0x{result.Code:X8}). A Direct3D 11.0-capable GPU and driver " +
                "are required for video rendering.");
        }
    }

    private void CreateBackBufferViews()
    {
        _backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _device!.CreateRenderTargetView(_backBuffer);
        _context!.OMSetRenderTargets(_renderTargetView);
        _context.RSSetViewport(0, 0, _width, _height);
    }

    private void ReleaseBackBufferViews()
    {
        _renderTargetView?.Dispose();
        _renderTargetView = null;
        _backBuffer?.Dispose();
        _backBuffer = null;
    }

    private void PresentCore(VideoFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            throw new ArgumentException(
                $"Frame has invalid dimensions {frame.Width}x{frame.Height}.", nameof(frame));
        }

        (int x, int y, int width, int height) =
            ComputeLetterboxRect(frame.Width, frame.Height, _width, _height);

        // Black bars first; the picture is then written into the letterboxed rect.
        _context!.ClearRenderTargetView(_renderTargetView!, new Color4(0f, 0f, 0f, 1f));

        if (frame.IsHardwareFrame && width == frame.Width && height == frame.Height)
        {
            // No scaling required: copy the GPU texture straight into the rect.
            CopyHardware(frame, x, y);
        }
        else
        {
            byte[] bgra = ScaleToBgra(frame, width, height);
            var region = new Box(x, y, 0, x + width, y + height, 1);
            _context.UpdateSubresource(bgra, _backBuffer!, 0, width * 4, 0, region);
        }
    }

    /// <summary>
    /// Computes the centred destination rect that preserves the source aspect
    /// ratio inside the target size (letterbox/pillarbox bars around it).
    /// </summary>
    private static (int X, int Y, int Width, int Height) ComputeLetterboxRect(
        int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        double scale = Math.Min(
            (double)targetWidth / sourceWidth,
            (double)targetHeight / sourceHeight);

        int width = Math.Min(targetWidth, Math.Max(1, (int)Math.Round(sourceWidth * scale)));
        int height = Math.Min(targetHeight, Math.Max(1, (int)Math.Round(sourceHeight * scale)));

        return ((targetWidth - width) / 2, (targetHeight - height) / 2, width, height);
    }

    private void CopyHardware(VideoFrame frame, int destinationX, int destinationY)
    {
        // The AVFrame owns the COM texture; take a private reference so wrapping it
        // in a managed COM object (which releases on Dispose) keeps the net refcount
        // balanced and never frees the decoder's texture out from under it.
        Marshal.AddRef(frame.TextureHandle);
        try
        {
            using var source = new ID3D11Texture2D(frame.TextureHandle);
            _context!.CopySubresourceRegion(
                _backBuffer!,
                0,
                destinationX,
                destinationY,
                0,
                source,
                frame.SubResourceIndex,
                null);
        }
        finally
        {
            // The managed wrapper above already released once; drop our extra ref.
            Marshal.Release(frame.TextureHandle);
        }
    }

    private byte[] ScaleToBgra(VideoFrame frame, int targetWidth, int targetHeight)
    {
        return frame.IsHardwareFrame
            ? ScaleHardwareToBgra(frame, targetWidth, targetHeight)
            : ScaleSoftwareToBgra(frame, targetWidth, targetHeight);
    }

    private byte[] ScaleSoftwareToBgra(VideoFrame frame, int targetWidth, int targetHeight)
    {
        byte[] source = frame.BgraData
            ?? throw new ArgumentException("Software frame carries no pixel data.", nameof(frame));

        EnsureScaler(frame.Width, frame.Height, AVPixelFormat.AV_PIX_FMT_BGRA, targetWidth, targetHeight);

        byte[] bgra = new byte[(long)targetWidth * targetHeight * 4];
        fixed (byte* destination = bgra)
        fixed (byte* src = source)
        {
            byte*[] srcSlices = { src, null, null, null };
            int[] srcStrides = { frame.Width * 4, 0, 0, 0 };
            byte*[] dstSlices = { destination, null, null, null };
            int[] dstStrides = { targetWidth * 4, 0, 0, 0 };

            int rows = ffmpeg.sws_scale(_swsContext, srcSlices, srcStrides, 0, frame.Height, dstSlices, dstStrides);
            if (rows < 0)
            {
                throw new FfmpegException(rows, "sws_scale");
            }
        }

        return bgra;
    }

    private byte[] ScaleHardwareToBgra(VideoFrame frame, int targetWidth, int targetHeight)
    {
        Marshal.AddRef(frame.TextureHandle);
        try
        {
            using var source = new ID3D11Texture2D(frame.TextureHandle);
            Texture2DDescription sourceDescription = source.Description;
            AVPixelFormat sourceFormat = ToFFmpegFormat(sourceDescription.Format);

            ID3D11Texture2D staging = EnsureStagingTexture(
                sourceDescription.Width, sourceDescription.Height, sourceDescription.Format);

            // Copy the decoder's texture-array slice into a CPU-readable texture.
            _context!.CopySubresourceRegion(staging, 0, 0, 0, 0, source, frame.SubResourceIndex, null);

            MappedSubresource mapped =
                _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                EnsureScaler(frame.Width, frame.Height, sourceFormat, targetWidth, targetHeight);

                byte[] bgra = new byte[(long)targetWidth * targetHeight * 4];
                byte* sourcePtr = (byte*)mapped.DataPointer;
                int rowPitch = mapped.RowPitch;

                fixed (byte* destination = bgra)
                {
                    // NV12/P010/P016 are biplanar: the packed UV plane follows the
                    // luma plane at rowPitch * height bytes and shares its row pitch.
                    byte*[] srcSlices =
                    {
                        sourcePtr,
                        sourcePtr + (long)rowPitch * sourceDescription.Height,
                        null,
                        null,
                    };
                    int[] srcStrides = { rowPitch, rowPitch, 0, 0 };
                    byte*[] dstSlices = { destination, null, null, null };
                    int[] dstStrides = { targetWidth * 4, 0, 0, 0 };

                    int rows = ffmpeg.sws_scale(
                        _swsContext, srcSlices, srcStrides, 0, frame.Height, dstSlices, dstStrides);
                    if (rows < 0)
                    {
                        throw new FfmpegException(rows, "sws_scale");
                    }
                }

                return bgra;
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
        }
        finally
        {
            Marshal.Release(frame.TextureHandle);
        }
    }

    private ID3D11Texture2D EnsureStagingTexture(int width, int height, Format format)
    {
        if (_stagingTexture is not null
            && _stagingWidth == width
            && _stagingHeight == height
            && _stagingFormat == format)
        {
            return _stagingTexture;
        }

        _stagingTexture?.Dispose();
        _stagingTexture = null;

        var description = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        _stagingTexture = _device!.CreateTexture2D(description);
        _stagingWidth = width;
        _stagingHeight = height;
        _stagingFormat = format;
        return _stagingTexture;
    }

    private void EnsureScaler(
        int sourceWidth, int sourceHeight, AVPixelFormat sourceFormat, int targetWidth, int targetHeight)
    {
        if (_swsContext is not null
            && _swsSourceWidth == sourceWidth
            && _swsSourceHeight == sourceHeight
            && _swsSourceFormat == sourceFormat
            && _swsTargetWidth == targetWidth
            && _swsTargetHeight == targetHeight)
        {
            return;
        }

        if (_swsContext is not null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

        _swsContext = ffmpeg.sws_getContext(
            sourceWidth, sourceHeight, sourceFormat,
            targetWidth, targetHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
            ffmpeg.SWS_BILINEAR, null, null, null);
        if (_swsContext is null)
        {
            throw new InvalidOperationException("sws_getContext failed to create a scaler.");
        }

        _swsSourceWidth = sourceWidth;
        _swsSourceHeight = sourceHeight;
        _swsSourceFormat = sourceFormat;
        _swsTargetWidth = targetWidth;
        _swsTargetHeight = targetHeight;
    }

    private static AVPixelFormat ToFFmpegFormat(Format format) => format switch
    {
        Format.NV12 => AVPixelFormat.AV_PIX_FMT_NV12,
        Format.P010 => AVPixelFormat.AV_PIX_FMT_P010LE,
        Format.P016 => AVPixelFormat.AV_PIX_FMT_P016LE,
        _ => throw new NotSupportedException(
            $"Hardware frame format '{format}' is not supported for software scaling."),
    };

    private void ClearCore()
    {
        _context!.ClearRenderTargetView(_renderTargetView!, new Color4(0f, 0f, 0f, 1f));
        _swapChain!.Present(1, PresentFlags.None);
    }

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("Video renderer has not been initialized.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_swsContext is not null)
            {
                ffmpeg.sws_freeContext(_swsContext);
                _swsContext = null;
            }

            _stagingTexture?.Dispose();
            _stagingTexture = null;

            ReleaseBackBufferViews();
            _swapChain?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
            _swapChain = null;
            _context = null;
            _device = null;
        }
    }
}
