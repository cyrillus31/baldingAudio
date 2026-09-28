using BaldingAudio.App.Config;
using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Overlay;

namespace BaldingAudio.App;

/// <summary>
/// A panel that shows what the overlay will look like, drawn by the real renderer.
/// </summary>
/// <remarks>
/// <para>
/// Wraps <see cref="PreviewSurface"/> and adds the two things a preview needs that the
/// overlay does not: a background image to judge contrast against, and a rule of
/// thirds so "front at the top" and "behind at the bottom" are visible as position
/// rather than taken on trust.
/// </para>
///
/// <remarks>
/// The background is a generated gradient, not a photograph. When a real game frame is
/// dropped in, that is a one-line change here; the reason it is not the default is that
/// a preview over a photo the user has not chosen is a preview they cannot generalise
/// from, and the cost of guessing wrong is tuning the wrong number.
/// </remarks>
internal sealed class PreviewPanel : Panel
{
    private readonly PreviewSurface _surface;
    private System.Drawing.Image? _background;

    public PreviewPanel(OverlayStyle style, SoundFilter filter)
    {
        _surface = new PreviewSurface(style, filter);
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 24, 26);
    }

    /// <summary>Draws the rule-of-thirds guides. Off for the plain overlay preview.</summary>
    public bool ShowGuides { get; set; }

    public void SetEvents(IReadOnlyList<AudioEvent> events)
    {
        _surface.Events = events;
        Invalidate();
    }

    /// <summary>Optional backdrop, e.g. a game screenshot.</summary>
    public System.Drawing.Image? Background
    {
        get => _background;
        set { _background = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        var box = new Rectangle(0, 0, Width, Height);

        if (_background is not null)
        {
            // Cover-fit, so a photo in any ratio fills the box without letterboxing.
            var scale = Math.Max((double)Width / _background.Width, (double)Height / _background.Height);
            var w = (float)(_background.Width * scale);
            var h = (float)(_background.Height * scale);
            g.DrawImage(_background, new RectangleF((Width - w) / 2f, (Height - h) / 2f, w, h));

            // Dimmed, because the overlay has to be legible over whatever the game is
            // doing. A photo at full brightness is harder to read a line against than a
            // dimmed one, and the dimming matches what the eye does in a dark scene.
            using (var veil = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                g.FillRectangle(veil, box);
        }

        _surface.Paint(g, box);

        if (ShowGuides) PaintGuides(g, box);
    }

    /// <summary>
    /// Thirds guides and the two labels that matter.
    /// </summary>
    /// <remarks>
    /// Horizontal lines at a third and two thirds of the height, with "in front" at the
    /// top and "behind" at the bottom. These are the two things the current model encodes
    /// that are not obvious: that vertical position means front-versus-behind at all, and
    /// which end is which. A guide line makes the second one unambiguous, and the
    /// user rejected two earlier designs precisely because the spatial meaning was
    /// unclear.
    /// </remarks>
    private void PaintGuides(Graphics g, Rectangle box)
    {
        using var pen = new Pen(Color.FromArgb(90, 255, 255, 255)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dot };
        for (var i = 1; i <= 2; i++)
        {
            var y = box.Height * i / 3f;
            g.DrawLine(pen, 0, y, box.Width, y);
        }

        using var font = new Font("Segoe UI", 8f);
        using var brush = new SolidBrush(Color.FromArgb(170, 255, 255, 255));
        g.DrawString("in front", font, brush, 4, 3);
        g.DrawString("behind", font, brush, 4, box.Height - 16);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _surface.Dispose();
        base.Dispose(disposing);
    }
}
