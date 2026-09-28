using System.Runtime.InteropServices;

using BaldingAudio.Core.Audio;

namespace BaldingAudio.App.Audio;

/// <summary>Configuration for a loopback capture.</summary>
public sealed class CaptureOptions
{
    /// <summary>Endpoint id, or null/empty for the default render device.</summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Requested engine buffer in milliseconds. Smaller is more responsive but can
    /// glitch on some drivers. 10 ms is a good default; the displayed cue is
    /// dominated by the driver's own latency anyway.
    /// </summary>
    public int BufferMilliseconds { get; set; } = 10;

    /// <summary>When false, the stream is written to the speakers as well as read.</summary>
    public bool PassThrough { get; set; } = true;
}

/// <summary>Captures the audio a render endpoint is playing, via WASAPI loopback.
///
/// This is the only data source the app has. It is the documented, public way for
/// any application to observe system audio on Windows, and it is what screen
/// recorders, music visualisers and VoIP tools all use. It reads the endpoint's
/// output mix - it does not touch any application that produced the audio.
/// </summary>
public sealed class WasapiLoopbackCapture : IDisposable
{
    private readonly object _gate = new();
    private IMMDevice? _device;
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private Thread? _thread;
    private volatile bool _running;

    private int _sampleRate;
    private int _channelCount;
    private uint _channelMask;
    private long _devicePeriod;
    private int _bytesPerSample;
    private int _bytesPerFrame;
    private float[]? _deinterleaveScratch;

    public bool IsRunning => _running;

    public int SampleRate => _sampleRate;
    public int ChannelCount => _channelCount;
    public uint ChannelMask => _channelMask;
    public string LayoutDescription => ChannelLayout.Describe(_channelMask);
    public bool HasReliableFrontBack => ChannelLayout.SupportsFrontBack(_channelMask);
    public long DevicePeriodFrames => _devicePeriod;

    public string? DeviceName { get; private set; }

    public event Action<string>? Log;

    /// <summary>Raised for each block of interleaved samples. Called on the capture thread.</summary>
    public event Action<ReadOnlyMemory<float>, int>? OnSamples;

    /// <summary>Options the capture was last started with, so it can restart itself.</summary>
    private CaptureOptions? _options;

    /// <summary>Number of times the stream has been re-opened after a failure.</summary>
    public int RestartCount { get; private set; }

    /// <summary>Describes the last failure, or null while capture is healthy.</summary>
    public string? LastFailure { get; private set; }

    public void Start(CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        lock (_gate)
        {
            if (_running) return;
            Open(options);
        }

        LaunchThread();
    }

    /// <summary>
    /// Opens the endpoint, initialises the client and starts the stream. Assumes
    /// <see cref="_gate"/> is held and not already running.
    /// </summary>
    private void Open(CaptureOptions options)
    {
        ReleaseCom();

        _device = MMDevice.GetDevice(options.DeviceId)
            ?? throw new InvalidOperationException("Audio endpoint not found.");

        try
        {
            _device.GetId(out _);
        }
        catch
        {
            // Not fatal.
        }

        var iid = typeof(IAudioClient).GUID;
        var activateHr = _device.Activate(ref iid, MMDevice.CLSCTX_ALL, IntPtr.Zero, out var clientObj);
        if (activateHr < 0 || clientObj is null)
            throw new InvalidOperationException(
                $"Could not activate IAudioClient (hr=0x{activateHr:X8}). " +
                "If this is E_NOINTERFACE the IID in WasapiInterop.cs is wrong.");
        _client = (IAudioClient)clientObj;

        // A wrong struct layout yields absurd channel counts rather than an error,
        // so check the size before trusting anything read through it.
        if (Marshal.SizeOf(typeof(WAVEFORMATEXTENSIBLE)) != WAVEFORMATEXTENSIBLE.NativeSize)
            throw new InvalidOperationException(
                $"WAVEFORMATEXTENSIBLE is {Marshal.SizeOf(typeof(WAVEFORMATEXTENSIBLE))} bytes, " +
                $"expected {WAVEFORMATEXTENSIBLE.NativeSize}. The interop struct is wrong.");

        // Read the mix format out of unmanaged memory by hand. Letting the CLR
        // marshal WAVEFORMATEXTENSIBLE silently yields garbage - see the type.
        var mixHr = _client.GetMixFormat(out var mixPtr);
        if (mixHr < 0) Marshal.ThrowExceptionForHR(mixHr);
        var mix = WAVEFORMATEXTENSIBLE.Read(mixPtr);

        if (mix.nChannels is < 1 or > 8 || mix.nSamplesPerSec < 8000)
            throw new InvalidOperationException(
                $"Mix format read back as nonsense ({mix.nChannels} ch, {mix.nSamplesPerSec} Hz). " +
                "The interop struct layout is wrong.");

        _sampleRate = mix.SampleRate;
        _channelCount = mix.nChannels;
        _channelMask = mix.dwChannelMask == 0 ? SpeakerPosition.Stereo : mix.dwChannelMask;
        _bytesPerSample = mix.BitsPerSample / 8;
        _bytesPerFrame = mix.BytesPerFrame;

        if (!mix.IsFloat && _bytesPerSample != 2)
            throw new NotSupportedException(
                $"Unsupported mix format: {mix}. Only 16-bit PCM and float are handled.");

        _client.GetDevicePeriod(out var defPeriod, out var minPeriod);

        // GetDevicePeriod reports 100-nanosecond units, not frames. 101587 is a
        // perfectly normal answer and means 10.16 ms.
        var periodMs = defPeriod / 10_000.0;
        _devicePeriod = Math.Max(64, (int)(mix.SampleRate * periodMs / 1000.0));

        var bufferTicks = (long)options.BufferMilliseconds * 10_000;

        // Initialise with the endpoint's own mix format rather than a format we would
        // prefer. Asking a stereo-configured device for 7.1 fails with
        // AUDCLNT_E_DEVICE_INVALIDATED (0x88890004); asking for what it already does
        // always works. The device's channel count is also the only honest source of
        // how many channels we can actually measure.
        //
        // Polling, not event-driven: WASAPI needs SetEventHandle before Start() when
        // AUDCLNT_STREAMFLAGS_EVENTCALLBACK is requested, and we have no latency need
        // for it - analysis runs on a 128-sample frame regardless.
        var initHr = _client.Initialize(
            AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback,
            bufferTicks, 0, mixPtr, IntPtr.Zero);
        if (initHr < 0)
        {
            Marshal.ThrowExceptionForHR(initHr);
        }

        var captureGuid = typeof(IAudioCaptureClient).GUID;
        var serviceHr = _client.GetService(ref captureGuid, out var captureObj);
        if (serviceHr < 0 || captureObj is null)
            throw new InvalidOperationException(
                $"Could not get IAudioCaptureClient (hr=0x{serviceHr:X8}).");
        _capture = (IAudioCaptureClient)captureObj;

        _deinterleaveScratch = new float[4096 * _channelCount];

        var startHr = _client.Start();
        if (startHr < 0) Marshal.ThrowExceptionForHR(startHr);

        _running = true;
    }

