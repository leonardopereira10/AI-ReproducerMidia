using System.Reflection;
using System.Runtime.InteropServices;
using CATRA.Core.Interfaces;
using CATRA.Services.Processing;
using FluentAssertions;
using Xunit;

namespace CATRA.Services.Tests.Processing;

/// <summary>
/// In-memory <see cref="INativeLibrary"/> fake. Lets the tests drive every native
/// return code and capture outbound calls (init device, destroy handles, log sink)
/// so the <see cref="NativeBridge"/> wrapper logic is verified with no real DLL.
/// </summary>
internal sealed class FakeNativeLibrary : INativeLibrary
{
    public bool IsAvailable { get; set; } = true;

    // Configurable native return codes / out values.
    public int InitResult { get; set; }
    public int UpscaleModeResult { get; set; }
    public int Fsr4Result { get; set; }
    public int InterpMethodResult { get; set; }

    public int InterpCreateResult { get; set; }
    public int InterpCreateContext { get; set; } = 100;
    public int InterpProcessResult { get; set; }
    public int InterpProcessCount { get; set; }
    public IntPtr InterpProcessFrames { get; set; }

    public int UpscaleCreateResult { get; set; }
    public int UpscaleCreateContext { get; set; } = 200;
    public int UpscaleProcessResult { get; set; }
    public IntPtr UpscaleProcessDst { get; set; }

    public int EncodeCreateResult { get; set; }
    public int EncodeCreateContext { get; set; } = 300;
    public int EncodeFrameResult { get; set; }
    public int EncodeFlushResult { get; set; }
    public IntPtr EncodeBuffer { get; set; }
    public int EncodeSize { get; set; }

    // Call capture.
    public int InitCallCount { get; private set; }
    public IntPtr LastInitDevice { get; private set; }
    public int ShutdownCallCount { get; private set; }
    public int InterpDestroyCallCount { get; private set; }
    public int LastInterpDestroyContext { get; private set; }
    public int UpscaleDestroyCallCount { get; private set; }
    public int EncodeDestroyCallCount { get; private set; }
    public int SetLogCallbackCallCount { get; private set; }
    public NativeLogCallback? LastLogCallback { get; private set; }

    public int Init(IntPtr d3d11Device)
    {
        InitCallCount++;
        LastInitDevice = d3d11Device;
        return InitResult;
    }

    public void Shutdown() => ShutdownCallCount++;

    public int GetUpscaleMode() => UpscaleModeResult;

    public int IsFsr4Available() => Fsr4Result;

    public int GetInterpMethod() => InterpMethodResult;

    public void SetLogCallback(NativeLogCallback? callback)
    {
        SetLogCallbackCallCount++;
        LastLogCallback = callback;
    }

    public int InterpCreate(int srcWidth, int srcHeight, double srcFps, double targetFps, int method, out int context)
    {
        context = InterpCreateContext;
        return InterpCreateResult;
    }

    public int InterpProcess(int context, IntPtr frameA, IntPtr frameB, out IntPtr framesBuffer, out int frameCount)
    {
        framesBuffer = InterpProcessFrames;
        frameCount = InterpProcessCount;
        return InterpProcessResult;
    }

    public void InterpDestroy(int context)
    {
        InterpDestroyCallCount++;
        LastInterpDestroyContext = context;
    }

    public int UpscaleCreate(int srcWidth, int srcHeight, int dstWidth, int dstHeight, int method, out int context)
    {
        context = UpscaleCreateContext;
        return UpscaleCreateResult;
    }

    public int UpscaleProcess(int context, IntPtr srcTexture, out IntPtr dstTexture)
    {
        dstTexture = UpscaleProcessDst;
        return UpscaleProcessResult;
    }

    public void UpscaleDestroy(int context) => UpscaleDestroyCallCount++;

    public int EncodeCreate(int width, int height, int bitrateKbps, double fps, out int context)
    {
        context = EncodeCreateContext;
        return EncodeCreateResult;
    }

    public int EncodeFrame(int context, IntPtr texture, out IntPtr packetBuffer, out int packetSize)
    {
        packetBuffer = EncodeBuffer;
        packetSize = EncodeSize;
        return EncodeFrameResult;
    }

    public int EncodeFlush(int context, out IntPtr packetBuffer, out int packetSize)
    {
        packetBuffer = EncodeBuffer;
        packetSize = EncodeSize;
        return EncodeFlushResult;
    }

    public void EncodeDestroy(int context) => EncodeDestroyCallCount++;
}

/// <summary>
/// Wrapper-logic tests for <see cref="NativeBridge"/> (ST-12): graceful
/// degradation when the DLL is absent, native error-code →
/// <see cref="NativeBridgeException"/> mapping, handle marshalling and the log
/// callback. All run against <see cref="FakeNativeLibrary"/> — no native DLL.
/// </summary>
public class NativeBridgeTests
{
    // --- Contract ----------------------------------------------------------

