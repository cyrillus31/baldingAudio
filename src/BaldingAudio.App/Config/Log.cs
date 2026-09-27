using System.Diagnostics;
using System.Text;

namespace BaldingAudio.App.Config;

/// <summary>
/// Minimal logger: console while debugging, a file next to the config otherwise.
/// A silent failure in an overlay is indistinguishable from a working one, so the
/// log is deliberately chatty about state changes.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;
    private static bool _console;

    public static bool Verbose { get; set; }

    public static void Open(string? path, bool console)
    {
        _console = console;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _file = new StreamWriter(path, append: true, encoding: Encoding.UTF8)
            {
                AutoFlush = true,
            };
        }
        catch
        {
            _file = null;
        }
    }

    public static void Info(string message) => Write("INFO", message, false);
    public static void Warn(string message) => Write("WARN", message, false);
    public static void Error(string message) => Write("ERROR", message, false);
    public static void Debug(string message) => Write("DEBUG", message, true);

    private static void Write(string level, string message, bool debugOnly)
    {
        if (debugOnly && !Verbose) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        lock (Gate)
        {
            if (_console) Console.WriteLine(line);
            try { _file?.WriteLine(line); } catch { /* logging must never throw */ }
        }
    }
}
