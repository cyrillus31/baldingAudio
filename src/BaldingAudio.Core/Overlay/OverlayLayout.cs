using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Dsp;

namespace BaldingAudio.Core.Overlay;

/// <summary>
/// Geometry and styling for the visual indicator, in normalised 0..1 screen
/// coordinates so it scales with any resolution and DPI.
///
/// Two side scales. Lines run inward from the left and right edges, and height on
/// screen means front/behind:
///
///   |azimuth|        ->  height. 0 (straight ahead) is the top of the band, 90 (to the
///                        side) is the middle, 180 (behind) is the bottom.
///
/// On a two-channel endpoint the edge comes from the left/right imbalance instead, and
/// its length is that same imbalance. See <see cref="OverlayStyle.BalanceFloorDb"/>.
///
/// So a sound in front and to the left starts at the left edge, near the top; one
/// behind and to the left starts at the left edge, near the bottom. Both halves of the
/// direction survive - this is a different decomposition of the bearing, not a coarser
/// one.
///
/// The top and bottom edges are never used. The band is inset, so nothing projects
/// inward from the top or the bottom, and the extremes sit clear of both.
///
/// Because the game pans audio into the player's own frame of reference, "front" and
/// "right" are relative to where the player is looking. Turning rotates the whole
/// display without the overlay needing to know anything about the camera.
///
/// The line's <b>length</b> encodes loudness, and its <b>thickness</b> is constant, so
/// it reads as a line rather than a dot. Nothing is drawn at all when nothing is
/// happening: the indicator appears on a sound and recedes when it stops.
///
/// <para>
/// This replaced a compass drawn on the screen border with radial lines, and before
/// that a 2D field with lines floating around the middle of the screen. The user
/// rejected both: the border version put cues in the corners where the game's own UI
/// lives, and the floating version put every cue in the middle of the view. See
/// <c>AGENTS.md</c>.
/// </para>
/// </summary>
public sealed class OverlayStyle
{
    /// <summary>Maximum line length, as a fraction of screen width. One eighth.</summary>
    public double MaxLengthFraction { get; set; } = 0.125;

    /// <summary>Line thickness, as a fraction of the shorter screen dimension.</summary>
    public double LineThicknessFraction { get; set; } = 0.009;

    /// <summary>Shortest line drawn, so a very faint cue is still visible.</summary>
    public double MinLengthFraction { get; set; } = 0.010;

    /// <summary>
    /// Smallest left/right imbalance worth drawing, -1..1. Below this a cue is not off
    /// to one side, it is simply in front of the player, and drawing it would claim a
    /// side that isn't there.
    ///
    /// <para>
    /// Read and set this through <see cref="BalanceFloorDb"/>. The raw number is an
    /// energy ratio, so 0.1 is not "a tenth off to one side", it is 0.87 dB between the
    /// ears - below the noise of most rooms, which is why it drew things in a silent
    /// space. The default is 3 dB, which is where a level difference starts to be
    /// reliably localisable and is a third of what it was.
    /// </para>
    ///
    /// <para>
    /// Measured on the running app by panning a test signal across the endpoint:
    /// 1 dB reads 0.11, 2 dB 0.23, 3 dB 0.33, 4 dB 0.43, 6 dB 0.60, 9 dB 0.78. Those
    /// agree with the closed form (g-1)/(g+1) for g = 10^(dB/10) to three decimals, which
    /// is the check that the analyser compares energy and the conversion does too.
    /// </para>
    ///
    /// <para>
    /// Does not apply in multichannel mode, where a speaker's position is not an
    /// inference and there is nothing to be uncertain about.
    /// </para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double BalanceFloor { get; set; } = Decibel.BalanceFromDb(DefaultBalanceFloorDb);

