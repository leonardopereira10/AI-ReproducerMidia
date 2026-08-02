using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Orchestrates DLNA transmission (ST-08, RF-06): discovery, registering the
/// media file on the embedded HTTP server, pointing the renderer at it
/// (AVTransport SetAVTransportURI + Play) and remote transport/volume control
/// with 1s position polling.
/// </summary>
public interface ICastingService
{
    /// <summary>Current session state.</summary>
    CastingState State { get; }

    /// <summary>The renderer being cast to, or <c>null</c> when idle.</summary>
    DlnaDeviceInfo? CurrentDevice { get; }

    /// <summary>Last error message (set when <see cref="State"/> is <see cref="CastingState.Error"/>).</summary>
    string? ErrorMessage { get; }

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    event EventHandler<CastingState>? StateChanged;

    /// <summary>Raised ~1s cadence with the renderer's reported playback position.</summary>
    event EventHandler<TimeSpan>? PositionChanged;

    /// <summary>Runs one SSDP discovery pass (AVTransport-capable renderers only).</summary>
    Task<List<DlnaDeviceInfo>> DiscoverDevicesAsync();

    /// <summary>
    /// Registers <paramref name="filePath"/> on the HTTP server, sends
    /// SetAVTransportURI (DIDL-Lite metadata with <paramref name="title"/>) and
    /// Play. Transitions Idle → Connecting → Streaming (or Error).
    /// </summary>
    Task StartCastingAsync(DlnaDeviceInfo device, string filePath, string title);

    /// <summary>Resumes playback on the renderer.</summary>
    Task PlayAsync();

    /// <summary>Pauses playback on the renderer.</summary>
    Task PauseAsync();

    /// <summary>Stops the renderer and unregisters the media file (server keeps running).</summary>
    Task StopCastingAsync();

    /// <summary>Seeks the renderer to <paramref name="position"/> (REL_TIME).</summary>
    Task SeekAsync(TimeSpan position);

    /// <summary>Sets the renderer Master volume (0–100).</summary>
    Task SetVolumeAsync(int volume);

    /// <summary>Queries the renderer's current position.</summary>
    Task<TimeSpan> GetPositionAsync();
}
