namespace BaldingAudio.Core.Diagnostics;

/// <summary>
/// Synthesised test audio for the settings window.
///
/// <para>
/// Two scenes, matching the two things worth checking by ear and eye:
///
/// </para>
///
/// <list type="bullet">
/// <item>
/// <b>Gunshots from both sides.</b> Alternating left and right, so the correct edge can be
/// confirmed at a glance - a bar on the wrong side is the failure a screenshot hides.
/// </item>
/// <item>
/// <b>Distant war, with a footstep crossing right to left.</b> A low bed of distant fire
/// under a walk that pans across from one edge to the other. This is the direct test for
/// whether lines *move*, which is the open problem of a direction indicator that reports
/// a bearing but shows nothing sliding.
/// </item>
/// </list>
///
/// <para>
/// Everything is generated, not sampled: nothing to license, works offline, and the
/// signal is exactly repeatable, so a threshold found with it still holds next time.
/// </para>
///
/// <para>
/// Pan is applied per transient rather than per file, because a crossing footstep is the
/// whole point of the second scene and a single file-wide pan cannot express one. That in
/// turn means each transient is rendered at its own pan and summed into the mix, so the
/// ambience bed stays exactly centred and cannot contribute a side of its own.
/// </para>
///
/// <para>
/// The scenes are mixed to a single buffer before playback, because the app's playback
/// path sounds one file at a time - starting a second sound replaces the first - so a
/// gunshot cannot be layered over a looping bed. Mixing avoids that entirely.
/// </para>
/// </summary>
public static class TestSignals
{
    /// <summary>
    /// Endpoints report this; generated audio uses the device's own rate so Windows does
    /// not resample, which would smear the pan the test depends on.
    /// </summary>
    public const int DefaultSampleRate = 44100;

    /// <summary>What a transient sounds like. Broadband by design - see <see cref="Build"/>.</summary>
    public enum Voice
    {
        /// <summary>A gunshot: broadband crack, fast attack, short tail, low thump.</summary>
        Gunshot,

        /// <summary>A footstep: quieter, duller, no crack. The thing you actually hunt for.</summary>
        Footstep,
    }

    /// <summary>One short sound at one time, at one side.</summary>
    public sealed record Transient
    {
        public required double AtSeconds { get; init; }
        public required double PanDb { get; init; }
        public Voice Voice { get; init; } = Voice.Gunshot;
        public double GainDb { get; init; }
    }

    public sealed record SceneSpec
    {
        public double Seconds { get; init; } = 12.0;
        public IReadOnlyList<Transient> Transients { get; init; } = Array.Empty<Transient>();

        /// <summary>Low bed of distant fire and rumble underneath.</summary>
        public bool Ambience { get; init; } = true;

        /// <summary>
        /// How far below the transients the ambience sits. Negative, because a background
        /// has to be a background.
        ///
        /// <para>
        /// Not cosmetic. The bed is low-frequency, the same band a footstep occupies, so
        /// a bed mixed at the transients' own level cannot be separated from them by band
        /// selection - they are in the same band and the bed wins on energy. At -8 dB the
        /// shots produced one onset across a whole scene against six with no bed at all.
        /// </para>
        /// </summary>
        public double AmbienceOffsetDb { get; init; } = -24.0;

        public double Peak { get; init; } = 0.7;
        public int SampleRate { get; init; } = DefaultSampleRate;
        public int Seed { get; init; } = 1;
    }

    /// <summary>
    /// Gunshots alternating left and right, at irregular intervals so the ear cannot lock
    /// onto a rhythm. The point is the edge, so both sides get equal attention and the
    /// gaps are uneven - a metronome would let you predict the next one and stop looking.
    /// </summary>
    public static SceneSpec GunshotsBothSides(double seconds = 12.0, double panDb = 6.0) => new()
    {
        Seconds = seconds,
        Ambience = false,
        Transients = AlternatingShots(seconds, panDb),
    };

    /// <summary>
    /// A distant war bed with a footstep walking from the right edge to the left.
    ///
    /// <para>
    /// The crossing is stepped rather than swept, one pan per step, because that is what
    /// a real footstep is: a series of separate sounds at separate places, not a
    /// continuous slide. A swept pan is a tone, and the overlay is not meant to be able to
    /// follow a tone.
    /// </para>
    /// </summary>
    public static SceneSpec DistantWarWithCrossingFootsteps(
        double seconds = 14.0, double fromPanDb = 7.0, double toPanDb = -7.0)
    {
        var rng = new Random(20260928);
        var list = new List<Transient>();

        // Distant fire, near-centred, so it is the bed and not a direction.
        var at = 1.0;
        while (at < seconds - 1.0)
        {
            list.Add(new Transient
            {
                AtSeconds = at,
                PanDb = (rng.NextDouble() * 2.0 - 1.0) * 1.5, // almost centred
                Voice = Voice.Gunshot,
                GainDb = -20.0,                                   // far off
            });
            at += 0.8 + 2.6 * rng.NextDouble();
        }

        // The crossing: a walk from one side to the other, at a steady human cadence.
        const double StepSeconds = 0.62;
        var started = false;
        for (var t = 1.5; t < seconds - 0.5; t += StepSeconds)
        {
            var progress = (t - 1.5) / Math.Max(0.1, seconds - 2.5);
            var pan = fromPanDb + (toPanDb - fromPanDb) * progress;

            // Two feet: alternate them so it reads as walking rather than as a metronome,
            // and put the quieter one on the trailing side, as a real step is.
            list.Add(new Transient
            {
                AtSeconds = t,
                PanDb = pan,
                Voice = Voice.Footstep,
                GainDb = started ? -4.0 : 0.0,
            });
            list.Add(new Transient
            {
                AtSeconds = t + StepSeconds * 0.45,
                PanDb = pan * 0.85,
                Voice = Voice.Footstep,
                GainDb = -4.0,
            });
            started = true;
        }

        return new SceneSpec
        {
            Seconds = seconds,
            Ambience = true,
            AmbienceOffsetDb = -24.0,
            Transients = list,
        };
    }

