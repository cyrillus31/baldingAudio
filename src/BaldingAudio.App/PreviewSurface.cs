using BaldingAudio.App.Config;
using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Overlay;

namespace BaldingAudio.App;

/// <summary>
/// The demonstration shown on the Help tab, and on the Look and Sounds tabs while
/// they are being edited.
/// </summary>
/// <remarks>
/// <para>
/// Drawn by the <b>real</b> <see cref="OverlayRenderer"/> against the <b>live</b>
/// config, into a plain <see cref="PixelBuffer"/> that this class then blits into a
/// WinForms bitmap. Nothing here re-implements the display, and nothing caches what the
/// overlay would look like.
/// </para>
///
/// <para>
/// That is the whole design, and it is the reason a help page can be trusted. A
/// separately-drawn picture of the display is a second implementation: it agrees today
/// and diverges the first time a slider moves, and then it is confidently wrong. Here
/// moving a slider changes the preview because the preview is the overlay.
/// </para>
///
/// <para>
/// The buffer is scaled rather than resized, so a line's shape on the preview is the
/// shape on screen - a genuine 1920x1080 render blitted down, with no re-layout at the
/// preview's size. Scaling geometry would make a 4% line look thicker than it is, and
/// the user would tune the wrong number.
/// </para>
/// </remarks>
internal sealed class PreviewSurface
{
    private readonly PixelBuffer _buffer;
    private readonly OverlayRenderer _renderer;
    private readonly System.Drawing.Bitmap _bitmap;
    private readonly object _lock = new();

    /// <summary>Full-resolution size the overlay is rendered at.</summary>
    private const int SimWidth = 1920;
    private const int SimHeight = 1080;

    public PreviewSurface(OverlayStyle style, SoundFilter filter)
    {
        _renderer = new OverlayRenderer(style, filter);
        _buffer = new PixelBuffer(SimWidth, SimHeight);
        _bitmap = new System.Drawing.Bitmap(
            SimWidth, SimHeight, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
    }

    /// <summary>The cues currently on show. The Help tab replaces these.</summary>
    public IReadOnlyList<AudioEvent> Events { get; set; } = Array.Empty<AudioEvent>();

    /// <summary>
    /// Re-renders at full resolution. Cheap enough to do on every slider release and on
    /// every help-tab cue change; a full frame is 2 million pixels of mostly-cleared
    /// memory.
    /// </summary>
    public void Rebuild()
    {
        lock (_lock)
        {
            _renderer.Render(Events, _buffer);
            RenderToBitmap();
        }
    }

    /// <summary>
    /// Copies the pixel buffer into the GDI bitmap. The buffer is already in
    /// premultiplied BGRA, which is exactly the layout <c>Format32bppPArgb</c> needs, so
    /// this is a copy rather than a conversion.
    /// </summary>
    private void RenderToBitmap()
    {
        var data = _bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, SimWidth, SimHeight),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        try
        {
            _buffer.CopyTo(data.Scan0);
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
    }

    /// <summary>
    /// Draws the current frame into a box, letterboxed to keep the aspect ratio.
    /// </summary>
    public void Paint(Graphics g, Rectangle target, bool drawFrame = true)
    {
        lock (_lock)
        {
            var scale = Math.Min((double)target.Width / SimWidth, (double)target.Height / SimHeight);
            var w = (int)(SimWidth * scale);
            var h = (int)(SimHeight * scale);
            var x = target.X + (target.Width - w) / 2;
            var y = target.Y + (target.Height - h) / 2;

            // A backdrop, so the user is judging contrast against something rather than
            // against a control's background colour. A mid-dark grey is the honest
            // middle: a near-black backdrop flatters a dark line and a white one
            // flatters nothing.
            using (var backdrop = new SolidBrush(Color.FromArgb(38, 40, 44)))
                g.FillRectangle(backdrop, x, y, w, h);

            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(_bitmap, new Rectangle(x, y, w, h));

            if (drawFrame)
                using (var pen = new Pen(Color.FromArgb(70, 70, 78)))
                    g.DrawRectangle(pen, x, y, w, h);
        }
    }

    public void Dispose() => _bitmap.Dispose();
}