    /// <summary>
    /// The same threshold in decibels, which is the unit that means something: how much
    /// louder one ear has to be before a line is drawn. 3 dB by default.
    ///
    /// <para>
    /// Offered as dB because 0.33 is not a quantity a person can judge by ear, and
    /// because the response is steep near zero - 0.05 to 0.15 covers 0.45 dB to 1.4 dB,
    /// so a slider over the raw balance spends most of its travel in a range where
    /// nothing visible happens.
    /// </para>
    ///
    /// <para>
    /// This is the only form that reaches config.json. Both were serialised once, and
    /// the loader applied them in document order, so whichever came second silently won
    /// - a file written by the previous build held both 0.1 and 0.87 and the app ran at
    /// 0.87 dB while every log line and every screenshot said 0.1. Two names for one
    /// number is how a threshold ends up disagreeing with itself.
    /// </para>
    ///
    /// <para>
    /// A config written before this existed keeps its "balanceFloor" key, which is now
    /// ignored. That key only ever held 0.1, the over-sensitive default being fixed
    /// here, so nothing a person chose by hand is lost.
    /// </para>
    /// </summary>
    public double BalanceFloorDb
    {
        get => Decibel.DbFromBalance(BalanceFloor);
        set => BalanceFloor = Decibel.BalanceFromDb(value);
    }

    /// <summary>3 dB between the ears. See <see cref="BalanceFloorDb"/>.</summary>
    public const double DefaultBalanceFloorDb = 3.0;

    /// <summary>
    /// Blank margin kept between the top and bottom of the screen and the ends of the
    /// side scales, as a fraction of the shorter screen dimension.
    ///
    /// This is why nothing is ever drawn at the very top or the very bottom: the
    /// vertical range is inset, so "straight ahead" is the top of the band rather than
    /// the top edge of the screen. A fraction rather than a pixel count, so it survives
    /// a resolution change.
    /// </summary>
    public double FieldInsetFraction { get; set; } = 0.06;

    /// <summary>
    /// How far each line starts from its side edge, as a fraction of the shorter screen
    /// dimension. Small by default, so lines read as growing in from the edge.
    ///
    /// This is the knob for the corner problem. Games put their minimap and ammo counter
    /// in the bottom corners, and lines starting hard at the edge would run under them.
    /// Raising this pulls every line's outer end in from the edge, and nothing else
    /// changes.
    /// </summary>
    public double SideInsetFraction { get; set; } = 0.01;

    /// <summary>
    /// How much of the available height the scales span, 0..1. 1 puts straight ahead at
    /// the top of the band and directly behind at the bottom; smaller pulls both toward
    /// the middle of the screen.
    /// </summary>
    public double FieldRadiusYFraction { get; set; } = 0.84;

    /// <summary>
    /// Width of the outline drawn around each line, as a fraction of the line's own
    /// thickness. 0 disables it.
    ///
    /// This is a visibility fix, not a new look. A single flat line vanishes against a
    /// dark scene: Battlefield's interiors and night maps are mostly dark, and a
    /// semi-transparent red line over near-black reads as nothing at all. A second,
    /// slightly wider copy underneath in a lighter version of the same colour gives the
    /// line an edge, and an edge is what separates a shape from its background.
    ///
    /// Deliberately a halo of the same hue rather than a black bar with a white
    /// outline: the user asked for two similar colours, not for a high-contrast frame.
    /// The outline is drawn first and the fill over it, so the visible result is a
    /// rounded line with a soft rim - the geometry is unchanged, only the paint.
    /// </summary>
    public double OutlineWidthFraction { get; set; } = 0.55;

    /// <summary>
    /// How far the outline colour is pushed toward white, 0..1. Keeps the rim the same
    /// hue as the fill so the pair reads as one object.
    /// </summary>
    public double OutlineLighten { get; set; } = 0.55;

    /// <summary>Extra opacity on the outline, relative to the line's own alpha.</summary>
    public double OutlineAlphaGain { get; set; } = 1.0;

    /// <summary>How cues are drawn. See <see cref="OverlayDisplayMode"/>.</summary>
    public OverlayDisplayMode DisplayMode { get; set; } = OverlayDisplayMode.Lines;

