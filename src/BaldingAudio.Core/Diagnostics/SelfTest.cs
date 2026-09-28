using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Config;
using BaldingAudio.Core.Overlay;

namespace BaldingAudio.Core.Diagnostics;

/// <summary>
/// Generates synthetic audio and checks that the analyser reports the direction that
/// was actually rendered.
///
/// This is the only way to verify the core without Battlefield, a headset, and a
/// second person to walk around. It renders a transient onto one speaker of a virtual
/// 7.1 rig, then asks the analyser where it came from. Run with <c>--selftest</c>.
/// </summary>
public static partial class SelfTest
{
    private const double SampleRate = 48000;
    private const int FrameSize = 128;

    public record struct Result(string Name, bool Passed, string Detail);

    public static IReadOnlyList<Result> RunAll(Action<string>? log = null)
    {
        var results = new List<Result>
        {
            TestSustainedSoundReportsWhileSounding(),
            TestStereoItdResolvesBothSides(),
            TestStereoItdResolvesBothSidesOnTonalSound(),
            TestStereoSpectrumFollowsTheMeasuredBearing(),
            TestSingleSpeaker(),
            TestDominatedSpeaker(),
            TestLoudnessOrdering(),
            TestFrontBackResolved(),
            TestSilenceProducesNothing(),
            TestLinesRunInwardFromTheSideEdges(),
            TestLinesStaySmall(),
            TestLinesHaveAReadableOutline(),
            TestLineFollowsAMovingSound(),
            TestLineFallsWhenSoundStops(),
            TestFrozenOverlayKeepsItsLines(),
            TestSpectrumCanStartALine(),
            TestSideThresholdIsInDecibels(),
            TestCentredStereoSourceHasNoSide(),
            TestBarLengthAndEdgeComeFromTheImbalance(),
            TestMeasuredImbalanceHasTheRightSign(),
            TestTestAudioIsPannedAsAsked(),
            TestGeneratedWavIsWellFormed(),
            TestCrossingFootstepIsMeasuredCrossing(),
            TestAmbienceDoesNotFakeADirection(),
            TestLoudCentredSoundDoesNotHideASideSound(),
            TestHelpPageDemonstrationsAreTrue(),
            TestEdgeGlowSideAndGrowth(),
            TestLineCapKeepsTheLoudest(),
            TestSoundFilterSelectsByClassAndLevel(),
        };
        foreach (var r in results)
            log?.Invoke($"  [{(r.Passed ? "PASS" : "FAIL")}] {r.Name}: {r.Detail}");
        return results;
    }

    public static bool AllPassed(IReadOnlyList<Result> results) => results.All(r => r.Passed);

    /// <summary>Channel order for Windows canonical 7.1: FL FR FC LFE BL BR FLOC FROC.</summary>
    private static readonly int[] Fl = { 0 };
    private static readonly int[] Fr = { 1 };
    private static readonly int[] Fc = { 2 };
    private static readonly int[] Bl = { 4 };
    private static readonly int[] Br = { 5 };
    private static readonly int[] Sl = { 6 };
    private static readonly int[] Sr = { 7 };

    private static SpatialAnalyzer New7_1(AudioTuning? tuning = null) =>
        new(SpeakerPosition.Surround71, 8, SampleRate, tuning ?? AudioTuning.Default(), FrameSize);

    /// <summary>
    /// Renders <paramref name="seconds"/> of audio containing one footstep-like
    /// transient on the given channels, then returns the events detected.
    /// </summary>
    private static List<AudioEvent> Render(
        SpatialAnalyzer analyzer,
        int[] channels,
        double seconds,
        double peak,
        double atSeconds = 0.6,
        bool broadband = false,
        uint mask = SpeakerPosition.Surround71,
        int channelCount = 8)
    {
        var total = (int)(seconds * SampleRate);
        var block = new float[1024 * channelCount];
        var events = new List<AudioEvent>();
        var onsetFrame = (int)(atSeconds * SampleRate);

        for (var pos = 0; pos < total; pos += 1024)
        {
            var n = Math.Min(1024, total - pos);
            Array.Clear(block);
            for (var i = 0; i < n; i++)
            {
                var abs = pos + i;
                var envelope = Transient(abs - onsetFrame);
                if (envelope <= 0) continue;

                // Broadband flag adds a high-frequency crack, which is what separates
                // a gunshot from a footstep in the classifier.
                var s = peak * (float)envelope * Noise(abs, channels[0] * 7919 + 13);
                if (broadband) s += peak * (float)envelope * 0.6f * Noise(abs, 104729);

                foreach (var ch in channels)
                    block[i * channelCount + ch] += (float)s;
            }
            analyzer.Process(block.AsSpan(0, n * channelCount), n, events);
        }
        return events;
    }

    /// <summary>A short percussive envelope: near-instant attack, exponential decay.</summary>
    private static double Transient(int t)
    {
        if (t < 0) return 0;
        if (t < 64) return t / 64.0;                 // 1.3 ms attack
        var d = (t - 64) / (SampleRate * 0.09);
        return Math.Exp(-d * 3.0);
    }

    private static float Noise(int t, int seed)
    {
        var x = (uint)(t * 1103515245 + seed * 12345);
        x ^= x >> 13;
        x *= 0x5bd1e995u;
        x ^= x >> 15;
        return (x / (float)uint.MaxValue) * 2f - 1f;
    }