    private static List<Transient> AlternatingShots(double seconds, double panDb)
    {
        var rng = new Random(4242);
        var list = new List<Transient>();
        var at = 0.6;
        var right = true;

        while (at < seconds - 0.6)
        {
            list.Add(new Transient { AtSeconds = at, PanDb = right ? panDb : -panDb });
            right = !right;
            at += 0.55 + 1.5 * rng.NextDouble();
        }

        return list;
    }

    /// <summary>
    /// Renders a scene to interleaved stereo floats in -1..1, ready for
    /// <see cref="ToWavBytes"/>.
    /// </summary>
    public static float[] Render(SceneSpec spec)
    {
        var rate = spec.SampleRate <= 0 ? DefaultSampleRate : spec.SampleRate;
        var total = Math.Max(1, (int)Math.Round(spec.Seconds * rate));
        var left = new float[total];
        var right = new float[total];
        var rng = new Random(spec.Seed);

        if (spec.Ambience)
            MixAmbience(left, right, total, rate, rng, spec.AmbienceOffsetDb);

        foreach (var t in spec.Transients)
        {
            var voice = Build(t.Voice, rate, rng);
            var start = (int)(t.AtSeconds * rate);
            if (start >= total) continue;

            // Each transient is panned on its own. Rendering it into a scratch pair first
            // is what lets the pan vary within the scene; panning the summed mix could
            // only ever move everything at once.
            var n = Math.Min(voice.Length, total - start);
            var l = new float[n];
            var r = new float[n];
            Array.Copy(voice, l, n);
            Array.Copy(voice, r, n);
            ApplyPan(l, r, t.PanDb);

            var gain = (float)Math.Pow(10.0, t.GainDb / 20.0);
            for (var i = 0; i < n; i++)
            {
                left[start + i] += l[i] * gain;
                right[start + i] += r[i] * gain;
            }
        }

        Normalise(left, right, spec.Peak);
        return Interleave(left, right);
    }

    /// <summary>
    /// One transient.
    ///
    /// <para>
    /// Broadband matters more than realism. The analyser scores each frequency band and
    /// picks the winner, so a sound occupying one band would be judged by that band alone
    /// and the test would not exercise the selection it is meant to check.
    /// </para>
    /// </summary>
    private static float[] Build(Voice voice, int rate, Random rng)
    {
        var length = (int)((voice == Voice.Gunshot ? 0.42 : 0.16) * rate);
        var buf = new float[length];
        var decay = voice == Voice.Gunshot ? 26.0 : 40.0;
        var tone = voice == Voice.Gunshot ? 85.0 : 130.0;   // the low thump
        var toneAmp = voice == Voice.Gunshot ? 0.5 : 0.9;   // a footstep is mostly thump
        var attack = voice == Voice.Gunshot ? 0.0015 : 0.004;

        for (var i = 0; i < length; i++)
        {
            var t = i / (double)rate;
            var x = i / (double)length;

            var env = Math.Exp(-decay * x) * Math.Min(1.0, i / (attack * rate));
            var thump = Math.Exp(-decay * 0.35 * x) * Math.Sin(2 * Math.PI * tone * t) * toneAmp;

            buf[i] = (float)((rng.NextDouble() * 2 - 1) * env + thump);
        }

        return buf;
    }

