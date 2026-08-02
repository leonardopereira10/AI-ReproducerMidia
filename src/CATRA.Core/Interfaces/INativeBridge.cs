namespace CATRA.Core.Interfaces;

/// <summary>
/// Managed façade over the <c>catra-gpu</c> native DLL (ST-12): GPU frame
/// interpolation (RIFE / FSR 3 FG), upscaling (FSR 4 / FSR 1) and AMF H.265
/// encoding, all of which live in C++/DX12.
/// </summary>
/// <remarks>
/// <para>
/// The bridge degrades gracefully when the native library is absent
/// (<see cref="IsAvailable"/> is <c>false</c>): capability queries return safe
/// defaults (<c>IsFsr4Available</c> → <c>false</c>, mode/method → <c>0</c>) and
/// <see cref="Shutdown"/> / destroy calls are no-ops, while operations that need
/// the GPU throw a native-bridge exception instead of a raw
/// <c>DllNotFoundException</c>.
/// </para>
/// <para>
/// Context handles are opaque <see cref="IntPtr"/> values (the native side uses
/// dense <c>int</c> handles); treat them as tokens to echo back, never dereference.
/// This interface is a low-level bridge — the processing pipeline (later STs)
/// wraps it with higher-level, resource-safe abstractions.
/// </para>
/// </remarks>
public interface INativeBridge : IDisposable
{
    /// <summary>Whether the native <c>catra-gpu</c> library was found and can be called.</summary>
    bool IsAvailable { get; }

    /// <summary>Whether <see cref="Initialize"/> has succeeded (and <see cref="Shutdown"/> has not run since).</summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Initializes the bridge on top of the caller's D3D11 device (an
    /// <c>ID3D11Device*</c>). Throws a native-bridge exception when the library
    /// is unavailable or the native call fails.
    /// </summary>
    void Initialize(IntPtr d3d11Device);

    /// <summary>Tears down the bridge and releases native resources. Idempotent; no-op when unavailable.</summary>
    void Shutdown();

    /// <summary>Whether FSR 4 is usable on the active adapter. Returns <c>false</c> when unavailable.</summary>
    bool IsFsr4Available();

    /// <summary>Current upscale mode: 0 = off, 1 = FSR 1, 2 = FSR 4. Returns 0 when unavailable.</summary>
    int GetUpscaleMode();

    /// <summary>Active interpolation method: 0 = none, 1 = RIFE, 2 = FSR 3 FG. Returns 0 when unavailable.</summary>
    int GetInterpMethod();

    // --- Frame interpolation (stubs until ST-13) ---------------------------

    /// <summary>Creates an interpolation job; returns an opaque context handle.</summary>
    IntPtr CreateInterpolation(int srcWidth, int srcHeight, double srcFps, double targetFps, int method);

    /// <summary>
    /// Feeds a consecutive frame pair and produces intermediate frames. Returns the
    /// number of generated frames and writes the native frame array pointer to
    /// <paramref name="framesBuffer"/>.
    /// </summary>
    int ProcessInterpolation(IntPtr context, IntPtr frameA, IntPtr frameB, out IntPtr framesBuffer);

    /// <summary>Destroys an interpolation job. No-op when unavailable.</summary>
    void DestroyInterpolation(IntPtr context);

    // --- Upscale (stubs until ST-14) ---------------------------------------

    /// <summary>Creates an upscaler; returns an opaque context handle.</summary>
    IntPtr CreateUpscaler(int srcWidth, int srcHeight, int dstWidth, int dstHeight, int method);

    /// <summary>Upscales one texture and returns the destination texture pointer.</summary>
    IntPtr ProcessUpscale(IntPtr context, IntPtr srcTexture);

    /// <summary>Destroys an upscaler. No-op when unavailable.</summary>
    void DestroyUpscaler(IntPtr context);

    // --- Encode (stubs until ST-16) ----------------------------------------

    /// <summary>Creates an H.265 encoder; returns an opaque context handle.</summary>
    IntPtr CreateEncoder(int width, int height, int bitrateKbps, double fps);

    /// <summary>Encodes one texture, writing the packet bytes/size to the out parameters.</summary>
    void EncodeFrame(IntPtr context, IntPtr texture, out IntPtr packetBuffer, out int packetSize);

    /// <summary>Drains buffered packets, writing the packet bytes/size to the out parameters.</summary>
    void FlushEncoder(IntPtr context, out IntPtr packetBuffer, out int packetSize);

    /// <summary>Destroys an encoder. No-op when unavailable.</summary>
    void DestroyEncoder(IntPtr context);
}