    /// <summary>
    /// A sound that never stops must still produce an event, and produce it while it is
    /// still sounding.
    ///
    /// This is the case the analyser used to get wrong, and nothing else covered it. The
    /// other direction tests render a transient and then let the level fall silent, so
    /// they pass whether or not events are emitted at the onset. The demo source injects
    /// events directly and never runs the analyser at all. So a soundtrack - continuous
    /// audio, which never meets the end-of-sound silence test - produced no events, and
    /// the overlay stayed blank while the log showed a live direction spectrum.
    /// </summary>
    private static Result TestSustainedSoundReportsWhileSounding()
    {
        var a = New7_1();
        var events = new List<AudioEvent>();
        var total = (int)(2.0 * SampleRate);
        var block = new float[1024 * 8];

        // Continuous noise on the front-left channel: loud, unchanging, never silent.
        for (var pos = 0; pos < total; pos += 1024)
        {
            var n = Math.Min(1024, total - pos);
            Array.Clear(block);
            for (var i = 0; i < n; i++)
            {
                var abs = pos + i;
                foreach (var ch in Fl)
                    block[i * 8 + ch] = 0.3f * Noise(abs, ch * 7919 + 13);
            }
            a.Process(block.AsSpan(0, n * 8), n, events);

            // The first event must arrive well before the sound ends. If events only
            // appear at the end, the analyser is reporting too late to be useful.
            if (events.Count > 0 && pos < total - SampleRate / 2)
            {
                var e = events[0];
                var reported = e.Timestamp;
                return new(
                    "a continuous sound is reported while it is still sounding",
                    reported < 1.0,
                    $"first event at {reported:F3}s of 2.0s of unbroken audio, " +
                    $"azimuth {e.Direction.AzimuthDegrees:F0} deg, level {e.Level:F2}");
            }
        }

        return new(
            "a continuous sound is reported while it is still sounding",
            false,
            $"no event at all from 2.0s of unbroken audio ({events.Count} events). "
            + "Continuous audio must not depend on silence to be reported.");
    }

    /// <summary>
    /// On stereo the direction spectrum must report the bearing the analyser measured, not
    /// the position of the physical channels.
    ///
    /// Stereo is a mix, not a pair of speakers. Publishing the left channel at its fixed
    /// -60 degrees and the right at +60 describes the wiring rather than the sound, so
    /// every bearing reads the same, the tracker has no gradient to follow, and a line
    /// never moves - it sits wherever its first event happened to land. That is what made
    /// a single bar stick to one side of the screen while the log showed the direction
    /// estimate correctly alternating left and right.
    ///
    /// So: a hard-panned left signal and a hard-panned right one must put the spectrum's
    /// peak on opposite sides of the screen, and a silent frame must leave it empty rather
    /// than reporting some arbitrary bin.
    /// </summary>
    private static Result TestStereoSpectrumFollowsTheMeasuredBearing()
    {
        const int Ch = 2;

        static (double Azimuth, double Level) Peak(SpatialAnalyzer a)
        {
            var s = a.Spectrum;
            var best = 0.0;
            var bestAz = double.NaN;
            for (var i = 0; i < s.Bins; i++)
                if (s[i] > best) { best = s[i]; bestAz = s.AzimuthOf(i); }
            return (bestAz, best);
        }

        // Broadband noise, with one ear hearing it later than the other. That delay is the
        // interaural time difference: the thing ITD measures, and what a source off to one
        // side of the head really produces. Noise rather than a tone because a steady sine
        // is identical in both channels apart from gain, so it has no delay to find and
        // every ITD estimator reads it as dead ahead. Footsteps and gunfire are broadband,
        // and so is this.
        //
        // `rightDelay` is how many samples late the right ear hears the source. Positive
        // means the source is on the LEFT, because the near ear always hears it first.
        static double Run(int rightDelay, int frames = 300)
        {
            var a = new SpatialAnalyzer(SpeakerPosition.Stereo, Ch, SampleRate);
            var events = new List<AudioEvent>();
            var block = new float[1024 * Ch];
            var total = frames * 1024;

            for (var i = 0; i < total; i++)
            {
                var p = i % 1024;
                block[p * Ch + 0] = Noise(i, 4242);
                block[p * Ch + 1] = i - rightDelay >= 0 ? Noise(i - rightDelay, 4242) : 0f;

                if (p == 1023) a.Process(block.AsSpan(0, 1024 * Ch), 1024, events);
            }

            var (az, level) = Peak(a);
            return level > 0.0005 ? az : double.NaN;
        }

        var left = Run(rightDelay: 20);
        var right = Run(rightDelay: -20);

        if (double.IsNaN(left))
            return new(N3, false, "a source on the left left the direction spectrum empty");
        if (double.IsNaN(right))
            return new(N3, false, "a source on the right left the direction spectrum empty");

        // The whole point: a source on each side must put the spectrum on that side. The
        // magnitude is deliberately not checked, because whether the front/back heuristic
        // calls a given delay ahead or behind is a separate question - and it is
        // asymmetric, so demanding mirror-image bearings would be testing that instead.
        if (left >= 0)
            return new(N3, false, $"a source on the left peaked at {left:F0} deg, expected the spectrum on the left (negative)");
        if (right <= 0)
            return new(N3, false, $"a source on the right peaked at {right:F0} deg, expected the spectrum on the right (positive)");

        // A silent frame must leave the spectrum empty, not report bin 0 at -172 deg.
        var quiet = new SpatialAnalyzer(SpeakerPosition.Stereo, Ch, SampleRate);
        var quietEvents = new List<AudioEvent>();
        var silence = new float[1024 * Ch];
        for (var f = 0; f < 60; f++) quiet.Process(silence.AsSpan(0, 1024 * Ch), 1024, quietEvents);
        var (qz, ql) = Peak(quiet);
        if (ql > 0.0005)
            return new(N3, false, $"digital silence still put {ql:F3} at {qz:F0} deg in the spectrum");

        return new(N3, true,
            $"a source on the left peaks at {left:F0} deg and on the right at {right:F0} deg; " +
            "silence reports nothing");
    }

