using System.Reflection;
using BaldingAudio.App.Audio;
using BaldingAudio.Core.Audio;

namespace BaldingAudio.App;

/// <summary>
/// Checks on the capture-to-UI handoff, which is the one place where a fault is
/// invisible from inside the DSP.
///
/// <para>
/// Everything the analyser does is covered by <see cref="Core.Diagnostics.SelfTest"/>,
/// and everything Win32 is covered by <see cref="Audio.InteropSelfCheck"/>. What sits
/// between them is <see cref="SpectrumExchange"/>, and a fault there is reported as a
/// broken analyser. The field case: 69.5% of heartbeats logged <c>loudest silent</c>
/// while the peak level on the same line read -28 dBFS and events were still being
/// emitted, because the exchange was publishing a stale zero-filled buffer on alternating
/// frames and the 60 Hz UI kept landing on those.
///
/// These run on Linux under <c>--selftest</c> and need no audio device, which is the
/// point - the bug they cover was invisible on the user's machine and would have been
/// again.
/// </para>
/// </summary>
internal static class AppSelfCheck
{
    internal record struct Check(string Name, bool Passed, string Detail);

    public static IReadOnlyList<Check> RunAll(Action<string>? log = null)
    {
        var results = new List<Check>
        {
            CheckPublishDeliversTheCurrentFrame(),
            CheckAmbiguitySurvivesTheHandoff(),
            CheckSnapshotIsStableWhileTheCaptureThreadRuns(),
            CheckHealthyIterationDoesNotReopen(),
            CheckSilenceCanNeverCauseAReopen(),
            CheckFailureClassificationDecidesTheLoop(),
            CheckBackoffClearsAfterAStreamProvesItself(),
        };

        foreach (var r in results)
            log?.Invoke($"  [{(r.Passed ? "PASS" : "FAIL")}] {r.Name}: {r.Detail}");

        return results;
    }

    public static bool AllPassed(IReadOnlyList<Check> results) => results.All(r => r.Passed);

    private static Check CheckPublishDeliversTheCurrentFrame()
    {
        var exchange = new SpectrumExchange();
        var spectrum = new DirectionSpectrum(exchange.Bins);

        // The first frame must arrive, and must carry its data. This is the frame the
        // original swap dropped: it published the freshly zero-filled spare instead.
        spectrum.Clear();
        spectrum.Add(-45, 0.75);
        exchange.Publish(spectrum);

        var first = exchange.Snapshot();
        if (first is null)
            return new(Name, false, "nothing was published by the first Publish");

        // DirectionSpectrum.Add spreads energy across the two nearest bins, so 0.75 lands
        // as two bins of 0.375. Compare against the total, which is what the overlay
        // actually reads, and which an all-zero buffer cannot fake.
        var firstTotal = Total(first);
        if (firstTotal < 0.6)
            return new(Name, false,
                $"the first published frame carried {firstTotal:F3} of energy, expected the 0.75 that " +
                "was published. A freshly allocated buffer is all zeros, so publishing one instead of " +
                "the frame just written makes the UI see silence.");

        // Then the case the field hit: a frame with no measurable energy. The analyser
        // clears its spectrum and adds nothing on such a frame. The UI must see that
        // frame as empty - and the frame *after* it must be live again, which is what the
        // old two-frames-stale swap broke.
        spectrum.Clear();
        exchange.Publish(spectrum);

        var quiet = exchange.Snapshot();
        if (quiet is null || Total(quiet) > 0.0005)
            return new(Name, false, "a frame with no energy did not read as empty");

        spectrum.Clear();
        spectrum.Add(120, 0.6);
        exchange.Publish(spectrum);

        var recovered = exchange.Snapshot();
        if (recovered is null)
            return new(Name, false, "nothing was published after a quiet frame");

        var recoveredPeak = Total(recovered);
        if (recoveredPeak < 0.5)
            return new(Name, false,
                $"the frame after a quiet one carried {recoveredPeak:F3} of energy, expected 0.6. " +
                "This is the alternating empty frame: the exchange published the buffer written " +
                "two frames ago, so half of all frames arrived stale and zero-filled.");

        // And the bearing has to come through, not just the level: a buffer swapped in the
        // wrong place can carry the right magnitude at the wrong bearing.
        var bestBin = 0;
        var best = 0.0;
        for (var i = 0; i < recovered.Bins; i++)
            if (recovered[i] > best) { best = recovered[i]; bestBin = i; }

        var azimuth = recovered.AzimuthOf(bestBin);
        if (Math.Abs(azimuth - 120) > 20)
            return new(Name, false,
                $"the strongest bin reads {azimuth:F0} deg, expected about 120. " +
                "The exchange must carry the spectrum as published, not a shifted copy.");

        return new(Name, true,
            $"first frame {firstTotal:F2}, quiet frame empty, following frame {recoveredPeak:F2} " +
            $"at {azimuth:F0} deg - every published frame is the one just written");
    }

