using System.Globalization;
using System.Windows.Data;
using CATRA.Core.Enums;
using CATRA.Core.Processing;

namespace CATRA.UI.Converters;

/// <summary>
/// Converts a pipeline step (<see cref="PipelineStep"/> or
/// <see cref="ProcessStep"/>) to a human label (ST-19), e.g. "Upscale (FSR4)".
/// Pass the interpolation/upscale method as the converter parameter to suffix
/// the interp/upscale stages ("fsr4" → "(FSR4)").
/// </summary>
[ValueConversion(typeof(object), typeof(string))]
public sealed class StepToLabelConverter : IValueConverter
{
    /// <summary>Maps a step + optional method to a label (usable without a binding).</summary>
    public static string ToLabel(PipelineStep step, string? method = null)
    {
        string baseLabel = step switch
        {
            PipelineStep.Decode => "Decode",
            PipelineStep.Interp => "Interpolação",
            PipelineStep.Upscale => "Upscale",
            PipelineStep.Encode => "Encode",
            PipelineStep.Mux => "Mux",
            _ => "—",
        };

        bool methodStage = step is PipelineStep.Interp or PipelineStep.Upscale;
        if (methodStage && !string.IsNullOrWhiteSpace(method))
        {
            return $"{baseLabel} ({method!.ToUpperInvariant()})";
        }

        return baseLabel;
    }

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        PipelineStep step = value switch
        {
            PipelineStep ps => ps,
            ProcessStep ss => MapProcessStep(ss),
            _ => PipelineStep.Encode,
        };

        return ToLabel(step, parameter as string);
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("StepToLabelConverter is one-way.");

    private static PipelineStep MapProcessStep(ProcessStep step) => step switch
    {
        ProcessStep.Decode => PipelineStep.Decode,
        ProcessStep.Interp => PipelineStep.Interp,
        ProcessStep.Upscale => PipelineStep.Upscale,
        ProcessStep.Encode => PipelineStep.Encode,
        _ => PipelineStep.Encode,
    };
}
