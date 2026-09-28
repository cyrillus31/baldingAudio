using System.Collections.Concurrent;
using System.Diagnostics;
using BaldingAudio.App.Config;
using BaldingAudio.App.Overlay;
using BaldingAudio.Core.Audio;
using BaldingAudio.App.Audio;
using BaldingAudio.Core.Dsp;
using BaldingAudio.Core.Overlay;
using static BaldingAudio.App.Audio.NativeMethods;

namespace BaldingAudio.App;

/// <summary>
/// Wires capture to analysis to drawing, and owns the render loop.
///
/// Threading:
///   capture thread  - reads WASAPI packets, runs the analyser, enqueues events
///   UI thread       - owns the overlay window, runs the WinForms timer, draws
///
/// Analysis runs on the capture thread rather than the UI thread so a burst of audio
/// can never delay drawing, and drawing runs on the UI thread because
/// <c>UpdateLayeredWindow</c> belongs to the window that created it.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly AppConfig _config;
    private readonly ConcurrentQueue<AudioEvent> _pending = new();
    private readonly EventTracker _tracker;
    private readonly OverlayRenderer _renderer;

    private WasapiLoopbackCapture? _capture;
    private SpatialAnalyzer? _analyzer;
    private OverlayWindow? _overlay;
    private System.Windows.Forms.Timer? _timer;
    private DemoSource? _demo;

    private Stopwatch _clock = new();
    private double _lastTick;
    private double _lastHeartbeat = -1;
    private long _framesDrawn;
    private long _eventsSeen;
    private double _lastNoiseFloorDb = -120;
    private double _lastDbfs = -120;
    private string _status = "starting";
    private volatile bool _paused;

    public AppConfig Config => _config;

    public bool IsDemo { get; private set; }

    /// <summary>Raised on the UI thread with a short status string, for the tray tooltip.</summary>
    public event Action<string>? StatusChanged;

    public AppHost(AppConfig config)
    {
        _config = config;
        config.ApplyPreset();
        _tracker = new EventTracker(config.Tuning.EventHoldSeconds, config.Tuning.EventFadeSeconds);
        _renderer = new OverlayRenderer(config.Style);
    }

    public void Start(bool demo, bool holdDemo = false)
    {
        IsDemo = demo;
        _clock.Restart();

        if (demo)
        {
            // No audio device needed. Useful for checking the overlay renders, and for
            // demonstrating the layout to someone who cannot hear the app work.
            _demo = new DemoSource { HoldEverything = holdDemo };
            _status = holdDemo ? "demo mode: all cues held" : "demo mode (no capture)";
            Log.Info("starting in demo mode");
        }
        else
        {
            StartCapture();
        }

        CreateOverlay();
        StartRenderLoop();
    }

    private void StartCapture()
    {
        _capture = new WasapiLoopbackCapture();
        _capture.Log += m => Log.Info($"capture: {m}");

        try
        {
            _capture.Start(new CaptureOptions
            {
                DeviceId = _config.DeviceId,
                BufferMilliseconds = 10,
            });
        }
        catch (Exception ex)
        {
            // Without audio there is nothing to show, but the overlay and tray icon
            // should still come up so the user can read the error and fix it.
            _status = $"capture failed: {ex.Message}";
            Log.Error($"could not start capture: {ex}");
            _analyzer = null;
            return;
        }

        _analyzer = new SpatialAnalyzer(
            _capture.ChannelMask,
            _capture.ChannelCount,
            _capture.SampleRate,
            _config.Tuning,
            frameSize: 128);

        _capture.OnSamples += OnSamples;

        _status = _capture.HasReliableFrontBack
            ? $"listening: {_capture.LayoutDescription}"
            : $"listening: {_capture.LayoutDescription} (front/back is a guess)";

        if (!_capture.HasReliableFrontBack)
        {
            Log.Warn(
                "The active output device is not multichannel. Left/right will be accurate, " +
                "but front/back cannot be measured. For full directionality set Windows " +
                "Sound > Playback to a 7.1 device (see README).");
        }
    }

    private void OnSamples(ReadOnlyMemory<float> samples, int frames)
    {
        var analyzer = _analyzer;
        if (analyzer is null || _paused) return;

        // Only the capture thread calls this, so the analyser needs no lock.
        analyzer.Process(samples.Span, frames, _scratch);
        for (var i = 0; i < _scratch.Count; i++) _pending.Enqueue(_scratch[i]);
        if (_scratch.Count > 0)
        {
            _lastDbfs = _scratch[_scratch.Count - 1].Dbfs;
            Interlocked.Add(ref _eventsSeen, _scratch.Count);
        }
        _scratch.Clear();
        _spectrumExchange.Publish(analyzer.Spectrum);
    }

    private readonly List<AudioEvent> _scratch = new();
    private readonly SpectrumExchange _spectrumExchange = new();

    private void CreateOverlay()
    {
        var (x, y, w, h) = _config.Monitor switch
        {
            MonitorSelection.Foreground => ForegroundMonitorBounds(),
            _ => PrimaryMonitorBounds(),
        };

        _overlay = new OverlayWindow(x, y, w, h);
        _overlay.Create();
        Log.Info($"overlay window {w}x{h} at ({x},{y})");
    }

    private void StartRenderLoop()
    {
        var hz = Math.Clamp(_config.RenderHz, 10, 240);
        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, 1000 / hz) };
        _timer.Tick += (_, _) => Tick();
        _lastTick = 0;
        _timer.Start();
    }

    private void Tick()
    {
        var overlay = _overlay;
        if (overlay is null) return;

        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastTick, 0, 0.25);
        _lastTick = now;

        while (_pending.TryDequeue(out var e)) _tracker.Push(e);

        // Follow the live sound field before stepping the envelopes, so the lines are
        // already aimed at the right place by the time they are drawn. The snapshot is
        // taken on the capture thread, so the UI thread never sees a half-written frame.
        var spectrum = _analyzer is null ? null : _spectrumExchange.Snapshot();
        if (spectrum is not null) _tracker.Follow(spectrum, dt);
        _tracker.Tick(dt);

        // The demo source injects events directly, so it runs on the UI thread.
        _demo?.Tick(now, _tracker);

        _renderer.Render(_tracker.Visible, overlay.Buffer);
        overlay.Present();
        _framesDrawn++;

        if (_analyzer is not null) _lastNoiseFloorDb = _analyzer.NoiseFloorDb;
        RaiseStatus();
        LogHeartbeat(now);
    }

    /// <summary>
    /// Writes a one-line-per-second summary to the log.
    ///
    /// Without this there is no way to tell "the game is silent" from "capture is
    /// broken" from "the analyser is producing events the layout is throwing away" -
    /// all three look identical, which is a blank screen. The tray tooltip carries
    /// the same numbers, but nobody hovers a tray icon in a game.
    /// </summary>
    /// <summary>
    /// Whether the last heartbeat found capture healthy. Used to log a warning on the
    /// transition into failure rather than on every second of it.
    /// </summary>
    private bool _lastCaptureHealthy = true;

    private void LogHeartbeat(double now)
    {
        if (now - _lastHeartbeat < 1.0) return;
        _lastHeartbeat = now;

        var mode = IsDemo ? "demo" : (_capture?.LayoutDescription ?? "no audio");
        var captureState = CaptureState();
        var spectrum = _analyzer is null ? null : _spectrumExchange.Snapshot();
        var loudestBin = 0;
        var loudest = 0.0;
        if (spectrum is not null)
        {
            for (var i = 0; i < spectrum.Bins; i++)
                if (spectrum[i] > loudest) { loudest = spectrum[i]; loudestBin = i; }
        }

        // Only name a bearing when there is energy to name. An empty spectrum otherwise
        // reports whichever bin happens to be index 0, which prints as "-172 deg level
        // 0.000" and reads like a real measurement of a sound from behind. It is not one.
        var where = loudest > 0.0005
            ? $"{spectrum!.AzimuthOf(loudestBin),5:F0}° level {loudest,5:F3}"
            : "silent          ";

        Log.Info(
            $"{mode} | {captureState} | peak {_lastDbfs,6:F0} dBFS | floor {_lastNoiseFloorDb,6:F0} dBFS | " +
            $"events {Interlocked.Read(ref _eventsSeen)} | lines {_tracker.Visible.Count} | " +
            $"loudest {where} | {_framesDrawn} frames");

        WarnIfCaptureUnhealthy();
    }

    /// <summary>
    /// A short description of capture liveness, for the heartbeat line.
    ///
    /// This exists because the heartbeat used to say nothing about the capture thread,
    /// and the thread can die. That was the whole of open problem 1: the log showed
    /// frames climbing and events frozen, and "the log is still ticking" was read as
    /// proof the pipeline was healthy. It is not - the heartbeat is driven by the UI
    /// timer and would tick just as happily with no audio behind it. The line has to
    /// name the state of the thread that gets the audio, or it proves nothing.
    /// </summary>
    private string CaptureState()
    {
        var capture = _capture;
        if (IsDemo) return "capture demo   ";
        if (capture is null) return "capture none    ";

        var restarts = capture.RestartCount;
        var suffix = restarts > 0 ? $" restarts {restarts}" : "";

        if (capture.LastFailure is { } failure) return $"capture FAILED {failure}";
        if (!capture.IsRunning) return "capture stopped " + suffix;
        if (!capture.IsHealthy) return $"capture STALLED{suffix}";
        return $"capture ok{suffix}";
    }

    /// <summary>
    /// Warns once when capture goes bad, and once when it comes back.
    ///
    /// Rate-limited because the heartbeat runs every second and a dead capture is
    /// otherwise perfectly stable: without this it would write the same warning 3600
    /// times an hour and bury the lines that matter.
    /// </summary>
    private void WarnIfCaptureUnhealthy()
    {
        var healthy = IsDemo || (_capture is { IsRunning: true, IsHealthy: true, LastFailure: null });
        if (healthy == _lastCaptureHealthy) return;

        _lastCaptureHealthy = healthy;
        if (healthy)
        {
            if (_capture is not null)
                Log.Info($"capture healthy again after {_capture.RestartCount} restart(s)");
            return;
        }

        Log.Warn(
            $"capture is not healthy: {CaptureState()}. " +
            "The overlay will stay blank until audio flows; it will recover on its own " +
            "if the endpoint can be re-opened.");
    }

    private void RaiseStatus()
    {
        var mode = IsDemo ? "demo" : (_capture?.LayoutDescription ?? "no audio");
        var front = _capture?.HasReliableFrontBack ?? true;
        StatusChanged?.Invoke(
            $"baldingAudio | {mode}{(front ? "" : " (front/back uncertain)")} | " +
            $"events {_eventsSeen} | peak {_lastDbfs:F0} dBFS | floor {_lastNoiseFloorDb:F0} dBFS" +
            (_paused ? " | PAUSED" : ""));
    }

    public void TogglePaused()
    {
        _paused = !_paused;

        // Freeze, never clear. Clearing made a pause pixel-for-pixel identical to a
        // crash: the user reported the overlay vanishing mid-game with no way to tell a
        // toggle from a fault. Holding the last frame keeps it visibly there.
        if (_paused) _tracker.Freeze(); else _tracker.Thaw();

        Log.Info(_paused ? "paused (display held, capture still running)" : "resumed");
    }

    public void TogglePreset()
    {
        _config.Preset = _config.Preset == OverlayLayoutPreset.Edge
            ? OverlayLayoutPreset.Compact
            : OverlayLayoutPreset.Edge;
        _config.ApplyPreset();
        Log.Info($"layout preset: {_config.Preset}");
    }

    public void CycleDevice()
    {
        if (IsDemo)
        {
            Log.Info("demo mode has no device to change");
            return;
        }
        Log.Info("device switching is not implemented yet; edit deviceId in config.json");
    }

    public void SelfTest()
    {
        var t = new Thread(() =>
        {
            var results = Core.Diagnostics.SelfTest.RunAll(Log.Info);
            var ok = Core.Diagnostics.SelfTest.AllPassed(results);
            StatusChanged?.Invoke(ok ? "self-test passed" : "self-test FAILED - see log");
        })
        { IsBackground = true, Name = "baldingAudio.selftest" };
        t.Start();
    }

    public long FramesDrawn => Interlocked.Read(ref _framesDrawn);
    public long EventsSeen => Interlocked.Read(ref _eventsSeen);

    public void Dispose()
    {
        _timer?.Stop();
        _timer?.Dispose();
        if (_capture is not null)
        {
            _capture.OnSamples -= OnSamples;
            _capture.Dispose();
        }
        _overlay?.Dispose();
    }
}
