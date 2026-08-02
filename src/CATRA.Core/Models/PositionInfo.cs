namespace CATRA.Core.Models;

/// <summary>
/// AVTransport <c>GetPositionInfo</c> result (ST-08): current playback position
/// and total track duration as reported by the DLNA renderer.
/// </summary>
/// <param name="RelTime">Current position relative to the start of the track.</param>
/// <param name="TrackDuration">Total duration (<see cref="TimeSpan.Zero"/> when the renderer does not report it).</param>
public sealed record PositionInfo(TimeSpan RelTime, TimeSpan TrackDuration);
