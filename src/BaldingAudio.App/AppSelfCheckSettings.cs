using BaldingAudio.App.Config;
using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Dsp;
using BaldingAudio.Core.Overlay;

namespace BaldingAudio.App;

/// <summary>
/// Checks on the settings window's three claims.
/// </summary>
/// <para>
/// Each of these is a claim the window makes to the user, and each is a way the window
/// can be confidently, visibly wrong:
///
/// <list type="bullet">
/// <item>
/// <b>It says whether a sound would draw.</b> If that verdict disagrees with what the
/// renderer actually does, the window is worse than no window: it tells the user a
/// sound will show when it will not, and they raise the threshold to compensate for a
/// fault that is not there.
/// </item>
/// <item>
/// <b>The meter does not flicker past the comparison.</b> A meter that shows a value
/// for 20 ms out of every 200 cannot be used to set anything.
/// </item>
/// <item>
/// <b>Moving a slider changes the overlay.</b> The whole premise of dynamic config. It
/// works only because the renderer holds the same <see cref="OverlayStyle"/> instance
/// the config object does, with no copy and no apply step - a property nothing in the
/// code guarantees, so it is asserted here rather than assumed.
/// </item>
/// </list>
///
/// <para>
/// The verdict check deliberately drives the real <see cref="OverlayRenderer"/> rather
/// than re-deriving the comparison. Two implementations of "above the floor" agreeing
/// with each other proves nothing; the renderer agreeing with the window is the thing.
/// </para>
/// </summary>
internal static partial class AppSelfCheck
{
    private static Check CheckTheVerdictAgreesWithWhatTheRendererDraws()
    {
        const int W = 1920, H = 1080;
        var config = new AppConfig();
        var renderer = new OverlayRenderer(config.Style);
        var buffer = new PixelBuffer(W, H);

        // A spread of imbalances, each carried by one event. The spread straddles every
        // threshold the slider offers, so the two are compared across the whole range
        // rather than at one lucky point.
        var balances = new[] { 0.05, 0.12, 0.20, 0.28, 0.33, 0.40, 0.52, 0.65, 0.78, -0.20, -0.45, -0.60 };
        var ev = new AudioEvent(
            Direction.Forward, Level: 0.8, Dbfs: -20,
            SoundClass.Footstep, ClassConfidence: 1, Confidence: 1,
            DistanceConfidence: 0, Timestamp: 0);

        foreach (var floorDb in new[] { 1.0, 3.0, 6.0, 9.0, 12.0 })
        {
            config.Style.BalanceFloorDb = floorDb;

            var readout = new LiveReadout(
                HasAudio: true, CaptureState: "ok", Layout: "stereo (2ch)",
                IsMultichannel: false,
                Balance: null, PeakHoldBalance: null, BalanceHoldSeconds: 1.1,
                LoudestDbfs: -20, NoiseFloorDb: -60, LevelUnit: 0.8,
                FloorDb: floorDb, LinesDrawn: 0, TracksTracked: 0, EventsSeen: 0, Paused: false);

            foreach (var b in balances)
            {
                var withPeak = readout with { PeakHoldBalance = b };

                renderer.Render(new[] { ev with { Balance = b } }, buffer);
                var drawnByRenderer = renderer.LastLineCount > 0;

                if (withPeak.PeakWouldDraw != drawnByRenderer)
                    return new(Name8, false,
                        $"at a {floorDb:F0} dB threshold a balance of {b:F2} ({withPeak.PeakHoldDb:F1} dB) is " +
                        $"reported as {(withPeak.PeakWouldDraw ? "drawn" : "hidden")} but the renderer " +
                        $"{(drawnByRenderer ? "drew" : "drew nothing")}; the window would send the user " +
                        "tuning against a verdict the overlay does not produce");

                // And the side it names has to be the side the renderer paints, which is
                // the polarity invariant one level up.
                if (withPeak.PeakWouldDraw)
                {
                    var expectedSide = b < 0 ? "left" : "right";
                    if (withPeak.Side != expectedSide)
                        return new(Name8, false,
                            $"a balance of {b:F2} on the {expectedSide} is reported as the " +
                            $"{withPeak.Side}");
                }
            }
        }

        return new(Name8, true,
            $"across {balances.Length} imbalances and thresholds of 1 to 12 dB, the drawn/hidden verdict " +
            "matches the renderer on every one, and the side it names matches the sign of the balance");
    }

    private const string Name8 = "the window's drawn-or-hidden verdict matches what the overlay actually draws";

