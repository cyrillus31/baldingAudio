using System.Text;
using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Dsp;
using BaldingAudio.Core.Overlay;

namespace BaldingAudio.Core.Diagnostics;

/// <summary>
/// Checks on the synthesised test audio behind the settings window.
///
/// <para>
/// The failure this guards against is a silent one. A scene that generates correctly but
/// writes a malformed WAV plays nothing at all, and a scene that plays but is panned the
/// wrong way still draws a bar - just on the wrong edge. Both look the same from the
/// user's side: the buttons do something they cannot account for. Neither is visible in a
/// screenshot or a log line.
/// </para>
///
/// <para>
/// Pan is checked closed-loop, through the real <see cref="SpatialAnalyzer"/>, and
/// against the decibels asked for rather than a remembered constant. It is checked for
/// sign as well as magnitude, because a reversed pan still produces a bar and still
/// produces a plausible-looking number - it did, until this check read the sign.
/// </para>
/// </summary>
public static partial class SelfTest
{
    /// <summary>
    /// Runs a scene through the analyser and reports, per block, the loudest reading and
    /// every onset.
    /// </summary>
    private readonly record struct SceneRead(
        double BalanceAtLoudest,
        double LoudestLevel,
        List<double> OnsetTimes,
        List<(double Time, double Balance, double Level)> Trace)
    {
        /// <summary>
        /// One balance per onset: the clearest reading within a short window after each
        /// detected sound.
        ///
        /// <para>
        /// Asked per-onset rather than over the whole trace, because a walk is mostly
        /// silence and silence has a balance of exactly zero. A median over every frame of
        /// a crossing scene is zero in both halves and reports that nothing moved when in
        /// fact everything did; a level threshold to exclude the gaps just moves the magic
        /// number rather than removing it. Onsets are where the sounds are, so asking there
        /// needs no threshold at all - and "which side was each individual footstep on" is
        /// the question the scene exists to answer.
        /// </para>
        /// </summary>
        public List<(double Time, double Balance)> BalancePerOnset(double windowSeconds = 0.25)
        {
            var readings = new List<(double Time, double Balance)>();
            foreach (var onset in OnsetTimes)
            {
                var best = double.NaN;
                foreach (var (time, balance, _) in Trace)
                {
                    if (time < onset || time > onset + windowSeconds) continue;
                    if (double.IsNaN(best) || Math.Abs(balance) > Math.Abs(best)) best = balance;
                }

                if (!double.IsNaN(best)) readings.Add((onset, best));
            }

            return readings;
        }
    }

    private static SceneRead ReadScene(TestSignals.SceneSpec spec, int rate = 44100, int blockSize = 1024)
    {
        spec = spec with { SampleRate = rate };
        var pcm = TestSignals.Render(spec);

        var analyzer = new SpatialAnalyzer(SpeakerPosition.Stereo, 2, rate);
        var events = new List<AudioEvent>();
        var block = new float[blockSize * 2];
        var onsets = new List<double>();
        var trace = new List<(double Time, double Balance, double Level)>();
        var loudest = -1.0;
        var balanceAtLoudest = double.NaN;

        for (var f = 0; f * blockSize * 2 < pcm.Length; f++)
        {
            for (var i = 0; i < blockSize; i++)
            {
                var idx = f * blockSize * 2 + i * 2;
                block[i * 2 + 0] = idx < pcm.Length ? pcm[idx] : 0f;
                block[i * 2 + 1] = idx + 1 < pcm.Length ? pcm[idx + 1] : 0f;
            }

            var before = events.Count;
            analyzer.Process(block.AsSpan(0, blockSize * 2), blockSize, events);
            if (events.Count > before)
                onsets.Add(f * blockSize / (double)rate);

            var (balance, hasBalance) = analyzer.Spectrum.Balance is double b ? (b, true) : (double.NaN, false);
            var (_, level) = analyzer.Spectrum.Peak();
            if (hasBalance) trace.Add((f * blockSize / (double)rate, balance, level));

            // Sampled at the loudest frame, not the last. A scene ends in silence between
            // shots, and reading the spectrum there reports no balance at all.
            if (level > loudest && hasBalance)
            {
                loudest = level;
                balanceAtLoudest = balance;
            }
        }

        return new(balanceAtLoudest, loudest, onsets, trace);
    }

