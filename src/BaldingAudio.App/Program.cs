using System.Runtime.InteropServices;
using BaldingAudio.App.Config;
using BaldingAudio.Core.Audio;
using BaldingAudio.App.Audio;

namespace BaldingAudio.App;

internal static class Program
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_APP = 0x8000;

    private static Mutex? _singleInstance;

    [STAThread]
    private static int Main(string[] args)
    {
        var options = CommandLine.Parse(args);

        if (options.ShowHelp)
        {
            Console.WriteLine(CommandLineOptions.Help);
            return 0;
        }

        // COM is needed for the audio endpoint enumerator. Doing this per-thread is
        // harmless and avoids the main-thread STA conflict.
        try { MMDevice.CoInitializeEx(IntPtr.Zero, 0x2 /* COINIT_APARTMENTTHREADED */); }
        catch (Exception ex) { Log.Warn($"CoInitializeEx failed: {ex.Message}"); }

        if (options.RunSelfTest)
        {
            Console.WriteLine("baldingAudio self-test");
            var results = Core.Diagnostics.SelfTest.RunAll(Console.WriteLine);
            var ok = Core.Diagnostics.SelfTest.AllPassed(results);
            Console.WriteLine(ok ? "\nALL PASSED" : "\nFAILURES PRESENT");
            return ok ? 0 : 1;
        }

        if (!options.RunSelfTest && !options.Console)
            Log.Open(Path.Combine(AppConfig.DefaultPath, "..", "baldingAudio.log"), console: false);
        Log.Verbose = options.Verbose;

        _singleInstance = new Mutex(true, "baldingAudio-single-instance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show(
                "baldingAudio is already running. Look for the tray icon.",
                "baldingAudio",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        var config = AppConfig.Load(options.ConfigPath);
        if (options.EndpointId is not null) config.DeviceId = options.EndpointId;
        if (options.Preset is not null) config.Preset = options.Preset.Value;
        if (options.RenderHz is not null) config.RenderHz = options.RenderHz.Value;
        config.ApplyPreset();

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error($"unhandled: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"fatal: {e.ExceptionObject}");

        using var host = new AppHost(config);
        using var hotkeys = new HotkeyWindow(host);
        using var tray = new TrayIcon(host, config);

        try
        {
            host.Start(demo: options.Demo, holdDemo: options.DemoFlood);
        }
        catch (Exception ex)
        {
            Log.Error($"startup failed: {ex}");
            MessageBox.Show(
                $"baldingAudio could not start.\n\n{ex.Message}\n\nSee the log at:\n{AppConfig.DefaultPath}",
                "baldingAudio",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }

        hotkeys.Register();
        config.Save();
        Log.Info("running. Ctrl+Alt+B toggles, Ctrl+Alt+P switches layout, Ctrl+Alt+Q quits.");

        Application.Run();

        tray.Visible = false;
        config.Save();
        return 0;
    }
}

/// <summary>Command line, kept separate so it stays testable and documented in one place.</summary>
internal sealed record CommandLineOptions
{
    public bool Demo { get; init; }

    /// <summary>Demo variant that parks every cue on screen at once.</summary>
    public bool DemoFlood { get; init; }
    public bool ShowHelp { get; init; }
    public bool RunSelfTest { get; init; }
    public bool Console { get; init; }
    public bool Verbose { get; init; }
    public string? ConfigPath { get; init; }
    public string? EndpointId { get; init; }
    public MonitorSelection? Monitor { get; init; }
    public OverlayLayoutPreset? Preset { get; init; }
    public int? RenderHz { get; init; }

    public const string Help = """
        baldingAudio - directional sound indicator for shooters

        Usage: baldingAudio [options]

          --demo             run the scripted visual demo, no audio needed
          --demo-flood       like --demo, but park every cue on screen at once
          --selftest         verify the analyser against synthetic 7.1 audio
          --endpoint <id>    capture a specific render endpoint
          --monitor <which>  primary | foreground | all
          --preset <which>   edge | compact
          --render-hz <n>    overlay refresh rate (default 60)
          --config <path>    use an alternative config file
          --console          log to stdout as well as the log file
          --verbose          include per-frame debug logging
          --help             this text

        For direction to work properly the default playback device must be
        multichannel. See README.md.
        """;
}

internal static class CommandLine
{
    public static CommandLineOptions Parse(string[] args)
    {
        var o = new CommandLineOptions
        {
            Console = Array.IndexOf(args, "--console") >= 0,
        };

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--demo": o = o with { Demo = true }; break;
                case "--demo-flood": o = o with { Demo = true, DemoFlood = true }; break;
                case "--selftest": o = o with { RunSelfTest = true }; break;
                case "--help" or "-h" or "/?": o = o with { ShowHelp = true }; break;
                case "--verbose": o = o with { Verbose = true }; break;
                case "--console": o = o with { Console = true }; break;
                case "--endpoint": o = o with { EndpointId = Next(args, ref i) }; break;
                case "--config": o = o with { ConfigPath = Next(args, ref i) }; break;
                case "--render-hz" or "--renderhz":
                    if (int.TryParse(Next(args, ref i), out var hz)) o = o with { RenderHz = hz };
                    break;
                case "--monitor":
                    if (Enum.TryParse<MonitorSelection>(Next(args, ref i), true, out var mon))
                        o = o with { Monitor = mon };
                    break;
                case "--preset":
                    if (Enum.TryParse<OverlayLayoutPreset>(Next(args, ref i), true, out var pre))
                        o = o with { Preset = pre };
                    break;
                default:
                    Console.Error.WriteLine($"unknown option: {a}");
                    o = o with { ShowHelp = true };
                    break;
            }
        }

        if (o.Console) Log.Open(null, console: true);
        Log.Verbose = o.Verbose;
        return o;
    }

    private static string? Next(string[] args, ref int i) => i + 1 < args.Length ? args[++i] : null;
}
