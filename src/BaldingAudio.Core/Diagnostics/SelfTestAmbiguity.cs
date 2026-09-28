using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Dsp;
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

    private const string N11 = "the side threshold is expressed in decibels and defaults to 3 dB";

    /// <summary>
    /// The threshold is the one number the user tunes, and it was wrong by a factor of
    /// two.
    ///
    /// <para>
    /// The analyser compares mean squares, so a 3 dB level difference between the ears
    /// is a factor of two in energy and reads as 0.33 - not 0.17, which is what the
    /// amplitude form gives. The old default of 0.10 was written as though the measure
    /// were amplitude, and it is 0.87 dB in reality: below the noise of a quiet room,
    /// which is exactly why the user saw lines while nothing was playing.
    /// </para>
    ///
    /// <para>
    /// This also pins the round trip, because a decibel conversion is the kind of thing
    /// that is right in one direction and wrong in the other, and a wrong inverse makes
    /// a slider move the wrong way.
    /// </para>
    /// </summary>
    private static Result TestSideThresholdIsInDecibels()
    {
        var style = OverlayStyle.Default();

        // Against a literal, not against the constant. The default is built *from*
        // DefaultBalanceFloorDb, so comparing the two is comparing a value with itself -
        // setting the constant to anything at all still passes. That was verified by
        // trying it: 0.87 dB, the old threshold expressed in the right unit, went green.
        const double WantDb = 3.0;
        if (Math.Abs(style.BalanceFloorDb - WantDb) > 1e-9)
            return new(N11, false,
                $"the default side threshold is {style.BalanceFloorDb:F2} dB, expected {WantDb:F2} dB");

        // Checked against the closed form, (g-1)/(g+1) for g = 10^(dB/10), and not
        // against a rounded landmark like 1/3. An energy ratio of exactly 2 is
        // 3.0103 dB, so 3 dB is 1.9953 and lands at 0.3323, not 0.3333. Asserting 1/3
        // would have forced the conversion to be the one that produces 1/3 at 3 dB,
        // which is the amplitude reading - the exact mistake this check exists to catch.
        static double ClosedForm(double db)
        {
            var g = Math.Pow(10.0, db / 10.0);
            return (g - 1.0) / (g + 1.0);
        }

        var threeDb = Decibel.BalanceFromDb(3.0);
        if (Math.Abs(threeDb - ClosedForm(3.0)) > 1e-12)
            return new(N11, false,
                $"3 dB converted to {threeDb:F4}, expected {ClosedForm(3.0):F4}; the conversion is " +
                "using amplitude where the analyser uses energy, or the other way round");

        // The amplitude reading of 3 dB, which is what the conversion must NOT produce.
        // Checked as a distance from 0.17 so it fails on the energy/amplitude confusion
        // specifically, rather than incidentally on the default above.
        if (Math.Abs(threeDb - ClosedForm(6.0)) < 0.05)
            return new(N11, false,
                $"3 dB converted to {threeDb:F4}, which is the amplitude answer for 3 dB; the " +
                "analyser compares mean squares, so the energy answer is about 0.33");

        foreach (var db in new[] { 0.5, 1.0, 2.0, 3.0, 6.0, 9.0, 12.0 })
        {
            if (Math.Abs(Decibel.BalanceFromDb(db) - ClosedForm(db)) > 1e-12)
                return new(N11, false,
                    $"{db:F1} dB converted to {Decibel.BalanceFromDb(db):F4}, expected " +
                    $"{ClosedForm(db):F4} from the closed form");
        }

        foreach (var db in new[] { 0.5, 1.0, 2.0, 3.0, 6.0, 9.0, 12.0 })
        {
            var back = Decibel.DbFromBalance(Decibel.BalanceFromDb(db));
            if (Math.Abs(back - db) > 1e-9)
                return new(N11, false,
                    $"{db:F1} dB round-tripped to {back:F4} dB, so the conversion and its inverse " +
                    "disagree and a slider built on them would move the wrong way");
        }

        // And it must be reachable through the style, since that is the config path. Read
        // the default into a local before mutating: printing style.BalanceFloorDb after
        // this assignment reports the value just set as "the default", which is a log
        // line that looks fine and is wrong - and the whole reason this file exists.
        var defaultDb = style.BalanceFloorDb;

        style.BalanceFloorDb = 9.0;
        if (Math.Abs(Decibel.BalanceFromDb(9.0) - style.BalanceFloor) > 1e-12)
            return new(N11, false,
                $"setting the threshold to 9 dB stored a balance of {style.BalanceFloor:F4}, which is " +
                "not the 9 dB value round-tripped");

        return new(N11, true,
            $"the default is {defaultDb:F1} dB, 3 dB converts to {threeDb:F3}, and the " +
            "conversion round-trips exactly from 0.5 to 12 dB");
    }

    private const string N7 = "the bar length and the edge both come from the imbalance between the ears";

    /// <summary>
    /// The display rule the user asked for: a bar means "off to one side, and this is
    /// how far off". A sound to the right draws on the right only, a sound to the left
    /// on the left only, and a sound with no meaningful difference between its channels
    /// draws nothing at all - which is what makes a grenade in your face invisible
    /// instead of a pair of identical bars on both edges.
    ///
    /// <para>
    /// The polarity is the part worth guarding. A sign flipped anywhere between the
    /// analyser and the layout would put every sound on the wrong side, and a screenshot
    /// of that looks entirely plausible.
    /// </para>
    /// </summary>
    private static Result TestBarLengthAndEdgeComeFromTheImbalance()
    {
        const int W = 2560, H = 1440;
        var style = OverlayStyle.Default();
        var maxLen = style.MaxLengthFraction * W;

        static AudioEvent Cue(double balance) => new(
            new Direction(-70, 0), 0.8, -20, SoundClass.Voice, 0.5, 0.5, 0.2, 0)
        { Balance = balance };

        // Hard right: one line, running inward from the right edge.
        var right = OverlayLayout.BuildLines(new[] { Cue(0.9) }, W, H, style);
        if (right.Count != 1 || right[0].Dx >= 0)
            return new(N7, false,
                $"a cue loud on the right produced {right.Count} line(s) and dx={right.FirstOrDefault().Dx:F0}; " +
                "expected a single line running inward from the right edge (negative dx)");

        // Hard left, same magnitude: mirrored edge, same length.
        var left = OverlayLayout.BuildLines(new[] { Cue(-0.9) }, W, H, style);
        if (left.Count != 1 || left[0].Dx <= 0)
            return new(N7, false,
                $"a cue loud on the left produced {left.Count} line(s) and dx={left.FirstOrDefault().Dx:F0}; " +
                "expected a single line running inward from the left edge (positive dx)");

        if (Math.Abs(right[0].Length - left[0].Length) > 1.0)
            return new(N7, false,
                $"left and right bars of the same magnitude differ in length, " +
                $"{left[0].Length:F0} and {right[0].Length:F0}");

        // Length tracks the magnitude of the imbalance, not the loudness. Expressed
        // relative to the floor rather than as a fixed number, so that moving the
        // threshold does not turn this into a check of the threshold instead - a fixed
        // 0.2 drew a bar when the floor was 0.10 and drew nothing once it was 0.33, and
        // the failure it produced was an out-of-range index rather than a statement
        // about length.
        var floor = style.BalanceFloor;
        var slightLines = OverlayLayout.BuildLines(new[] { Cue(floor * 1.15) }, W, H, style);
        if (slightLines.Count != 1)
            return new(N7, false,
                $"a cue just over the floor ({floor * 1.15:F3}) produced {slightLines.Count} line(s); " +
                "it is off to one side and must draw");
        var slight = slightLines[0];
        if (slight.Length >= right[0].Length)
            return new(N7, false,
                $"a barely lopsided cue drew a bar of {slight.Length:F0} against {right[0].Length:F0} " +
                "for a fully one-sided one; the length is not tracking the imbalance");

        if (Math.Abs(right[0].Length - maxLen * 0.9) > 2.0)
            return new(N7, false,
                $"a fully one-sided cue drew {right[0].Length:F0}px, expected about {maxLen * 0.9:F0}px " +
                $"(90% of the {maxLen:F0}px maximum)");

        // Centred: nothing at all, on either edge. This is the grenade-in-your-face case,
        // and the reason the old both-sides mirroring had to go.
        //
        // The near-miss values are relative to the floor on purpose. Written as fixed
        // small numbers they quietly became floor checks the day the floor rose from
        // 0.10 to 0.33: "0.02 draws nothing" then passed for the wrong reason, because
        // the threshold had excluded it rather than the mix being even. A check that
        // cannot fail for the reason it names is worse than no check, so the values sit
        // just under the floor on both sides, which makes the floor itself the thing
        // being verified.
        foreach (var flat in new[] { 0.0, floor * 0.4, -floor * 0.4, floor * 0.999 })
        {
            var lines = OverlayLayout.BuildLines(new[] { Cue(flat) }, W, H, style);
            if (lines.Count != 0)
                return new(N7, false,
                    $"a cue with a balance of {flat:F3} drew {lines.Count} line(s) against a floor " +
                    $"of {floor:F3}; an even or near-even mix is in front of the player and must " +
                    "draw nothing");
        }

        // And immediately over the floor it must appear, on the correct edge. Without
        // this the block above passes for any floor at all, including one high enough to
        // hide every real sound.
        var justOver = OverlayLayout.BuildLines(new[] { Cue(floor * 1.001) }, W, H, style);
        if (justOver.Count != 1 || justOver[0].Dx >= 0)
            return new(N7, false,
                $"a cue at balance {floor * 1.001:F3}, just over the floor of {floor:F3}, drew " +
                $"{justOver.Count} line(s); it should draw once, from the right edge");

        // A cue with no balance at all is multichannel, where each speaker has a real
        // bearing, so it keeps the loudness-length model and is drawn once.
        var multi = new AudioEvent(
            new Direction(-70, 0), 0.8, -20, SoundClass.Voice, 0.5, 0.5, 0.2, 0);
        var multiLines = OverlayLayout.BuildLines(new[] { multi }, W, H, style);
        if (multiLines.Count != 1 || multiLines[0].Length <= 0)
            return new(N7, false,
                $"a multichannel cue produced {multiLines.Count} line(s); it has a real bearing and " +
                "must still be drawn once");

        return new(N7, true,
            $"right draws {right[0].Length:F0}px inward from the right edge, left {left[0].Length:F0}px " +
            $"from the left, a cue just over the {floor:F3} floor {slight.Length:F0}px, and an even mix " +
            "nothing at all");
    }

    private const string N9 = "a source on the right is measured as a positive imbalance, and the reverse for the left";

    /// <summary>
    /// Runs the real analyser over a real stereo mix and reads back the balance it
    /// publishes, so the polarity is checked through the whole chain rather than at the
    /// layout alone.
    ///
    /// <para>
    /// The layout check above feeds the layout a signed number, which proves the layout
    /// respects one. It cannot prove the analyser produces the right sign, and a sign
    /// reversed anywhere in the analysis is invisible in a screenshot: a footstep to the
    /// right still draws a bar, just on the wrong edge, and it would be caught here and
    /// nowhere else. The same inverted number also turns a left-handed sound into a
    /// right-handed one in the log, which is how a real report gets misdiagnosed.
    /// </para>
    ///
    /// <para>
    /// Gain difference, not a delay: the point of this model is the difference in level
    /// between the ears, so the signal carries a delay as well to keep the bearing
    /// estimate agreeing with it, and both are then checked.
    /// </para>
    /// </summary>
    private static Result TestMeasuredImbalanceHasTheRightSign()
    {
        const int Ch = 2;
        const int Block = 1024;
        const double RightGainDb = 9.0;
        var rightGain = Math.Pow(10.0, RightGainDb / 20.0);

        // `rightDelay` is how many samples late the right ear hears the source, so a
        // negative value puts the source on the right, as in the ITD check above.
        static (double Balance, double Azimuth, int Events) Run(int rightDelay, double rightGain)
        {
            var a = new SpatialAnalyzer(SpeakerPosition.Stereo, Ch, SampleRate);
            var events = new List<AudioEvent>();
            var block = new float[Block * Ch];

            for (var i = 0; i < Block * 300; i++)
            {
                var p = i % Block;
                block[p * Ch + 0] = Noise(i, 4242);
                var shifted = i - rightDelay;
                block[p * Ch + 1] = (shifted >= 0 ? Noise(shifted, 4242) : 0f) * (float)rightGain;

                if (p == Block - 1) a.Process(block.AsSpan(0, Block * Ch), Block, events);
            }

            return (a.Spectrum.Balance ?? double.NaN, a.Spectrum.Peak().Azimuth, events.Count);
        }

        var (rightBalance, rightAz, rightEvents) = Run(-12, rightGain);
        var (leftBalance, leftAz, _) = Run(12, 1.0 / rightGain);

        if (double.IsNaN(rightBalance))
            return new(N9, false,
                "the stereo spectrum published no balance at all, so the overlay would fall back " +
                "to the multichannel display model on a two-channel endpoint");

        if (rightBalance <= 0)
            return new(N9, false,
                $"a source {RightGainDb:F0} dB louder on the right was measured as a balance of " +
                $"{rightBalance:F3}; it must be positive, or every sound is drawn on the wrong edge");

        if (leftBalance >= 0)
            return new(N9, false,
                $"a source {-RightGainDb:F0} dB on the right, i.e. louder on the left, was measured as " +
                $"a balance of {leftBalance:F3}; it must be negative");

        // The loud channel alone has to be beating the quiet one by enough to clear the
        // floor the layout draws above, or the bar exists but is invisible.
        if (Math.Abs(rightBalance) < OverlayStyle.Default().BalanceFloor)
            return new(N9, false,
                $"a source {RightGainDb:F0} dB to the right measured a balance of only {rightBalance:F3}, " +
                $"under the {OverlayStyle.Default().BalanceFloor:F2} the layout needs before it draws anything");

        // The delay and the gain have to point the same way, or the edge comes from one
        // cue and the height from another and the line lands somewhere neither describes.
        if (rightAz <= 0)
            return new(N9, false,
                $"the same right-side source measured a bearing of {rightAz:F0} deg, expected positive; " +
                "the edge comes from the balance and the height from the bearing, so they must agree");

        return new(N9, true,
            $"a source {RightGainDb:F0} dB to the right measured {rightBalance:F2} at {rightAz:F0} deg and " +
            $"one to the left measured {leftBalance:F2} at {leftAz:F0} deg, agreeing in sign on both");
    }

    private const string N10 = "loud centred sound in another band does not hide a quiet sound off to one side";

    /// <summary>
    /// The user's own scenario: music playing evenly in both channels, and footsteps to
    /// the right. The footstep is what matters and the music is not.
    ///
    /// <para>
    /// The app can only measure the mix, so whole-mix reasoning averages the two
    /// together and the footstep disappears - which is exactly the complaint that
    /// produced this model. That is why the balance is chosen per band: this signal
    /// puts the music in the high band at 4.0 and the footstep in the low band at 0.35,
    /// about 21 dB apart, so a whole-mix balance would come out at 0.04 and draw
    /// nothing at all, while the low band on its own is entirely right and should fill
    /// the bar.
    /// </para>
    ///
    /// <para>
    /// The music-only control matters as much as the mixed case. Without it a bar could
    /// come from the loud centred music alone and the test would pass for the wrong
    /// reason, which is the failure mode this whole exercise has been fighting.
    /// </para>
    /// </summary>
    private static Result TestLoudCentredSoundDoesNotHideASideSound()
    {
        const int Ch = 2;
        const int Block = 1024;
        const double MusicGain = 4.0;

        // Inside the real band edges, not on them: a sine sitting on a bandpass corner
        // leaks into its neighbours and the test would be measuring the filter.
        static double Band(int n, double[] hz)
        {
            var s = 0.0;
            for (var k = 0; k < hz.Length; k++)
            {
                // Coprime-ish integer ratios keep the sum aperiodic enough not to be a
                // single tone, while staying exactly reproducible.
                s += Math.Sin(2.0 * Math.PI * hz[k] * n / SampleRate + k * 1.7);
            }
            return s / hz.Length;
        }

        var highHz = new[] { 2000.0, 2800.0, 3600.0, 5000.0, 6500.0 };
        var lowHz = new[] { 130.0, 170.0, 215.0, 265.0 };

        // `footstep` in the low band and the right channel only. Music is identical in
        // both channels, so it is perfectly centred by construction and can only ever
        // produce a balance of exactly zero.
        static double Run(bool withFootstep, double[] highHz, double[] lowHz)
        {
            var a = new SpatialAnalyzer(SpeakerPosition.Stereo, Ch, SampleRate);
            var events = new List<AudioEvent>();
            var block = new float[Block * Ch];

            for (var i = 0; i < Block * 400; i++)
            {
                var music = MusicGain * Band(i, highHz);
                var step = withFootstep ? 0.35 * Band(i, lowHz) : 0.0;

                var p = i % Block;
                block[p * Ch + 0] = (float)music;
                block[p * Ch + 1] = (float)(music + step);

                if (p == Block - 1) a.Process(block.AsSpan(0, Block * Ch), Block, events);
            }

            return a.Spectrum.Balance ?? double.NaN;
        }

        var musicOnly = Run(false, highHz, lowHz);
        if (double.IsNaN(musicOnly))
            return new(N10, false, "the music-only control published no balance");
        if (Math.Abs(musicOnly) > 1e-6)
            return new(N10, false,
                $"perfectly centred music measured a balance of {musicOnly:F4} instead of zero, so the " +
                "channels are being compared unevenly and every stereo reading is suspect");

        var floor = OverlayStyle.Default().BalanceFloor;
        if (Math.Abs(musicOnly) >= floor)
            return new(N10, false,
                $"centred music measured {musicOnly:F4}, above the {floor:F2} draw floor, so a bar would " +
                "appear for something that is not off to one side");

        var mixed = Run(true, highHz, lowHz);
        if (double.IsNaN(mixed))
            return new(N10, false, "the music-plus-footstep case published no balance");
        if (mixed <= 0)
            return new(N10, false,
                $"a footstep in the right channel measured {mixed:F3} under centred music; the footstep " +
                "is the sound the player needs, and the music must not hide it");

        // A whole-mix balance would be 0.35 / (4.0 + 4.35) = 0.042, under the floor. So
        // anything above the floor proves the band was chosen rather than the mix.
        var wholeMix = 0.35 / (2.0 * MusicGain + 0.35);
        if (mixed <= floor)
            return new(N10, false,
                $"the mixed case measured only {mixed:F3}, at or below the {floor:F2} floor; that is the " +
                $"same as the whole-mix answer of {wholeMix:F3}, so the footstep was averaged away");

        return new(N10, true,
            $"centred music alone measures {musicOnly:F4} and draws nothing, while a footstep 21 dB quieter " +
            $"in the low band measures {mixed:F2} and fills the bar, against a whole-mix answer of {wholeMix:F3}");
    }
}
