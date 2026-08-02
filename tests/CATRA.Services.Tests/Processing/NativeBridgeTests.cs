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
    public int InterpCreateCallCount { get; private set; }
    public int LastInterpCreateSrcWidth { get; private set; }
    public int LastInterpCreateSrcHeight { get; private set; }
    public double LastInterpCreateSrcFps { get; private set; }
    public double LastInterpCreateTargetFps { get; private set; }
    public int LastInterpCreateMethod { get; private set; }
    public int InterpProcessCallCount { get; private set; }
    public int LastInterpProcessContext { get; private set; }
    public IntPtr LastInterpProcessFrameA { get; private set; }
    public IntPtr LastInterpProcessFrameB { get; private set; }
    public int InterpDestroyCallCount { get; private set; }
    public int LastInterpDestroyContext { get; private set; }
    public int UpscaleDestroyCallCount { get; private set; }
    public int LastUpscaleDestroyContext { get; private set; }
    public int UpscaleCreateCallCount { get; private set; }
    public int LastUpscaleCreateSrcWidth { get; private set; }
    public int LastUpscaleCreateSrcHeight { get; private set; }
    public int LastUpscaleCreateDstWidth { get; private set; }
    public int LastUpscaleCreateDstHeight { get; private set; }
    public int LastUpscaleCreateMethod { get; private set; }
    public int UpscaleProcessCallCount { get; private set; }
    public int LastUpscaleProcessContext { get; private set; }
    public IntPtr LastUpscaleProcessSrc { get; private set; }
    public int EncodeCreateCallCount { get; private set; }
    public int LastEncodeCreateWidth { get; private set; }
    public int LastEncodeCreateHeight { get; private set; }
    public int LastEncodeCreateBitrateKbps { get; private set; }
    public double LastEncodeCreateFps { get; private set; }
    public int EncodeFrameCallCount { get; private set; }
    public int LastEncodeFrameContext { get; private set; }
    public IntPtr LastEncodeFrameTexture { get; private set; }
    public int EncodeFlushCallCount { get; private set; }
    public int LastEncodeFlushContext { get; private set; }
    public int EncodeDestroyCallCount { get; private set; }
    public int LastEncodeDestroyContext { get; private set; }
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
        InterpCreateCallCount++;
        LastInterpCreateSrcWidth = srcWidth;
        LastInterpCreateSrcHeight = srcHeight;
        LastInterpCreateSrcFps = srcFps;
        LastInterpCreateTargetFps = targetFps;
        LastInterpCreateMethod = method;
        context = InterpCreateContext;
        return InterpCreateResult;
    }

    public int InterpProcess(int context, IntPtr frameA, IntPtr frameB, out IntPtr framesBuffer, out int frameCount)
    {
        InterpProcessCallCount++;
        LastInterpProcessContext = context;
        LastInterpProcessFrameA = frameA;
        LastInterpProcessFrameB = frameB;
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
        UpscaleCreateCallCount++;
        LastUpscaleCreateSrcWidth = srcWidth;
        LastUpscaleCreateSrcHeight = srcHeight;
        LastUpscaleCreateDstWidth = dstWidth;
        LastUpscaleCreateDstHeight = dstHeight;
        LastUpscaleCreateMethod = method;
        context = UpscaleCreateContext;
        return UpscaleCreateResult;
    }

    public int UpscaleProcess(int context, IntPtr srcTexture, out IntPtr dstTexture)
    {
        UpscaleProcessCallCount++;
        LastUpscaleProcessContext = context;
        LastUpscaleProcessSrc = srcTexture;
        dstTexture = UpscaleProcessDst;
        return UpscaleProcessResult;
    }

    public void UpscaleDestroy(int context)
    {
        UpscaleDestroyCallCount++;
        LastUpscaleDestroyContext = context;
    }

    public int EncodeCreate(int width, int height, int bitrateKbps, double fps, out int context)
    {
        EncodeCreateCallCount++;
        LastEncodeCreateWidth = width;
        LastEncodeCreateHeight = height;
        LastEncodeCreateBitrateKbps = bitrateKbps;
        LastEncodeCreateFps = fps;
        context = EncodeCreateContext;
        return EncodeCreateResult;
    }

    public int EncodeFrame(int context, IntPtr texture, out IntPtr packetBuffer, out int packetSize)
    {
        EncodeFrameCallCount++;
        LastEncodeFrameContext = context;
        LastEncodeFrameTexture = texture;
        packetBuffer = EncodeBuffer;
        packetSize = EncodeSize;
        return EncodeFrameResult;
    }

    public int EncodeFlush(int context, out IntPtr packetBuffer, out int packetSize)
    {
        EncodeFlushCallCount++;
        LastEncodeFlushContext = context;
        packetBuffer = EncodeBuffer;
        packetSize = EncodeSize;
        return EncodeFlushResult;
    }

    public void EncodeDestroy(int context)
    {
        EncodeDestroyCallCount++;
        LastEncodeDestroyContext = context;
    }
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
/// ST-13 interpolation coverage for <see cref="NativeBridge"/>: argument
/// forwarding to the native ABI, native error-code mapping, the RN-07
/// passthrough (source FPS >= target FPS yields zero frames) and disposal
/// guard rails. All driven through <see cref="FakeNativeLibrary"/> — no DLL.
/// </summary>
public class NativeBridgeInterpTests
{
    [Fact]
    public void CreateInterpolation_ForwardsAllArgumentsToNative()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, InterpCreateResult = 0, InterpCreateContext = 7 };
        var bridge = new NativeBridge(lib);

        IntPtr handle = bridge.CreateInterpolation(1920, 1080, 24.0, 135.0, 1);

        handle.Should().Be((IntPtr)7);
        lib.InterpCreateCallCount.Should().Be(1);
        lib.LastInterpCreateSrcWidth.Should().Be(1920);
        lib.LastInterpCreateSrcHeight.Should().Be(1080);
        lib.LastInterpCreateSrcFps.Should().Be(24.0);
        lib.LastInterpCreateTargetFps.Should().Be(135.0);
        lib.LastInterpCreateMethod.Should().Be(1);
    }

    [Fact]
    public void ProcessInterpolation_ForwardsContextAndFramePointers()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, InterpProcessResult = 0, InterpProcessCount = 5 };
        var bridge = new NativeBridge(lib);
        var frameA = new IntPtr(0xA1);
        var frameB = new IntPtr(0xB2);

        int count = bridge.ProcessInterpolation(new IntPtr(7), frameA, frameB, out _);

        count.Should().Be(5);
        lib.InterpProcessCallCount.Should().Be(1);
        lib.LastInterpProcessContext.Should().Be(7);
        lib.LastInterpProcessFrameA.Should().Be(frameA);
        lib.LastInterpProcessFrameB.Should().Be(frameB);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(-4)]
    [InlineData(-5)]
    public void ProcessInterpolation_NativeError_ThrowsWithCode(int code)
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, InterpProcessResult = code };
        var bridge = new NativeBridge(lib);

        var act = () => bridge.ProcessInterpolation(new IntPtr(1), new IntPtr(2), new IntPtr(3), out _);

        act.Should().Throw<NativeBridgeException>().Which.ErrorCode.Should().Be(code);
    }

    [Fact]
    public void ProcessInterpolation_Rn07Passthrough_ReturnsZeroFrames()
    {
        // Source FPS >= target FPS: the native context emits no intermediate
        // frames (count 0) and the wrapper surfaces that as a clean zero.
        var lib = new FakeNativeLibrary
        {
            IsAvailable = true,
            InterpProcessResult = 0,
            InterpProcessCount = 0,
            InterpProcessFrames = IntPtr.Zero,
        };
        var bridge = new NativeBridge(lib);

        int count = bridge.ProcessInterpolation(new IntPtr(1), new IntPtr(2), new IntPtr(3), out IntPtr buffer);

        count.Should().Be(0);
        buffer.Should().Be(IntPtr.Zero);
    }

    [Fact]
    public void Unavailable_ProcessInterpolation_ThrowsNativeBridgeException()
    {
        var bridge = new NativeBridge(new FakeNativeLibrary { IsAvailable = false });

        var act = () => bridge.ProcessInterpolation(new IntPtr(1), new IntPtr(2), new IntPtr(3), out _);

        act.Should().Throw<NativeBridgeException>().WithMessage("*not available*");
    }

    [Fact]
    public void AfterDispose_CreateInterpolation_ThrowsObjectDisposed()
    {
        var bridge = new NativeBridge(new FakeNativeLibrary { IsAvailable = true });
        bridge.Dispose();

        var act = () => bridge.CreateInterpolation(1920, 1080, 24, 135, 1);

        act.Should().Throw<ObjectDisposedException>();
    }
}

