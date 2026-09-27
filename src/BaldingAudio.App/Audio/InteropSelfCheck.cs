using System.Runtime.InteropServices;

namespace BaldingAudio.App.Audio;

/// <summary>
/// Static checks on the WASAPI interop declarations.
///
/// Every fault that stopped capture from starting was a constant or a struct layout
/// written from memory rather than checked against the Windows SDK:
/// <list type="bullet">
///   <item><description>two invented interface IIDs,</description></item>
///   <item><description>LOOPBACK set to the CROSSPROCESS value,</description></item>
///   <item><description>WAVEFORMATEXTENSIBLE with no Pack, and a nested packed struct.</description></item>
/// </list>
///
/// None of them throw where you would expect. A wrong IID becomes
/// <c>InvalidCastException</c> from the marshaller, a wrong flag becomes
/// <c>E_INVALIDARG</c>, and a wrong layout returns channel counts like 32843. Every one
/// of them cost real debugging time on a machine that was reporting the wrong error.
///
/// All of it is checkable without an audio device, so it is checked here. Runs on Linux
/// under <c>--selftest</c> (see <see cref="Program"/>), which is the point: catch these
/// before they reach a user's machine.
/// </summary>
internal static class InteropSelfCheck
{
    internal record struct Check(string Name, bool Passed, string Detail);