    /// <summary>
    /// Pairs each footstep in the scene with the onset that detected it, and reports the
    /// clearest balance measured during that onset.
    ///
    /// <para>
    /// The scene also fires distant rounds at random, 20 dB down and near-centred, so
    /// nearest-onset matching kept landing on those and reported a footstep asked for at
    /// 6 dB as measuring 0.00. The footstep is the loud thing near a footstep, so of the
    /// candidate onsets the loudest one wins.
    /// </para>
    /// </summary>
    private static List<(TestSignals.Transient Step, double Balance)> MatchFootsteps(
        TestSignals.SceneSpec spec, SceneRead read, double toleranceSeconds = 0.2, double window = 0.25)
    {
        var readings = new List<(TestSignals.Transient, double)>();

        foreach (var step in spec.Transients.Where(t => t.Voice == TestSignals.Voice.Footstep))
        {
            var candidates = read.OnsetTimes
                .Where(o => Math.Abs(o - step.AtSeconds) <= toleranceSeconds)
                .Select(o => (Onset: o, Frames: read.Trace
                    .Where(t => t.Time >= o && t.Time <= o + window)
                    .ToList()))
                .Where(x => x.Frames.Count > 0)
                .OrderByDescending(x => x.Frames.Max(t => t.Level))
                .ToList();

            if (candidates.Count == 0) continue;

            var clearest = candidates[0].Frames.OrderByDescending(t => Math.Abs(t.Balance)).First();
            readings.Add((step, clearest.Balance));
        }

        return readings;
    }

    private const string N12 = "test audio is panned to the side and by the amount that was asked for";

    private static Result TestTestAudioIsPannedAsAsked()
    {
        // Single transients at a known pan, so the measurement has one thing in it. The
        // expected value is the closed form, not a constant, so the check keeps meaning
        // whatever the decibel conversion does.
        static double Expected(double panDb)
        {
            var g = Math.Pow(10.0, Math.Abs(panDb) / 10.0);
            return Math.Sign(panDb) * (g - 1.0) / (g + 1.0);
        }

        foreach (var pan in new[] { 6.0, 9.0, -6.0, -9.0 })
        {
            var read = ReadScene(new TestSignals.SceneSpec
            {
                Seconds = 2.0,
                Ambience = false,
                Transients = new[]
                {
                    new TestSignals.Transient
                    {
                        AtSeconds = 0.2,
                        PanDb = pan,
                        Voice = TestSignals.Voice.Gunshot,
                    },
                },
            });

            if (double.IsNaN(read.BalanceAtLoudest))
                return new(N12, false,
                    $"a shot panned {pan:F0} dB published no balance, so the test facility would show " +
                    "nothing at all and look broken rather than wrong");

            if (read.OnsetTimes.Count == 0)
                return new(N12, false,
                    $"a shot panned {pan:F0} dB produced no onset; the sound is not reaching the " +
                    "detector, so pressing the button would appear to do nothing");

            var expected = Expected(pan);

            // Sign first. A reversed pan still draws a bar, still reports a magnitude, and
            // is the failure a screenshot cannot show.
            if (Math.Sign(read.BalanceAtLoudest) != Math.Sign(expected))
                return new(N12, false,
                    $"a shot panned {pan:F0} dB measured {read.BalanceAtLoudest:F3}, which is the wrong " +
                    "side; the bar would be drawn on the opposite edge to the sound");

            if (Math.Abs(read.BalanceAtLoudest - expected) > 0.12)
                return new(N12, false,
                    $"a shot panned {pan:F0} dB measured {read.BalanceAtLoudest:F3}, expected about " +
                    $"{expected:F3} (within 0.12); the pan and the measurement disagree, so the " +
                    "facility would be teaching the wrong threshold");

            // And it has to clear the floor the layout draws above, or the button does
            // nothing on screen no matter how well the pan is right.
            var floor = OverlayStyle.Default().BalanceFloor;
            if (Math.Abs(read.BalanceAtLoudest) < floor)
                return new(N12, false,
                    $"a shot panned {pan:F0} dB measured {read.BalanceAtLoudest:F3}, under the " +
                    $"{floor:F3} the layout needs before it draws anything; the default pan is too " +
                    "small to demonstrate anything");
        }

        // Dead ahead must be dead ahead, since that is the one guarantee the display model
        // makes and the button that demonstrates it must not contradict.
        var centred = ReadScene(new TestSignals.SceneSpec
        {
            Seconds = 1.5,
            Ambience = false,
            Transients = new[]
            {
                new TestSignals.Transient { AtSeconds = 0.2, PanDb = 0, Voice = TestSignals.Voice.Gunshot },
            },
        });

        if (Math.Abs(centred.BalanceAtLoudest) > 0.05)
            return new(N12, false,
                $"a shot with no pan measured {centred.BalanceAtLoudest:F3}; dead ahead is supposed to " +
                "draw nothing, and the button that shows that must not show a bar");

        return new(N12, true,
            "shots panned 6 and 9 dB each way measure the closed form's balance on the correct side, " +
            "clear the draw floor, and an unpanned one measures " +
            $"{Math.Abs(centred.BalanceAtLoudest):F3}");
    }

