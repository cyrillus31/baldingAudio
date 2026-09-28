using BaldingAudio.App.Config;
using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Overlay;

namespace BaldingAudio.App;

/// <summary>
/// The Sounds tab: which classes of sound reach the display, and how loud each has to be.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the classifier is a heuristic. It calls a sound a footstep or a
/// gunshot from the balance of low, mid and high energy plus how fast the level rose, and
/// nobody has ever checked it against real Battlefield audio. So the tab is built to be
/// <i>correctable</i>: every class is a visible row with its own on/off and its own level
/// floor, and the same list reports what the classifier has actually been calling things
/// lately. If it is wrong, the user can see that it is wrong and mute the class rather
/// than wondering why footsteps are arriving as vehicles.
/// </para>
///
/// <para>
/// The level floors are in dBFS on the same scale the analyser gates on, so "quieter
/// than -50" means one thing everywhere.
/// </para>
/// </remarks>
internal sealed class SoundsTab
{
    private readonly AppConfig _config;
    private readonly PreviewPanel _preview;
    private readonly AppHost _host;
    private readonly List<Control> _rows = new();
    private Label _seen = null!;

    public SoundsTab(AppConfig config, AppHost host, PreviewPanel preview)
    {
        _config = config;
        _host = host;
        _preview = preview;
    }

    public Control Build()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(10),
        };

        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var left = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var y = 0;

        y = Group(left, y, "Which sounds are shown");

        left.Controls.Add(new Label
        {
            Bounds = new Rectangle(0, y, 410, 30),
            Height = 30,
            ForeColor = SystemColors.GrayText,
            Text = "Each row is a type of sound. Turn off the ones you do not want to be " +
                   "interrupted by, or set a level floor so only the loud ones get through.",
        });
        y += 34;

        foreach (var cls in SoundFilter.AllClasses)
            y = ClassRow(left, y, cls);

        y = Group(left, y, "Quick settings");
        foreach (var (label, factory) in new (string, Func<SoundFilter>)[]
                 {
                     ("Everything on", SoundFilter.Default),
                     ("Footsteps and gunfire only", SoundFilter.FootstepsAndGunfire),
                 })
        {
            var btn = new Button { Text = label, Width = 200, Height = 26, Location = new Point(0, y) };
            btn.Click += (_, _) =>
            {
                var f = factory();
                foreach (var c in SoundFilter.AllClasses)
                {
                    _config.FilterOrDefault.SetEnabled(c, f.IsEnabled(c));
                    _config.FilterOrDefault.SetFloor(c, f.FloorFor(c));
                }
                Reload();
                Refresh();
            };
            left.Controls.Add(btn);
            y += 30;
        }

        _seen = new Label
        {
            Bounds = new Rectangle(0, y + 6, 410, 60),
            ForeColor = SystemColors.GrayText,
        };
        left.Controls.Add(_seen);
        y += 70;

        left.Height = y + 20;
        root.Controls.Add(left, 0, 0);

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
            Text = "The preview re-draws as you change things, using the same code as the overlay.",
        };
        right.Controls.Add(caption);
        root.Controls.Add(right, 1, 0);

        Reload();
        Refresh();
        return root;
    }

    private int ClassRow(Control into, int y, SoundClass cls)
    {
        var filter = _config.FilterOrDefault;
        var row = new Panel { Bounds = new Rectangle(0, y, 410, 30) };

        var name = new Label
        {
            Bounds = new Rectangle(0, 5, 110, 20),
            Text = Describe(cls),
        };
        row.Controls.Add(name);

        var swatch = new Panel
        {
            Bounds = new Rectangle(112, 6, 16, 16),
            BackColor = LookTab.ToColor(_config.Style.For(cls)),
            BorderStyle = BorderStyle.FixedSingle,
        };
        row.Controls.Add(swatch);

        var on = new CheckBox
        {
            Bounds = new Rectangle(134, 5, 60, 20),
            Text = "show",
            Checked = filter.IsEnabled(cls),
        };
        on.CheckedChanged += (_, _) =>
        {
            filter.SetEnabled(cls, on.Checked);
            name.ForeColor = on.Checked ? SystemColors.ControlText : SystemColors.GrayText;
            Refresh();
        };
        row.Controls.Add(on);

        var bar = new TrackBar
        {
            Bounds = new Rectangle(196, 3, 130, 24),
            Minimum = -90,
            Maximum = 0,
            Value = ClampLevel(filter.FloorFor(cls)),
            TickStyle = TickStyle.None,
        };
        // Commit on release, for the same reason as every other slider: a value applied
        // mid-drag would let sounds in and out while the user is still deciding.
        bar.MouseUp += (_, _) => Commit(cls, bar.Value, on.Checked, filter);
        bar.KeyUp += (_, _) => Commit(cls, bar.Value, on.Checked, filter);
        row.Controls.Add(bar);

        var value = new Label
        {
            Bounds = new Rectangle(330, 5, 78, 20),
            Font = new Font("Consolas", 9f),
            TextAlign = ContentAlignment.MiddleRight,
        };
        row.Controls.Add(value);

        void Show()
        {
            var db = ClampLevel(filter.FloorFor(cls));
            value.Text = !on.Checked ? "off" : db <= -89 ? "any" : $"{db} dBFS";
            value.ForeColor = on.Checked ? SystemColors.ControlText : SystemColors.GrayText;
        }

        // Local so the click handlers and the reload path share one definition.
        row.Tag = new Action(Show);
        _rows.Add(row);
        into.Controls.Add(row);
        Show();

        name.ForeColor = on.Checked ? SystemColors.ControlText : SystemColors.GrayText;
        return y + 32;
    }

    private void Commit(SoundClass cls, int db, bool enabled, SoundFilter filter)
    {
        filter.SetEnabled(cls, enabled);
        if (enabled) filter.SetFloor(cls, db <= -89 ? SoundFilter.DefaultMinDbfs : db);
        Refresh();
    }

    /// <summary>Re-reads every control from the filter, for after a quick-setting button.</summary>
    public void Reload()
    {
        foreach (var row in _rows)
            if (row.Tag is Action show) show();
    }

    /// <summary>
    /// Re-renders the preview with the filter applied, and refreshes the running tally of
    /// what the classifier has been calling things.
    /// </summary>
    /// <remarks>
    /// The tally is the point of this tab, and it is fed by real detections rather than
    /// the demo cues. The classifier has never been validated against actual game audio,
    /// so a user needs to be able to see that it is calling footsteps "vehicle" before
    /// trusting a filter built on its labels - otherwise "footsteps only" quietly means
    /// "whatever the heuristic decided was a footstep", and there is no way to tell.
    /// </remarks>
    public void Refresh()
    {
        _preview.SetEvents(_config.FilterOrDefault.Apply(DemoCues.Live()).Cast<AudioEvent>().ToList());
        _seen.Text = _host.ClassTally();
    }

    private int ClampLevel(double db)
        => db == SoundFilter.DefaultMinDbfs || db < -90 ? -90 : (int)Math.Round(db);

    private static int Group(Control into, int y, string title)
    {
        into.Controls.Add(new Label
        {
            Bounds = new Rectangle(0, y, 410, 20),
            Text = title,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        });
        return y + 24;
    }

    /// <summary>Plain-language names. The enum names would mean nothing to a player.</summary>
    private static string Describe(SoundClass c) => c switch
    {
        SoundClass.Footstep => "Footsteps",
        SoundClass.Gunshot => "Gunfire",
        SoundClass.Explosion => "Explosions",
        SoundClass.Vehicle => "Vehicles",
        SoundClass.Voice => "Voices",
        _ => "Everything else",
    };
}