    [Fact]
    public void NativeBridge_ImplementsINativeBridge()
    {
        typeof(NativeBridge).Should().Implement<INativeBridge>();
    }

    // --- Graceful degradation (library unavailable) ------------------------

    [Fact]
    public void Unavailable_IsFsr4Available_ReturnsFalse()
    {
        var bridge = new NativeBridge(new FakeNativeLibrary { IsAvailable = false });
        bridge.IsAvailable.Should().BeFalse();
        bridge.IsFsr4Available().Should().BeFalse();
    }

    [Fact]
    public void Unavailable_GetUpscaleMode_ReturnsZero()
    {
        var bridge = new NativeBridge(new FakeNativeLibrary { IsAvailable = false });
        bridge.GetUpscaleMode().Should().Be(0);
    }

    [Fact]
    public void Unavailable_GetInterpMethod_ReturnsZero()
    {
        var bridge = new NativeBridge(new FakeNativeLibrary { IsAvailable = false });
        bridge.GetInterpMethod().Should().Be(0);
    }

    [Fact]
    public void Unavailable_Initialize_ThrowsNativeBridgeException()
    {
        var bridge = new NativeBridge(new FakeNativeLibrary { IsAvailable = false });
        var act = () => bridge.Initialize(new IntPtr(1));
        act.Should().Throw<NativeBridgeException>()
            .WithMessage("*not available*");
    }

    [Fact]
    public void Unavailable_CreateOperations_ThrowNativeBridgeException()
    {
        var bridge = new NativeBridge(new FakeNativeLibrary { IsAvailable = false });

        var interp = () => bridge.CreateInterpolation(1920, 1080, 24, 60, 1);
        var upscale = () => bridge.CreateUpscaler(1920, 1080, 3840, 2160, 2);
        var encode = () => bridge.CreateEncoder(3840, 2160, 20_000, 60);

        interp.Should().Throw<NativeBridgeException>();
        upscale.Should().Throw<NativeBridgeException>();
        encode.Should().Throw<NativeBridgeException>();
    }

    [Fact]
    public void Unavailable_ShutdownAndDestroy_AreNoOps()
    {
        var lib = new FakeNativeLibrary { IsAvailable = false };
        var bridge = new NativeBridge(lib);

        var act = () =>
        {
            bridge.Shutdown();
            bridge.DestroyInterpolation(new IntPtr(1));
            bridge.DestroyUpscaler(new IntPtr(2));
            bridge.DestroyEncoder(new IntPtr(3));
        };

        act.Should().NotThrow();
        lib.ShutdownCallCount.Should().Be(0);
        lib.InterpDestroyCallCount.Should().Be(0);
        lib.UpscaleDestroyCallCount.Should().Be(0);
        lib.EncodeDestroyCallCount.Should().Be(0);
    }

    // --- Lifecycle (library available) -------------------------------------