    /// <summary>
    /// The colour every cue uses, whichever class it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set by <see cref="SetAllColours"/>, and the single-colour case is kept as a real
    /// property rather than by writing all six class slots from the settings window. The
    /// six per-class slots were all identical to begin with, so a "colour" setting that
    /// quietly meant "set all six" was a trap: it looked like one control and was really
    /// six, and any future divergence between them would leave the control lying.
    /// </para>
    /// <para>
    /// A cue's colour on screen is still the per-class one, so a future per-class
    /// override is a change in one place rather than a change in how colour is stored.
    /// </para>
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public Rgba IndicatorColour
    {
        get => For(SoundClass.Footstep);
        set => SetAllColours(value);
    }

    /// <summary>Sets every class's colour to the same value.</summary>
    public void SetAllColours(Rgba c)
    {
        Footstep = c;
        Gunshot = c;
        Explosion = c;
        Vehicle = c;
        Voice = c;
        Other = c;
    }

    /// <summary>
    /// The most cues drawn at once, across both sides. 0 is no limit.
    /// </summary>
    /// <remarks>
    /// Loudest kept when the limit bites, so the cap drops the quietest rather than
    /// whichever happened to be drawn first. A user in a firefight wants the gunshots,
    /// not the first four footsteps that started talking.
    /// </remarks>
    public int MaxLines { get; set; } = 0;

    /// <summary>
    /// Collapse everything on a side into one cue instead of one line each.
    /// </summary>
    /// <remarks>
    /// Offered because a busy fight can put a dozen cues up at once and the display
    /// becomes a solid block that says nothing about which one mattered. The merged cue
    /// is weighted by power and reports the loudest level, so one gunshot does not get
    /// averaged away by five footsteps.
    /// </remarks>
    public bool MergeToSingleLine { get; set; }

    /// <summary>The neon hue for <see cref="OverlayDisplayMode.EdgeGlow"/>, both edges.</summary>
    public Rgba EdgeGlow { get; set; } = new(0x39, 0xFF, 0x9E, 0xD0);

    /// <summary>How far an edge block reaches inward, as a fraction of screen width.</summary>
    public double EdgeGlowWidthFraction { get; set; } = 0.045;

    /// <summary>
    /// The largest the vertical protrusion may get, as a fraction of screen height.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cap is what keeps this mode away from the rejected first design. That one put
    /// rear sounds in the bottom corners, over the minimap. Nothing here is placed by
    /// front-versus-behind, so a rear sound lands at the same height as a front one - but
    /// a very large imbalance would still spread until it reached the corners, so the
    /// spread is bounded instead of left to run to the edge.
    /// </para>
    /// <para>
    /// 0.30 of the height means the block spans at most 60% of the screen vertically,
    /// leaving a fifth clear at the top and the bottom even at a full one-sided sound.
    /// </para>
    /// </remarks>
    public double EdgeGlowMaxHalfHeightFraction { get; set; } = 0.30;

    /// <summary>Line thickness in pixels, before any outline.</summary>
    public double ThicknessFor(int screenWidth, int screenHeight)
        => Math.Max(3.0, LineThicknessFraction * Math.Min(screenWidth, screenHeight));

    /// <summary>
    /// Half the total painted height of a line, outline included.
    ///
    /// The band radius is computed from this rather than from the fill thickness, so the
    /// margin the layout promises from the top and bottom edges is measured against what
    /// is actually on screen. Using the fill thickness alone would let the rim of the
    /// frontmost and rearmost cues cross the margin the geometry is built to keep.
    /// </summary>
    public double PaintedHalfThickness(int screenWidth, int screenHeight)
        => ThicknessFor(screenWidth, screenHeight) * (0.5 + Math.Max(0.0, OutlineWidthFraction));

    /// <summary>
    /// The outline colour for a line: the same hue, lighter, optionally more opaque.
    /// The line's own alpha is applied by the renderer as with the fill, so this only
    /// deals with the colour and the base opacity of the class colour.
    /// </summary>
    public Rgba OutlineFor(Rgba fill)
        => fill
            .Lighten(Math.Clamp(OutlineLighten, 0.0, 1.0))
            .ScaleAlpha(Math.Max(0.0, OutlineAlphaGain));