    /// <summary>
    /// Values transcribed from the Windows SDK. If one of these disagrees with the
    /// interop declarations, the declaration is wrong - not this table.
    /// </summary>
    private static readonly Dictionary<string, Guid> SdkIids = new()
    {
        ["IID_IMMDeviceEnumerator"] = new("A95664D2-9614-4F35-A746-DE8DB63617E6"),
        ["IID_IMMDevice"] = new("D666063F-1587-4E43-81F1-B948E807363F"),
        ["IID_IAudioClient"] = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),
        ["IID_IAudioCaptureClient"] = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),
        ["CLSID_MMDeviceEnumerator"] = new("BCDE0395-E52F-467C-8E3D-C4579291692E"),
        ["KSDATAFORMAT_SUBTYPE_IEEE_FLOAT"] = new("00000003-0000-0010-8000-00aa00389b71"),
    };

    private static readonly Dictionary<string, uint> SdkFlags = new()
    {
        ["AUDCLNT_STREAMFLAGS_CROSSPROCESS"] = 0x00010000,
        ["AUDCLNT_STREAMFLAGS_LOOPBACK"] = 0x00020000,
        ["AUDCLNT_STREAMFLAGS_EVENTCALLBACK"] = 0x00040000,
        ["AUDCLNT_STREAMFLAGS_NOPERSIST"] = 0x00080000,
    };

    private static readonly Dictionary<string, int> SdkHresults = new()
    {
        ["AUDCLNT_S_BUFFER_EMPTY"] = unchecked((int)0x08890001),
        ["AUDCLNT_E_DEVICE_INVALIDATED"] = unchecked((int)0x88890008),
        ["AUDCLNT_E_UNSUPPORTED_FORMAT"] = unchecked((int)0x88890014),
        ["AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED"] = unchecked((int)0x8889001A),
    };

    public static IReadOnlyList<Check> RunAll(Action<string>? log = null)
    {
        var results = new List<Check>
        {
            CheckStructSizes(),
            CheckIids(),
            CheckFlags(),
            CheckHresults(),
            CheckWaveFormatTag(),
        };

        foreach (var r in results)
            log?.Invoke($"  [{(r.Passed ? "PASS" : "FAIL")}] {r.Name}: {r.Detail}");

        return results;
    }

    public static bool AllPassed(IReadOnlyList<Check> results) => results.All(r => r.Passed);

    private static Check CheckStructSizes()
    {
        var exSize = Marshal.SizeOf(typeof(WAVEFORMATEXTENSIBLE));
        var plainSize = Marshal.SizeOf(typeof(WAVEFORMATEX));

        // WAVEFORMATEX is 18 bytes because the SDK declares it inside #pragma pack(2);
        // C#'s default layout makes it 20 and shifts every field after it.
        // WAVEFORMATEXTENSIBLE is 44: the fields pack out to 42, and the struct's
        // alignment of 4 rounds it up.
        if (plainSize == 18 && exSize == 44)
            return new("interop struct sizes are right", true, $"WAVEFORMATEX 18, WAVEFORMATEXTENSIBLE {exSize}");

        return new("interop struct sizes are right", false,
            $"WAVEFORMATEX is {plainSize} (want 18, needs Pack=2), " +
            $"WAVEFORMATEXTENSIBLE is {exSize} (want 44). The CLR would read garbage.");
    }

    private static Check CheckIids()
    {
        var actual = new Dictionary<string, Guid>
        {
            ["IID_IMMDeviceEnumerator"] = typeof(IMMDeviceEnumerator).GUID,
            ["IID_IMMDevice"] = typeof(IMMDevice).GUID,
            ["IID_IAudioClient"] = typeof(IAudioClient).GUID,
            ["IID_IAudioCaptureClient"] = typeof(IAudioCaptureClient).GUID,
            ["CLSID_MMDeviceEnumerator"] = MMDevice.ClsidDeviceEnumerator,
            ["KSDATAFORMAT_SUBTYPE_IEEE_FLOAT"] = MMDevice.WaveFormatSubtypes_IeeeFloat,
        };

        var bad = new List<string>();
        foreach (var (name, expected) in SdkIids)
        {
            if (!actual.TryGetValue(name, out var got)) { bad.Add($"{name} missing"); continue; }
            if (got != expected) bad.Add($"{name} is {got}, SDK says {expected}");
        }

        if (bad.Count == 0)
            return new("interface IIDs match the SDK", true, $"{SdkIids.Count} checked");

        // This is the check that would have caught the original bug. Say so explicitly.
        return new("interface IIDs match the SDK", false,
            string.Join("; ", bad) + ". A wrong IID makes IMMDevice.Activate return " +
            "E_NOINTERFACE, which the marshaller reports as InvalidCastException.");
    }

    private static Check CheckFlags()
    {
        var actual = new Dictionary<string, uint>
        {
            ["AUDCLNT_STREAMFLAGS_CROSSPROCESS"] = (uint)AudioClientStreamFlags.CrossProcess,
            ["AUDCLNT_STREAMFLAGS_LOOPBACK"] = (uint)AudioClientStreamFlags.Loopback,
            ["AUDCLNT_STREAMFLAGS_EVENTCALLBACK"] = (uint)AudioClientStreamFlags.EventCallback,
            ["AUDCLNT_STREAMFLAGS_NOPERSIST"] = (uint)AudioClientStreamFlags.NoPersist,
        };

        var bad = new List<string>();
        foreach (var (name, expected) in SdkFlags)
        {
            if (!actual.TryGetValue(name, out var got)) { bad.Add($"{name} missing"); continue; }
            if (got != expected) bad.Add($"{name} is 0x{got:X8}, SDK says 0x{expected:X8}");
        }

        return bad.Count == 0
            ? new("stream flags match the SDK", true, $"{SdkFlags.Count} checked")
            : new("stream flags match the SDK", false, string.Join("; ", bad) +
              ". A wrong flag makes IAudioClient.Initialize return E_INVALIDARG.");
    }

    private static Check CheckHresults()
    {
        var actual = new Dictionary<string, int>
        {
            ["AUDCLNT_S_BUFFER_EMPTY"] = MMDevice.AUDCLNT_S_BUFFER_EMPTY,
            ["AUDCLNT_E_DEVICE_INVALIDATED"] = MMDevice.AUDCLNT_E_DEVICE_INVALIDATED,
            ["AUDCLNT_E_UNSUPPORTED_FORMAT"] = MMDevice.AUDCLNT_E_UNSUPPORTED_FORMAT,
            ["AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED"] = MMDevice.AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED,
        };

        var bad = new List<string>();
        foreach (var (name, expected) in SdkHresults)
        {
            if (!actual.TryGetValue(name, out var got)) { bad.Add($"{name} missing"); continue; }
            if (got != expected) bad.Add($"{name} is 0x{got:X8}, SDK says 0x{expected:X8}");
        }

        return bad.Count == 0
            ? new("audio HRESULTs match the SDK", true, $"{SdkHresults.Count} checked")
            : new("audio HRESULTs match the SDK", false,
                string.Join("; ", bad) + ". A wrong HRESULT makes normal operation look like a failure.");
    }

    private static Check CheckWaveFormatTag()
    {
        // 0xFFFE is WAVE_FORMAT_EXTENSIBLE and 0x0003 is IEEE float. These two tags
        // decide whether the rest of the 44-byte format block is read at all, so
        // getting either wrong invalidates everything after it.
        //
        // Compared through a table rather than against literals: the compiler folds
        // const-to-const comparisons, which makes the failure branch unreachable and
        // the check silently useless.
        var expected = new Dictionary<string, ushort>
        {
            ["WAVE_FORMAT_EXTENSIBLE"] = 0xFFFE,
            ["WAVE_FORMAT_IEEE_FLOAT"] = 0x0003,
        };

        var actual = new Dictionary<string, ushort>
        {
            ["WAVE_FORMAT_EXTENSIBLE"] = WAVEFORMATEX.WAVE_FORMAT_EXTENSIBLE,
            ["WAVE_FORMAT_IEEE_FLOAT"] = WAVEFORMATEX.WAVE_FORMAT_IEEE_FLOAT,
        };

        var bad = new List<string>();
        foreach (var (name, want) in expected)
        {
            if (!actual.TryGetValue(name, out var got)) { bad.Add($"{name} missing"); continue; }
            if (got != want) bad.Add($"{name} is 0x{got:X4}, SDK says 0x{want:X4}");
        }

        return bad.Count == 0
            ? new("wave format tags are right", true, $"{expected.Count} checked")
            : new("wave format tags are right", false, string.Join("; ", bad) +
              ". The rest of the format block is only read when these are right.");
    }
}
