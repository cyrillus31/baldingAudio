using BaldingAudio.Core.Audio;

namespace BaldingAudio.Core.Overlay;

/// <summary>
/// Keeps a live line per sound, following it as it moves.
///
/// An onset says "something started over there". On its own that produces a bar that
/// appears, sits still, and disappears - which is useless for a sound that is walking
/// around you. So each track is instead re-measured against the analyser's
/// <see cref="DirectionSpectrum"/> on every frame:
///
///  * level    follows the spectrum with a fast attack and a slower release, which is
///             what makes the line jump on a footstep and fall away between steps
///             rather than blinking on and off
///  * bearing  slews toward the loudest bearing nearby, so an enemy crossing the player
///             takes the line from one edge of the screen to the other
///  * an event arriving for a track that already exists raises that track instead of
///    creating a second line for the same sound
///
/// Nothing is drawn while nothing is sounding.
/// </summary>
public sealed class EventTracker
{
    private sealed class Track
    {
        public double Azimuth;
        public double Level;
        public double TargetLevel;
        public double Confidence;
        public double DistanceConfidence;
        public double Age;
        public double Silence;
        public double Dbfs;
        public double ClassConfidence;
        public SoundClass Class;
        public long Id;
    }

    private readonly List<Track> _tracks = new();
    private long _nextId = 1;

    /// <summary>Seconds for a line to reach the currently measured level. Fast on purpose.</summary>
    public double AttackSeconds { get; set; } = 0.012;

    /// <summary>Seconds for a line to fall from full to nothing. Slow, so it reads as a meter.</summary>
    public double ReleaseSeconds { get; set; } = 0.22;

    /// <summary>Longest a line can stay alive on release alone, after which it is dropped.</summary>
    public double MaxSilenceSeconds { get; set; } = 0.45;

    /// <summary>How far a line may look for a moved source, in degrees.</summary>
    public double FollowWindowDegrees { get; set; } = 30.0;

    /// <summary>Maximum bearing change per second, in degrees. Stops the line flickering.</summary>
    public double SlewDegreesPerSecond { get; set; } = 420.0;

    /// <summary>Below this level a bearing is considered noise, not a moved source.</summary>
    public double FollowThreshold { get; set; } = 0.06;

    /// <summary>Two events closer than this on the front/back axis are one cue.</summary>
    public double MergeWindowDegrees { get; set; } = 16.0;

    public EventTracker()
    {
    }

    /// <summary>Legacy constructor; the hold and fade became the attack and release.</summary>
    public EventTracker(double holdSeconds, double fadeSeconds)
    {
        AttackSeconds = Math.Max(0.004, holdSeconds * 0.05);
        ReleaseSeconds = Math.Max(0.02, fadeSeconds);
    }

    public int ActiveCount => _tracks.Count;
    public IReadOnlyList<AudioEvent> Visible => Snapshot();

    /// <summary>Starts a line, or raises the matching one if it is already up.</summary>
    public void Push(AudioEvent e)
    {
        var rail = OverlayLayout.Rail(e.Direction.AzimuthDegrees);
        var frontBack = e.Direction.Frontness * 180.0;

        for (var i = 0; i < _tracks.Count; i++)
        {
            var t = _tracks[i];
            if (OverlayLayout.Rail(t.Azimuth) != rail) continue;
            if (Math.Abs(FrontBackOf(t.Azimuth) - frontBack) > MergeWindowDegrees) continue;

            // Same cue, still sounding: sustain rather than restart.
            t.Azimuth = DirectionSpectrum.Wrap180(t.Azimuth * 0.55 + e.Direction.AzimuthDegrees * 0.45);
            t.TargetLevel = Math.Max(t.TargetLevel, e.Level);
            t.Level = Math.Max(t.Level, e.Level);
            t.Confidence = Math.Max(t.Confidence, e.Confidence);
            t.DistanceConfidence = Math.Max(t.DistanceConfidence, e.DistanceConfidence);
            t.Age = 0;
            t.Silence = 0;
            t.Dbfs = Math.Max(t.Dbfs, e.Dbfs);
            if (Rank(e.Class) >= Rank(t.Class))
            {
                t.Class = e.Class;
                t.ClassConfidence = e.ClassConfidence;
            }
            return;
        }

        _tracks.Add(new Track
        {
            Azimuth = e.Direction.AzimuthDegrees,
            Level = e.Level,
            TargetLevel = e.Level,
            Confidence = e.Confidence,
            DistanceConfidence = e.DistanceConfidence,
            Age = 0,
            Silence = 0,
            Dbfs = e.Dbfs,
            Class = e.Class,
            ClassConfidence = e.ClassConfidence,
            Id = _nextId++,
        });

        const int maxTracks = 12;
        if (_tracks.Count > maxTracks)
        {
            _tracks.Sort(static (a, b) => (b.Level * (1 + Rank(b.Class))).CompareTo(a.Level * (1 + Rank(a.Class))));
            _tracks.RemoveRange(maxTracks, _tracks.Count - maxTracks);
        }
    }

