using BaldingAudio.Core.Audio;

namespace BaldingAudio.Core.Overlay;

/// <summary>
/// Draws the indicator into a <see cref="PixelBuffer"/>.
///
/// Purely a function of (events, style, size), so the same code path is used by the
/// live overlay and by the headless screenshot tests.
/// </summary>
public sealed class OverlayRenderer
{
    private readonly OverlayStyle _style;

    public OverlayRenderer(OverlayStyle? style = null) => _style = style ?? OverlayStyle.Default();

    public OverlayStyle Style => _style;

    /// <summary>
    /// Renders a full frame.
    /// </summary>
    /// <param name="events">Currently visible cues, normally from an <see cref="EventTracker"/>.</param>
    /// <param name="buffer">Destination, sized to the target monitor.</param>
    /// <param name="decal">
    /// Optional small text drawn bottom-centre (status line). Kept as an injected
    /// callback so the core library needs no font or GDI dependency.
    /// </param>
    public void Render(
        IReadOnlyList<AudioEvent> events,
        PixelBuffer buffer,
        Action<PixelBuffer, int, int>? decal = null)
    {
        buffer.Clear();

        var w = buffer.Width;
        var h = buffer.Height;

        if (_style.ShowGuideLines) DrawGuides(buffer, w, h);
        DrawBars(events, buffer, w, h);
        decal?.Invoke(buffer, w, h);
    }

    private void DrawGuides(PixelBuffer buffer, int w, int h)
    {
        var span = _style.VerticalSpanFraction * h;
        var top = (h - span) * 0.5;
        var railX = _style.EdgeInsetFraction * w;
        var length = _style.MaxLengthFraction * w;

        foreach (var v in OverlayLayout.GuideLines())
        {
            var y = (float)(top + v * span);
            var alpha = v == 0.0 ? _style.GuideLineAlpha * 1.6 : _style.GuideLineAlpha;
            var x0 = (float)railX;
            var x1 = (float)(railX + length);
            buffer.FillHorizontalLine(x0 * 0.6f, x1, y, 1.0f, _style.GuideLine, alpha);
            buffer.FillHorizontalLine(w - x1, w - x0 * 0.6f, y, 1.0f, _style.GuideLine, alpha);
        }
    }

    private void DrawBars(IReadOnlyList<AudioEvent> events, PixelBuffer buffer, int w, int h)
    {
        var bars = OverlayLayout.BuildBars(events, w, h, _style);
        foreach (var b in bars)
        {
            if (b.Alpha <= 0.004) continue;
            var height = (float)b.Height;
            var radius = height * 0.5f;
            var width = (float)b.Width;

            // A bright leading cap, so the direction of travel is unambiguous even
            // when the fade makes the far end of the bar faint.
            if (b.X < w * 0.5)
                buffer.FillRoundedRectFadingRight((float)b.X, (float)b.Y, width, height, radius, b.Colour, b.Alpha);
            else
                buffer.FillRoundedRectFadingLeft((float)b.X, (float)b.Y, width, height, radius, b.Colour, b.Alpha);

            DrawCap(buffer, b, h);
        }
    }

    private void DrawCap(PixelBuffer buffer, BarGeometry b, int screenHeight)
    {
        var height = (float)b.Height;
        var capThickness = (float)(b.Height * 0.16);
        var cap = b.Colour.WithAlpha(255);

        if (b.X < buffer.Width * 0.5)
        {
            // Cap sits at the tip of the bar, on the centre-facing side.
            var x = (float)(b.X + b.Width - capThickness);
            buffer.FillRoundedRect(
                x, (float)b.Y + height * 0.18f, capThickness, height * 0.64f,
                capThickness * 0.5f, cap, b.Alpha);
        }
        else
        {
            var x = (float)b.X;
            buffer.FillRoundedRect(
                x, (float)b.Y + height * 0.18f, capThickness, height * 0.64f,
                capThickness * 0.5f, cap, b.Alpha);
        }
    }
}
