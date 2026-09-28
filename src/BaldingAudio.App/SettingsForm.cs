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
        BackColor = Color.FromArgb(45, 45, 48);
        AutoScroll = true;

        _preview = new PreviewPanel(_config.Style, _config.FilterOrDefault);
        _preview.BackColor = Color.FromArgb(25, 25, 27);

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
            ForeColor = Color.LightGray,
        };
        Controls.Add(_verdict);
        y += 26;

        _numbers = new Label
        {
            Bounds = new Rectangle(12, y, 576, 92),
            Font = new Font("Consolas", 9f),
            Text = "starting...",
            ForeColor = Color.LightGray,
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
            BackColor = Color.FromArgb(45, 45, 48),
        };

        var close = new Button { 
            Text = "Close", 
            DialogResult = DialogResult.None, 
            AutoSize = true,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
        };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(close);

        var defaults = new Button { 
            Text = "Reset all", 
            AutoSize = true,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
        };
        defaults.Click += (_, _) => RestoreDefaults();
        buttons.Controls.Add(defaults);

        var save = new Button { 
            Text = "Save", 
            AutoSize = true,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
        };
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
        var tuning = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(45, 45, 48) };
        var y = 12;

        AddGroup(tuning, ref y, "Side threshold");
        AddRow(tuning, ref y, "Draw above",
            () => style.BalanceFloorDb,
            v => style.BalanceFloorDb = v,
            0.5, 12.0, 0.1, "dB",
            "0.5 dB = very sensitive (draws even tiny imbalances)\n" +
            "12 dB = only very loud imbalances show");

        AddGroup(tuning, ref y, "Volume range");
        AddRow(tuning, ref y, "Faintest",
            () => tuningObj.DisplayFloorDb,
            v => tuningObj.DisplayFloorDb = v,
            -90, -30, 1, "dBFS",
            "-90 dBFS = show everything (even silence noise)\n" +
            "-30 dBFS = only loud sounds show");

        AddRow(tuning, ref y, "Loud max",
            () => tuningObj.DisplayCeilingDb,
            v => tuningObj.DisplayCeilingDb = v,
            -40, 0, 1, "dBFS",
            "What loudness is shown as full brightness");

        AddRow(tuning, ref y, "Contrast",
            () => tuningObj.LevelExponent,
            v => tuningObj.LevelExponent = v,
            0.5, 3.0, 0.05, "",
            "0.5 = very gradual brightness increase\n" +
            "3.0 = very sudden brightness increase (logarithmic)");

        AddGroup(tuning, ref y, "Line layout");
        AddRow(tuning, ref y, "Longest line",
            () => style.MaxLengthFraction,
            v => style.MaxLengthFraction = v,
            0.02, 0.20, 0.005, "of width",
            "0.02 = very short line\n" +
            "0.20 = very long line");

        AddRow(tuning, ref y, "Shortest line",
            () => style.MinLengthFraction,
            v => style.MinLengthFraction = v,
            0.0, 0.03, 0.001, "of width",
            "0.0 = short lines can disappear\n" +
            "0.03 = even faint cues show at least this length");

        AddRow(tuning, ref y, "Line thickness",
            () => style.LineThicknessFraction,
            v => style.LineThicknessFraction = v,
            0.002, 0.020, 0.001, "of height",
            "Thicker lines are easier to see, but take more screen space");

        AddGroup(tuning, ref y, "Position");
        AddRow(tuning, ref y, "Top/bottom",
            () => style.FieldInsetFraction,
            v => style.FieldInsetFraction = v,
            0.0, 0.25, 0.005, "of height",
            "0.0 = front/behind on screen edges\n" +
            "0.25 = pulled far from edges");

        AddRow(tuning, ref y, "Side inset",
            () => style.SideInsetFraction,
            v => style.SideInsetFraction = v,
            0.0, 0.08, 0.001, "of short side",
            "0.0 = cues on screen edges\n" +
            "0.08 = pulled in to avoid game UI");

        AddRow(tuning, ref y, "Band height",
            () => style.FieldRadiusYFraction,
            v => style.FieldRadiusYFraction = v,
            0.4, 1.0, 0.01, "",
            "0.4 = narrow vertical range\n" +
            "1.0 = full screen height for front/behind");

        tuning.Height = y + 20;
        _tabs.TabPages.Add(new TabPage("Tuning") { BackColor = Color.FromArgb(45, 45, 48), Controls = { tuning } });

        // Tab 2: Look
        var look = new LookTab(_config, _host, _preview);
        _tabs.TabPages.Add(new TabPage("Look") { BackColor = Color.FromArgb(45, 45, 48), Controls = { look.Build() } });

        // Tab 3: Sounds
        var sounds = new SoundsTab(_config, _host, _preview);
        _tabs.TabPages.Add(new TabPage("Sounds") { BackColor = Color.FromArgb(45, 45, 48), Controls = { sounds.Build() } });

        // Tab 4: Help
        var help = BuildHelpTab();
        _tabs.TabPages.Add(new TabPage("Help") { BackColor = Color.FromArgb(45, 45, 48), Controls = { help } });

        _tabs.SelectedIndexChanged += (_, _) =>
        {
            _preview.Visible = _tabs.SelectedIndex == 1 || _tabs.SelectedIndex == 2;
        };
    }

    private Panel BuildHelpTab()
    {
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(45, 45, 48) };
        var y = 12;

        AddGroup(panel, ref y, "What you are seeing");

        panel.Controls.Add(new Label
        {
            Bounds = new Rectangle(0, y, 550, 90),
            Text = "Lines run inward from the side edges:\n" +
                   "• Left/right side → which ear is louder\n" +
                   "• Length → how much louder (imbalance)\n" +
                   "• Height → front (top) vs behind (bottom)",
            ForeColor = Color.LightGray,
            AutoSize = false,
        });
        y += 100;

        AddGroup(panel, ref y, "Neon blocks");

        panel.Controls.Add(new Label
        {
            Bounds = new Rectangle(0, y, 550, 120),
            Text = "Neon blocks (stereo only):\n" +
                   "• Left block = left ear louder\n" +
                   "• Right block = right ear louder\n" +
                   "• Brightest at screen edge, fades toward center\n" +
                   "• Vertical size = how much louder one ear is\n" +
                   "• Multichannel automatically falls back to lines",
            ForeColor = Color.LightGray,
            AutoSize = false,
        });
        y += 130;

        AddGroup(panel, ref y, "Live preview");

        panel.Controls.Add(new Label
        {
            Bounds = new Rectangle(0, y, 550, 40),
            Text = "The Look and Sounds tabs show exactly what you'll see in-game. " +
                   "Changes update instantly as you drag sliders.",
            ForeColor = Color.LightGray,
            AutoSize = false,
        });
        y += 50;

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
            ForeColor = Color.LightGray,
            BackColor = Color.Transparent,
        });
        into.Controls.Add(new Panel
        {
            Bounds = new Rectangle(0, y + 14, 550, 2),
            BackColor = Color.FromArgb(100, 100, 100),
        });
        y += 22;
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
        var row = new Panel { Bounds = new Rectangle(0, y, 550, 70), BackColor = Color.Transparent };
        
        var labelTop = new Label
        {
            Bounds = new Rectangle(0, 0, 140, 20),
            Text = label,
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
        };
        row.Controls.Add(labelTop);

        var value = new Label
        {
            Bounds = new Rectangle(140, 0, 60, 20),
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Consolas", 9f),
            ForeColor = Color.LightGreen,
            BackColor = Color.Transparent,
        };
        row.Controls.Add(value);

        var bar = new TrackBar
        {
            Bounds = new Rectangle(0, 24, 550, 24),
            Minimum = (int)Math.Round(min / step),
            Maximum = (int)Math.Round(max / step),
            TickStyle = TickStyle.None,
            SmallChange = 1,
            LargeChange = (int)Math.Round(1.0 / step),
            BackColor = Color.FromArgb(60, 60, 60),
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

        var helpLabel = new Label
        {
            Bounds = new Rectangle(0, 52, 550, 18),
            Text = help,
            Font = new Font("Segoe UI", 8f),
            ForeColor = Color.LightGray,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
        };
        row.Controls.Add(helpLabel);

        into.Controls.Add(row);
        y += 76;
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
                ? "PAUSED - the display is held, capture still running"
                : r.IsMultichannel
                    ? $"{r.Layout} - multichannel: length shows loudness"
                    : r.PeakHoldBalance is null
                        ? $"{r.Layout} - silent"
                        : r.PeakWouldDraw
                            ? $"DRAWS  {r.PeakHoldDb,5:F1} dB to the {r.Side}  (thresh {_config.Style.BalanceFloorDb:F1} dB)"
                            : $"HIDDEN {r.PeakHoldDb,5:F1} dB to the {r.Side}, under {_config.Style.BalanceFloorDb:F1} dB";

        _verdict.ForeColor = !r.HasAudio || r.PeakHoldBalance is null
            ? Color.Gray
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
        g.Clear(Color.FromArgb(30, 30, 32));

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
