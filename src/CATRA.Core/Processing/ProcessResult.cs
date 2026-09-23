namespace CATRA.Core.Processing;

/// <summary>
/// Outcome of a pipeline run (ST-17). For a batch, this aggregates every episode:
/// <see cref="Success"/> is <c>true</c> only when all episodes succeeded,
/// <see cref="OutputSizeBytes"/> is the sum of the successful outputs,
/// <see cref="OutputPath"/> is <c>null</c> (multiple files) and
/// <see cref="ErrorMessage"/> joins the per-episode failures.
/// </summary>
/// <param name="Success">Whether the run (or every batch episode) completed.</param>
/// <param name="OutputPath">Path of the processed file for a single run; <c>null</c> for a batch or on failure.</param>
/// <param name="OutputSizeBytes">Size of the output (single) or summed successful outputs (batch).</param>
/// <param name="Duration">Wall-clock time spent processing.</param>
/// <param name="ErrorMessage">Failure description (<c>"Cancelled"</c> on cancellation), or <c>null</c> on success.</param>
/// <param name="SelectedEncoder">Name of the encoder actually used (e.g. "AMF", "hevc_nvenc", "libx265"). Null for batch or when encoding was not reached.</param>
public sealed record ProcessResult(
    bool Success,
    string? OutputPath,
    long OutputSizeBytes,
    TimeSpan Duration,
    string? ErrorMessage,
    string? SelectedEncoder = null);