    /// <summary>
    /// A low bed of rumble and distant fire, summed identically into both channels.
    /// Exactly centred by construction, so it cannot register as a direction.
    /// </summary>
    private static void MixAmbience(
        float[] left, float[] right, int total, int rate, Random rng, double offsetDb)
    {
        var gain = (float)Math.Pow(10.0, offsetDb / 20.0);
        var rumbleA = 2 * Math.PI * 41 / rate;
        var rumbleB = 2 * Math.PI * 57 / rate;
        var noiseLp = 0.0;

        for (var i = 0; i < total; i++)
        {
            // One-pole low pass on white noise, for wind and distant engines.
            noiseLp += 0.02 * ((rng.NextDouble() * 2 - 1) - noiseLp);

            var bed = 0.34 * Math.Sin(rumbleA * i) + 0.20 * Math.Sin(rumbleB * i) + 0.55 * noiseLp;
            var level = (float)(bed * 0.5 * gain);
            left[i] += level;
            right[i] += level;
        }

        // Distant fire, quiet and dull. Added after the bed so it is not itself scaled by
        // the bed's gain twice.
        var fireGain = (float)(Math.Pow(10.0, (offsetDb + 4.0) / 20.0));
        for (var i = 0; i < total; i++)
        {
            if (rng.NextDouble() >= 0.00025) continue;

            var len = Math.Min((int)(0.5 * rate), total - i);
            for (var k = 0; k < len; k++)
            {
                var x = k / (double)len;
                var env = Math.Exp(-7.0 * x) * Math.Min(1.0, k / (0.004 * rate));
                var v = (float)(env * (rng.NextDouble() * 2 - 1) * 0.4 * fireGain);
                left[i + k] += v;
                right[i + k] += v;
            }

            // Skip past the burst so one trigger does not fire many times over.
            i += len;
        }
    }

    /// <summary>Applies a level difference between the channels.</summary>
    private static void ApplyPan(float[] left, float[] right, double panDb)
    {
        if (Math.Abs(panDb) < 1e-9) return;

        var gain = Math.Pow(10.0, panDb / 20.0);

        // Attenuate whichever side is meant to be quieter. The first version chose the
        // channel with a ternary and then always scaled it by 1/gain, so a negative pan -
        // right meant to be quieter - boosted the right instead. The magnitude was right
        // and only the sign was wrong, which is the worst kind: a bar still appeared, on
        // the wrong edge, and only a check that reads the sign would catch it.
        if (gain >= 1.0)
        {
            var attenuate = (float)(1.0 / gain);
            for (var i = 0; i < left.Length; i++) left[i] *= attenuate;
        }
        else
        {
            var attenuate = (float)gain;
            for (var i = 0; i < right.Length; i++) right[i] *= attenuate;
        }
    }

    private static void Normalise(float[] left, float[] right, double peak)
    {
        var max = 0.0;
        for (var i = 0; i < left.Length; i++)
        {
            max = Math.Max(max, Math.Abs(left[i]));
            max = Math.Max(max, Math.Abs(right[i]));
        }

        if (max < 1e-9) return;
        var scale = (float)(peak / max);
        for (var i = 0; i < left.Length; i++)
        {
            left[i] *= scale;
            right[i] *= scale;
        }
    }

    private static float[] Interleave(float[] left, float[] right)
    {
        var outBuf = new float[left.Length * 2];
        for (var i = 0; i < left.Length; i++)
        {
            outBuf[i * 2] = left[i];
            outBuf[i * 2 + 1] = right[i];
        }
        return outBuf;
    }

    /// <summary>
    /// Writes 16-bit stereo PCM.
    ///
    /// <para>
    /// The header is hand-built, because the rest of this project hand-builds its interop
    /// and adding a package to emit a 44-byte header would be a poor trade. The sizes are
    /// known before writing, since the frame count comes from the buffer length.
    /// </para>
    /// </summary>
    public static byte[] ToWavBytes(float[] interleaved, int sampleRate = DefaultSampleRate)
    {
        const int Channels = 2, BitsPerSample = 16, HeaderBytes = 44;
        var frames = interleaved.Length / 2;
        var dataBytes = frames * Channels * BitsPerSample / 8;

        var bytes = new byte[HeaderBytes + dataBytes];
        var o = 0;

        void Ascii(string s) { foreach (var c in s) bytes[o++] = (byte)c; }
        void U32(int v) { BitConverter.GetBytes(v).CopyTo(bytes, o); o += 4; }
        void U16(ushort v) { BitConverter.GetBytes(v).CopyTo(bytes, o); o += 2; }

        Ascii("RIFF");
        U32(36 + dataBytes);
        Ascii("WAVE");
        Ascii("fmt ");
        U32(16);                                        // PCM format chunk size
        U16(1);                                         // 1 = PCM
        U16(Channels);
        U32(sampleRate);
        U32(sampleRate * Channels * BitsPerSample / 8); // byte rate
        U16((ushort)(Channels * BitsPerSample / 8));    // block align
        U16(BitsPerSample);
        Ascii("data");
        U32(dataBytes);

        for (var i = 0; i < frames; i++)
        {
            var l = (short)Math.Clamp(interleaved[i * 2] * 32767.0, -32767, 32767);
            var r = (short)Math.Clamp(interleaved[i * 2 + 1] * 32767.0, -32767, 32767);
            bytes[o++] = (byte)(l & 0xFF);
            bytes[o++] = (byte)((l >> 8) & 0xFF);
            bytes[o++] = (byte)(r & 0xFF);
            bytes[o++] = (byte)((r >> 8) & 0xFF);
        }

        return bytes;
    }
}