    private static Check CheckTheMeterKeepsAPeakLongEnoughToReadIt()
    {
        var hold = new PeakHold(fallSeconds: 1.6);

        // One loud footstep, then silence. At the window's 20 Hz refresh the sample lands
        // once and then there is nothing; without a hold the number is gone before it can
        // be compared with anything.
        hold.Push(0.45, 0.05);
        if (!hold.Holding)
            return new(Name9, false, "a single reading of 0.45 was not held at all, so the meter shows " +
                "nothing between sounds and cannot be used to set a threshold");

        if (Math.Abs(hold.Value - 0.45) > 1e-9)
            return new(Name9, false, $"the held value is {hold.Value:F3} rather than the 0.45 that arrived");

        // And it must still be there a good fraction of a second later - long enough to
        // read a number and move a slider.
        for (var i = 0; i < 8; i++) hold.Push(null, 0.05);   // 400 ms of silence
        if (!hold.Holding || Math.Abs(hold.Value) < 0.30)
            return new(Name9, false,
                $"after 400 ms of silence the held value is {hold.Value:F3}; a peak that falls away in a " +
                "few frames is no more readable than no peak at all");

        // A stronger reading must take over, and a weaker one must not.
        hold.Push(0.10, 0.05);
        if (Math.Abs(hold.Value) < 0.30)
            return new(Name9, false,
                $"a weaker reading of 0.10 replaced the held 0.45, leaving {hold.Value:F3}; the meter would " +
                "drop back down mid-decay and lose the peak it was asked to keep");

        hold.Push(0.80, 0.05);
        if (Math.Abs(hold.Value - 0.80) > 1e-9)
            return new(Name9, false, $"a stronger reading of 0.80 left the meter at {hold.Value:F3}");

        // Silence has to empty it eventually, or the meter shows a stale sound forever and
        // the user chases a ghost.
        for (var i = 0; i < 200; i++) hold.Push(null, 0.05);  // 10 s
        if (hold.Holding)
            return new(Name9, false, $"10 seconds of silence left the meter at {hold.Value:F3}; a peak " +
                "that never clears is worse than none, because it looks like a live reading");

        return new(Name9, true,
            "a 0.45 peak is still at 0.45 immediately and above 0.30 after 400 ms, a stronger reading " +
            "replaces it, a weaker one does not, and 10 s of silence clears it");
    }

    private const string Name9 = "the meter's peak survives long enough to be compared with the threshold";

    private static Check CheckMovingASliderChangesTheOverlay()
    {
        const int W = 1920, H = 1080;
        var config = new AppConfig();
        var renderer = new OverlayRenderer(config.Style);
        var buffer = new PixelBuffer(W, H);

        // A sound 2.5 dB off to the right: hidden at the 3 dB default, drawn at 2.
        var ev = new AudioEvent(
            Direction.Forward, Level: 0.8, Dbfs: -20,
            SoundClass.Footstep, ClassConfidence: 1, Confidence: 1,
            DistanceConfidence: 0, Timestamp: 0)
        { Balance = Decibel.BalanceFromDb(2.5) };

        var events = new List<AudioEvent> { ev };

        // Default first, so the starting point is the state the user actually launches in.
        config.Style.BalanceFloorDb = 3.0;
        renderer.Render(events, buffer);
        var atDefault = renderer.LastLineCount;

        // This is the load-bearing line: the renderer was handed config.Style once, in
        // the constructor, and must still be reading that same object now. If it had
        // copied it, every slider in the window would move a number and change nothing.
        config.Style.BalanceFloorDb = 2.0;
        renderer.Render(events, buffer);
        var atTwo = renderer.LastLineCount;

        if (atDefault != 0)
            return new(Name10, false,
                $"a sound 2.5 dB off centre drew {atDefault} line(s) at the 3 dB default; the starting " +
                "threshold is already letting through a sound it should hide");

        if (atTwo == 0)
            return new(Name10, false,
                "lowering the threshold to 2 dB changed nothing: the renderer is not reading the same " +
                "style object the window writes to, so every slider would move a number and change nothing");

        // And the other direction, so it is a live setting rather than a one-way latch.
        config.Style.BalanceFloorDb = 9.0;
        renderer.Render(events, buffer);
        var atNine = renderer.LastLineCount;

        if (atNine != 0)
            return new(Name10, false,
                $"raising the threshold to 9 dB still drew {atNine} line(s) for a sound 2.5 dB off centre");

        // The geometry sliders go through the same object, so check one of those too.
        config.Style.BalanceFloorDb = 2.0;
        renderer.Render(events, buffer);
        var shortBar = CountPainted(buffer);

        config.Style.MaxLengthFraction = 0.20;
        renderer.Render(events, buffer);
        var longBar = CountPainted(buffer);

        if (longBar <= shortBar)
            return new(Name10, false,
                $"raising the longest-line setting from 0.125 to 0.20 of the width changed the painted " +
                $"area from {shortBar} to {longBar} pixels; a geometry slider that does not move the " +
                "geometry is worse than one that is absent");

        return new(Name10, true,
            "the same sound is hidden at 3 dB, drawn at 2 dB and hidden again at 9 dB, and raising the " +
            $"longest-line setting grows the painted area from {shortBar} to {longBar} pixels, all without " +
            "any apply step - the renderer reads the config object directly");
    }

