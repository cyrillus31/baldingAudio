using System.Runtime.InteropServices;

namespace BaldingAudio.Core.Audio;

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

    public void Start(CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            if (_running) return;
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
                throw new InvalidOperationException($"Could not activate the audio client (hr=0x{activateHr:X8}).");

            _client = (IAudioClient)clientObj;

            var mixHr = _client.GetMixFormat(out var mix);
            if (mixHr < 0) Marshal.ThrowExceptionForHR(mixHr);

            _sampleRate = mix.SampleRate;
            _channelCount = mix.Format.nChannels;
            _channelMask = mix.dwChannelMask == 0 ? SpeakerPosition.Stereo : mix.dwChannelMask;
            _bytesPerSample = mix.BitsPerSample / 8;
            _bytesPerFrame = mix.BytesPerFrame;

            var isFloat = mix.Format.wFormatTag == WAVEFORMATEX.WAVE_FORMAT_IEEE_FLOAT
                          || mix.SubFormat == MMDevice.WaveFormatSubtypes_IeeeFloat;
            if (!isFloat && _bytesPerSample != 2)
                throw new NotSupportedException(
                    $"Unsupported mix format: {mix}. Only 16-bit PCM and float are handled.");

            _devicePeriod = _client.GetDevicePeriod(out var def, out var min);
            if (_devicePeriod <= 0) _devicePeriod = def > 0 ? def : min > 0 ? min : 512;

            var bufferTicks = (long)options.BufferMilliseconds * 10_000;
            var flags = AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback;

            var initHr = _client.Initialize(AudioClientShareMode.Shared, flags, bufferTicks, 0, ref mix, IntPtr.Zero);
            if (initHr < 0)
            {
                // Some drivers reject an event-driven loopback; fall back to polling.
                initHr = _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, bufferTicks, 0, ref mix, IntPtr.Zero);
            }
            if (initHr < 0) Marshal.ThrowExceptionForHR(initHr);

            var captureGuid = typeof(IAudioCaptureClient).GUID;
            _client.GetService(ref captureGuid, out var captureObj);
            _capture = (IAudioCaptureClient)captureObj;

            _deinterleaveScratch = new float[4096 * _channelCount];

            var startHr = _client.Start();
            if (startHr < 0) Marshal.ThrowExceptionForHR(startHr);

            _running = true;
        }

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

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        _thread = null;

        lock (_gate)
        {
            try { _client?.Stop(); } catch { /* device may be gone */ }
            if (_capture is not null) Marshal.ReleaseComObject(_capture);
            if (_client is not null) Marshal.ReleaseComObject(_client);
            if (_device is not null) Marshal.ReleaseComObject(_device);
            _capture = null; _client = null; _device = null;
        }
    }

    private void CaptureLoop()
    {
        var waitMs = Math.Clamp((int)(_devicePeriod * 1000.0 / Math.Max(1, _sampleRate)), 2, 50);

        while (_running)
        {
            try
            {
                Drain(waitMs);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"capture loop error: {ex.Message}");
                break;
            }
        }
    }

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
