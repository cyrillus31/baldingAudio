using BaldingAudio.Core.Audio;

namespace BaldingAudio.Core.Overlay;

/// <summary>
/// Whether one class of sound reaches the display, and how loud it has to be.
/// </summary>
/// <param name="Enabled">False hides the class entirely.</param>
/// <param name="MinDbfs">
/// Quietest level shown for it, in dBFS. Ignored when <paramref name="Enabled"/> is
/// false.
/// </param>
/// <remarks>
/// <para>
/// A type rather than two parallel fields, so "off" and "no level floor" cannot be
/// confused. The first version of this expressed "off" as a level of positive infinity,
/// which reads neatly in memory and throws on save: System.Text.Json refuses infinity
/// unless named-floating-point handling is switched on, so the config write failed the
/// moment anyone filtered anything out.
/// </para>
/// </remarks>
public sealed record SoundClassRule
{
    /// <summary>Shown, with no level floor.</summary>
    public static readonly SoundClassRule On = new() { Enabled = true, MinDbfs = SoundFilter.DefaultMinDbfs };

    public bool Enabled { get; set; } = true;

    /// <summary>Level floor in dBFS. Never infinite; see the note on the type.</summary>
    public double MinDbfs { get; set; } = SoundFilter.DefaultMinDbfs;

    public bool Allows(double dbfs) => Enabled && dbfs >= MinDbfs;
}

/// <summary>
/// Which sounds reach the display, and how loud they have to be to get there.
/// </summary>
/// <remarks>
/// <para>
/// Exists because the classifier is a heuristic, and a user who has decided that only
/// footsteps and gunfire are worth interrupting for should not have to fight the rest of
/// the mix. A vehicle or a voice can be filtered out entirely; a footstep can have its
/// own level cut so that only hard steps show.
/// </para>
///
/// <para>
/// The level is a floor on the event's own <see cref="AudioEvent.Dbfs"/>, the same
/// quantity the onset detector gates on, so "quieter than -50 dBFS" means one thing
/// everywhere rather than a second scale calibrated to nothing.
/// </para>
///
/// <para>
/// Default is every class on with no floor, which is the behaviour that shipped before
/// this existed. Anything else would silently hide sounds the user had not chosen to
/// hide, on a display whose whole purpose is not missing a footstep.
/// </para>
/// </remarks>
public sealed class SoundFilter
{
    /// <summary>All the classes, in the order they are presented.</summary>
    public static readonly SoundClass[] AllClasses =
    {
        SoundClass.Footstep,
        SoundClass.Gunshot,
        SoundClass.Explosion,
        SoundClass.Vehicle,
        SoundClass.Voice,
        SoundClass.Other,
    };

    /// <summary>The value meaning "no floor at all".</summary>
    /// <remarks>
    /// <see cref="double.MinValue"/> rather than negative infinity, because this value
    /// is written to config.json and read back, and JSON has no infinity.
    /// </remarks>
    public const double DefaultMinDbfs = double.MinValue;

    private readonly Dictionary<SoundClass, SoundClassRule> _rules = new();

    /// <summary>The rule for a class, created on first use.</summary>
    public SoundClassRule Rule(SoundClass c)
    {
        if (!_rules.TryGetValue(c, out var r))
        {
            r = new SoundClassRule();
            _rules[c] = r;
        }
        return r;
    }

    public bool IsEnabled(SoundClass c) => Rule(c).Enabled;

    public void SetEnabled(SoundClass c, bool on) => Rule(c).Enabled = on;

    public double FloorFor(SoundClass c) => Rule(c).MinDbfs;

    public void SetFloor(SoundClass c, double dbfs) => Rule(c).MinDbfs = dbfs;

    /// <summary>Whether an event passes. The enabled check comes first, so a class that
    /// is off never has its level looked at.</summary>
    public bool Allows(SoundClass c, double dbfs) => Rule(c).Allows(dbfs);

    /// <summary>Filters a list of events down to the ones that would be shown.</summary>
    public List<AudioEvent> Apply(IEnumerable<AudioEvent> events)
    {
        var kept = new List<AudioEvent>();
        foreach (var e in events)
            if (Allows(e.Class, e.Dbfs)) kept.Add(e);
        return kept;
    }

    public static SoundFilter Default() => new();

    /// <summary>
    /// Only footsteps and gunfire, which is the common request: the two things that kill
    /// you, and nothing else.
    /// </summary>
    public static SoundFilter FootstepsAndGunfire()
    {
        var f = new SoundFilter();
        foreach (var c in AllClasses)
            if (c is not (SoundClass.Footstep or SoundClass.Gunshot)) f.SetEnabled(c, false);
        return f;
    }

    /// <summary>JSON round trip. Rules are keyed by class name.</summary>
    public Dictionary<string, SoundClassRule> Serialise()
    {
        var d = new Dictionary<string, SoundClassRule>();
        foreach (var c in AllClasses)
            if (_rules.ContainsKey(c)) d[c.ToString()] = _rules[c];
        return d;
    }

    public void Deserialise(Dictionary<string, SoundClassRule>? d)
    {
        if (d is null) return;
        foreach (var (name, rule) in d)
            // An unknown name is ignored rather than fatal: a config written by a build
            // that knew about a class this one does not should not stop the app starting.
            if (Enum.TryParse<SoundClass>(name, out var c)) _rules[c] = rule;
    }
}
