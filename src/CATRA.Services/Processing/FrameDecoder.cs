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
    private bool _diagLogged;
    private SwsContext* _scaler;
    private int _scalerW;
    private int _scalerH;

    private FrameSourceMetadata? _metadata;
    private IntPtr _d3d11DevicePtr;
    private IntPtr _d3d11DeviceContextPtr; // borrowed from AVD3D11VADeviceContext (do NOT Release)
    private VDX.ID3D11Device? _vorticeDevice;
    private VDX.ID3D11DeviceContext? _vorticeContext;

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
                    Console.Error.WriteLine($"[TryReadFrame] buffered dequeue, remaining={_bufferedTextures.Count}");
                    Console.Error.Flush();
                    return true;
                }

                if (_eofSent)
                {
                    return TryReceiveFrame(out texture);
                }

                Console.Error.WriteLine("[TryReadFrame] av_read_frame...");
                Console.Error.Flush();
                int result = ffmpeg.av_read_frame(_formatContext, _packet);
                Console.Error.WriteLine($"[TryReadFrame] av_read_frame result={result}");
                Console.Error.Flush();
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

                Console.Error.WriteLine("[TryReadFrame] SendPacketDraining...");
                Console.Error.Flush();
                int send = SendPacketDraining(_packet);
                Console.Error.WriteLine($"[TryReadFrame] SendPacketDraining result={send}");
                Console.Error.Flush();
                ffmpeg.av_packet_unref(_packet);
                if (send < 0 && send != ffmpeg.AVERROR_EOF)
                {
                    throw new FfmpegException(send, "avcodec_send_packet");
                }

                Console.Error.WriteLine("[TryReadFrame] TryReceiveFrame...");
                Console.Error.Flush();
                if (TryReceiveFrame(out texture))
                {
                    Console.Error.WriteLine($"[TryReadFrame] TryReceiveFrame OK texture=0x{texture:X}");
                    Console.Error.Flush();
                    return true;
                }
                Console.Error.WriteLine("[TryReadFrame] TryReceiveFrame returned false, looping");
                Console.Error.Flush();
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
        Console.Error.WriteLine($"[SendPacketDraining] avcodec_send_packet ctx=0x{(IntPtr)_codecContext:X} pkt=0x{(IntPtr)packet:X}");
        Console.Error.Flush();
        int result = ffmpeg.avcodec_send_packet(_codecContext, packet);
        Console.Error.WriteLine($"[SendPacketDraining] avcodec_send_packet result={result}");
        Console.Error.Flush();
        while (result == -ffmpeg.EAGAIN)
        {
            Console.Error.WriteLine("[SendPacketDraining] EAGAIN, draining...");
            Console.Error.Flush();
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
    /// Moves the decoded D3D11 frame into a privately-owned <c>AVFrame</c> so its COM
    /// texture reference stays valid until <see cref="ReleaseFrame"/>.
    ///
    /// D3D11VA decoders return frames as slices of a shared texture ARRAY
    /// (ArraySize &gt; 1, one slice per decoder surface). Downstream GPU stages
    /// (RIFE, FSR, AMF) expect standalone ID3D11Texture2D pointers (ArraySize == 1),
    /// so we copy the relevant subresource into a fresh texture here. The original
    /// AVFrame is kept alive (holding a COM ref on the texture array) until
    /// ReleaseFrame; the standalone copy is tracked separately and Released via COM.
    /// </summary>
    private IntPtr OwnFrame(AVFrame* frame)
    {
        if ((AVPixelFormat)frame->format != AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            throw new FfmpegException(
                ffmpeg.AVERROR(ffmpeg.EINVAL),
                "avcodec_receive_frame (pipeline requires D3D11VA hardware frames)");
        }

        // AMD RDNA4 (Adrenalin) driver bug, confirmed empirically on this machine:
        // every direct D3D11 read of an NV12 decoder array slice returns ZEROS
        // (CopySubresourceRegion into staging reads zeros; NV12 staging ARRAYS
        // fail CreateTexture2D with E_INVALIDARG so whole-array CopyResource is
        // not an option either) => green frames.
        // FFmpeg's own D3D11VA download (av_hwframe_transfer_data) is the one
        // path that works: validated BIT-IDENTICAL to software decode (md5 of
        // raw NV12 matches CPU decode). Download to system NV12 here, convert
        // NV12 -> BGRA on the CPU, and upload a standalone BGRA texture.
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

        // Pixels are now in system memory; drop the hw frame immediately so the
        // decoder surface (texture array slice) is recycled right away.
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
            if (!_diagLogged)
            {
                _diagLogged = true;
                // Early green-frame probe: zeros here would mean the download is broken.
                Console.Error.WriteLine($"[FrameDecoder] NV12 download {sw->width}x{sw->height} -> {dstW}x{dstH} " +
                    $"Y(10,0)={yPtr[10]} Y(10,{dstH / 2})={yPtr[(long)(dstH / 2) * yPitch + 10]} U={uvPtr[0]} V={uvPtr[1]}");
                Console.Error.Flush();
            }

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