    private const string Name = "the capture thread's published spectrum reaches the UI intact";

    /// <summary>
    /// The UI must never see a frame being rewritten underneath it.
    ///
    /// The whole reason for the double buffer is that the analyser mutates its spectrum
    /// in place; a torn read there would show a line that flickers between two bearings.
    /// Checked by snapshotting while a writer is publishing, and confirming every
    /// snapshot is internally consistent.
    /// </summary>
    private const string AmbiguityName =
        "the 'side unknown' flag survives the capture-to-UI handoff";

    /// <summary>
    /// The ambiguity flag has to arrive at the UI thread, and it is easy to leave it
    /// behind.
    ///
    /// <para>
    /// <c>Snapshot</c> rebuilds a fresh <see cref="DirectionSpectrum"/> from the bins
    /// alone, so a flag set on the capture thread's instance arrives false. The headless
    /// checks publish and read the same object, so they would not notice: the overlay
    /// would keep drawing a cue with no known side on one arbitrary edge while every
    /// check stayed green. That is precisely the "a check that cannot fail" trap, and it
    /// is the whole reason this check lives next to the exchange rather than in Core.
    /// </para>
    /// </summary>
    private static Check CheckAmbiguitySurvivesTheHandoff()
    {
        var exchange = new SpectrumExchange();
        var spectrum = new DirectionSpectrum(exchange.Bins);

        // A source with no knowable side: loud, and flagged.
        spectrum.Clear();
        spectrum.Add(-6, 0.8);
        spectrum.Ambiguous = true;
        exchange.Publish(spectrum);

        var flagged = exchange.Snapshot();
        if (flagged is null)
            return new(AmbiguityName, false, "nothing was published by the first Publish");
        if (!flagged.Ambiguous)
            return new(AmbiguityName, false,
                "a frame published as having no known side arrived at the UI thread claiming to " +
                "know its side, so the overlay would draw it on one arbitrary edge");

        // And a frame that DOES know its side must not inherit the flag from the frame
        // before it, which is the same class of mistake one step later.
        spectrum.Clear();
        spectrum.Add(-70, 0.8);
        spectrum.Ambiguous = false;
        exchange.Publish(spectrum);

        var decided = exchange.Snapshot();
        if (decided is null)
            return new(AmbiguityName, false, "the second Publish did not arrive");
        if (decided.Ambiguous)
            return new(AmbiguityName, false,
                "a frame with a definite side arrived flagged as unknown, so a real sound " +
                "would be mirrored onto both edges");

        return new(AmbiguityName, true,
            "a frame with no known side arrived flagged, and a frame with a definite side arrived unflagged");
    }

