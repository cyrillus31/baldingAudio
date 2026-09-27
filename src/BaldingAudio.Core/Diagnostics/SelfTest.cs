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
public static class SelfTest
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
            TestStereoSpectrumFollowsTheMeasuredBearing(),
            TestSingleSpeaker(),
            TestDominatedSpeaker(),
            TestLoudnessOrdering(),
            TestFrontBackResolved(),
            TestSilenceProducesNothing(),
            TestLinesRunInwardFromTheSideEdges(),
            TestLinesStaySmall(),
            TestLineFollowsAMovingSound(),
            TestLineFallsWhenSoundStops(),
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
            var y0 = l.Y - l.Thickness * 0.5;
            var y1 = l.Y + l.Thickness * 0.5;
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
}
