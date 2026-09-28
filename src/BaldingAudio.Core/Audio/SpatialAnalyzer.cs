using BaldingAudio.Core.Config;
using BaldingAudio.Core.Dsp;

namespace BaldingAudio.Core.Audio;

/// <summary>
/// Turns an interleaved multichannel audio stream into directional sound events.
///
/// Two modes, chosen automatically from the stream's channel mask:
///
///  * <b>Multichannel</b> (5.1 / 7.1 / 7.1.4). Each channel corresponds to a real
///    speaker position, so direction is measured directly and front/back is fully
///    resolved. This is the accurate mode.
///  * <b>Stereo</b>. The audio has already been collapsed to two ears, so direction is
///    estimated from inter-aural level and time differences. Left/right is reliable;
///    front/back is a low-confidence guess, and the app says so rather than pretending.
/// </summary>
public sealed class SpatialAnalyzer
{
    private const int BandCount = 3;
    private const int MaxChannels = 24;

    private readonly AudioTuning _tuning;
    private readonly int _channelCount;
    private readonly int _frameSize;
    private readonly double _sampleRate;
    private readonly uint _channelMask;
    private readonly Direction?[] _channelDirection;
    private readonly bool[] _isDirectional;

    private readonly BandPass?[] _low = new BandPass[MaxChannels];
    private readonly BandPass?[] _mid = new BandPass[MaxChannels];
    private readonly BandPass?[] _high = new BandPass[MaxChannels];

    /// <summary>Interleaved carry-over samples that did not fill a whole frame.</summary>
    private readonly float[] _pending;
    private int _pendingFrames;

    private readonly OnsetDetector _onset;
    private readonly StereoItd _stereo;

    /// <summary>True when the two channels are a mix and not a pair of speakers.</summary>
    private bool _useStereo;

    /// <summary>Bearing StereoItd measured this frame. Read by BuildSpectrum.</summary>
    private double _stereoAzimuth;

    /// <summary>
    /// True when the stereo bearing carries no side, so the overlay must not pick one.
    /// Read by BuildSpectrum and Emit; see <see cref="StereoItd.LastAmbiguous"/>.
    /// </summary>
    private bool _stereoAmbiguous;

    private readonly double[] _bandEnergy = new double[BandCount];
    private readonly double[][] _channelBand;
    private readonly double[] _channelTotal = new double[MaxChannels];

    // Event accumulation state.
    private bool _inEvent;
    private double _eventPeak;
    private double _eventStartClock;
    private double _eventSilenceRun;
    private double _riseDb;
    private double _peakLow, _peakMid, _peakHigh, _peakBandTotal;
    private Direction _eventDirection = Direction.Forward;
    private double _eventConfidence;
    private double _eventDistanceConfidence;
    private readonly double[] _bandVoteX = new double[BandCount];
    private readonly double[] _bandVoteZ = new double[BandCount];
    private readonly double[] _bandVoteW = new double[BandCount];

    public SpatialAnalyzer(
        uint channelMask,
        int channelCount,
        double sampleRate,
        AudioTuning? tuning = null,
        int frameSize = 128)
    {
        _tuning = tuning ?? AudioTuning.Default();
        _channelMask = channelMask;
        _channelCount = Math.Clamp(channelCount, 1, MaxChannels);
        _sampleRate = sampleRate;
        _frameSize = Math.Max(32, frameSize);

        _channelDirection = ChannelLayout.Resolve(channelMask, _channelCount);
        _isDirectional = new bool[_channelCount];
        for (var c = 0; c < _channelCount; c++)
            _isDirectional[c] = _channelDirection[c] is not null;

        for (var c = 0; c < _channelCount; c++)
        {
            if (!_isDirectional[c]) continue;
            _low[c] = new BandPass(sampleRate, _tuning.LowBandHz, _tuning.LowMidSplitHz);
            _mid[c] = new BandPass(sampleRate, _tuning.LowMidSplitHz, _tuning.MidHighSplitHz);
            _high[c] = new BandPass(sampleRate, _tuning.MidHighSplitHz, _tuning.HighBandCeilingHz);
        }

        _channelBand = new double[_channelCount][];
        for (var c = 0; c < _channelCount; c++) _channelBand[c] = new double[BandCount];

        _pending = new float[_frameSize * _channelCount];

        _onset = new OnsetDetector(sampleRate, _frameSize);
        _onset.Configure(_tuning);
        _stereo = new StereoItd(sampleRate, _frameSize);
    }

