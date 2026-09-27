using System.Runtime.InteropServices;
using BaldingAudio.App.Config;

namespace BaldingAudio.App;

/// <summary>
/// Global hotkeys. The overlay is click-through and the game owns the mouse and most
/// of the keyboard, so the only practical way to reach the app is a global hotkey.
///
///   Ctrl+Alt+B  show/hide the display
///   Ctrl+Alt+P  switch between the edge and compact layouts
///   Ctrl+Alt+D  run the analyser self-test
///   Ctrl+Alt+Q  quit
///
/// The combinations are chosen to be implausible in a game, so they cannot be
/// swallowed by a fullscreen title or fire a real game action.
/// </summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_DISPLAY_CHANGE = 0x007E;

    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_ALT = 0x0001;

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
        if (!RegisterHotKey(Handle, id, MOD_CONTROL | MOD_ALT, vk))
        {
            // Another app already owns this combination. Not fatal: the tray menu
            // still works, so carry on and just say so.
            Log.Warn($"hotkey Ctrl+Alt+{(char)vk} is already taken by another application");
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