    private const string Name10 = "moving a slider changes the overlay on the next frame, with no apply step";

    /// <summary>
    /// The same hold, driven through the code that actually runs in production.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the other two checks were not enough. They exercise
    /// <see cref="PeakHold"/> and <see cref="LiveReadout"/> as separate pieces, and a
    /// fault in between - the host handing the raw spectrum balance to the readout
    /// instead of the held one - leaves both of them green. That is not a hypothetical
    /// gap: injecting exactly that fault and re-running the suite changed nothing.
    /// </para>
    /// <para>
    /// So the check drives <see cref="ReadoutBuilder.Build"/>, which is the only code
    /// that runs in the app, and requires the number that comes out to still be there
    /// after the sound has stopped. An unheld value is zero on every quiet frame, so
    /// this is exactly the case the wiring would break and nothing else would notice.
    /// </para>
    /// </remarks>
    private static Check CheckTheReadoutTheAppBuildsActuallyHolds()
    {
        const double Step = 0.05;   // the settings window's 20 Hz refresh
        var builder = new ReadoutBuilder(fallSeconds: 1.6);

        var inputs = new ReadoutInputs(
            HasAudio: true, CaptureState: "ok", Layout: "stereo (2ch)",
            FloorDb: 3.0, LoudestDbfs: -24, NoiseFloorDb: -70,
            LinesDrawn: 1, TracksTracked: 1, EventsSeen: 42, Paused: false);

        // One footstep, 6 dB to the right.
        var loud = new DirectionSpectrum();
        loud.Balance = Decibel.BalanceFromDb(6.0);
        loud.Add(0, 0.5);

        var duringSound = builder.Build(loud, inputs, Step);
        if (duringSound.PeakHoldBalance is not double heldNow || Math.Abs(heldNow) < 0.3)
            return new(Name11, false,
                $"a sound 6 dB to the right produced a held balance of {duringSound.PeakHoldBalance}; " +
                "the readout the app builds is not carrying the measurement at all");

        // Then silence, and the number has to still be readable.
        var quiet = new DirectionSpectrum();
        quiet.Balance = 0.0;

        LiveReadout afterSilence = duringSound;
        for (var i = 0; i < 6; i++) afterSilence = builder.Build(quiet, inputs, Step);

        if (afterSilence.PeakHoldBalance is null)
            return new(Name11, false,
                "the readout the app builds reported no held balance 300 ms after the sound stopped; the " +
                "meter's value is being passed through unheld, so it reads zero between sounds and the " +
                "window cannot be used to set a threshold");

        if (Math.Abs(afterSilence.PeakHoldBalance!.Value) < Math.Abs(heldNow) * 0.6)
            return new(Name11, false,
                $"300 ms of silence took the held balance from {heldNow:F3} to " +
                $"{afterSilence.PeakHoldBalance.Value:F3}, well past the 0.95 a 1.6 s constant implies; " +
                "the app is not using the hold it reports");

        // A multichannel frame has no imbalance at all, and the window has to say so
        // rather than showing a stale stereo number as if it were still current.
        builder.Reset();
        var surround = new DirectionSpectrum();   // Balance stays null
        var mc = builder.Build(surround, inputs with { Layout = "7.1 (8ch)" }, Step);
        if (!mc.IsMultichannel || mc.PeakHoldBalance is not null)
            return new(Name11, false,
                $"a 7.1 frame reported IsMultichannel={mc.IsMultichannel} with a held balance of " +
                $"{mc.PeakHoldBalance?.ToString("F3") ?? "none"}; a reset must clear the hold, or a stereo " +
                "reading from before a device change stays on the meter");

        return new(Name11, true,
            "a 6 dB sound reads 6 dB while it lasts and is still at least 60% of that 300 ms later, " +
            "through the same Build call the app uses, and a 7.1 frame reports no imbalance at all");
    }

    private const string Name11 = "the readout the app actually builds holds its peak, not just the class that does";

    /// <summary>
    /// Counts pixels that are not fully transparent, which is the only honest way to
    /// ask "did the geometry change" - line count is unaffected by length, so it passes
    /// for a slider that does nothing to the thing it names.
    /// </summary>
    private static int CountPainted(PixelBuffer buffer)
    {
        var pixels = buffer.Pixels;
        var n = 0;
        for (var i = 0; i < pixels.Length; i++)
            if (pixels[i] >> 24 != 0) n++;
        return n;
    }
}
