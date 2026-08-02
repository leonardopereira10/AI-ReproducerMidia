using CATRA.Core.Interfaces;
using CATRA.Core.Models;
using FFmpeg.AutoGen;

namespace CATRA.Services.Playback;

/// <summary>
/// FFmpeg-backed audio decoder (ST-05). Runs its own demuxer over the media file,
/// decodes the selected audio stream and resamples every frame to interleaved
/// 32-bit float via libswresample, ready for <see cref="IAudioRenderer"/>.
/// </summary>
/// <remarks>
/// Not unit-tested directly (requires FFmpeg binaries); validated manually. The
/// playback engine depends on <see cref="IAudioDecoder"/> and is tested with fakes.
/// </remarks>
public sealed unsafe class AudioDecoder : IAudioDecoder
{
    private readonly object _gate = new();

    private AVFormatContext* _formatContext;
    private AVCodecContext* _codecContext;
    private SwrContext* _swrContext;
    private AVFrame* _frame;
    private AVPacket* _packet;

    private int _audioStreamIndex = -1;
    private int _sampleRate;
    private int _channels;
    private bool _opened;
    private bool _eofSent;
    private bool _disposed;

    // Frames decoded while draining an EAGAIN (decoder input buffer full) are
    // queued here so ReadSamples keeps returning one buffer per call, in decode
    // order, without losing any. Cleared on seek (stale post-flush).
    private readonly Queue<AudioFrame> _bufferedFrames = new();

    // Cached resampler input geometry; the context is rebuilt if it changes.
    private AVSampleFormat _swrInFormat;
    private int _swrInRate;
    private int _swrInChannels;

    /// <inheritdoc />
    public int SampleRate => _sampleRate;

    /// <inheritdoc />
    public int Channels => _channels;

    /// <inheritdoc />
    public void Open(string filePath, int audioStreamIndex = -1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_opened)
            {
                throw new InvalidOperationException("Audio decoder is already opened.");
            }

