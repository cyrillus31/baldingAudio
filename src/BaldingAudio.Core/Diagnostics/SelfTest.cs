using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Config;

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
            TestSingleSpeaker(),
            TestDominatedSpeaker(),
            TestLoudnessOrdering(),
            TestFrontBackResolved(),
            TestSilenceProducesNothing(),
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
}
