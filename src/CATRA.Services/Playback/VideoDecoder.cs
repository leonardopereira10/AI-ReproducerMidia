using System.Runtime.InteropServices;
using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using FFmpeg.AutoGen;

namespace CATRA.Services.Playback;

/// <summary>
/// FFmpeg-backed video decoder (ST-05). Opens the container, negotiates D3D11VA
/// hardware decode (falling back to software YUV→BGRA when no GPU device is
/// available) and produces <see cref="VideoFrame"/>s one at a time.
/// </summary>
/// <remarks>
/// <para>
/// D3D11VA frames are read back to system memory here (via
/// <c>av_hwframe_transfer_data</c>) and converted to BGRA before being handed to
/// the renderer. The decoded textures live on FFmpeg's private d3d11va device,
/// while the renderer owns a separate D3D11 device: passing those textures over
/// would force cross-device <c>CopySubresourceRegion</c>/<c>Map</c>, which silently
/// corrupts the D3D11 runtime and crashes with an access violation.
/// </para>
/// <para>
/// Not unit-tested directly (requires FFmpeg binaries + a GPU); validated manually.
/// The playback engine depends on <see cref="IVideoDecoder"/> and is tested with fakes.
/// </para>
/// </remarks>
public sealed unsafe class VideoDecoder : IVideoDecoder
{
    // Rooted delegate: FFmpeg stores a native function pointer to it, so it must
    // never be garbage-collected. A static readonly field guarantees that.
    private static readonly AVCodecContext_get_format GetFormatCallback = OnGetFormat;

    private readonly object _gate = new();

    private AVFormatContext* _formatContext;
    private AVCodecContext* _codecContext;
    private AVBufferRef* _hwDeviceContext;
    private SwsContext* _swsContext;
    private AVFrame* _frame;
    private AVPacket* _packet;

    private int _videoStreamIndex = -1;
    private bool _hardware;
    private bool _opened;
    private bool _eofSent;
    private bool _disposed;

    // Frames decoded while draining an EAGAIN (decoder input buffer full) are
    // queued here so ReadVideoFrame keeps returning one frame per call and no
    // decoded picture is lost. Drained on seek (stale) and on dispose (native
    // lifetimes).
    private readonly Queue<VideoFrame> _bufferedFrames = new();

    // Cached software-scaler source geometry; the context is rebuilt if it changes.
    private int _swsWidth;
    private int _swsHeight;
    private AVPixelFormat _swsSourceFormat;

    private VideoMetadata? _metadata;
    private List<AudioTrack> _audioTracks = new();

    /// <inheritdoc />
    public VideoMetadata Metadata =>
        _metadata ?? throw new InvalidOperationException("Decoder has not been opened.");

    /// <inheritdoc />
    public IReadOnlyList<AudioTrack> AudioTracks => _audioTracks;

    /// <inheritdoc />
    public bool IsHardwareAccelerated => _hardware;

    /// <inheritdoc />
    public Task OpenAsync(string filePath, CancellationToken cancellationToken = default)
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

        return Task.CompletedTask;
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

            _hardware = TryInitializeHardware();

