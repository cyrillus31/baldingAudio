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
            TestSingleSpeaker(),
            TestDominatedSpeaker(),
            TestLoudnessOrdering(),
            TestFrontBackResolved(),
            TestSilenceProducesNothing(),
            TestCompassMapsBearingsToBorder(),
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
    /// The display is a compass on the screen border: dead ahead must be the top edge,
    /// behind must be the bottom edge, and the two must be at the same height, so that
    /// "higher on the screen" and "further from the bottom" mean the same thing.
    /// </summary>
    private static Result TestCompassMapsBearingsToBorder()
    {
        const int W = 2560, H = 1440;
        var s = OverlayStyle.Default();
        var inset = s.LineThicknessFraction * Math.Min(W, H) * 0.5;

        static (double x, double y) At(int w, int h, double az, double inset)
        {
            OverlayLayout.BorderAnchor(w, h, az, inset, out var x, out var y, out _, out _);
            return (x, y);
        }

        var ahead = At(W, H, 0, inset);
        var right = At(W, H, 90, inset);
        var behind = At(W, H, 180, inset);
        var left = At(W, H, -90, inset);

        if (ahead.y > inset + 4) return new(N, false, $"dead ahead landed at y={ahead.y:F0}, expected the top edge");
        if (behind.y < H - inset - 4) return new(N, false, $"behind landed at y={behind.y:F0}, expected the bottom edge");
        if (right.x < W - inset - 4) return new(N, false, $"right landed at x={right.x:F0}, expected the right edge");
        if (left.x > inset + 4) return new(N, false, $"left landed at x={left.x:F0}, expected the left edge");

        // Left and right are the mirror of each other, and front and back are too.
        if (Math.Abs(right.y - left.y) > 1) return new(N, false, $"left/right not mirrored: y={left.y:F0} vs {right.y:F0}");
        if (Math.Abs(ahead.x - behind.x) > 1) return new(N, false, $"front/back not aligned: x={ahead.x:F0} vs {behind.x:F0}");

        // Anything at all should be inside the screen, or it would be clipped.
        for (var az = -180.0; az <= 180.0; az += 5)
        {
            var p = At(W, H, az, inset);
            if (p.x < 0 || p.x > W || p.y < 0 || p.y > H)
                return new(N, false, $"azimuth {az:F0} landed outside the screen at ({p.x:F0},{p.y:F0})");
        }

        return new(N, true,
            $"front y={ahead.y:F0}, rear y={behind.y:F0}, right x={right.x:F0}, left x={left.x:F0}; all 73 bearings on-screen");
    }

    private const string N = "compass maps bearings onto the screen border";

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
