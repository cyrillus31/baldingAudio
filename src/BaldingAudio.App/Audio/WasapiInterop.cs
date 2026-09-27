using System.Runtime.InteropServices;

using BaldingAudio.Core.Audio;

namespace BaldingAudio.App.Audio;

// --- WASAPI COM interop ------------------------------------------------------
// Declarations only. No behaviour here touches any process but our own.

[Flags]
public enum EDataFlow : uint
{
    eRender = 0,
    eCapture = 1,
    eAll = 2,
}

[Flags]
public enum ERole : uint
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2,
}

// Pack = 2 is required: the Windows SDK declares WAVEFORMATEX inside #pragma pack(2),
// making it 18 bytes. C#'s default Sequential layout pads it to 20, which shifts every
// field after it. Only used for reporting now - the mix format is read by hand - but
// getting it wrong is how the channel count ends up as 32843.
[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;

    public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
    public const ushort WAVE_FORMAT_IEEE_FLOAT = 0x0003;
}

/// <summary>
/// The endpoint's mix format, as 44 opaque bytes.
///
/// This is declared flat and read out of unmanaged memory by hand, on purpose. Both of
/// the natural-looking declarations are wrong and fail quietly:
///
///  * Nesting a packed <see cref="WAVEFORMATEX"/> inside another struct makes the CLR's
///    COM interop disagree with <see cref="Marshal.SizeOf(Type)"/>, and
///    <c>GetMixFormat</c> then returns garbage — channel counts like 32843.
///  * Letting the CLR marshal the struct directly fails the same way, silently.
///
/// So <c>GetMixFormat</c> hands back an <see cref="IntPtr"/> and the 44 bytes are read
/// with <see cref="Read"/>. Everything else in this file is an ordinary ComImport
/// interface and works normally; this one type is the exception.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct WAVEFORMATEXTENSIBLE
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;

    /// <summary>WAVEFORMATEX.cbSize. 22 when the format is extensible.</summary>
    public ushort cbSizeFormat;

    public ushort wValidBitsPerSample;
    public uint dwChannelMask;
    public Guid SubFormat;

    /// <summary>WAVEFORMATEXTENSIBLE.cbSize. The audio engine requires this to be set.</summary>
    public ushort cbSize;

    /// <summary>
    /// The fields above pack out to 42 bytes, but the native struct is 44 because its
    /// alignment is 4. This one pad closes the gap - two would give 46 and fail the
    /// size assertion in WasapiLoopbackCapture.
    /// </summary>
    public ushort pad;

    public const ushort KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = 0x0003;
    public const ushort KSDATAFORMAT_SUBTYPE_PCM = 0x0001;

    /// <summary>Bytes this struct occupies natively. Asserted at startup.</summary>
    public const int NativeSize = 44;

    /// <summary>Reads the 44 bytes of a native WAVEFORMATEXTENSIBLE.</summary>
    public static WAVEFORMATEXTENSIBLE Read(IntPtr p)
    {
        if (p == IntPtr.Zero) throw new InvalidOperationException("mix format pointer was null");
        return new WAVEFORMATEXTENSIBLE
        {
            wFormatTag = (ushort)Marshal.ReadInt16(p, 0),
            nChannels = (ushort)Marshal.ReadInt16(p, 2),
            nSamplesPerSec = (uint)Marshal.ReadInt32(p, 4),
            nAvgBytesPerSec = (uint)Marshal.ReadInt32(p, 8),
            nBlockAlign = (ushort)Marshal.ReadInt16(p, 12),
            wBitsPerSample = (ushort)Marshal.ReadInt16(p, 14),
            cbSizeFormat = (ushort)Marshal.ReadInt16(p, 16),
            wValidBitsPerSample = (ushort)Marshal.ReadInt16(p, 18),
            dwChannelMask = (uint)Marshal.ReadInt32(p, 20),
            SubFormat = Marshal.ReadInt32(p, 24) == 3 && Marshal.ReadInt32(p, 28) == 0x00100000
                ? new Guid(3, 0, 0x10, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71)
                : Marshal.ReadInt32(p, 24) == 1
                    ? new Guid(1, 0, 0x10, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71)
                    : Guid.Empty,
            cbSize = (ushort)Marshal.ReadInt16(p, 40),
        };
    }

    public WAVEFORMATEX Format => new()
    {
        wFormatTag = wFormatTag,
        nChannels = nChannels,
        nSamplesPerSec = nSamplesPerSec,
        nAvgBytesPerSec = nAvgBytesPerSec,
        nBlockAlign = nBlockAlign,
        wBitsPerSample = wBitsPerSample,
        cbSize = cbSizeFormat,
    };

    public int BytesPerFrame => nBlockAlign;
    public int SampleRate => (int)nSamplesPerSec;
    public int BitsPerSample => wBitsPerSample;

    public bool IsFloat => wFormatTag == WAVEFORMATEX.WAVE_FORMAT_IEEE_FLOAT
                           || SubFormat == new Guid(3, 0, 0x10, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71);

    public override string ToString()
    {
        var layout = ChannelLayout.Describe(dwChannelMask);
        var kind = IsFloat ? "float" : "pcm";
        return $"{layout}, {nSamplesPerSec} Hz, {BitsPerSample}-bit {kind}";
    }
}