            OpenInternal(filePath, audioStreamIndex);
            _opened = true;
        }
    }

    private void OpenInternal(string filePath, int audioStreamIndex)
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
            _audioStreamIndex = audioStreamIndex >= 0
                ? audioStreamIndex
                : ffmpeg.av_find_best_stream(
                    _formatContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);

            if (_audioStreamIndex < 0)
            {
                throw new InvalidOperationException("The file contains no audio stream.");
            }

            AVStream* stream = _formatContext->streams[_audioStreamIndex];
            if (codec == null)
            {
                codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            }

            if (codec == null)
            {
                throw new InvalidOperationException("No decoder found for the audio stream.");
            }

            _codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecContext == null)
            {
                throw new OutOfMemoryException("avcodec_alloc_context3 returned null.");
            }

            result = ffmpeg.avcodec_parameters_to_context(_codecContext, stream->codecpar);
            ThrowIfError(result, "avcodec_parameters_to_context");

            result = ffmpeg.avcodec_open2(_codecContext, codec, null);
            ThrowIfError(result, "avcodec_open2");

            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            if (_packet == null || _frame == null)
            {
                throw new OutOfMemoryException("Failed to allocate FFmpeg packet/frame.");
            }

            // Output format is fixed at open time so the audio renderer can be
            // initialised before the first frame is decoded.
            _sampleRate = stream->codecpar->sample_rate > 0 ? stream->codecpar->sample_rate : 48000;
            _channels = stream->codecpar->ch_layout.nb_channels > 0
                ? stream->codecpar->ch_layout.nb_channels
                : 2;
        }
        catch
        {
            ReleaseNativeResources();
            throw;
        }
    }

    /// <inheritdoc />
    public AudioFrame? ReadSamples()
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
                    // Drain decoder frames, then flush the resampler once.
                    AudioFrame? drained = TryReceiveAudio();
                    if (drained is not null)
                    {
                        return drained;
                    }

                    return FlushResampler();
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

                if (_packet->stream_index != _audioStreamIndex)
                {
                    ffmpeg.av_packet_unref(_packet);
                    continue;
                }

                // EAGAIN means "decoder full, drain me", not an error; the packet
                // is unreferenced only once accepted, so its data can be resubmitted
                // after draining and is never lost.
                int send = SendPacketDraining(_packet);
                ffmpeg.av_packet_unref(_packet);
                if (send < 0 && send != ffmpeg.AVERROR_EOF)
                {
                    throw new FfmpegException(send, "avcodec_send_packet");
                }

                AudioFrame? frame = TryReceiveAudio();
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
        while (result == ffmpeg.EAGAIN)
        {
            while (true)
            {
                AudioFrame? drained = TryReceiveAudio();
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

    private AudioFrame? TryReceiveAudio()
    {
        int result = ffmpeg.avcodec_receive_frame(_codecContext, _frame);
        if (result == ffmpeg.EAGAIN || result == ffmpeg.AVERROR_EOF)
        {
            return null;
        }

        if (result < 0)
        {
            throw new FfmpegException(result, "avcodec_receive_frame");
        }

        AudioFrame frame = Resample(_frame);
        ffmpeg.av_frame_unref(_frame);
        return frame;
    }

    private AudioFrame Resample(AVFrame* frame)
    {
        EnsureResampler(frame);

        int inSamples = frame->nb_samples;
        int outSamples = ffmpeg.swr_get_out_samples(_swrContext, inSamples);
        if (outSamples <= 0)
        {
            outSamples = inSamples > 0 ? inSamples : 1024;
        }

        float[] output = new float[(long)outSamples * _channels];
        int converted;

        byte*[] sourceData = frame->data.ToArray();
        fixed (byte** source = sourceData)
        fixed (float* destination = output)
        {
            byte*[] destData = { (byte*)destination };
            fixed (byte** dest = destData)
            {
                converted = ffmpeg.swr_convert(_swrContext, dest, outSamples, source, inSamples);
            }
        }

        if (converted < 0)
        {
            throw new FfmpegException(converted, "swr_convert");
        }

        int totalSamples = converted * _channels;
        if (totalSamples != output.Length)
        {
            Array.Resize(ref output, totalSamples);
        }

        return new AudioFrame(output, totalSamples, _channels, TimestampToTimeSpan(frame));
    }

    private AudioFrame? FlushResampler()
    {
        if (_swrContext == null)
        {
            return null;
        }

        int outSamples = ffmpeg.swr_get_out_samples(_swrContext, 0);
        if (outSamples <= 0)
        {
            // Nothing buffered inside the resampler.
            SwrContext* swr = _swrContext;
            ffmpeg.swr_free(&swr);
            _swrContext = null;
            return null;
        }

        float[] output = new float[(long)outSamples * _channels];
        int converted;
        fixed (float* destination = output)
        {
            byte*[] destData = { (byte*)destination };
            fixed (byte** dest = destData)
            {
                converted = ffmpeg.swr_convert(_swrContext, dest, outSamples, null, 0);
            }
        }

        SwrContext* context = _swrContext;
        ffmpeg.swr_free(&context);
        _swrContext = null;

        if (converted <= 0)
        {
            return null;
        }

        int totalSamples = converted * _channels;
        Array.Resize(ref output, totalSamples);
        return new AudioFrame(output, totalSamples, _channels, TimeSpan.Zero);
    }

    private void EnsureResampler(AVFrame* frame)
    {
        AVSampleFormat inFormat = (AVSampleFormat)frame->format;
        int inRate = frame->sample_rate > 0 ? frame->sample_rate : _sampleRate;
        int inChannels = frame->ch_layout.nb_channels;

        if (_swrContext != null && _swrInFormat == inFormat && _swrInRate == inRate && _swrInChannels == inChannels)
        {
            return;
        }

        if (_swrContext != null)
        {
            SwrContext* old = _swrContext;
            ffmpeg.swr_free(&old);
            _swrContext = null;
        }

        AVChannelLayout outLayout;
        ffmpeg.av_channel_layout_default(&outLayout, _channels);

        SwrContext* swr = null;
        int result = ffmpeg.swr_alloc_set_opts2(
            &swr,
            &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, _sampleRate,
            &frame->ch_layout, inFormat, inRate,
            0, null);
        ffmpeg.av_channel_layout_uninit(&outLayout);
        ThrowIfError(result, "swr_alloc_set_opts2");

        result = ffmpeg.swr_init(swr);
        if (result < 0)
        {
            ffmpeg.swr_free(&swr);
            ThrowIfError(result, "swr_init");
        }

        _swrContext = swr;
        _swrInFormat = inFormat;
        _swrInRate = inRate;
        _swrInChannels = inChannels;
    }

    private TimeSpan TimestampToTimeSpan(AVFrame* frame)
    {
        long timestamp = frame->best_effort_timestamp != long.MinValue
            ? frame->best_effort_timestamp
            : frame->pts;
        if (timestamp <= 0)
        {
            return TimeSpan.Zero;
        }

        AVStream* stream = _formatContext->streams[_audioStreamIndex];
        double seconds = timestamp * ffmpeg.av_q2d(stream->time_base);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <inheritdoc />
    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            EnsureOpened();

            AVStream* stream = _formatContext->streams[_audioStreamIndex];
            double timeBase = ffmpeg.av_q2d(stream->time_base);
            long target = timeBase > 0 ? (long)(position.TotalSeconds / timeBase) : 0;

            int result = ffmpeg.av_seek_frame(
                _formatContext, _audioStreamIndex, target, ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (result < 0)
            {
                throw new FfmpegException(result, "av_seek_frame");
            }

            ffmpeg.avcodec_flush_buffers(_codecContext);
            if (_swrContext != null)
            {
                SwrContext* swr = _swrContext;
                ffmpeg.swr_free(&swr);
                _swrContext = null;
            }

            _eofSent = false;

            // Queued frames belong to the pre-seek timeline (and the resampler was
            // just reset): drop them.
            _bufferedFrames.Clear();
        }
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
            throw new InvalidOperationException("Audio decoder has not been opened.");
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
        if (_swrContext != null)
        {
            SwrContext* swr = _swrContext;
            ffmpeg.swr_free(&swr);
            _swrContext = null;
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
            AVCodecContext* context = _codecContext;
            ffmpeg.avcodec_free_context(&context);
            _codecContext = null;
        }

        if (_formatContext != null)
        {
            AVFormatContext* format = _formatContext;
            ffmpeg.avformat_close_input(&format);
            _formatContext = null;
        }
    }
}