    public bool HasReliableFrontBack => ChannelLayout.SupportsFrontBack(_channelMask);
    public int ChannelCount => _channelCount;
    public uint ChannelMask => _channelMask;
    public double NoiseFloorDb => _onset.NoiseFloorDb;
    public double FrameSeconds => _frameSize / _sampleRate;
    public string LayoutDescription => ChannelLayout.Describe(_channelMask);
    public double LastEventDbfs { get; private set; }
    public int SuppressedCount { get; private set; }

    /// <summary>
    /// Where sound energy is on this frame. Read it from the capture thread immediately
    /// after <see cref="Process"/>; the overlay uses it to keep lines moving.
    /// </summary>
    public DirectionSpectrum Spectrum { get; } = new();

    /// <summary>
    /// Processes a run of interleaved samples, appending any completed events to
    /// <paramref name="output"/>. Framing is handled internally.
    /// </summary>
    /// <param name="interleaved">Interleaved float samples, nominally in [-1, 1].</param>
    /// <param name="frameCount">Number of sample frames (not samples) supplied.</param>
    /// <param name="output">Sink for completed events.</param>
    /// <returns>Number of events appended.</returns>
    public int Process(ReadOnlySpan<float> interleaved, int frameCount, List<AudioEvent> output)
    {
        var stride = _channelCount;
        var produced = 0;
        var consumed = 0;

        while (consumed < frameCount)
        {
            var need = _frameSize - _pendingFrames;
            var take = Math.Min(need, frameCount - consumed);
            var dst = _pendingFrames * stride;
            for (var i = 0; i < take; i++)
            {
                var src = (consumed + i) * stride;
                var d = dst + i * stride;
                for (var c = 0; c < stride; c++) _pending[d + c] = interleaved[src + c];
            }

            _pendingFrames += take;
            consumed += take;

            if (_pendingFrames < _frameSize) break;
            _pendingFrames = 0;
            if (ProcessFrame(_pending, output)) produced++;
        }

        return produced;
    }

