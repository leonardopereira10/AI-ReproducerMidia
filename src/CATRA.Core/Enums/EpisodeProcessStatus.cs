namespace CATRA.Core.Enums;

/// <summary>
/// Pre-processing badge state for an episode (ST-19, Tela 2/3). Derived from
/// the episode's processed file and job for the active profile so the UI can
/// show ⚙ fila / ✓ pronto / ○ original / ↻ stale. Lives in Core so both the
/// <see cref="Library.EpisodeDetail"/> read model and the UI converters share it.
/// </summary>
public enum EpisodeProcessStatus
{
    /// <summary>No processed file and no active job — the original source (○).</summary>
    Original,

    /// <summary>Queued for processing in the sliding window (⚙).</summary>
    Queued,

    /// <summary>Currently being processed (⚙).</summary>
    Processing,

    /// <summary>A processed file exists for the active profile (✓).</summary>
    Ready,

    /// <summary>The source hash changed after processing — needs reprocessing (↻).</summary>
    Stale,
}
