using CATRA.Core.Models;
using CATRA.Core.Processing;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Orchestrates the offline GPU pre-processing pipeline (ST-17):
/// decode → frame interpolation → FSR upscale → AMF H.265 encode → audio mux.
/// Manages the lifecycle of the native GPU contexts (always destroyed, even on
/// error/cancellation), reports weighted progress with an ETA, supports cancellation
/// and processes batches without letting a single episode failure abort the run.
/// </summary>
/// <remarks>
/// Depends only on abstractions (<see cref="INativeBridge"/>, <see cref="IFrameDecoder"/>,
/// <see cref="IAudioMuxer"/>) so the full flow is unit-testable with fakes — no GPU,
/// native DLL or ffmpeg binary required. Real end-to-end execution (valid H.265 output,
/// A/V sync, no GPU leak) is validated manually.
/// </remarks>
public interface IProcessingPipeline
{
    /// <summary>
    /// Processes a single episode and returns the outcome. Never throws for expected
    /// failures (native / ffmpeg / IO / cancellation): those surface as a
    /// <see cref="ProcessResult"/> with <see cref="ProcessResult.Success"/> = <c>false</c>.
    /// </summary>
    Task<ProcessResult> ProcessAsync(
        Episode episode,
        PipelineConfig config,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Processes a batch of episodes sequentially. A failure in one episode does NOT
    /// abort the batch: every episode is attempted and the result aggregates the
    /// outcomes (see <see cref="ProcessResult"/>).
    /// </summary>
    Task<ProcessResult> ProcessBatchAsync(
        List<Episode> episodes,
        PipelineConfig config,
        IProgress<PipelineProgress> progress,
        CancellationToken cancellationToken);
}
