using System.Reflection;
using System.Runtime.InteropServices;
using CATRA.Core.Interfaces;

namespace CATRA.Services.Processing;

/// <summary>
/// Delegate matching the native <c>catra_log_callback</c> signature. Marshalled
/// to C so the native bridge can forward diagnostics to managed code. The string
/// is UTF-8 on the native side and is valid only for the duration of the call.
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void NativeLogCallback(
    [MarshalAs(UnmanagedType.LPUTF8Str)] string message,
    int level);

/// <summary>
/// Payload for <see cref="NativeBridge.LogReceived"/> — a single diagnostic line
/// emitted by the native bridge. <see cref="Level"/> mirrors the native
/// <c>CATRA_LOG_*</c> constants (0 = debug, 1 = info, 2 = warn, 3 = error).
/// </summary>
public sealed class NativeLogEventArgs : EventArgs
{
    /// <summary>Creates the event args.</summary>
    public NativeLogEventArgs(string message, int level)
    {
        Message = message;
        Level = level;
    }

    /// <summary>The log line (already UTF-8 decoded).</summary>
    public string Message { get; }

    /// <summary>Native log level: 0 = debug, 1 = info, 2 = warn, 3 = error.</summary>
    public int Level { get; }
}

/// <summary>
/// Thin, testable mirror of the native <c>catra-gpu</c> C ABI. The production
/// implementation (<see cref="NativeLibraryLoader"/>) forwards to P/Invoke; tests
/// substitute a fake so the wrapper logic (error mapping, graceful degradation,
/// handle marshalling) is verified without the real DLL.
/// </summary>
internal interface INativeLibrary
{
    /// <summary>Whether the native DLL was located and is callable.</summary>
    bool IsAvailable { get; }

    int Init(IntPtr d3d11Device);
    void Shutdown();
    int GetUpscaleMode();
    int IsFsr4Available();
    int IsFfxAvailable();
    int GetInterpMethod();
    void SetLogCallback(NativeLogCallback? callback);

    int InterpCreate(int srcWidth, int srcHeight, double srcFps, double targetFps, int method, out int context);
    int InterpProcess(int context, IntPtr frameA, IntPtr frameB, out IntPtr framesBuffer, out int frameCount);
    void InterpDestroy(int context);

    int UpscaleCreate(int srcWidth, int srcHeight, int dstWidth, int dstHeight, int method, out int context);
    int UpscaleProcess(int context, IntPtr srcTexture, out IntPtr dstTexture);
    void UpscaleDestroy(int context);
    int UpscaleSubmitAsync(int context, IntPtr srcTexture);
    int UpscalePollResult(int context, int ticket, out IntPtr dstTexture);
    int UpscalePendingCount(int context);

    int EncodeCreate(int width, int height, int bitrateKbps, double fps, out int context);
    int EncodeFrame(int context, IntPtr texture, out IntPtr packetBuffer, out int packetSize);
    int EncodeFlush(int context, out IntPtr packetBuffer, out int packetSize);
    void EncodeDestroy(int context);

    int ReleaseTexture(IntPtr texture);
    int FreeArray(IntPtr ptr);

    // ST-23: GPU NV12 → BGRA compute shader
    int Nv12BgraInit(IntPtr d3d11Device);
    int Nv12BgraConvert(IntPtr d3d11Device, IntPtr d3d11Ctx,
        IntPtr nv12ArrayTex, uint arraySlice, uint width, uint height,
        out IntPtr outBgraTex);
    void Nv12BgraShutdown();

    // AMD RDNA 4 workaround: NV12 → BGRA via CPU staging copy.
    // Bypasses av_hwframe_transfer_data (produces zeros on this driver).
    // Requires bridge to be initialized (catra_init).
    int Nv12StagingConvert(IntPtr nv12Tex, uint arraySlice,
        uint width, uint height, out IntPtr outBgraTex);

    // Texture readback: D3D12/D3D11 → CPU BGRA bytes (encoder cascade fallback).
    // *out_data is allocated natively (catra_alloc / new[]); caller frees with catra_free.
    int TextureReadbackBgra(IntPtr texture, out IntPtr outData, out int outSize,
        out uint outFormat, out uint outPitch);
}

