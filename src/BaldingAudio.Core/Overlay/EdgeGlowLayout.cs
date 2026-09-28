using BaldingAudio.Core.Audio;

namespace BaldingAudio.Core.Overlay;

/// <summary>How a cue is drawn.</summary>
public enum OverlayDisplayMode
{
    /// <summary>Horizontal lines running inward from the left and right edges. Default.</summary>
    Lines = 0,

    /// <summary>
    /// A neon block on the left or right edge, centred on the screen's horizontal
    /// midline, growing symmetrically up and down and inward with the size of the
    /// difference between the ears.
    /// </summary>
    EdgeGlow = 1,
}

/// <summary>
/// The vertical edge blocks used by <see cref="OverlayDisplayMode.EdgeGlow"/>.
/// </summary>
/// <param name="X">Left edge of the block, in pixels.</param>
/// <param name="Y">Centre of the block on the vertical axis.</param>
/// <param name="Width">How far it reaches inward from the edge.</param>
/// <param name="HalfHeight">Half its height, so it is symmetric about the midline.</param>
/// <param name="Alpha">0..1.</param>
/// <param name="Colour">The neon hue.</param>
/// <param name="Side">-1 left, +1 right.</param>
public readonly record struct EdgeGlowGeometry(
    double X,
    double Y,
    double Width,
    double HalfHeight,
    double Alpha,
    Rgba Colour,
    double Side)
{
    public double Top => Y - HalfHeight;
    public double Bottom => Y + HalfHeight;
}

/// <summary>
/// Builds the edge-glow geometry from measured imbalances.
/// </summary>
/// <remarks>
/// <para>
/// The user's specification, in full: this is for a two-channel endpoint only, so the
/// vertical axis carries no information at all. The block is symmetric about the screen's
/// horizontal midline, and the size of the up-down protrusion shows the size of the
/// difference between the ears. One neon hue on both edges, mirrored.
/// </para>
///
/// <para>
/// Which means the earlier concern about the bottom corners does not arise from this
/// mode: nothing here is placed by front-versus-behind, so a rear sound lands at the
/// same height as a front one. What can still reach a corner is a very large imbalance,
/// because half-height is capped by <see cref="OverlayStyle.EdgeGlowMaxHalfHeightFraction"/>
/// rather than being allowed to run to the edge. That cap is why the default is 0.30 of
/// the half-height and why the slider stops well short of 0.5.
/// </para>
///
/// <para>
/// Pure geometry, like <see cref="OverlayLayout"/>, so it is unit tested headless.
/// </para>
/// </remarks>
public static class EdgeGlowLayout
{
    /// <summary>
    /// Builds the blocks for a set of events, loudest first, honouring the line cap.
    /// </summary>
    /// <param name="events">Candidate events.</param>
    /// <param name="screenWidth">Screen width in pixels.</param>
    /// <param name="screenHeight">Screen height in pixels.</param>
    /// <param name="style">Geometry and colour.</param>
    /// <returns>
    /// One block per visible event, or one merged block per side when
    /// <see cref="OverlayStyle.MergeToSingleLine"/> is set.
    /// </returns>
    public static List<EdgeGlowGeometry> Build(
        IReadOnlyList<AudioEvent> events,
        int screenWidth,
        int screenHeight,
        OverlayStyle style)
    {
        var result = new List<EdgeGlowGeometry>();
        if (screenWidth <= 0 || screenHeight <= 0) return result;

        var shortest = Math.Min(screenWidth, screenHeight);

        // Inward reach, and the cap on the vertical spread.
        var maxWidth = Math.Max(1.0, style.EdgeGlowWidthFraction * screenWidth);
        var maxHalf = Math.Max(1.0, style.EdgeGlowMaxHalfHeightFraction * screenHeight);
        var sideInset = style.SideInsetFraction * shortest;
        var mid = screenHeight * 0.5;

        // Split by side first, so merging and the cap happen within a side and a cue can
        // never be counted once on the left and once on the right.
        var left = new List<AudioEvent>();
        var right = new List<AudioEvent>();
        foreach (var e in events)
        {
            // Balance is null in multichannel, where there is no left/right difference
            // to show. This mode has nothing to draw there, and the app falls back to
            // lines rather than pretending.
            if (e.Balance is not double b) continue;
            if (b == 0.0) continue;
            if (Math.Abs(b) < style.BalanceFloor) continue;

            // Sign convention: negative balance is the left ear louder, so the LEFT
            // side. The same convention as the line model, where a negative balance
            // anchors at the left edge. Getting this backwards is the one fault that
            // a screenshot cannot show, because both edges look identical.
            if (b < 0) left.Add(e); else right.Add(e);
        }

        if (style.MergeToSingleLine)
        {
            AddMerged(result, left, -1, mid, sideInset, maxWidth, maxHalf, screenWidth, style);
            AddMerged(result, right, 1, mid, sideInset, maxWidth, maxHalf, screenWidth, style);
            return result;
        }

        var cap = style.MaxLines;
        AddCapped(result, left, -1, mid, sideInset, maxWidth, maxHalf, cap, screenWidth, style);
        AddCapped(result, right, 1, mid, sideInset, maxWidth, maxHalf, cap, screenWidth, style);
        return result;
    }

