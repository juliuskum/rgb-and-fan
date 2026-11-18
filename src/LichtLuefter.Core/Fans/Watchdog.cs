using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using LichtLuefter.Core.Hardware;
using LichtLuefter.Core.Settings;

namespace LichtLuefter.Core.Fans;

/// <summary>
/// Eigenständiger Wächterprozess. Stirbt oder hängt die App, während sie Lüfter steuert, stellt der Watchdog
/// mit eigenem Treiberzugriff alle Kanäle aus der Zustandsdatei zurück auf die BIOS-Kurve.
/// </summary>
public static class Watchdog
{
    public const string StopEventName = @"Local\LichtLuefter.WatchdogStop";

    public static int Run(int ownerPid)
    {
        Log.Prefix = "watchdog";
        Log.Write($"Watchdog gestartet für PID {ownerPid}.");
        Process? owner = null;
        try { owner = Process.GetProcessById(ownerPid); } catch { }

        using var hb = new Heartbeat();
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName);
        stop.Reset();
        string reason = "App-Prozess beendet";

        while (owner != null && !owner.HasExited)
        {
            if (stop.WaitOne(1000))
            {
                // Die App hat sauber beendet und selbst zurückgestellt – trotzdem prüfen.
                owner.WaitForExit(10000);
                reason = "sauberes Beenden (Kontrolle)";
                break;
            }
            var state = FanStateFile.Load();
            if (state != null && state.OwnerPid == ownerPid && hb.Age.TotalSeconds > Safety.HeartbeatTimeoutSeconds)
            {
                reason = $"App reagiert seit {hb.Age.TotalSeconds:0} s nicht";
                Log.Write(reason + " – beende sie und stelle auf BIOS zurück.");
                // Nur die App selbst, nicht den Prozessbaum (ein von ihr gestartetes OpenRGB soll weiterlaufen).
                try { owner.Kill(false); owner.WaitForExit(5000); } catch (Exception ex) { Log.Write("Beenden fehlgeschlagen: " + ex.Message); }
                break;
            }
        }

        var st = FanStateFile.Load();
        if (st == null || (st.OwnerPid != ownerPid && st.OwnerPid != -1))
        {
            Log.Write($"Ende ({reason}): nichts zurückzustellen.");
            return 0;
        }
        Log.Write($"{reason}: stelle Kanäle {string.Join(",", st.Channels.Select(c => c.Index))} auf BIOS zurück.");
        return RestoreFromStateFile(st) ? 0 : 1;
    }

    /// <summary>Öffnet eigenen Treiberzugriff (nur Mainboard) und stellt zurück. Versucht es bis zu 60 s.</summary>
    public static bool RestoreFromStateFile(FanStateFile st, bool deleteStateFile = true)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until)
        {
            var c = new Computer { IsMotherboardEnabled = true };
            try
            {
                c.Open();
                foreach (var hw in c.Hardware) { hw.Update(); foreach (var s in hw.SubHardware) s.Update(); }
                var sio = SuperIoAccess.Find(c);
                if (sio == null || !sio.IsSupported)
                {
                    Log.Write("Watchdog: Lüfter-Controller nicht zugreifbar: " + (sio?.UnsupportedReason ?? "nicht gefunden"));
                }
                else
                {
                    var failed = sio.RestoreBiosAll(st.States);
                    if (failed.Count == 0)
                    {
                        if (deleteStateFile) FanStateFile.Delete();
                        Log.Write("Watchdog: alle Kanäle auf BIOS – bestätigt.");
                        return true;
                    }
                    Log.Write("Watchdog: nicht bestätigt für " + string.Join(",", failed));
                }
            }
            catch (Exception ex) { Log.Write("Watchdog-Fehler: " + ex.Message); }
            finally { try { c.Close(); } catch { } }
            Thread.Sleep(2000);
        }
        Log.Write("Watchdog: Zurückstellen nach 60 s aufgegeben. Ein Neustart des PCs setzt den Lüfter-Controller sicher zurück.");
        return false;
    }
}
