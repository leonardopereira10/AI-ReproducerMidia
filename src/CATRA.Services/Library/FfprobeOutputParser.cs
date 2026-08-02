using System.Globalization;
using System.Text.Json;
using CATRA.Core.Models;

namespace CATRA.Services.Library;

/// <summary>
/// Parses ffprobe JSON output (<c>-show_format -show_streams</c>) into a
/// <see cref="MediaProbeResult"/>. Kept separate from
/// <see cref="FfprobeMediaProbeService"/> so it is unit-testable with captured
/// JSON samples — no ffprobe binary required.
/// </summary>
public static class FfprobeOutputParser
{
    /// <summary>
    /// Parses ffprobe JSON. Returns <c>null</c> for blank/malformed input or when
    /// no usable field is found.
    /// </summary>
    public static MediaProbeResult? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            double? duration = null;
            string? title = null;
            if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object)
            {
                if (format.TryGetProperty("duration", out var durationElement) &&
                    double.TryParse(
                        durationElement.GetString(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var durationValue))
                {
                    duration = durationValue;
                }

                if (format.TryGetProperty("tags", out var tags) &&
                    tags.ValueKind == JsonValueKind.Object &&
                    tags.TryGetProperty("title", out var titleElement))
                {
                    title = titleElement.GetString();
                }
            }

            double? fps = null;
            int? width = null;
            int? height = null;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.ValueKind != JsonValueKind.Object ||
                        !stream.TryGetProperty("codec_type", out var codecType) ||
                        codecType.GetString() != "video")
                    {
                        continue;
                    }

                    if (stream.TryGetProperty("width", out var widthElement) &&
                        widthElement.ValueKind == JsonValueKind.Number)
                    {
                        width = widthElement.GetInt32();
                    }

                    if (stream.TryGetProperty("height", out var heightElement) &&
                        heightElement.ValueKind == JsonValueKind.Number)
                    {
                        height = heightElement.GetInt32();
                    }

                    fps = ParseFrameRate(stream, "avg_frame_rate") ??
                          ParseFrameRate(stream, "r_frame_rate");
                    break;
                }
            }

            if (duration is null && fps is null && width is null && height is null && title is null)
            {
                return null;
            }

            return new MediaProbeResult(duration, fps, width, height, title);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? ParseFrameRate(JsonElement stream, string propertyName)
    {
        if (!stream.TryGetProperty(propertyName, out var rate))
        {
            return null;
        }

        var value = rate.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Fraction form: "24000/1001" (ffprobe default). "0/0" means unknown.
        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator > 0)
        {
            return numerator / denominator;
        }

        // Plain number form, just in case.
        if (parts.Length == 1 &&
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var direct))
        {
            return direct;
        }

        return null;
    }
}