    private const string N3 = "the stereo spectrum follows the measured bearing, not the channel positions";

    /// <summary>
    /// A source on the right must not be reported as dead ahead.
    ///
    /// The Woodworth ITD model is one-sided - <c>mid + sin(mid)</c> is positive across
    /// 0..pi/2 - so bisecting on a *negative* delay never advances the lower bound and
    /// collapses to zero. Every right-side sound was therefore reported as 0 degrees, and
    /// since 0 draws as "straight ahead" the right side of the screen stayed empty. Only
    /// the left worked, because a positive delay happens to bisect correctly.
    ///
    /// This is the bug behind "I can see a bar on the left side only". It is checked
    /// against <see cref="StereoItd"/> directly rather than through the whole pipeline, so
    /// a failure here points at the estimator instead of at the display.
    /// </summary>
    private static Result TestStereoItdResolvesBothSides()
    {
        // The near ear always hears a source first, so delaying the right channel puts
        // the source on the left, and vice versa.
        static double AzimuthFor(int rightDelay)
        {
            var itd = new StereoItd(SampleRate, 128);
            var block = new float[128 * 2];
            for (var i = 0; i < 20000; i++)
            {
                var p = i % 128;
                block[p * 2 + 0] = Noise(i, 4242);
                block[p * 2 + 1] = i - rightDelay >= 0 ? Noise(i - rightDelay, 4242) : 0f;
                if (p == 127) itd.Analyse(block, 128, 2);
            }
            return itd.Result.Direction.AzimuthDegrees;
        }

        var left = AzimuthFor(20);
        var right = AzimuthFor(-20);

        if (left >= 0)
            return new(N4, false,
                $"a source 20 samples late on the right reported {left:F1} deg; the right ear hears it last, so the source is on the left and the bearing must be negative");
        if (right <= 0)
            return new(N4, false,
                $"a source 20 samples early on the right reported {right:F1} deg; the right ear hears it first, so the source is on the right and the bearing must be positive");

        return new(N4, true,
            $"20 samples of delay reads {left:F0} deg on the left and {right:F0} deg on the right, so both sides resolve");
    }

    private const string N4 = "stereo ITD resolves a source on either side, not just the left";

    /// <summary>
    /// The same test on a sound that is partly tonal, which is most real sounds.
    ///
    /// <para>
    /// The ITD estimate is a cross-correlation, and a cross-correlation is only as
    /// trustworthy as its peak. Broadband noise gives a sharp, unambiguous peak;
    /// anything with strong periodic content gives a flat one, with several lags scoring
    /// almost as well. That is where the correlation window mattered.
    /// </para>
    ///
    /// <para>
    /// The window was trimmed at its right end only, because a delay can push samples
    /// past the end of the buffer but never before its start. Positive lags were
    /// therefore scored over fewer samples than negative ones and lost points for that
    /// reason alone. On noise that is a small bias. On a 440 Hz tone the correlation is
    /// so flat that the missing tail moved the peak past the true lag, and a source on
    /// the left was reported on the right: -291 us measured, +88.6 deg reported. On the
    /// user's machine, against a real signal, a sound on the left drew its line on the
    /// right-hand edge of the screen.
    /// </para>
    ///
    /// <para>
    /// <see cref="N4"/> cannot catch this. It uses broadband noise, chosen for good
    /// reason, and broadband noise is the one signal that hides the fault.
    /// </para>
    /// </summary>
    private static Result TestStereoItdResolvesBothSidesOnTonalSound()
    {
        // Harmonics with decaying amplitude: a voice, a gunshot body, an engine note.
        // Periodic, so the cross-correlation is flat and the peak is a real test.
        static float Tone(int t)
        {
            var v = 0.0;
            for (var h = 1; h <= 5; h++)
                v += Math.Sin(2.0 * Math.PI * 220.0 * h * t / SampleRate) / h;
            return (float)(v * 0.5);
        }

        // `rightDelay` is how many samples late the right ear hears it, so a positive
        // value puts the source on the LEFT and must read negative.
        static (double Azimuth, double Peak) AzimuthFor(int rightDelay)
        {
            var itd = new StereoItd(SampleRate, 128);
            var block = new float[128 * 2];
            for (var i = 0; i < 20000; i++)
            {
                var p = i % 128;
                block[p * 2 + 0] = Tone(i);
                block[p * 2 + 1] = i - rightDelay >= 0 ? Tone(i - rightDelay) : 0f;
                if (p == 127) itd.Analyse(block, 128, 2);
            }
            return (itd.Result.Direction.AzimuthDegrees, itd.LastCorrelationPeak);
        }

        var (left, leftPeak) = AzimuthFor(20);
        var (right, rightPeak) = AzimuthFor(-20);

        if (left >= 0)
            return new(N4b, false,
                $"a tonal source 20 samples late on the right reported {left:F1} deg; the right ear " +
                "hears it last, so the source is on the left and the bearing must be negative. A " +
                "correct bearing for a source this periodic is only possible if every candidate " +
                "lag was scored on the same samples.");
        if (right <= 0)
            return new(N4b, false,
                $"a tonal source 20 samples early on the right reported {right:F1} deg; the right " +
                "ear hears it first, so the source is on the right and the bearing must be positive");

        // The correlation must also be a real peak. A low value means the search settled on
        // a lag that is merely the least-bad one, which is how a truncated window announces
        // itself on a periodic signal.
        if (leftPeak < 0.9 || rightPeak < 0.9)
            return new(N4b, false,
                $"the correlation peaked at {leftPeak:F3} (left) and {rightPeak:F3} (right), below " +
                "0.9. A delayed copy of the same periodic signal should correlate almost perfectly, " +
                "so a low peak means the winning lag is an artefact of the window rather than the " +
                "signal.");

        return new(N4b, true,
            $"tonal source reads {left:F0} deg on the left and {right:F0} deg on the right, with " +
            $"correlation peaks of {leftPeak:F3} and {rightPeak:F3}");
    }

