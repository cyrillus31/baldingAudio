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

            // A flat line, no fade and no brighter end. It used to brighten toward the
            // inner tip, to read as reaching in off the border - but nothing is anchored
            // to a border now, so both ends of a line mean the same thing and ramping
            // between them would only imply a direction that is not there.
            buffer.FillCapsule(
                ax, ay, tipX, tipY, radius, l.Colour, l.Alpha,
                alphaAtStart: 1.0f, alphaAtEnd: 1.0f);
        }
    }
}
