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

    /// <summary>Composites a source pixel over the existing destination.</summary>
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
        var inv = 255 - sa;
        var r = (byte)((src & 0xFF) + ((dst & 0xFF) * inv + 127) / 255);
        var g = (byte)(((src >> 8) & 0xFF) + (((dst >> 8) & 0xFF) * inv + 127) / 255);
        var b = (byte)(((src >> 16) & 0xFF) + (((dst >> 16) & 0xFF) * inv + 127) / 255);
        _pixels[index] = 0xFF000000u | (uint)(b << 16) | (uint)(g << 8) | r;
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

    /// <summary>Horizontal hairline with vertical anti-aliasing, used for the guide lines.</summary>
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
