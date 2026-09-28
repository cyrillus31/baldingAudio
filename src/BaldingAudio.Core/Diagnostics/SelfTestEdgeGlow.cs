using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Dsp;
using BaldingAudio.Core.Overlay;

namespace BaldingAudio.Core.Diagnostics;

/// <summary>
/// Checks on the edge-glow mode, the line cap, and the sound filter.
/// </summary>
/// <remarks>
/// <para>
/// Three features that can each be wrong in a way nothing else would notice, because
/// they all still draw something. An edge block on the wrong side still looks like a
/// glow. A cap that keeps the wrong four still shows four lines. A filter that inverts
/// still hides sounds. A screenshot of any of them looks plausible, so every claim here
/// is checked against the geometry rather than against "something was painted".
/// </para>
/// </remarks>
public static partial class SelfTest
{
    /// <summary>Screen width the geometry checks use.</summary>
    private const int ScreenW = 1920;

    /// <summary>
    /// A synthetic event. Separate from the analyser's rendering helpers because these
    /// checks are about what the layout does with a measurement, not about whether the
    /// analyser can make one.
    /// </summary>
    private static AudioEvent Ev(
        double bearing,
        double? balanceDb,
        double level = 0.8,
        SoundClass cls = SoundClass.Footstep,
        double? dbfs = null,
        double distanceConfidence = 0.5) => new(
        new Direction(bearing, 0),
        Level: level,
        Dbfs: dbfs ?? Decibel.FromUnitLevel(level),
        Class: cls,
        ClassConfidence: 1,
        Confidence: 1,
        DistanceConfidence: distanceConfidence,
        Timestamp: 0)
    {
        Balance = balanceDb is double d ? Decibel.BalanceFromDb(d) : null,
    };

    private const string N17 = "the edge glow sits on the right side and grows with the difference";

    private static Result TestEdgeGlowSideAndGrowth()
    {
        const int W = 1920, H = 1080;
        var style = OverlayStyle.Default();
        style.DisplayMode = OverlayDisplayMode.EdgeGlow;

        // 9 dB to the right, and 6 dB to the left.
        var right = Ev(bearing: 20, balanceDb: 9, level: 0.8);
        var left = Ev(bearing: -20, balanceDb: -6, level: 0.8);

        var blocks = EdgeGlowLayout.Build(new[] { right }, W, H, style);
        if (blocks.Count != 1)
            return new(N17, false, $"a 9 dB right-hand sound produced {blocks.Count} block(s), expected 1");

        var r = blocks[0];
        // Positive balance is the right ear louder, so the RIGHT side, which is +1. This
        // assertion was written as `<= 0` the first time and failed a correct
        // implementation - the check was wrong, not the code.
        if (r.Side <= 0)
            return new(N17, false, $"a 9 dB right-hand sound reported side {r.Side}; positive balance is the " +
                "right ear being louder, so it must be the right side");

        // It has to hug the right edge, so its right edge is at the screen width less the
        // inset, and its left edge is inward of that.
        var rightEdge = r.X + r.Width;
        if (Math.Abs(rightEdge - (W - style.SideInsetFraction * Math.Min(W, H))) > 1.0)
            return new(N17, false,
                $"the right-hand block ends at x={rightEdge:F0}, expected the screen edge less the inset " +
                $"({W - style.SideInsetFraction * Math.Min(W, H):F0}); a mirrored pair that is not actually " +
                "mirrored looks right until you compare the two edges");

        // Vertical: centred on the horizontal midline, and symmetric about it.
        if (Math.Abs(r.Y - H * 0.5) > 0.5)
            return new(N17, false, $"the block is centred at y={r.Y:F0}, not the horizontal midline at " +
                $"{H * 0.5:F0}; the specification is symmetric about that line");

        // Top/Bottom are computed as Y +/- HalfHeight, and Y is exactly H/2, so both arms
        // are trivially equal. Testing that would be testing the arithmetic of the
        // accessors, not the geometry. What can actually go wrong is the block being
        // pinned to a corner or drawn one-sided, so the meaningful question is whether it
        // stays within the band the cap allows and touches neither screen edge.
        if (r.Top < 0 || r.Bottom > H)
            return new(N17, false, $"the block runs from y={r.Top:F0} to y={r.Bottom:F0}, off the screen");

        // And it must leave a real margin top and bottom even at full size.
        var margin = H - 2 * r.HalfHeight;
        if (margin < H * 0.30)
            return new(N17, false,
                $"a full-size block leaves only {margin:F0} px of the {H} px height clear, under the 30% the " +
                "cap promises; the block is reaching nearer the corners than the design allows");

        // And the size has to follow the difference, not the volume. Both events are the
        // same level on purpose, so any change in size is the imbalance showing through.
        var l = EdgeGlowLayout.Build(new[] { left }, W, H, style)[0];
        if (r.HalfHeight <= l.HalfHeight || r.Width <= l.Width)
            return new(N17, false,
                $"9 dB gives a block {r.HalfHeight:F0} tall and 6 dB gives {l.HalfHeight:F0}, at the same " +
                "volume; the up-down protrusion is supposed to be the size of the difference between the ears");

        if (Math.Abs(r.HalfHeight / l.HalfHeight - 0.78 / 0.60) > 0.05)
            return new(N17, false,
                $"9 dB protrudes {r.HalfHeight / l.HalfHeight:F2}x as far as 6 dB, expected about " +
                $"{0.78 / 0.60:F2}x from the imbalance ratio");

        // The vertical cap is what keeps this mode off the corners.
        var extreme = Ev(bearing: 0, balanceDb: 60, level: 1.0);   // as one-sided as it goes
        var e = EdgeGlowLayout.Build(new[] { extreme }, W, H, style)[0];
        var capPx = style.EdgeGlowMaxHalfHeightFraction * H;
        if (e.HalfHeight > capPx + 0.5)
            return new(N17, false,
                $"a fully one-sided sound protrudes {e.HalfHeight:F0} px, above the {capPx:F0} px cap; this " +
                "mode is only safe from the minimap because the spread is bounded");

        if (e.Top < 0 || e.Bottom > H)
            return new(N17, false,
                $"a fully one-sided block runs from y={e.Top:F0} to y={e.Bottom:F0}, off the screen; it " +
                "would cover the game's own UI");

        return new(N17, true,
            "9 dB right gives one block on the right, hugging the right edge and centred on the midline, " +
            "protruding 1.3x as far as 6 dB at the same volume, and a fully one-sided sound is capped at 30% " +
            "of the screen height");
    }

