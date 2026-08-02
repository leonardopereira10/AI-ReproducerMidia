using CATRA.Core.Interfaces;
using CATRA.Core.Processing;
using CATRA.Services.Playback;
using FFmpeg.AutoGen;

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

    private FrameSourceMetadata? _metadata;

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
                    // Drain any frames buffered inside the decoder, then signal EOF.
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
        while (result == ffmpeg.EAGAIN)
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
        if (result == ffmpeg.EAGAIN || result == ffmpeg.AVERROR_EOF)
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
    /// texture reference stays valid until <see cref="ReleaseFrame"/>, and returns the
    /// <c>ID3D11Texture2D*</c>.
    /// </summary>
    private IntPtr OwnFrame(AVFrame* frame)
    {
        if ((AVPixelFormat)frame->format != AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            throw new FfmpegException(
                ffmpeg.AVERROR(ffmpeg.EINVAL),
                "avcodec_receive_frame (pipeline requires D3D11VA hardware frames)");
        }

        IntPtr texture = (IntPtr)frame->data[0];

        AVFrame* owned = ffmpeg.av_frame_alloc();
        if (owned == null)
        {
            throw new OutOfMemoryException("av_frame_alloc returned null.");
        }

        ffmpeg.av_frame_move_ref(owned, frame);
        _ownedFrames.Add((texture, (IntPtr)owned));
        return texture;
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

                AVFrame* frame = (AVFrame*)_ownedFrames[i].FramePtr;
                ffmpeg.av_frame_unref(frame);
                ffmpeg.av_frame_free(&frame);
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
        // Release every owned frame (drops the COM texture references).
        foreach ((_, IntPtr framePtr) in _ownedFrames)
        {
            AVFrame* frame = (AVFrame*)framePtr;
            ffmpeg.av_frame_unref(frame);
            ffmpeg.av_frame_free(&frame);
        }

        _ownedFrames.Clear();
        _bufferedTextures.Clear();

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
