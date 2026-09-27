using System.ComponentModel;
using System.Runtime.InteropServices;
using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Overlay;
using static BaldingAudio.Core.Audio.NativeMethods;

namespace BaldingAudio.App.Overlay;

/// <summary>
/// A click-through, always-on-top, per-pixel-alpha window covering one monitor.
///
/// Style: <c>WS_POPUP</c> with the extended styles layered / transparent / no-activate
/// / tool-window. The combination means the window never takes focus, never appears
/// in the taskbar or Alt-Tab, and lets every mouse click through to the game. That is
/// the same technique Discord, Steam and OBS overlays use, and it is the reason this
/// app can run over a game without interfering with input.
/// </summary>
public sealed class OverlayWindow : IDisposable
{
    private IntPtr _hwnd;
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _bits;
    private IntPtr _oldBitmap;
    private PixelBuffer? _buffer;
    private (int Width, int Height) _pendingSize;

    public int X { get; private set; }
    public int Y { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public OverlayWindow(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        X = x; Y = y; Width = width; Height = height;
    }

    public IntPtr Handle => _hwnd;

    /// <summary>
    /// Creates the window and the GDI surfaces. Must run on the thread that will own
    /// the message pump, because window creation binds a queue to the thread.
    /// </summary>
    public void Create()
    {
        if (_hwnd != IntPtr.Zero) return;

        var className = "baldingAudioOverlay";
        if (!IsWindowClassRegistered(className)) RegisterClass(className);

        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            className, "baldingAudio",
            WS_POPUP,
            X, Y, Width, Height,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed for the overlay window.");

        _buffer = new PixelBuffer(Width, Height);
        CreateGdiSurfaces();
        Present();
    }

    private static bool _classRegistered;
    private static string _registeredName = string.Empty;

    private static bool IsWindowClassRegistered(string name) => _classRegistered && _registeredName == name;

    private static void RegisterClass(string name)
    {
        var wc = new WNDCLASS
        {
            style = 0,
            lpfnWndProc = DefWindowProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = name,
        };
        if (RegisterClass(ref wc) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _classRegistered = true;
        _registeredName = name;
    }

    private void CreateGdiSurfaces()
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDC failed.");

        _memoryDc = CreateCompatibleDC(screenDc);
        ReleaseDC(IntPtr.Zero, screenDc);
        if (_memoryDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateCompatibleDC failed.");

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = _pendingSize.Width > 0 ? _pendingSize.Width : Width,
            biHeight = -(_pendingSize.Height > 0 ? _pendingSize.Height : Height), // negative = top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BITMAPINFOHEADER.BI_RGB,
        };

        _bitmap = CreateDIBSection(_memoryDc, ref bmi, BITMAPINFOHEADER.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDIBSection failed.");

        _oldBitmap = SelectObject(_memoryDc, _bitmap);
    }

    public PixelBuffer Buffer => _buffer ?? throw new InvalidOperationException("Create() has not been called.");

    /// <summary>Pushes the current buffer contents to the screen.</summary>
    public void Present()
    {
        if (_hwnd == IntPtr.Zero || _buffer is null) return;

        _buffer.CopyTo(_bits);

        var dst = new POINT { X = X, Y = Y };
        var size = new SIZE { CX = Width, CY = Height };
        var src = new POINT { X = 0, Y = 0 };
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA,
        };

        if (!UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref dst, ref size, _memoryDc, ref src, 0, ref blend, ULW_ALPHA))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateLayeredWindow failed.");
    }

    /// <summary>Moves and resizes the overlay. Buffer is reallocated when the size changes.</summary>
    public void SetBounds(int x, int y, int width, int height)
    {
        if (x == X && y == Y && width == Width && height == Height) return;
        if (!SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW))
            return;

        X = x;
        Y = y;

        if (width != Width || height != Height)
        {
            RecreateGdiSurfaces(width, height);
            Width = width;
            Height = height;
            _buffer = new PixelBuffer(width, height);
        }
    }

    private void RecreateGdiSurfaces(int width, int height)
    {
        if (_oldBitmap != IntPtr.Zero) { SelectObject(_memoryDc, _oldBitmap); _oldBitmap = IntPtr.Zero; }
        if (_bitmap != IntPtr.Zero) { DeleteObject(_bitmap); _bitmap = IntPtr.Zero; }
        if (_memoryDc != IntPtr.Zero) { DeleteDC(_memoryDc); _memoryDc = IntPtr.Zero; }

        _pendingSize = (width, height);
        CreateGdiSurfaces();
    }

    public void Dispose()
    {
        if (_oldBitmap != IntPtr.Zero) { SelectObject(_memoryDc, _oldBitmap); _oldBitmap = IntPtr.Zero; }
        if (_bitmap != IntPtr.Zero) { DeleteObject(_bitmap); _bitmap = IntPtr.Zero; }
        if (_memoryDc != IntPtr.Zero) { DeleteDC(_memoryDc); _memoryDc = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
    }
}
