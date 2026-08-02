using CATRA.Core.Enums;

namespace CATRA.Core.Processing;

/// <summary>
/// Immutable per-profile configuration for a pre-processing run (ST-17). Built
/// from <c>AppSettings</c> (local / dlna targets) and handed to
/// <see cref="Interfaces.IProcessingPipeline"/>.
/// </summary>
/// <remarks>
/// Lives in <c>CATRA.Core</c> (not <c>CATRA.Services</c>) because the pipeline
/// contract <see cref="Interfaces.IProcessingPipeline"/> references it and Core
/// must remain a dependency-free leaf.
/// </remarks>
/// <param name="Profile">Output profile (Local 1080p135 / Dlna 4K55).</param>
/// <param name="TargetWidth">Output width in pixels.</param>
/// <param name="TargetHeight">Output height in pixels.</param>
/// <param name="TargetFps">Output frames-per-second.</param>
/// <param name="EncodeBitrateKbps">AMF CBR target bitrate in kbit/s.</param>
/// <param name="InterpMethod">Interpolation backend: <c>"rife"</c> | <c>"fsr3fg"</c>.</param>
/// <param name="UpscaleMethod">Upscale backend: <c>"fsr4"</c> | <c>"fsr1"</c>.</param>
/// <param name="OutputFolder">Directory that receives the processed <c>.mp4</c> files.</param>
public sealed record PipelineConfig(
    ProcessProfile Profile,
    int TargetWidth,
    int TargetHeight,
    double TargetFps,
    int EncodeBitrateKbps,
    string InterpMethod,
    string UpscaleMethod,
    string OutputFolder);