    [Fact]
    public void Available_Initialize_Succeeds_AndForwardsDevice()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, InitResult = 0 };
        var bridge = new NativeBridge(lib);
        var device = new IntPtr(0x1234);

        bridge.Initialize(device);

        bridge.IsInitialized.Should().BeTrue();
        lib.InitCallCount.Should().Be(1);
        lib.LastInitDevice.Should().Be(device);
    }

    [Fact]
    public void Available_Initialize_NativeError_ThrowsWithCode()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, InitResult = -1 };
        var bridge = new NativeBridge(lib);

        var act = () => bridge.Initialize(new IntPtr(1));

        act.Should().Throw<NativeBridgeException>().Which.ErrorCode.Should().Be(-1);
        bridge.IsInitialized.Should().BeFalse();
    }

    [Fact]
    public void Available_Shutdown_CallsNativeAndClearsState()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true };
        var bridge = new NativeBridge(lib);
        bridge.Initialize(new IntPtr(1));

        bridge.Shutdown();

        lib.ShutdownCallCount.Should().Be(1);
        bridge.IsInitialized.Should().BeFalse();
    }

    // --- Capability queries (library available) ----------------------------

    [Fact]
    public void Available_IsFsr4Available_NativeNonZero_ReturnsTrue()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, Fsr4Result = 1 };
        new NativeBridge(lib).IsFsr4Available().Should().BeTrue();
    }

    [Fact]
    public void Available_GetUpscaleMode_ReturnsNativeValue()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, UpscaleModeResult = 2 };
        new NativeBridge(lib).GetUpscaleMode().Should().Be(2);
    }

    [Fact]
    public void Available_GetInterpMethod_NativeNotImpl_ClampsToZero()
    {
        // The skeleton native returns CATRA_ERR_NOT_IMPL (-2); the wrapper maps
        // any negative capability result to the safe default (0 = none).
        var lib = new FakeNativeLibrary { IsAvailable = true, InterpMethodResult = -2 };
        new NativeBridge(lib).GetInterpMethod().Should().Be(0);
    }

    // --- Handle marshalling + stub error mapping ---------------------------

    [Fact]
    public void Available_CreateInterpolation_MarshalsHandleToIntPtr()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, InterpCreateResult = 0, InterpCreateContext = 42 };
        var bridge = new NativeBridge(lib);

        bridge.CreateInterpolation(1920, 1080, 24, 60, 1).Should().Be((IntPtr)42);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-5)]
    public void Available_CreateInterpolation_NativeError_ThrowsWithCode(int code)
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, InterpCreateResult = code };
        var bridge = new NativeBridge(lib);

        var act = () => bridge.CreateInterpolation(1, 1, 1, 1, 1);

        act.Should().Throw<NativeBridgeException>().Which.ErrorCode.Should().Be(code);
    }

    [Fact]
    public void Available_CreateUpscaler_NativeError_Throws()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, UpscaleCreateResult = -2 };
        var bridge = new NativeBridge(lib);

        var act = () => bridge.CreateUpscaler(1, 1, 2, 2, 1);

        act.Should().Throw<NativeBridgeException>().Which.ErrorCode.Should().Be(-2);
    }

    [Fact]
    public void Available_CreateEncoder_NativeError_Throws()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, EncodeCreateResult = -2 };
        var bridge = new NativeBridge(lib);

        var act = () => bridge.CreateEncoder(1, 1, 1000, 30);

        act.Should().Throw<NativeBridgeException>().Which.ErrorCode.Should().Be(-2);
    }

    [Fact]
    public void Available_ProcessUpscale_ReturnsDestinationTexture()
    {
        var dst = new IntPtr(0xABCD);
        var lib = new FakeNativeLibrary { IsAvailable = true, UpscaleProcessResult = 0, UpscaleProcessDst = dst };
        var bridge = new NativeBridge(lib);

        bridge.ProcessUpscale(new IntPtr(1), new IntPtr(0x1111)).Should().Be(dst);
    }

    [Fact]
    public void Available_ProcessInterpolation_ReturnsCountAndBuffer()
    {
        var frames = new IntPtr(0x7777);
        var lib = new FakeNativeLibrary
        {
            IsAvailable = true,
            InterpProcessResult = 0,
            InterpProcessCount = 3,
            InterpProcessFrames = frames,
        };
        var bridge = new NativeBridge(lib);

        int count = bridge.ProcessInterpolation(new IntPtr(1), new IntPtr(2), new IntPtr(3), out IntPtr buffer);

        count.Should().Be(3);
        buffer.Should().Be(frames);
    }

    [Fact]
    public void Available_EncodeFrame_ExposesOutParams()
    {
        var buf = new IntPtr(0x9999);
        var lib = new FakeNativeLibrary
        {
            IsAvailable = true,
            EncodeFrameResult = 0,
            EncodeBuffer = buf,
            EncodeSize = 512,
        };
        var bridge = new NativeBridge(lib);

        bridge.EncodeFrame(new IntPtr(1), new IntPtr(2), out IntPtr packet, out int size);

        packet.Should().Be(buf);
        size.Should().Be(512);
    }

    [Fact]
    public void Available_EncodeFrame_NativeError_Throws()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, EncodeFrameResult = -2 };
        var bridge = new NativeBridge(lib);

        var act = () => bridge.EncodeFrame(new IntPtr(1), new IntPtr(2), out _, out _);

        act.Should().Throw<NativeBridgeException>().Which.ErrorCode.Should().Be(-2);
    }

    [Fact]
    public void Available_DestroyInterpolation_ForwardsHandleAsInt()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true };
        var bridge = new NativeBridge(lib);

        bridge.DestroyInterpolation(new IntPtr(77));

        lib.InterpDestroyCallCount.Should().Be(1);
        lib.LastInterpDestroyContext.Should().Be(77);
    }

    // --- Log callback ------------------------------------------------------

    [Fact]
    public void Initialize_InstallsLogSink_AndForwardsMessages()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true };
        var bridge = new NativeBridge(lib);
        var received = new List<(string Message, int Level)>();
        bridge.LogReceived += (_, e) => received.Add((e.Message, e.Level));

        bridge.Initialize(new IntPtr(1));

        lib.LastLogCallback.Should().NotBeNull("the bridge must root a delegate for the native sink");

        // Simulate the native side emitting a diagnostic through the callback.
        lib.LastLogCallback!("hello from native", 1);

        received.Should().HaveCount(1);
        received[0].Message.Should().Be("hello from native");
        received[0].Level.Should().Be(1);
    }

    // --- Disposal ----------------------------------------------------------

    [Fact]
    public void Dispose_ShutsDownAndClearsLogSink()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true };
        var bridge = new NativeBridge(lib);
        bridge.Initialize(new IntPtr(1));

        bridge.Dispose();

        lib.ShutdownCallCount.Should().Be(1);
        lib.LastLogCallback.Should().BeNull("Dispose must detach the native log sink");
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true };
        var bridge = new NativeBridge(lib);
        bridge.Initialize(new IntPtr(1));

        bridge.Dispose();
        bridge.Dispose();

        lib.ShutdownCallCount.Should().Be(1);
    }

    [Fact]
    public void AfterDispose_Initialize_ThrowsObjectDisposed()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true };
        var bridge = new NativeBridge(lib);
        bridge.Dispose();

        var act = () => bridge.Initialize(new IntPtr(1));

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void NativeBridgeException_DefaultErrorCode_IsZero()
    {
        new NativeBridgeException("missing").ErrorCode.Should().Be(0);
    }
}