    /// <summary>Processes exactly one full frame of interleaved samples.</summary>
    private bool ProcessFrame(float[] data, List<AudioEvent> output)
    {
        var stride = _channelCount;
        var useStereo = !HasReliableFrontBack && _channelCount >= 2;

        for (var c = 0; c < _channelCount; c++) Array.Clear(_channelBand[c]);
        Array.Clear(_bandEnergy);
        Array.Clear(_channelTotal);

        for (var c = 0; c < _channelCount; c++)
        {
            double eLow = 0, eMid = 0, eHigh = 0;
            var low = _low[c];
            var mid = _mid[c];
            var high = _high[c];

            // All three are allocated together whenever the channel has a direction,
            // so this null check narrows all of them at once.
            if (low is null || mid is null || high is null)
            {
                // LFE or otherwise unmapped: contributes loudness, never direction.
                for (var i = 0; i < _frameSize; i++)
                {
                    var s = data[i * stride + c];
                    eLow += (double)s * s;
                }
                eLow /= _frameSize;
                _channelBand[c][0] = eLow;
                _channelBand[c][1] = 0;
                _channelBand[c][2] = 0;
                _channelTotal[c] = eLow * 0.5;
                continue;
            }

            for (var i = 0; i < _frameSize; i++)
            {
                var s = data[i * stride + c];
                var yl = low.Process(s);
                var ym = mid.Process(s);
                var yh = high.Process(s);
                eLow += (double)yl * yl;
                eMid += (double)ym * ym;
                eHigh += (double)yh * yh;
            }

            eLow /= _frameSize; eMid /= _frameSize; eHigh /= _frameSize;
            _channelBand[c][0] = eLow;
            _channelBand[c][1] = eMid;
            _channelBand[c][2] = eHigh;
            _channelTotal[c] = eLow + 2.0 * eMid + 1.5 * eHigh;
        }

        for (var c = 0; c < _channelCount; c++)
            for (var b = 0; b < BandCount; b++)
                _bandEnergy[b] += _channelBand[c][b];

        if (useStereo) _stereo.Analyse(data, _frameSize, stride);

        var totalWeighted = _bandEnergy[0] + 2.0 * _bandEnergy[1] + 1.5 * _bandEnergy[2];

        _useStereo = useStereo;
        if (useStereo)
        {
            _stereoAzimuth = _stereo.Result.Direction.AzimuthDegrees;
            _stereoAmbiguous = _stereo.LastAmbiguous;
        }

        // Publish the current sound field every frame, not only on onsets, so the
        // overlay can follow a sound that is moving. Same per-channel energy the
        // direction estimate uses, so the two cannot disagree.
        BuildSpectrum();


        var onOnset = _onset.Update(totalWeighted);
        if (onOnset)
        {
            var wasAlreadySounding = _inEvent;
            _inEvent = true;
            _eventPeak = totalWeighted;
            _eventStartClock = _onset.ClockSeconds;
            _riseDb = Math.Max(0, Decibel.FromMeanSquare(totalWeighted) - Decibel.FromMeanSquare(totalWeighted * 0.25));
            _eventSilenceRun = 0;
            CapturePeakBandRatios();
            CaptureDirection(useStereo, 1.0);

            // Emit now rather than when the sound stops.
            //
            // Waiting for the silence test below is wrong twice over. A line that only
            // appears after the sound has finished is useless in a shooter. Worse, the
            // test needs the level to sit 18 dB below the event's peak for 120 ms, and
            // continuous audio never does that - music, an engine, a firefight - so a
            // steady soundtrack produced no events at all and the overlay stayed blank
            // while the analyser was demonstrably tracking it.
            //
            // Only the first onset of a sound emits; later onsets during the same sound
            // update the peak instead, so one sound is one event. EventTracker.Follow
            // keeps the line's level and bearing current from the live spectrum in the
            // meantime, and the completion below still emits once more with the true
            // peak so the line ends at the right length.
            if (!wasAlreadySounding) Emit(output);
        }
        else if (_inEvent)
        {
            if (totalWeighted > _eventPeak)
            {
                _eventPeak = totalWeighted;
                CapturePeakBandRatios();
            }
            CaptureDirection(useStereo, 0.35);

            if (totalWeighted < _eventPeak * 0.12) _eventSilenceRun += FrameSeconds;
            else _eventSilenceRun = 0;
        }

        if (!_inEvent) return false;
        if (_eventSilenceRun <= 0.12) return false;

        _inEvent = false;
        return Emit(output);
    }

    /// <summary>
    /// Turns this frame's per-channel energy into a spectrum over bearings, using the
    /// same level mapping as the event's dBFS so that a line's length and an event's
    /// loudness are directly comparable.
    /// </summary>
    private void BuildSpectrum()
    {
        Spectrum.Clear();

        // Stereo is a mix, not a set of speakers, so per-channel position is meaningless
        // here: putting half the energy at the fixed -60 of the left channel and half at
        // the fixed +60 of the right describes the wiring, not the sound. Every bearing
        // then reads the same, the tracker has no gradient to follow, and a line never
        // moves - it just sits wherever its first event landed.
        //
        // So publish it once, at the bearing StereoItd actually measured. That agrees
        // with the per-event direction, and gives the tracker a real peak to slew toward.
        if (_useStereo)
        {
            var total = 0.0;
            for (var c = 0; c < _channelCount; c++) total += _channelTotal[c];
            if (total <= 1e-12) return;

            var db = Decibel.FromMeanSquare(total);
            var level = Decibel.ToUnit(db, _tuning.DisplayFloorDb, _tuning.DisplayCeilingDb, _tuning.LevelExponent);
            if (level > 0)
            {
                Spectrum.Add(_stereoAzimuth, level);
                Spectrum.Ambiguous = _stereoAmbiguous;
            }
            return;
        }

        for (var c = 0; c < _channelCount; c++)
        {
            var energy = _channelTotal[c];
            if (energy <= 1e-12) continue;
            if (!_isDirectional[c]) continue;

            var az = _channelDirection[c]!.Value.AzimuthDegrees;
            var db = Decibel.FromMeanSquare(energy);
            Spectrum.Add(az, Decibel.ToUnit(db, _tuning.DisplayFloorDb, _tuning.DisplayCeilingDb, _tuning.LevelExponent));
        }
    }

