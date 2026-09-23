using CATRA.Core.Interfaces;

namespace CATRA.Services.Processing;

/// <summary>
/// <see cref="IVideoEncoder"/> that delegates to the native AMF encoder via the
/// bridge. This is a trivial wrapper — zero behavioral change from the previous
/// direct bridge calls. The pipeline's AMF code path is unchanged (CA-2.1, CA-2.4).
/// </summary>
internal sealed class AmfBridgeEncoder : IVideoEncoder
{
    private readonly INativeBridge _bridge;
    private readonly IntPtr _context;

    /// <summary>Wraps an already-created AMF encoder context.</summary>
    public AmfBridgeEncoder(INativeBridge bridge, IntPtr context)
    {
        _bridge = bridge;
        _context = context;
    }

    /// <inheritdoc />
    public string SelectedEncoder => "AMF";

    /// <inheritdoc />
    public void EncodeFrame(IntPtr texture, out IntPtr packetBuffer, out int packetSize)
        => _bridge.EncodeFrame(_context, texture, out packetBuffer, out packetSize);

    /// <inheritdoc />
    public void Flush(out IntPtr packetBuffer, out int packetSize)
        => _bridge.FlushEncoder(_context, out packetBuffer, out packetSize);

    /// <inheritdoc />
    public void Dispose() => _bridge.DestroyEncoder(_context);
}