    private const string N4b = "stereo ITD resolves a source on either side for a periodic sound too";

    private static Result TestSingleSpeaker()
    {
        var a = New7_1();
        var events = Render(a, Fl, 2.0, 0.35f);
        if (events.Count == 0) return new("front-left transient is detected", false, "no event produced");
        var e = events[0];
        // FL sits at -60 degrees in the channel map.
        var ok = Math.Abs(e.Direction.AzimuthDegrees - (-60)) < 22;
        return new("front-left transient is detected and located",
            ok, $"azimuth {e.Direction.AzimuthDegrees:F1} deg (expected about -60), level {e.Level:F2}, {e.Class}");
    }

    private static Result TestDominatedSpeaker()
    {
        // A loud sound on Back Right with a little bleed onto Front Right: the
        // estimate should still land on the rear side.
        var a = New7_1();
        var events = Render(a, Br, 2.0, 0.5f, broadband: true);
        if (events.Count == 0) return new("rear-right is resolved", false, "no event produced");
        var e = events[0];
        var ok = e.Direction.AzimuthDegrees > 100;
        return new("rear-right is resolved",
            ok, $"azimuth {e.Direction.AzimuthDegrees:F1} deg (expected > 100)");
    }

    private static Result TestFrontBackResolved()
    {
        var front = New7_1();
        var fe = Render(front, Fc, 2.0, 0.4f);
        var back = New7_1();
        var be = Render(back, Bl, 2.0, 0.4f);

        if (fe.Count == 0 || be.Count == 0)
            return new("front and back are distinguished", false, "an event was missed");

        var f = Math.Abs(fe[0].Direction.AzimuthDegrees);
        var b = Math.Abs(be[0].Direction.AzimuthDegrees);
        // Frontness is what the overlay's vertical axis uses, so that is the number
        // that matters: front must land clearly above back on screen.
        var ok = f < 60 && b > 100;
        return new("front and back are distinguished",
            ok, $"front |az| {f:F0} deg, back |az| {b:F0} deg (need front < 60, back > 100)");
    }

    private static Result TestLoudnessOrdering()
    {
        var quiet = New7_1();
        var q = Render(quiet, Sl, 2.0, 0.05f);
        var loud = New7_1();
        var l = Render(loud, Sl, 2.0, 0.8f);

        if (q.Count == 0) return new("loudness maps to bar length", false, "quiet transient was not detected");
        if (l.Count == 0) return new("loudness maps to bar length", false, "loud transient was not detected");

        var ok = l[0].Level > q[0].Level + 0.3;
        return new("loudness maps to bar length",
            ok, $"quiet level {q[0].Level:F2}, loud level {l[0].Level:F2}");
    }

    private static Result TestSilenceProducesNothing()
    {
        var a = New7_1();
        var block = new float[1024 * 8];
        var events = new List<AudioEvent>();
        for (var i = 0; i < 200; i++) a.Process(block, 1024, events);
        return new("silence produces no events", events.Count == 0, $"{events.Count} events from digital silence");
    }

