using CATRA.Core.Enums;

namespace CATRA.Services.Library;

/// <summary>
/// RN-01: a folder with a single video file is a movie; two or more video files
/// make it a series. Manual override remains possible through the UI (Phase 1 UI).
/// </summary>
public static class MediaTypeDetector
{
    /// <summary>Detects the media type from the video file count of a level-2 folder.</summary>
    public static MediaType Detect(int videoFileCount) =>
        videoFileCount >= 2 ? MediaType.Series : MediaType.Movie;
}
