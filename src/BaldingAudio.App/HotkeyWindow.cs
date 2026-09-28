using System.Runtime.InteropServices;
using BaldingAudio.App.Config;

namespace BaldingAudio.App;

/// <summary>
/// Global hotkeys. The overlay is click-through and the game owns the mouse and most
/// of the keyboard, so the only practical way to reach the app is a global hotkey.
///
///   Ctrl+Alt+Shift+B  show/hide the display
///   Ctrl+Alt+Shift+P  switch between the edge and compact layouts
///   Ctrl+Alt+Shift+D  run the analyser self-test
///   Ctrl+Alt+Shift+Q  quit
///
/// All four carry Shift, and that is a fix rather than a style choice. Without it, the
/// show/hide key was Ctrl+Alt+B, and the user's field log recorded it firing four times
/// unprompted partway through a game - the overlay paused itself and, because pausing
/// cleared every line, looked exactly like a crash. Something in the game or in
/// peripheral software was sending that combination. A three-modifier chord is not a
/// thing those send by accident, so the collision is much less likely to recur, and
/// pausing now freezes the display rather than blanking it, so if one ever does, it will
/// be visible rather than looking like a fault.
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_DISPLAY_CHANGE = 0x007E;

    // Values from the Win32 header, not from memory.
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;

    private const uint Modifiers = MOD_CONTROL | MOD_ALT | MOD_SHIFT;

    private readonly AppHost _host;
    private readonly Dictionary<int, Action> _actions = new();
    private readonly List<int> _registered = new();
    private int _nextId = 0xB000;

    public HotkeyWindow(AppHost host)
    {
        _host = host;
        CreateHandle(new CreateParams
        {
            Caption = "baldingAudio.hotkeys",
            // A message-only window: exists to receive WM_HOTKEY and never appears.
            Parent = new IntPtr(-3), // HWND_MESSAGE
        });
    }

    public void Register()
    {
        Bind((byte)'B', () => _host.TogglePaused());
        Bind((byte)'P', () => _host.TogglePreset());
        Bind((byte)'D', () => _host.SelfTest());
        Bind((byte)'Q', () => Application.Exit());
    }

    private void Bind(byte vk, Action action)
    {
        var id = _nextId++;
        if (!RegisterHotKey(Handle, id, Modifiers, vk))
        {
            // Another app already owns this combination. Not fatal: the tray menu
            // still works, so carry on and just say so.
            Log.Warn($"hotkey Ctrl+Alt+Shift+{(char)vk} is already taken by another application");
            return;
        }
        _actions[id] = action;
        _registered.Add(id);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            if (_actions.TryGetValue(m.WParam.ToInt32(), out var action))
            {
                try { action(); }
                catch (Exception ex) { Log.Error($"hotkey failed: {ex}"); }
                return;
            }
        }
        else if (m.Msg == WM_DISPLAY_CHANGE)
        {
            // Resolution or monitor topology changed; the overlay should follow.
            Log.Info("display configuration changed");
        }

        base.WndProc(ref m);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public void Dispose()
    {
        foreach (var id in _registered) UnregisterHotKey(Handle, id);
        _registered.Clear();
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