/// <summary>
/// Verifies the P/Invoke surface by reflection (no native calls): every entry
/// point exists, targets <c>catra-gpu.dll</c>, uses <c>Cdecl</c>, and is private
/// (so it stays invisible to analyzers/consumers).
/// </summary>
public class NativeBridgePInvokeSignatureTests
{
    private const string LibraryName = "catra-gpu.dll";

    public static IEnumerable<object[]> EntryPoints()
    {
        string[] names =
        {
            "catra_init",
            "catra_shutdown",
            "catra_get_upscale_mode",
            "catra_is_fsr4_available",
            "catra_get_interp_method",
            "catra_set_log_callback",
            "catra_interp_create",
            "catra_interp_process",
            "catra_interp_destroy",
            "catra_upscale_create",
            "catra_upscale_process",
            "catra_upscale_destroy",
            "catra_encode_create",
            "catra_encode_frame",
            "catra_encode_flush",
            "catra_encode_destroy",
        };

        return names.Select(name => new object[] { name });
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public void EntryPoint_HasCdeclDllImport_OnCatraGpu(string methodName)
    {
        MethodInfo? method = typeof(NativeLibraryLoader).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull($"the P/Invoke '{methodName}' must exist");

        DllImportAttribute? import = method!.GetCustomAttribute<DllImportAttribute>();
        import.Should().NotBeNull();
        import!.Value.Should().Be(LibraryName);
        import.CallingConvention.Should().Be(CallingConvention.Cdecl);
    }

    [Fact]
    public void AllEntryPoints_ArePrivate_AndComplete()
    {
        List<MethodInfo> imports = typeof(NativeLibraryLoader)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<DllImportAttribute>() is not null)
            .ToList();

        imports.Should().HaveCount(16, "the full catra_gpu.h C ABI must be declared");
        imports.Should().OnlyContain(m => m.IsPrivate);
    }

    [Fact]
    public void LogCallbackDelegate_UsesCdecl()
    {
        UnmanagedFunctionPointerAttribute? attr =
            typeof(NativeLogCallback).GetCustomAttribute<UnmanagedFunctionPointerAttribute>();

        attr.Should().NotBeNull();
        attr!.CallingConvention.Should().Be(CallingConvention.Cdecl);
    }
}

/// <summary>
/// Exercises the real <see cref="NativeLibraryLoader"/> path end-to-end without
/// assuming the DLL is present: it adapts to the environment so it never fails
/// with <see cref="DllNotFoundException"/>.
/// </summary>
public class NativeLibraryLoaderAvailabilityTests
{
    [Fact]
    public void RealBridge_DegradesOrQueries_WithoutThrowingDllNotFound()
    {
        using var bridge = new NativeBridge(); // production loader

        if (!bridge.IsAvailable)
        {
            // DLL absent (typical dev/CI): queries degrade, init throws the
            // graceful NativeBridgeException rather than DllNotFoundException.
            bridge.IsFsr4Available().Should().BeFalse();
            bridge.GetUpscaleMode().Should().Be(0);
            bridge.GetInterpMethod().Should().Be(0);

            var act = () => bridge.Initialize(IntPtr.Zero);
            act.Should().Throw<NativeBridgeException>();
        }
        else
        {
            // DLL present: capability queries must be callable without throwing.
            var act = () =>
            {
                _ = bridge.IsFsr4Available();
                _ = bridge.GetUpscaleMode();
                _ = bridge.GetInterpMethod();
            };

            act.Should().NotThrow();
        }
    }
}
