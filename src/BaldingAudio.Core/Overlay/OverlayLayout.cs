using BaldingAudio.Core.Audio;

namespace BaldingAudio.Core.Overlay;

/// <summary>
/// Geometry and styling for the visual indicator, in normalised 0..1 screen
/// coordinates so it scales with any resolution and DPI.
///
/// The display is a compass drawn on the border of the screen. Each cue owns a
/// position on that border, chosen by which way the sound came from, and a line that
/// reaches inward from the border toward the centre of the screen:
///
///   dead ahead  ->  top edge
///   to the right ->  right edge
///   behind you  ->  bottom edge
///   to the left  ->  left edge
///
/// Anything in between lands in between, so "slightly right and forward" sits on the
/// top edge toward the right, and "behind and to the left" sits on the bottom edge
/// toward the left. Because a wide screen's corners are reached at about 150 degrees
/// rather than 135, the familiar bottom-left and bottom-right corners are where rear
/// flankers appear.
///
/// Because the game pans audio into the player's own frame of reference, "front" and
/// "right" are relative to where the player is looking. Turning rotates the whole
/// compass without the overlay needing to know anything about the camera.
///
/// The line's <b>length</b> encodes loudness, and its <b>thickness</b> is constant, so
/// it reads as a line rather than a dot. Nothing is drawn at all when nothing is
/// happening: the indicator appears on a sound and recedes when it stops.
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
    /// Alpha of the outer end of a line, as a fraction of the inner end. Below 1 so the
    /// line brightens as it reaches inward, which reads as movement off the border.
    /// </summary>
    public double BorderFade { get; set; } = 0.45;

    /// <summary>Bright cap at the inner tip, which makes the reach unambiguous.</summary>
    public bool ShowTipCap { get; set; } = true;

    /// <summary>Fraction of the line's length used by the bright tip cap.</summary>
    public double TipCapFraction { get; set; } = 0.30;

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
    /// Finds the point where a bearing from the centre of the screen meets the border,
    /// plus the unit vector from that point back toward the centre.
    ///
    /// Azimuth 0 is the top edge, +90 the right edge, 180 the bottom edge, -90 the left.
    /// </summary>
    public static void BorderAnchor(
        int screenWidth,
        int screenHeight,
        double azimuthDegrees,
        double inset,
        out double x,
        out double y,
        out double dx,
        out double dy)
    {
        var rad = azimuthDegrees * Math.PI / 180.0;
        var ux = Math.Sin(rad);
        var uy = -Math.Cos(rad); // screen y grows downward, so -cos puts 0 at the top

        var cx = screenWidth * 0.5;
        var cy = screenHeight * 0.5;
        var hw = Math.Max(1.0, cx - inset);
        var hh = Math.Max(1.0, cy - inset);

        // How far the centre is from the border along this bearing.
        var toVerticalEdge = Math.Abs(ux) < 1e-9 ? double.PositiveInfinity : hw / Math.Abs(ux);
        var toHorizontalEdge = Math.Abs(uy) < 1e-9 ? double.PositiveInfinity : hh / Math.Abs(uy);
        var t = Math.Min(toVerticalEdge, toHorizontalEdge);

        x = cx + ux * t;
        y = cy + uy * t;
        dx = -ux;
        dy = -uy;
    }

    /// <summary>Maps loudness (0..1) to line length in pixels.</summary>
    public static double LengthForLevel(double level, int screenWidth, OverlayStyle style)
    {
        var minLen = style.MinLengthFraction * screenWidth;
        var maxLen = style.MaxLengthFraction * screenWidth;
        return minLen + Math.Clamp(level, 0.0, 1.0) * (maxLen - minLen);
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

        // Sit the anchor half a thickness inside the border so the rounded end cap is
        // not clipped by the screen edge.
        var inset = thickness * 0.5;

        foreach (var e in events)
        {
            BorderAnchor(
                screenWidth, screenHeight, e.Direction.AzimuthDegrees, inset,
                out var ax, out var ay, out var dx, out var dy);

            var length = LengthForLevel(e.Level, screenWidth, style);

            // Faint readings are dimmed, so an unreliable direction is visibly less
            // assertive than a confident one.
            var confidence = 0.35 + 0.65 * Math.Clamp(e.Confidence, 0.0, 1.0);
            var distanceConfidence = 0.45 + 0.55 * Math.Clamp(e.DistanceConfidence, 0.0, 1.0);
            var alpha = Math.Clamp(e.Level, 0.0, 1.0) * confidence * distanceConfidence;

            result.Add(new LineGeometry(
                Math.Round(ax, 2), Math.Round(ay, 2), Math.Round(dx, 6), Math.Round(dy, 6),
                Math.Round(length), Math.Round(thickness),
                alpha, style.For(e.Class), e.Class, e.Level, e.Direction.AzimuthDegrees));
        }

        return result;
    }
}