    private const string N15 = "the crossing footstep is measured crossing, not stuck on one side";

    /// <summary>
    /// The second scene exists to answer one question: does the overlay show a sound
    /// moving across? A direction indicator that reports a bearing but never shows
    /// anything sliding is the open complaint, and it is not answered by checking that a
    /// single panned shot lands on the right edge.
    ///
    /// <para>
    /// So this reads the balance trace across the whole scene and requires it to start on
    /// one side and finish on the other. A stuck reading - the sign frozen, or a trace
    /// with no spread - fails, and so does one that starts and ends on the same side.
    /// </para>
    /// </summary>
    private static Result TestCrossingFootstepIsMeasuredCrossing()
    {
        var spec = TestSignals.DistantWarWithCrossingFootsteps();
        var read = ReadScene(spec);
        var footsteps = spec.Transients.Count(t => t.Voice == TestSignals.Voice.Footstep);
        var matched = MatchFootsteps(spec, read);

        if (matched.Count < 6)
            return new(N15, false,
                $"only {matched.Count} of {footsteps} footsteps produced an onset within 200 ms " +
                $"across a {spec.Seconds:F0}-second walk; the walk is not being detected");

        // Every step that is meant to be clearly off to one side has to be measured on that
        // side, and has to clear the floor the layout draws above. Steps near the centre are
        // exempt from the second half: dead ahead draws nothing, by design.
        const double ClearlyOffToOneSide = 4.0;
        var offToOneSide = matched
            .Where(r => Math.Abs(r.Step.PanDb) >= ClearlyOffToOneSide)
            .ToList();

        if (offToOneSide.Count < 6)
            return new(N15, false,
                $"only {offToOneSide.Count} footsteps are more than {ClearlyOffToOneSide:F0} dB off centre; " +
                "the scene is not set up to demonstrate a crossing");

        var floor = OverlayStyle.Default().BalanceFloor;

        var wrongSide = offToOneSide.Where(r => Math.Sign(r.Balance) != Math.Sign(r.Step.PanDb)).ToList();
        if (wrongSide.Count > 0)
            return new(N15, false,
                $"{wrongSide.Count} of {offToOneSide.Count} footsteps were measured on the wrong side, " +
                $"the first asked for {wrongSide[0].Step.PanDb:F1} dB and measured " +
                $"{wrongSide[0].Balance:F2}; a line would be drawn on the edge the sound is not on");

        var belowFloor = offToOneSide.Where(r => Math.Abs(r.Balance) < floor).ToList();
        if (belowFloor.Count > offToOneSide.Count / 4)
            return new(N15, false,
                $"{belowFloor.Count} of {offToOneSide.Count} footsteps more than " +
                $"{ClearlyOffToOneSide:F0} dB off centre measured under the {floor:F2} the layout draws " +
                "above; they would be heard but never seen");

        var first = offToOneSide.OrderBy(r => r.Step.AtSeconds).First();
        var last = offToOneSide.OrderBy(r => r.Step.AtSeconds).Last();

        // Different sides at the two ends is the crossing. (This was inverted once, and
        // then a walk that crossed cleanly was reported as not crossing.)
        if (Math.Sign(first.Balance) == Math.Sign(last.Balance))
            return new(N15, false,
                $"the walk starts on the {Side(first.Balance)} at {first.Balance:F2} and ends on the " +
                $"{Side(last.Balance)} at {last.Balance:F2}; both ends are on the same side, so it does " +
                "not cross");

        var startStrong = offToOneSide.Count(r => r.Step.PanDb > 0 && Math.Abs(r.Balance) >= floor);
        var endStrong = offToOneSide.Count(r => r.Step.PanDb < 0 && Math.Abs(r.Balance) >= floor);
        var startTotal = offToOneSide.Count(r => r.Step.PanDb > 0);
        var endTotal = offToOneSide.Count(r => r.Step.PanDb < 0);

        return new(N15, true,
            $"all {offToOneSide.Count} footsteps more than {ClearlyOffToOneSide:F0} dB off centre are " +
            $"measured on the correct side, {startStrong}/{startTotal} and {endStrong}/{endTotal} clear " +
            $"the {floor:F2} draw floor, and the walk goes from {first.Balance:F2} to {last.Balance:F2}, " +
            "so it crosses the screen");

        static string Side(double balance) => balance >= 0 ? "right" : "left";
    }