/// <summary>
/// Production <see cref="INativeLibrary"/>: P/Invoke into <c>catra-gpu.dll</c>
/// (Cdecl). Availability is probed once against the .NET native search layout so
/// callers can degrade instead of hitting a raw <see cref="DllNotFoundException"/>.
/// </summary>
internal sealed class NativeLibraryLoader : INativeLibrary
{
    internal const string LibraryName = "catra-gpu.dll";

    private static readonly Lazy<bool> s_available = new(DetectAvailability);

    // B2 fix: cache the resolved absolute path so NativeLibrary.SetDllImportResolver
    // always loads from runtimes/win-x64/native/ (the correct, fully-built DLL),
    // never from a stale copy in the bin root. The resolver runs once per process.
    private static readonly Lazy<string?> s_resolvedPath = new(ResolveNativeDllPath);

    // B2 fix: register the DLL import resolver once (process-global).
    // NativeLibrary.SetDllImportResolver is idempotent — calling multiple times
    // overwrites, which is harmless (we always register the same resolver).
    private static bool s_resolverRegistered;
    private static readonly object s_resolverLock = new();

    public bool IsAvailable => s_available.Value;

    /// <summary>
    /// Resolves the absolute path to catra-gpu.dll, preferring
    /// <c>runtimes/win-x64/native/</c> (the fully-built DLL with all exports)
    /// over any stale copy in the bin root. Returns null when not found.
    /// </summary>
    private static string? ResolveNativeDllPath()
    {
        string baseDir = AppContext.BaseDirectory;

        // PREFERRED: runtimes/win-x64/native/ — this is where the csproj
        // BuildNativeBridge target copies the fully-built DLL (1.38MB with
        // all exports). A stale DLL in the bin root (189KB, leftover from
        // manual copies or old build artifacts) shadows this and causes
        // EntryPointNotFoundException or missing FFX providers.
        string preferred = Path.Combine(baseDir, "runtimes", "win-x64", "native", LibraryName);
        if (File.Exists(preferred))
        {
            System.Diagnostics.Trace.WriteLine($"[NativeBridge] resolved {LibraryName} -> {preferred}");
            return preferred;
        }

        // FALLBACK: bin root (may be stale, but better than nothing on CI/dev
        // machines where the native build hasn't run).
        string root = Path.Combine(baseDir, LibraryName);
        if (File.Exists(root))
        {
            System.Diagnostics.Trace.WriteLine($"[NativeBridge] resolved {LibraryName} -> {root} (root fallback)");
            return root;
        }

        System.Diagnostics.Trace.WriteLine($"[NativeBridge] {LibraryName} not found in {baseDir}");
        return null;
    }

    /// <summary>
    /// Registers a process-global DLL import resolver that loads catra-gpu.dll
    /// by absolute path from runtimes/win-x64/native/. This prevents a stale
    /// copy in the bin root from being loaded by the default P/Invoke resolver.
    /// </summary>
    internal static void EnsureDllImportResolver()
    {
        if (s_resolverRegistered)
        {
            return;
        }

        lock (s_resolverLock)
        {
            if (s_resolverRegistered)
            {
                return;
            }

            NativeLibrary.SetDllImportResolver(
                typeof(NativeLibraryLoader).Assembly,
                (libraryName, assembly, searchPath) =>
                {
                    if (!string.Equals(libraryName, LibraryName, StringComparison.OrdinalIgnoreCase))
                    {
                        return IntPtr.Zero; // let the default resolver handle it
                    }

                    string? path = s_resolvedPath.Value;
                    if (path == null)
                    {
                        return IntPtr.Zero; // not found, let default resolver fail gracefully
                    }

                    if (NativeLibrary.TryLoad(path, out IntPtr handle))
                    {
                        return handle;
                    }

                    return IntPtr.Zero;
                });

            s_resolverRegistered = true;
        }
    }

