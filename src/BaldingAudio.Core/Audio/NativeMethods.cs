using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BaldingAudio.Core.Audio;

/// <summary>
/// Plain Win32 entry points needed by the Windows shell.
///
/// DESIGN CONSTRAINT, enforced by docs/ANTI-CHEAT.md and the review checklist:
/// every function here is about drawing a window or reading a WASAPI audio stream.
/// None of them can open another process, write into another process, load code
/// into another process, or synthesise user input. Do not add such a function.
/// </summary>
internal static class NativeMethods
{
    internal const int WS_EX_LAYERED = 0x0008_0000;
    internal const int WS_EX_TRANSPARENT = 0x0000_0020;
    internal const int WS_EX_TOPMOST = 0x0000_0008;
    internal const int WS_EX_TOOLWINDOW = 0x0000_0080;
    internal const int WS_EX_NOACTIVATE = 0x0800_0000;
    internal const int WS_POPUP = unchecked((int)0x8000_0000);

    internal const int ULW_ALPHA = 0x0000_0002;
    internal const byte AC_SRC_OVER = 0x00;
    internal const byte AC_SRC_ALPHA = 0x01;

    internal const int SWP_NOSIZE = 0x0001;
    internal const int SWP_NOMOVE = 0x0002;
    internal const int SWP_NOACTIVATE = 0x0010;
    internal const int SWP_SHOWWINDOW = 0x0040;
    internal const int SWP_NOZORDER = 0x0004;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int CX;
        public int CY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;

        public const uint BI_RGB = 0;
        public const uint DIB_RGB_COLORS = 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetLayeredWindowAttributes(IntPtr hwnd, int crKey, byte bAlpha, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, int uFlags);

    internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    internal const uint MONITORINFOF_PRIMARY = 1;
    internal const int SM_CXSCREEN = 0;
    internal const int SM_CYSCREEN = 1;
    internal const int LOGPIXELSX = 88;
    internal const int HORZRES = 8;
    internal const int VERTRES = 10;

    /// <summary>Enumerates monitors, for the overlay to follow the game window.</summary>
    public static IReadOnlyList<(int X, int Y, int Width, int Height, string Name, bool Primary)> EnumerateMonitors()
    {
        var result = new List<(int, int, int, int, string, bool)>();
        var dc = GetDC(IntPtr.Zero);
        try
        {
            EnumDisplayMonitors(dc, IntPtr.Zero, (hMon, _, _, _) =>
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMon, ref mi))
                {
                    result.Add((
                        mi.rcMonitor.Left,
                        mi.rcMonitor.Top,
                        mi.rcMonitor.Right - mi.rcMonitor.Left,
                        mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                        $"Monitor {result.Count + 1}",
                        (mi.dwFlags & MONITORINFOF_PRIMARY) != 0));
                }
                return true;
            }, IntPtr.Zero);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    // --- window creation -----------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern ushort RegisterClass(ref WNDCLASS lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateWindowEx(
        int dwExStyle,
        [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
        [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
        int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>
    /// The monitor holding the foreground window, so the overlay follows the game when
    /// the user alt-tabs between a laptop panel and an external display.
    /// </summary>
    public static (int X, int Y, int Width, int Height) ForegroundMonitorBounds()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return PrimaryMonitorBounds();
        if (!GetWindowRect(hwnd, out var rc)) return PrimaryMonitorBounds();

        var monitors = EnumerateMonitors();
        if (monitors.Count == 0) return PrimaryMonitorBounds();

        // Largest overlap wins, not nearest centre: a window straddling two screens
        // should follow the bigger half, which is what the eye expects.
        var best = monitors[0];
        var bestOverlap = -1L;
        foreach (var m in monitors)
        {
            var ox = Math.Max(0, Math.Min(rc.Right, m.X + m.Width) - Math.Max(rc.Left, m.X));
            var oy = Math.Max(0, Math.Min(rc.Bottom, m.Y + m.Height) - Math.Max(rc.Top, m.Y));
            var overlap = (long)ox * oy;
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = m;
            }
        }
        return (best.X, best.Y, best.Width, best.Height);
    }

    public static (int X, int Y, int Width, int Height) PrimaryMonitorBounds()
    {
        var monitors = EnumerateMonitors();
        foreach (var m in monitors)
            if (m.Primary)
                return (m.X, m.Y, m.Width, m.Height);
        if (monitors.Count > 0) return (monitors[0].X, monitors[0].Y, monitors[0].Width, monitors[0].Height);
        return (0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
    }
}
