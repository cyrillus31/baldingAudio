using BaldingAudio.Core.Audio;

namespace BaldingAudio.App;

/// <summary>
/// Everything the readout needs that is not the sound field itself.
/// </summary>
/// <param name="HasAudio">False when capture never started.</param>
/// <param name="CaptureState">Short description of the capture thread.</param>
/// <param name="Layout">The endpoint's channel layout.</param>
/// <param name="FloorDb">The side threshold currently set, in decibels.</param>
/// <param name="LoudestDbfs">Most recent event level, dBFS.</param>
/// <param name="NoiseFloorDb">The analyser's adaptive floor, dBFS.</param>
/// <param name="LinesDrawn">Lines painted on the last frame.</param>
/// <param name="TracksTracked">Events the tracker holds.</param>
/// <param name="EventsSeen">Lifetime event count.</param>
/// <param name="Paused">Whether the display is held.</param>
public readonly record struct ReadoutInputs(
    bool HasAudio,
    string CaptureState,
    string Layout,
    double FloorDb,
    double LoudestDbfs,
    double NoiseFloorDb,
    int LinesDrawn,
    int TracksTracked,
    long EventsSeen,
    bool Paused);

/// <summary>
/// Assembles a <see cref="LiveReadout"/> from a frame's sound field.
/// </summary>
/// <para>
/// Owned by <see cref="AppHost"/> rather than inlined in it, for the same reason
/// <see cref="PeakHold"/> is its own class: this is the seam between the analyser and
/// the one number the user tunes against, and inlined it could only be checked by
/// standing up a WinForms timer and an audio endpoint. As a class it is checked by
/// feeding it a <see cref="DirectionSpectrum"/> and reading the result.
/// </para>
///
/// <para>
/// That gap was not hypothetical. The first version of this had the peak-hold logic
/// inline in the render tick, and a fault injected into the wiring - passing the raw
/// spectrum balance straight through instead of the held one - left every check green,
/// because the checks exercised <see cref="PeakHold"/> on its own and never the code
/// that decides to call it. The meter would have shown an instantaneous flicker in
/// production while the self-test said it held.
/// </para>
///
/// <para>
/// Stateless across frames except for the peak it holds, so it lives for the life of
/// the host rather than being rebuilt per frame.
/// </para>
/// </summary>
public sealed class ReadoutBuilder
{
    private readonly PeakHold _hold;

    public ReadoutBuilder(double fallSeconds = 1.6) => _hold = new PeakHold(fallSeconds);

    /// <summary>The meter's fall time constant, quoted in the readout so the window
    /// reports the figure that governs it rather than a separate guess.</summary>
    public double BalanceHoldSeconds => _hold.FallSeconds;

    /// <summary>
    /// Builds one frame's readout.
    /// </summary>
    /// <param name="spectrum">
    /// This frame's sound field, or null when there is none - no capture, or the
    /// capture thread has not published yet.
    /// </param>
    /// <param name="inputs">The non-audio numbers, gathered by the caller.</param>
    /// <param name="dt">Seconds since the previous frame.</param>
    public LiveReadout Build(DirectionSpectrum? spectrum, ReadoutInputs inputs, double dt)
    {
        // The held peak. This is the line the wiring check protects: handing
        // spectrum?.Balance straight to the readout would show an instantaneous value
        // that is almost always zero between sounds, and the window would be useless.
        _hold.Push(spectrum?.Balance, dt);
        var hold = _hold.Value;

        var (_, level) = spectrum?.Peak() ?? (0.0, 0.0);

        return new LiveReadout(
            HasAudio: inputs.HasAudio,
            CaptureState: inputs.CaptureState,
            Layout: inputs.Layout,
            // Multichannel is exactly the case where there is no imbalance to report.
            // Keyed off the field rather than off the channel count, because that is what
            // the layout will actually branch on.
            IsMultichannel: spectrum?.Balance is null,
            Balance: spectrum?.Balance,
            PeakHoldBalance: hold == 0.0 ? null : hold,
            BalanceHoldSeconds: _hold.FallSeconds,
            LoudestDbfs: inputs.LoudestDbfs,
            NoiseFloorDb: inputs.NoiseFloorDb,
            LevelUnit: spectrum is null ? null : level,
            FloorDb: inputs.FloorDb,
            LinesDrawn: inputs.LinesDrawn,
            TracksTracked: inputs.TracksTracked,
            EventsSeen: inputs.EventsSeen,
            Paused: inputs.Paused);
    }

    /// <summary>Drops the held peak, for when capture restarts.</summary>
    public void Reset() => _hold.Clear();
}
