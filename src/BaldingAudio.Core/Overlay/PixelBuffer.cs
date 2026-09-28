using System.Runtime.InteropServices;

namespace BaldingAudio.Core.Overlay;

/// <summary>
/// A 32-bit premultiplied BGRA pixel buffer. This is exactly the layout
/// <c>UpdateLayeredWindow</c> requires for per-pixel alpha, so the buffer can be
/// handed to GDI with no conversion pass.
/// </summary>
public sealed class PixelBuffer
{
    private readonly uint[] _pixels;

    public int Width { get; }
    public int Height { get; }

    public PixelBuffer(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        _pixels = new uint[width * height];
    }

    public Span<uint> Pixels => _pixels;

    public void Clear() => Array.Clear(_pixels);

    /// <summary>Packs straight (non-premultiplied) RGBA into the premultiplied layout.</summary>
    private static uint Pack(Rgba c, double alphaScale)
    {
        var a = Math.Clamp(c.A * alphaScale, 0, 255) / 255.0;
        // Premultiplication matters: with straight alpha, colour darker than the
        // background bleeds through the edge and the bars get a dark halo.
        var r = (byte)Math.Round(c.R * a);
        var g = (byte)Math.Round(c.G * a);
        var b = (byte)Math.Round(c.B * a);
        var av = (byte)Math.Round(255 * a);
        return (uint)(av << 24 | b << 16 | g << 8 | r);
    }

