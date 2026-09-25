using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualStreamDeck.Core;

public static class AppInfo
{
    public const string Name = "Virtual Stream Deck";
    public const string FullName = "Virtual Stream Deck & Soundboard";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "1.0.0";

    /// <summary>Full path of the running exe (the single-file bundle when published).</summary>
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "VirtualStreamDeck.exe");
}

/// <summary>
/// Everything the app writes lives under one per-user folder in %APPDATA% - nothing is written
/// beside the executable, so the exe can live on a read-only share or a USB stick.
/// </summary>
public static class AppPaths
{
    private static string? _root;

    public static string Root
    {
        get
        {
            if (_root != null) return _root;
            _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VirtualStreamDeck");
            return _root;
        }
    }

    /// <summary>Redirects all data (used by --data for tests and self-verification).</summary>
    public static void Override(string root) => _root = Path.GetFullPath(root);

    public static string Config => Path.Combine(Root, "config.json");
    public static string ConfigBackup => Path.Combine(Root, "config.bak.json");
    public static string Sounds => Path.Combine(Root, "Sounds");
    public static string Logs => Path.Combine(Root, "Logs");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Sounds);
    }
}

public enum LogLevel { Info, Warn, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message);

/// <summary>
/// Always keeps the last few hundred entries in memory for the diagnostics panel; writes to disk
/// only when logging is enabled in settings.
/// </summary>
public static class Log
{
    private static readonly ConcurrentQueue<LogEntry> Ring = new();
    private static readonly object FileLock = new();
    public static bool FileEnabled { get; set; }

    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);
    public static void Error(string msg, Exception? ex = null) =>
        Write(LogLevel.Error, ex == null ? msg : $"{msg}: {ex.GetType().Name}: {ex.Message}");

    public static IReadOnlyList<LogEntry> Recent => Ring.ToArray();

    private static void Write(LogLevel level, string msg)
    {
        var e = new LogEntry(DateTime.Now, level, msg);
        Ring.Enqueue(e);
        while (Ring.Count > 400 && Ring.TryDequeue(out _)) { }
        Debug.WriteLine($"[{level}] {msg}");
        if (!FileEnabled) return;
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                File.AppendAllText(Path.Combine(AppPaths.Logs, $"vsd-{DateTime.Now:yyyyMMdd}.log"),
                    $"{e.Time:HH:mm:ss.fff} {level,-5} {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never take the app down */ }
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Deep copy through JSON - the model is plain data, so this is exact.</summary>
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}

public static class ColorUtil
{
    public static bool TryParse(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 8) s = s[2..];
        if (s.Length == 3) s = string.Concat(s.Select(c => new string(c, 2)));
        if (s.Length != 6) return false;
        try
        {
            r = Convert.ToByte(s[..2], 16);
            g = Convert.ToByte(s.Substring(2, 2), 16);
            b = Convert.ToByte(s.Substring(4, 2), 16);
            return true;
        }
        catch { return false; }
    }

    public static string? Normalize(string? hex) =>
        TryParse(hex, out var r, out var g, out var b) ? $"#{r:X2}{g:X2}{b:X2}" : null;
}
