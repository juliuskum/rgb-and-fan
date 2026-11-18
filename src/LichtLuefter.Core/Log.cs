namespace LichtLuefter.Core;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LichtLuefter");
    public static string Settings => Path.Combine(Root, "settings.json");
    /// <summary>Zustand für den Watchdog: welche Kanäle die App gerade steuert und deren BIOS-Werte.</summary>
    public static string FanState => Path.Combine(Root, "fan-state.json");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Backups => Path.Combine(Root, "backups");
    public static string WebView => Path.Combine(Root, "webview2");
}

/// <summary>Einfaches, threadsicheres Textlog mit Rotation (eine Datei pro Tag, 14 Tage).</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string Prefix { get; set; } = "app";

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var file = Path.Combine(AppPaths.Logs, $"{Prefix}-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* Logging darf nie die App stören */ }
    }

    public static void Cleanup()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.Logs, "*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
        }
        catch { }
    }
}
