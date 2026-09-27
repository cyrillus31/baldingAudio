using BaldingAudio.App.Config;
using BaldingAudio.Core.Overlay;

namespace BaldingAudio.App;

/// <summary>
/// Tray icon and menu. The app has no main window: the overlay is click-through, so
/// the tray is the only place to reach settings and the only visible thing in the
/// taskbar area.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly AppHost _host;
    private readonly NotifyIcon _icon;
    private ToolStripMenuItem _statusItem = null!;
    private ToolStripMenuItem _layoutItem = null!;
    private ToolStripMenuItem _pauseItem = null!;
    private readonly AppConfig _config;
    private bool _disposed;

    public TrayIcon(AppHost host, AppConfig config)
    {
        _host = host;
        _config = config;

        _statusItem = new ToolStripMenuItem("starting...") { Enabled = false };

        _layoutItem = new ToolStripMenuItem($"Layout: {config.Preset}", null!, (_, _) =>
        {
            _host.TogglePreset();
            _layoutItem.Text = $"Layout: {_config.Preset}";
        });

        _pauseItem = new ToolStripMenuItem("Pause display", null!, (_, _) => _host.TogglePaused());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_layoutItem);
        menu.Items.Add(new ToolStripMenuItem("Run self-test", null!, (_, _) => _host.SelfTest()));
        menu.Items.Add(new ToolStripMenuItem("Open config folder", null!, (_, _) => OpenConfigFolder()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit", null!, (_, _) => Application.Exit()));

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "baldingAudio",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => _host.TogglePaused();

        _host.StatusChanged += s =>
        {
            try
            {
                _statusItem.Text = Truncate(s, 90);
                // NotifyIcon.Text is capped at 63 characters; longer throws.
                _icon.Text = Truncate(s, 60);
            }
            catch (Exception ex)
            {
                Log.Warn($"tray update failed: {ex.Message}");
            }
        };
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static void OpenConfigFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(AppConfig.DefaultPath)!;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"could not open config folder: {ex.Message}");
        }
    }

    /// <summary>
    /// A small drawn icon rather than a .ico file, so there is no binary asset to keep
    /// in sync with the colours the overlay actually uses.
    /// </summary>
    private static System.Drawing.Icon CreateIcon()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            var style = OverlayStyle.Default();
            // Two bars, left and right, echoing the overlay layout.
            using var left = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(200, style.Footstep.R, style.Footstep.G, style.Footstep.B));
            using var right = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(200, style.Gunshot.R, style.Gunshot.G, style.Gunshot.B));
            g.FillRectangle(left, 2, 6, 12, 5);
            g.FillRectangle(left, 2, 15, 9, 5);
            g.FillRectangle(right, 18, 9, 12, 5);
            g.FillRectangle(right, 21, 15, 9, 5);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