internal enum AudioClientShareMode : uint
{
    Shared = 0,
    Exclusive = 1,
}

/// <summary>
/// Values from audioclient.h. Checked against the header rather than written from
/// memory: Loopback was originally 0x00010000 here, which is actually CROSSPROCESS, and
/// Initialize rejected it with E_INVALIDARG. Neighbouring flags are close enough in
/// shape to be easy to get wrong, so do not "fix" one of these from memory either.
/// </summary>
internal enum AudioClientStreamFlags : uint
{
    None = 0,

    /// <summary>AUDCLNT_STREAMFLAGS_CROSSPROCESS. Not loopback - do not confuse them.</summary>
    CrossProcess = 0x0001_0000,

    /// <summary>AUDCLNT_STREAMFLAGS_LOOPBACK. Capture the endpoint mix instead of playing.</summary>
    Loopback = 0x0002_0000,

    /// <summary>AUDCLNT_STREAMFLAGS_EVENTCALLBACK. Requires SetEventHandle before Start.</summary>
    EventCallback = 0x0004_0000,

    /// <summary>AUDCLNT_STREAMFLAGS_NOPERSIST.</summary>
    NoPersist = 0x0008_0000,
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out object collection);
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    int RegisterEndpointNotificationCallback(IntPtr client);
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal interface IMMDevice
{
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    int OpenPropertyStore(int stgmAccess, out IntPtr properties);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    int GetState(out int state);
}

/// <summary>
/// IID_IAudioClient. Verified against the Windows SDK audioclient.h rather than written
/// from memory: the first version of this file used ...C82FEC552AED, which is not a real
/// IID. A wrong IID makes IMMDevice::Activate fail with E_NOINTERFACE, and the CLR then
/// reports that as InvalidCastException - which is how a typo in a GUID cost an evening.
/// </summary>
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
internal interface IAudioClient
{
    int Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, IntPtr sessionGuid);
    int GetBufferSize(out uint numBufferFrames);
    int GetStreamLatency(out long latency);
    int GetCurrentPadding(out uint numPaddingFrames);
    int IsFormatSupported(AudioClientShareMode shareMode, IntPtr format, out int closest);
    int GetMixFormat(out IntPtr format);
    int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
    int Start();
    int Stop();
    int Reset();
    int SetEventHandle(IntPtr eventHandle);
    int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

/// <summary>IID_IAudioCaptureClient, also verified against the SDK.</summary>
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
internal interface IAudioCaptureClient
{
    int GetBuffer(out IntPtr data, out uint numFramesToRead, out AudioClientBufferFlags flags, out long devicePosition, out long qpcPosition);
    int ReleaseBuffer(uint numFramesRead);
    int GetNextPacketSize(out uint numFramesInNextPacket);
}

[Flags]
public enum AudioClientBufferFlags : uint
{
    None = 0,
    DataDiscontinuity = 1,
    TimestampError = 2,
    Silent = 4,
}

internal static class MMDevice
{
    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT, as a GUID.</summary>
    internal static readonly Guid WaveFormatSubtypes_IeeeFloat =
        new(0x00000003, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    /// <summary>CLSID_MMDeviceEnumerator, from mmdeviceapi.h.</summary>
    internal static readonly Guid ClsidDeviceEnumerator =
        new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    internal const int CLSCTX_ALL = 23;
    internal const int AUDCLNT_S_BUFFER_EMPTY = unchecked((int)0x08890001);
    internal const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890008);
    internal const int AUDCLNT_E_UNSUPPORTED_FORMAT = unchecked((int)0x88890014);
    internal const int AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED = unchecked((int)0x8889001A);

    public static IMMDeviceEnumerator CreateEnumerator()
    {
        var clsid = ClsidDeviceEnumerator;
        var iid = typeof(IMMDeviceEnumerator).GUID;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out var obj);
        Marshal.ThrowExceptionForHR(hr);
        return (IMMDeviceEnumerator)obj;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext, ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

    public static IMMDevice? GetDevice(string? id)
    {
        var enumerator = CreateEnumerator();
        if (string.IsNullOrEmpty(id))
        {
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out var d);
            return d;
        }
        try
        {
            enumerator.GetDevice(id, out var dev);
            return dev;
        }
        catch
        {
            return null;
        }
    }
}
