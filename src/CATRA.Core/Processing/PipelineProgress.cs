namespace CATRA.Core.Processing;

/// <summary>
/// A single progress sample emitted by <see cref="Interfaces.IProcessingPipeline"/>
/// through <see cref="IProgress{T}"/> (ST-17).
/// </summary>
/// <param name="EpisodeIndex">Zero-based index of the episode within the batch.</param>
/// <param name="EpisodeCount">Total episodes in the batch (1 for a single run).</param>
/// <param name="CurrentStep">The pipeline stage currently executing.</param>
/// <param name="StepProgressPct">Progress within <paramref name="CurrentStep"/> (0–100).</param>
/// <param name="OverallPct">Weighted progress across the whole episode (0–100).</param>
/// <param name="Elapsed">Wall-clock time spent on this episode so far.</param>
/// <param name="Eta">Estimated time remaining for this episode, or <c>null</c> before a
/// rate can be established (e.g. during the mux stage or before any frame is timed).</param>
public sealed record PipelineProgress(
    int EpisodeIndex,
    int EpisodeCount,
    PipelineStep CurrentStep,
    double StepProgressPct,
    double OverallPct,
    TimeSpan Elapsed,
    TimeSpan? Eta);
