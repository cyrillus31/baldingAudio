using System.Drawing.Drawing2D;
using BaldingAudio.App.Config;
using BaldingAudio.Core.Dsp;
using BaldingAudio.Core.Overlay;

namespace BaldingAudio.App;

internal sealed class SettingsForm : Form
{
    private readonly AppHost _host;
    private readonly AppConfig _config;
    private readonly TabControl _tabs;
    private readonly PreviewPanel _preview;
    private readonly BalanceMeter _meter;
    private readonly Label _numbers;
    private readonly Label _verdict;

    private readonly System.Windows.Forms.Timer _poll;

    public SettingsForm(AppHost host, AppConfig config)
    {
        _host = host;
        _config = config;

        Text = "baldingAudio settings";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9f);

        AutoScroll = true;

        _preview = new PreviewPanel(_config.Style, _config.FilterOrDefault);

        var y = 12;

        _meter = new BalanceMeter { Bounds = new Rectangle(12, y, 576, 92) };
        Controls.Add(_meter);
        y += 100;

        _verdict = new Label
        {
            Bounds = new Rectangle(12, y, 576, 22),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Text = "listening",
        };
        Controls.Add(_verdict);
        y += 26;

        _numbers = new Label
        {
            Bounds = new Rectangle(12, y, 576, 92),
            Font = new Font("Consolas", 9f),
            Text = "starting...",
        };
        Controls.Add(_numbers);
        y += 98;

        _tabs = new TabControl { Bounds = new Rectangle(12, y, 576, 520) };
        Tabs_Build();
        Controls.Add(_tabs);

        var buttons = new FlowLayoutPanel
        {
            Bounds = new Rectangle(12, y + 530, 576, 40),
            FlowDirection = FlowDirection.RightToLeft,
        };

        var close = new Button { Text = "Close", DialogResult = DialogResult.None, AutoSize = true };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(close);

        var defaults = new Button { Text = "Defaults", AutoSize = true };
        defaults.Click += (_, _) => RestoreDefaults();
        buttons.Controls.Add(defaults);

        var save = new Button { Text = "Save now", AutoSize = true };
        save.Click += (_, _) => Save();
        buttons.Controls.Add(save);

        Controls.Add(buttons);

        ClientSize = new Size(600, y + 580);
        MinimumSize = new Size(480, 240);

        _poll = new System.Windows.Forms.Timer { Interval = 50 };
        _poll.Tick += (_, _) => UpdateReadoutPanel();
        _poll.Start();

