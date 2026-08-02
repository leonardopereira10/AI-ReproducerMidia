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
    int GetInterpMethod();
    void SetLogCallback(NativeLogCallback? callback);

    int InterpCreate(int srcWidth, int srcHeight, double srcFps, double targetFps, int method, out int context);
    int InterpProcess(int context, IntPtr frameA, IntPtr frameB, out IntPtr framesBuffer, out int frameCount);
    void InterpDestroy(int context);

    int UpscaleCreate(int srcWidth, int srcHeight, int dstWidth, int dstHeight, int method, out int context);
    int UpscaleProcess(int context, IntPtr srcTexture, out IntPtr dstTexture);
    void UpscaleDestroy(int context);

    int EncodeCreate(int width, int height, int bitrateKbps, double fps, out int context);
    int EncodeFrame(int context, IntPtr texture, out IntPtr packetBuffer, out int packetSize);
    int EncodeFlush(int context, out IntPtr packetBuffer, out int packetSize);
    void EncodeDestroy(int context);
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

    public bool IsAvailable => s_available.Value;

    /// <summary>
    /// Probes the same locations the runtime native loader uses: the app base
    /// directory and the RID-specific <c>runtimes/win-x64/native</c> folder that
    /// CATRA.App.csproj populates from the CMake install output.
    /// </summary>
    private static bool DetectAvailability()
    {
        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, LibraryName),
            Path.Combine(baseDir, "runtimes", "win-x64", "native", LibraryName),
        };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        return false;
    }

    public int Init(IntPtr d3d11Device) => catra_init(d3d11Device);

    public void Shutdown() => catra_shutdown();

    public int GetUpscaleMode() => catra_get_upscale_mode();

    public int IsFsr4Available() => catra_is_fsr4_available();

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

    public int EncodeCreate(int width, int height, int bitrateKbps, double fps, out int context)
        => catra_encode_create(width, height, bitrateKbps, fps, out context);

    public int EncodeFrame(int context, IntPtr texture, out IntPtr packetBuffer, out int packetSize)
        => catra_encode_frame(context, texture, out packetBuffer, out packetSize);

    public int EncodeFlush(int context, out IntPtr packetBuffer, out int packetSize)
        => catra_encode_flush(context, out packetBuffer, out packetSize);

    public void EncodeDestroy(int context) => catra_encode_destroy(context);

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
    private static extern int catra_encode_create(int width, int height, int bitrateKbps, double fps, out int context);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_encode_frame(int context, IntPtr texture, out IntPtr packetBuffer, out int packetSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int catra_encode_flush(int context, out IntPtr packetBuffer, out int packetSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void catra_encode_destroy(int context);
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
            InstallLogSink();
            int resultCode = _library.Init(d3d11Device);
            ThrowIfError(resultCode, "catra_init");
            _initialized = true;
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