    private static Check CheckSnapshotIsStableWhileTheCaptureThreadRuns()
    {
        var exchange = new SpectrumExchange();
        using var stop = new CancellationTokenSource();

        // Each frame puts all its energy in one bin, so a torn read is detectable: any
        // snapshot showing energy in two bins at once was read mid-write.
        var writer = new Thread(() =>
        {
            var s = new DirectionSpectrum(exchange.Bins);
            var frame = 0;
            while (!stop.IsCancellationRequested)
            {
                s.Clear();
                s.Add(-180 + 360.0 * (frame % exchange.Bins) / exchange.Bins, 0.9);
                exchange.Publish(s);
                frame++;
            }
        })
        {
            IsBackground = true,
            Name = "baldingAudio.selftest.w spectrum",
        };
        writer.Start();

        var torn = 0;
        var reads = 0;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                var snapshot = exchange.Snapshot();
                if (snapshot is null) continue;

                reads++;
                var occupied = 0;
                for (var i = 0; i < snapshot.Bins; i++)
                    if (snapshot[i] > 0.5) occupied++;

                if (occupied > 1) torn++;
            }
        }
        finally
        {
            stop.Cancel();
            writer.Join(1000);
        }

        if (reads < 100)
            return new(Name2, false, $"only {reads} snapshots completed, too few to judge stability");

        if (torn > 0)
            return new(Name2, false,
                $"{torn} of {reads} snapshots showed energy in more than one bin, so the UI thread " +
                "read a buffer while the capture thread was rewriting it");

        return new(Name2, true, $"{reads} snapshots during concurrent publishing, none torn");
    }

    private const string Name2 = "a snapshot is never read while the capture thread is writing it";

    /// <summary>
    /// A capture loop with nothing wrong with it must leave the endpoint alone.
    ///
    /// <para>
    /// The field bug, and the reason this exists. The loop was written to re-open the
    /// endpoint on any failure, and the re-open ended up outside the failure handling, so
    /// it also ran on a perfectly healthy iteration - where there is no failure to report
    /// and the reason is null. The endpoint was torn down and rebuilt about ten times a
    /// second, and a stream that is destroyed every 100 ms never has time to deliver
    /// audio, so the overlay showed nothing at all. The process stayed alive, the 60 Hz
    /// heartbeat kept ticking, and every line said <c>capture ok</c> - because the thread
    /// was running, which is true, and the thread had nothing to work with, which it does
    /// not check.
    /// </para>
    ///
    /// <para>
    /// So the assertion is the negative one, which is the only one that matters: given a
    /// healthy iteration, the answer must be "keep going" and nothing else.
    /// </para>
    /// </summary>
    private static Check CheckHealthyIterationDoesNotReopen()
    {
        var healthy = WasapiLoopbackCapture.Decide(threwRecoverable: false, threwFatal: false);

        if (healthy != WasapiLoopbackCapture.LoopAction.KeepGoing)
            return new(Name3, false,
                $"a healthy iteration returned {healthy}, so the loop re-opens the endpoint while " +
                "everything is working. Each re-open destroys the stream before it can deliver " +
                "audio, so the overlay stays empty. The recovery has to be reachable only from a " +
                "real failure.");

        // Checked across a full second of iterations rather than once, because a decision
        // that is correct once but wrong per-iteration still tears the endpoint down ~10x a
        // second. This is the rate the field run showed.
        var reopensInOneSecond = 0;
        for (var iteration = 0; iteration < 250; iteration++)
        {
            if (WasapiLoopbackCapture.Decide(false, false) != WasapiLoopbackCapture.LoopAction.KeepGoing)
                reopensInOneSecond++;
        }

        if (reopensInOneSecond > 0)
            return new(Name3, false,
                $"{reopensInOneSecond} of 250 consecutive healthy iterations chose to re-open");

        return new(Name3, true,
            "a healthy iteration re-opens nothing, in 250 consecutive iterations - the endpoint " +
            "survives to deliver audio");
    }

    private const string Name3 = "a healthy capture loop leaves the endpoint alone";

    /// <summary>
    /// Silence must be incapable of causing a re-open, at any duration.
    ///
    /// <para>
    /// This is the strongest form the assertion can take, and the field run is why. A
    /// WASAPI loopback stream delivers nothing while the endpoint is not playing - not a
    /// trickle, nothing, and <c>GetNextPacketSize</c> returns
    /// <c>AUDCLNT_S_BUFFER_EMPTY</c> indefinitely. Measured: 55 seconds of the app running
    /// with nothing playing, zero packets, then correct bearings within a second of audio
    /// starting.
    /// </para>
    ///
    /// <para>
    /// So "no packets" is the normal idle state and is indistinguishable from a dead
    /// stream. A stall threshold does not separate them, it picks a point on the overlap
    /// and destroys working endpoints past it. 5 seconds of quiet between sounds did
    /// exactly that: the stream was re-opened, and the replacement never delivered, so the
    /// overlay was permanently blank while the log said <c>capture ok</c>. That is worse
    /// than the disappearing overlay the stall check was added to fix.
    /// </para>
    ///
    /// <para>
    /// The check asserts the input is <em>absent</em> from the decision, not merely that
    /// some duration is tolerated. A silent, never-played endpoint and a healthy one are
    /// the same input now, so no value can push the decision to re-open, and the mistake
    /// cannot be reintroduced without this failing first.
    /// </para>
    /// </summary>
    private static Check CheckSilenceCanNeverCauseAReopen()
    {
        var decide = typeof(WasapiLoopbackCapture)
            .GetMethod("Decide", BindingFlags.NonPublic | BindingFlags.Static);

        if (decide is null)
            return new(Name4, false, "the capture loop's decision function could not be found");

        var parameters = decide.GetParameters().Select(p => p.Name ?? "?").ToArray();
        var silenceInputs = parameters
            .Where(n => n.Contains("silence", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("stall", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("sawPacket", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (silenceInputs.Length > 0)
            return new(Name4, false,
                $"the capture loop's decision takes {string.Join(", ", silenceInputs)}. A loopback " +
                "stream delivers nothing while nothing is playing, so silence is the normal idle " +
                "state and not a fault signal. Re-opening on a timer tore down a working endpoint " +
                "and left the overlay permanently blank. Recovery belongs to thrown HRESULTs, which " +
                "are unambiguous.");

        // The field case exactly: healthy, and the endpoint is not playing.
        var idle = WasapiLoopbackCapture.Decide(threwRecoverable: false, threwFatal: false);
        if (idle != WasapiLoopbackCapture.LoopAction.KeepGoing)
            return new(Name4, false, $"a silent, healthy endpoint returned {idle}, expected KeepGoing");

        // A recoverable failure must still re-open, or the overlay disappears when the
        // device is invalidated. Removing silence must not have removed recovery.
        var recovered = WasapiLoopbackCapture.Decide(threwRecoverable: true, threwFatal: false);
        if (recovered != WasapiLoopbackCapture.LoopAction.Reopen)
            return new(Name4, false, $"a recoverable failure returned {recovered}, expected Reopen");

        return new(Name4, true,
            "the decision takes no silence input at all, so a quiet endpoint can never re-open " +
            "the stream, and thrown HRESULTs still recover");
    }

    private const string Name4 = "silence can never cause the endpoint to be re-opened";

    /// <summary>
    /// The three loop outcomes are driven by the HRESULT classification, and the loop
    /// applies them to the same <c>Decide</c> the healthy and stall cases above test.
    ///
    /// <para>
    /// This ties the interop checks to the behaviour that depends on them. The
    /// classification itself is asserted in <see cref="Audio.InteropSelfCheck"/> against the
    /// SDK; what was not covered until now is that a recoverable HRESULT actually reaches
    /// the recovery path and a fatal one actually stops the thread. A fault in either
    /// direction is silent in the field: too many recoveries looks like a busy log, and too
    /// few looks like the overlay "just not working today".
    /// </para>
    /// </summary>
    private static Check CheckFailureClassificationDecidesTheLoop()
    {
        // The real interop constants, not literals. This check is only worth anything if it
        // tests the same values the capture loop branches on, and a copy would drift.
        // Read into locals because they are `static readonly`, not `const`.
        var invalidated = MMDevice.AUDCLNT_E_DEVICE_INVALIDATED;
        var notAnInvalidation = MMDevice.AUDCLNT_E_WRONG_ENDPOINT_TYPE;

        if (WasapiLoopbackCapture.IsRecoverableFailure(invalidated) != true)
            return new(Name5, false,
                $"AUDCLNT_E_DEVICE_INVALIDATED (0x{(uint)invalidated:X8}) is not classified as " +
                "recoverable, so an endpoint that was invalidated is never re-opened. This is the " +
                "fault that left the overlay permanently dead until the app was restarted.");

        if (WasapiLoopbackCapture.IsRecoverableFailure(notAnInvalidation))
            return new(Name5, false,
                $"AUDCLNT_E_WRONG_ENDPOINT_TYPE (0x{(uint)notAnInvalidation:X8}) is classified as " +
                "recoverable. It is a permanent mistake in how the stream was opened, so retrying " +
                "it forever leaves the thread spinning on a failure it can never get past.");

        // Recoverable HRESULT -> the loop recovers. This is the whole point of the fix for
        // the disappearing overlay.
        var recoverable = WasapiLoopbackCapture.Decide(threwRecoverable: true, threwFatal: false);
        if (recoverable != WasapiLoopbackCapture.LoopAction.Reopen)
            return new(Name5, false,
                $"a recoverable failure returned {recoverable}, expected Reopen");

        // Fatal HRESULT -> the loop stops, rather than retrying something it cannot fix.
        var fatal = WasapiLoopbackCapture.Decide(threwRecoverable: false, threwFatal: true);
        if (fatal != WasapiLoopbackCapture.LoopAction.Stop)
            return new(Name5, false,
                $"an unrecoverable failure returned {fatal}, expected Stop");

        // And a fatal HRESULT wins over a recoverable one, so a loop cannot be left running
        // on the strength of a stale flag.
        var both = WasapiLoopbackCapture.Decide(threwRecoverable: true, threwFatal: true);
        if (both != WasapiLoopbackCapture.LoopAction.Stop)
            return new(Name5, false, $"with both flags set the loop returned {both}, expected Stop");

        return new(Name5, true,
            "recoverable HRESULT re-opens the endpoint, unrecoverable stops the thread, and a " +
            "non-invalidation HRESULT is not treated as recoverable");
    }

    private const string Name5 = "the HRESULT classification reaches the recovery path";

    /// <summary>
    /// A re-opened stream that keeps working must clear the restart backoff.
    ///
    /// <para>
    /// The count is only used to lengthen the delay between recovery attempts, so that
    /// an endpoint that is genuinely gone does not spin. It is never reset by anything
    /// else, which makes clearing it the difference between recovering promptly and
    /// being stuck at the 2 s ceiling for the rest of the session.
    /// </para>
    ///
    /// <para>
    /// The check exists because the clear was dead code and nothing said so. It was
    /// guarded on the time since the last packet, but called from inside the packet
    /// handler on the line after that timestamp was set to the current time, so the
    /// elapsed value was always zero and the guard always failed. The comment above it
    /// described behaviour that did not exist. Extracted as a pure function so the
    /// threshold can be asserted rather than inferred.
    /// </para>
    /// </summary>
    private static Check CheckBackoffClearsAfterAStreamProvesItself()
    {
        const long required = 10_000;

        // A stream that has just been re-opened has proved nothing yet.
        if (WasapiLoopbackCapture.IsProvenStable(0, required))
            return new(Name6, false, "a stream that has only just been opened counts as proven stable");

        if (WasapiLoopbackCapture.IsProvenStable(required - 1, required))
            return new(Name6, false, "a stream one millisecond short of the threshold counts as proven stable");

        if (!WasapiLoopbackCapture.IsProvenStable(required, required))
            return new(Name6, false, $"a stream open for {required} ms does not count as proven stable");

        if (!WasapiLoopbackCapture.IsProvenStable(required * 10, required))
            return new(Name6, false, "a stream open far longer than the threshold does not count as proven stable");

        return new(Name6, true,
            "the backoff clears once a re-opened stream has kept delivering past the threshold, and " +
            "not before, so a stream that dies immediately cannot reset it every attempt");
    }

    private const string Name6 = "the recovery backoff clears once a stream proves itself";

    /// <summary>
    /// Total energy across all bins, which is what <c>Add</c> was given before it spread
    /// it between the two nearest bins. Comparing sums rather than a single bin, so the
    /// assertion holds regardless of where between two bins the bearing happens to fall.
    /// </summary>
    private static double Total(DirectionSpectrum s)
    {
        var sum = 0.0;
        for (var i = 0; i < s.Bins; i++) sum += s[i];
        return sum;
    }
}