    private void CapturePeakBandRatios()
    {
        double best = -1;
        _peakLow = _peakMid = _peakHigh = _peakBandTotal = 0;
        for (var c = 0; c < _channelCount; c++)
        {
            var l = _channelBand[c][0];
            var m = _channelBand[c][1];
            var h = _channelBand[c][2];
            var t = l + m + h;
            if (t <= best) continue;
            best = t;
            _peakLow = l; _peakMid = m; _peakHigh = h; _peakBandTotal = t;
        }
    }

    /// <summary>
    /// Turns the current in-progress sound into an <see cref="AudioEvent"/> and adds it
    /// to <paramref name="output"/>.
    ///
    /// Called twice per sound: once at the onset, so the line appears while the sound is
    /// happening, and once when the sound has finished, so the line's final length
    /// reflects the true peak rather than the level at the instant it started.
    /// </summary>
    private bool Emit(List<AudioEvent> output)
    {
        var dbfs = Decibel.FromMeanSquare(_eventPeak);
        LastEventDbfs = dbfs;

        var level = Decibel.ToUnit(dbfs, _tuning.DisplayFloorDb, _tuning.DisplayCeilingDb, _tuning.LevelExponent);
        if (level <= 0.002) return false;

        var dir = _eventDirection;
        if (_tuning.SuppressOwnSounds
            && Math.Abs(dir.AzimuthDegrees) < _tuning.SelfSoundConeDegrees
            && dbfs > _tuning.SelfSoundLevelDb)
        {
            SuppressedCount++;
            return false;
        }

        var duration = _onset.ClockSeconds - _eventStartClock;
        var (cls, clsConf) = Classify(duration, dbfs);

        output.Add(new AudioEvent(
            dir,
            level,
            dbfs,
            cls,
            clsConf,
            _eventConfidence,
            _eventDistanceConfidence,
            _eventStartClock)
        {
            // Only meaningful on a two-channel endpoint, and false everywhere else,
            // because each multichannel speaker really does have a side.
            Ambiguous = _useStereo && _stereoAmbiguous,
        });
        return true;
    }

    private void CaptureDirection(bool useStereo, double smoothing)
    {
        Direction dir;
        double confidence;
        double distanceConfidence;

        if (useStereo)
        {
            (dir, confidence, distanceConfidence) = _stereo.Result;
        }
        else
        {
            (dir, confidence, distanceConfidence) = MultichannelDirection();
        }

        if (smoothing >= 1.0)
        {
            _eventDirection = dir;
            _eventConfidence = confidence;
            _eventDistanceConfidence = distanceConfidence;
        }
        else
        {
            var az = LerpAngle(_eventDirection.AzimuthDegrees, dir.AzimuthDegrees, smoothing);
            _eventDirection = new Direction(az, _eventDirection.ElevationDegrees);
            _eventConfidence = Math.Max(_eventConfidence * 0.85, confidence);
            _eventDistanceConfidence = Math.Max(_eventDistanceConfidence, distanceConfidence);
        }
    }

