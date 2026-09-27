using BaldingAudio.Core.Audio;

namespace BaldingAudio.Core.Overlay;

/// <summary>
/// A live directional cue: where it points, how loud, and how long it stays up.
/// Events are merged per rail so a burst of footsteps produces one steady bar
/// rather than a flickering pile.
/// </summary>
public sealed class EventTracker
{
    private sealed class Track
    {
        public double Azimuth;
        public double FrontBack;
        public double Level;
        public double PeakLevel;
        public double Confidence;
        public double DistanceConfidence;
        public double Age;
        public double Hold;
        public double Fade;
        public SoundClass Class;
        public double Dbfs;
        public double ClassConfidence;
        public long Id;
    }

    private readonly List<Track> _tracks = new();
    private readonly double _holdSeconds;
    private readonly double _fadeSeconds;
    private long _nextId = 1;

    /// <summary>Two events closer than this on the front/back axis are treated as one cue.</summary>
    public double MergeWindowDegrees { get; set; } = 14.0;

    public EventTracker(double holdSeconds = 0.9, double fadeSeconds = 0.35)
    {
        _holdSeconds = Math.Max(0.05, holdSeconds);
        _fadeSeconds = Math.Max(0.01, fadeSeconds);
    }

    public int ActiveCount => _tracks.Count;
    public IReadOnlyList<AudioEvent> Visible => Snapshot();

    public void Push(AudioEvent e)
    {
        var rail = OverlayLayout.Rail(e.Direction.AzimuthDegrees);
        var frontBack = e.Direction.Frontness * 180.0;

        for (var i = 0; i < _tracks.Count; i++)
        {
            var t = _tracks[i];
            if (OverlayLayout.Rail(t.Azimuth) != rail) continue;
            if (Math.Abs(t.FrontBack - frontBack) > MergeWindowDegrees) continue;

            // Same cue, still sounding: sustain rather than restart.
            t.Level = Math.Max(t.Level, e.Level);
            t.PeakLevel = Math.Max(t.PeakLevel, e.Level);
            t.Azimuth = t.Azimuth * 0.6 + e.Direction.AzimuthDegrees * 0.4;
            t.FrontBack = OverlayLayout.FrontBackToVertical(t.Azimuth) * 180.0;
            t.Confidence = Math.Max(t.Confidence, e.Confidence);
            t.DistanceConfidence = Math.Max(t.DistanceConfidence, e.DistanceConfidence);
            t.Age = 0;
            t.Hold = _holdSeconds;
            t.Fade = _fadeSeconds;
            t.Dbfs = Math.Max(t.Dbfs, e.Dbfs);
            // A louder, higher-priority sound takes over the bar's colour.
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
            FrontBack = frontBack,
            Level = e.Level,
            PeakLevel = e.Level,
            Confidence = e.Confidence,
            DistanceConfidence = e.DistanceConfidence,
            Age = 0,
            Hold = _holdSeconds,
            Fade = _fadeSeconds,
            Class = e.Class,
            Dbfs = e.Dbfs,
            ClassConfidence = e.ClassConfidence,
            Id = _nextId++,
        });

        // Never let the screen fill with bars; keep the most assertive.
        const int maxTracks = 12;
        if (_tracks.Count > maxTracks)
        {
            _tracks.Sort(static (a, b) => (b.Level * (1 + Rank(b.Class))).CompareTo(a.Level * (1 + Rank(a.Class))));
            _tracks.RemoveRange(maxTracks, _tracks.Count - maxTracks);
        }
    }

    private static int Rank(SoundClass c) => c switch
    {
        SoundClass.Gunshot => 4,
        SoundClass.Footstep => 3,
        SoundClass.Explosion => 2,
        SoundClass.Vehicle => 1,
        _ => 0,
    };

    /// <summary>Advances decay by <paramref name="dt"/> seconds and retires expired cues.</summary>
    public void Tick(double dt)
    {
        for (var i = _tracks.Count - 1; i >= 0; i--)
        {
            var t = _tracks[i];
            t.Age += dt;
            if (t.Age <= t.Hold) continue;

            var into = t.Age - t.Hold;
            var fade = t.Fade <= 0 ? 0 : 1.0 - (into / t.Fade);
            t.Level = t.PeakLevel * Math.Clamp(fade, 0, 1);

            if (t.Level <= 0.005)
            {
                _tracks.RemoveAt(i);
                continue;
            }
        }
    }

    public void Clear() => _tracks.Clear();

    public List<AudioEvent> Snapshot()
    {
        var list = new List<AudioEvent>(_tracks.Count);
        foreach (var t in _tracks)
        {
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
