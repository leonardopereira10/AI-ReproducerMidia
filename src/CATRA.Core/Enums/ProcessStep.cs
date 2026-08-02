namespace CATRA.Core.Enums;

/// <summary>
/// Pipeline stage of a processing job. Persisted as lowercase text in the
/// <c>ProcessJob.CurrentStep</c> column.
/// </summary>
public enum ProcessStep
{
    /// <summary>FFmpeg hardware decode.</summary>
    Decode,

    /// <summary>Frame interpolation (RIFE / FSR3 FG).</summary>
    Interp,

    /// <summary>FSR upscale.</summary>
    Upscale,

    /// <summary>AMF H.265 encode.</summary>
    Encode,
}
