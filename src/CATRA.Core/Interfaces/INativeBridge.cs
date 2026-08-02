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

    // --- Frame interpolation (RIFE v4, ST-13) ------------------------------

    /// <summary>
    /// Creates an interpolation job; returns an opaque context handle.
    /// </summary>
    /// <remarks>
    /// The intermediate-frame count is computed natively as
    /// <c>ceil(targetFps / srcFps) - 1</c> (e.g. 24→135 → 5, 24→55 → 2).
    /// Per RN-07, when <paramref name="srcFps"/> &gt;= <paramref name="targetFps"/>
    /// the native context is a passthrough and
    /// <see cref="ProcessInterpolation"/> yields zero frames.
    /// </remarks>
    IntPtr CreateInterpolation(int srcWidth, int srcHeight, double srcFps, double targetFps, int method);

    /// <summary>
    /// Feeds a consecutive frame pair and produces intermediate frames. Returns the
    /// number of generated frames and writes the native frame array pointer to
    /// <paramref name="framesBuffer"/>.
    /// </summary>
    /// <remarks>
    /// The returned buffer is a native array of texture pointers owned by the
    /// caller (release each texture and free the array). Returns <c>0</c> for a
    /// passthrough context (RN-07: source FPS already meets the target).
    /// </remarks>
    int ProcessInterpolation(IntPtr context, IntPtr frameA, IntPtr frameB, out IntPtr framesBuffer);

    /// <summary>Destroys an interpolation job. No-op when unavailable.</summary>
    void DestroyInterpolation(IntPtr context);

    // --- Upscale (FSR 4 / FSR 1, ST-14) ------------------------------------

    /// <summary>Creates an upscaler; returns an opaque context handle.</summary>
    /// <remarks>
    /// <paramref name="method"/> selects the backend: <c>0</c> = off (passthrough
    /// copy), <c>1</c> = FSR 1 (EASU, always available), <c>2</c> = FSR 4
    /// (FidelityFX SDK, RDNA 4). When <c>2</c> is requested but FSR 4 is
    /// unavailable (no SDK / non-RDNA 4 adapter) the native bridge logs a warning
    /// and downgrades to FSR 1; <see cref="GetUpscaleMode"/> then reports <c>1</c>.
    /// Per RN-07 the caller decides whether to upscale at all (source already
    /// &gt;= target &rarr; request <c>0</c>); the bridge only executes what it is asked.
    /// </remarks>
    IntPtr CreateUpscaler(int srcWidth, int srcHeight, int dstWidth, int dstHeight, int method);

    /// <summary>Upscales one texture and returns the destination texture pointer.</summary>
    /// <remarks>
    /// The returned pointer is an owned native texture reference the caller must
    /// release. For passthrough it is an <c>ID3D11Texture2D*</c>; for FSR 1 / FSR 4
    /// it is an <c>ID3D12Resource*</c> (shared back to D3D11 by the pipeline).
    /// Throws a native-bridge exception on a negative native return code.
    /// </remarks>
    IntPtr ProcessUpscale(IntPtr context, IntPtr srcTexture);

    /// <summary>Destroys an upscaler. No-op when unavailable.</summary>
    void DestroyUpscaler(IntPtr context);

    // --- Encode (AMF H.265 / HEVC, ST-16) ----------------------------------

    /// <summary>Creates an H.265 (HEVC) encoder; returns an opaque context handle.</summary>
    /// <remarks>
    /// Runs on the bridge's shared D3D12 device via AMD Media Framework (AMF).
    /// <paramref name="bitrateKbps"/> is the CBR target bitrate in kbit/s and
    /// <paramref name="fps"/> the frame rate; the HEVC tier is chosen natively
    /// (Main for &lt;= 4K30, High above). Throws a native-bridge exception on a
    /// negative native return code &mdash; including <c>CATRA_ERR_NOT_IMPL</c>
    /// when the bridge was built without the (headers-only, GPUOpen) AMF SDK.
    /// </remarks>
    IntPtr CreateEncoder(int width, int height, int bitrateKbps, double fps);

    /// <summary>Encodes one DX12 texture, writing the packet bytes/size to the out parameters.</summary>
    /// <remarks>
    /// <paramref name="texture"/> is an <c>ID3D12Resource*</c> (NV12, context
    /// width x height). The returned <paramref name="packetBuffer"/> points into
    /// a CONTEXT-OWNED buffer valid until the next <see cref="EncodeFrame"/> /
    /// <see cref="FlushEncoder"/> on the same context (the caller must NOT free
    /// it). A <paramref name="packetSize"/> of <c>0</c> is not an error: the
    /// asynchronous encoder may still be buffering, in which case the caller
    /// keeps feeding frames. Output is an Annex B NAL-unit stream (MP4 muxing is
    /// a later pipeline step). Throws a native-bridge exception on a negative
    /// native return code.
    /// </remarks>
    void EncodeFrame(IntPtr context, IntPtr texture, out IntPtr packetBuffer, out int packetSize);

    /// <summary>Drains buffered packets, writing the packet bytes/size to the out parameters.</summary>
    /// <remarks>
    /// Called once at the end of the stream; concatenates every remaining packet
    /// into the context-owned buffer (same ownership/validity contract as
    /// <see cref="EncodeFrame"/>). Idempotent thereafter. Throws a native-bridge
    /// exception on a negative native return code.
    /// </remarks>
    void FlushEncoder(IntPtr context, out IntPtr packetBuffer, out int packetSize);

    /// <summary>Destroys an encoder. No-op when unavailable.</summary>
    void DestroyEncoder(IntPtr context);

    // --- Resource release helpers (ST-17 ownership fix) --------------------

    /// <summary>
    /// Releases a caller-owned GPU texture previously handed back by the bridge
    /// (an upscale destination from <see cref="ProcessUpscale"/> or an interpolation
    /// intermediate from <see cref="ProcessInterpolation"/>).
    /// </summary>
    /// <remarks>
    /// Works for both <c>ID3D11Texture2D*</c> and <c>ID3D12Resource*</c> (both are
    /// <c>IUnknown</c>). Null-safe (<see cref="IntPtr.Zero"/> is a no-op). Best-effort:
    /// never throws &mdash; a native failure is logged, not surfaced, because this runs
    /// in cleanup paths. Must NOT be called on decoder-owned frames (those are released
    /// by <see cref="IFrameDecoder.ReleaseFrame"/>).
    /// </remarks>
    void ReleaseTexture(IntPtr texture);

    /// <summary>
    /// Frees a caller-owned native frame array previously handed back by
    /// <see cref="ProcessInterpolation"/>.
    /// </summary>
    /// <remarks>
    /// The array is allocated natively with <c>new void*[N]</c> (CRT heap); this is the
    /// matching deallocator. Release each contained texture with <see cref="ReleaseTexture"/>
    /// FIRST, then free the array. Null-safe. Best-effort: never throws. Do NOT free this
    /// buffer with <c>Marshal.FreeHGlobal</c> (different heap &rarr; undefined behaviour).
    /// </remarks>
    void FreeNativeArray(IntPtr ptr);
}