    /// <summary>
    /// All cues share one colour for now. Colour is not carrying information; the
    /// per-class entries exist so it can be switched on later without touching the
    /// renderer.
    /// </summary>
    public Rgba Footstep { get; set; } = Indicator;
    public Rgba Gunshot { get; set; } = Indicator;
    public Rgba Explosion { get; set; } = Indicator;
    public Rgba Vehicle { get; set; } = Indicator;
    public Rgba Voice { get; set; } = Indicator;
    public Rgba Other { get; set; } = Indicator;

    private static Rgba Indicator => new(0xFF, 0x3B, 0x3B, 0xD6);

    public Rgba For(SoundClass cls) => cls switch
    {
        SoundClass.Footstep => Footstep,
        SoundClass.Gunshot => Gunshot,
        SoundClass.Explosion => Explosion,
        SoundClass.Vehicle => Vehicle,
        SoundClass.Voice => Voice,
        _ => Other,
    };

    public static OverlayStyle Default() => new();
}

/// <summary>Straight 8-bit RGBA, non-premultiplied.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    public Rgba WithAlpha(byte a) => this with { A = a };

    public Rgba ScaleAlpha(double f) => this with { A = (byte)Math.Clamp(A * f, 0, 255) };

    /// <summary>
    /// Moves the colour <paramref name="f"/> of the way toward white, keeping the hue.
    ///
    /// Used for the line outline. Interpolating toward white rather than scaling the RGB
    /// channels up is what keeps a red outline red instead of turning it pink: a
    /// multiplier would clip the red channel to 255 immediately and leave only green and
    /// blue to grow.
    /// </summary>
    public Rgba Lighten(double f)
    {
        f = Math.Clamp(f, 0.0, 1.0);
        static byte Mix(byte c, double f) => (byte)Math.Round(c + (255 - c) * f);
        return new Rgba(Mix(R, f), Mix(G, f), Mix(B, f), A);
    }
}

/// <summary>A line ready to be drawn, in pixels. The anchor is on the screen border.</summary>
public readonly record struct LineGeometry(
    double X,
    double Y,
    double Dx,
    double Dy,
    double Length,
    double Thickness,
    double Alpha,
    Rgba Colour,
    SoundClass Class,
    double Level,
    double Azimuth);

/// <summary>
/// Converts <see cref="AudioEvent"/>s into line segments.
/// Kept free of any drawing code so the layout can be unit tested.
/// </summary>
public static class OverlayLayout
{
    /// <summary>
    /// Maps a direction to a vertical position, 0 at dead ahead and 1 directly behind.
    /// Still used to decide which cues merge into one line.
    /// </summary>
    public static double FrontBackToVertical(double azimuthDegrees)
        => Math.Clamp(Math.Abs(azimuthDegrees) / 180.0, 0.0, 1.0);

    /// <summary>
    /// Which third of the compass a direction belongs to: 0 left, 1 right, 2 dead
    /// ahead. Used only to keep merging from crossing the centre.
    /// </summary>
    public static int Rail(double azimuthDegrees)
        => azimuthDegrees < -8 ? 0 : azimuthDegrees > 8 ? 1 : 2;

    /// <summary>
    /// Finds where a line starts on the screen, and which way it runs inward.
    ///
    /// A bearing is encoded as (sign, magnitude) rather than as a point on a circle:
    ///
    ///   side    the sign of the bearing. Negative is the left edge, positive the right,
    ///           and the line runs inward from there. Exactly 0 - dead ahead - goes to
    ///           the right, since it has no side of its own.
    ///   height  |bearing|, 0 at the top of the band and 180 at the bottom. So the closer
    ///           to the top, the more in front the sound is, and the closer to the
    ///           bottom, the more behind.
    ///
    /// Both halves survive, so nothing is lost: this is a different decomposition of the
    /// same bearing, not a coarser one. The top and bottom edges are never used, because
    /// the band is inset by <see cref="OverlayStyle.FieldInsetFraction"/>, so a cue
    /// straight ahead is a line near the top - never a line hanging off the top edge.
    /// </summary>
    public static void EdgeAnchor(
        int screenWidth,
        int screenHeight,
        double azimuthDegrees,
        OverlayStyle style,
        out double x,
        out double y,
        out double dx,
        out double dy)
    {
        var toLeft = azimuthDegrees < 0;
        var magnitude = Math.Abs(azimuthDegrees);

        var shortest = Math.Min(screenWidth, screenHeight);
        var inset = style.FieldInsetFraction * shortest;
        var sideInset = style.SideInsetFraction * shortest;

        // Includes the outline. The band is inset from the top and bottom so nothing
        // reaches those edges, and that promise is only true of the pixels actually
        // painted - the rim of a line extends past its fill.
        var halfThickness = style.PaintedHalfThickness(screenWidth, screenHeight);

        // Centre the band vertically, then scale it about that centre, so shrinking the
        // radius pulls both extremes toward the middle of the screen.
        var cy = screenHeight * 0.5;
        var bandRadius = Math.Max(0.0, cy - inset - halfThickness) * style.FieldRadiusYFraction;

        y = cy - Math.Cos(magnitude * Math.PI / 180.0) * bandRadius;
        x = toLeft ? sideInset : screenWidth - sideInset;

        // Inward, and always horizontal.
        dx = toLeft ? 1.0 : -1.0;
        dy = 0.0;
    }