    /// <summary>
    /// Lines run inward from the left and right edges, and height on screen means
    /// front/behind. Three things have to hold, and the design rests on all of them:
    ///
    ///   * the sign of the bearing picks the edge and |bearing| picks the height, so both
    ///     halves of the direction survive rather than one being thrown away
    ///   * every line is horizontal and starts at a side edge, and none is ever anchored
    ///     to the top or the bottom, so nothing projects inward from those
    ///   * the band is inset, so the frontmost and rearmost cues are still well clear of
    ///     the top and bottom edges
    ///
    /// This builds real lines through <c>BuildLines</c> rather than sampling the mapping,
    /// because it is the drawn extent that has to stay clear, not the anchor point.
    /// </summary>
    private static Result TestLinesRunInwardFromTheSideEdges()
    {
        const int W = 2560, H = 1440;
        var style = OverlayStyle.Default();
        var inset = style.FieldInsetFraction * Math.Min(W, H);
        var sideInset = style.SideInsetFraction * Math.Min(W, H);

        static LineGeometry At(double az, OverlayStyle s)
            => OverlayLayout.BuildLines(
                new List<AudioEvent> { new(new Direction(az, 0), 1.0, 0, SoundClass.Gunshot, 1, 1, 1, 0) },
                W, H, s)[0];

        var ahead = At(0, style);
        var right = At(90, style);
        var behind = At(180, style);
        var left = At(-90, style);

        // Front is high, behind is low.
        if (ahead.Y > H * 0.5) return new(N, false, $"dead ahead landed at y={ahead.Y:F0}, expected the upper half");
        if (behind.Y < H * 0.5) return new(N, false, $"directly behind landed at y={behind.Y:F0}, expected the lower half");
        if (ahead.Y >= behind.Y) return new(N, false, $"front y={ahead.Y:F0} is not above behind y={behind.Y:F0}");

        // The sign picks the side, and each side runs inward.
        if (left.Dx <= 0) return new(N, false, $"a left-hand sound runs at dx={left.Dx:F2}, expected inward (rightwards)");
        if (right.Dx >= 0) return new(N, false, $"a right-hand sound runs at dx={right.Dx:F2}, expected inward (leftwards)");

        // 90 degrees is to the side, so it sits halfway up the band, and the two sides
        // mirror each other.
        if (Math.Abs(left.Y - right.Y) > 1) return new(N, false, $"left/right not mirrored: y={left.Y:F0} vs {right.Y:F0}");
        if (Math.Abs(left.Y - H * 0.5) > H * 0.1) return new(N, false, $"a sound at 90 degrees landed at y={left.Y:F0}, expected near the vertical middle {H * 0.5:F0}");

        for (var az = -180.0; az <= 180.0; az += 5)
        {
            var l = At(az, style);

            if (Math.Abs(l.Dy) > 1e-9)
                return new(N, false, $"azimuth {az:F0} drew a line at dy={l.Dy:F2}, every line must be horizontal");

            // X is the outer end, at the side edge, and dx points inward from it, so the
            // inner tip is reached by walking the direction over the line's length.
            var outerX = l.X;
            var innerX = l.X + l.Dx * l.Length;
            var expectedOuter = az < 0 ? sideInset : W - sideInset;
            if (Math.Abs(outerX - expectedOuter) > 1)
                return new(N, false, $"azimuth {az:F0} starts at x={outerX:F0}, expected the side edge at {expectedOuter:F0}");

            // Nothing may reach the top or the bottom, at any bearing, at full loudness.
            // Measured against the painted extent, which includes the outline - the
            // promise is about pixels on screen, not about the fill's geometry.
            var half = style.PaintedHalfThickness(W, H);
            var y0 = l.Y - half;
            var y1 = l.Y + half;
            if (y0 < inset - 0.5 || y1 > H - inset + 0.5)
                return new(N, false,
                    $"azimuth {az:F0} drew a line at y {y0:F0}..{y1:F0}, " +
                    $"which breaks the {inset:F0}px margin from the top and bottom edges");

            if (Math.Min(outerX, innerX) < 0 || Math.Max(outerX, innerX) > W)
                return new(N, false, $"azimuth {az:F0} drew a line spanning x {outerX:F0}..{innerX:F0}, outside the screen");
        }

        // Shrinking the band must pull the extremes toward the middle of the screen. This
        // is the knob that keeps the overlay off the game's own UI, so it has to work
        // rather than merely exist.
        var tight = OverlayStyle.Default();
        tight.FieldRadiusYFraction = 0.40;

        foreach (var az in new[] { 0.0, 60.0, 120.0, 180.0 })
        {
            var wide = At(az, style);
            var small = At(az, tight);
            if (Math.Abs(small.Y - H * 0.5) > Math.Abs(wide.Y - H * 0.5) + 0.5)
                return new(N, false,
                    $"azimuth {az:F0} did not move toward the middle when the band was shrunk: " +
                    $"y {wide.Y:F0} -> {small.Y:F0}");
        }

        return new(N, true,
            $"front y={ahead.Y:F0}, side y={left.Y:F0}, behind y={behind.Y:F0}; " +
            $"lines start at x={sideInset:F0} and {W - sideInset:F0} and run inward, " +
            $"all 73 bearings horizontal and inside a {inset:F0}px vertical margin");
    }

    private const string N = "lines run inward from the side edges, height meaning front or behind";

    /// <summary>
    /// A line must never grow beyond a fraction of the screen, because the whole point
    /// is that it stays in peripheral vision.
    /// </summary>
    private static Result TestLinesStaySmall()
    {
        const int W = 2560, H = 1440;
        var s = OverlayStyle.Default();
        var style = OverlayStyle.Default();
        style.MaxLengthFraction = s.MaxLengthFraction;
        style.MinLengthFraction = s.MinLengthFraction;
        style.LineThicknessFraction = s.LineThicknessFraction;

        var full = new List<AudioEvent>
        {
            new(new Direction(0, 0), 1.0, 0, SoundClass.Gunshot, 1, 1, 1, 0),
        };
        var lines = OverlayLayout.BuildLines(full, W, H, style);
        if (lines.Count != 1) return new("lines stay small", false, $"expected 1 line, got {lines.Count}");

        var l = lines[0];
        var limit = W / 8.0;
        if (l.Length > limit + 0.5)
            return new("lines stay small", false, $"loudest line is {l.Length:F0}px, limit is {limit:F0}px (1/8 of width)");

        return new("lines stay small", true, $"loudest line {l.Length:F0}px, thickness {l.Thickness:F0}px, limit {limit:F0}px");
    }