        UpdateReadoutPanel();
    }

    private void Tabs_Build()
    {
        var style = _config.Style;
        var tuningObj = _config.Tuning;

        // Tab 1: Tuning
        var tuning = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var y = 12;

        AddGroup(tuning, ref y, "Side threshold");
        AddRow(tuning, ref y, "Draw above",
            () => style.BalanceFloorDb,
            v => style.BalanceFloorDb = v,
            0.5, 12.0, 0.1, "dB",
            "How much louder one ear has to be before a line is drawn.");

        AddGroup(tuning, ref y, "How loud before it shows");
        AddRow(tuning, ref y, "Faintest shown",
            () => tuningObj.DisplayFloorDb,
            v => tuningObj.DisplayFloorDb = v,
            -90, -30, 1, "dBFS",
            "Below this level nothing is drawn at all.");
        AddRow(tuning, ref y, "Loudest shown",
            () => tuningObj.DisplayCeilingDb,
            v => tuningObj.DisplayCeilingDb = v,
            -40, 0, 1, "dBFS",
            "The level that maps to full brightness.");
        AddRow(tuning, ref y, "Contrast",
            () => tuningObj.LevelExponent,
            v => tuningObj.LevelExponent = v,
            0.5, 3.0, 0.05, "",
            "How quickly brightness rises with level.");

        AddGroup(tuning, ref y, "Line");
        AddRow(tuning, ref y, "Longest",
            () => style.MaxLengthFraction,
            v => style.MaxLengthFraction = v,
            0.02, 0.20, 0.005, "of width",
            "Length is the imbalance, so this is where a sound right at the edge of the side you reach.");
        AddRow(tuning, ref y, "Shortest",
            () => style.MinLengthFraction,
            v => style.MinLengthFraction = v,
            0.0, 0.03, 0.001, "of width",
            "A floor so a faint line is still visible.");
        AddRow(tuning, ref y, "Thickness",
            () => style.LineThicknessFraction,
            v => style.LineThicknessFraction = v,
            0.002, 0.020, 0.001, "of height",
            "Kept constant so a line reads as a line rather than a dot.");

        AddGroup(tuning, ref y, "Position");
        AddRow(tuning, ref y, "Inset top/bottom",
            () => style.FieldInsetFraction,
            v => style.FieldInsetFraction = v,
            0.0, 0.20, 0.005, "of height",
            "Keeps straight-ahead and directly-behind clear of the screen edges.");
        AddRow(tuning, ref y, "Inset from side",
            () => style.SideInsetFraction,
            v => style.SideInsetFraction = v,
            0.0, 0.06, 0.001, "of height",
            "Pulls the outer end in from the edge, clear of a game's minimap.");
        AddRow(tuning, ref y, "Band height",
            () => style.FieldRadiusYFraction,
            v => style.FieldRadiusYFraction = v,
            0.4, 1.0, 0.01, "",
            "How much of the height the scales span.");

        tuning.Height = y + 20;
        _tabs.TabPages.Add(new TabPage("Tuning") { Controls = { tuning } });

        // Tab 2: Look
        var look = new LookTab(_config, _host, _preview);
        _tabs.TabPages.Add(new TabPage("Look") { Controls = { look.Build() } });

        // Tab 3: Sounds
        var sounds = new SoundsTab(_config, _host, _preview);
        _tabs.TabPages.Add(new TabPage("Sounds") { Controls = { sounds.Build() } });

        // Tab 4: Help
        var help = BuildHelpTab();
        _tabs.TabPages.Add(new TabPage("Help") { Controls = { help } });

        _tabs.SelectedIndexChanged += (_, _) =>
        {
            _preview.Visible = _tabs.SelectedIndex == 1 || _tabs.SelectedIndex == 2;
        };
    }

    private Panel BuildHelpTab()
    {
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var y = 12;

        AddGroup(panel, ref y, "What you are seeing");

        panel.Controls.Add(new Label
        {
            Bounds = new Rectangle(0, y, 550, 120),
            Text = "Horizontal lines run inward from the left and right edges. " +
                   "The side the line starts from (left or right) tells you which ear is louder. " +
                   "The length of the line tells you how much louder (the imbalance). " +
                   "The vertical position tells you where the sound is: " +
                   "near the top is straight ahead, near the bottom is directly behind.",
            AutoSize = false,
        });
        y += 130;

        AddGroup(panel, ref y, "Neon side mode (alternative)");

        panel.Controls.Add(new Label
        {
            Bounds = new Rectangle(0, y, 550, 140),
            Text = "In this mode, the display uses neon blocks on the edges instead of lines. " +
                   "A block on the left or right edge, centred on the horizontal middle, " +
                   "growing symmetrically up and down with the difference between your ears. " +
                   "The vertical size shows how big the difference between your ears is. " +
                   "This mode is for two-channel (stereo) endpoints only; multichannel falls " +
                   "back to lines.",
            AutoSize = false,
        });
        y += 150;

        AddGroup(panel, ref y, "Live preview");
        panel.Controls.Add(new Label
        {
            Bounds = new Rectangle(0, y, 550, 50),
            Text = "The Look and Sounds tabs show a live preview of what will be drawn. " +
                   "Everything you change updates instantly, so you can see the effect before closing the window.",
            AutoSize = false,
        });
        y += 60;

        panel.Height = y + 20;
        return panel;
    }

    private static void AddGroup(Control into, ref int y, string title)
    {
        into.Controls.Add(new Label
        {
            Bounds = new Rectangle(0, y, 550, 18),
            Text = title,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        });
        y += 20;
    }

    private void AddRow(
        Control into, ref int y,
        string label,
        Func<double> get,
        Action<double> set,
        double min,
        double max,
        double step,
        string unit,
        string help)
    {
        var row = new Panel { Bounds = new Rectangle(0, y, 550, 30) };
        row.Controls.Add(new Label
        {
            Bounds = new Rectangle(0, 5, 130, 18),
            Text = label,
        });

        var bar = new TrackBar
        {
            Bounds = new Rectangle(134, 3, 340, 24),
            Minimum = (int)Math.Round(min / step),
            Maximum = (int)Math.Round(max / step),
            TickStyle = TickStyle.None,
            SmallChange = 1,
            LargeChange = (int)Math.Round(1.0 / step),
        };

        bar.MouseUp += (_, _) =>
        {
            var raw = bar.Value * step;
            set(Math.Round(raw, 6));
        };
        bar.KeyUp += (_, _) =>
        {
            var raw = bar.Value * step;
            set(Math.Round(raw, 6));
        };
        row.Controls.Add(bar);

        var value = new Label
        {
            Bounds = new Rectangle(480, 5, 96, 18),
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Consolas", 9f),
        };
        row.Controls.Add(value);

        var tip = new ToolTip();
        tip.SetToolTip(row, help);

        var show = new Action(() =>
        {
            var v = get();
            value.Text = unit.Length > 0
                ? $"{v:0.###} {unit}"
                : v.ToString("0.###");
        });
        show();
        row.Tag = show;
        into.Controls.Add(row);
        y += 32;
    }

    private void RestoreDefaults()
    {
        var d = OverlayStyle.Default();
        var t = Core.Config.AudioTuning.Default();

        _config.Style.BalanceFloorDb = d.BalanceFloorDb;
        _config.Style.MaxLengthFraction = d.MaxLengthFraction;
        _config.Style.MinLengthFraction = d.MinLengthFraction;
        _config.Style.LineThicknessFraction = d.LineThicknessFraction;
        _config.Style.FieldInsetFraction = d.FieldInsetFraction;
        _config.Style.SideInsetFraction = d.SideInsetFraction;
        _config.Style.FieldRadiusYFraction = d.FieldRadiusYFraction;

        _config.Tuning.DisplayFloorDb = t.DisplayFloorDb;
        _config.Tuning.DisplayCeilingDb = t.DisplayCeilingDb;
        _config.Tuning.LevelExponent = t.LevelExponent;

        _config.Filter = new SoundFilter();

        foreach (TabPage page in _tabs.TabPages)
        {
            if (page.Controls[0] is Panel inner)
            {
                foreach (Control ctrl in inner.Controls)
                {
                    if (ctrl.Tag is Action show)
                        show();
                }
            }
        }

        Log.Info("settings restored to defaults (not saved yet)");
    }

    private void Save() => _config.Save();

    private void UpdateReadoutPanel()
    {
        var r = _host.Readout;

        _meter.Set(r);
        _meter.Invalidate();

        _verdict.Text = !r.HasAudio
            ? $"no audio - {r.CaptureState}"
            : r.Paused
                ? "PAUSED - the display is held, capture is still running"
                : r.IsMultichannel
                    ? $"{r.Layout} - multichannel, so length shows loudness not side"
                    : r.PeakHoldBalance is null
                        ? $"{r.Layout} - silent"
                        : r.PeakWouldDraw
                            ? $"DRAWS  {r.PeakHoldDb,5:F1} dB to the {r.Side}  (threshold {_config.Style.BalanceFloorDb:F1} dB)"
                            : $"HIDDEN {r.PeakHoldDb,5:F1} dB to the {r.Side}, under the {_config.Style.BalanceFloorDb:F1} dB threshold";

        _verdict.ForeColor = !r.HasAudio || r.PeakHoldBalance is null
            ? SystemColors.GrayText
            : r.PeakWouldDraw ? Color.FromArgb(0, 130, 0) : Color.FromArgb(170, 40, 0);

        _numbers.Text =
            $"balance    {Fmt(r.Balance, 3)}  ({FmtDb(r.Balance),6} dB)   peak hold {Fmt(r.PeakHoldBalance, 3)}\n" +
            $"level      {r.LoudestDbfs,6:F0} dBFS   shown as {(r.LevelUnit is double u ? u.ToString("F3") : "  -  ")}\n" +
            $"noise flr  {r.NoiseFloorDb,6:F0} dBFS   threshold   {r.FloorDb,5:F1} dB   ({LiveReadout.BalanceFromDb(r.FloorDb):F3})\n" +
            $"lines      {r.LinesDrawn} drawn / {r.TracksTracked} tracked      events {r.EventsSeen}\n" +
            $"capture    {r.CaptureState}   frames {_host.FramesDrawn}";
    }

    private static string Fmt(double? v, int decimals)
        => v is double d ? d.ToString("F" + decimals).PadLeft(5) : "    -";

    private static string FmtDb(double? balance)
        => balance is double b ? Decibel.DbFromBalance(b).ToString("F1") : "  -";

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll.Stop();
        _poll.Dispose();
        Save();
        _host.SettingsClosed();
        _preview.Dispose();
        base.OnFormClosed(e);
    }
}