    /// <summary>
    /// Composites a source pixel over the existing destination, in premultiplied alpha.
    ///
    /// The "over" operator is
    /// <code>
    ///     ao = as + ab * (1 - as)
    ///     co = cs + cb * (1 - as)
    /// </code>
    /// and both halves matter. The colour half is the one usually written; the alpha half
    /// used to be skipped, with the destination alpha hard-set to <c>0xFF</c>.
    ///
    /// <para>
    /// That is wrong, and visibly so. Every partially covered pixel - which is every
    /// anti-aliased edge, and therefore a whole ring around every line - was written with
    /// the colour premultiplied down for a low alpha but the alpha byte claiming to be
    /// fully opaque. The edge pixels were then composited by GDI as solid dark colour:
    /// the exact "dark halo" that <see cref="Pack"/> exists to prevent. It also made a
    /// half-transparent line look fully opaque, so a line's own alpha meant nothing.
    /// </para>
    /// </summary>
    /// <summary>
    /// Composites one straight (non-premultiplied) colour over a single pixel.
    /// </summary>
    /// <remarks>
    /// Exposed for the edge-glow mode, which is the only painter here that is not a
    /// shape primitive and so has to address pixels itself. Everything else goes through
    /// <see cref="FillCapsule"/> or the rounded-rect fills, which is deliberate: a shape
    /// primitive gets anti-aliased edges for free, and hand-rolled per-pixel loops are
    /// where the dark-halo bug above came from.
    /// </remarks>
    internal void Blend(int x, int y, Rgba colour, double alphaScale)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        Blend(y * Width + x, Pack(colour, alphaScale));
    }

    private void Blend(int index, uint src)
    {
        var sa = src >> 24;
        if (sa == 0) return;
        if (sa == 255)
        {
            _pixels[index] = src;
            return;
        }

        var dst = _pixels[index];
        var inv = 255 - (int)sa;

        static int Over(byte s, byte d, int inv) => s + (d * inv + 127) / 255;

        var r = (byte)Over((byte)src, (byte)dst, inv);
        var g = (byte)Over((byte)(src >> 8), (byte)(dst >> 8), inv);
        var b = (byte)Over((byte)(src >> 16), (byte)(dst >> 16), inv);
        var a = (byte)Over((byte)sa, (byte)(dst >> 24), inv);

        _pixels[index] = (uint)(a << 24 | b << 16 | g << 8 | r);
    }

    /// <summary>
    /// Anti-aliased rounded rectangle, using a signed distance field evaluated at the
    /// pixel centre. One pass, no supersampling, and the edge stays clean at any size.
    /// </summary>
    public void FillRoundedRect(float x, float y, float w, float h, float radius, Rgba colour, double alphaScale = 1.0)
    {
        if (w <= 0 || h <= 0) return;

        var minX = Math.Max(0, (int)Math.Floor(x) - 1);
        var maxX = Math.Min(Width - 1, (int)Math.Ceiling(x + w) + 1);
        var minY = Math.Max(0, (int)Math.Floor(y) - 1);
        var maxY = Math.Min(Height - 1, (int)Math.Ceiling(y + h) + 1);
        if (minX > maxX || minY > maxY) return;

        var r = Math.Clamp(radius, 0, Math.Min(w, h) * 0.5);
        var cx = x + w * 0.5f;
        var cy = y + h * 0.5f;
        var hx = w * 0.5f - r;
        var hy = h * 0.5f - r;

        for (var py = minY; py <= maxY; py++)
        {
            var row = py * Width;
            for (var px = minX; px <= maxX; px++)
            {
                // Distance from the pixel centre to the nearest rounded-rect boundary.
                var dx = Math.Abs((px + 0.5f) - cx) - hx;
                var dy = Math.Abs((py + 0.5f) - cy) - hy;
                var outside = Math.Sqrt(Math.Max(dx, 0) * Math.Max(dx, 0) + Math.Max(dy, 0) * Math.Max(dy, 0));
                var inside = Math.Min(Math.Max(dx, dy), 0.0f);
                var d = outside + inside - r;

                // d < 0 inside; the 0.5 band straddles the edge and becomes the AA ramp.
                var coverage = Math.Clamp(0.5f - d, 0f, 1f);
                if (coverage <= 0f) continue;

                var src = Pack(colour, alphaScale * coverage);
                Blend(row + px, src);
            }
        }
    }

    /// <summary>
    /// Rounded rectangle that fades out toward its far end, so a bar reads as
    /// "coming from over there" rather than as a solid block.
    /// </summary>
    public void FillRoundedRectFadingRight(float x, float y, float w, float h, float radius, Rgba colour, double alphaScale = 1.0)
        => FillRoundedRectAxis(x, y, w, h, radius, colour, alphaScale, horizontal: true, fadeRight: true);

    public void FillRoundedRectFadingLeft(float x, float y, float w, float h, float radius, Rgba colour, double alphaScale = 1.0)
        => FillRoundedRectAxis(x, y, w, h, radius, colour, alphaScale, horizontal: true, fadeRight: false);

    private void FillRoundedRectAxis(float x, float y, float w, float h, float radius, Rgba colour, double alphaScale, bool horizontal, bool fadeRight)
    {
        if (w <= 0 || h <= 0) return;

        var minX = Math.Max(0, (int)Math.Floor(x) - 1);
        var maxX = Math.Min(Width - 1, (int)Math.Ceiling(x + w) + 1);
        var minY = Math.Max(0, (int)Math.Floor(y) - 1);
        var maxY = Math.Min(Height - 1, (int)Math.Ceiling(y + h) + 1);
        if (minX > maxX || minY > maxY) return;

        var r = Math.Clamp(radius, 0, Math.Min(w, h) * 0.5f);
        var cx = x + w * 0.5f;
        var cy = y + h * 0.5f;
        var hx = w * 0.5f - r;
        var hy = h * 0.5f - r;

        for (var py = minY; py <= maxY; py++)
        {
            var row = py * Width;
            for (var px = minX; px <= maxX; px++)
            {
                var dx = Math.Abs((px + 0.5f) - cx) - hx;
                var dy = Math.Abs((py + 0.5f) - cy) - hy;
                var outside = Math.Sqrt(Math.Max(dx, 0) * Math.Max(dx, 0) + Math.Max(dy, 0) * Math.Max(dy, 0));
                var inside = Math.Min(Math.Max(dx, dy), 0.0f);
                var d = outside + inside - r;
                var coverage = Math.Clamp(0.5f - d, 0f, 1f);
                if (coverage <= 0f) continue;

                // Keep the edge nearest the screen border solid, fade toward the centre.
                var t = fadeRight
                    ? Math.Clamp((px - x) / Math.Max(1f, w), 0f, 1f)
                    : Math.Clamp((x + w - px) / Math.Max(1f, w), 0f, 1f);
                var fade = 0.45f + 0.55f * t;

                Blend(row + px, Pack(colour, alphaScale * coverage * fade));
            }
        }
    }

    /// <summary>
    /// Anti-aliased capsule: every point within <paramref name="radius"/> of the
    /// segment (x0,y0)-(x1,y1). This is the shape a direction indicator needs, because
    /// the lines point inward from the screen border at every angle, and an
    /// axis-aligned rounded rectangle cannot express that.
    ///
    /// Alpha ramps linearly from <paramref name="alphaAtStart"/> to
    /// <paramref name="alphaAtEnd"/> along the segment, so the far end can be the
    /// brightest part and the line reads as reaching in from the border.
    /// </summary>
    public void FillCapsule(
        float x0, float y0, float x1, float y1,
        float radius,
        Rgba colour,
        double alphaScale = 1.0,
        float alphaAtStart = 0.45f,
        float alphaAtEnd = 1.0f)
    {
        if (radius <= 0) return;

        var vx = x1 - x0;
        var vy = y1 - y0;
        var lengthSq = vx * vx + vy * vy;

        var minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, x1) - radius) - 1);
        var maxX = Math.Min(Width - 1, (int)Math.Ceiling(Math.Max(x0, x1) + radius) + 1);
        var minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, y1) - radius) - 1);
        var maxY = Math.Min(Height - 1, (int)Math.Ceiling(Math.Max(y0, y1) + radius) + 1);
        if (minX > maxX || minY > maxY) return;

        for (var py = minY; py <= maxY; py++)
        {
            var row = py * Width;
            var fy = py + 0.5f;
            for (var px = minX; px <= maxX; px++)
            {
                var fx = px + 0.5f;

                // Distance from the pixel centre to the segment, not to the infinite
                // line: clamping t to 0..1 is what gives the rounded end caps.
                float t = lengthSq > 1e-6f
                    ? Math.Clamp(((fx - x0) * vx + (fy - y0) * vy) / lengthSq, 0f, 1f)
                    : 0f;

                var dx = fx - (x0 + vx * t);
                var dy = fy - (y0 + vy * t);
                var d = MathF.Sqrt(dx * dx + dy * dy) - radius;

                var coverage = Math.Clamp(0.5f - d, 0f, 1f);
                if (coverage <= 0f) continue;

                var fade = alphaAtStart + (alphaAtEnd - alphaAtStart) * t;
                Blend(row + px, Pack(colour, alphaScale * coverage * fade));
            }
        }
    }

    /// <summary>Horizontal hairline with vertical anti-aliasing.</summary>
    public void FillHorizontalLine(float x0, float x1, float y, float thickness, Rgba colour, double alphaScale = 1.0)
    {
        var minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, x1)));
        var maxX = Math.Min(Width - 1, (int)Math.Ceiling(Math.Max(x0, x1)));
        if (minX > maxX) return;

        var half = Math.Max(0.5f, thickness * 0.5f);

        for (var px = minX; px <= maxX; px++)
        {
            for (var sub = 0; sub < 2; sub++)
            {
                var py = (int)Math.Floor(y) + sub;
                if (py < 0 || py >= Height) continue;
                var coverage = Math.Clamp(half + 0.5f - Math.Abs((py + 0.5f) - y), 0f, 1f);
                if (coverage <= 0f) continue;
                Blend(py * Width + px, Pack(colour, alphaScale * coverage));
            }
        }
    }

    /// <summary>Writes the buffer into native memory in the exact order GDI expects.</summary>
    public void CopyTo(IntPtr destination)
    {
        var bytes = MemoryMarshal.AsBytes(_pixels.AsSpan());
        Marshal.Copy(bytes.ToArray(), 0, destination, bytes.Length);
    }
}