    private void LaunchThread()
    {
        _thread = new Thread(CaptureLoop)
        {
            Name = "baldingAudio.capture",
            IsBackground = true,
            // Above normal priority keeps the analysis off the critical path of a
            // 240 Hz game frame without competing with the audio engine itself.
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();

        Log?.Invoke($"capture started: {LayoutDescription}, {_sampleRate} Hz, device period {_devicePeriod} frames");
    }

    private void ReleaseCom()
    {
        try { _client?.Stop(); } catch { /* device may be gone */ }
        if (_capture is not null) { try { Marshal.ReleaseComObject(_capture); } catch { } _capture = null; }
        if (_client is not null) { try { Marshal.ReleaseComObject(_client); } catch { } _client = null; }
        if (_device is not null) { try { Marshal.ReleaseComObject(_device); } catch { } _device = null; }
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;

        lock (_gate)
        {
            ReleaseCom();
        }
    }

    /// <summary>Longest gap between packets before the stream counts as stalled.</summary>
    private const double StallSeconds = 5.0;

    private long _lastPacketTicks;

    /// <summary>True while a capture thread is alive and the endpoint is not stalled.</summary>
    public bool IsHealthy => _running && Environment.TickCount64 - _lastPacketTicks < (long)(StallSeconds * 1000);

    private void CaptureLoop()
    {
        _lastPacketTicks = Environment.TickCount64;

        while (_running)
        {
            var waitMs = Math.Clamp((int)(_devicePeriod * 1000.0 / Math.Max(1, _sampleRate)), 2, 50);

            string? reason = null;
            try
            {
                Drain(waitMs);

                // No exception, but also no audio. A driver can leave the stream
                // open and simply stop delivering, and that is the same dead overlay
                // as a thrown error, so it is detected the same way.
                if (Environment.TickCount64 - _lastPacketTicks >= (long)(StallSeconds * 1000))
                    reason = $"no packets for {StallSeconds:F0}s";
            }
            catch (Exception ex)
            {
                if (!IsRecoverableFailure(ex.HResult))
                {
                    LastFailure = Describe(ex);
                    _running = false;
                    Log?.Invoke($"capture stopped: {Describe(ex)}");
                    return;
                }
                reason = Describe(ex);
            }

            // This branch is the fix. The loop used to break on any exception and
            // nothing restarted it, so one invalidated endpoint left the process alive
            // and the render loop drawing 60 frames a second with no audio behind it.
            // From the user's side that is indistinguishable from a crashed app, and it
            // is why "the overlay disappears" went undiagnosed: the heartbeat keeps
            // ticking, because it runs on the UI thread and cannot see this thread die.
            if (!TryReopen(reason)) return;
        }
    }

    /// <summary>
    /// Re-opens the endpoint after a recoverable failure or a stall.
    /// Returns false only when capture should stop entirely.
    /// </summary>
    private bool TryReopen(string? reason)
    {
        var options = _options;
        if (options is null || !_running) return false;

        // Back off, so an endpoint that is genuinely gone does not spin the CPU
        // retrying it. 100 ms doubling to a 2 s ceiling; reset once packets flow again.
        var delayMs = Math.Min(2000, 100 << Math.Min(RestartCount, 5));
        Thread.Sleep(delayMs);
        if (!_running) return false;

        try
        {
            lock (_gate)
            {
                if (!_running) return false;
                _running = false;
                Open(options);
            }
        }
        catch (Exception ex)
        {
            LastFailure = Describe(ex);
            Log?.Invoke($"capture re-open failed ({Describe(ex)}); retrying in {delayMs} ms");
            return true;
        }

        RestartCount++;
        LastFailure = null;
        _lastPacketTicks = Environment.TickCount64;
        Log?.Invoke(
            $"capture re-opened after {reason} (attempt {RestartCount}): " +
            $"{LayoutDescription}, {_sampleRate} Hz");
        return true;
    }

    /// <summary>
    /// Whether a failure HRESULT is worth retrying.
    ///
    /// Invalidation is the important one: the endpoint was reconfigured or removed,
    /// and re-opening it is not merely a retry but the only way to recover. The
    /// rest are transient engine conditions. Anything not listed here - an
    /// unsupported format, exclusive mode refused - needs a different endpoint or a
    /// different format, so retrying it forever would just spin.
    ///
    /// Takes the HRESULT rather than the message because the message WASAPI produces
    /// is a bare "0x88890004": unreadable in a log, and string-matching on it is how
    /// the previous version of this code ended up unable to recognise the one error
    /// that actually happened.
    /// </summary>
    /// <summary>
    /// The recoverable set, as a lookup rather than a <c>switch</c>.
    ///
    /// The HRESULTs are <c>static readonly</c> fields rather than <c>const</c> because
    /// the SDK writes them as macro expansions. C# will not use a non-const as a
    /// switch pattern, which is why this is a set and not the obvious
    /// <c>hr switch { MMDevice.AUDCLNT_E_DEVICE_INVALIDATED =&gt; true, ... }</c>.
    /// InteropSelfCheck reads these same fields and asserts the classification, so a
    /// wrong HRESULT here fails the self-test rather than silently disabling recovery.
    /// </summary>
    private static readonly HashSet<int> RecoverableFailures = new()
    {
        MMDevice.AUDCLNT_E_DEVICE_INVALIDATED,
        MMDevice.AUDCLNT_E_RESOURCES_INVALIDATED,
        MMDevice.AUDCLNT_E_SERVICE_NOT_RUNNING,
        MMDevice.AUDCLNT_E_BUFFER_ERROR,
        MMDevice.AUDCLNT_E_OUT_OF_ORDER,
    };

    internal static bool IsRecoverableFailure(int hr) => RecoverableFailures.Contains(hr);

    private static string Describe(Exception ex)
        => $"{MMDevice.DescribeHresult(ex.HResult)} (0x{(uint)ex.HResult:X8}): {ex.Message}";

    private void Drain(int waitMs)
    {
        var client = _client;
        var capture = _capture;
        if (client is null || capture is null) return;

        uint available;
        var hr = capture.GetNextPacketSize(out available);
        if (hr == MMDevice.AUDCLNT_S_BUFFER_EMPTY || available == 0)
        {
            Thread.Sleep(waitMs);
            return;
        }

        while (available > 0)
        {
            capture.GetBuffer(out var data, out var frames, out var flags, out _, out _);
            if (frames == 0) break;

            if (flags.HasFlag(AudioClientBufferFlags.Silent) || data == IntPtr.Zero)
            {
                Array.Clear(_deinterleaveScratch!, 0, (int)frames * _channelCount);
            }
            else
            {
                Deinterleave(data, (int)frames);
            }

            OnSamples?.Invoke(_deinterleaveScratch!.AsMemory(0, (int)frames * _channelCount), (int)frames);

            capture.ReleaseBuffer(frames);
            if (frames < 1024) break; // avoid spinning when the driver trickles
        }
    }

    /// <summary>
    /// Converts the driver's interleaved buffer to planar-per-channel float and packs
    /// it back to interleaved float. WASAPI loopback delivers the mix in the endpoint's
    /// own format, which is usually 32-bit float but is not guaranteed.
    /// </summary>
    private void Deinterleave(IntPtr data, int frames)
    {
        var scratch = _deinterleaveScratch!;
        var total = frames * _channelCount;
        if (scratch.Length < total) Array.Resize(ref scratch, total);
        _deinterleaveScratch = scratch;

        if (_bytesPerSample == 4)
        {
            // The mix format is IEEE float, so the buffer is already float; copy it.
            Marshal.Copy(data, scratch, 0, total);
            return;
        }

        // 16-bit PCM.
        var raw = new short[total];
        Marshal.Copy(data, raw, 0, total);
        const float scale = 1f / 32768f;
        for (var i = 0; i < total; i++) scratch[i] = raw[i] * scale;
    }

    public void Dispose() => Stop();
}
