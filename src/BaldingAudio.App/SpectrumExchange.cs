using BaldingAudio.Core.Audio;

namespace BaldingAudio.App;

/// <summary>
/// Hands the direction spectrum from the capture thread to the UI thread.
///
/// The analyser rewrites its spectrum in place every 2.7 ms frame, so the UI thread
/// cannot simply read it: it would see a half-updated mixture of two frames. This
/// double-buffers it - the capture thread fills the buffer the UI is not reading and
/// then publishes it by swapping the two references. One frame of latency is invisible
/// at 60 Hz.
///
/// <para>
/// The exchange used to swap in the wrong order. It filled <c>_pending</c> and then did
/// <c>(_published, _taken) = (_taken, _published ?? _pending)</c>, which published
/// <c>_taken</c> - the buffer written <i>two</i> frames ago, and a freshly zero-filled
/// one on the very first frame. Since the analyser zeroes its spectrum on any frame with
/// no measurable energy, and the UI samples at 60 Hz against an analyser running at
/// ~370 frames a second, the UI kept catching those all-zero frames and reported the
/// field as silent: 69.5% of heartbeats logged <c>loudest silent</c> while the peak level
/// on the same line read a healthy -28 dBFS and events were still being emitted.
///
/// So the overlay drew far fewer lines than the audio warranted, and the log pointed at
/// a broken analyser rather than a broken handoff. The display floor was never the
/// problem.
/// </para>
/// </summary>
internal sealed class SpectrumExchange
{
    /// <summary>
    /// Bin count. Fixed rather than taken from a published spectrum, because the UI
    /// thread asks for <see cref="Bins"/> before anything has been published.
    /// </summary>
    public const int BinCount = 24;

    private readonly object _gate = new();

    /// <summary>The frame the UI thread reads. Written by the capture thread.</summary>
    private double[]? _published;

    /// <summary>The frame the capture thread fills. Never the one the UI is reading.</summary>
    private double[] _write = new double[BinCount];

    public int Bins => BinCount;

    /// <summary>Called on the capture thread once per analysed frame.</summary>
    public void Publish(DirectionSpectrum spectrum)
    {
        lock (_gate)
        {
            for (var i = 0; i < BinCount; i++) _write[i] = spectrum[i];

            // Publish the buffer just written. The one it replaces becomes the spare the
            // next frame fills, so the two threads never touch the same array.
            //
            // Getting this backwards is what made every other frame arrive empty.
            (_published, _write) = (_write, _published ?? new double[BinCount]);
        }
    }

    /// <summary>Called on the UI thread. Returns null until the first frame arrives.</summary>
    public DirectionSpectrum? Snapshot()
    {
        lock (_gate)
        {
            if (_published is null) return null;
            var view = new DirectionSpectrum(_published.Length);
            for (var i = 0; i < _published.Length; i++) view.SetBin(i, _published[i]);
            return view;
        }
    }
}
