using System.Runtime.InteropServices;
using CATRA.Core.Interfaces;
using CATRA.Core.Processing;
using CATRA.Services.Playback;
using FFmpeg.AutoGen;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;
using VDX = Vortice.Direct3D11;

namespace CATRA.Services.Processing;

/// <summary>
/// Production <see cref="IFrameDecoder"/> (ST-17): FFmpeg.AutoGen sequential decode
/// with D3D11VA hardware acceleration (same negotiation as the playback
/// <c>VideoDecoder</c>, ST-05). Each decoded frame is surfaced as an
/// <c>ID3D11Texture2D*</c> and kept alive until <see cref="ReleaseFrame"/> so the
/// pipeline can hold an A/B pair for interpolation.
/// </summary>
/// <remarks>
/// <para>
/// Not unit-tested directly: it requires FFmpeg binaries, a GPU and a real media file.
/// The pipeline depends on <see cref="IFrameDecoder"/> and is tested with a fake. Real
/// decode (valid frames, no COM/GPU leak over a batch) is validated manually.
/// </para>
/// <para>
/// The pipeline's GPU stages need D3D11 textures, so a source that only decodes to a
/// software pixel format (no GPU device) throws rather than silently producing frames
/// the native bridge cannot consume.
/// </para>
/// </remarks>
public sealed unsafe class FrameDecoder : IFrameDecoder
{
    // Rooted delegate: FFmpeg stores a native function pointer to it, so it must never
    // be garbage-collected. A static readonly field guarantees that.
    private static readonly AVCodecContext_get_format GetFormatCallback = OnGetFormat;

    private readonly object _gate = new();

    private AVFormatContext* _formatContext;
    private AVCodecContext* _codecContext;
    private AVBufferRef* _hwDeviceContext;
    private AVFrame* _frame;
    private AVPacket* _packet;

    private int _videoStreamIndex = -1;
    private bool _opened;
    private bool _eofSent;
    private bool _disposed;

    private SwsContext* _scaler;
    private int _scalerW;
    private int _scalerH;

    private FrameSourceMetadata? _metadata;
    private IntPtr _d3d11DevicePtr;
    private IntPtr _d3d11DeviceContextPtr; // borrowed from AVD3D11VADeviceContext (do NOT Release)
    private VDX.ID3D11Device? _vorticeDevice;
    private VDX.ID3D11DeviceContext? _vorticeContext;

    // ST-23: GPU NV12 → BGRA compute shader state.
    // _gpuNv12InitAttempted: true after the first OwnFrame call attempted init.
    // _gpuNv12Available: true if catra_nv12_bgra_init succeeded (permanently false if it failed).
    private bool _gpuNv12InitAttempted;
    private bool _gpuNv12Available;
    private readonly NativeLibraryLoader _nativeLib = new();

    /// <inheritdoc />
    public IntPtr D3D11DevicePtr => _d3d11DevicePtr;

    // Decoded frames are moved into privately-owned AVFrames (one COM texture ref each)
    // tracked here so they stay valid until released. _bufferedTextures holds frames
    // drained while resolving an EAGAIN; they are also present in _ownedFrames.
    private readonly List<(IntPtr Texture, IntPtr FramePtr)> _ownedFrames = new();
    private readonly Queue<IntPtr> _bufferedTextures = new();

    /// <inheritdoc />
    public FrameSourceMetadata Metadata =>
        _metadata ?? throw new InvalidOperationException("Decoder has not been opened.");

    /// <inheritdoc />
    public void Open(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_opened)
            {
                throw new InvalidOperationException("Decoder is already opened.");
            }

