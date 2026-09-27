namespace BaldingAudio.Core.Audio;

/// <summary>
/// Speaker position identifiers taken from the Windows <c>SPEAKER_*</c> constants
/// (ksmedia.h). These are the bits of <c>WAVEFORMATEXTENSIBLE.dwChannelMask</c> and
/// are the <b>only</b> authoritative statement of which physical channel is which.
/// Channel index order must never be assumed: different renderers order channels
/// differently, the mask is what is contractual.
/// </summary>
public static class SpeakerPosition
{
    public const uint FrontLeft = 0x0000_0001;
    public const uint FrontRight = 0x0000_0002;
    public const uint FrontCenter = 0x0000_0004;
    public const uint LowFrequency = 0x0000_0008;
    public const uint BackLeft = 0x0000_0010;
    public const uint BackRight = 0x0000_0020;
    public const uint FrontLeftOfCenter = 0x0000_0040;
    public const uint FrontRightOfCenter = 0x0000_0080;
    public const uint BackCenter = 0x0000_0100;
    public const uint SideLeft = 0x0000_0200;
    public const uint SideRight = 0x0000_0400;
    public const uint TopCenter = 0x0000_0800;
    public const uint TopFrontLeft = 0x0000_1000;
    public const uint TopFrontCenter = 0x0000_2000;
    public const uint TopFrontRight = 0x0000_4000;
    public const uint TopBackLeft = 0x0000_8000;
    public const uint TopBackCenter = 0x0001_0000;
    public const uint TopBackRight = 0x0002_0000;

    public const uint Stereo = FrontLeft | FrontRight;
    public const uint Surround51 = FrontLeft | FrontRight | FrontCenter | LowFrequency | BackLeft | BackRight;
    public const uint Surround71 = Surround51 | FrontLeftOfCenter | FrontRightOfCenter;
    public const uint Surround71Alt = Stereo | FrontCenter | LowFrequency | BackLeft | BackRight;
    public const uint Surround714 = Surround71 | TopFrontLeft | TopFrontCenter | TopFrontRight | TopBackLeft | TopBackCenter | TopBackRight;
}

/// <summary>
/// A direction in the listener's horizontal plane, plus optional elevation.
/// </summary>
/// <param name="AzimuthDegrees">
/// Signed degrees around the listener. 0 = dead ahead, +90 = right, +/-180 = directly
/// behind, -90 = left.
/// </param>
/// <param name="ElevationDegrees">
/// Degrees above the horizon. 0 = level with the ears, +90 = overhead,
/// -90 = below (e.g. the player's own feet).
/// </param>
public readonly record struct Direction(double AzimuthDegrees, double ElevationDegrees)
{
    public static readonly Direction Forward = new(0, 0);
    public static readonly Direction Backward = new(180, 0);
    public static readonly Direction Left = new(-90, 0);
    public static readonly Direction Right = new(90, 0);

    /// <summary>0 = dead ahead, 1 = directly behind. Used for the vertical overlay axis.</summary>
    public double Frontness => Math.Abs(AzimuthDegrees) / 180.0;

    public bool IsLeft => AzimuthDegrees < 0;
    public bool IsRight => AzimuthDegrees > 0;

    public Direction Normalized()
    {
        // Fold into (-180, 180].
        var az = AzimuthDegrees % 360.0;
        if (az > 180.0) az -= 360.0;
        if (az <= -180.0) az += 360.0;
        return new Direction(az, Math.Clamp(ElevationDegrees, -90.0, 90.0));
    }

    /// <summary>Unit vector on the horizontal plane, used for energy-weighted direction voting.</summary>
    public (double X, double Z) HorizontalUnitVector()
    {
        var r = AzimuthDegrees * Math.PI / 180.0;
        // Z points "in front of the listener", X points to the listener's right.
        return (Math.Sin(r), Math.Cos(r));
    }

    public override string ToString()
    {
        var h = Math.Abs(AzimuthDegrees) < 22.5 ? "front"
            : Math.Abs(AzimuthDegrees) < 67.5 ? (AzimuthDegrees < 0 ? "front-left" : "front-right")
            : Math.Abs(AzimuthDegrees) < 112.5 ? (AzimuthDegrees < 0 ? "left" : "right")
            : Math.Abs(AzimuthDegrees) < 157.5 ? (AzimuthDegrees < 0 ? "rear-left" : "rear-right")
            : "behind";
        return ElevationDegrees > 20 ? $"{h} (above)" : ElevationDegrees < -20 ? $"{h} (below)" : h;
    }
}