internal sealed class BalanceMeter : Control
{
    private LiveReadout _readout = LiveReadout.None;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        if (!_readout.HasAudio)
        {
            using var brush = new SolidBrush(Color.FromArgb(170, 40, 0));
            g.DrawString("no audio - " + _readout.CaptureState, Font, brush, new PointF(0, 0));
            return;
        }

        var cx = Width / 2f;
        var cy = Height / 2f;
        var halfBar = 200f;
        var min = -halfBar;
        var max = halfBar;

        var tf = _readout.FloorDb;
        var balanceFloor = LiveReadout.BalanceFromDb(tf is double f ? f : 0.0);
        if (balanceFloor is double bf)
        {
            var xLeft = cx + min * (float)bf;
            var xRight = cx + max * (float)bf;

            using var pen = new Pen(Color.FromArgb(120, 255, 120, 0), 2);
            g.DrawLine(pen, (float)xLeft, 0, (float)xLeft, (float)Height);
            g.DrawLine(pen, (float)xRight, 0, (float)xRight, (float)Height);
        }

        if (_readout.PeakHoldBalance is double peak)
        {
            var x = cx + peak * halfBar;
            using var brush = new SolidBrush(_readout.PeakWouldDraw
                ? Color.FromArgb(0, 160, 0)
                : Color.FromArgb(200, 120, 0));
            g.FillRectangle(brush, new RectangleF((float)Math.Max(min, Math.Min(max, x)) - 3, cy - 6, 6, 12));
        }
    }

    public void Set(LiveReadout r) => _readout = r;
}
