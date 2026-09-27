using System.Runtime.InteropServices;

namespace BaldingAudio.Core.Audio;

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

[StructLayout(LayoutKind.Sequential)]
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

[StructLayout(LayoutKind.Sequential)]
public struct WAVEFORMATEXTENSIBLE
{
    public WAVEFORMATEX Format;

    /// <summary>Union: validBitsPerSample, channelMask, subformat GUID.</summary>
    public ushort wValidBitsPerSample;

    public uint dwChannelMask;
    public Guid SubFormat;

    public const ushort KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = 0x0003;
    public const ushort KSDATAFORMAT_SUBTYPE_PCM = 0x0001;

    public int BytesPerFrame => Format.nBlockAlign;
    public int SampleRate => (int)Format.nSamplesPerSec;
    public int BitsPerSample => Format.wBitsPerSample;

    public override string ToString()
    {
        var layout = ChannelLayout.Describe(dwChannelMask);
        var kind = Format.wFormatTag == WAVEFORMATEX.WAVE_FORMAT_IEEE_FLOAT ? "float" : "pcm";
        return $"{layout}, {Format.nSamplesPerSec} Hz, {BitsPerSample}-bit {kind}";
    }
}

internal enum AudioClientShareMode : uint
{
    Shared = 0,
    Exclusive = 1,
}

internal enum AudioClientStreamFlags : uint
{
    None = 0,
    Loopback = 0x0001_0000,
    EventCallback = 0x0004_0000,
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

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1CB9AD4C-DBFA-4C32-B178-C82FEC552AED")]
internal interface IAudioClient
{
    int Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags, long hnsBufferDuration, long hnsPeriodicity, ref WAVEFORMATEXTENSIBLE format, IntPtr sessionGuid);
    int GetBufferSize(out uint numBufferFrames);
    int GetStreamLatency(out long latency);
    int GetCurrentPadding(out uint numPaddingFrames);
    int IsFormatSupported(AudioClientShareMode shareMode, ref WAVEFORMATEXTENSIBLE format, out int closest);
    int GetMixFormat(out WAVEFORMATEXTENSIBLE format);
    int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
    int Start();
    int Stop();
    int Reset();
    int SetEventHandle(IntPtr eventHandle);
    int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C294ADCE-4DE9-4493-9971-9FD8400C3FCD")]
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

    internal const int CLSCTX_ALL = 23;
    internal const int AUDCLNT_S_BUFFER_EMPTY = unchecked((int)0x08890001);
    internal const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890008);

    public static IMMDeviceEnumerator CreateEnumerator()
    {
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
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