    /// <summary>Maps loudness (0..1) to line length in pixels.</summary>
    public static double LengthForLevel(double level, int screenWidth, OverlayStyle style)
    {
        var minLen = style.MinLengthFraction * screenWidth;
        var maxLen = style.MaxLengthFraction * screenWidth;
        return minLen + Math.Clamp(level, 0.0, 1.0) * (maxLen - minLen);
    }

    /// <summary>
    /// Maps a signed left/right imbalance to line length in pixels: 0 at dead even,
    /// <see cref="OverlayStyle.MaxLengthFraction"/> of the width at fully one-sided.
    ///
    /// <para>
    /// Starts at zero and not at <see cref="OverlayStyle.MinLengthFraction"/>, unlike
    /// <see cref="LengthForLevel"/>. That minimum length is a legibility floor for a
    /// line that is being drawn - a faint footstep still has to be a visible dash - but
    /// here a short bar means "barely off to one side", and giving it a floor would
    /// turn every centred sound into a stub on both edges, which is the clutter this
    /// model exists to remove. A sound that is not lopsided draws nothing at all.
    /// </para>
    /// </summary>
    public static double LengthForBalance(double balance, int screenWidth, OverlayStyle style)
    {
        var maxLen = style.MaxLengthFraction * screenWidth;
        return Math.Clamp(Math.Abs(balance), 0.0, 1.0) * maxLen;
    }

