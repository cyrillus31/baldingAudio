namespace BaldingAudio.Core.Audio;

/// <summary>
/// A continuous picture of where sound energy is right now, sampled at a fixed set of
/// bearings around the player.
///
/// Onsets tell you that something happened. They do not tell you where it is a moment
/// later, or how loud it is now, which is what a moving sound needs. This is the
/// ongoing measurement the overlay follows, so that a sound sliding from the left to
/// the right of the player produces a line that slides with it.
///
/// The spectrum is built from the same per-channel energy the direction estimate uses,
/// interpolated across the gap between speakers, so it cannot disagree with the
/// per-event direction it sits beside.
/// </summary>
public sealed class DirectionSpectrum
{
    private readonly double[] _levels;
    private readonly double _minAzimuth;
    private readonly double _spanAzimuth;

    public DirectionSpectrum(int binCount = 24)
    {
        Bins = Math.Max(4, binCount);
        _levels = new double[Bins];
        _minAzimuth = -180.0;
        _spanAzimuth = 360.0;
    }

    /// <summary>Number of bearings sampled around the full circle.</summary>
    public int Bins { get; }

    /// <summary>
    /// True when the bearing(s) in this spectrum have no measurable side.
    ///
    /// <para>
    /// One flag rather than one per bin, and that is not a simplification: on a
    /// two-channel endpoint the whole spectrum is a single bearing measured by
    /// interaural timing, so there is only ever one thing for it to describe. In
    /// multichannel mode each bin is a real speaker at a real bearing, so it stays
    /// false and the overlay never mirrors anything.
    /// </para>
    /// </summary>
    public bool Ambiguous { get; set; }

    /// <summary>Level of each bin, 0..1. Not smoothed; the tracker does that.</summary>
    public double this[int bin] => _levels[bin];

    /// <summary>Bearing of a bin, in degrees. Bin 0 is directly behind.</summary>
    public double AzimuthOf(int bin) => _minAzimuth + (bin + 0.5) * (_spanAzimuth / Bins);

    /// <summary>Clears the whole spectrum. Called at the start of every frame.</summary>
    public void Clear()
    {
        Array.Clear(_levels);
        Ambiguous = false;
    }

    /// <summary>Overwrites one bin. Used to publish a snapshot taken on another thread.</summary>
    public void SetBin(int bin, double level) => _levels[Wrap(bin)] = level;

    /// <summary>Adds the energy found at one bearing, spread between the two
    /// nearest bins so a source between two speakers does not alias to one side.</summary>
    public void Add(double azimuthDegrees, double level)
    {
        if (level <= 0) return;

        var t = (azimuthDegrees - _minAzimuth) / _spanAzimuth * Bins - 0.5;
        var lower = (int)Math.Floor(t);
        var frac = t - lower;
        var a = Wrap(lower);
        var b = Wrap(lower + 1);

        _levels[a] += level * (1.0 - frac);
        _levels[b] += level * frac;
    }

    private int Wrap(int bin)
    {
        var m = bin % Bins;
        return m < 0 ? m + Bins : m;
    }

    /// <summary>Level at an arbitrary bearing, linearly interpolated between bins.</summary>
    public double LevelAt(double azimuthDegrees)
    {
        var t = (Wrap180(azimuthDegrees) - _minAzimuth) / _spanAzimuth * Bins - 0.5;
        var lower = (int)Math.Floor(t);
        var frac = t - lower;
        return _levels[Wrap(lower)] * (1.0 - frac) + _levels[Wrap(lower + 1)] * frac;
    }

    /// <summary>
    /// Loudest bearing anywhere in the spectrum, and its level. Returns NaN and 0 when
    /// the spectrum is empty, so a caller cannot mistake "nothing is sounding" for a
    /// real reading at whatever bin happens to be loudest of nothing.
    /// </summary>
    public (double Azimuth, double Level) Peak()
    {
        var best = 0.0;
        var bestAz = double.NaN;

        for (var bin = 0; bin < Bins; bin++)
            if (_levels[bin] > best) { best = _levels[bin]; bestAz = AzimuthOf(bin); }

        return (bestAz, best);
    }

    /// <summary>
    /// Strongest bin within <paramref name="windowDegrees"/> of a bearing, and its
    /// level. This is how a track follows a sound that is moving: it looks for the
    /// loudest nearby bearing each frame instead of staying where it started.
    /// </summary>
    public (double Azimuth, double Level) PeakNear(double azimuthDegrees, double windowDegrees)
    {
        var best = 0.0;
        var bestAz = Wrap180(azimuthDegrees);

        foreach (var bin in Enumerable.Range(0, Bins))
        {
            var az = AzimuthOf(bin);
            if (Math.Abs(Wrap180(az - Wrap180(azimuthDegrees))) > windowDegrees) continue;
            if (_levels[bin] > best)
            {
                best = _levels[bin];
                bestAz = az;
            }
        }

        return (bestAz, best);
    }

    /// <summary>Normalises the spectrum so its loudest bin is <paramref name="target"/>.</summary>
    public void NormaliseTo(double target)
    {
        var peak = 0.0;
        foreach (var l in _levels) if (l > peak) peak = l;
        if (peak <= 1e-9) return;

        var scale = target / peak;
        for (var i = 0; i < Bins; i++) _levels[i] = Math.Min(_levels[i] * scale, 1.0);
    }

    /// <summary>Folds an angle into -180..180 so left and right wrap cleanly.</summary>
    public static double Wrap180(double degrees)
    {
        var d = (degrees + 180.0) % 360.0;
        if (d < 0) d += 360.0;
        return d - 180.0;
    }
}