            // Selects AV_PIX_FMT_D3D11 when a hw device is attached, else the first
            // (software) format offered by the decoder.
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
            _audioTracks = BuildAudioTracks();
        }
        catch
        {
            // OpenInternal failed part-way: release anything already allocated so we
            // do not leak native resources, then rethrow.
            ReleaseNativeResources();
            throw;
        }
    }

    private bool TryInitializeHardware()
    {
        AVBufferRef* hw = null;
        int result = ffmpeg.av_hwdevice_ctx_create(
            &hw, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
        if (result < 0 || hw == null)
        {
            // No D3D11 device (e.g. headless / no GPU): fall back to software decode.
            return false;
        }

        _hwDeviceContext = hw;
        _codecContext->hw_device_ctx = ffmpeg.av_buffer_ref(hw);
        return true;
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

        // First software format in the list (decoder's native pixel format).
        return *formats;
    }

    private VideoMetadata BuildMetadata(AVStream* stream)
    {
        long rawDuration = _formatContext->duration;
        TimeSpan duration = rawDuration > 0
            ? TimeSpan.FromSeconds((double)rawDuration / ffmpeg.AV_TIME_BASE)
            : TimeSpan.Zero;

        AVRational fpsRational = ffmpeg.av_guess_frame_rate(_formatContext, stream, null);
        double fps = fpsRational.den != 0 ? (double)fpsRational.num / fpsRational.den : 0.0;

        string videoCodec = ffmpeg.avcodec_get_name(_codecContext->codec_id) ?? "unknown";

        string? audioCodec = null;
        AVCodec* audioCodecPtr = null;
        int audioIndex = ffmpeg.av_find_best_stream(
            _formatContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &audioCodecPtr, 0);
        if (audioIndex >= 0)
        {
            audioCodec = ffmpeg.avcodec_get_name(_formatContext->streams[audioIndex]->codecpar->codec_id);
        }

        string? title = GetDictionaryString(_formatContext->metadata, "title");

        return new VideoMetadata(
            duration,
            fps,
            _codecContext->width,
            _codecContext->height,
            videoCodec,
            audioCodec,
            title,
            _hardware);
    }

    private List<AudioTrack> BuildAudioTracks()
    {
        var tracks = new List<AudioTrack>();
        for (int i = 0; i < _formatContext->nb_streams; i++)
        {
            AVStream* stream = _formatContext->streams[i];
            AVCodecParameters* par = stream->codecpar;
            if (par->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                continue;
            }

            tracks.Add(new AudioTrack(
                Index: i,
                Language: GetDictionaryString(stream->metadata, "language"),
                Codec: ffmpeg.avcodec_get_name(par->codec_id) ?? "unknown",
                Channels: par->ch_layout.nb_channels,
                SampleRate: par->sample_rate));
        }

        return tracks;
    }

    /// <inheritdoc />
    public VideoFrame? ReadVideoFrame()
    {
        lock (_gate)
        {
            EnsureOpened();

            while (true)
            {
                if (_bufferedFrames.Count > 0)
                {
                    return _bufferedFrames.Dequeue();
                }

                if (_eofSent)
                {
                    // Drain any frames buffered inside the decoder, then signal EOF.
                    return TryReceiveFrame();
                }

                int result = ffmpeg.av_read_frame(_formatContext, _packet);
                if (result == ffmpeg.AVERROR_EOF)
                {
                    // Flush: a null packet tells the decoder to emit buffered frames.
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

                // EAGAIN means "decoder full, drain me" (common with B-frames), not
                // an error; the packet is unreferenced only once accepted, so its
                // data can be resubmitted after draining and is never lost.
                int send = SendPacketDraining(_packet);
                ffmpeg.av_packet_unref(_packet);
                if (send < 0 && send != ffmpeg.AVERROR_EOF)
                {
                    throw new FfmpegException(send, "avcodec_send_packet");
                }

                VideoFrame? frame = TryReceiveFrame();
                if (frame is not null)
                {
                    return frame;
                }
            }
        }
    }

    /// <summary>
    /// Sends <paramref name="packet"/> (or <c>null</c> to flush) to the decoder,
    /// treating <c>EAGAIN</c> as "the input buffer is full" rather than an error:
    /// decoded frames are pulled out — queued in <see cref="_bufferedFrames"/> so
    /// none is lost — until the decoder accepts the packet, which is then resent
    /// unchanged. FFmpeg guarantees <c>avcodec_send_packet</c> and
    /// <c>avcodec_receive_frame</c> never both return <c>EAGAIN</c>, so the loop
    /// always makes progress.
    /// </summary>
    private int SendPacketDraining(AVPacket* packet)
    {
        int result = ffmpeg.avcodec_send_packet(_codecContext, packet);
        while (result == -ffmpeg.EAGAIN)
        {
            while (true)
            {
                VideoFrame? drained = TryReceiveFrame();
                if (drained is null)
                {
                    break; // decoder asked for input (or reached EOF): retry the send
                }

                _bufferedFrames.Enqueue(drained);
            }

            result = ffmpeg.avcodec_send_packet(_codecContext, packet);
        }

        return result;
    }

    private VideoFrame? TryReceiveFrame()
    {
        int result = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
        if (result == -ffmpeg.EAGAIN || result == ffmpeg.AVERROR_EOF)
        {
            return null;
        }

        if (result < 0)
        {
            throw new FfmpegException(result, "avcodec_receive_frame");
        }

        return CreateFrame(_frame);
    }

    private VideoFrame CreateFrame(AVFrame* frame)
    {
        TimeSpan pts = TimestampToTimeSpan(
            frame->best_effort_timestamp != long.MinValue ? frame->best_effort_timestamp : frame->pts);
        int width = frame->width;
        int height = frame->height;

        if ((AVPixelFormat)frame->format == AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            // Read the GPU picture back on FFmpeg's own device (legal) instead of
            // exposing the texture to the renderer's different device (undefined
            // behaviour — see class remarks). Future optimisation: wrap the
            // renderer's device in the FFmpeg hw-device context so hardware frames
            // can be copied zero-copy.
            AVFrame* software = ffmpeg.av_frame_alloc();
            if (software == null)
            {
                throw new OutOfMemoryException("av_frame_alloc returned null.");
            }

            int transfer = ffmpeg.av_hwframe_transfer_data(software, frame, 0);
            if (transfer < 0)
            {
                ffmpeg.av_frame_free(&software);
                throw new FfmpegException(transfer, "av_hwframe_transfer_data");
            }

            // av_hwframe_transfer_data copies frame properties but not the
            // decoder-derived best-effort timestamp.
            software->best_effort_timestamp = frame->best_effort_timestamp;

            try
            {
                byte[] bgra = ConvertToBgra(software, width, height);
                return VideoFrame.CreateSoftware(pts, width, height, bgra);
            }
            finally
            {
                ffmpeg.av_frame_free(&software);
            }
        }

        byte[] softwareBgra = ConvertToBgra(frame, width, height);
        return VideoFrame.CreateSoftware(pts, width, height, softwareBgra);
    }

    private byte[] ConvertToBgra(AVFrame* frame, int width, int height)
    {
        AVPixelFormat sourceFormat = (AVPixelFormat)frame->format;
        if (_swsContext == null || _swsWidth != width || _swsHeight != height || _swsSourceFormat != sourceFormat)
        {
            if (_swsContext != null)
            {
                ffmpeg.sws_freeContext(_swsContext);
                _swsContext = null;
            }

            _swsContext = ffmpeg.sws_getContext(
                width, height, sourceFormat,
                width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)FFmpeg.AutoGen.SwsFlags.SWS_BILINEAR, null, null, null);
            if (_swsContext == null)
            {
                throw new InvalidOperationException("sws_getContext failed to create a scaler.");
            }

            _swsWidth = width;
            _swsHeight = height;
            _swsSourceFormat = sourceFormat;
        }

        byte[] bgra = new byte[(long)width * height * 4];
        int destinationStride = width * 4;

        fixed (byte* destination = bgra)
        {
            byte*[] dstSlices = { destination, null, null, null };
            int[] dstStrides = { destinationStride, 0, 0, 0 };
            byte*[] srcSlices = frame->data.ToArray();
            int[] srcStrides = frame->linesize.ToArray();

            int scaled = ffmpeg.sws_scale(
                _swsContext, srcSlices, srcStrides, 0, height, dstSlices, dstStrides);
            if (scaled < 0)
            {
                throw new FfmpegException(scaled, "sws_scale");
            }
        }

        return bgra;
    }

    private TimeSpan TimestampToTimeSpan(long timestamp)
    {
        if (timestamp <= 0)
        {
            return TimeSpan.Zero;
        }

        AVStream* stream = _formatContext->streams[_videoStreamIndex];
        double seconds = timestamp * ffmpeg.av_q2d(stream->time_base);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <inheritdoc />
    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            EnsureOpened();

            AVStream* stream = _formatContext->streams[_videoStreamIndex];
            double timeBase = ffmpeg.av_q2d(stream->time_base);
            long target = timeBase > 0 ? (long)(position.TotalSeconds / timeBase) : 0;

            int result = ffmpeg.av_seek_frame(
                _formatContext, _videoStreamIndex, target, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (result < 0)
            {
                throw new FfmpegException(result, "av_seek_frame");
            }

            ffmpeg.avcodec_flush_buffers(_codecContext);
            _eofSent = false;

            // Queued frames belong to the pre-seek timeline: drop them.
            ClearBufferedFrames();
        }
    }

    private void ClearBufferedFrames()
    {
        while (_bufferedFrames.Count > 0)
        {
            _bufferedFrames.Dequeue().Dispose();
        }
    }

    private static string? GetDictionaryString(AVDictionary* dictionary, string key)
    {
        if (dictionary == null)
        {
            return null;
        }

        AVDictionaryEntry* entry = ffmpeg.av_dict_get(dictionary, key, null, 0);
        if (entry == null || entry->value == null)
        {
            return null;
        }

        return Marshal.PtrToStringAnsi((IntPtr)entry->value);
    }

    private static void ThrowIfError(int result, string context)
    {
        if (result < 0)
        {
            throw new FfmpegException(result, context);
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
        ClearBufferedFrames();

        if (_swsContext != null)
        {
            ffmpeg.sws_freeContext(_swsContext);
            _swsContext = null;
        }

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
            // hw_device_ctx is caller-owned; release the reference we added before
            // freeing the context (avcodec_free_context does not unref it).
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
