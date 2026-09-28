using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Dsp;

namespace BaldingAudio.Core.Overlay;

/// <summary>
/// One demonstration sound on the help page: what it is, and what it means.
/// </summary>
/// <param name="Label">Short description of the input, e.g. "9 dB to the right".</param>
/// <param name="Meaning">What that input does to the display, in one line.</param>
/// <param name="Event">The sound itself, fed to the real renderer.</param>
/// <remarks>
/// <para>
/// The descriptions are of the <b>input</b>, never of the result. Whether a cue
/// actually draws is worked out live against the threshold in force, because a help
/// page that says "this draws" in fixed text goes stale the moment a slider moves -
/// and a stale help page is worse than none, since it is the page people trust.
/// </para>
/// </remarks>
public sealed record PreviewCue(string Label, string Meaning, AudioEvent Event)
{
    /// <summary>Left/right imbalance in decibels, or null when the model does not use one.</summary>
    public double? BalanceDb => Event.Balance is double b ? Decibel.DbFromBalance(b) : null;
}

/// <summary>
/// The demonstration sounds on the help page, and the numbers behind them.
/// </summary>
/// <remarks>
/// <para>
/// These are <b>inputs</b>, deliberately not pictures. The help page renders them with
/// the same <see cref="OverlayRenderer"/>, the same <see cref="OverlayStyle"/> and the
/// same config object the live overlay uses, so a demonstration cannot disagree with
/// what the game does. A help page drawn by its own code would be a second
/// implementation of the display, and the two would diverge the first time a slider
/// moved.
/// </para>
///
/// <para>
/// The two stereo cues that measure 0 dB and 1 dB draw nothing at any sane threshold,
/// and that is the point of including them. A user who expects a line for every sound
/// needs to see that dead ahead and barely-off-centre are <i>supposed</i> to be blank -
/// it is the single most surprising property of this display model, and it is invisible
/// unless something on screen demonstrates it.
/// </para>
/// </remarks>
public static class PreviewScenes
{
    /// <summary>
    /// The stereo demonstration. Balance-driven: the edge and the length both come from
    /// the difference between the ears, and the height still comes from the bearing.
    /// </summary>
    public static IReadOnlyList<PreviewCue> Stereo() => new[]
    {
        new PreviewCue(
            "9 dB to the right, in front",
            "Long line from the right edge, high up. A big difference between the ears.",
            Event(bearing: 35, balanceDb: 9, level: 0.85, cls: SoundClass.Footstep)),

        new PreviewCue(
            "3 dB to the right, in front",
            "Same place on screen, much shorter. Length is the difference, not the volume.",
            Event(bearing: 35, balanceDb: 3, level: 0.85, cls: SoundClass.Footstep)),

        new PreviewCue(
            "dead ahead, both ears equal",
            "Nothing. Neither ear is louder, so there is no side to claim.",
            Event(bearing: 0, balanceDb: 0, level: 0.85, cls: SoundClass.Gunshot)),

        // Negative, because a negative balance is the right ear being quieter - the
        // right edge is positive, the left edge negative, and the sign is the whole
        // measurement. This was written as +6 once, which drew a right-hand line under a
        // caption saying left, and the self-test caught it.
        new PreviewCue(
            "6 dB to the left, behind",
            "Left edge, low down. Height is still front-versus-behind.",
            Event(bearing: -140, balanceDb: -6, level: 0.70, cls: SoundClass.Gunshot)),

        new PreviewCue(
            "1 dB to the right, in front",
            "Nothing at the 3 dB threshold. It appears as soon as the threshold reaches 1 dB. " +
            "Room noise measures this far, which is why the default is well above it.",
            Event(bearing: 20, balanceDb: 1, level: 0.50, cls: SoundClass.Other)),
    };

    /// <summary>
    /// The multichannel demonstration. Every speaker has a real bearing, so the sign of
    /// the bearing is a measurement and length carries loudness instead.
    /// </summary>
    public static IReadOnlyList<PreviewCue> Multichannel() => new[]
    {
        new PreviewCue(
            "loud, front right",
            "Right edge, high up, long. Length is volume here, not side.",
            Event(bearing: 35, balanceDb: null, level: 0.85, cls: SoundClass.Footstep)),

        new PreviewCue(
            "quiet, front right",
            "Right edge, same height, much shorter. Same direction, less of it.",
            Event(bearing: 35, balanceDb: null, level: 0.35, cls: SoundClass.Footstep)),

        new PreviewCue(
            "dead ahead",
            "Top of the band. A two-channel endpoint cannot show this one at all.",
            Event(bearing: 0, balanceDb: null, level: 0.85, cls: SoundClass.Gunshot)),

        new PreviewCue(
            "behind, slightly left",
            "Bottom of the band, from the left edge.",
            Event(bearing: -160, balanceDb: null, level: 0.70, cls: SoundClass.Gunshot)),
    };

    /// <summary>
    /// Builds one demonstration event.
    /// </summary>
    /// <param name="bearing">Degrees, 0 straight ahead, negative left.</param>
    /// <param name="balanceDb">
    /// Left/right difference in decibels, or null for a multichannel demonstration
    /// where the speaker's own position supplies the side. Null is the discriminator
    /// between the two display models, so it is a parameter rather than two factories.
    /// </param>
    /// <param name="level">Perceptual magnitude 0..1.</param>
    /// <param name="cls">Sound class, so the colour swatches on the help page are the
    /// real ones rather than a guess.</param>
    private static AudioEvent Event(double bearing, double? balanceDb, double level, SoundClass cls)
        => new AudioEvent(
            new Direction(bearing, 0),
            Level: level,
            // Inverse of the unit mapping, so the printed dBFS agrees with the level
            // rather than being a separate number that happens to sit nearby.
            Dbfs: Decibel.FromUnitLevel(level),
            Class: cls,
            ClassConfidence: 0.8,
            // Full confidence: these are demonstrations, and a low value would dim them
            // and make the page disagree with the numbers it prints beside them.
            Confidence: 1.0,
            // 1 on the front/back axis, which is only true in multichannel. In stereo the
            // help page says so, and showing a confident height for a stereo endpoint
            // would be claiming the app can measure something it cannot.
            DistanceConfidence: balanceDb is null ? 1.0 : 0.45,
            Timestamp: 0)
        {
            Balance = balanceDb is double db ? Decibel.BalanceFromDb(db) : null,
        };
}
