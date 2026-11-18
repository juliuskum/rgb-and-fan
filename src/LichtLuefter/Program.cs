using System.Security.Principal;
using LichtLuefter.Core;
using LichtLuefter.Core.Fans;

namespace LichtLuefter;

internal static class Program
{
    public const string ShowEventName = @"Local\LichtLuefter.Show";
    public const string ExitEventName = @"Local\LichtLuefter.Exit";
    /// <summary>Laufende App auf „BIOS-Kurve“ umschalten (für „--restore-bios“).</summary>
    public const string BiosEventName = @"Local\LichtLuefter.Bios";
    public const string InstanceMutexName = @"Local\LichtLuefter.Instance";

    /// <summary>
    /// Ereignis, das auch nicht erhöhte Prozesse desselben Benutzers setzen dürfen (z. B. „--exit“ aus einer
    /// normalen Konsole). Mehr als Fenster zeigen bzw. sauber beenden kann man darüber nicht auslösen.
    /// </summary>
    public static EventWaitHandle UserEvent(string name)
    {
        var sec = new System.Security.AccessControl.EventWaitHandleSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        sec.AddAccessRule(new System.Security.AccessControl.EventWaitHandleAccessRule(user,
            System.Security.AccessControl.EventWaitHandleRights.Synchronize | System.Security.AccessControl.EventWaitHandleRights.Modify,
            System.Security.AccessControl.AccessControlType.Allow));
        sec.AddAccessRule(new System.Security.AccessControl.EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            System.Security.AccessControl.EventWaitHandleRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        return System.Threading.EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, sec);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // Watchdog-Prozess: eigener Einstieg, keine Oberfläche.
        if (args.Length >= 2 && args[0] == "--watchdog" && int.TryParse(args[1], out int pid))
            return Watchdog.Run(pid);

        // Notfall: alle Kanäle sofort zurück auf BIOS (auch ohne laufende App).
        // Deinstallation (aus „Apps & Features“).
        if (args.Contains("--uninstall"))
        {
            ApplicationConfiguration.Initialize();
            return Installer.Uninstall(quiet: args.Contains("--quiet"));
        }

        if (args.Contains("--restore-bios"))
            return EmergencyRestore(quiet: args.Contains("--quiet"));

        // Laufende App umschalten: --light-effect <effekt> | --light-profile <name> | --fan-profile <id>
        foreach (var (flag, cmd) in new[] { ("--light-effect", "light-effect"), ("--light-profile", "light-profile"), ("--fan-profile", "fan-profile") })
        {
            int i = Array.IndexOf(args, flag);
            if (i >= 0 && i + 1 < args.Length) return SendCommand($"{cmd} {args[i + 1]}");
        }

        // Laufende App sauber beenden (Lüfter zurück auf BIOS).
        if (args.Contains("--exit"))
        {
            try { EventWaitHandle.OpenExisting(ExitEventName).Set(); return 0; }
            catch { return 1; }
        }

        using var instance = new Mutex(true, InstanceMutexName, out bool first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            return 0;
        }

        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("Licht & Lüfter braucht Administratorrechte (für Sensoren und Lüfter-Controller über PawnIO).",
                "Licht & Lüfter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        Log.Prefix = "app";
        Log.Cleanup();
        Log.Write("==== Start " + string.Join(' ', args));

        TrayApp? ctx = null;
        // Vor dem ersten Steuerelement registrieren. UI-Fehler sind nicht fatal; echte Abstürze stellen die Lüfter zurück.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => { if (ctx != null) ctx.OnUiError(e.Exception); else Log.Write("UI-Fehler: " + e.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ctx?.OnFatal(e.ExceptionObject as Exception);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ctx?.ShutdownHardware("Prozessende");

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        using (ctx = new TrayApp(startHidden: args.Contains("--tray")))
            Application.Run(ctx);
        return 0;
    }

    public const string CommandPipeName = "LichtLuefter.Cmd";

    private static int SendCommand(string line)
    {
        try
        {
            using var c = new System.IO.Pipes.NamedPipeClientStream(".", CommandPipeName, System.IO.Pipes.PipeDirection.Out);
            c.Connect(2000);
            using var w = new StreamWriter(c) { AutoFlush = true };
            w.WriteLine(line);
            return 0;
        }
        catch { return 1; }
    }

    private static int EmergencyRestore(bool quiet)
    {
        Log.Prefix = "restore";
        bool appRunning = Mutex.TryOpenExisting(InstanceMutexName, out var m);
        m?.Dispose();
        bool ok;
        if (appRunning)
        {
            // Die laufende App würde direkt zurückgestellte Kanäle sofort wieder übernehmen. Deshalb sie bitten,
            // auf „BIOS-Kurve“ umzuschalten (sie gibt dann selbst zurück und löscht die Zustandsdatei).
            Log.Write("App läuft – schalte sie auf BIOS-Kurve um.");
            try { EventWaitHandle.OpenExisting(BiosEventName).Set(); } catch (Exception ex) { Log.Write("Signal fehlgeschlagen: " + ex.Message); }
            var until = DateTime.UtcNow.AddSeconds(10);
            while (File.Exists(AppPaths.FanState) && DateTime.UtcNow < until) Thread.Sleep(250);
            ok = !File.Exists(AppPaths.FanState);
            if (!ok)
            {
                // App reagiert nicht: direkt zurückstellen, Zustandsdatei aber für Watchdog/App stehen lassen.
                Log.Write("App hat nicht reagiert – stelle direkt zurück.");
                var st = FanStateFile.Load() ?? FanStateFile.AllChannels();
                st.OwnerPid = -1;
                ok = Watchdog.RestoreFromStateFile(st, deleteStateFile: false);
            }
        }
        else
        {
            var st = FanStateFile.Load() ?? FanStateFile.AllChannels();
            st.OwnerPid = -1;
            ok = Watchdog.RestoreFromStateFile(st);
        }
        if (!quiet) MessageBox.Show(ok ? "Alle Lüfterkanäle laufen wieder nach den BIOS-Kurven." : "Zurückstellen nicht bestätigt. Ein Neustart des PCs setzt den Lüfter-Controller sicher zurück.",
            "Licht & Lüfter", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        return ok ? 0 : 1;
    }
}
