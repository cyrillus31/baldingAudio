namespace BaldingAudio.Core.Audio;

/// <summary>
/// Direction estimate for a two-channel (binaural or plain stereo) stream.
///
/// What is recoverable from two channels:
///  * <b>Left vs right</b> - reliable, from inter-aural level difference (ILD) and
///    inter-aural time difference (ITD).
///  * <b>Front vs back</b> - much weaker, but there is a real cue. For a source at a
///    given lateral position, the ITD <i>reverses sign</i> between the front and rear
///    halves of the head. ITD and ILD pointing the same way implies a source in front;
///    disagreeing implies one behind. This is the classical ILD-ITD cue.
///
/// The output is deliberately given a low <c>DistanceConfidence</c> so the overlay can
/// show the front/back position dimmer than the side position, and so nobody relies on
/// it for aiming decisions it cannot support.
/// </summary>
public sealed class StereoItd
{
    private const double HeadRadiusMetres = 0.0875;
    private const double SpeedOfSound = 343.0;

    /// <summary>Largest inter-aural delay for a source at +/-90 degrees, in seconds.</summary>
    private static readonly double MaxItd = 2.0 * HeadRadiusMetres / SpeedOfSound;

    private readonly int _maxLag;
    private readonly int _frameSize;
    private readonly double _sampleRate;
    private readonly double _frameSeconds;

    /// <summary>Rolling buffer holding the current and previous frame, per channel.</summary>
    private readonly float[] _left;
    private readonly float[] _right;
    private bool _haveHistory;

    public StereoItd(double sampleRate, int frameSize)
    {
        _sampleRate = sampleRate;
        _frameSize = frameSize;
        _frameSeconds = frameSize / sampleRate;
        _maxLag = Math.Max(1, (int)Math.Ceiling(MaxItd * sampleRate));
        var capacity = frameSize * 2;
        _left = new float[capacity];
        _right = new float[capacity];
    }

    public (Direction Direction, double Confidence, double DistanceConfidence) Result { get; private set; }
        = (Direction.Forward, 0, 0);

    public double LastItdMicroseconds { get; private set; }
    public double LastIldDb { get; private set; }
    public double LastCorrelationPeak { get; private set; }

    public void Reset()
    {
        Array.Clear(_left);
        Array.Clear(_right);
        _haveHistory = false;
        Result = (Direction.Forward, 0, 0);
        LastItdMicroseconds = 0;
        LastIldDb = 0;
        LastCorrelationPeak = 0;
    }

    /// <summary>
    /// Consumes one frame of interleaved stereo, retaining the previous frame as
    /// correlation context. Call once per analyser frame.
    /// </summary>
    public void Analyse(float[] interleaved, int frameSize, int stride)
    {
        if (frameSize != _frameSize) return;

        var leftIndex = 0;
        var rightIndex = Math.Min(1, stride - 1);

        // Shift the previous frame back, then append the new one. The correlation
        // window then spans two frames, which is what makes a sub-millisecond delay
        // measurable at all with a short analysis frame.
        Array.Copy(_left, 0, _left, _frameSize, _frameSize);
        Array.Copy(_right, 0, _right, _frameSize, _frameSize);
        for (var i = 0; i < _frameSize; i++)
        {
            _left[i] = interleaved[i * stride + leftIndex];
            _right[i] = interleaved[i * stride + rightIndex];
        }

        var offset = _frameSize;
        double sumL = 0, sumR = 0;
        for (var i = 0; i < _frameSize; i++)
        {
            sumL += (double)_left[offset + i] * _left[offset + i];
            sumR += (double)_right[offset + i] * _right[offset + i];
        }
        var rmsL = Math.Sqrt(sumL / _frameSize);
        var rmsR = Math.Sqrt(sumR / _frameSize);

        var ildDb = rmsR > 1e-9 && rmsL > 1e-9
            ? 20.0 * Math.Log10(rmsR / rmsL)
            : (rmsR > 1e-9 ? 20.0 : -20.0);

        if (!_haveHistory)
        {
            _haveHistory = true;
            LastIldDb = ildDb;
            Result = (new Direction(ildDb < 0 ? -30 : 30, 0), 0.25, 0.0);
            return;
        }

        var (lag, peak) = BestLag(offset, _frameSize);

        LastItdMicroseconds = lag / _sampleRate * 1e6;
        LastIldDb = ildDb;
        LastCorrelationPeak = peak;

        var lateral = AzimuthFromItd(lag);
        if (Math.Abs(lateral) < 5 && Math.Abs(ildDb) < 3)
        {
            Result = (Direction.Forward, 0.2, 0.0);
            return;
        }

        // ITD sign is negative for a right-side source, ILD positive for a right-side
        // source. Agreement therefore means the source is in front of the listener;
        // disagreement means it is behind.
        var itdSaysLeft = lag > 0;
        var ildSaysLeft = ildDb < 0;
        var inFront = itdSaysLeft == ildSaysLeft;
        var strength = Math.Min(Math.Abs(lateral) / 60.0, 1.0);
        var azimuth = inFront
            ? lateral
            : Math.Sign(lateral) * Math.Min(180.0, Math.Abs(lateral) + 55.0);

        var confidence = Math.Clamp(peak * 0.6 + strength * 0.5, 0, 1);
        var distanceConfidence = Math.Clamp(
            (Math.Abs(ildDb) - 2.0) / 8.0 * 0.5 + (Math.Abs(lateral) - 10.0) / 50.0 * 0.5,
            0, 0.5);

        Result = (new Direction(azimuth, 0), confidence, distanceConfidence);
    }