    /// <summary>
    /// Every line needs an outline, in a similar colour, or it disappears over a dark
    /// scene.
    ///
    /// The user's report: dark lines are invisible. Battlefield's night maps and
    /// interiors are mostly dark, and a single flat semi-transparent line over near-black
    /// is effectively not there. Two things have to hold for the fix to work, and both
    /// are invisible to a geometry test:
    ///
    ///   * the painted line really has two bands of different colour, so the rim is
    ///     being drawn at all - a style property that exists but is never used would
    ///     pass any amount of layout checking
    ///   * the outer band is *lighter* than the fill, because a rim darker than the line
    ///     does not separate it from a dark background, it just makes the line fatter
    ///
    /// Two similar colours, not a high-contrast frame: the check is that the outline
    /// keeps the fill's hue, not that it is a different colour entirely.
    /// </summary>
    private static Result TestLinesHaveAReadableOutline()
    {
        const int W = 800, H = 600;
        var style = OverlayStyle.Default();
        var renderer = new OverlayRenderer(style);

        var events = new List<AudioEvent>
        {
            new(new Direction(-40, 0), 1.0, 0, SoundClass.Gunshot, 1, 1, 1, 0),
        };

        var buffer = new PixelBuffer(W, H);
        renderer.Render(events, buffer);

        var line = OverlayLayout.BuildLines(events, W, H, style)[0];

        // Sample a column through the middle of the line, well past the anti-aliased
        // end caps, so what comes back is the line's own cross-section.
        var midX = Math.Clamp((int)(line.X + line.Dx * line.Length * 0.5), 0, W - 1);

        // The cross-section is sampled at two known distances from the line's centre
        // rather than scanned for bands, because the geometry already says where the two
        // colours must be: the fill occupies the middle, the rim the band outside it.
        //
        // A scan for "two distinct colours" would pass on a rim drawn down the centre of
        // the line, and a scan filtered by alpha would miss the rim entirely - the rim is
        // painted *under* the fill, so its alpha is the class colour's alpha and lower
        // than the composited centre. That is correct, not a defect; it just means
        // "which pixel is brightest" is not a reliable way to find it.
        var half = line.Thickness * 0.5;
        var outlinePx = style.OutlineWidthFraction * line.Thickness;

        static int Luma(int r, int g, int b) => r + g + b;

        // Un-premultiply, so two colours at different alphas compare like for like. The
        // buffer is premultiplied (see PixelBuffer.Pack), so a raw byte comparison
        // measures coverage as much as colour.
        static (int R, int G, int B) Straight(uint px)
        {
            var a = px >> 24;
            if (a == 0) return (0, 0, 0);
            return ((int)((px & 0xFF) * 255L / a),
                    (int)(((px >> 8) & 0xFF) * 255L / a),
                    (int)(((px >> 16) & 0xFF) * 255L / a));
        }

        uint At(double offsetFromCentre)
        {
            var py = (int)Math.Round(line.Y + offsetFromCentre);
            if (py < 0 || py >= H) return 0;
            return buffer.Pixels[py * W + midX];
        }

        if (At(0) == 0)
            return new(NO, false, $"nothing was painted at x={midX}");

        // Sample in the middle of each band, not at its inner boundary: the fill's
        // anti-aliased edge blends into the rim, so the pixel just outside the fill is
        // part of the fill's ramp rather than the rim.
        var fillPx = At(0);
        var rimOffset = half + outlinePx * 0.5;
        var rimAbove = At(-rimOffset);
        var rimBelow = At(rimOffset);

        if (rimAbove == 0 || rimBelow == 0)
            return new(NO, false,
                $"nothing is painted {rimOffset:F1}px from the line's centre " +
                $"(thickness {line.Thickness:F0}px, outline would put the rim at " +
                $"{rimOffset:F1}px), so the line is no wider than its fill");

        var fill = Straight(fillPx);
        var above = Straight(rimAbove);
        var below = Straight(rimBelow);

        // Alpha must be taken at face value. Blend used to hard-set the destination alpha
        // to 0xFF, which made every edge look solid while carrying colour premultiplied
        // for a much lower alpha - a dark halo, the exact thing premultiplication exists
        // to prevent. A line that is meant to be semi-transparent and reads as fully
        // opaque means the alpha channel is being ignored somewhere.
        var fillAlpha = fillPx >> 24;
        if (fillAlpha >= 255)
            return new(NO, false,
                $"the line's centre is fully opaque (alpha {fillAlpha}); a line is meant to be " +
                "semi-transparent, so a buffer claiming 255 here is not reporting its real coverage");

        // The rim has to be lighter than the fill, or it does not separate the line from
        // a dark background - it only makes the line fatter.
        var fillLuma = Luma(fill.R, fill.G, fill.B);
        var aboveLuma = Luma(above.R, above.G, above.B);
        var belowLuma = Luma(below.R, below.G, below.B);

        if (aboveLuma < fillLuma + 30 || belowLuma < fillLuma + 30)
            return new(NO, false,
                $"the rim is not lighter than the fill (fill luma {fillLuma}, " +
                $"rim {aboveLuma} above and {belowLuma} below), so it will not separate the " +
                "line from a dark scene");

        // Two similar colours, not two different ones. The rim is the fill pushed toward
        // white, so every channel moves the same way and the dominant one stays dominant.
        // An outline that inverted the hue would read as a second object rather than an
        // edge on the first.
        if (above.R < fill.R)
            return new(NO, false,
                $"the outline ({above.R},{above.G},{above.B}) is not a lighter version of the " +
                $"fill ({fill.R},{fill.G},{fill.B}); the two must be the same hue");

        var rimLuma = Math.Max(aboveLuma, belowLuma);
        return new(NO, true,
            $"rim {outlinePx:F1}px on each side of a {line.Thickness:F0}px line, sampled at " +
            $"{rimOffset:F1}px from centre; fill ({fill.R},{fill.G},{fill.B}) luma {fillLuma} to " +
            $"rim ({above.R},{above.G},{above.B}) luma {rimLuma}, same hue and lighter, " +
            $"centre alpha {fillAlpha}");
    }