    /// <summary>Builds all lines for a set of events at a given screen size.</summary>
    public static List<LineGeometry> BuildLines(
        IReadOnlyList<AudioEvent> events,
        int screenWidth,
        int screenHeight,
        OverlayStyle style)
    {
        var result = new List<LineGeometry>();
        if (screenWidth <= 0 || screenHeight <= 0) return result;

        var thickness = Math.Max(3.0, style.LineThicknessFraction * Math.Min(screenWidth, screenHeight));

        // Merging, when on, collapses each side to a single cue before anything is laid
        // out. Done first so the cap and the merge cannot both apply to the same set and
        // the two settings can contradict each other.
        if (style.MergeToSingleLine)
        {
            foreach (var side in new[] { -1, 1 })
            {
                var onSide = events.Where(e => Math.CopySign(1.0, (e.Balance ?? side)) == side).ToList();
                if (onSide.Count > 0)
                    AddLine(result, side * 90, Brightness(onSide), onSide[0],
                        LengthForLevel(Brightness(onSide), screenWidth, style), thickness,
                        screenWidth, screenHeight, style);
            }

            ApplyCap(result, style);
            return result;
        }

        foreach (var e in events)
        {
            // Faint readings are dimmed, so an unreliable direction is visibly less
            // assertive than a confident one.
            var confidence = 0.35 + 0.65 * Math.Clamp(e.Confidence, 0.0, 1.0);
            var distanceConfidence = 0.45 + 0.55 * Math.Clamp(e.DistanceConfidence, 0.0, 1.0);
            var alpha = Math.Clamp(e.Level, 0.0, 1.0) * confidence * distanceConfidence;

            if (e.Balance is double balance)
            {
                // Stereo: the edge and the length both come from the imbalance between
                // the channels, and the height still comes from the bearing.
                //
                // A sound that is not off to one side draws nothing, because the sign of
                // its bearing is noise out here and the difference between the ears is
                // the only honest thing to show. The old behaviour - mirror it onto both
                // edges - lit both sides at equal intensity for everything uncertain, so
                // a grenade off to the right looked identical to a grenade in your face.
                //
                // The exact-zero case is separate, and not a refinement of the threshold.
                // A floor of 0 dB turns the floor into the balance 0.0, and a strict
                // comparison lets 0.0 through - so a perfectly even mix, which has no
                // side at all, drew a line claiming one. Zero is the one value that is
                // not a matter of degree: it is the absence of the thing being measured.
                // Checked before the floor so no threshold setting can bring it back.
                if (balance == 0.0) continue;
                if (Math.Abs(balance) < style.BalanceFloor) continue;

                var side = Math.CopySign(1.0, balance);
                AddLine(result, side * Math.Abs(e.Direction.AzimuthDegrees), alpha, e,
                    LengthForBalance(balance, screenWidth, style), thickness,
                    screenWidth, screenHeight, style);
                continue;
            }

            // Multichannel: every speaker has a real bearing, so the sign of the bearing
            // is a measurement and length can carry loudness as before.
            AddLine(result, e.Direction.AzimuthDegrees, alpha, e,
                LengthForLevel(e.Level, screenWidth, style), thickness,
                screenWidth, screenHeight, style);
        }

        ApplyCap(result, style);
        return result;
    }

    /// <summary>
    /// Drops the quietest cues once there are more than <see cref="OverlayStyle.MaxLines"/>.
    /// </summary>
    /// <remarks>
    /// Applied after layout, to the alpha rather than the input level, so it ranks by what
    /// is actually painted. <c>Level</c> alone would misrank: a loud cue with poor
    /// directional confidence is painted fainter than a quiet confident one, and the user
    /// is comparing the two shapes they can see.
    /// </remarks>
    private static void ApplyCap(List<LineGeometry> result, OverlayStyle style)
    {
        var cap = style.MaxLines;
        if (cap <= 0 || result.Count <= cap) return;

        // Loudest first, so the survivors keep their original relative order below.
        var keep = result.OrderByDescending(l => l.Alpha).Take(cap).ToHashSet();
        result.RemoveAll(l => !keep.Contains(l));
    }

    /// <summary>
    /// A representative level for a merged side: the loudest cue's, not an average.
    /// </summary>
    /// <remarks>
    /// Averaging dB values is meaningless - the decibel scale is logarithmic, so the mean
    /// of -20 and -40 dBFS is not the level of two sounds together. Taking the loudest
    /// says "this is how much attention this side wants", which is what a merged cue is
    /// for. The weight that <see cref="EdgeGlowLayout"/> applies to the imbalance is a
    /// separate question and is a proper power-weighted mean there, because an imbalance
    /// is a ratio and ratios do combine.
    /// </remarks>
    private static double Brightness(IReadOnlyList<AudioEvent> events)
    {
        var best = 0.0;
        foreach (var e in events)
            if (e.Level > best) best = e.Level;
        return best;
    }

    private static void AddLine(
        List<LineGeometry> result,
        double azimuthDegrees,
        double alpha,
        AudioEvent e,
        double length,
        double thickness,
        int screenWidth,
        int screenHeight,
        OverlayStyle style)
    {
        EdgeAnchor(
            screenWidth, screenHeight, azimuthDegrees, style,
            out var ax, out var ay, out var dx, out var dy);

        // (ax, ay) is the outer end at the side edge, and dx points inward, so the
        // capsule is drawn from the edge toward the middle.
        result.Add(new LineGeometry(
            Math.Round(ax, 2), Math.Round(ay, 2), Math.Round(dx, 6), Math.Round(dy, 6),
            Math.Round(length), Math.Round(thickness),
            alpha, style.For(e.Class), e.Class, e.Level, azimuthDegrees));
    }
}
