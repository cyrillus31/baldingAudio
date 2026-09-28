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
    private readonly SoundFilter? _filter;

    /// <param name="style">
    /// Held by reference, not copied, so a settings window writing into the live config
    /// changes the next frame. Copying it here would make every slider move a number and
    /// nothing else.
    /// </param>
    /// <param name="filter">
    /// Which sounds are allowed through. Also by reference, for the same reason.
    /// </param>
    public OverlayRenderer(OverlayStyle? style = null, SoundFilter? filter = null)
    {
        _style = style ?? OverlayStyle.Default();
        _filter = filter;
    }

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
        LastLineCount = 0;

        var visible = _filter is null ? events : _filter.Apply(events);
        if (visible.Count > 0) Draw(visible, buffer);
        decal?.Invoke(buffer, buffer.Width, buffer.Height);
    }

    private void Draw(IReadOnlyList<AudioEvent> events, PixelBuffer buffer)
    {
        // The edge-glow mode has no meaning on a multichannel endpoint: there the sign of
        // the bearing is the measurement, and the whole point of this mode is that the
        // vertical axis carries nothing. Falling back to lines there, rather than
        // drawing nothing, is the difference between a mode that is unavailable in a
        // given room and a display that goes dead when the endpoint changes.
        if (_style.DisplayMode == OverlayDisplayMode.EdgeGlow && AnyHasBalance(events))
        {
            DrawEdgeGlow(events, buffer);
            return;
        }

        DrawLines(events, buffer);
    }

    private static bool AnyHasBalance(IReadOnlyList<AudioEvent> events)
    {
        foreach (var e in events)
            if (e.Balance is double b && b != 0.0 && Math.Abs(b) >= 0) return true;
        return false;
    }

    private void DrawEdgeGlow(IReadOnlyList<AudioEvent> events, PixelBuffer buffer)
    {
        var blocks = EdgeGlowLayout.Build(events, buffer.Width, buffer.Height, _style);
        LastLineCount = blocks.Count;

        var outlineWidth = (float)(_style.OutlineWidthFraction * _style.ThicknessFor(buffer.Width, buffer.Height));

        foreach (var b in blocks)
        {
            if (b.Alpha <= 0.004 || b.Width <= 0 || b.HalfHeight <= 0) continue;

            // Soft edges, because a hard rectangle reads as a UI panel pasted over the
            // game rather than a glow. The falloff is at the inner end and the two sides,
            // not the outer one: the outer edge is where the block meets the screen border
            // and a fade there would look like a mistake.
            var w = (float)b.Width;
            var h = (float)b.HalfHeight;
            var x = (float)b.X;
            var y = (float)b.Y;

            // float throughout: the 0.45 would otherwise promote the whole expression to
            // double and the two FillGlow calls below stop agreeing on a type.
            var feather = Math.Max(1f, Math.Min(w, h) * 0.45f);

            if (outlineWidth >= 0.5f)
                FillGlow(buffer, x, y, w + outlineWidth * 2, h + outlineWidth * 2,
                    _style.OutlineFor(b.Colour), b.Alpha, feather + outlineWidth);

            FillGlow(buffer, x, y, w, h, b.Colour, b.Alpha, feather);
        }
    }

    /// <summary>
    /// One soft-edged block, brighter at its middle and toward its outer end.
    /// </summary>
    /// <remarks>
    /// Drawn as a stack of horizontal slices rather than a gradient brush because the
    /// buffer is plain pixels with no GDI+ behind it, and this has to be identical in the
    /// live overlay and in the headless preview. A brush would make the help page a
    /// different renderer from the game.
    /// </remarks>
    private static void FillGlow(
        PixelBuffer buffer, float cx, float cy, float w, float h, Rgba colour, double alpha, float feather)
    {
        var top = (int)Math.Floor(cy - h);
        var bottom = (int)Math.Ceiling(cy + h);
        var left = (int)Math.Floor(cx);
        var right = (int)Math.Ceiling(cx + w);

        left = Math.Max(0, left);
        top = Math.Max(0, top);
        right = Math.Min(buffer.Width, right);
        bottom = Math.Min(buffer.Height, bottom);

        for (var y = top; y < bottom; y++)
        {
            // Distance from the block's vertical centre, 0 at the middle and 1 at the edge.
            var dv = Math.Abs(y + 0.5f - cy) / Math.Max(1e-3f, h);
            var vertical = 1.0f - Smooth(dv);
            if (vertical <= 0f) continue;

            for (var x = left; x < right; x++)
            {
                var dx = (x + 0.5f - cx) / Math.Max(1e-3f, w);
                // Brightest at the screen edge, fading out toward the middle of the
                // screen, so the block reads as emanating from the border.
                var horizontal = 1.0f - Smooth(dx);
                var a = alpha * vertical * horizontal;
                if (a <= 0.004) continue;
                buffer.Blend(x, y, colour, (float)a);
            }
        }
    }

    /// <summary>Smoothstep, so the edge of the glow has no visible seam.</summary>
    private static float Smooth(double t)
    {
        var c = Math.Clamp(1.0 - t, 0.0, 1.0);
        return (float)(c * c * (3.0 - 2.0 * c));
    }

    /// <summary>
    /// Lines actually painted on the last frame. Not the number of events handed in:
    /// anything under the side threshold is tracked but deliberately not drawn, so the
    /// two numbers differ exactly when the display is holding back.
    /// </summary>
    public int LastLineCount { get; private set; }

    private void DrawLines(IReadOnlyList<AudioEvent> events, PixelBuffer buffer)
    {
        var lines = OverlayLayout.BuildLines(events, buffer.Width, buffer.Height, _style);
        LastLineCount = lines.Count;
        var outlineWidth = (float)(_style.OutlineWidthFraction * _style.ThicknessFor(buffer.Width, buffer.Height));

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

            // Outline first, fill over it. Both are the same capsule shape at different
            // radii, so what is left visible is a band of the outline colour around the
            // edge of the line - a rim of the same hue, not a contrasting frame.
            //
            // A flat line, no fade and no brighter end. It used to brighten toward the
            // inner tip, to read as reaching in off the border - but nothing is anchored
            // to a border now, so both ends of a line mean the same thing and ramping
            // between them would only imply a direction that is not there.
            if (outlineWidth >= 0.5f)
            {
                buffer.FillCapsule(
                    ax, ay, tipX, tipY, radius + outlineWidth,
                    _style.OutlineFor(l.Colour), l.Alpha,
                    alphaAtStart: 1.0f, alphaAtEnd: 1.0f);
            }

            buffer.FillCapsule(
                ax, ay, tipX, tipY, radius, l.Colour, l.Alpha,
                alphaAtStart: 1.0f, alphaAtEnd: 1.0f);
        }
    }
}