    /// <summary>
    /// Probes the same locations the runtime native loader uses: the app base
    /// directory and the RID-specific <c>runtimes/win-x64/native</c> folder that
    /// CATRA.App.csproj populates from the CMake install output.
    /// </summary>
    private static bool DetectAvailability()
    {
        // B2 fix: register the DLL import resolver as early as possible so
        // subsequent P/Invoke calls use the correct DLL.
        EnsureDllImportResolver();
        return s_resolvedPath.Value != null;
    }

    public int Init(IntPtr d3d11Device) => catra_init(d3d11Device);

    public void Shutdown() => catra_shutdown();

    public int GetUpscaleMode() => catra_get_upscale_mode();

    public int IsFsr4Available() => catra_is_fsr4_available();

    public int IsFfxAvailable() => catra_is_ffx_available();

    // N4: static accessor for the process-level cache lambda.
    internal static int StaticIsFfxAvailable() => catra_is_ffx_available();

    public int GetInterpMethod() => catra_get_interp_method();

    public void SetLogCallback(NativeLogCallback? callback) => catra_set_log_callback(callback);

    public int InterpCreate(int srcWidth, int srcHeight, double srcFps, double targetFps, int method, out int context)
        => catra_interp_create(srcWidth, srcHeight, srcFps, targetFps, method, out context);

    public int InterpProcess(int context, IntPtr frameA, IntPtr frameB, out IntPtr framesBuffer, out int frameCount)
        => catra_interp_process(context, frameA, frameB, out framesBuffer, out frameCount);

    public void InterpDestroy(int context) => catra_interp_destroy(context);

    public int UpscaleCreate(int srcWidth, int srcHeight, int dstWidth, int dstHeight, int method, out int context)
        => catra_upscale_create(srcWidth, srcHeight, dstWidth, dstHeight, method, out context);

    public int UpscaleProcess(int context, IntPtr srcTexture, out IntPtr dstTexture)
        => catra_upscale_process(context, srcTexture, out dstTexture);

    public void UpscaleDestroy(int context) => catra_upscale_destroy(context);

    public int UpscaleSubmitAsync(int context, IntPtr srcTexture)
        => catra_upscale_submit_async(context, srcTexture);

    public int UpscalePollResult(int context, int ticket, out IntPtr dstTexture)
        => catra_upscale_poll_result(context, ticket, out dstTexture);

    public int UpscalePendingCount(int context)
        => catra_upscale_pending_count(context);

    public int EncodeCreate(int width, int height, int bitrateKbps, double fps, out int context)
        => catra_encode_create(width, height, bitrateKbps, fps, out context);

    public int EncodeFrame(int context, IntPtr texture, out IntPtr packetBuffer, out int packetSize)
        => catra_encode_frame(context, texture, out packetBuffer, out packetSize);

    public int EncodeFlush(int context, out IntPtr packetBuffer, out int packetSize)
        => catra_encode_flush(context, out packetBuffer, out packetSize);

    public void EncodeDestroy(int context) => catra_encode_destroy(context);

    public int ReleaseTexture(IntPtr texture) => catra_release_texture(texture);

    public int FreeArray(IntPtr ptr) => catra_free(ptr);

    // ST-23: GPU NV12 → BGRA compute shader
    public int Nv12BgraInit(IntPtr d3d11Device) => catra_nv12_bgra_init(d3d11Device);

    public int Nv12BgraConvert(IntPtr d3d11Device, IntPtr d3d11Ctx,
        IntPtr nv12ArrayTex, uint arraySlice, uint width, uint height,
        out IntPtr outBgraTex)
        => catra_nv12_bgra_convert(d3d11Device, d3d11Ctx, nv12ArrayTex, arraySlice, width, height, out outBgraTex);

    public void Nv12BgraShutdown() => catra_nv12_bgra_shutdown();

    // AMD RDNA 4 workaround: NV12 → BGRA via CPU staging copy.
    public int Nv12StagingConvert(IntPtr nv12Tex, uint arraySlice,
        uint width, uint height, out IntPtr outBgraTex)
        => catra_nv12_staging_convert(nv12Tex, arraySlice, width, height, out outBgraTex);

