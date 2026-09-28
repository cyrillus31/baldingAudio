using System.Reflection;
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

    /// <summary>
    /// The code numbers exactly as audioclient.h writes them, and nothing else.
    ///
    /// <para>
    /// This table used to hold finished HRESULTs - 0x88890008 for
    /// AUDCLNT_E_DEVICE_INVALIDATED and so on - and compared them against
    /// declarations that were wrong in the same way, so it passed. It was checking
    /// one piece of transcription against another piece of transcription, which is
    /// exactly the mistake it was written to catch. Three of the four were wrong, and
    /// the one that actually occurred in the field
    /// (<c>AUDCLNT_E_DEVICE_INVALIDATED</c>) was among them.
    /// </para>
    /// <para>
    /// The header's own form is <c>AUDCLNT_ERR(0x004)</c>: the code number is the
    /// whole of the information, and the HRESULT is <c>0x88890000 | code</c>
    /// (<c>0x08890000 | code</c> for S_ codes). Storing only the code number means
    /// the base cannot be transposed, and the arithmetic below is what the SDK's
    /// macro does.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, int> SdkHresultCodes = new()
    {
        ["AUDCLNT_S_BUFFER_EMPTY"] = 0x001,
        ["AUDCLNT_E_NOT_INITIALIZED"] = 0x001,
        ["AUDCLNT_E_WRONG_ENDPOINT_TYPE"] = 0x003,
        ["AUDCLNT_E_DEVICE_INVALIDATED"] = 0x004,
        ["AUDCLNT_E_NOT_STOPPED"] = 0x005,
        ["AUDCLNT_E_BUFFER_TOO_LARGE"] = 0x006,
        ["AUDCLNT_E_OUT_OF_ORDER"] = 0x007,
        ["AUDCLNT_E_UNSUPPORTED_FORMAT"] = 0x008,
        ["AUDCLNT_E_INVALID_SIZE"] = 0x009,
        ["AUDCLNT_E_DEVICE_IN_USE"] = 0x00a,
        ["AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED"] = 0x00e,
        ["AUDCLNT_E_ENDPOINT_CREATE_FAILED"] = 0x00f,
        ["AUDCLNT_E_SERVICE_NOT_RUNNING"] = 0x010,
        ["AUDCLNT_E_BUFFER_ERROR"] = 0x018,
        ["AUDCLNT_E_RESOURCES_INVALIDATED"] = 0x026,
    };

    /// <summary>Expands a code number the way the SDK's AUDCLNT_ERR macro does.</summary>
    private static int Expand(string name, int code)
        => name.StartsWith("AUDCLNT_S_", StringComparison.Ordinal)
            ? unchecked((int)0x08890000) | code
            : unchecked((int)0x88890000) | code;

    public static IReadOnlyList<Check> RunAll(Action<string>? log = null)
    {
        var results = new List<Check>
        {
            CheckStructSizes(),
            CheckIids(),
            CheckFlags(),
            CheckHresults(),
            CheckRecoverableClassification(),
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
            ["AUDCLNT_E_NOT_INITIALIZED"] = MMDevice.AUDCLNT_E_NOT_INITIALIZED,
            ["AUDCLNT_E_WRONG_ENDPOINT_TYPE"] = MMDevice.AUDCLNT_E_WRONG_ENDPOINT_TYPE,
            ["AUDCLNT_E_DEVICE_INVALIDATED"] = MMDevice.AUDCLNT_E_DEVICE_INVALIDATED,
            ["AUDCLNT_E_NOT_STOPPED"] = MMDevice.AUDCLNT_E_NOT_STOPPED,
            ["AUDCLNT_E_BUFFER_TOO_LARGE"] = MMDevice.AUDCLNT_E_BUFFER_TOO_LARGE,
            ["AUDCLNT_E_OUT_OF_ORDER"] = MMDevice.AUDCLNT_E_OUT_OF_ORDER,
            ["AUDCLNT_E_UNSUPPORTED_FORMAT"] = MMDevice.AUDCLNT_E_UNSUPPORTED_FORMAT,
            ["AUDCLNT_E_INVALID_SIZE"] = MMDevice.AUDCLNT_E_INVALID_SIZE,
            ["AUDCLNT_E_DEVICE_IN_USE"] = MMDevice.AUDCLNT_E_DEVICE_IN_USE,
            ["AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED"] = MMDevice.AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED,
            ["AUDCLNT_E_ENDPOINT_CREATE_FAILED"] = MMDevice.AUDCLNT_E_ENDPOINT_CREATE_FAILED,
            ["AUDCLNT_E_SERVICE_NOT_RUNNING"] = MMDevice.AUDCLNT_E_SERVICE_NOT_RUNNING,
            ["AUDCLNT_E_BUFFER_ERROR"] = MMDevice.AUDCLNT_E_BUFFER_ERROR,
            ["AUDCLNT_E_RESOURCES_INVALIDATED"] = MMDevice.AUDCLNT_E_RESOURCES_INVALIDATED,
        };

        var bad = new List<string>();
        foreach (var (name, code) in SdkHresultCodes)
        {
            var expected = Expand(name, code);
            if (!actual.TryGetValue(name, out var got)) { bad.Add($"{name} missing"); continue; }
            if (got != expected)
                bad.Add($"{name} is 0x{(uint)got:X8}, SDK says AUDCLNT_{(name.StartsWith("AUDCLNT_S_", StringComparison.Ordinal) ? "SUCCESS" : "ERR")}(0x{code:X3}) = 0x{(uint)expected:X8}");
        }

        return bad.Count == 0
            ? new("audio HRESULTs match the SDK", true, $"{SdkHresultCodes.Count} checked")
            : new("audio HRESULTs match the SDK", false,
                string.Join("; ", bad) + ". A wrong HRESULT makes normal operation look like a failure.");
    }

    /// <summary>
    /// The recoverable failures must actually be classified as recoverable.
    ///
    /// This is the check that would have caught the bug in the field. The capture loop
    /// treated every exception as fatal, so when the endpoint was invalidated the
    /// thread exited and the overlay went permanently blank while the process stayed
    /// alive and the heartbeat kept ticking. The classification table below is the
    /// thing that has to be right for recovery to happen at all, and it was only
    /// introduced along with that recovery - so there was nothing to check before.
    /// </summary>
    private static Check CheckRecoverableClassification()
    {
        var expected = new Dictionary<string, bool>
        {
            // The endpoint was reconfigured or removed. Recoverable by re-opening it,
            // which is the only way out, and the one that actually happened.
            ["AUDCLNT_E_DEVICE_INVALIDATED"] = true,
            ["AUDCLNT_E_RESOURCES_INVALIDATED"] = true,
            ["AUDCLNT_E_SERVICE_NOT_RUNNING"] = true,
            ["AUDCLNT_E_BUFFER_ERROR"] = true,
            ["AUDCLNT_E_OUT_OF_ORDER"] = true,

            // Not transient. Retrying forever would just spin on a condition that
            // needs a different endpoint or a different format.
            ["AUDCLNT_E_UNSUPPORTED_FORMAT"] = false,
            ["AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED"] = false,
            ["AUDCLNT_E_NOT_INITIALIZED"] = false,
        };

        var bad = new List<string>();
        foreach (var (name, want) in expected)
        {
            if (!TryGetHresult(name, out var hr))
            {
                bad.Add($"{name} is not declared");
                continue;
            }

            // The real failure, not the description of it. A string comparison would
            // pass on any log line that merely mentions the code, which is the check
            // the first version of this made and it proved nothing.
            var got = WasapiLoopbackCapture.IsRecoverableFailure(hr);
            if (got != want)
                bad.Add($"{MMDevice.DescribeHresult(hr)} (0x{(uint)hr:X8}) classified " +
                        $"{(got ? "recoverable" : "fatal")}, expected {(want ? "recoverable" : "fatal")}");
        }

        return bad.Count == 0
            ? new("recoverable audio failures are classified as recoverable", true, $"{expected.Count} checked")
            : new("recoverable audio failures are classified as recoverable", false,
                string.Join("; ", bad) +
                ". A failure classified fatal silently ends capture, which looks exactly like a dead overlay.");
    }

    /// <summary>
    /// Reads a declared HRESULT field by name. Reflection, not a switch: these are
    /// <c>static readonly</c> fields rather than consts, because the SDK writes them
    /// as macro expansions and a switch on them would not compile as case labels.
    /// </summary>
    private static bool TryGetHresult(string name, out int hr)
    {
        var field = typeof(MMDevice).GetField(name,
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (field?.GetValue(null) is int value)
        {
            hr = value;
            return true;
        }

        hr = 0;
        return false;
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
