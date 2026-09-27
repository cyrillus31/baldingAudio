using BaldingAudio.Core.Audio;

namespace BaldingAudio.Core.Overlay;

/// <summary>
/// Geometry and styling for the visual indicator, in normalised 0..1 screen
/// coordinates so it scales with any resolution and DPI.
///
/// Layout follows the requested design:
///  * A sound on the left produces a bar anchored at the left edge, growing right,
///    toward the centre of the screen. A sound on the right mirrors it.
///  * The bar's <b>vertical</b> position encodes front-to-back: dead ahead sits at the
///    top of the indicator, directly behind at the bottom.
///  * The bar's <b>length</b> encodes loudness.
///  * The bar's <b>thickness</b> is constant, so it reads as a bar rather than a dot.
/// </summary>
public sealed class OverlayStyle
{
    /// <summary>Horizontal inset of the left and right rails, as a fraction of screen width.</summary>
    public double EdgeInsetFraction { get; set; } = 0.045;

    /// <summary>Maximum bar length, as a fraction of screen width. Keeps bars off the crosshair.</summary>
    public double MaxLengthFraction { get; set; } = 0.30;

    /// <summary>Bar thickness, as a fraction of screen height.</summary>
    public double BarThicknessFraction { get; set; } = 0.016;

    /// <summary>Vertical span used for the front-to-back axis, as a fraction of screen height.</summary>
    public double VerticalSpanFraction { get; set; } = 0.62;

    /// <summary>Minimum length so a faint but real cue is still visible.</summary>
    public double MinLengthFraction { get; set; } = 0.012;

    /// <summary>Spacing of the horizontal guide lines, 0 = off.</summary>
    public bool ShowGuideLines { get; set; } = true;

    /// <summary>Alpha of the guide lines, 0..1.</summary>
    public double GuideLineAlpha { get; set; } = 0.10;

    public Rgba Footstep { get; set; } = new(0x35, 0xE0, 0x8A, 0xC8);
    public Rgba Gunshot { get; set; } = new(0xFF, 0x4A, 0x4A, 0xE0);
    public Rgba Explosion { get; set; } = new(0xFF, 0x9E, 0x33, 0xE0);
    public Rgba Vehicle { get; set; } = new(0x9B, 0x8A, 0xFF, 0xD0);
    public Rgba Voice { get; set; } = new(0x7A, 0x8A, 0x99, 0xB0);
    public Rgba Other { get; set; } = new(0xE8, 0xEE, 0xF5, 0xE0);
    public Rgba GuideLine { get; set; } = new(0xFF, 0xFF, 0xFF, 0x40);

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

/// <summary>A bar ready to be drawn, in pixels.</summary>
public readonly record struct BarGeometry(
    double X,
    double Y,
    double Width,
    double Height,
    double Alpha,
    Rgba Colour,
    SoundClass Class,
    double Level,
    double Azimuth);

/// <summary>
/// Converts <see cref="AudioEvent"/>s into pixel rectangles.
/// Kept free of any drawing code so the layout can be unit tested.
/// </summary>
public static class OverlayLayout
{
    /// <summary>
    /// Maps a direction to the vertical position of its bar.
    /// 0 (dead ahead) maps to 0 (top of the span); 180 (behind) maps to 1 (bottom).
    /// </summary>
    public static double FrontBackToVertical(double azimuthDegrees)
        => Math.Clamp(Math.Abs(azimuthDegrees) / 180.0, 0.0, 1.0);

    /// <summary>
    /// Maps a direction to which rail it belongs on. A dead-ahead sound is centred,
    /// anything with a left or right component goes to that rail.
    /// </summary>
    public static int Rail(double azimuthDegrees)
        => azimuthDegrees < -8 ? 0 : azimuthDegrees > 8 ? 1 : 2; // 0 = left, 1 = right, 2 = centre

    /// <summary>Maps loudness (0..1) to bar length in pixels.</summary>
    public static double LengthForLevel(double level, double screenWidth, OverlayStyle style)
    {
        var minLen = style.MinLengthFraction * screenWidth;
        var maxLen = style.MaxLengthFraction * screenWidth;
        return minLen + Math.Clamp(level, 0.0, 1.0) * (maxLen - minLen);
    }

    /// <summary>Builds all bars for a set of events at a given screen size.</summary>
    public static List<BarGeometry> BuildBars(
        IReadOnlyList<AudioEvent> events,
        int screenWidth,
        int screenHeight,
        OverlayStyle style)
    {
        var result = new List<BarGeometry>();
        if (screenWidth <= 0 || screenHeight <= 0) return result;

        var edge = style.EdgeInsetFraction * screenWidth;
        var maxLen = style.MaxLengthFraction * screenWidth;
        var thickness = Math.Max(3.0, style.BarThicknessFraction * screenHeight);
        var span = style.VerticalSpanFraction * screenHeight;
        var top = (screenHeight - span) * 0.5;

        foreach (var e in events)
        {
            var rail = Rail(e.Direction.AzimuthDegrees);
            var v = FrontBackToVertical(e.Direction.AzimuthDegrees);
            var y = top + v * span - thickness * 0.5;

            var length = LengthForLevel(e.Level, screenWidth, style);

            // Faint readings are dimmed, so an unreliable direction is visibly less
            // assertive than a confident one.
            var confidence = 0.35 + 0.65 * Math.Clamp(e.Confidence, 0.0, 1.0);
            var distanceConfidence = 0.45 + 0.55 * Math.Clamp(e.DistanceConfidence, 0.0, 1.0);
            var alpha = Math.Clamp(e.Level, 0.0, 1.0) * confidence * distanceConfidence;

            var colour = style.For(e.Class);

            double x;
            switch (rail)
            {
                case 0: // left rail, grows right toward the centre
                    x = edge;
                    break;
                case 1: // right rail, grows left toward the centre
                    x = screenWidth - edge - length;
                    break;
                default: // dead ahead, grows from the left of centre
                    x = screenWidth * 0.5 - maxLen * 0.5;
                    break;
            }

            result.Add(new BarGeometry(
                Math.Round(x), Math.Round(y), Math.Round(length), Math.Round(thickness),
                alpha, colour, e.Class, e.Level, e.Direction.AzimuthDegrees));
        }

        return result;
    }

    /// <summary>Positions of the front/back guide lines, as normalised vertical positions.</summary>
    public static IEnumerable<double> GuideLines()
    {
        // 0, 90 and 180 degrees of frontness.
        yield return 0.0;
        yield return 0.5;
        yield return 1.0;
    }
}