    /// <summary>
    /// Energy-weighted vector vote. Each frequency band votes independently and the
    /// votes are combined weighted by how much energy each band currently holds, so a
    /// broadband gunshot is located by its crack and a footstep by its body.
    /// </summary>
    private (Direction, double, double) MultichannelDirection()
    {
        Array.Clear(_bandVoteX);
        Array.Clear(_bandVoteZ);
        Array.Clear(_bandVoteW);

        double sumE = 0, maxE = 0;
        var directionalCount = 0;
        for (var c = 0; c < _channelCount; c++)
        {
            if (!_isDirectional[c]) continue;
            directionalCount++;
            sumE += _channelTotal[c];
            maxE = Math.Max(maxE, _channelTotal[c]);
            for (var b = 0; b < BandCount; b++)
            {
                var e = _channelBand[c][b];
                if (e <= 0) continue;
                var (ux, uz) = _channelDirection[c]!.Value.HorizontalUnitVector();
                _bandVoteX[b] += e * ux;
                _bandVoteZ[b] += e * uz;
                _bandVoteW[b] += e;
            }
        }

        if (directionalCount == 0) return (Direction.Forward, 0, 0);

        double vx = 0, vz = 0, weight = 0;
        for (var b = 0; b < BandCount; b++)
        {
            if (_bandVoteW[b] <= 0) continue;
            var bandEnergy = _bandEnergy[b];
            if (bandEnergy <= 0) continue;
            vx += _bandVoteX[b] / _bandVoteW[b] * bandEnergy;
            vz += _bandVoteZ[b] / _bandVoteW[b] * bandEnergy;
            weight += bandEnergy;
        }

        if (weight <= 0) return (Direction.Forward, 0, 0);

        var coherence = Math.Clamp(Math.Sqrt(vx * vx + vz * vz) / weight, 0, 1);

        // A near-equal mix across all speakers carries almost no positional
        // information; fade the bar out rather than pointing somewhere arbitrary.
        var meanE = sumE / directionalCount;
        if (maxE > 0 && meanE > 0)
        {
            var contrast = Decibel.FromMeanSquare(maxE) - Decibel.FromMeanSquare(meanE);
            if (contrast < _tuning.DirectionalContrastDb)
                coherence *= Math.Clamp(contrast / Math.Max(_tuning.DirectionalContrastDb, 0.001), 0, 1) * 0.5 + 0.2;
        }

        var az = Math.Atan2(vx, vz) * 180.0 / Math.PI;
        return (new Direction(az, 0), coherence, 1.0);
    }

    private (SoundClass, double) Classify(double durationSeconds, double dbfs)
    {
        if (_peakBandTotal <= 0) return (SoundClass.Other, 0);
        var rLow = _peakLow / _peakBandTotal;
        var rMid = _peakMid / _peakBandTotal;
        var rHigh = _peakHigh / _peakBandTotal;

        var loud = dbfs > -28;
        var sharp = _riseDb > 15;
        var crisp = _riseDb > 7;

        if (sharp && (rHigh > 0.16 || (rMid > 0.32 && rLow < 0.45)) && loud)
            return (SoundClass.Gunshot, Math.Clamp(0.45 + (_riseDb - 15) / 25.0, 0, 1));

        if (crisp && rMid >= rLow && rMid >= rHigh && (rLow + rMid) > 0.5 && durationSeconds < 0.5)
            return (SoundClass.Footstep, Math.Clamp(0.45 + (rMid - 0.34) * 1.8, 0, 1));

        if (rLow > 0.5 && durationSeconds > 0.28)
            return (SoundClass.Explosion, Math.Clamp((rLow - 0.5) * 1.7, 0, 1));

        if (durationSeconds > 0.55 && (rLow + rMid) > 0.55)
            return (SoundClass.Vehicle, 0.4);

        if (!crisp && rMid > 0.38)
            return (SoundClass.Voice, 0.4);

        return (SoundClass.Other, 0.3);
    }

    private static double LerpAngle(double from, double to, double t)
    {
        var d = ((to - from + 540.0) % 360.0) - 180.0;
        return from + d * t;
    }

    public void Reset()
    {
        for (var c = 0; c < _channelCount; c++)
        {
            _low[c]?.Reset(); _mid[c]?.Reset(); _high[c]?.Reset();
        }
        _onset.Reset();
        _stereo.Reset();
        _inEvent = false;
        _pendingFrames = 0;
        Array.Clear(_pending);
    }
}
