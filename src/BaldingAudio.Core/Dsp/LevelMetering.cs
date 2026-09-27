namespace BaldingAudio.Core.Dsp;

/// <summary>
/// Converts a linear mean-square value to dBFS, and maps a dB range onto a
/// perceptual 0..1 bar length.
///
/// The mapping is deliberately done in the dB domain: a footstep at 30 m in
/// Battlefield is roughly 40 dB quieter than one at 5 m, and on a linear scale it
/// would simply be invisible. Mapping dB onto the bar makes distant-but-real cues
/// visible, which is the entire point of the tool.
/// </summary>
public static class Decibel
{
    public const double Silence = -120.0;

    public static double FromAmplitude(double rms) => rms <= 1e-12 ? Silence : 20.0 * Math.Log10(rms);

    public static double FromMeanSquare(double meanSquare) => meanSquare <= 1e-24 ? Silence : 10.0 * Math.Log10(meanSquare);

    /// <summary>
    /// Maps <paramref name="dbfs"/> into 0..1 given a visibility window.
    /// </summary>
    public static double ToUnit(double dbfs, double floorDb, double ceilingDb)
    {
        if (dbfs <= floorDb) return 0.0;
        if (ceilingDb <= floorDb) return dbfs >= ceilingDb ? 1.0 : 0.0;
        var t = (dbfs - floorDb) / (ceilingDb - floorDb);
        return t * t * (3.0 - 2.0 * t); // smoothstep: avoids a hard edge at the ends
    }

    public static double ToUnit(double dbfs, double floorDb, double ceilingDb, double exponent)
    {
        var t = ToUnit(dbfs, floorDb, ceilingDb);
        return Math.Pow(t, exponent);
    }

    /// <summary>Inverse of the default visibility window, for synthesising test cues.</summary>
    public static double FromUnitLevel(double level) => -72.0 + Math.Clamp(level, 0.0, 1.0) * 56.0;
}

/// <summary>
/// Tracks the recent noise floor of a signal using minimum statistics, so that the
/// onset detector can use a relative threshold that adapts to whatever is playing.
///
/// Without this, a loud firefight raises the floor and quiet footsteps after it
/// vanish; conversely a silent map would trip on almost nothing.
/// </summary>
public sealed class AdaptiveNoiseFloor
{
    private readonly int _window;
    private readonly float[] _history;
    private int _count;
    private int _index;
    private float _floor;
    private bool _primed;

    public AdaptiveNoiseFloor(int window = 96)
    {
        _window = Math.Max(4, window);
        _history = new float[_window];
    }

    public float Floor => _floor;

    public void Reset()
    {
        Array.Clear(_history);
        _count = 0; _index = 0; _floor = 0f; _primed = false;
    }

    /// <summary>Feeds one instantaneous energy value and returns the current floor.</summary>
    public float Update(float energy)
    {
        _history[_index] = energy;
        _index = (_index + 1) % _window;
        if (_count < _window) _count++;

        if (!_primed)
        {
            if (_count < _window) return _floor;
            // Seed from the window we have buffered, using a low percentile so a
            // single early transient does not set the floor too high.
            var sorted = (float[])_history.Clone();
            Array.Sort(sorted);
            _floor = Math.Max(sorted[(int)(_window * 0.10)], 1e-9f);
            _primed = true;
            return _floor;
        }

        // A slow attack / fast release: the floor can rise when a genuinely quiet
        // passage continues, but must fall quickly after a loud passage ends.
        var min = float.MaxValue;
        for (var i = 0; i < _window; i++)
            if (_history[i] < min) min = _history[i];

        if (min < _floor) _floor = min;                        // drop fast
        else _floor += (min - _floor) * 0.02f;                 // rise very slowly

        _floor = Math.Max(_floor, 1e-9f);
        return _floor;
    }
}
