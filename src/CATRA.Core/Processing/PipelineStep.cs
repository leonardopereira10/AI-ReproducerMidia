namespace CATRA.Core.Processing;

/// <summary>
/// Stages of the offline pre-processing pipeline (ST-17). Reported through
/// <see cref="PipelineProgress.CurrentStep"/> so the UI can show which stage is
/// active. Mirrors <see cref="Enums.ProcessStep"/> but adds the final
/// <see cref="Mux"/> stage (audio multiplexing), which the job-table enum predates.
/// </summary>
public enum PipelineStep
{
    /// <summary>FFmpeg hardware decode of the source frames.</summary>
    Decode,

    /// <summary>Frame interpolation (RIFE / FSR 3 FG).</summary>
    Interp,

    /// <summary>FSR upscale (FSR 4 / FSR 1).</summary>
    Upscale,

    /// <summary>AMF H.265 encode.</summary>
    Encode,

    /// <summary>Audio extraction + multiplexing into the output container.</summary>
    Mux,
}
