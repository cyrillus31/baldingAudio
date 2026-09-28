using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Dsp;
using BaldingAudio.Core.Overlay;

namespace BaldingAudio.Core.Diagnostics;

/// <summary>
/// Checks that the help page's demonstrations are true of the real renderer.
/// </summary>
/// <remarks>
/// <para>
/// The settings window's help tab exists to answer "what am I looking at". That is only
/// worth anything if the pictures on it are produced by the same code that draws over
/// the game, and that the sentences beside them describe what that code does. Both are
/// easy to get wrong in a way nothing else would catch: the page would still render,
/// still look plausible, and be quietly wrong - and a help page that is confidently
/// wrong costs more trust than a missing one.
/// </para>
///
/// <para>
/// So the page is fed by <see cref="PreviewScenes"/> into the real
/// <see cref="OverlayRenderer"/>, and these checks read the geometry back out. A fault
/// injected into the layout - a side flipped, a height encoding the wrong half of the
/// bearing, the threshold ignored - turns one of these red, which is the only evidence
/// that the help page is describing the app rather than a memory of it.
/// </para>
/// </remarks>
public static partial class SelfTest
{
    private const string N16 = "the help page's demonstrations are true of the real renderer";

    private static Result TestHelpPageDemonstrationsAreTrue()
    {
        const int W = 1920, H = 1080;
        var style = OverlayStyle.Default();
        var buffer = new PixelBuffer(W, H);

        var cues = PreviewScenes.Stereo();
        var byLabel = cues.ToDictionary(c => c.Label, c => c);

        LineGeometry? Draw(string label)
        {
            var cue = byLabel[label];
            var renderer = new OverlayRenderer(style);
            renderer.Render(new[] { cue.Event }, buffer);
            var lines = OverlayLayout.BuildLines(new[] { cue.Event }, W, H, style);
            return lines.Count == 0 ? null : lines[0];
        }

        // --- the long right-hand line -------------------------------------------------
        var strong = Draw("9 dB to the right, in front");
        if (strong is null)
            return new(N16, false, "a sound 9 dB to the right draws no line at the default threshold, " +
                "so the help page's first demonstration is a blank screen");

        // It has to come from the right edge and run left, inward. Both halves: a line
        // that starts on the right and runs the wrong way is the single most damaging
        // fault this app can have, and it is invisible in a screenshot.
        if (strong.Value.X < W * 0.5 || strong.Value.Dx >= 0)
            return new(N16, false,
                $"the 9 dB right-hand line starts at x={strong.Value.X:F0} and runs dx={strong.Value.Dx:F2}; " +
                "it must start on the right edge and run inward, and the help page shows it as a " +
                "right-hand cue");

        // --- length is the difference, not the volume ---------------------------------
        var weak = Draw("3 dB to the right, in front");
        if (weak is null)
            return new(N16, false, "a sound 3 dB to the right draws no line, so the help page cannot " +
                "demonstrate that length means the difference between the ears");

        if (strong.Value.Length <= weak.Value.Length)
            return new(N16, false,
                $"the 9 dB line is {strong.Value.Length:F0} px and the 3 dB line {weak.Value.Length:F0} px; " +
                "the page tells the user length is the imbalance, and here the bigger imbalance is shorter");

        // Both cues are the same loudness on purpose, so this is a real comparison of
        // the two quantities rather than a coincidence.
        if (Math.Abs(strong.Value.Length / Math.Max(1, weak.Value.Length) - 0.78 / 0.33) > 0.05)
            return new(N16, false,
                $"9 dB gives {strong.Value.Length / Math.Max(1, weak.Value.Length):F2}x the length of 3 dB, " +
                "expected about 2.36x from the imbalance ratio - length is not tracking the measurement");

        // --- the two blanks, which are the surprising part ------------------------------
        foreach (var label in new[] { "dead ahead, both ears equal", "1 dB to the right, in front" })
        {
            if (Draw(label) is not null)
                return new(N16, false,
                    $"\"{label}\" draws a line at the default 3 dB threshold, so the help page's claim that " +
                    "it draws nothing is false");
        }

        // A dead-ahead sound has exactly zero imbalance, so it stays blank at every
        // threshold. That one is a real invariant and the page leans on it: the whole
        // "nothing for a sound in your face" rule is this, and if the floor could go
        // below zero the rule would quietly stop holding.
        style.BalanceFloorDb = 0.0;
        if (Draw("dead ahead, both ears equal") is not null)
            return new(N16, false, "a dead-ahead sound draws a line even at a 0 dB threshold; a perfectly " +
                "even mix has no side at any threshold and the help page relies on that being true");

        // The 1 dB case is a threshold comparison, not an absolute: 1 dB of imbalance is
        // 0.115 as a raw balance, so it appears exactly when the floor drops below that.
        // The caption promises exactly that, so both halves of it are checked - a page
        // claiming "never" here would be as wrong as one claiming "always".
        foreach (var (floor, shouldDraw) in new[] { (3.0, false), (1.5, false), (1.0, true), (0.1, true) })
        {
            style.BalanceFloorDb = floor;
            var drawn = Draw("1 dB to the right, in front") is not null;
            if (drawn != shouldDraw)
                return new(N16, false,
                    $"at a {floor:F1} dB threshold the 1 dB sound {(drawn ? "draws" : "draws nothing")}, " +
                    $"but the help page's caption says {(shouldDraw ? "draws" : "nothing")}; the page and the " +
                    "renderer disagree about the one number the page exists to explain. Note the boundary is " +
                    "inclusive - at exactly 1 dB it draws");
        }

        style.BalanceFloorDb = OverlayStyle.DefaultBalanceFloorDb;

        // --- height is still front-versus-behind --------------------------------------
        var rear = Draw("6 dB to the left, behind");
        if (rear is null)
            return new(N16, false, "a sound 6 dB to the left and behind draws no line, so the help page " +
                "cannot demonstrate that height still carries front-versus-behind");

        if (rear.Value.X >= W * 0.5 || rear.Value.Dx <= 0)
            return new(N16, false,
                $"the left-hand line starts at x={rear.Value.X:F0} and runs dx={rear.Value.Dx:F2}; it must " +
                "start on the left edge and run inward");

        // A bearing of -140 is well behind, so it must sit lower on screen than one at
        // +35, which is in front. This is the second thing a screenshot hides: both
        // lines are the same shape, and only their y differs.
        if (rear.Value.Y <= strong.Value.Y)
            return new(N16, false,
                $"the behind-and-left line is at y={rear.Value.Y:F0} and the in-front-and-right line at " +
                $"y={strong.Value.Y:F0}; height is supposed to be front at the top and behind at the bottom, " +
                "and these are the wrong way round");

        // --- and the multichannel page uses the other model ----------------------------
        var mcCues = PreviewScenes.Multichannel();
        var mcStyle = OverlayStyle.Default();
        var loud = mcCues.First(c => c.Label.StartsWith("loud"));
        var quiet = mcCues.First(c => c.Label.StartsWith("quiet"));
        if (loud.Event.Balance is not null || quiet.Event.Balance is not null)
            return new(N16, false, "the multichannel demonstrations carry a left/right imbalance, so the help " +
                "page would describe them with the stereo model, where length means side rather than volume");

        var mcLoud = OverlayLayout.BuildLines(new[] { loud.Event }, W, H, mcStyle)[0];
        var mcQuiet = OverlayLayout.BuildLines(new[] { quiet.Event }, W, H, mcStyle)[0];
        if (mcLoud.Length <= mcQuiet.Length)
            return new(N16, false,
                $"in multichannel the loud line is {mcLoud.Length:F0} px and the quiet one {mcQuiet.Length:F0} px; " +
                "the page says length carries volume there, and it does not");

        return new(N16, true,
            "9 dB right draws from the right edge inward at 2.4x the length of 3 dB, an even mix draws nothing " +
            "even at a 0 dB threshold, the 1 dB case switches on exactly at 1 dB, a behind-left line sits " +
            "lower than a front-right one, and the multichannel page measures length by volume");
    }
}