    // Texture readback: D3D12/D3D11 → CPU BGRA bytes.
    public int TextureReadbackBgra(IntPtr texture, out IntPtr outData, out int outSize,
        out uint outFormat, out uint outPitch)
        => catra_texture_readback_bgra(texture, out outData, out outSize, out outFormat, out outPitch);

    // --- P/Invoke surface (private so CA1401 "P/Invokes should not be visible" stays silent) ---

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_init(IntPtr d3d11Device);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void catra_shutdown();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_get_upscale_mode();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_is_fsr4_available();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_is_ffx_available();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_get_interp_method();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void catra_set_log_callback(NativeLogCallback? callback);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_interp_create(int srcWidth, int srcHeight, double srcFps, double targetFps, int method, out int context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_interp_process(int context, IntPtr frameA, IntPtr frameB, out IntPtr framesBuffer, out int frameCount);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void catra_interp_destroy(int context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_upscale_create(int srcWidth, int srcHeight, int dstWidth, int dstHeight, int method, out int context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_upscale_process(int context, IntPtr srcTexture, out IntPtr dstTexture);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void catra_upscale_destroy(int context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_upscale_submit_async(int context, IntPtr srcTexture);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_upscale_poll_result(int context, int ticket, out IntPtr dstTexture);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_upscale_pending_count(int context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_encode_create(int width, int height, int bitrateKbps, double fps, out int context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_encode_frame(int context, IntPtr texture, out IntPtr packetBuffer, out int packetSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_encode_flush(int context, out IntPtr packetBuffer, out int packetSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void catra_encode_destroy(int context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_release_texture(IntPtr texture);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_free(IntPtr ptr);

    // ST-23: GPU NV12 → BGRA compute shader
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_nv12_bgra_init(IntPtr d3d11Device);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_nv12_bgra_convert(IntPtr d3d11Device, IntPtr d3d11Ctx,
        IntPtr nv12ArrayTex, uint arraySlice, uint width, uint height, out IntPtr outBgraTex);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void catra_nv12_bgra_shutdown();

    // AMD RDNA 4 workaround: NV12 → BGRA via CPU staging copy.
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_nv12_staging_convert(IntPtr nv12Tex,
        uint arraySlice, uint width, uint height, out IntPtr outBgraTex);

    // Texture readback: D3D12/D3D11 → CPU BGRA bytes (encoder cascade fallback).
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_texture_readback_bgra(IntPtr texture,
        out IntPtr outData, out int outSize, out uint outFormat, out uint outPitch);
}

/// <summary>
/// Default <see cref="INativeBridge"/> implementation (ST-12). Wraps the
/// <c>catra-gpu</c> native DLL behind a testable <see cref="INativeLibrary"/>,
/// maps negative <c>CATRA_ERR_*</c> codes to <see cref="NativeBridgeException"/>,
/// and degrades gracefully when the DLL is absent.
/// </summary>
public sealed class NativeBridge : INativeBridge
{
    private readonly INativeLibrary _library;
    private readonly object _gate = new();

    // Rooted for the lifetime of the bridge: the native side stores this pointer,
    // so the delegate must not be garbage-collected while in use.
    private NativeLogCallback? _logSink;
    private bool _initialized;
    // The device the native bridge currently runs on. Each FrameDecoder creates
    // its own D3D11VA device, so consecutive episodes bring DIFFERENT devices;
    // the bridge must re-init when the device changes, otherwise its GPU stages
    // (interp / upscale / encode) run on the previous episode's device while the
    // new episode's textures live on the new one — a cross-device use that
    // crashes the GPU.
    private IntPtr _initializedDevice;
    private bool _disposed;

    /// <summary>Creates the bridge backed by the real native loader.</summary>
    public NativeBridge()
        : this(new NativeLibraryLoader())
    {
    }

    /// <summary>Creates the bridge over an explicit library (used by tests).</summary>
    internal NativeBridge(INativeLibrary library)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
    }

    /// <inheritdoc />
    public bool IsAvailable => _library.IsAvailable;

    /// <inheritdoc />
    public bool IsInitialized
    {
        get
        {
            lock (_gate)
            {
                return _initialized;
            }
        }
    }

    /// <summary>Raised for each diagnostic line emitted by the native bridge.</summary>
    public event EventHandler<NativeLogEventArgs>? LogReceived;

    /// <inheritdoc />
    public void Initialize(IntPtr d3d11Device)
    {
        EnsureNotDisposed();
        EnsureAvailable();

        lock (_gate)
        {
            if (_initialized && _initializedDevice == d3d11Device)
            {
                return; // already bound to this exact device
            }

            if (_initialized)
            {
                // Different device (the next episode's fresh D3D11VA decoder
                // device): tear down and re-init so every GPU stage runs on the
                // same device the incoming textures were created on. Native
                // catra_shutdown destroys all backend contexts, the NV12→BGRA
                // cache and the D3D11<->DX12 interop, so no context may be live
                // at this point — true between episodes (the pipeline destroys
                // every context in its per-episode finally).
                _library.Shutdown();
                _initialized = false;
                _initializedDevice = IntPtr.Zero;
            }

            InstallLogSink();
            int resultCode = _library.Init(d3d11Device);
            ThrowIfError(resultCode, "catra_init");
            _initialized = true;
            _initializedDevice = d3d11Device;
        }
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        lock (_gate)
        {
            if (_initialized && _library.IsAvailable)
            {
                _library.Shutdown();
            }

            _initialized = false;
            _initializedDevice = IntPtr.Zero;
        }
    }

    /// <inheritdoc />
    public bool IsFsr4Available()
    {
        if (!_library.IsAvailable)
        {
            return false;
        }

        return _library.IsFsr4Available() > 0;
    }

    /// <inheritdoc />
    public bool IsFfxAvailable()
    {
        if (!_library.IsAvailable)
        {
            return false;
        }

        // N4 fix: when using the real NativeLibraryLoader (production), cache
        // the probe result at process level. The FFX probe creates a transient
        // D3D12 device + calls ffxQuery (measured 82-164ms on RDNA4). Caching
        // ensures repeated Settings navigation doesn't block the UI thread.
        //
        // Tests use FakeNativeLibrary (not NativeLibraryLoader), so the cache
        // is bypassed — each test controls the fake's return value directly.
        if (_library is NativeLibraryLoader)
        {
            return s_ffxProbeCache.Value;
        }

        return _library.IsFfxAvailable() > 0;
    }

    // N4: process-level cache for the FFX availability probe. Only populated
    // when NativeBridge wraps the real NativeLibraryLoader (production path).
    // Static Lazy ensures the probe runs at most once per process lifetime.
    private static Lazy<bool> s_ffxProbeCache = new(ProbeFfxAvailableNative);

    private static bool ProbeFfxAvailableNative()
    {
        try
        {
            return NativeLibraryLoader.StaticIsFfxAvailable() > 0;
        }
        catch (EntryPointNotFoundException)
        {
            // N3: stale DLL or old build without catra_is_ffx_available export.
            System.Diagnostics.Trace.WriteLine(
                "[NativeBridge] catra_is_ffx_available entry point not found (stale DLL?)");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[NativeBridge] FFX probe failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Resets the process-level FFX probe cache. Call between tests that
    /// exercise the real <see cref="NativeLibraryLoader"/> path with different
    /// expected outcomes (e.g. after deploying a correct DLL to replace a stale one).
    /// No-op for tests using <c>FakeNativeLibrary</c> (they bypass the cache).
    /// </summary>
    internal static void ResetFfxProbeCache()
    {
        s_ffxProbeCache = new Lazy<bool>(ProbeFfxAvailableNative);
    }

    /// <inheritdoc />
    public int GetUpscaleMode()
    {
        if (!_library.IsAvailable)
        {
            return 0;
        }

        int resultCode = _library.GetUpscaleMode();
        return resultCode < 0 ? 0 : resultCode;
    }

    /// <inheritdoc />
    public int GetInterpMethod()
    {
        if (!_library.IsAvailable)
        {
            return 0;
        }

        int resultCode = _library.GetInterpMethod();
        return resultCode < 0 ? 0 : resultCode;
    }

    /// <inheritdoc />
    public IntPtr CreateInterpolation(int srcWidth, int srcHeight, double srcFps, double targetFps, int method)
    {
        EnsureAvailable();
        int resultCode = _library.InterpCreate(srcWidth, srcHeight, srcFps, targetFps, method, out int context);
        ThrowIfError(resultCode, "catra_interp_create");
        return (IntPtr)context;
    }

    /// <inheritdoc />
    public int ProcessInterpolation(IntPtr context, IntPtr frameA, IntPtr frameB, out IntPtr framesBuffer)
    {
        EnsureAvailable();
        int resultCode = _library.InterpProcess(ToInt32(context), frameA, frameB, out IntPtr frames, out int count);
        ThrowIfError(resultCode, "catra_interp_process");
        framesBuffer = frames;
        return count;
    }

    /// <inheritdoc />
    public void DestroyInterpolation(IntPtr context)
    {
        if (!_library.IsAvailable)
        {
            return;
        }

        _library.InterpDestroy(ToInt32(context));
    }

    /// <inheritdoc />
    public IntPtr CreateUpscaler(int srcWidth, int srcHeight, int dstWidth, int dstHeight, int method)
    {
        EnsureAvailable();
        int resultCode = _library.UpscaleCreate(srcWidth, srcHeight, dstWidth, dstHeight, method, out int context);
        ThrowIfError(resultCode, "catra_upscale_create");
        return (IntPtr)context;
    }

    /// <inheritdoc />
    public IntPtr ProcessUpscale(IntPtr context, IntPtr srcTexture)
    {
        EnsureAvailable();
        int resultCode = _library.UpscaleProcess(ToInt32(context), srcTexture, out IntPtr dstTexture);
        ThrowIfError(resultCode, "catra_upscale_process");
        return dstTexture;
    }

    /// <inheritdoc />
    public void DestroyUpscaler(IntPtr context)
    {
        if (!_library.IsAvailable)
        {
            return;
        }

        _library.UpscaleDestroy(ToInt32(context));
    }

    /// <inheritdoc />
    public int SubmitUpscaleAsync(IntPtr context, IntPtr srcTexture)
    {
        EnsureAvailable();
        int ticket = _library.UpscaleSubmitAsync(ToInt32(context), srcTexture);
        if (ticket < 0)
        {
            ThrowIfError(ticket, "catra_upscale_submit_async");
        }
        return ticket;
    }

    /// <inheritdoc />
    public IntPtr PollUpscaleResult(IntPtr context, int ticket)
    {
        EnsureAvailable();
        int resultCode = _library.UpscalePollResult(ToInt32(context), ticket, out IntPtr dstTexture);
        if (resultCode == 0) // CATRA_OK
        {
            return dstTexture;
        }
        if (resultCode == -6) // CATRA_ERR_UNKNOWN = still in flight OR ticket not found
        {
            return IntPtr.Zero;
        }
        // Any other error is fatal
        ThrowIfError(resultCode, "catra_upscale_poll_result");
        return IntPtr.Zero; // unreachable
    }

    /// <inheritdoc />
    public int GetUpscalePendingCount(IntPtr context)
    {
        if (!_library.IsAvailable)
        {
            return 0;
        }
        return _library.UpscalePendingCount(ToInt32(context));
    }

    /// <inheritdoc />
    public IntPtr CreateEncoder(int width, int height, int bitrateKbps, double fps)
    {
        EnsureAvailable();
        int resultCode = _library.EncodeCreate(width, height, bitrateKbps, fps, out int context);
        ThrowIfError(resultCode, "catra_encode_create");
        return (IntPtr)context;
    }

    /// <inheritdoc />
    public void EncodeFrame(IntPtr context, IntPtr texture, out IntPtr packetBuffer, out int packetSize)
    {
        EnsureAvailable();
        int resultCode = _library.EncodeFrame(ToInt32(context), texture, out IntPtr buffer, out int size);
        ThrowIfError(resultCode, "catra_encode_frame");
        packetBuffer = buffer;
        packetSize = size;
    }

    /// <inheritdoc />
    public void FlushEncoder(IntPtr context, out IntPtr packetBuffer, out int packetSize)
    {
        EnsureAvailable();
        int resultCode = _library.EncodeFlush(ToInt32(context), out IntPtr buffer, out int size);
        ThrowIfError(resultCode, "catra_encode_flush");
        packetBuffer = buffer;
        packetSize = size;
    }

    /// <inheritdoc />
    public void DestroyEncoder(IntPtr context)
    {
        if (!_library.IsAvailable)
        {
            return;
        }

        _library.EncodeDestroy(ToInt32(context));
    }

    /// <inheritdoc />
    public void ReleaseTexture(IntPtr texture)
    {
        // Best-effort cleanup: never throw. Null / unavailable / disposed are no-ops;
        // a negative native code is logged (error level) rather than surfaced, because
        // this runs inside per-frame finally blocks where an exception would mask the
        // original outcome and could leak the remaining resources.
        if (_disposed || !_library.IsAvailable || texture == IntPtr.Zero)
        {
            return;
        }

        int resultCode = _library.ReleaseTexture(texture);
        if (resultCode < 0)
        {
            OnNativeLog($"catra_release_texture failed with error code {resultCode}.", level: 3);
        }
    }

    /// <inheritdoc />
    public void ReadbackTextureToCpu(IntPtr texture, out byte[] pixels,
        out uint dxgiFormat, out uint rowPitch)
    {
        EnsureAvailable();
        int resultCode = _library.TextureReadbackBgra(texture,
            out IntPtr nativeData, out int size, out dxgiFormat, out rowPitch);
        ThrowIfError(resultCode, "catra_texture_readback_bgra");

        pixels = new byte[size];
        if (size > 0 && nativeData != IntPtr.Zero)
        {
            Marshal.Copy(nativeData, pixels, 0, size);
        }

        // Free the native buffer via the bridge's matched deallocator.
        if (nativeData != IntPtr.Zero)
        {
            _library.FreeArray(nativeData);
        }
    }

    /// <inheritdoc />
    public void FreeNativeArray(IntPtr ptr)
    {
        // Best-effort cleanup (see ReleaseTexture): the array is native `new[]`, so it
        // must be freed through catra_free — never Marshal.FreeHGlobal (cross-heap UB).
        if (_disposed || !_library.IsAvailable || ptr == IntPtr.Zero)
        {
            return;
        }

        int resultCode = _library.FreeArray(ptr);
        if (resultCode < 0)
        {
            OnNativeLog($"catra_free failed with error code {resultCode}.", level: 3);
        }
    }

    /// <summary>Releases native resources and detaches the log sink. Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_library.IsAvailable)
            {
                if (_initialized)
                {
                    _library.Shutdown();
                }

                // Stop native code from calling into a disposing object.
                _library.SetLogCallback(null);
            }

            _logSink = null;
            _initialized = false;
            _initializedDevice = IntPtr.Zero;
            _disposed = true;
        }
    }

    private void InstallLogSink()
    {
        _logSink = OnNativeLog;
        _library.SetLogCallback(_logSink);
    }

    private void OnNativeLog(string message, int level)
    {
        System.Diagnostics.Trace.WriteLine($"[catra-gpu] {message}");
        Console.Error.WriteLine($"[catra-gpu] {message}");
        LogReceived?.Invoke(this, new NativeLogEventArgs(message, level));
    }

    private void EnsureAvailable()
    {
        EnsureNotDisposed();

        if (!_library.IsAvailable)
        {
            throw new NativeBridgeException(
                "The catra-gpu native library is not available. Build it with scripts/build-native.ps1 " +
                "and ensure catra-gpu.dll is deployed next to the application or under runtimes/win-x64/native/.");
        }
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(NativeBridge));
        }
    }

    private static void ThrowIfError(int resultCode, string operation)
    {
        if (resultCode < 0)
        {
            throw new NativeBridgeException(
                $"Native call '{operation}' failed with error code {resultCode}.",
                resultCode);
        }
    }

    private static int ToInt32(IntPtr context) => context.ToInt32();
}