/// <summary>
/// Maps a <c>dwChannelMask</c> to per-channel <see cref="Direction"/> values.
/// </summary>
public static class ChannelLayout
{
    /// <summary>
    /// Canonical ITU/speaker positions for a listener facing forward.
    /// Azimuths follow the usual convention for a head-locked 7.1 setup where
    /// FRONT_LEFT sits at -30 degrees of the listener's forward axis, which is what
    /// Windows and the common virtual-surround renderers use.
    /// </summary>
    private static readonly Dictionary<uint, Direction> Map = new()
    {
        [SpeakerPosition.FrontCenter] = new Direction(0, 0),
        [SpeakerPosition.FrontLeftOfCenter] = new Direction(-30, 0),
        [SpeakerPosition.FrontRightOfCenter] = new Direction(30, 0),
        [SpeakerPosition.FrontLeft] = new Direction(-60, 0),
        [SpeakerPosition.FrontRight] = new Direction(60, 0),
        [SpeakerPosition.SideLeft] = new Direction(-110, 0),
        [SpeakerPosition.SideRight] = new Direction(110, 0),
        [SpeakerPosition.BackLeft] = new Direction(-150, 0),
        [SpeakerPosition.BackRight] = new Direction(150, 0),
        [SpeakerPosition.BackCenter] = new Direction(180, 0),

        [SpeakerPosition.TopFrontCenter] = new Direction(0, 60),
        [SpeakerPosition.TopFrontLeft] = new Direction(-45, 60),
        [SpeakerPosition.TopFrontRight] = new Direction(45, 60),
        [SpeakerPosition.TopCenter] = new Direction(0, 90),
        [SpeakerPosition.TopBackLeft] = new Direction(-135, 60),
        [SpeakerPosition.TopBackCenter] = new Direction(180, 60),
        [SpeakerPosition.TopBackRight] = new Direction(135, 60),

        // LFE carries no positional information, so it is excluded from direction
        // estimation. It still contributes to overall loudness.
        [SpeakerPosition.LowFrequency] = new Direction(0, 0),
    };

    /// <summary>
    /// The set of mask bits that carry usable direction. LFE is excluded.
    /// </summary>
    public const uint DirectionalMask =
        SpeakerPosition.FrontLeft | SpeakerPosition.FrontRight | SpeakerPosition.FrontCenter |
        SpeakerPosition.FrontLeftOfCenter | SpeakerPosition.FrontRightOfCenter |
        SpeakerPosition.SideLeft | SpeakerPosition.SideRight |
        SpeakerPosition.BackLeft | SpeakerPosition.BackRight | SpeakerPosition.BackCenter |
        SpeakerPosition.TopFrontLeft | SpeakerPosition.TopFrontCenter | SpeakerPosition.TopFrontRight |
        SpeakerPosition.TopCenter | SpeakerPosition.TopBackLeft | SpeakerPosition.TopBackCenter |
        SpeakerPosition.TopBackRight;

    /// <summary>
    /// Resolves a channel mask into a per-channel-index direction array.
    /// </summary>
    /// <param name="channelMask">The <c>dwChannelMask</c> from WAVEFORMATEXTENSIBLE.</param>
    /// <param name="channelCount">Number of interleaved channels in the stream.</param>
    /// <returns>
    /// One entry per channel index. <c>null</c> means the channel is not used for
    /// direction (either unmapped, LFE, or surplus channels beyond the mask).
    /// </returns>
    public static Direction?[] Resolve(uint channelMask, int channelCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCount, 1);

        var result = new Direction?[channelCount];
        if (channelMask == 0)
        {
            // No mask: fall back to the common implicit orders. Mono is dead ahead,
            // stereo is the standard L/R pair, 7.1 is the Windows canonical order.
            for (var i = 0; i < channelCount; i++) result[i] = ImplicitDirection(i, channelCount);
            return result;
        }

        for (var i = 0; i < channelCount; i++)
        {
            var bit = 1u << i;
            if ((channelMask & bit) == 0) continue;
            if (Map.TryGetValue(bit, out var dir)) result[i] = dir;
        }

        return result;
    }

    private static Direction? ImplicitDirection(int index, int count) => count switch
    {
        1 => Direction.Forward,
        2 => index == 0 ? new Direction(-30, 0) : new Direction(30, 0),
        8 => index switch
        {
            0 => new Direction(-30, 0),  // FL
            1 => new Direction(30, 0),   // FR
            2 => Direction.Forward,      // FC
            3 => Direction.Forward,      // LFE
            4 => new Direction(-150, 0), // BL
            5 => new Direction(150, 0),  // BR
            6 => new Direction(-60, 0),  // FLOC
            _ => new Direction(60, 0),   // FROC
        },
        _ => Direction.Forward,
    };

    /// <summary>
    /// True when the mask carries enough independent positions to distinguish
    /// front from back. This is the single most important capability flag in the
    /// whole app: without it, front/back is simply not recoverable from the audio.
    /// </summary>
    public static bool SupportsFrontBack(uint channelMask)
        => (channelMask & SpeakerPosition.BackLeft) != 0
        && (channelMask & SpeakerPosition.BackRight) != 0
        && (channelMask & SpeakerPosition.FrontLeft) != 0
        && (channelMask & SpeakerPosition.FrontRight) != 0;

    /// <summary>Human readable name for a channel mask, for the diagnostics panel.</summary>
    public static string Describe(uint channelMask) => channelMask switch
    {
        0 => "unspecified",
        SpeakerPosition.Stereo => "stereo (2ch)",
        SpeakerPosition.Surround51 => "5.1 surround",
        SpeakerPosition.Surround71 => "7.1 surround",
        SpeakerPosition.Surround714 => "7.1.4 surround",
        _ => $"{System.Numerics.BitOperations.PopCount(channelMask)}ch custom (0x{channelMask:X8})",
    };
}
