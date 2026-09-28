using BaldingAudio.App.Config;
using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Dsp;
using BaldingAudio.Core.Overlay;

namespace BaldingAudio.App;

/// <summary>
/// The Look tab: colours, how far cues come in from the edges, how many there are, and
/// which of the two display modes to use.
/// </summary>
internal sealed class LookTab
{
    private readonly AppConfig _config;
    private readonly AppHost _host;
    private readonly PreviewPanel _preview;
    private readonly List<Action> _refreshers = new();
    private readonly Panel _leftPanel;

    public LookTab(AppConfig host_config, AppHost host, PreviewPanel preview)
    {
        _config = host_config;
        _host = host;
        _preview = preview;
        _leftPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(45, 45, 48) };
    }

    public Control Build()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(10),
            BackColor = Color.FromArgb(45, 45, 48),
        };

        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var y = 0;

        // --- casual mode ------------------------------------------------------------------------------
        var casualPanel = new Panel { Bounds = new Rectangle(0, y, 360, 50), BackColor = Color.Transparent };
        
        var casualLabel = new Label {
            Bounds = new Rectangle(0, 5, 150, 20),
            Text = "Casual mode",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.LightGreen,
            BackColor = Color.Transparent,
        };
        casualPanel.Controls.Add(casualLabel);

        var casualCombo = new ComboBox {
            Width = 180,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            Location = new Point(156, 2),
        };
        casualCombo.Items.Add("Simple (casual)");
        casualCombo.Items.Add("Advanced (full control)");
        casualCombo.SelectedIndex = _config.CasualMode ? 0 : 1;
        casualCombo.SelectedIndexChanged += (_, _) =>
        {
            _config.CasualMode = casualCombo.SelectedIndex == 0;
            UpdateCasualMode();
            Refresh();
        };
        casualPanel.Controls.Add(casualCombo);

        var casualHelp = new Label {
            Bounds = new Rectangle(0, 32, 360, 18),
            Text = "Simple: hides advanced sliders, keeps defaults that work for most games.",
            Font = new Font("Segoe UI", 7.5f),
            ForeColor = Color.LightGray,
            BackColor = Color.Transparent,
        };
        casualPanel.Controls.Add(casualHelp);

        _leftPanel.Controls.Add(casualPanel);
        y += 54;

        // --- mode -------------------------------------------------------------------
        y = Group(_leftPanel, y, "Display mode");

        var mode = new ComboBox { 
            Width = 260, 
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
        };
        mode.Items.Add("Lines from edges");
        mode.Items.Add("Neon blocks");
        mode.SelectedIndex = _config.Style.DisplayMode == OverlayDisplayMode.EdgeGlow ? 1 : 0;
        mode.SelectedIndexChanged += (_, _) =>
        {
            _config.Style.DisplayMode = mode.SelectedIndex == 1
                ? OverlayDisplayMode.EdgeGlow
                : OverlayDisplayMode.Lines;
            _host.SelfTest();
            Refresh();
        };
        y = Control(_leftPanel, y, "Mode", mode);

        var modeNote = new Label
        {
            Width = 340,
            Height = 50,
            ForeColor = Color.LightGray,
            Font = new Font("Segoe UI", 8f),
            BackColor = Color.Transparent,
        };
        modeNote.Text = _config.Style.DisplayMode == OverlayDisplayMode.EdgeGlow
            ? "• Left block = left ear louder, right block = right ear louder.\n" +
              "• Brightest at screen edge, fades toward center.\n" +
              "• Vertical size = how much louder one ear is."
            : "• Lines start at left/right edges and run inward.\n" +
              "• Line length = how much louder one ear is.\n" +
              "• Height shows front (top) vs behind (bottom).";
        _leftPanel.Controls.Add(modeNote);
        y += 54;

        // --- colour -----------------------------------------------------------------
        y = Group(_leftPanel, y, "Appearance");

        y = ColourRow(_leftPanel, y, "Cue colour", () => _config.Style.IndicatorColour,
            c => { _config.Style.SetAllColours(c); }, "All sound types.");

        if (_config.Style.DisplayMode == OverlayDisplayMode.EdgeGlow)
            y = ColourRow(_leftPanel, y, "Neon colour", () => _config.Style.EdgeGlow,
                c => _config.Style.EdgeGlow = c, "Used on both edges.");

        y = Slider(_leftPanel, y, "Outline width", () => _config.Style.OutlineWidthFraction,
            v => _config.Style.OutlineWidthFraction = v, 0, 1.5, 0.05, "",
            "0 = no outline.\n0.55 = default (lighter rim visible on dark scenes).",
            isAdvanced: true);

        y = Slider(_leftPanel, y, "Outline lightness", () => _config.Style.OutlineLighten,
            v => _config.Style.OutlineLighten = v, 0, 1, 0.05, "",
            "0 = stays same colour.\n1 = turns white.\n0.55 = default (lighter rim keep hue).",
            isAdvanced: true);

        // --- spacing ----------------------------------------------------------------
        y += 8;

        // --- how far in --------------------------------------------------------------
        y = Group(_leftPanel, y, "Position");

        y = Slider(_leftPanel, y, "Side inset", () => _config.Style.SideInsetFraction,
            v => _config.Style.SideInsetFraction = v, 0, 0.08, 0.001, "",
            "0 = cues on screen edge.\n0.08 = pulled in to avoid game UI.",
            isAdvanced: true);

        y = Slider(_leftPanel, y, "Top/bottom inset", () => _config.Style.FieldInsetFraction,
            v => _config.Style.FieldInsetFraction = v, 0, 0.25, 0.005, "",
            "0 = on screen edges.\n0.25 = pulled far from edges.",
            isAdvanced: true);

        y = Slider(_leftPanel, y, "Band height", () => _config.Style.FieldRadiusYFraction,
            v => _config.Style.FieldRadiusYFraction = v, 0.4, 1.0, 0.01, "",
            "0.4 = narrow vertical range.\n1.0 = full screen height for front/behind.",
            isAdvanced: true);

        // --- how many ----------------------------------------------------------------
        y += 8;
        y = Group(_leftPanel, y, "Cue limits");

        y = Slider(_leftPanel, y, "Max cues", () => _config.Style.MaxLines,
            v => _config.Style.MaxLines = (int)Math.Round(v), 0, 12, 1, "",
            "0 = no limit (all cues show).\n12 = show at most 12 cues, loudest wins.");

        var merge = new CheckBox
        {
            Text = "Merge cues on same side",
            Width = 340,
            Height = 22,
            Checked = _config.Style.MergeToSingleLine,
            Font = new Font("Segoe UI", 9f),
            BackColor = Color.Transparent,
            ForeColor = Color.White,
        };
        merge.CheckedChanged += (_, _) =>
        {
            _config.Style.MergeToSingleLine = merge.Checked;
            Refresh();
        };
        _leftPanel.Controls.Add(merge);
        y += 26;

        var mergeNote = new Label
        {
            Width = 340,
            Height = 26,
            ForeColor = Color.LightGray,
            Font = new Font("Segoe UI", 8f),
            BackColor = Color.Transparent,
            Text = "All cues on one side become a single merged block showing loudest cue.",
        };
        _leftPanel.Controls.Add(mergeNote);
        y += 30;

        // --- neon geometry, only relevant in that mode --------------------------------
        if (_config.Style.DisplayMode == OverlayDisplayMode.EdgeGlow)
        {
            y += 8;
            y = Group(_leftPanel, y, "Neon block size");

            y = Slider(_leftPanel, y, "Width (inward)", () => _config.Style.EdgeGlowWidthFraction,
                v => _config.Style.EdgeGlowWidthFraction = v, 0.01, 0.20, 0.005, "",
                "How wide block extends from edge (0.01 = thin, 0.20 = very wide).",
                isAdvanced: true);

            y = Slider(_leftPanel, y, "Height (max spread)", () => _config.Style.EdgeGlowMaxHalfHeightFraction,
                v => _config.Style.EdgeGlowMaxHalfHeightFraction = v, 0.05, 0.45, 0.01, "",
                "Max vertical spread (0.05 = small, 0.45 = huge).\nKept below corners so minimap/ammo UI isn't covered.",
                isAdvanced: true);
        }

        _leftPanel.Height = y + 20;
        root.Controls.Add(_leftPanel, 0, 0);

        // --- preview ------------------------------------------------------------------
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(25, 25, 27) };
        _preview.Dock = DockStyle.Fill;
        _preview.ShowGuides = true;
        _preview.BackColor = Color.FromArgb(30, 30, 32);
        right.Controls.Add(_preview);

        var caption = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.LightGray,
            Font = new Font("Segoe UI", 8f),
            Text = "Live preview (exactly what you'll see in-game).",
            BackColor = Color.FromArgb(30, 30, 32),
        };
        right.Controls.Add(caption);
        root.Controls.Add(right, 1, 0);

        Refresh();
        return root;
    }

    public void Refresh()
    {
        foreach (var r in _refreshers) r();
        _preview.SetEvents(DemoCues.Live());
    }

    // --- layout helpers ----------------------------------------------------------

    private static int Group(Control into, int y, string title)
    {
        into.Controls.Add(new Label
        {
            Bounds = new Rectangle(0, y, 340, 20),
            Text = title,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.LightGray,
            BackColor = Color.Transparent,
        });
        into.Controls.Add(new Panel
        {
            Bounds = new Rectangle(0, y + 16, 340, 2),
            BackColor = Color.FromArgb(100, 100, 100),
        });
        return y + 26;
    }

    private static int Control(Control into, int y, string label, Control child)
    {
        into.Controls.Add(new Label { 
            Bounds = new Rectangle(0, y + 4, 100, 18), 
            Text = label,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
        });
        child.Location = new Point(106, y);
        into.Controls.Add(child);
        return y + 26;
    }

    private int Slider(
        Control into, int y, string label, Func<double> get, Action<double> set,
        double min, double max, double step, string unit, string help, bool isAdvanced = false)
    {
        var row = new Panel { 
            Bounds = new Rectangle(0, y, 360, 70),
            Tag = isAdvanced ? "advanced" : null,
        };
        
        var labelTop = new Label
        {
            Bounds = new Rectangle(0, 0, 100, 20),
            Text = label,
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
        };
        row.Controls.Add(labelTop);

        var value = new Label
        {
            Bounds = new Rectangle(100, 0, 60, 20),
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Consolas", 9f),
            ForeColor = Color.LightGreen,
            BackColor = Color.Transparent,
        };
        row.Controls.Add(value);

        var bar = new TrackBar
        {
            Bounds = new Rectangle(0, 24, 360, 24),
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
            Bounds = new Rectangle(0, 52, 360, 18),
            Text = help,
            Font = new Font("Segoe UI", 7.5f),
            ForeColor = Color.LightGray,
            BackColor = Color.Transparent,
            AutoEllipsis = true,
        };
        row.Controls.Add(helpLabel);

        var tip = new ToolTip();
        tip.SetToolTip(bar, help);

        _leftPanel.Controls.Add(row);
        _refreshers.Add(() =>
        {
            var v = get();
            value.Text = unit.Length > 0
                ? $"{v:0.###} {unit}"
                : v.ToString("0.###");
        });

        return y + 74;
    }

    private int ColourRow(
        Control into, int y, string label, Func<Rgba> get, Action<Rgba> set, string help)
    {
        into.Controls.Add(new Label { 
            Bounds = new Rectangle(0, y + 4, 100, 18), 
            Text = label,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
        });

        var box = new Panel
        {
            Bounds = new Rectangle(106, y, 36, 20),
            BackColor = ToColor(get()),
            BorderStyle = BorderStyle.FixedSingle,
        };

        var tip = new ToolTip();
        tip.SetToolTip(box, help + "  Click to choose.");

        box.Click += (_, _) =>
        {
            using var dlg = new ColorDialog
            {
                Color = ToColor(get()),
                FullOpen = true,
                CustomColors = { },
            };

            if (dlg.ShowDialog() != DialogResult.OK) return;

            set(FromColor(dlg.Color));
            box.BackColor = dlg.Color;
            _preview.SetEvents(DemoCues.Live());
        };

        into.Controls.Add(box);
        return y + 28;
    }

    private void UpdateCasualMode()
    {
        foreach (Control ctrl in _leftPanel.Controls)
        {
            if (ctrl.Tag as string == "advanced")
            {
                ctrl.Visible = !_config.CasualMode;
            }
        }
        _leftPanel.PerformLayout();
    }

    internal static Color ToColor(Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    internal static Rgba FromColor(Color c) => new(c.R, c.G, c.B, c.A);
}

/// <summary>
/// The demonstration cues the live preview shows.
/// </summary>
internal static class DemoCues
{
    public static IReadOnlyList<AudioEvent> Live() => new[]
    {
        Cue(20, 9.0, 0.9, SoundClass.Gunshot),
        Cue(55, 5.0, 0.7, SoundClass.Footstep),
        Cue(90, 2.5, 0.5, SoundClass.Footstep),
        Cue(-40, 7.0, 0.8, SoundClass.Footstep),
        Cue(-95, 3.5, 0.6, SoundClass.Vehicle),
        Cue(160, 6.0, 0.65, SoundClass.Explosion),
    };

    private static AudioEvent Cue(double bearing, double balanceDb, double level, SoundClass cls) => new(
        new Direction(bearing, 0),
        Level: level,
        Dbfs: Decibel.FromUnitLevel(level),
        Class: cls,
        ClassConfidence: 0.8,
        Confidence: 1.0,
        DistanceConfidence: 0.5,
        Timestamp: 0)
    {
        Balance = Decibel.BalanceFromDb(balanceDb),
    };
}