/// <summary>
/// ST-14 upscale coverage for <see cref="NativeBridge"/>: argument forwarding,
/// native error-code mapping, the FSR 4 -> FSR 1 downgrade (transparent to the
/// wrapper — the native bridge performs it and still returns CATRA_OK), the
/// method=0 passthrough and disposal guard rails. All driven through
/// <see cref="FakeNativeLibrary"/> — no DLL.
/// </summary>
public class NativeBridgeUpscaleTests
{
    [Fact]
    public void CreateUpscaler_ForwardsAllArgumentsToNative()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, UpscaleCreateResult = 0, UpscaleCreateContext = 11 };
        var bridge = new NativeBridge(lib);

        IntPtr handle = bridge.CreateUpscaler(1920, 1080, 3840, 2160, 2);

        handle.Should().Be((IntPtr)11);
        lib.UpscaleCreateCallCount.Should().Be(1);
        lib.LastUpscaleCreateSrcWidth.Should().Be(1920);
        lib.LastUpscaleCreateSrcHeight.Should().Be(1080);
        lib.LastUpscaleCreateDstWidth.Should().Be(3840);
        lib.LastUpscaleCreateDstHeight.Should().Be(2160);
        lib.LastUpscaleCreateMethod.Should().Be(2);
    }

    [Fact]
    public void CreateUpscaler_Fsr4Request_ReturnsHandle_AndDoesNotReject()
    {
        // The FSR 4 -> FSR 1 downgrade happens NATIVELY (the bridge logs + returns
        // CATRA_OK with a FSR 1 context). The wrapper must forward method=2 as-is
        // and surface the handle; it never second-guesses the native decision.
        var lib = new FakeNativeLibrary { IsAvailable = true, UpscaleCreateResult = 0, UpscaleCreateContext = 22 };
        var bridge = new NativeBridge(lib);

        bridge.CreateUpscaler(1280, 720, 3840, 2160, method: 2).Should().Be((IntPtr)22);
        lib.LastUpscaleCreateMethod.Should().Be(2, "the wrapper forwards the requested method unchanged");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CreateUpscaler_PassthroughAndFsr1_ForwardMethod(int method)
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, UpscaleCreateResult = 0 };
        var bridge = new NativeBridge(lib);

        bridge.CreateUpscaler(1280, 720, 1920, 1080, method);

        lib.LastUpscaleCreateMethod.Should().Be(method);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-4)]
    [InlineData(-5)]
    public void CreateUpscaler_NativeError_ThrowsWithCode(int code)
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, UpscaleCreateResult = code };
        var bridge = new NativeBridge(lib);

        var act = () => bridge.CreateUpscaler(1920, 1080, 3840, 2160, 2);

        act.Should().Throw<NativeBridgeException>().Which.ErrorCode.Should().Be(code);
    }

    [Fact]
    public void ProcessUpscale_ForwardsContextAndSrcPointer()
    {
        var dst = new IntPtr(0xD5D5);
        var src = new IntPtr(0x5E5E);
        var lib = new FakeNativeLibrary { IsAvailable = true, UpscaleProcessResult = 0, UpscaleProcessDst = dst };
        var bridge = new NativeBridge(lib);

        IntPtr result = bridge.ProcessUpscale(new IntPtr(9), src);

        result.Should().Be(dst);
        lib.UpscaleProcessCallCount.Should().Be(1);
        lib.LastUpscaleProcessContext.Should().Be(9);
        lib.LastUpscaleProcessSrc.Should().Be(src);
    }

    [Fact]
    public void ProcessUpscale_PassthroughContext_ReturnsCopiedTexture()
    {
        // method=0 context: the native bridge returns a copied ID3D11Texture2D*.
        // The wrapper simply surfaces whatever pointer the native side hands back.
        var copied = new IntPtr(0xC0DE);
        var lib = new FakeNativeLibrary { IsAvailable = true, UpscaleProcessResult = 0, UpscaleProcessDst = copied };
        var bridge = new NativeBridge(lib);

        bridge.ProcessUpscale(new IntPtr(1), new IntPtr(2)).Should().Be(copied);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(-4)]
    [InlineData(-6)]
    public void ProcessUpscale_NativeError_ThrowsWithCode(int code)
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, UpscaleProcessResult = code };
        var bridge = new NativeBridge(lib);

        var act = () => bridge.ProcessUpscale(new IntPtr(1), new IntPtr(2));

        act.Should().Throw<NativeBridgeException>().Which.ErrorCode.Should().Be(code);
    }

    [Fact]
    public void DestroyUpscaler_ForwardsHandleAsInt()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true };
        var bridge = new NativeBridge(lib);

        bridge.DestroyUpscaler(new IntPtr(88));

        lib.UpscaleDestroyCallCount.Should().Be(1);
        lib.LastUpscaleDestroyContext.Should().Be(88);
    }

    [Fact]
    public void Unavailable_ProcessUpscale_ThrowsNativeBridgeException()
    {
        var bridge = new NativeBridge(new FakeNativeLibrary { IsAvailable = false });

        var act = () => bridge.ProcessUpscale(new IntPtr(1), new IntPtr(2));

        act.Should().Throw<NativeBridgeException>().WithMessage("*not available*");
    }

    [Fact]
    public void Unavailable_DestroyUpscaler_IsNoOp()
    {
        var lib = new FakeNativeLibrary { IsAvailable = false };
        var bridge = new NativeBridge(lib);

        var act = () => bridge.DestroyUpscaler(new IntPtr(3));

        act.Should().NotThrow();
        lib.UpscaleDestroyCallCount.Should().Be(0);
    }

    [Fact]
    public void AfterDispose_CreateUpscaler_ThrowsObjectDisposed()
    {
        var bridge = new NativeBridge(new FakeNativeLibrary { IsAvailable = true });
        bridge.Dispose();

        var act = () => bridge.CreateUpscaler(1920, 1080, 3840, 2160, 2);

        act.Should().Throw<ObjectDisposedException>();
    }
}

