using BaldingAudio.Core.Dsp;

namespace BaldingAudio.App;

/// <summary>
/// One frame's worth of numbers for the settings window, captured on the UI thread.
/// </summary>
/// <para>
/// Built in <see cref="AppHost.Tick"/> and read by the settings form, which is also on
/// the UI thread, so there is no lock and no torn read. A snapshot type rather than a
/// pile of live properties because the form wants a consistent set: reading
/// <c>LoudestDbfs</c> and <c>BalanceDb</c> one at a time can pair a level from one
/// frame with a balance from another, which is how a readout ends up disagreeing with
/// the screen next to it.
/// </para>
/// </summary>
/// <param name="HasAudio">False when capture never started, so the panel can say so
/// rather than showing a flat line and looking like a dead threshold.</param>
/// <param name="CaptureState">Short description of the capture thread, for the same
/// reason the heartbeat log carries one.</param>
/// <param name="Layout">The endpoint's channel layout, because the balance column is
/// meaningless in multichannel and the panel has to say which model is in use.</param>
/// <param name="IsMultichannel">True when balance is null and length means loudness.</param>
/// <param name="Balance">
/// The left/right imbalance, -1 all left through +1 all right. Null in multichannel.
/// </para>
/// <param name="PeakHoldBalance">
/// The largest imbalance seen in the last <see cref="BalanceHoldSeconds"/>, held and
/// then decayed.
/// <para>
/// A peak hold rather than the instantaneous value, because the whole job of this
/// number is comparison against a threshold, and an instantaneous value sampled at the
/// window's refresh rate flickers past the comparison before it can be read. A sound
/// that is 4 dB off to one side for 200 ms has to be visible on the panel long enough
/// to set a slider against.
/// </para>
/// </param>
/// <param name="BalanceHoldSeconds">How long a peak is held before it starts to fall.</param>
/// <param name="LoudestDbfs">Loudest level in the current frame, dBFS.</param>
/// <param name="NoiseFloorDb">The analyser's adaptive noise floor, dBFS.</param>
/// <param name="LevelUnit">Loudness mapped to 0..1 for display, or null with no audio.</param>
/// <param name="FloorDb">The current side threshold in decibels.</param>
/// <param name="LinesDrawn">Lines actually painted on the last frame.</param>
/// <param name="TracksTracked">Events the tracker is holding, drawn or not.</param>
/// <param name="EventsSeen">Lifetime count, for confirming capture is alive at all.</param>
/// <param name="Paused">Whether the display is held.</param>
public readonly record struct LiveReadout(
    bool HasAudio,
    string CaptureState,
    string Layout,
    bool IsMultichannel,
    double? Balance,
    double? PeakHoldBalance,
    double BalanceHoldSeconds,
    double LoudestDbfs,
    double NoiseFloorDb,
    double? LevelUnit,
    double FloorDb,
    int LinesDrawn,
    int TracksTracked,
    long EventsSeen,
    bool Paused)
{
    /// <summary>The empty reading, for before capture has started.</summary>
    public static readonly LiveReadout None = new(
        HasAudio: false,
        CaptureState: "not started",
        Layout: "none",
        IsMultichannel: false,
        Balance: null,
        PeakHoldBalance: null,
        BalanceHoldSeconds: 0,
        LoudestDbfs: -120,
        NoiseFloorDb: -120,
        LevelUnit: null,
        FloorDb: 0,
        LinesDrawn: 0,
        TracksTracked: 0,
        EventsSeen: 0,
        Paused: false);

    /// <summary>
    /// The peak-held balance in decibels, or null when there is nothing to read.
    /// </summary>
    public double? PeakHoldDb => PeakHoldBalance is double b
        ? Decibel.DbFromBalance(b)
        : null;

    /// <summary>
    /// Whether a sound with the held imbalance would be drawn at all.
    /// </summary>
    /// <para>
    /// This is the number the settings window exists to produce. The threshold decides
    /// whether a line appears, the balance is what a real sound measures, and "would
    /// this show?" is the comparison between them - which a slider on its own cannot
    /// answer, because a slider shows where the threshold is set and not where the
    /// sound is.
    /// </para>
    /// </summary>
    public bool PeakWouldDraw => PeakHoldBalance is double b && Math.Abs(b) >= BalanceFromDb(FloorDb);

    /// <summary>
    /// Raw balance for a decibel threshold. Named separately so the unit conversion is
    /// in one place and cannot drift from the one in <c>OverlayStyle</c>.
    /// </summary>
    public static double BalanceFromDb(double db) => Decibel.BalanceFromDb(db);

    public string Side =>
        PeakHoldBalance is not double b ? "-"
        : Math.Abs(b) < BalanceFromDb(FloorDb) ? "centred"
        : b < 0 ? "left" : "right";
}
