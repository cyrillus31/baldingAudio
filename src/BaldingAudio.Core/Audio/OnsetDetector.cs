using BaldingAudio.Core.Config;
using BaldingAudio.Core.Dsp;

namespace BaldingAudio.Core.Audio;

/// <summary>Broad category of a detected sound. Used only for visual styling.</summary>
public enum SoundClass
{
    Unknown,
    /// <summary>Sharp transient, energy concentrated in the low-mid band. The primary target.</summary>
    Footstep,
    /// <summary>Very fast, very loud, broadband transient.</summary>
    Gunshot,
    /// <summary>Slow low-frequency build-up, long decay.</summary>
    Explosion,
    /// <summary>Sustained, low-frequency dominant, no sharp onset.</summary>
    Vehicle,
    /// <summary>Sustained mid-band, no sharp onset - almost certainly speech.</summary>
    Voice,
    Other,
}

public static class SoundClassExtensions
{
    /// <summary>Classes that the user most likely cares about in a competitive shooter.</summary>
    public static bool IsPriority(this SoundClass c)
        => c is SoundClass.Footstep or SoundClass.Gunshot or SoundClass.Explosion;
}

/// <summary>
/// A single detected directional sound event.
/// </summary>
/// <param name="Direction">Where it came from.</param>
/// <param name="Level">Perceptual magnitude, 0..1, already dB-mapped for display.</param>
/// <param name="Dbfs">Raw level in dBFS, for diagnostics.</param>
/// <param name="Class">Heuristic category.</param>
/// <param name="ClassConfidence">0..1. Low values mean the classifier is guessing.</param>
/// <param name="Confidence">
/// 0..1 spatial coherence. 1 = all of the energy came from one direction.
/// Low values mean several sounds are happening at once, so the direction is a
/// weighted average and should be displayed more faintly.
/// </param>
/// <param name="DistanceConfidence">
/// How much to trust the front/back axis. 1 in multichannel mode, low in stereo mode.
/// </param>
/// <param name="Timestamp">Position in the captured stream, in seconds.</param>
public readonly record struct AudioEvent(
    Direction Direction,
    double Level,
    double Dbfs,
    SoundClass Class,
    double ClassConfidence,
    double Confidence,
    double DistanceConfidence,
    double Timestamp);

/// <summary>
/// Detects the start of a sound. This is the latency-critical part of the whole
/// pipeline: a footstep indicator is only useful if it appears while the sound is
/// still happening, not after it has finished.
///
/// The test is a rise over an adaptive floor plus a rise over the immediately
/// preceding frames, which distinguishes a footstep from a drone or a gunshot's tail.
/// </summary>
public sealed class OnsetDetector
{
    private const int HistoryLength = 6;

    private readonly AdaptiveNoiseFloor _floor;
    private readonly double[] _recent = new double[HistoryLength];
    private int _recentIndex;
    private int _recentCount;

    private double _absoluteFloorDb = -78.0;
    private double _relativeThreshold = 3.2;   // dB above the adaptive floor
    private double _riseThreshold = 2.0;       // dB above the preceding frame
    private double _absoluteCeilingDb = -18.0; // louder than this = a big transient, accept freely
    private double _refractorySeconds = 0.035;

    private double _lastOnsetTime = -10;
    private double _clock;

    public OnsetDetector(double sampleRate, double frameSize)
    {
        _frameSeconds = frameSize / sampleRate;
        // ~350 ms of history for the floor tracker: long enough to sit through a
        // firefight, short enough to follow a change of scene.
        _floor = new AdaptiveNoiseFloor((int)Math.Round(0.35 / _frameSeconds));
    }

    private readonly double _frameSeconds;

    public void Configure(AudioTuning tuning)
    {
        _absoluteFloorDb = tuning.OnsetAbsoluteFloorDb;
        _relativeThreshold = Math.Pow(10.0, tuning.OnsetRelativeThresholdDb / 20.0);
        _riseThreshold = Math.Pow(10.0, tuning.OnsetRiseThresholdDb / 20.0);
        _absoluteCeilingDb = tuning.OnsetAbsoluteCeilingDb;
        _refractorySeconds = tuning.OnsetRefractorySeconds;
    }

    public void Reset()
    {
        _floor.Reset();
        Array.Clear(_recent);
        _recentIndex = _recentCount = 0;
        _lastOnsetTime = -10;
        _clock = 0;
    }

    /// <summary>Advances the internal clock by one frame.</summary>
    public void AdvanceClock() => _clock += _frameSeconds;

    /// <summary>The most recent adaptive noise floor, in dBFS.</summary>
    public double NoiseFloorDb => Decibel.FromMeanSquare(_floor.Floor);

    /// <summary>
    /// Feeds one frame of total band energy. Returns true when this frame contains
    /// the onset of a new sound.
    /// </summary>
    public bool Update(double meanSquare)
    {
        AdvanceClock();
        var floor = _floor.Update((float)meanSquare);

        // Preceding-frame reference: the maximum of the short history, so a rising
        // tone does not keep retriggering.
        var prevMax = 0.0;
        for (var i = 0; i < _recentCount; i++) prevMax = Math.Max(prevMax, _recent[i]);
        if (_recentCount == 0) prevMax = floor;

        _recent[_recentIndex] = meanSquare;
        _recentIndex = (_recentIndex + 1) % HistoryLength;
        if (_recentCount < HistoryLength) _recentCount++;

        var db = Decibel.FromMeanSquare(meanSquare);
        var floorDb = Decibel.FromMeanSquare(floor);

        var loudEnough = db > _absoluteFloorDb && meanSquare > floor * _relativeThreshold;
        var rising = meanSquare > prevMax * _riseThreshold;
        var isBig = db > _absoluteCeilingDb;
        var pastRefractory = _clock - _lastOnsetTime > _refractorySeconds;

        if (!pastRefractory) return false;
        if (!loudEnough) return false;
        if (!isBig && !rising) return false;

        _lastOnsetTime = _clock;
        return true;
    }

    public double ClockSeconds => _clock;
}