    private const string N13 = "the generated audio is a WAV file the system will actually play";

    /// <summary>
    /// The header is hand-built, so it is checked by hand.
    ///
    /// <para>
    /// A wrong chunk size, byte rate or magic number produces a file the system rejects,
    /// and the whole test facility then does nothing at all with no error anywhere - the
    /// buttons appear dead. The derived numbers are checked against each other rather
    /// than repeated, so a consistently changed format still passes and an inconsistent
    /// one cannot.
    /// </para>
    /// </summary>
    private static Result TestGeneratedWavIsWellFormed()
    {
        const int Rate = 44100;
        var pcm = TestSignals.Render(TestSignals.GunshotsBothSides(seconds: 1.0));
        var wav = TestSignals.ToWavBytes(pcm, Rate);

        string Tag(int offset, int length) => Encoding.ASCII.GetString(wav, offset, length);
        int U32(int offset) => BitConverter.ToInt32(wav, offset);
        int U16(int offset) => BitConverter.ToUInt16(wav, offset);

        if (wav.Length < 44)
            return new(N13, false, $"the file is {wav.Length} bytes, too short to hold a header");

        if (Tag(0, 4) != "RIFF" || Tag(8, 4) != "WAVE")
            return new(N13, false,
                $"the header starts {Tag(0, 4)}/{Tag(8, 4)}, expected RIFF/WAVE; the system rejects " +
                "the file and the test buttons do nothing");

        if (Tag(12, 4) != "fmt ")
            return new(N13, false, $"the chunk at offset 12 is {Tag(12, 4)}, expected fmt ");

        if (U32(16) != 16)
            return new(N13, false,
                $"the format chunk declares {U32(16)} bytes; PCM is 16, and a mismatch puts the data " +
                "somewhere other than where the header says it is");

        if (U16(20) != 1)
            return new(N13, false, $"format tag {U16(20)} is not 1 (PCM)");

        var channels = U16(22);
        if (channels != 2)
            return new(N13, false,
                $"the file declares {channels} channels; the analyser is being exercised on stereo, " +
                "so a mono file would answer a different question");

        if (U32(24) != Rate)
            return new(N13, false, $"the file declares {U32(24)} Hz, expected {Rate}");

        var bits = U16(34);
        if (bits != 16)
            return new(N13, false, $"{bits} bits per sample, expected 16");

        var blockAlign = U16(32);
        if (blockAlign != channels * bits / 8)
            return new(N13, false,
                $"block align {blockAlign} does not match {channels} channels of {bits} bits " +
                $"({channels * bits / 8})");

        var byteRate = U32(28);
        if (byteRate != Rate * blockAlign)
            return new(N13, false,
                $"byte rate {byteRate} does not match {Rate} Hz x {blockAlign} bytes ({Rate * blockAlign})");

        if (Tag(36, 4) != "data")
            return new(N13, false, $"the chunk at offset 36 is {Tag(36, 4)}, expected data");

        var frames = pcm.Length / 2;
        var dataSize = U32(40);
        if (dataSize != frames * 4)
            return new(N13, false,
                $"the data chunk declares {dataSize} bytes for {frames} stereo frames, which is {frames * 4}");

        if (U32(4) != wav.Length - 8)
            return new(N13, false,
                $"the RIFF size field says {U32(4)} but the file is {wav.Length} bytes, so it should be " +
                $"{wav.Length - 8}");

        return new(N13, true,
            $"a {wav.Length}-byte RIFF/WAVE PCM file: {channels}ch, {Rate} Hz, {bits}-bit, byte rate and " +
            $"block align both consistent, {frames} frames declared and present");
    }

