using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using LichtLuefter.Core;

namespace LichtLuefter;

/// <summary>Autostart über die Aufgabenplanung (nötig, weil die App Adminrechte braucht).</summary>
internal static class Autostart
{
    private const string TaskName = "LichtLuefter";

    public static bool IsEnabled()
    {
        var r = RunSchtasks($"/Query /TN \"{TaskName}\"");
        return r.code == 0;
    }

    public static (bool ok, string message) Set(bool enable)
    {
        if (!enable)
        {
            var d = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
            return (d.code == 0 || !IsEnabled(), d.output);
        }
        var exe = Environment.ProcessPath!;
        var user = SecurityElement.Escape(System.Security.Principal.WindowsIdentity.GetCurrent().Name);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Startet Licht &amp; Lüfter beim Anmelden (mit Adminrechten, ohne UAC-Abfrage).</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT8S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec>
                <Command>{SecurityElement.Escape(exe)}</Command>
                <Arguments>--tray</Arguments>
                <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exe)!)}</WorkingDirectory>
              </Exec></Actions>
            </Task>
            """;
        var tmp = Path.Combine(Path.GetTempPath(), "LichtLuefter-task.xml");
        File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
        try
        {
            var r = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
            Log.Write($"Autostart anlegen: {r.code} {r.output}");
            return (r.code == 0, r.output);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    private static (int code, string output) RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(10000);
        return (p.ExitCode, o.Trim());
    }
}

/// <summary>Startet den Watchdog als eigenständigen Prozess außerhalb des Job-Objekts der App.</summary>
internal static class WatchdogLauncher
{
    private const uint CREATE_NO_WINDOW = 0x08000000, CREATE_BREAKAWAY_FROM_JOB = 0x01000000, DETACHED_PROCESS = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, System.Text.StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    public static Process? Start()
    {
        var exe = Environment.ProcessPath!;
        foreach (var flags in new[] { CREATE_NO_WINDOW | DETACHED_PROCESS | CREATE_BREAKAWAY_FROM_JOB, CREATE_NO_WINDOW | DETACHED_PROCESS })
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var cmd = new System.Text.StringBuilder($"\"{exe}\" --watchdog {Environment.ProcessId}");
            if (CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi))
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                Log.Write($"Watchdog gestartet (PID {pi.dwProcessId}, Breakaway={(flags & CREATE_BREAKAWAY_FROM_JOB) != 0}).");
                try { return Process.GetProcessById(pi.dwProcessId); } catch { return null; }
            }
        }
        Log.Write("Watchdog konnte nicht gestartet werden: " + Marshal.GetLastWin32Error());
        return null;
    }
}

internal static class OpenRgbHelper
{
    public static bool IsRunning() => Process.GetProcessesByName("OpenRGB").Length > 0;

    /// <summary>Startet OpenRGB minimiert mit SDK-Server. Ändert keine OpenRGB-Einstellungen.</summary>
    public static bool Start(string path)
    {
        if (!File.Exists(path)) { Log.Write("OpenRGB nicht gefunden: " + path); return false; }
        Process.Start(new ProcessStartInfo(path, "--server --startminimized")
        {
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = false,
        });
        Log.Write("OpenRGB gestartet: " + path + " --server --startminimized");
        return true;
    }

    /// <summary>Beendet laufendes OpenRGB und startet es mit SDK-Server neu (nur auf Knopfdruck).</summary>
    public static bool Restart(string path)
    {
        foreach (var p in Process.GetProcessesByName("OpenRGB"))
        {
            try { p.CloseMainWindow(); if (!p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(3000); } } catch (Exception ex) { Log.Write("OpenRGB beenden: " + ex.Message); }
        }
        return Start(path);
    }

    public static string? FindPath(string configured)
    {
        if (File.Exists(configured)) return configured;
        foreach (var p in Process.GetProcessesByName("OpenRGB"))
            try { if (p.MainModule?.FileName is string f && File.Exists(f)) return f; } catch { }
        foreach (var c in new[] { @"C:\Program Files\OpenRGB\OpenRGB.exe", @"D:\Program Files\OpenRGB\OpenRGB.exe" })
            if (File.Exists(c)) return c;
        return null;
    }
}

/// <summary>
/// Sicherung der OpenRGB-Konfiguration (%APPDATA%\OpenRGB) bei jedem Start, sofern sie sich geändert hat.
/// Die App schreibt selbst nichts in diese Dateien; die Sicherung schützt vor Änderungen durch OpenRGB-Neustarts.
/// </summary>
internal static class OpenRgbBackup
{
    public static string? Run()
    {
        try
        {
            var src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRGB");
            if (!Directory.Exists(src)) return null;
            var files = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "logs" + Path.DirectorySeparatorChar))
                .OrderBy(f => f).ToList();
            using var sha = SHA256.Create();
            foreach (var f in files)
            {
                var b = File.ReadAllBytes(f);
                var name = System.Text.Encoding.UTF8.GetBytes(f);
                sha.TransformBlock(name, 0, name.Length, null, 0);
                sha.TransformBlock(b, 0, b.Length, null, 0);
            }
            sha.TransformFinalBlock([], 0, 0);
            var hash = Convert.ToHexString(sha.Hash!)[..16];

            Directory.CreateDirectory(AppPaths.Backups);
            var existing = Directory.GetDirectories(AppPaths.Backups, "OpenRGB-*").OrderBy(d => d).ToList();
            if (existing.Any(d => d.EndsWith(hash))) return null;

            var dst = Path.Combine(AppPaths.Backups, $"OpenRGB-{DateTime.Now:yyyyMMdd-HHmmss}-{hash}");
            foreach (var f in files)
            {
                var rel = Path.GetRelativePath(src, f);
                var t = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                File.Copy(f, t);
            }
            existing.Add(dst);
            foreach (var old in existing.Take(Math.Max(0, existing.Count - 10))) try { Directory.Delete(old, true); } catch { }
            Log.Write("OpenRGB-Konfiguration gesichert: " + dst);
            return dst;
        }
        catch (Exception ex)
        {
            Log.Write("Sicherung der OpenRGB-Konfiguration fehlgeschlagen: " + ex.Message);
            return null;
        }
    }
}