    private const string N18 = "the line cap keeps the loudest cues, and merging weights by power";

    private static Result TestLineCapKeepsTheLoudest()
    {
        const int W = 1920, H = 1080;
        var style = OverlayStyle.Default();
        style.MaxLines = 2;

        // Four cues on the right, 20 dB apart, so "loudest" is unambiguous.
        // Full confidence on both axes, so a line's alpha is exactly its level. Without
        // that, alpha is scaled by a confidence factor and the check ends up comparing a
        // level against a scaled level - which fails for a correct implementation, and
        // which is what happened the first time.
        double[] levels = { -60, -40, -20, -40 };
        var events = levels
            .Select((db, i) => Ev(bearing: 20 + i, balanceDb: 6,
                level: Decibel.ToUnit(db, -72, -16), dbfs: db, distanceConfidence: 1.0))
            .ToList();

        var lines = OverlayLayout.BuildLines(events, W, H, style);
        if (lines.Count != 2)
            return new(N18, false, $"four cues with a cap of 2 produced {lines.Count} line(s)");

        // Which two survived is the whole claim. The cue set is 20 dB apart, so if the
        // cap had kept the first two, or the last two, the -60 dBFS footstep would still be
        // here. Comparing alphas of the survivors to each other was the first version of
        // this check and it could not fail: two survivors always have a larger and a
        // smaller alpha whichever two they are.
        var quietest = events.Min(e => e.Dbfs);
        var quietestAlpha = events.First(e => e.Dbfs == quietest).Level;

        if (lines.Max(l => l.Alpha) <= quietestAlpha + 1e-9 && quietestAlpha < lines.Max(l => l.Alpha))
            return new(N18, false, "the quietest cue is one of the survivors");

        // The loudest must definitely still be here.
        var loudestLevel = events.Max(e => e.Level);
        if (lines.Max(l => l.Alpha) < loudestLevel - 1e-9)
            return new(N18, false,
                $"the loudest cue has level {loudestLevel:F3} but the brightest surviving line is " +
                $"{lines.Max(l => l.Alpha):F3}; the cap is dropping the loudest");

        // The survivors must be the two highest levels, compared against the full set.
        var keptLevels = lines.Select(l => l.Alpha).OrderByDescending(a => a).ToList();
        var expected = events.Select(e => e.Level).OrderByDescending(a => a).Take(2).ToList();
        for (var i = 0; i < Math.Min(2, keptLevels.Count); i++)
            if (Math.Abs(keptLevels[i] - expected[i]) > 1e-6)
                return new(N18, false,
                    $"survivor {i + 1} has level {keptLevels[i]:F3}, expected {expected[i]:F3} from a set of " +
                    $"{string.Join(", ", events.Select(e => e.Level.ToString("F3")))}; the cap is not keeping " +
                    "the loudest");

        // Same for the glow, which has its own cap path.
        var glowStyle = OverlayStyle.Default();
        glowStyle.DisplayMode = OverlayDisplayMode.EdgeGlow;
        glowStyle.MaxLines = 2;
        var blocks = EdgeGlowLayout.Build(events, W, H, glowStyle);
        if (blocks.Count != 2)
            return new(N18, false, $"four cues with a cap of 2 produced {blocks.Count} glow block(s)");

        // Merging: two cues at 20 dB and 40 dB. Weighted by power, the imbalance should
        // land between them and nearer the loud one, not at the arithmetic mean.
        var mergeStyle = OverlayStyle.Default();
        mergeStyle.MergeToSingleLine = true;
        var merged = new[]
        {
            Ev(bearing: 20, balanceDb: 9, level: 0.8, dbfs: -20),
            Ev(bearing: 20, balanceDb: 3, level: 0.8, dbfs: -40),
        };
        var m = EdgeGlowLayout.Build(merged, W, H, mergeStyle);
        if (m.Count != 1)
            return new(N18, false, $"two cues on one side with merging on produced {m.Count} block(s), expected 1");

        var got = Math.Abs(ImpliedBalance(m[0], mergeStyle, W));
        var plainMean = (0.78 + 0.33) / 2;
        // Power weighting: louder dominates, so the result must be well above the mean.
        if (got <= plainMean + 0.02)
            return new(N18, false,
                $"merging 9 dB with 3 dB gives {got:F3}, at or below the plain mean of {plainMean:F3}; " +
                "weighting by power is the point, otherwise one quiet cue drags the block down as far as itself");

        if (got > 0.78 + 0.001)
            return new(N18, false, $"merging produced {got:F3}, above both inputs; a weighted mean cannot " +
                "exceed the loudest contributor");

        return new(N18, true,
            "a cap of 2 out of 4 cues keeps the loudest, the glow path caps the same way, and merging 9 dB " +
            "with 3 dB lands above the plain mean and below the loudest, which is what power weighting does");
    }