    private static void AddCapped(
        List<EdgeGlowGeometry> result,
        List<AudioEvent> side,
        double edge,
        double mid,
        double sideInset,
        double maxWidth,
        double maxHalf,
        int cap,
        int screenWidth,
        OverlayStyle style)
    {
        if (side.Count == 0) return;

        // Loudest first, so when the cap bites it keeps the cues that matter. Sorting on
        // Dbfs rather than Level: Level is already dB-mapped through a floor and a
        // ceiling, and Dbfs is the number the filter and the log both speak.
        side.Sort(static (a, b) => b.Dbfs.CompareTo(a.Dbfs));

        var take = cap <= 0 ? side.Count : Math.Min(cap, side.Count);
        for (var i = 0; i < take; i++)
            result.Add(Geometry(side[i], edge, mid, sideInset, maxWidth, maxHalf, screenWidth, style));
    }

    private static void AddMerged(
        List<EdgeGlowGeometry> result,
        List<AudioEvent> side,
        double edge,
        double mid,
        double sideInset,
        double maxWidth,
        double maxHalf,
        int screenWidth,
        OverlayStyle style)
    {
        if (side.Count == 0) return;

        // One block, weighted by how loud each cue is. Weighting by power rather than by
        // dB is deliberate: adding dB values is meaningless, and an unweighted mean
        // would let one quiet cue drag a block down as far as itself.
        var sum = 0.0;
        var weighted = 0.0;
        double? balance = null;
        var level = 0.0;
        var loudest = double.NegativeInfinity;

        foreach (var e in side)
        {
            var w = Math.Pow(10.0, e.Dbfs / 20.0);
            sum += w;
            if (e.Balance is double b)
            {
                weighted += b * w;
                level = Math.Max(level, e.Level);
                loudest = Math.Max(loudest, e.Dbfs);
            }
        }

        if (sum <= 0) return;
        balance = weighted / sum;

        // The merged block reports the loudest cue's level, not an average, so a wall of
        // gunfire does not read as one quiet sound.
        var merged = new AudioEvent(
            Direction.Forward,
            Level: level,
            Dbfs: loudest,
            Class: SoundClass.Other,
            ClassConfidence: 0,
            Confidence: 1,
            DistanceConfidence: 0,
            Timestamp: 0)
        {
            Balance = balance,
        };

        result.Add(Geometry(merged, edge, mid, sideInset, maxWidth, maxHalf, screenWidth, style));
    }

    private static EdgeGlowGeometry Geometry(
        AudioEvent e,
        double edge,
        double mid,
        double sideInset,
        double maxWidth,
        double maxHalf,
        int screenWidth,
        OverlayStyle style)
    {
        var balance = Math.Abs(e.Balance ?? 0.0);
        var width = balance * maxWidth;
        var half = balance * maxHalf;

        // Mirrored, so both blocks hug their own edge. The left one starts just inside
        // the left edge and grows rightwards; the right one ends just inside the right
        // edge and grows leftwards, which means its X is the screen width less the
        // inset less the width it has already taken.
        var left = edge < 0;
        var x = left ? sideInset : screenWidth - sideInset - width;

        return new EdgeGlowGeometry(
            X: x,
            Y: mid,
            Width: width,
            HalfHeight: half,
            Alpha: Math.Clamp(e.Level, 0.0, 1.0),
            Colour: style.EdgeGlow,
            Side: edge);
    }
}
