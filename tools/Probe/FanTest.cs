using LichtLuefter.Core.Hardware;

/// <summary>
/// Verifiziert Setzen und das korrigierte Zurücksetzen auf BIOS. Sicherheitsregeln:
///  * Lüfterkanäle (0, 7): 100 % und 40 %, je ~6 s. Pumpe (1): nur 100 %. Leere Kanäle werden nicht angefasst.
///  * Nach jedem Kanal: zurück auf BIOS, Modusbit und Ausgabewert werden geprüft.
///  * finally / Strg+C / ProcessExit: alle berührten Kanäle zurück auf BIOS.
/// </summary>
static class FanTest
{
    public static void Run(Action<string> W, bool writeTest)
    {
        using var hm = new HardwareMonitor();
        hm.Open();
        W($"Board: {hm.BoardName} | CPU: {hm.CpuName} {hm.CpuTemp:0.0} °C | GPU: {hm.GpuName} {hm.GpuTemp:0.0} °C");
        foreach (var h in hm.Headers) W($"  Kanal {h.Index} {h.Name,-16} {h.Rpm,6:0} U/min  {h.DutyPercent,4:0} %");
        var sio = hm.SuperIo;
        if (sio == null || !sio.IsSupported) { W("Nicht unterstützt: " + sio?.UnsupportedReason); return; }
        W($"Modusregister: 0x{sio.ReadModeRegister():X2}");
        if (!writeTest) return;

        var touched = new Dictionary<int, BiosChannelState>();
        var gate = new object();
        void RestoreAll(string why)
        {
            var failed = sio.RestoreBiosAll(touched.Values);
            if (touched.Count > 0) W($"  [{why}] Kanäle {string.Join(",", touched.Keys)} -> BIOS: {(failed.Count == 0 ? "bestätigt" : "FEHLGESCHLAGEN " + string.Join(",", failed))}");
            touched.Clear();
        }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; lock (gate) RestoreAll("Abbruch"); Environment.Exit(2); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { lock (gate) RestoreAll("Exit"); };

        string State(int ch) { hm.Update(); var h = hm.Headers.First(x => x.Index == ch); return $"{h.Rpm:0} U/min, Ausgabe {h.DutyPercent:0} %, manuell={sio.IsManual(ch)}"; }

        try
        {
            foreach (var (ch, steps) in new (int, double[])[] { (0, [100, 40]), (1, [100]), (7, [100, 40]) })
            {
                W($"\nKanal {ch}: vorher {State(ch)}");
                lock (gate) touched[ch] = sio.ReadBiosState(ch) ?? throw new Exception("Bus belegt");
                W($"  BIOS-Befehlsregister gesichert: 0x{touched[ch].Command:X2}");
                foreach (var p in steps)
                {
                    bool ok; lock (gate) ok = sio.SetPercent(ch, p);
                    Thread.Sleep(6000);
                    W($"  {p,3} % gesetzt (bestätigt={ok}) -> {State(ch)}");
                }
                bool r; lock (gate) { r = sio.RestoreBios(ch, touched[ch].Command); if (r) touched.Remove(ch); }
                Thread.Sleep(5000);
                W($"  zurück auf BIOS (bestätigt={r}) -> {State(ch)}");
            }
        }
        finally { lock (gate) RestoreAll("Ende"); }
        Thread.Sleep(3000);
        W($"\nEndzustand: Modusregister 0x{sio.ReadModeRegister():X2}");
        hm.Update();
        foreach (var h in hm.Headers) W($"  Kanal {h.Index} {h.Name,-16} {h.Rpm,6:0} U/min  {h.DutyPercent,4:0} %");
    }
}