    private const string N14 = "the distant war bed does not register as a sound off to one side";

    /// <summary>
    /// The bed is the thing that must <i>not</i> draw, so that a footstep to one side can
    /// be seen surviving it. A lopsided bed would draw a permanent bar and be
    /// indistinguishable from the signal under test.
    /// </summary>
    private static Result TestAmbienceDoesNotFakeADirection()
    {
        // A long bed with no footsteps in it at all. Nothing else is in the scene, so any
        // side it reports is its own.
        var bedOnly = ReadScene(new TestSignals.SceneSpec
        {
            Seconds = 3.0,
            Ambience = true,
            Transients = Array.Empty<TestSignals.Transient>(),
        });

        if (Math.Abs(bedOnly.BalanceAtLoudest) > 0.15)
            return new(N14, false,
                $"the bed on its own measures a balance of {bedOnly.BalanceAtLoudest:F2}; it is meant to " +
                "be neutral, and a lopsided one looks like a sound off to one side");

        // And the bed must not be so loud that it hides what is meant to be heard over it.
        // This is the case the per-band selection exists for, and the reason
        // AmbienceOffsetDb is a documented knob rather than a constant.
        //
        // Measured at frame level, not by counting events. An earlier version counted
        // footsteps that produced their own event, and read 14 of 14 with no bed against 7
        // of 14 with one - which looked exactly like a bed masking half the footsteps. It
        // is not. SpatialAnalyzer only emits an event on the not-sounding -> sounding
        // transition, and _inEvent clears only after 120 ms at 18 dB below the event's peak.
        // A continuous bed never allows that, so consecutive footsteps merge into one long
        // event by design. Lowering the bed from -24 dB to -8 dB changed nothing (still 6
        // or 7 of 14), which is the clue: a masking problem would have moved with the level.
        //
        // What actually matters is whether the direction is still measurable, because the
        // spectrum is published every frame and EventTracker.Follow reads it - so a merged
        // event still gets a line that follows the walk across. That is what is checked.
        var walk = TestSignals.DistantWarWithCrossingFootsteps(seconds: 6.0);
        var walkRead = ReadScene(walk);
        var floor = OverlayStyle.Default().BalanceFloor;

        var expected = walk.Transients
            .Where(t => t.Voice == TestSignals.Voice.Footstep && Math.Abs(t.PanDb) >= 4.0)
            .ToList();

        var right = 0;
        var left = 0;
        var wrongSide = 0;

        foreach (var step in expected)
        {
            // The clearest balance in the footstep's own window, no onset required.
            var window = walkRead.Trace
                .Where(t => t.Time >= step.AtSeconds && t.Time <= step.AtSeconds + 0.15)
                .ToList();

            if (window.Count == 0) { wrongSide++; continue; }

            var clearest = window.OrderByDescending(t => Math.Abs(t.Balance)).First();
            if (Math.Sign(clearest.Balance) != Math.Sign(step.PanDb)) wrongSide++;
            else if (clearest.Balance > 0) right++;
            else left++;
        }

        if (expected.Count < 4)
            return new(N14, false,
                $"only {expected.Count} footsteps are more than 4 dB off centre in a 6-second walk; the " +
                "scene is not set up to show the bed sitting behind a directional signal");

        if (wrongSide > 0)
            return new(N14, false,
                $"{wrongSide} of {expected.Count} footsteps more than 4 dB off centre were not measured on " +
                $"the correct side over the bed ({right} read right, {left} read left); the bed is hiding " +
                "the direction, which is the one thing it must not do");

        // Both sides have to appear, or the scene is not actually crossing and the check
        // would pass on a walk stuck to one edge.
        if (right == 0 || left == 0)
            return new(N14, false,
                $"over the bed the walk read {right} frames right and {left} left; one side is missing, so " +
                "the bed is holding the walk to a single edge");

        return new(N14, true,
            $"the bed alone measures {Math.Abs(bedOnly.BalanceAtLoudest):F2}, and all " +
            $"{expected.Count} footsteps more than 4 dB off centre are still measured on the correct side " +
            $"over it ({right} right, {left} left), so it hides neither the sound nor its direction");
    }
}
