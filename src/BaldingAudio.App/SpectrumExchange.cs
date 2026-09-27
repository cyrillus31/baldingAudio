using BaldingAudio.Core.Audio;

namespace BaldingAudio.App;

/// <summary>
/// Hands the direction spectrum from the capture thread to the UI thread.
///
/// The analyser rewrites its spectrum in place every 2.7 ms frame, so the UI thread
/// cannot simply read it: it would see a half-updated mixture of two frames. This
/// double-buffers it - the capture thread fills the spare copy and publishes it, the
/// UI thread takes a stable copy. One frame of latency is invisible at 60 Hz.
/// </summary>
internal sealed class SpectrumExchange
{
    private readonly object _gate = new();
    private readonly double[] _pending = new double[24];
    private double[]? _published;
    private double[] _taken = new double[24];

    public int Bins => _pending.Length;

    /// <summary>Called on the capture thread once per analysed frame.</summary>
    public void Publish(DirectionSpectrum spectrum)
    {
        lock (_gate)
        {
            for (var i = 0; i < _pending.Length; i++) _pending[i] = spectrum[i];
            // Swap rather than copy: the array the capture thread is filling next frame
            // must not be the one the UI thread is reading.
            (_published, _taken) = (_taken, _published ?? _pending);
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