/// <summary>
/// ST-16 AMF encode coverage for <see cref="NativeBridge"/>: argument forwarding,
/// native error-code mapping, the async-encoder buffering contract (a 0-byte
/// packet is NOT an error — the caller keeps feeding frames), flush draining the
/// final NALs, destroy forwarding and disposal guard rails. All driven through
/// <see cref="FakeNativeLibrary"/> — no DLL.
/// </summary>
public class NativeBridgeEncodeTests
{
    [Fact]
    public void CreateEncoder_ForwardsAllArgumentsToNative()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, EncodeCreateResult = 0, EncodeCreateContext = 31 };
        var bridge = new NativeBridge(lib);

        IntPtr handle = bridge.CreateEncoder(3840, 2160, 20_000, 60.0);

        handle.Should().Be((IntPtr)31);
        lib.EncodeCreateCallCount.Should().Be(1);
        lib.LastEncodeCreateWidth.Should().Be(3840);
        lib.LastEncodeCreateHeight.Should().Be(2160);
        lib.LastEncodeCreateBitrateKbps.Should().Be(20_000);
        lib.LastEncodeCreateFps.Should().Be(60.0);
    }

    [Theory]
    [InlineData(-2)] // CATRA_ERR_NOT_IMPL (AMF SDK absent)
    [InlineData(-4)]
    [InlineData(-5)]
    public void CreateEncoder_NativeError_ThrowsWithCode(int code)
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, EncodeCreateResult = code };
        var bridge = new NativeBridge(lib);

        var act = () => bridge.CreateEncoder(3840, 2160, 20_000, 60);

        act.Should().Throw<NativeBridgeException>().Which.ErrorCode.Should().Be(code);
    }

    [Fact]
    public void EncodeFrame_ForwardsContextAndTexture()
    {
        var buf = new IntPtr(0xEE01);
        var texture = new IntPtr(0x7E57);
        var lib = new FakeNativeLibrary
        {
            IsAvailable = true,
            EncodeFrameResult = 0,
            EncodeBuffer = buf,
            EncodeSize = 4096,
        };
        var bridge = new NativeBridge(lib);

        bridge.EncodeFrame(new IntPtr(31), texture, out IntPtr packet, out int size);

        packet.Should().Be(buf);
        size.Should().Be(4096);
        lib.EncodeFrameCallCount.Should().Be(1);
        lib.LastEncodeFrameContext.Should().Be(31);
        lib.LastEncodeFrameTexture.Should().Be(texture);
    }

    [Fact]
    public void EncodeFrame_ZeroBytes_IsBuffering_NotAnError()
    {
        // The async AMF encoder may still be buffering: packetSize == 0 with a
        // CATRA_OK return must surface cleanly (caller keeps feeding frames),
        // never as an exception.
        var lib = new FakeNativeLibrary
        {
            IsAvailable = true,
            EncodeFrameResult = 0,
            EncodeBuffer = IntPtr.Zero,
            EncodeSize = 0,
        };
        var bridge = new NativeBridge(lib);

        IntPtr packet = IntPtr.Zero;
        int size = -1;
        var act = () => bridge.EncodeFrame(new IntPtr(31), new IntPtr(1), out packet, out size);

        act.Should().NotThrow();
        packet.Should().Be(IntPtr.Zero);
        size.Should().Be(0);
    }

    [Theory]
    [InlineData(-3)]
    [InlineData(-4)]
    [InlineData(-6)]
    public void EncodeFrame_NativeError_ThrowsWithCode(int code)
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, EncodeFrameResult = code };
        var bridge = new NativeBridge(lib);

        var act = () => bridge.EncodeFrame(new IntPtr(31), new IntPtr(1), out _, out _);

        act.Should().Throw<NativeBridgeException>().Which.ErrorCode.Should().Be(code);
    }

    [Fact]
    public void FlushEncoder_ReturnsFinalNals_AndForwardsContext()
    {
        var buf = new IntPtr(0xF105);
        var lib = new FakeNativeLibrary
        {
            IsAvailable = true,
            EncodeFlushResult = 0,
            EncodeBuffer = buf,
            EncodeSize = 8192,
        };
        var bridge = new NativeBridge(lib);

        bridge.FlushEncoder(new IntPtr(31), out IntPtr packet, out int size);

        packet.Should().Be(buf);
        size.Should().Be(8192);
        lib.EncodeFlushCallCount.Should().Be(1);
        lib.LastEncodeFlushContext.Should().Be(31);
    }

    [Fact]
    public void FlushEncoder_NoBufferedPackets_ReturnsZeroWithoutError()
    {
        // Idempotent flush / nothing left buffered: size 0 is a valid outcome.
        var lib = new FakeNativeLibrary
        {
            IsAvailable = true,
            EncodeFlushResult = 0,
            EncodeBuffer = IntPtr.Zero,
            EncodeSize = 0,
        };
        var bridge = new NativeBridge(lib);

        IntPtr packet = IntPtr.Zero;
        int size = -1;
        var act = () => bridge.FlushEncoder(new IntPtr(31), out packet, out size);

        act.Should().NotThrow();
        packet.Should().Be(IntPtr.Zero);
        size.Should().Be(0);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-5)]
    public void FlushEncoder_NativeError_ThrowsWithCode(int code)
    {
        var lib = new FakeNativeLibrary { IsAvailable = true, EncodeFlushResult = code };
        var bridge = new NativeBridge(lib);

        var act = () => bridge.FlushEncoder(new IntPtr(31), out _, out _);

        act.Should().Throw<NativeBridgeException>().Which.ErrorCode.Should().Be(code);
    }

    [Fact]
    public void DestroyEncoder_ForwardsHandleAsInt()
    {
        var lib = new FakeNativeLibrary { IsAvailable = true };
        var bridge = new NativeBridge(lib);

        bridge.DestroyEncoder(new IntPtr(99));

        lib.EncodeDestroyCallCount.Should().Be(1);
        lib.LastEncodeDestroyContext.Should().Be(99);
    }

    [Fact]
    public void Unavailable_EncodeFrame_ThrowsNativeBridgeException()
    {
        var bridge = new NativeBridge(new FakeNativeLibrary { IsAvailable = false });

        var act = () => bridge.EncodeFrame(new IntPtr(1), new IntPtr(2), out _, out _);

        act.Should().Throw<NativeBridgeException>().WithMessage("*not available*");
    }

    [Fact]
    public void Unavailable_FlushEncoder_ThrowsNativeBridgeException()
    {
        var bridge = new NativeBridge(new FakeNativeLibrary { IsAvailable = false });

        var act = () => bridge.FlushEncoder(new IntPtr(1), out _, out _);

        act.Should().Throw<NativeBridgeException>().WithMessage("*not available*");
    }

    [Fact]
    public void Unavailable_DestroyEncoder_IsNoOp()
    {
        var lib = new FakeNativeLibrary { IsAvailable = false };
        var bridge = new NativeBridge(lib);

        var act = () => bridge.DestroyEncoder(new IntPtr(3));

        act.Should().NotThrow();
        lib.EncodeDestroyCallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("frame")]
    [InlineData("flush")]
    public void AfterDispose_EncodeOperations_ThrowObjectDisposed(string operation)
    {
        var bridge = new NativeBridge(new FakeNativeLibrary { IsAvailable = true });
        bridge.Dispose();

        Action act = operation switch
        {
            "create" => () => bridge.CreateEncoder(3840, 2160, 20_000, 60),
            "frame" => () => bridge.EncodeFrame(new IntPtr(1), new IntPtr(2), out _, out _),
            _ => () => bridge.FlushEncoder(new IntPtr(1), out _, out _),
        };

        act.Should().Throw<ObjectDisposedException>();
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