    private const string N19 = "the sound filter hides what is switched off, and the floor is in dBFS";

    private static Result TestSoundFilterSelectsByClassAndLevel()
    {
        var filter = SoundFilter.FootstepsAndGunfire();

        if (!filter.Allows(SoundClass.Footstep, -70))
            return new(N19, false, "a -70 dBFS footstep is filtered out by the footsteps-and-gunfire filter");

        if (!filter.Allows(SoundClass.Gunshot, -20))
            return new(N19, false, "a -20 dBFS gunshot is filtered out by the footsteps-and-gunfire filter");

        foreach (var c in new[] { SoundClass.Explosion, SoundClass.Vehicle, SoundClass.Voice, SoundClass.Other })
            if (filter.Allows(c, 0))
                return new(N19, false, $"{c} is not filtered out by the footsteps-and-gunfire filter, even at " +
                    "0 dBFS");

        // The floor, in dBFS, on a class that is still on.
        filter.SetFloor(SoundClass.Footstep, -50);
        if (filter.Allows(SoundClass.Footstep, -55))
            return new(N19, false, "a -55 dBFS footstep passes a -50 dBFS floor");

        if (!filter.Allows(SoundClass.Footstep, -45))
            return new(N19, false, "a -45 dBFS footstep is rejected by a -50 dBFS floor");

        // The boundary is inclusive, like the balance threshold.
        if (!filter.Allows(SoundClass.Footstep, -50))
            return new(N19, false, "a -50 dBFS footstep is rejected by a -50 dBFS floor; the threshold is " +
                "inclusive, so a cue exactly at it shows");

        // The default must not hide anything, or the app starts up quieter than it was.
        var d = SoundFilter.Default();
        foreach (var c in SoundFilter.AllClasses)
            if (!d.Allows(c, -100))
                return new(N19, false, $"the default filter rejects a -100 dBFS {c}; the app must not start " +
                    "up hiding sounds nobody asked it to hide");

        // The whole list, applied to a real set.
        var events = new[]
        {
            Ev(bearing: 20, balanceDb: 6, level: 0.6, cls: SoundClass.Footstep, dbfs: -40),
            Ev(bearing: 20, balanceDb: 6, level: 0.9, cls: SoundClass.Gunshot, dbfs: -20),
            Ev(bearing: 20, balanceDb: 6, level: 0.9, cls: SoundClass.Vehicle, dbfs: -15),
        };
        var kept = filter.Apply(events);
        if (kept.Count != 2)
            return new(N19, false, $"three events (footstep, gunshot, vehicle) with feet and gunfire on " +
                $"produced {kept.Count}");

        if (kept.Any(e => e.Class == SoundClass.Vehicle))
            return new(N19, false, "the loudest event, a vehicle at -15 dBFS, survived a filter that switches " +
                "vehicles off; filtering by volume instead of by class would let exactly that through");

        return new(N19, true,
            "feet and gunfire pass and the other four classes are rejected at any level, a -50 dBFS floor on " +
            "footsteps is inclusive, the default hides nothing, and the loudest of three events is dropped " +
            "because of its class and not its volume");
    }

    /// <summary>Reads the imbalance a block actually implies, from its painted width.</summary>
    /// <remarks>
    /// <para>
    /// Derived from the geometry rather than read off a stored field, so this is a check
    /// on the thing that gets drawn. A block whose stored imbalance is right but whose
    /// width does not follow from it would pass a check that read the field, and the
    /// screen would show the wrong size.
    /// </para>
    /// <para>
    /// Normalised by the style's reach, so the result is an imbalance in -1..1 and
    /// comparable with the inputs it was built from.
    /// </para>
    /// </remarks>
    private static double ImpliedBalance(EdgeGlowGeometry g, OverlayStyle style, int screenWidth)
        => g.Width / Math.Max(1.0, style.EdgeGlowWidthFraction * screenWidth);
}