            OpenInternal(filePath);
            _opened = true;
        }
    }

    private void OpenInternal(string filePath)
    {
        int result;
        fixed (AVFormatContext** fmt = &_formatContext)
        {
            result = ffmpeg.avformat_open_input(fmt, filePath, null, null);
        }

        ThrowIfError(result, "avformat_open_input");

        try
        {
            result = ffmpeg.avformat_find_stream_info(_formatContext, null);
            ThrowIfError(result, "avformat_find_stream_info");

            AVCodec* codec = null;
            _videoStreamIndex = ffmpeg.av_find_best_stream(
                _formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
            if (_videoStreamIndex < 0 || codec == null)
            {
                throw new InvalidOperationException("The file contains no decodable video stream.");
            }

            AVStream* stream = _formatContext->streams[_videoStreamIndex];

            _codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecContext == null)
            {
                throw new OutOfMemoryException("avcodec_alloc_context3 returned null.");
            }

            result = ffmpeg.avcodec_parameters_to_context(_codecContext, stream->codecpar);
            ThrowIfError(result, "avcodec_parameters_to_context");

            TryInitializeHardware();

            // Selects AV_PIX_FMT_D3D11 when a hw device is attached.
            _codecContext->get_format = GetFormatCallback;

            result = ffmpeg.avcodec_open2(_codecContext, codec, null);
            ThrowIfError(result, "avcodec_open2");

            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            if (_packet == null || _frame == null)
            {
                throw new OutOfMemoryException("Failed to allocate FFmpeg packet/frame.");
            }

            _metadata = BuildMetadata(stream);

            // Now that the codec is open, the D3D11VA device is initialized.
            // Extract the device pointer so the pipeline can hand it to the native bridge.
            ExtractD3D11Device();
        }
        catch
        {
            // Open failed part-way: release anything already allocated, then rethrow.
            ReleaseNativeResources();
            throw;
        }
    }

    private void TryInitializeHardware()
    {
        AVBufferRef* hw = null;
        int result = ffmpeg.av_hwdevice_ctx_create(
            &hw, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
        if (result < 0 || hw == null)
        {
            // No D3D11 device (headless / no GPU): the pipeline cannot run without GPU
            // textures. Leave hw_device_ctx null; decode will then yield software frames
            // which TryReadFrame rejects with a clear error.
            return;
        }

        _hwDeviceContext = hw;
        _codecContext->hw_device_ctx = ffmpeg.av_buffer_ref(hw);
    }

    /// <summary>
    /// Extracts the ID3D11Device* from the initialized D3D11VA hardware context.
    /// Must be called AFTER avcodec_open2() which initializes the device.
    /// </summary>
    private void ExtractD3D11Device()
    {
        if (_hwDeviceContext == null)
        {
            System.Diagnostics.Trace.WriteLine("[FrameDecoder] No hw device context (software decode?)");
            return;
        }

        try
        {
            AVHWDeviceContext* devCtx = (AVHWDeviceContext*)_hwDeviceContext->data;
            if (devCtx == null)
            {
                System.Diagnostics.Trace.WriteLine("[FrameDecoder] devCtx is null");
                return;
            }

            if (devCtx->hwctx == null)
            {
                System.Diagnostics.Trace.WriteLine("[FrameDecoder] hwctx is null (device not initialized?)");
                return;
            }

            AVD3D11VADeviceContext* d3dCtx = (AVD3D11VADeviceContext*)devCtx->hwctx;
            if (d3dCtx->device == null)
            {
                System.Diagnostics.Trace.WriteLine("[FrameDecoder] d3dCtx->device is null");
                return;
            }

            _d3d11DevicePtr = (IntPtr)d3dCtx->device;
            _d3d11DeviceContextPtr = (IntPtr)d3dCtx->device_context;

            // Create persistent Vortice wrappers (AddRef to balance eventual Dispose).
            Marshal.AddRef(_d3d11DevicePtr);
            Marshal.AddRef(_d3d11DeviceContextPtr);
            _vorticeDevice = new VDX.ID3D11Device(_d3d11DevicePtr);
            _vorticeContext = new VDX.ID3D11DeviceContext(_d3d11DeviceContextPtr);

            System.Diagnostics.Trace.WriteLine($"[FrameDecoder] Extracted D3D11 device: 0x{_d3d11DevicePtr:X}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[FrameDecoder] ExtractD3D11Device failed: {ex}");
        }
    }

    private static AVPixelFormat OnGetFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        bool preferHardware = context->hw_device_ctx != null;
        AVPixelFormat* cursor = formats;
        while (*cursor != AVPixelFormat.AV_PIX_FMT_NONE)
        {
            if (preferHardware && *cursor == AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                return *cursor;
            }

            cursor++;
        }

        return *formats;
    }

    private FrameSourceMetadata BuildMetadata(AVStream* stream)
    {
        long rawDuration = _formatContext->duration;
        TimeSpan duration = rawDuration > 0
            ? TimeSpan.FromSeconds((double)rawDuration / ffmpeg.AV_TIME_BASE)
            : TimeSpan.Zero;

        AVRational fpsRational = ffmpeg.av_guess_frame_rate(_formatContext, stream, null);
        double fps = fpsRational.den != 0 ? (double)fpsRational.num / fpsRational.den : 0.0;

        long totalFrames = fps > 0 && duration > TimeSpan.Zero
            ? (long)Math.Round(duration.TotalSeconds * fps)
            : 0;

        return new FrameSourceMetadata(fps, _codecContext->width, _codecContext->height, duration, totalFrames);
    }

    /// <inheritdoc />
    public bool TryReadFrame(out IntPtr texture)
    {
        lock (_gate)
        {
            EnsureOpened();

            while (true)
            {
                if (_bufferedTextures.Count > 0)
                {
                    texture = _bufferedTextures.Dequeue();
                    return true;
                }

                if (_eofSent)
                {
                    return TryReceiveFrame(out texture);
                }

                int result = ffmpeg.av_read_frame(_formatContext, _packet);
                if (result == ffmpeg.AVERROR_EOF)
                {
                    int flush = SendPacketDraining(null);
                    if (flush < 0 && flush != ffmpeg.AVERROR_EOF)
                    {
                        throw new FfmpegException(flush, "avcodec_send_packet(flush)");
                    }

                    _eofSent = true;
                    continue;
                }

                if (result < 0)
                {
                    throw new FfmpegException(result, "av_read_frame");
                }

                bool isVideo = _packet->stream_index == _videoStreamIndex;
                if (!isVideo)
                {
                    ffmpeg.av_packet_unref(_packet);
                    continue;
                }

                int send = SendPacketDraining(_packet);
                ffmpeg.av_packet_unref(_packet);
                if (send < 0 && send != ffmpeg.AVERROR_EOF)
                {
                    throw new FfmpegException(send, "avcodec_send_packet");
                }

                if (TryReceiveFrame(out texture))
                {
                    return true;
                }
            }
        }
    }

    /// <summary>
    /// Sends <paramref name="packet"/> (or <c>null</c> to flush), treating <c>EAGAIN</c>
    /// as "input buffer full": decoded frames are pulled out (queued in
    /// <see cref="_bufferedTextures"/>) until the decoder accepts the packet, which is
    /// then resent unchanged. Mirrors the playback decoder so no picture is lost.
    /// </summary>
    private int SendPacketDraining(AVPacket* packet)
    {
        int result = ffmpeg.avcodec_send_packet(_codecContext, packet);
        while (result == -ffmpeg.EAGAIN)
        {
            while (TryReceiveFrame(out IntPtr drained))
            {
                _bufferedTextures.Enqueue(drained);
            }

            result = ffmpeg.avcodec_send_packet(_codecContext, packet);
        }

        return result;
    }

    private bool TryReceiveFrame(out IntPtr texture)
    {
        int result = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
        if (result == -ffmpeg.EAGAIN || result == ffmpeg.AVERROR_EOF)
        {
            texture = IntPtr.Zero;
            return false;
        }

        if (result < 0)
        {
            throw new FfmpegException(result, "avcodec_receive_frame");
        }

        texture = OwnFrame(_frame);
        return true;
    }

    /// <summary>
    /// Moves the decoded D3D11 frame into a privately-owned BGRA texture.
    ///
    /// D3D11VA decoders return frames as slices of a shared texture ARRAY
    /// (ArraySize &gt; 1, one slice per decoder surface). Downstream GPU stages
    /// (RIFE, FSR, AMF) expect standalone ID3D11Texture2D pointers (ArraySize == 1).
    ///
    /// Conversion priority:
    /// 1. <b>GPU compute shader</b> (ST-23): fastest, but requires the decoder texture
    ///    to have D3D11_BIND_SHADER_RESOURCE (many D3D11VA decoders don't).
    /// 2. <b>CPU fallback</b>: av_hwframe_transfer_data + sws_scale + upload.
    ///    FFmpeg's own D3D11VA download is the one verified-good read path on
    ///    this AMD RDNA4 machine (bit-identical to software decode, md5 match).
    ///    Direct D3D11 copies of NV12 decoder array slices (CopyResource /
    ///    CopySubresourceRegion into staging) read ZEROS on this driver —
    ///    the native staging path is intentionally NOT used here.
    /// </summary>
    private IntPtr OwnFrame(AVFrame* frame)
    {
        if ((AVPixelFormat)frame->format != AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            throw new FfmpegException(
                ffmpeg.AVERROR(ffmpeg.EINVAL),
                "avcodec_receive_frame (pipeline requires D3D11VA hardware frames)");
        }

        // 1. Try GPU NV12→BGRA compute shader path (ST-23).
        if (TryGpuConvert(frame, out IntPtr gpuResult))
        {
            ffmpeg.av_frame_unref(frame);
            _ownedFrames.Add((gpuResult, IntPtr.Zero));
            return gpuResult;
        }

        // 2. CPU fallback path: av_hwframe_transfer_data + sws_scale + upload.
        //    AMD RDNA4 (Adrenalin): direct D3D11 reads of NV12 decoder array
        //    slices return ZEROS (verified: staging CopySubresourceRegion,
        //    whole-array CopyResource, and Map of plane subresources all read
        //    zeros). FFmpeg's own download is the one path that works:
        //    bit-identical to software decode (md5 match).
        AVFrame* sw = ffmpeg.av_frame_alloc();
        if (sw == null)
        {
            throw new OutOfMemoryException("av_frame_alloc returned null.");
        }

        int transfer = ffmpeg.av_hwframe_transfer_data(sw, frame, 0);
        if (transfer < 0)
        {
            ffmpeg.av_frame_free(&sw);
            throw new FfmpegException(transfer, "av_hwframe_transfer_data");
        }

        // Diagnostic (black-frame hunt): probe the downloaded NV12 RIGHT AFTER
        // the transfer, before any conversion. Trace.WriteLine is the channel
        // captured by the VS debug log (Console.Error is not).
        unsafe
        {
            byte* yp = sw->data[0];
            byte* uvp = sw->data[1];
            if (yp != null)
            {
                int ls0 = sw->linesize[0];
                int ls1 = sw->linesize[1];
                int w = sw->width;
                int h = sw->height;
                long ySum = 0;
                // 16 sample points spread across the luma plane.
                for (int i = 0; i < 16; i++)
                {
                    int sy = (h - 1) * i / 15;
                    int sx = (w - 1) * ((i * 7) % 15) / 14;
                    ySum += yp[(long)sy * ls0 + sx];
                }
                long uvSum = 0;
                if (uvp != null)
                {
                    for (int i = 0; i < 8; i++)
                    {
                        int sy = (h / 2 - 1) * i / 7;
                        int sx = (w - 2) * ((i * 5) % 7) / 6;
                        uvSum += uvp[(long)sy * ls1 + sx];
                    }
                }
                System.Diagnostics.Trace.WriteLine(
                    $"[FrameDecoder] post-transfer probe fmt={(AVPixelFormat)sw->format} {w}x{h} " +
                    $"ls0={ls0} ls1={ls1} Yavg16={ySum / 16} UVavg8={(uvp != null ? uvSum / 8 : -1)} " +
                    $"Y00={yp[0]} Ymid={yp[(long)(h / 2) * ls0 + w / 2]}");
            }
        }

        ffmpeg.av_frame_unref(frame);

        try
        {
            IntPtr standalone = ConvertAndUploadNv12(sw);
            _ownedFrames.Add((standalone, IntPtr.Zero));
            return standalone;
        }
        finally
        {
            ffmpeg.av_frame_free(&sw);
        }
    }

    /// <summary>
    /// Attempts NV12→BGRA conversion via the GPU compute shader (ST-23).
    /// Returns <c>true</c> on success with the BGRA texture pointer in
    /// <paramref name="bgraTexture"/> (AddRef'd, caller owns). Returns
    /// <c>false</c> on any failure; the caller must fall back to the CPU path.
    /// </summary>
    private bool TryGpuConvert(AVFrame* frame, out IntPtr bgraTexture)
    {
        bgraTexture = IntPtr.Zero;

        // Extract the NV12 texture array pointer and array slice from the D3D11 frame.
        // frame->data[0] = ID3D11Texture2D* (the texture array)
        // frame->data[1] = array slice index (stored as intptr_t)
        IntPtr nv12TexPtr = (IntPtr)frame->data[0];
        if (nv12TexPtr == IntPtr.Zero)
        {
            return false;
        }

        uint arraySlice = (uint)(long)frame->data[1];
        int width = frame->width;
        int height = frame->height;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        // First call: attempt to initialize the GPU shader.
        if (!_gpuNv12InitAttempted)
        {
            _gpuNv12InitAttempted = true;

            if (!_nativeLib.IsAvailable || _d3d11DevicePtr == IntPtr.Zero)
            {
                // Native DLL not found or no D3D11 device — GPU path permanently disabled.
                _gpuNv12Available = false;
                return false;
            }

            int initResult = _nativeLib.Nv12BgraInit(_d3d11DevicePtr);
            if (initResult != 0)
            {
                // Shader compile or device failure — GPU path permanently disabled.
                Console.Error.WriteLine($"[FrameDecoder] ST-23: GPU NV12→BGRA init failed (rc={initResult}), falling back to CPU path");
                Console.Error.Flush();
                _gpuNv12Available = false;
                return false;
            }

            _gpuNv12Available = true;
            Console.Error.WriteLine("[FrameDecoder] ST-23: GPU NV12→BGRA compute shader initialized");
            Console.Error.Flush();
        }

        if (!_gpuNv12Available)
        {
            return false;
        }

        // Attempt the GPU conversion.
        int result = _nativeLib.Nv12BgraConvert(
            _d3d11DevicePtr,
            _d3d11DeviceContextPtr,
            nv12TexPtr,
            arraySlice,
            (uint)width,
            (uint)height,
            out bgraTexture);

        if (result != 0 || bgraTexture == IntPtr.Zero)
        {
            // GPU conversion failed for this frame — log once and fall back.
            // We do NOT permanently disable the GPU path here because transient
            // failures (e.g., odd frame size) may not affect subsequent frames.
            Console.Error.WriteLine($"[FrameDecoder] ST-23: GPU convert failed (rc={result}), CPU fallback");
            Console.Error.Flush();
            bgraTexture = IntPtr.Zero;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Attempts NV12→BGRA conversion via the native staging copy path.
    /// This is the AMD RDNA 4 workaround: uses full-texture CopyResource
    /// + two separate Map calls (Y and UV subresources) + CPU BT.601
    /// conversion + upload to a standalone BGRA texture.
    ///
    /// Bypasses av_hwframe_transfer_data which produces zeros on AMD RDNA 4
    /// for D3D11VA NV12 decoder textures (CopySubresourceRegion driver bug).
    ///
    /// Returns <c>true</c> on success with the BGRA texture pointer in
    /// <paramref name="bgraTexture"/> (AddRef'd, caller owns). Returns
    /// <c>false</c> on any failure; the caller must fall back to the CPU path.
    /// </summary>
    private bool TryStagingConvert(AVFrame* frame, out IntPtr bgraTexture)
    {
        bgraTexture = IntPtr.Zero;

        IntPtr nv12TexPtr = (IntPtr)frame->data[0];
        if (nv12TexPtr == IntPtr.Zero)
        {
            return false;
        }

        uint arraySlice = (uint)(long)frame->data[1];
        int width = frame->width;
        int height = frame->height;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        if (!_nativeLib.IsAvailable)
        {
            return false;
        }

        int result = _nativeLib.Nv12StagingConvert(
            nv12TexPtr, arraySlice, (uint)width, (uint)height, out bgraTexture);

        if (result != 0 || bgraTexture == IntPtr.Zero)
        {
            Console.Error.WriteLine($"[FrameDecoder] Staging NV12\u2192BGRA failed (rc={result})");
            Console.Error.Flush();
            bgraTexture = IntPtr.Zero;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Converts a system-memory NV12 <c>AVFrame</c> (from
    /// <c>av_hwframe_transfer_data</c>) into BGRA on the CPU and uploads it as a
    /// standalone BGRA D3D11 texture (ArraySize == 1). Returns an AddRef'd
    /// ID3D11Texture2D* that the caller must Release.
    /// </summary>
    private IntPtr ConvertAndUploadNv12(AVFrame* sw)
    {
        if (_vorticeDevice == null || _vorticeContext == null)
        {
            throw new InvalidOperationException(
                "D3D11 device/context not available; cannot create frame texture.");
        }

        if ((AVPixelFormat)sw->format != AVPixelFormat.AV_PIX_FMT_NV12)
        {
            throw new NotSupportedException(
                $"FrameDecoder: unexpected downloaded format {(AVPixelFormat)sw->format}; expected NV12.");
        }

        int dstW = _metadata?.Width ?? sw->width;
        int dstH = _metadata?.Height ?? sw->height;
        int yPitch = sw->linesize[0];
        int uvPitch = sw->linesize[1];

        // NV12 -> BGRA via FFmpeg sws_scale (SIMD; same approach as VideoRenderer).
        EnsureScaler(dstW, dstH);
        byte[] bgra = new byte[dstW * dstH * 4];
        unsafe
        {
            byte* yPtr = sw->data[0];
            byte* uvPtr = sw->data[1];
            // NV12 probe on the Trace channel (captured by VS debug log).
            System.Diagnostics.Trace.WriteLine($"[FrameDecoder] NV12 probe {sw->width}x{sw->height} -> {dstW}x{dstH} " +
                $"Y(10,0)={yPtr[10]} Y(10,{dstH / 2})={yPtr[(long)(dstH / 2) * yPitch + 10]} U={uvPtr[0]} V={uvPtr[1]}");

            byte*[] srcSlices = { yPtr, uvPtr, null, null };
            int[] srcStrides = { yPitch, uvPitch, 0, 0 };
            fixed (byte* dst = bgra)
            {
                byte*[] dstSlices = { dst, null, null, null };
                int[] dstStrides = { dstW * 4, 0, 0, 0 };
                // Convert only the visible dstH rows (source height may be aligned, e.g. 1088).
                int rows = ffmpeg.sws_scale(_scaler, srcSlices, srcStrides, 0, dstH, dstSlices, dstStrides);
                if (rows < 0)
                {
                    throw new FfmpegException(rows, "sws_scale");
                }
            }
        }

        // Upload the BGRA bytes into a standalone BGRA texture.
        var bgraStagingDesc = new Texture2DDescription
        {
            Width = dstW,
            Height = dstH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None
        };
        VDX.ID3D11Texture2D bgraStaging;
        try { bgraStaging = _vorticeDevice.CreateTexture2D(bgraStagingDesc); }
        catch (Exception ex) { Console.Error.WriteLine($"[CopyToStandalone] FAIL create BGRA staging: {ex.Message}"); Console.Error.Flush(); throw; }
        unsafe
        {
            MappedSubresource box;
            try { box = _vorticeContext.Map(bgraStaging, 0, MapMode.Write, MapFlags.None); }
            catch (Exception ex) { Console.Error.WriteLine($"[CopyToStandalone] FAIL Map(BGRA write): {ex.Message}"); Console.Error.Flush(); throw; }
            byte* dst = (byte*)box.DataPointer;
            fixed (byte* src = bgra)
            {
                for (int y = 0; y < dstH; y++)
                {
                    System.Buffer.MemoryCopy(src + (long)y * dstW * 4, dst + (long)y * box.RowPitch, dstW * 4, dstW * 4);
                }
            }
            _vorticeContext.Unmap(bgraStaging, 0);
        }

        var dstDesc = new Texture2DDescription
        {
            Width = dstW,
            Height = dstH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };
        VDX.ID3D11Texture2D dst2;
        try { dst2 = _vorticeDevice.CreateTexture2D(dstDesc); }
        catch (Exception ex) { Console.Error.WriteLine($"[CopyToStandalone] FAIL create BGRA GPU tex: {ex.Message}"); Console.Error.Flush(); throw; }
        try { _vorticeContext.CopyResource(dst2, bgraStaging); }
        catch (Exception ex) { Console.Error.WriteLine($"[CopyToStandalone] FAIL CopyResource(BGRA upload): {ex.Message}"); Console.Error.Flush(); throw; }
        bgraStaging.Dispose();

        IntPtr result = dst2.NativePointer;
        Marshal.AddRef(result); // caller's reference
        dst2.Dispose();         // releases Vortice's reference
        return result;
    }

    private static byte ClampByte(float v) =>
        v < 0f ? (byte)0 : v > 255f ? (byte)255 : (byte)(v + 0.5f);

    /// <summary>
    /// Creates (or reuses) a 1:1 NV12 -> BGRA sws context for the given size.
    /// </summary>
    private void EnsureScaler(int width, int height)
    {
        if (_scaler != null && _scalerW == width && _scalerH == height)
        {
            return;
        }

        if (_scaler != null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }

        _scaler = ffmpeg.sws_getContext(
            width, height, AVPixelFormat.AV_PIX_FMT_NV12,
            width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
            (int)FFmpeg.AutoGen.SwsFlags.SWS_BILINEAR, null, null, null);
        if (_scaler == null)
        {
            throw new InvalidOperationException("sws_getContext failed (NV12 -> BGRA).");
        }

        _scalerW = width;
        _scalerH = height;
    }

    /// <inheritdoc />
    public void ReleaseFrame(IntPtr texture)
    {
        if (texture == IntPtr.Zero)
        {
            return;
        }

        lock (_gate)
        {
            for (int i = 0; i < _ownedFrames.Count; i++)
            {
                if (_ownedFrames[i].Texture != texture)
                {
                    continue;
                }

                // Release the standalone texture copy (COM refcount).
                Marshal.Release(_ownedFrames[i].Texture);

                // Release the original AVFrame if one was kept (drops the texture
                // array ref). Frames downloaded via av_hwframe_transfer_data store
                // IntPtr.Zero here — only the texture needs releasing.
                if (_ownedFrames[i].FramePtr != IntPtr.Zero)
                {
                    AVFrame* frame = (AVFrame*)_ownedFrames[i].FramePtr;
                    ffmpeg.av_frame_unref(frame);
                    ffmpeg.av_frame_free(&frame);
                }
                _ownedFrames.RemoveAt(i);
                return;
            }
        }
    }

    private void EnsureOpened()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_opened)
        {
            throw new InvalidOperationException("Decoder has not been opened.");
        }
    }

    private static void ThrowIfError(int result, string context)
    {
        if (result < 0)
        {
            throw new FfmpegException(result, context);
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
            ReleaseNativeResources();
        }
    }

    private void ReleaseNativeResources()
    {
        // Release every owned frame: standalone texture copy (COM) + AVFrame.
        foreach ((IntPtr texture, IntPtr framePtr) in _ownedFrames)
        {
            if (texture != IntPtr.Zero)
            {
                Marshal.Release(texture);
            }

            if (framePtr != IntPtr.Zero)
            {
                AVFrame* frame = (AVFrame*)framePtr;
                ffmpeg.av_frame_unref(frame);
                ffmpeg.av_frame_free(&frame);
            }
        }

        _ownedFrames.Clear();
        _bufferedTextures.Clear();

        // ST-23: release cached GPU NV12→BGRA shader + output texture.
        if (_gpuNv12Available)
        {
            try { _nativeLib.Nv12BgraShutdown(); }
            catch { /* best-effort cleanup */ }
        }
        _gpuNv12Available = false;
        _gpuNv12InitAttempted = false;

        if (_scaler != null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }

        // Dispose Vortice wrappers (releases the extra AddRef from ExtractD3D11Device).
        _vorticeDevice?.Dispose();
        _vorticeDevice = null;
        _vorticeContext?.Dispose();
        _vorticeContext = null;
        _d3d11DevicePtr = IntPtr.Zero;
        _d3d11DeviceContextPtr = IntPtr.Zero;

        if (_frame != null)
        {
            AVFrame* frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        if (_packet != null)
        {
            AVPacket* packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_codecContext != null)
        {
            if (_codecContext->hw_device_ctx != null)
            {
                AVBufferRef* hwRef = _codecContext->hw_device_ctx;
                ffmpeg.av_buffer_unref(&hwRef);
                _codecContext->hw_device_ctx = null;
            }

            AVCodecContext* context = _codecContext;
            ffmpeg.avcodec_free_context(&context);
            _codecContext = null;
        }

        if (_hwDeviceContext != null)
        {
            AVBufferRef* hw = _hwDeviceContext;
            ffmpeg.av_buffer_unref(&hw);
            _hwDeviceContext = null;
        }

        if (_formatContext != null)
        {
            AVFormatContext* format = _formatContext;
            ffmpeg.avformat_close_input(&format);
            _formatContext = null;
        }
    }
}