    private const string NO = "every line has a lighter outline of the same hue, so it reads on a dark scene";

    /// <summary>
    /// The behaviour the user asked for by name: a sound moving from the left to the
    /// right should make the line on the left get shorter while the line on the right
    /// gets longer, rather than the line blinking out and a new one blinking in.
    /// </summary>
    private static Result TestLineFollowsAMovingSound()
    {
        var tracker = new EventTracker();
        var spectrum = new DirectionSpectrum();

        // A single cue starting hard left, loud.
        tracker.Push(new AudioEvent(new Direction(-70, 0), 0.8, -20, SoundClass.Footstep, 1, 1, 1, 0));
        for (var i = 0; i < 5; i++) { PushLevel(spectrum, -70, 0.8); tracker.Follow(spectrum, 1.0 / 60); tracker.Tick(1.0 / 60); }

        var startAz = tracker.Visible[0].Direction.AzimuthDegrees;
        var startLen = LengthFor(tracker);

        // Now the same sound walks from -70 degrees through dead ahead to +70.
        for (var step = 1; step <= 60; step++)
        {
            var az = -70 + 140.0 * step / 60.0;
            spectrum.Clear();
            PushLevel(spectrum, az, 0.8);
            tracker.Follow(spectrum, 1.0 / 60);
            tracker.Tick(1.0 / 60);
        }

        if (tracker.Visible.Count != 1)
            return new(N2, false, $"expected the line to stay a single line, got {tracker.Visible.Count}");

        var endAz = tracker.Visible[0].Direction.AzimuthDegrees;
        var endLen = LengthFor(tracker);

        if (endAz < 50)
            return new(N2, false, $"line only travelled from {startAz:F0} to {endAz:F0} degrees");

        // The length must have come back down as the source moved away from where it
        // was, otherwise the left side would stay lit forever.
        if (endLen >= startLen - 0.02)
            return new(N2, false, $"length did not shrink while moving: {startLen:F0} -> {endLen:F0}");

        return new(N2, true,
            $"one line travelled {startAz:F0} to {endAz:F0} degrees, length {startLen:F0} -> {endLen:F0}");
    }

    private const string N2 = "a moving sound slides one line across the screen";

    private static void PushLevel(DirectionSpectrum s, double azimuth, double level)
        => s.Add(azimuth, level);

    /// <summary>Line length in pixels for the tracker's current loudest line.</summary>
    private static double LengthFor(EventTracker tracker)
    {
        var best = 0.0;
        foreach (var e in tracker.Visible) if (e.Level > best) best = e.Level;
        return OverlayLayout.LengthForLevel(best, 2560, OverlayStyle.Default());
    }

    /// <summary>
    /// The level a line is drawn at must track the level measured, not the peak of a
    /// past event. This is the difference between a meter and a frozen bar.
    /// </summary>
    private static Result TestLineFallsWhenSoundStops()
    {
        var tracker = new EventTracker();
        var spectrum = new DirectionSpectrum();

        tracker.Push(new AudioEvent(new Direction(-40, 0), 0.9, -18, SoundClass.Footstep, 1, 1, 1, 0));
        for (var i = 0; i < 5; i++)
        {
            spectrum.Clear();
            spectrum.Add(-40, 0.9);
            tracker.Follow(spectrum, 1.0 / 60);
            tracker.Tick(1.0 / 60);
        }
        var loud = tracker.Visible.Count > 0 ? tracker.Visible[0].Level : 0.0;

        // The sound stops: an empty spectrum each frame.
        for (var i = 0; i < 60; i++)
        {
            spectrum.Clear();
            tracker.Follow(spectrum, 1.0 / 60);
            tracker.Tick(1.0 / 60);
        }

        var quiet = tracker.Visible.Count > 0 ? tracker.Visible[0].Level : 0.0;
        var ok = loud > 0.7 && quiet < 0.02;
        return new("a line falls away when the sound stops", ok,
            $"level {loud:F2} while sounding -> {quiet:F2} after one second of silence");
    }

    private const string N8 = "a paused overlay keeps the lines it had, instead of going blank";

