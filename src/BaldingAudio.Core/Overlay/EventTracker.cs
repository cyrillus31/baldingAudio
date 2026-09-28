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

        /// <summary>Level the envelope is heading towards, per frame from the spectrum.</summary>
        public double TargetLevel;

        /// <summary>
        /// Balance the envelope is heading towards, per frame from the spectrum, in the
        /// same units as <see cref="TargetLevel"/>. Separate from it because they answer
        /// different questions and a line has to show both: how loud, and how lopsided.
        /// </summary>
        public double TargetBalance;

        /// <summary>Balance after the envelope, -1..1. Drives the edge and the length.</summary>
        public double Balance;

        public double Confidence;
        public double DistanceConfidence;
        public double Age;
        public double Silence;
        public double Dbfs;
        public double ClassConfidence;
        public SoundClass Class;

        /// <summary>Null in multichannel mode, where the edge and length come from elsewhere.</summary>
        public double? BalanceDriven;

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

    /// <summary>
    /// Level at which a bearing with no line near it starts one.
    ///
    /// <para>
    /// Deliberately higher than <see cref="FollowThreshold"/>. Following an existing
    /// line is cheap and a low bar is right for it, because the line is already on
    /// screen. Starting a line is not: it puts something new in the player's field of
    /// view, so it takes a clearly audible bearing rather than a merely present one.
    /// </para>
    /// </summary>
    public double SpectrumStartThreshold { get; set; } = 0.18;

    /// <summary>
    /// How far, in degrees, a bearing may sit from a line and still be considered the
    /// same source - both when re-aiming a line and when deciding not to start one.
    ///
    /// <para>
    /// Much wider than <see cref="FollowWindowDegrees"/>, and it has to be. On a
    /// two-channel endpoint the whole spectrum is one bearing measured by interaural
    /// timing, and that bearing jumps tens of degrees between frames as the content
    /// changes. A tight window strands a line that is plainly still sounding, and makes
    /// a wandering one spawn a new line every frame instead of travelling.
    /// </para>
    /// </summary>
    public double StartWindowDegrees { get; set; } = 75.0;

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
            t.BalanceDriven = e.Balance;
            if (e.Balance is double b)
            {
                t.TargetBalance = b;
                t.Balance = b;
            }
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
            BalanceDriven = e.Balance,
            Balance = e.Balance ?? 0.0,
            TargetBalance = e.Balance ?? 0.0,
            Id = _nextId++,
        });

        TrimToLimit();
    }

    /// <summary>
    /// Adds a line the sound field asked for rather than an onset, and applies the same
    /// cap. Class and dBFS are left at their defaults: a line started from the spectrum
    /// has no onset behind it, so there is nothing honest to label it with.
    /// </summary>
    private void AddTrack(Track t)
    {
        t.Id = _nextId++;
        _tracks.Add(t);
        TrimToLimit();
    }

    private void TrimToLimit()
    {
        const int maxTracks = 12;
        if (_tracks.Count <= maxTracks) return;
        _tracks.Sort(static (a, b) => (b.Level * (1 + Rank(b.Class))).CompareTo(a.Level * (1 + Rank(a.Class))));
        _tracks.RemoveRange(maxTracks, _tracks.Count - maxTracks);
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

        // Frozen: hold every line exactly as it is. See Freeze.
        if (_frozen) return;

        for (var i = _tracks.Count - 1; i >= 0; i--)
        {
            var t = _tracks[i];
            t.Age += dt;
            t.Level = Step(t.Level, t.TargetLevel, dt);

            // The balance gets the same envelope, so the bar length and the edge move
            // at the speed the sound does. Stepping the signed value rather than the
            // magnitude is what makes a line slide across the band when a sound passes
            // in front of the player, instead of jumping from one edge to the other.
            t.Balance = Step(t.Balance, t.TargetBalance, dt);

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
    /// Re-measures every live line against the current sound field, and starts lines
    /// for bearings that are loud with nothing on screen to show for them.
    ///
    /// <para>
    /// Starting lines here is the point. A track used to be created only by an onset,
    /// so a sound that was already sounding when the last onset passed - music, a
    /// sustained burst of gunfire, footsteps while walking - had a live, loud direction
    /// spectrum and nothing on screen. The field log showed the spectrum loud on 109 of
    /// 119 heartbeats with a line on 27.
    /// </para>
    /// </summary>
    public void Follow(DirectionSpectrum spectrum, double dt)
    {
        if (spectrum is null || dt <= 0) return;

        var (globalAz, globalLevel) = spectrum.Peak();

        for (var i = 0; i < _tracks.Count; i++)
        {
            var t = _tracks[i];

            var (peakAz, peakLevel) = spectrum.PeakNear(t.Azimuth, FollowWindowDegrees);

            if (peakLevel >= FollowThreshold)
            {
                t.TargetLevel = peakLevel;
                RetargetBalance(t, spectrum);

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
                continue;
            }

            // Nothing inside the tight follow window. Before writing the line off,
            // check the wider start window: on a two-channel endpoint the measured
            // bearing moves by tens of degrees between frames, and a tight window would
            // retire a line for a source that never stopped sounding.
            if (globalLevel >= SpectrumStartThreshold &&
                Math.Abs(DirectionSpectrum.Wrap180(globalAz - t.Azimuth)) <= StartWindowDegrees)
            {
                t.TargetLevel = globalLevel;
                RetargetBalance(t, spectrum);
                continue;
            }

            // Nothing measured there: let the line fall back rather than hold.
            //
            // The balance goes with the level. A line whose sound has stopped being
            // lopsided has to shrink, or a line that happened to be hard right when it
            // started stays hard right for the whole release and keeps claiming a side
            // that no longer exists.
            t.TargetLevel = 0;
            t.TargetBalance = 0;
        }

        if (globalLevel < SpectrumStartThreshold) return;
        foreach (var t in _tracks)
            if (Math.Abs(DirectionSpectrum.Wrap180(globalAz - t.Azimuth)) <= StartWindowDegrees)
                return;

        AddTrack(new Track
        {
            Azimuth = globalAz,
            Level = globalLevel,
            TargetLevel = globalLevel,
            Confidence = 0.5,
            DistanceConfidence = 0.0,
            BalanceDriven = spectrum.Balance,
            Balance = spectrum.Balance ?? 0.0,
            TargetBalance = spectrum.Balance ?? 0.0,
        });
    }

    /// <summary>
    /// Aims a line at the balance the spectrum is reporting this frame.
    ///
    /// <para>
    /// Guarded on the spectrum actually carrying one, because a multichannel frame
    /// has none and a null there would silently retarget every line's balance to zero
    /// and blank the overlay on an endpoint that is working perfectly well.
    /// </para>
    /// </summary>
    private static void RetargetBalance(Track t, DirectionSpectrum spectrum)
    {
        if (spectrum.Balance is not double b) return;
        t.BalanceDriven = b;
        t.TargetBalance = b;
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

    private bool _frozen;

    /// <summary>
    /// Holds the display still, without emptying it.
    ///
    /// <para>
    /// This exists because pausing used to call <see cref="Clear"/>, and that made a
    /// pause look exactly like a crash: the user saw the overlay disappear partway
    /// through a game and had no way to tell a toggle from a fault. Freezing keeps the
    /// last frame on screen, so the overlay is still visibly there and still visibly
    /// wrong rather than simply gone.
    /// </para>
    ///
    /// <para>
    /// Not a pause of the whole app: the capture thread and the analyser keep running,
    /// and the lines resume from live data on <see cref="Thaw"/>.
    /// </para>
    /// </summary>
    public void Freeze() => _frozen = true;

    /// <summary>Releases a freeze, and lets the envelopes run again from here.</summary>
    public void Thaw() => _frozen = false;

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
                0)
            {
                Balance = t.BalanceDriven,
            });
        }
        return list;
    }
}
