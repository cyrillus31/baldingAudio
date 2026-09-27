namespace BaldingAudio.Core.Dsp;

/// <summary>
/// A direct-form-II transposed biquad section, RBJ cookbook coefficients.
/// One instance holds the filter's delay line; coefficients are set by
/// <see cref="Configure"/>.
/// </summary>
public sealed class Biquad
{
    private double _b0, _b1, _b2, _a1, _a2;
    private double _x1, _x2;

    public void Reset()
    {
        _x1 = _x2 = 0;
    }

    public void Configure(BiquadKind kind, double sampleRate, double frequency, double q, double gainDb = 0)
    {
        frequency = Math.Clamp(frequency, 10.0, sampleRate * 0.49);
        q = Math.Max(q, 0.05);

        var w0 = 2.0 * Math.PI * frequency / sampleRate;
        var cosw = Math.Cos(w0);
        var sinw = Math.Sin(w0);
        var alpha = sinw / (2.0 * q);
        var gain = Math.Pow(10.0, gainDb / 20.0);

        double b0, b1, b2, a0, a1, a2;
        switch (kind)
        {
            case BiquadKind.LowPass:
                b0 = (1 - cosw) / 2; b1 = 1 - cosw; b2 = (1 - cosw) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cosw; a2 = 1 - alpha;
                break;
            case BiquadKind.HighPass:
                b0 = (1 + cosw) / 2; b1 = -(1 + cosw); b2 = (1 + cosw) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cosw; a2 = 1 - alpha;
                break;
            case BiquadKind.BandPassConstantPeakGain:
                b0 = alpha; b1 = 0; b2 = -alpha;
                a0 = 1 + alpha;
                a1 = -2 * cosw; a2 = 1 - alpha;
                break;
            case BiquadKind.BandPassZeroDelay:
                b0 = sinw / 2; b1 = 0; b2 = -sinw / 2;
                a0 = 1 + alpha;
                a1 = -2 * cosw; a2 = 1 - alpha;
                break;
            case BiquadKind.Peak:
            {
                var A = Math.Pow(10.0, gainDb / 40.0);
                b0 = 1 + alpha * A; b1 = -2 * cosw; b2 = 1 - alpha * A;
                a0 = 1 + alpha / A;
                a1 = -2 * cosw; a2 = 1 - alpha / A;
                break;
            }
            case BiquadKind.HighShelf:
            {
                var A = Math.Pow(10.0, gainDb / 40.0);
                var beta = 2.0 * Math.Sqrt(A) * alpha;
                b0 = A * ((A + 1) + (A - 1) * cosw + beta);
                b1 = -2 * A * ((A - 1) + (A + 1) * cosw);
                b2 = A * ((A + 1) + (A - 1) * cosw - beta);
                a0 = (A + 1) - (A - 1) * cosw + beta;
                a1 = 2 * ((A - 1) - (A + 1) * cosw);
                a2 = (A + 1) - (A - 1) * cosw - beta;
                break;
            }
            case BiquadKind.LowShelf:
            {
                var A = Math.Pow(10.0, gainDb / 40.0);
                var beta = 2.0 * Math.Sqrt(A) * alpha;
                b0 = A * ((A + 1) - (A - 1) * cosw + beta);
                b1 = 2 * A * ((A - 1) - (A + 1) * cosw);
                b2 = A * ((A + 1) - (A - 1) * cosw - beta);
                a0 = (A + 1) + (A - 1) * cosw + beta;
                a1 = -2 * ((A - 1) + (A + 1) * cosw);
                a2 = (A + 1) + (A - 1) * cosw - beta;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        // Normalising by a0 is what keeps the filter stable. Skipping it leaves the
        // denominator roots outside the unit circle for low normalised frequencies
        // and the filter self-oscillates up to infinity within a few thousand samples.
        _b0 = b0 * gain / a0;
        _b1 = b1 * gain / a0;
        _b2 = b2 * gain / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }

    public double Process(double x)
    {
        var y = _b0 * x + _x1;
        _x1 = _b1 * x - _a1 * y + _x2;
        _x2 = _b2 * x - _a2 * y;
        return y;
    }

    public float Process(float x) => (float)Process((double)x);
}

public enum BiquadKind
{
    LowPass,
    HighPass,
    BandPassConstantPeakGain,
    BandPassZeroDelay,
    Peak,
    LowShelf,
    HighShelf,
}

/// <summary>
/// A band-pass filter built from two cascaded 2nd-order sections, giving a 4th-order
/// response. A single 2nd-order band-pass is too narrow and too peaky to characterise
/// a footstep, so every band in the analyser is a cascade.
/// </summary>
public sealed class BandPass
{
    private readonly Biquad _s1 = new();
    private readonly Biquad _s2 = new();

    public BandPass(double sampleRate, double lowHz, double highHz)
    {
        // Split the requested range at its geometric mean, then place each section at
        // the geometric centre of its own half with Q matched to that half's width.
        // Two sections roughly 2x apart keep the combined -3 dB points near the
        // requested edges without the deep dip a single narrow section would leave.
        var mid = Math.Sqrt(lowHz * highHz);

        var c1 = Math.Sqrt(lowHz * mid);
        var q1 = ClampQ(c1 / Math.Max(1.0, mid - lowHz));

        var c2 = Math.Sqrt(mid * highHz);
        var q2 = ClampQ(c2 / Math.Max(1.0, highHz - mid));

        _s1.Configure(BiquadKind.BandPassZeroDelay, sampleRate, c1, q1);
        _s2.Configure(BiquadKind.BandPassZeroDelay, sampleRate, c2, q2);
    }

    /// <summary>
    /// Bounds Q. The high side matters: a very high Q pushes the poles near the unit
    /// circle, and the lowest band starts close enough to DC to be sensitive to it.
    /// </summary>
    private static double ClampQ(double q) => Math.Clamp(q, 0.4, 4.0);

    public void Reset() { _s1.Reset(); _s2.Reset(); }

    public float Process(float x) => _s2.Process(_s1.Process(x));
}
