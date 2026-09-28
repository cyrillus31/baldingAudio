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
            CheckSnapshotIsStableWhileTheCaptureThreadRuns(),
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
