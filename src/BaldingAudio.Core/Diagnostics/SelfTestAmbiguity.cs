using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Overlay;

namespace BaldingAudio.Core.Diagnostics;

/// <summary>
/// Checks for the "which side?" question on a two-channel endpoint.
///
/// <para>
/// Split into its own file because the class it extends is long, and because these two
/// checks came out of one field report: music playing equally in both headphones showed
/// on the left. There is no seventh channel to ask, and the honest answer for a source
/// near dead-ahead is that its side is unknown. The overlay used to pick one anyway,
/// because the display turns the sign of the estimated bearing into "start at the left
/// edge" or "start at the right edge", and a bearing of a couple of degrees either way
/// is noise rather than a measurement.
/// </para>
/// </summary>
public static partial class SelfTest
{
    private const string N6 = "a centred stereo source is reported as having no side, not as left or right";

    /// <summary>
    /// A source near dead-ahead has no side, and the analyser must say so rather than
    /// inventing one.
    ///
    /// <para>
    /// A source clearly off to one side must NOT be called ambiguous, or every real
    /// sound would be drawn on both edges and the display would mean nothing.
    /// </para>
    /// </summary>
    private static Result TestCentredStereoSourceHasNoSide()
    {
        const int Ch = 2;

        // `rightDelay` is how many samples late the right ear hears the source. Zero is
        // a source dead ahead.
        static (double Azimuth, bool Ambiguous) Run(int rightDelay)
        {
            var itd = new StereoItd(SampleRate, FrameSize);
            var block = new float[FrameSize * Ch];
            var azimuth = 0.0;
            var ambiguous = false;

            for (var f = 0; f < 400; f++)
            {
                for (var i = 0; i < FrameSize; i++)
                {
                    var n = f * FrameSize + i;
                    block[i * Ch + 0] = Noise(n, 909);
                    block[i * Ch + 1] = n - rightDelay >= 0 ? Noise(n - rightDelay, 909) : 0f;
                }

                itd.Analyse(block, FrameSize, Ch);
                azimuth = itd.Result.Direction.AzimuthDegrees;
                ambiguous = itd.LastAmbiguous;
            }

            return (azimuth, ambiguous);
        }

        var (centredAzimuth, centredAmbiguous) = Run(0);
        if (!centredAmbiguous)
            return new(N6, false,
                $"a source dead ahead reported {centredAzimuth:F1} deg as a definite side; " +
                "a bearing that close to zero cannot tell left from right");

        var (sideAzimuth, sideAmbiguous) = Run(20);
        if (sideAmbiguous)
            return new(N6, false,
                $"a source 20 samples to the side ({sideAzimuth:F1} deg) was called ambiguous, " +
                "so a real side would be drawn on both edges");

        return new(N6, true,
            $"dead ahead ({centredAzimuth:F1} deg) is reported as having no side, while a source " +
            $"20 samples to the side ({sideAzimuth:F1} deg) is not");
    }

    private const string N7 = "a cue with no known side is drawn on both edges, not one arbitrary side";

    /// <summary>
    /// When the side is genuinely unknown, the cue is drawn at the same height on both
    /// edges. Picking one is a coin toss, and the user asked for this explicitly.
    /// </summary>
    private static Result TestAmbiguousCueIsDrawnOnBothEdges()
    {
        const int W = 2560, H = 1440;
        var style = OverlayStyle.Default();

        var ambiguous = new AudioEvent(
            new Direction(-12, 0), 0.8, -20, SoundClass.Voice, 0.5, 0.5, 0.2, 0)
        { Ambiguous = true };

        var lines = OverlayLayout.BuildLines(new[] { ambiguous }, W, H, style);
        if (lines.Count != 2)
            return new(N7, false, $"an ambiguous cue produced {lines.Count} line(s), expected 2 (one per edge)");

        var fromLeft = lines.Where(l => l.Dx > 0).ToList();
        var fromRight = lines.Where(l => l.Dx < 0).ToList();
        if (fromLeft.Count != 1 || fromRight.Count != 1)
            return new(N7, false,
                $"expected one line running inward from each edge, got {fromLeft.Count} from the left " +
                $"and {fromRight.Count} from the right");

        // Both must sit at the same height: the side is unknown, but the distance from
        // ahead is not, and two lines at different heights would read as two bearings.
        if (Math.Abs(fromLeft[0].Y - fromRight[0].Y) > 1.0)
            return new(N7, false,
                $"the two lines sit at different heights, y={fromLeft[0].Y:F0} and y={fromRight[0].Y:F0}, " +
                "which would read as two separate bearings rather than one unknown side");

        // And a cue that does know its side must still be drawn once, not mirrored.
        var decided = new AudioEvent(
            new Direction(-12, 0), 0.8, -20, SoundClass.Voice, 0.5, 0.5, 0.2, 0);
        if (OverlayLayout.BuildLines(new[] { decided }, W, H, style).Count != 1)
            return new(N7, false, "a cue with a known side was mirrored as well");

        return new(N7, true,
            $"one line from each edge at the same height (y={fromLeft[0].Y:F0}); " +
            "a cue with a known side is still drawn once");
    }
}