    private static double FrontBackOf(double azimuth) => OverlayLayout.FrontBackToVertical(azimuth) * 180.0;

    private static int Rank(SoundClass c) => c switch
    {
        SoundClass.Gunshot => 4,
        SoundClass.Footstep => 3,
        SoundClass.Explosion => 2,
        SoundClass.Vehicle => 1,
        _ => 0,
    };

    /// <summary>
    /// Advances the envelopes and retires lines that have fallen to nothing.
    /// Call once per rendered frame with the elapsed time.
    /// </summary>
    public void Tick(double dt)
    {
        if (dt <= 0) return;

        for (var i = _tracks.Count - 1; i >= 0; i--)
        {
            var t = _tracks[i];
            t.Age += dt;
            t.Level = Step(t.Level, t.TargetLevel, dt);

            if (t.Level > 0.004)
            {
                t.Silence = 0;
            }
            else
            {
                t.Silence += dt;
                if (t.Silence > MaxSilenceSeconds)
                {
                    _tracks.RemoveAt(i);
                    continue;
                }
            }
        }
    }

    /// <summary>
    /// Re-measures every live line against the current sound field. This is what makes
    /// a line travel as its source moves, and what makes it rise and fall with the
    /// sound instead of only reacting to new events.
    /// </summary>
    public void Follow(DirectionSpectrum spectrum, double dt)
    {
        if (spectrum is null || dt <= 0) return;

        for (var i = 0; i < _tracks.Count; i++)
        {
            var t = _tracks[i];

            var (peakAz, peakLevel) = spectrum.PeakNear(t.Azimuth, FollowWindowDegrees);

            if (peakLevel >= FollowThreshold)
            {
                t.TargetLevel = peakLevel;

                // Slew toward the louder bearing, but only when it is actually louder
                // than where the line already is. Otherwise a line would drift toward
                // whatever noise happened to be nearby.
                var here = spectrum.LevelAt(t.Azimuth);
                if (peakLevel > here * 1.15)
                {
                    var maxStep = SlewDegreesPerSecond * dt;
                    var delta = DirectionSpectrum.Wrap180(peakAz - t.Azimuth);
                    var move = Math.Clamp(delta, -maxStep, maxStep);
                    t.Azimuth = DirectionSpectrum.Wrap180(t.Azimuth + move);
                }
            }
            else
            {
                // Nothing measured there: let the line fall back rather than hold.
                t.TargetLevel = 0;
            }
        }
    }

    /// <summary>One-pole envelope step: rise at the attack rate, fall at the release rate.</summary>
    private double Step(double current, double target, double dt)
    {
        if (target >= current)
        {
            var rate = 1.0 / Math.Max(0.001, AttackSeconds);
            return Math.Min(target, current + rate * dt);
        }

        var fall = ReleaseSeconds <= 0 ? 0 : dt / ReleaseSeconds;
        return Math.Max(target, current - fall);
    }

    public void Clear() => _tracks.Clear();

    public List<AudioEvent> Snapshot()
    {
        var list = new List<AudioEvent>(_tracks.Count);
        foreach (var t in _tracks)
        {
            if (t.Level <= 0.004) continue;
            list.Add(new AudioEvent(
                new Direction(t.Azimuth, 0),
                t.Level,
                t.Dbfs,
                t.Class,
                t.ClassConfidence,
                t.Confidence,
                t.DistanceConfidence,
                0));
        }
        return list;
    }
}
