namespace CATRA.Core.Models;

/// <summary>
/// Container/stream metadata for an opened video file (ST-05).
/// Extracted by <see cref="Interfaces.IVideoDecoder"/> after opening the source.
/// </summary>
/// <param name="Duration">Total duration of the video stream.</param>
/// <param name="Fps">Source frames-per-second (average frame rate).</param>
/// <param name="Width">Coded width in pixels.</param>
/// <param name="Height">Coded height in pixels.</param>
/// <param name="VideoCodec">Video codec name (e.g. <c>hevc</c>, <c>h264</c>).</param>
/// <param name="AudioCodec">Primary audio codec name, if an audio stream exists.</param>
/// <param name="Title">Container <c>title</c> tag, if present.</param>
/// <param name="IsHardwareAccelerated">Whether the decoder negotiated a hardware pixel format.</param>
public class VideoMetadata
{
    private bool hardware;

    public VideoMetadata(TimeSpan duration, double fps, int width, int height, string videoCodec, string? audioCodec, string? title, bool hardware)
    {
        Duration = duration;
        Fps = fps;
        Width = width;
        Height = height;
        VideoCodec = videoCodec;
        AudioCodec = audioCodec;
        Title = title;
        this.hardware = hardware;
    }

    public VideoMetadata()
    {
        
    }

    public TimeSpan Duration { get; set; }
    public double Fps {get; set;}
    public int Width {get; set;}
    public int Height {get; set;}
    public string? Extension {get; set;}
    public string? VideoCodec {get; set;}
    public string? AudioCodec {get; set;}
    public string? Title {get; set;}
    public bool? IsHardwareAccelerated {get; set;}
    public string? Url {get; set;}
}
    
