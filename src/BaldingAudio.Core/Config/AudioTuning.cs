namespace BaldingAudio.Core.Config;

/// <summary>
/// Thresholds and band edges. Kept in one place because every one of these is an
/// empirical guess that will need tuning against real Battlefield 6 audio, and
/// because a user with partial hearing will want to move them.
/// </summary>
public sealed class AudioTuning
{
    /// <summary>Hz. Lowest band: engines, explosions, body of gunfire.</summary>
    public double LowBandHz { get; set; } = 90;

    /// <summary>Hz. Split between the low and mid bands.</summary>
    public double LowMidSplitHz { get; set; } = 320;

    /// <summary>Hz. Split between the mid and high bands. This is the footstep body band.</summary>
    public double MidHighSplitHz { get; set; } = 1500;

    /// <summary>Hz. Upper edge of the high band. Gunfire crack and surface detail.</summary>
    public double HighBandCeilingHz { get; set; } = 7000;

    /// <summary>
    /// dBFS. Below this, nothing is shown. Low enough to catch distant movement.
    ///
    /// <para>
    /// Left at -72 deliberately, and worth not "fixing" on the strength of the log's
    /// <c>loudest silent</c> lines. Those were measured against a median peak of
    /// -28 dBFS - forty-four dB above this floor - while events were still being
    /// emitted. The spectrum was not below the floor; it was arriving empty. That was
    /// <see cref="App.SpectrumExchange"/> publishing a stale zero-filled buffer on
    /// alternating frames, and it is fixed there. Loosening this would only have made
    /// noise visible, which is the opposite of what was wanted.
    /// </para>
    /// </summary>
    public double DisplayFloorDb { get; set; } = -72;

    /// <summary>dBFS. At or above this a bar is drawn at full length.</summary>
    public double DisplayCeilingDb { get; set; } = -16;

    /// <summary>Relative loudness curve applied to bar length. &gt;1 makes loud cues dominate.</summary>
    public double LevelExponent { get; set; } = 1.35;

    // --- onset detection -----------------------------------------------------

    /// <summary>dBFS hard floor for any detection, regardless of the adaptive floor.</summary>
    public double OnsetAbsoluteFloorDb { get; set; } = -76;

    /// <summary>dB above the adaptive noise floor required to trigger.</summary>
    public double OnsetRelativeThresholdDb { get; set; } = 7.0;

    /// <summary>dB rise over the preceding frames required to trigger.</summary>
    public double OnsetRiseThresholdDb { get; set; } = 2.6;

    /// <summary>dBFS above which a frame is treated as a large transient and accepted freely.</summary>
    public double OnsetAbsoluteCeilingDb { get; set; } = -22;

    /// <summary>Minimum gap between two onsets, prevents one footstep becoming four events.</summary>
    public double OnsetRefractorySeconds { get; set; } = 0.045;

    /// <summary>
    /// dB. A rise smaller than this between the quietest and loudest channel is
    /// treated as a diffuse mix and contributes less to the direction estimate.
    /// </summary>
    public double DirectionalContrastDb { get; set; } = 2.0;

    /// <summary>How long a bar stays on screen after its last update, in seconds.</summary>
    public double EventHoldSeconds { get; set; } = 0.9;

    /// <summary>Seconds for a bar to fade out once its hold expires.</summary>
    public double EventFadeSeconds { get; set; } = 0.35;

    /// <summary>
    /// Suppress near-centre, near-maximum transients, which in a shooter are usually
    /// the player's own gunfire and footsteps. Off by default because it also hides
    /// genuine dead-ahead enemies, which matters a lot in a cone-of-fire map.
    /// </summary>
    public bool SuppressOwnSounds { get; set; }

    /// <summary>Degrees from dead ahead within which self-sound suppression applies.</summary>
    public double SelfSoundConeDegrees { get; set; } = 25;

    /// <summary>dBFS at or above which a centre transient counts as the player's own.</summary>
    public double SelfSoundLevelDb { get; set; } = -12;

    public static AudioTuning Default() => new();
}
