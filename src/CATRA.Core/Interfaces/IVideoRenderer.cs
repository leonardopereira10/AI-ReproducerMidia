using CATRA.Core.Models;

namespace CATRA.Core.Interfaces;

/// <summary>
/// Presents decoded <see cref="VideoFrame"/>s into a Win32 window via a DXGI
/// swap chain (ST-05).
/// </summary>
/// <remarks>
/// The production implementation creates a D3D11 device + flip-model swap chain
/// bound to the HwndHost child window and copies the decoder texture (or uploads
/// software BGRA) to the back buffer. Abstracted so the engine is testable without
/// a real GPU or display.
/// </remarks>
public interface IVideoRenderer : IDisposable
{
    /// <summary>Whether <see cref="Initialize"/> has successfully created the swap chain.</summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Creates the D3D11 device and swap chain bound to <paramref name="windowHandle"/>.
    /// </summary>
    void Initialize(IntPtr windowHandle, int width, int height);

    /// <summary>
    /// Presents a single frame (copy to back buffer + <c>Present(1,0)</c>).
    /// Handles both hardware and software frames.
    /// </summary>
    void Present(VideoFrame frame);

    /// <summary>Recreates the back buffer / render target for a new client size.</summary>
    void Resize(int width, int height);

    /// <summary>Clears the back buffer to black and presents (used before the first frame).</summary>
    void Clear();
}
