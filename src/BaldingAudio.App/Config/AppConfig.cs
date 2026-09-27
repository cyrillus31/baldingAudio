using System.Text.Json;
using System.Text.Json.Serialization;
using BaldingAudio.Core.Config;
using BaldingAudio.Core.Overlay;

namespace BaldingAudio.App.Config;

public enum OverlayLayoutPreset
{
    /// <summary>Bars anchored near the screen edges, growing toward the centre. Default.</summary>
    Edge,

    /// <summary>Bars clustered around the centre, hugging the crosshair area.</summary>
    Compact,
}

public enum MonitorSelection
{
    Primary,
    /// <summary>Whichever monitor currently hosts the foreground window.</summary>
    Foreground,
    /// <summary>Every monitor, each with its own overlay window.</summary>
    All,
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    public string? DeviceId { get; set; }

    public MonitorSelection Monitor { get; set; } = MonitorSelection.Primary;

    public OverlayLayoutPreset Preset { get; set; } = OverlayLayoutPreset.Edge;

    public OverlayStyle Style { get; set; } = new();

    public AudioTuning Tuning { get; set; } = new();

    /// <summary>Overlay refresh rate in Hz. Higher costs more GPU; 60 is ample.</summary>
    public int RenderHz { get; set; } = 60;

    public bool StartMinimised { get; set; }

    /// <summary>Show a short status line while tuning. Off in normal use.</summary>
    public bool ShowDiagnostics { get; set; }

    /// <summary>Play a test cue on startup so the user can confirm it works.</summary>
    public bool SelfTestOnStart { get; set; } = true;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "baldingAudio",
        "config.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"could not read config at {path}: {ex.Message}. Using defaults.");
        }
        return new AppConfig();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Log.Warn($"could not write config to {path}: {ex.Message}");
        }
    }

    /// <summary>Applies the selected preset's geometry, leaving colours alone.</summary>
    public void ApplyPreset()
    {
        switch (Preset)
        {
            case OverlayLayoutPreset.Compact:
                Style.EdgeInsetFraction = 0.30;
                Style.MaxLengthFraction = 0.18;
                Style.BarThicknessFraction = 0.016;
                Style.VerticalSpanFraction = 0.46;
                break;
            default:
                Style.EdgeInsetFraction = 0.045;
                Style.MaxLengthFraction = 0.30;
                Style.BarThicknessFraction = 0.016;
                Style.VerticalSpanFraction = 0.62;
                break;
        }
    }
}