    /// <summary>Normalised cross-correlation over +/- the maximum plausible ITD.</summary>
    private (double Lag, double Peak) BestLag(int offset, int length)
    {
        var available = offset + length;
        var bestLag = 0;
        var bestScore = double.NegativeInfinity;

        var normL = 0.0;
        for (var i = 0; i < length; i++) normL += (double)_left[offset + i] * _left[offset + i];
        if (normL < 1e-12) return (0, 0);
        normL = Math.Sqrt(normL);

        for (var lag = -_maxLag; lag <= _maxLag; lag++)
        {
            var score = ScoreAt(lag, offset, length, normL, available);
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        if (bestScore == double.NegativeInfinity) return (0, 0);

        // Parabolic interpolation for sub-sample precision: one sample at 48 kHz is
        // 21 microseconds, far coarser than the cues being measured.
        var sub = 0.0;
        if (bestLag > -_maxLag && bestLag < _maxLag)
        {
            var y0 = ScoreAt(bestLag - 1, offset, length, normL, available);
            var y1 = bestScore;
            var y2 = ScoreAt(bestLag + 1, offset, length, normL, available);
            var denom = y0 - 2 * y1 + y2;
            if (Math.Abs(denom) > 1e-9) sub = Math.Clamp(0.5 * (y0 - y2) / denom, -1, 1);
        }

        return (bestLag + sub, bestScore);
    }

    private double ScoreAt(int lag, int offset, int length, double normL, int available)
    {
        var acc = 0.0;
        var normR = 0.0;
        for (var i = 0; i < length; i++)
        {
            var j = offset + i + lag;
            if (j < 0 || j >= available) continue;
            var b = _right[j];
            acc += (double)_left[offset + i] * b;
            normR += (double)b * b;
        }
        normR = Math.Sqrt(normR);
        return normR < 1e-12 ? double.NegativeInfinity : acc / (normL * normR);
    }

    /// <summary>
    /// Inverts the Woodworth spherical-head ITD model
    /// <c>ITD(t) = (a / c) * (t + sin t)</c> for the lateral angle t.
    /// A few bisection steps are cheaper than a table lookup and exact enough.
    /// </summary>
    private double AzimuthFromItd(double lagSamples)
    {
        var itd = lagSamples / _sampleRate;
        var aOverC = HeadRadiusMetres / SpeedOfSound;

        var lo = 0.0;
        var hi = Math.PI / 2;
        for (var i = 0; i < 20; i++)
        {
            var mid = 0.5 * (lo + hi);
            var model = aOverC * (mid + Math.Sin(mid));
            if (model < itd) lo = mid; else hi = mid;
        }
        var theta = 0.5 * (lo + hi) * 180.0 / Math.PI;
        return lagSamples >= 0 ? -theta : theta;
    }
}
