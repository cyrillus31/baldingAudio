using BaldingAudio.Core.Audio;

namespace BaldingAudio.Core.Overlay;

/// <summary>
/// Draws the indicator into a <see cref="PixelBuffer"/>.
///
/// Purely a function of (events, style, size), so the same code path is used by the
/// live overlay and by headless screenshot tests.
/// </summary>
public sealed class OverlayRenderer
{
    private readonly OverlayStyle _style;

    public OverlayRenderer(OverlayStyle? style = null) => _style = style ?? OverlayStyle.Default();

    public OverlayStyle Style => _style;

    /// <summary>
    /// Renders a full frame. The buffer is cleared first, so a frame with nothing
    /// sounding is fully transparent and leaves the game completely untouched.
    /// </summary>
    public void Render(
        IReadOnlyList<AudioEvent> events,
        PixelBuffer buffer,
        Action<PixelBuffer, int, int>? decal = null)
    {
        buffer.Clear();

        if (events.Count > 0) DrawLines(events, buffer);
        decal?.Invoke(buffer, buffer.Width, buffer.Height);
    }

    private void DrawLines(IReadOnlyList<AudioEvent> events, PixelBuffer buffer)
    {
        var lines = OverlayLayout.BuildLines(events, buffer.Width, buffer.Height, _style);

        foreach (var l in lines)
        {
            if (l.Alpha <= 0.004) continue;

            var ax = (float)l.X;
            var ay = (float)l.Y;
            var dx = (float)l.Dx;
            var dy = (float)l.Dy;
            var radius = (float)(l.Thickness * 0.5);
            var length = (float)l.Length;

            var tipX = ax + dx * length;
            var tipY = ay + dy * length;

            // The body: brightest at the tip, dimmer where it meets the screen border,
            // so the line reads as reaching in from the edge.
            buffer.FillCapsule(
                ax, ay, tipX, tipY, radius, l.Colour, l.Alpha,
                alphaAtStart: (float)_style.BorderFade,
                alphaAtEnd: 1.0f);

            if (!_style.ShowTipCap) continue;

            // A short, brighter cap at the inner end. On a 1/8-screen line this is a few
            // dozen pixels, which is what makes the reach readable at a glance.
            var capLength = (float)(length * _style.TipCapFraction);
            var capStartX = tipX - dx * capLength;
            var capStartY = tipY - dy * capLength;
            buffer.FillCapsule(
                capStartX, capStartY, tipX, tipY, radius,
                l.Colour.WithAlpha(255), l.Alpha, alphaAtStart: 0.75f, alphaAtEnd: 1.0f);
        }
    }
}
