using CATRA.Core.Interfaces;
using CATRA.Core.Processing;

namespace CATRA.Services.Processing;

/// <summary>
/// Production <see cref="IVideoEncoderFactory"/>: delegates to
/// <see cref="EncoderFallbackFactory.Create"/> with the vendored FFmpeg path.
/// </summary>
internal sealed class ProductionEncoderFactory : IVideoEncoderFactory
{
    private readonly string _ffmpegPath;

    /// <summary>Creates the factory with the resolved FFmpeg path.</summary>
    public ProductionEncoderFactory()
        : this(EncoderFallbackFactory.ResolveFFmpegPath())
    {
    }

    /// <summary>Creates the factory with an explicit FFmpeg path (for testing).</summary>
    public ProductionEncoderFactory(string ffmpegPath)
    {
        _ffmpegPath = ffmpegPath;
    }

    /// <inheritdoc />
    public IVideoEncoder Create(
        INativeBridge bridge,
        int width, int height,
        int bitrateKbps, double fps,
        Action<EncoderFallbackEventArgs>? onFallback)
    {
        return EncoderFallbackFactory.Create(bridge, _ffmpegPath, width, height,
            bitrateKbps, fps, onFallback);
    }
}
