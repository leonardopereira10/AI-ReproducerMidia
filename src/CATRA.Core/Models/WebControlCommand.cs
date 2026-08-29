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
///   <item><c>playEpisode</c> — <see cref="EpisodeId"/>, optionally <see cref="Profile"/>.</item>
///   <item><c>switchProfile</c> — <see cref="Profile"/>.</item>
///   <item><c>browse</c> — <see cref="Target"/>, optionally <see cref="CategoryId"/> and/or <see cref="ItemId"/>.</item>
///   <item><c>playItem</c> — <see cref="ItemId"/>, optionally <see cref="Profile"/>.</item>
///   <item><c>castTo</c> — <see cref="DeviceUdn"/>.</item>
///   <item><c>castSession</c> — <see cref="DeviceUdn"/> (enable).</item>
///   <item><c>stopCasting</c> — no extra parameters.</item>
/// </list>
/// </summary>
/// <param name="Type">Command type identifier.</param>
/// <param name="Position">Target seek position in seconds (only for <c>seek</c>).</param>
/// <param name="Level">Target volume level 0–100 (only for <c>volume</c>).</param>
/// <param name="EpisodeId">Target episode database id (only for <c>playEpisode</c>).</param>
/// <param name="Profile">Playback profile label (only for <c>playEpisode</c> and <c>switchProfile</c>).</param>
/// <param name="Target">Browse target: <c>categories</c>, <c>items</c> or <c>episodes</c> (only for <c>browse</c>).</param>
/// <param name="CategoryId">Category database id for browsing items (only for <c>browse</c>).</param>
/// <param name="ItemId">Media item database id for browsing episodes or playing an item (only for <c>browse</c> / <c>playItem</c>).</param>
/// <param name="DeviceUdn">Target DLNA device UDN (only for <c>castTo</c> / <c>castSession</c>).</param>
public sealed record WebControlCommand(
    string Type,
    double? Position = null,
    int? Level = null,
    int? EpisodeId = null,       // playEpisode
    string? Profile = null,      // playEpisode, switchProfile
    string? Target = null,       // browse: "categories", "items", "episodes"
    int? CategoryId = null,      // browse items
    int? ItemId = null,          // browse episodes
    string? DeviceUdn = null)    // castTo
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
