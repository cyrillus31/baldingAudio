using BaldingAudio.Core.Audio;
using BaldingAudio.Core.Dsp;
using BaldingAudio.Core.Overlay;

namespace BaldingAudio.App;

/// <summary>
/// Feeds the overlay a scripted sequence of cues, so the visual design can be checked
/// without a game, an audio device, or a working capture path.
///
/// Also the fastest way for the user to confirm the overlay is running and to see
/// what each colour and screen position means.
/// </summary>
public sealed class DemoSource
{
    private readonly Script[] _script;
    private int _next;
    private double _nextAt = 0.35;
    private int _cycle;

    /// <summary>
    /// When set, every cue is emitted once and then held instead of fading, so the
    /// full set of bars is on screen at the same time. This is what makes the layout
    /// inspectable: all of front, rear, left, right and centre are visible together
    /// rather than scrolling past.
    /// </summary>
    public bool HoldEverything { get; init; }

    private readonly record struct Script(
        double At,
        double Azimuth,
        double Level,
        SoundClass Class,
        string Note);

    public DemoSource()
    {
        // One pass of the script covers the whole front-to-back axis on both sides,
        // at three different loudnesses, so a single glance shows the layout works.
        _script = new[]
        {
            new Script(0.35, -25, 0.95, SoundClass.Gunshot, "gunshot, front-left, loud"),
            new Script(0.75,  35, 0.70, SoundClass.Gunshot, "gunshot, front-right"),
            new Script(1.25, -80, 0.55, SoundClass.Footstep, "footstep, left"),
            new Script(1.60,  95, 0.50, SoundClass.Footstep, "footstep, right"),
            new Script(2.05,   0, 0.60, SoundClass.Footstep, "footstep, dead ahead"),
            new Script(2.45, -140, 0.45, SoundClass.Footstep, "footstep, rear-left"),
            new Script(2.85, 165, 0.42, SoundClass.Footstep, "footstep, rear-right"),
            new Script(3.35, 180, 0.65, SoundClass.Explosion, "explosion, directly behind"),
            new Script(3.90, -50, 0.22, SoundClass.Footstep, "distant footstep, left"),
            new Script(4.25,  60, 0.18, SoundClass.Footstep, "very distant, right"),
            new Script(4.80,   0, 0.80, SoundClass.Vehicle, "vehicle, ahead"),
            new Script(5.40,  15, 0.30, SoundClass.Voice, "voice, ahead"),
        };
    }

    /// <summary>Length of one full pass, after which the script repeats.</summary>
    public double LoopSeconds => 6.0;

    public void Tick(double now, EventTracker tracker)
    {
        while (true)
        {
            var at = _nextAt;
            if (at > now) break;
            _nextAt = at + (HoldEverything ? 0.02 : 0.35);

            if (_next >= _script.Length)
            {
                // In hold mode the script plays once and then stops, leaving every
                // bar parked on screen at full opacity.
                if (HoldEverything) return;
                _next = 0;
                _cycle++;
            }

            var s = _script[_next++];
            tracker.Push(new AudioEvent(
                new Direction(s.Azimuth, 0),
                s.Level,
                Decibel.FromUnitLevel(s.Level),
                s.Class,
                0.8,
                HoldEverything ? 3600.0 : 0.9,
                1.0,
                at));
        }
    }
}
