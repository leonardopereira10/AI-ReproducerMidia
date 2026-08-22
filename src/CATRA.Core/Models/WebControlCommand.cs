using System.Text.Json;

namespace CATRA.Core.Models;

/// <summary>
/// A command received from the web control panel via WebSocket (ST-10).
/// The <see cref="Type"/> field determines which optional parameters are relevant:
/// <list type="bullet">
///   <item><c>play</c> — no extra parameters.</item>
///   <item><c>pause</c> — no extra parameters.</item>
///   <item><c>seek</c> — <see cref="Position"/> (seconds).</item>
///   <item><c>volume</c> — <see cref="Level"/> (0–100).</item>
///   <item><c>skipIntro</c> — no extra parameters.</item>
///   <item><c>nextEpisode</c> — no extra parameters.</item>
///   <item><c>previousEpisode</c> — no extra parameters.</item>
/// </list>
/// </summary>
/// <param name="Type">Command type identifier.</param>
/// <param name="Position">Target seek position in seconds (only for <c>seek</c>).</param>
/// <param name="Level">Target volume level 0–100 (only for <c>volume</c>).</param>
public sealed record WebControlCommand(
    string Type,
    double? Position = null,
    int? Level = null)
{
    /// <summary>
    /// Deserializes a <see cref="WebControlCommand"/> from a JSON string.
    /// Property names are matched case-insensitively.
    /// </summary>
    /// <param name="json">Raw JSON payload received from the WebSocket.</param>
    /// <returns>The parsed <see cref="WebControlCommand"/>.</returns>
    /// <exception cref="JsonException">Thrown when <paramref name="json"/> is not valid JSON or is missing required fields.</exception>
    public static WebControlCommand FromJson(string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        return JsonSerializer.Deserialize<WebControlCommand>(json, options)
               ?? throw new JsonException("Deserialized WebControlCommand is null.");
    }
}
