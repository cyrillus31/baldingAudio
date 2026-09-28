using BaldingAudio.App.Config;
using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Dsp;
using BaldingAudio.Core.Overlay;

namespace BaldingAudio.App;

/// <summary>
/// The Look tab: colours, how far cues come in from the edges, how many there are, and
/// which of the two display modes to use.
/// </summary>
/// <remarks>
/// <para>
/// Every control writes straight into the live config object, and the preview beside it
/// is drawn by the real renderer from that same object. There is no apply step and no
/// copy, so what is on screen is what will be over the game - including the moment
/// mid-drag, which is when a user actually judges a value.
/// </para>
/// </remarks>
internal sealed class LookTab
{
    private readonly AppConfig _config;
    private readonly AppHost _host;
    private readonly PreviewPanel _preview;
    private readonly List<Action> _refreshers = new();

    public LookTab(AppConfig host_config, AppHost host, PreviewPanel preview)
    {
        _config = host_config;
        _host = host;
        _preview = preview;
    }

    /// <summary>Builds the tab's controls and returns the panel to drop into the tab strip.</summary>
    public Control Build()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(10),
        };

        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var left = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var y = 0;

        // --- mode -------------------------------------------------------------------
        y = Group(left, y, "How cues are shown");

        var mode = new ComboBox { Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
        mode.Items.Add("Lines from the edges (default)");
        mode.Items.Add("Neon blocks on the edges");
        mode.SelectedIndex = _config.Style.DisplayMode == OverlayDisplayMode.EdgeGlow ? 1 : 0;
        mode.SelectedIndexChanged += (_, _) =>
        {
            _config.Style.DisplayMode = mode.SelectedIndex == 1
                ? OverlayDisplayMode.EdgeGlow
                : OverlayDisplayMode.Lines;
            _host.SelfTest();
            Refresh();
        };
        y = Control(left, y, "Mode", mode);

        var modeNote = new Label
        {
            Width = 370,
            Height = 44,
            ForeColor = SystemColors.GrayText,
        };
        modeNote.Text = _config.Style.DisplayMode == OverlayDisplayMode.EdgeGlow
            ? "A block on the left or right edge, centred on the middle of the screen, growing " +
              "symmetrically up and down and inward. The vertical size shows how big the " +
              "difference between your ears is. Both edges use the same colour."
            : "Horizontal lines running inward from the left and right edges. Length is how far " +
              "off to one side the sound is. Height is in front or behind.";
        left.Controls.Add(modeNote);
        y += 48;

        // --- colour -----------------------------------------------------------------
        y = Group(left, y, "Colour");

        y = ColourRow(left, y, "Cue colour", () => _config.Style.IndicatorColour,
            c => { _config.Style.SetAllColours(c); }, "All sound types.");

        if (_config.Style.DisplayMode == OverlayDisplayMode.EdgeGlow)
            y = ColourRow(left, y, "Neon colour", () => _config.Style.EdgeGlow,
                c => _config.Style.EdgeGlow = c, "Used on both edges in neon mode.");

        y = Slider(left, y, "Outline width", () => _config.Style.OutlineWidthFraction,
            v => _config.Style.OutlineWidthFraction = v, 0, 1.5, 0.05, "",
            "A lighter rim of the same colour, so a line is visible against a dark scene. 0 turns it off.");

        y = Slider(left, y, "Outline lighten", () => _config.Style.OutlineLighten,
            v => _config.Style.OutlineLighten = v, 0, 1, 0.05, "",
            "How far the rim is pushed toward white. Keeps it the same hue.");

        // --- how far in --------------------------------------------------------------
        y = Group(left, y, "How far cues come in from the edge");

        y = Slider(left, y, "Inset from side", () => _config.Style.SideInsetFraction,
            v => _config.Style.SideInsetFraction = v, 0, 0.08, 0.001, "of the short side",
            "Raise this to keep cues clear of a game's own icons in the corners.");

        y = Slider(left, y, "Inset top and bottom", () => _config.Style.FieldInsetFraction,
            v => _config.Style.FieldInsetFraction = v, 0, 0.25, 0.005, "of the short side",
            "Keeps sounds in front and directly behind clear of the screen edges.");

        y = Slider(left, y, "Band height", () => _config.Style.FieldRadiusYFraction,
            v => _config.Style.FieldRadiusYFraction = v, 0.4, 1.0, 0.01, "",
            "How much of the screen height the up-and-down range covers.");

        // --- how many ----------------------------------------------------------------
        y = Group(left, y, "How many at once");

        y = Slider(left, y, "Most lines", () => _config.Style.MaxLines,
            v => _config.Style.MaxLines = (int)Math.Round(v), 0, 12, 1, "",
            "0 means no limit. Above the limit the loudest are kept, so a firefight shows " +
            "gunshots rather than whichever footsteps started first.");

        var merge = new CheckBox
        {
            Text = "Collapse each side into one line",
            Width = 370,
            Height = 22,
            Checked = _config.Style.MergeToSingleLine,
        };
        merge.CheckedChanged += (_, _) =>
        {
            _config.Style.MergeToSingleLine = merge.Checked;
            Refresh();
        };
        left.Controls.Add(merge);
        y += 26;

        var mergeNote = new Label
        {
            Width = 370,
            Height = 32,
            ForeColor = SystemColors.GrayText,
            Text = "Everything on one side becomes a single cue, taking the loudest level. " +
                   "Useful when a lot is happening and the lines have started to blur together.",
        };
        left.Controls.Add(mergeNote);
        y += 36;

        // --- neon geometry, only relevant in that mode --------------------------------
        if (_config.Style.DisplayMode == OverlayDisplayMode.EdgeGlow)
        {
            y = Group(left, y, "Neon blocks");

            y = Slider(left, y, "Reach inward", () => _config.Style.EdgeGlowWidthFraction,
                v => _config.Style.EdgeGlowWidthFraction = v, 0.01, 0.20, 0.005, "of the width",
                "How far a block comes in from its edge at full size.");

            y = Slider(left, y, "Spread up and down", () => _config.Style.EdgeGlowMaxHalfHeightFraction,
                v => _config.Style.EdgeGlowMaxHalfHeightFraction = v, 0.05, 0.45, 0.01, "of the height",
                "The most a block can spread either side of the centre. Kept below half so it " +
                "never reaches the corners where the game's own icons are.");
        }

        left.Height = y + 20;
        root.Controls.Add(left, 0, 0);

        // --- preview ------------------------------------------------------------------
        var right = new Panel { Dock = DockStyle.Fill };
        _preview.Dock = DockStyle.Fill;
        _preview.ShowGuides = true;
        right.Controls.Add(_preview);

        var caption = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
            Text = "Exactly what will be drawn over the game. The dotted lines are a third and " +
                   "two thirds of the height: in front at the top, behind at the bottom.",
        };
        right.Controls.Add(caption);
        root.Controls.Add(right, 1, 0);

        Refresh();
        return root;
    }

    /// <summary>Re-renders the preview. Cheap, so it runs on every change.</summary>
    public void Refresh()
    {
        foreach (var r in _refreshers) r();
        _preview.SetEvents(DemoCues.Live());
    }

    // --- little layout helpers ---------------------------------------------------

    private static int Group(Control into, int y, string title)
    {
        into.Controls.Add(new Label
        {
            Bounds = new Rectangle(0, y, 380, 20),
            Text = title,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        });
        return y + 24;
    }

    private static int Control(Control into, int y, string label, Control child)
    {
        into.Controls.Add(new Label { Bounds = new Rectangle(0, y + 4, 120, 20), Text = label });
        child.Location = new Point(126, y);
        into.Controls.Add(child);
        return y + 28;
    }

    private int Slider(
        Control into, int y, string label, Func<double> get, Action<double> set,
        double min, double max, double step, string unit, string help)
    {
        var row = new Panel { Bounds = new Rectangle(0, y, 380, 30) };
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

        // Reload shows initial value
        var tip = new ToolTip();
        tip.SetToolTip(row, help);

        into.Controls.Add(row);
        _refreshers.Add(() =>
        {
            var v = get();
            value.Text = unit.Length > 0
                ? $"{v:0.###} {unit}"
                : v.ToString("0.###");
        });

        return y + 32;
    }

    private int ColourRow(
        Control into, int y, string label, Func<Rgba> get, Action<Rgba> set, string help)
    {
        into.Controls.Add(new Label { Bounds = new Rectangle(0, y + 4, 120, 20), Text = label });

        var box = new Panel
        {
            Bounds = new Rectangle(126, y, 44, 22),
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
                // Any colour at all, including fully transparent - a user may want a cue
                // visible in the preview and invisible in the game, or vice versa.
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

    internal static Color ToColor(Rgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    internal static Rgba FromColor(Color c) => new(c.R, c.G, c.B, c.A);
}

/// <summary>
/// The demonstration cues the live preview shows.
/// </summary>
/// <remarks>
/// A fixed spread of imbalances, so the preview always demonstrates the range rather
/// than whatever happened to be loudest. One is below the threshold on purpose, because
/// the user needs to see that a faint sound produces nothing at the current setting -
/// that is the single most surprising property of the display.
/// </remarks>
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
