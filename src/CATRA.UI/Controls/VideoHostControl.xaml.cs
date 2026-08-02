using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CATRA.UI.Controls;

/// <summary>
/// Win32 child window hosted inside the WPF visual tree (ST-05). The playback
/// engine's <c>IVideoRenderer</c> binds a DXGI swap chain to <see cref="WindowHandle"/>
/// so decoded video renders embedded in the UI rather than in a separate window.
/// </summary>
/// <remarks>
/// Derives from <see cref="HwndHost"/> and creates a plain <c>WS_CHILD</c> window in
/// <see cref="BuildWindowCore"/>. The window is DPI-aware: WPF lays the host out in
/// device-independent pixels and the renderer is told the real pixel size through
/// <c>ResizeOutput</c>, so no extra scaling is applied here.
/// </remarks>
public partial class VideoHostControl : HwndHost
{
    private const string WindowClassName = "CATRAVideoHost";

    // Rooted: Win32 stores the function pointer, so it must never be collected.
    private static readonly WndProcDelegate WindowProc = DefWindowProc;
    private static bool _classRegistered;

    /// <summary>The native child window handle (valid once the host is loaded).</summary>
    public IntPtr WindowHandle => Handle;

    /// <inheritdoc />
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        IntPtr instance = GetModuleHandle(null);
        EnsureClassRegistered(instance);

        int width = Math.Max(1, (int)ActualWidth);
        int height = Math.Max(1, (int)ActualHeight);

        IntPtr hwnd = CreateWindowEx(
            0,
            WindowClassName,
            string.Empty,
            WindowStyles.WS_CHILD | WindowStyles.WS_VISIBLE | WindowStyles.WS_CLIPCHILDREN,
            0,
            0,
            width,
            height,
            hwndParent.Handle,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowEx failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        return new HandleRef(this, hwnd);
    }

    /// <inheritdoc />
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DestroyWindow(hwnd.Handle);
    }

    private static void EnsureClassRegistered(IntPtr instance)
    {
        if (_classRegistered)
        {
            return;
        }

        var wndClass = new WndClass
        {
            style = 0,
            lpfnWndProc = WindowProc,
            hInstance = instance,
            lpszClassName = WindowClassName,
        };

        if (RegisterClass(ref wndClass) == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
        {
            throw new InvalidOperationException(
                $"RegisterClass failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        _classRegistered = true;
    }

    private const int ErrorClassAlreadyExists = 1410;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public int style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
    }

    private static class WindowStyles
    {
        public const int WS_CHILD = 0x40000000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_CLIPCHILDREN = 0x02000000;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WndClass lpWndClass);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