    /// <summary>
    /// Pausing must freeze the display, not clear it.
    ///
    /// <para>
    /// The user reported the overlay vanishing partway through a game and coming back
    /// only after showing the desktop. The log showed the cause: the hotkey paused the
    /// app, and pausing called <c>EventTracker.Clear</c>. Every line was wiped, so a
    /// pause was pixel-for-pixel identical to a crash, and the user's reasonable
    /// conclusion was that something had broken.
    /// </para>
    ///
    /// <para>
    /// The check holds the tracker frozen for far longer than any line could survive
    /// unfrozen - ten seconds against a hold of 0.9 s and a fade of 0.35 s - so it fails
    /// unless the freeze genuinely suspends retirement.
    /// </para>
    /// </summary>
    private static Result TestFrozenOverlayKeepsItsLines()
    {
        var tracker = new EventTracker();
        var spectrum = new DirectionSpectrum();

        tracker.Push(new AudioEvent(new Direction(-40, 0), 0.9, -18, SoundClass.Footstep, 1, 1, 1, 0));
        for (var i = 0; i < 5; i++)
        {
            spectrum.Clear();
            spectrum.Add(-40, 0.9);
            tracker.Follow(spectrum, 1.0 / 60);
            tracker.Tick(1.0 / 60);
        }
        if (tracker.Visible.Count == 0)
            return new(N8, false, "the test setup never produced a line to freeze");

        // Paused: the sound field stops updating, exactly as it does when the analyser
        // is skipped. What must not happen is the display emptying.
        tracker.Freeze();
        for (var i = 0; i < 600; i++)   // ten seconds at 60 Hz
        {
            spectrum.Clear();
            tracker.Follow(spectrum, 1.0 / 60);
            tracker.Tick(1.0 / 60);
        }

        if (tracker.Visible.Count == 0)
            return new(N8, false,
                "ten seconds of pause emptied the display, so a pause is indistinguishable from a crash");

        var held = tracker.Visible[0].Level;

        // Thawed, and still nothing sounding, the line must be allowed to go away -
        // otherwise "pause" would have become "stuck on screen forever".
        tracker.Thaw();
        for (var i = 0; i < 180; i++)
        {
            spectrum.Clear();
            tracker.Follow(spectrum, 1.0 / 60);
            tracker.Tick(1.0 / 60);
        }
        if (tracker.Visible.Count > 0)
            return new(N8, false, $"after resuming with nothing sounding, {tracker.Visible.Count} line(s) are still up");

        return new(N8, true,
            $"a line held at level {held:F2} through ten seconds of pause, and cleared once resumed");
    }

    private const string N5 = "a loud sound shows a line even with no recent onset";

    /// <summary>
    /// A loud sound must put a line on screen even when no onset has fired recently.
    ///
    /// <para>
    /// This is the fault the user's field log showed. Over one stretch of play the
    /// direction spectrum was loud on 109 of 119 one-second heartbeats, at levels of
    /// 0.4 to 0.8, and a line was drawn on 27 of them. <c>Follow</c> could only update
    /// tracks that already existed, and a track was only ever created by an onset. So a
    /// continuous sound - music, sustained gunfire - fired one onset, kept sounding, and
    /// then drew nothing for as long as it played. That is why music showed nothing and
    /// why shots "were not shown at all".
    /// </para>
    ///
    /// <para>
    /// The events are discarded on purpose. Pushing them would let the tracker's
    /// existing onset path create the line, and the check would pass whether or not the
    /// spectrum can start anything. The whole claim under test is that the sound field
    /// on its own is enough.
    /// </para>
    /// </summary>
    private static Result TestSpectrumCanStartALine()
    {
        const int Ch = 2;
        var analyzer = new SpatialAnalyzer(SpeakerPosition.Stereo, Ch, SampleRate);
        var events = new List<AudioEvent>();
        var tracker = new EventTracker();
        var block = new float[1024 * Ch];
        var dt = 1024.0 / SampleRate;

        // A continuous source on the left: the right ear hears it 20 samples late. No
        // transient anywhere, so this is the music case - it starts once and then never
        // stops, which is exactly when the onset count stops growing.
        var peakSpectrum = 0.0;
        for (var f = 0; f < 300; f++)
        {
            for (var i = 0; i < 1024; i++)
            {
                var n = f * 1024 + i;
                block[i * Ch + 0] = Noise(n, 4242);
                block[i * Ch + 1] = n - 20 >= 0 ? Noise(n - 20, 4242) : 0f;
            }

            analyzer.Process(block.AsSpan(0, 1024 * Ch), 1024, events);
            events.Clear();

            for (var b = 0; b < analyzer.Spectrum.Bins; b++)
                if (analyzer.Spectrum[b] > peakSpectrum) peakSpectrum = analyzer.Spectrum[b];

            tracker.Follow(analyzer.Spectrum, dt);
            tracker.Tick(dt);
        }

        if (peakSpectrum < 0.2)
            return new(N5, false,
                $"the test signal never reached the spectrum (peak {peakSpectrum:F2}), so it proves nothing");

        if (tracker.Visible.Count == 0)
            return new(N5, false,
                $"the spectrum peaked at {peakSpectrum:F2} for 300 frames and not one line was drawn, " +
                "because a track can only be created by an onset");

        if (!tracker.Visible.Any(e => e.Direction.AzimuthDegrees < 0))
            return new(N5, false,
                "a line was drawn but none on the left: " +
                string.Join(", ", tracker.Visible.Select(e => $"{e.Direction.AzimuthDegrees:F0}")));

        return new(N5, true,
            $"spectrum peaked at {peakSpectrum:F2} with no onset, and it drew " +
            $"{tracker.Visible.Count} line(s), including one on the left");
    }
}
