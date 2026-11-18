using LibreHardwareMonitor.Hardware;
using LichtLuefter.Core.Hardware;

/// <summary>
/// Misst Drehzahl über Tastgrad für CPU_FAN1 (0) und SYS_FAN6 (7) sowie die Pumpe (1, nur 70–100 %).
/// Abwärts bis 10 %, Abbruch sobald < 300 U/min (Stillstand). Danach Anlaufprobe aufwärts.
/// Zeichnet nebenbei jede Sekunde CPU Package, Kerndurchschnitt und GPU auf (für die Profilsimulation).
/// Restore auf BIOS in finally / Strg+C / ProcessExit.
/// </summary>
static class FanSweep
{
    public static void Run(Action<string> W, string traceFile)
    {
        using var hm = new HardwareMonitor();
        hm.Open();
        var sio = hm.SuperIo!;
        if (!sio.IsSupported) { W("nicht unterstützt"); return; }
        var c = new Computer { IsCpuEnabled = true, IsGpuEnabled = true }; c.Open();
        ISensor? S(HardwareType t, string n) => c.Hardware.Where(h => h.HardwareType == t).SelectMany(h => h.Sensors).FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name == n);
        var pkg = S(HardwareType.Cpu, "CPU Package"); var avg = S(HardwareType.Cpu, "Core Average"); var gpu = S(HardwareType.GpuNvidia, "GPU Core");
        var trace = new List<string> { "t;package;coreavg;gpu" };
        var t0 = DateTime.UtcNow; var lastTrace = DateTime.MinValue;

        var touched = new Dictionary<int, BiosChannelState>(); var gate = new object();
        void RestoreAll(string why) { lock (gate) { var f = sio.RestoreBiosAll(touched.Values); if (touched.Count > 0) W($"[{why}] zurück auf BIOS: {(f.Count == 0 ? "bestätigt" : "FEHLER " + string.Join(",", f))}"); touched.Clear(); } }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; RestoreAll("Abbruch"); Environment.Exit(2); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreAll("Exit");

        void Tick()
        {
            hm.Update();
            if ((DateTime.UtcNow - lastTrace).TotalSeconds >= 1)
            {
                foreach (var h in c.Hardware) h.Update();
                lastTrace = DateTime.UtcNow;
                trace.Add($"{(DateTime.UtcNow - t0).TotalSeconds:0};{pkg?.Value:0.0};{avg?.Value:0.0};{gpu?.Value:0.0}".Replace(',', '.').Replace(";", ";"));
            }
        }
        float Rpm(int ch) => hm.Headers.First(h => h.Index == ch).Rpm ?? 0;
        (float rpm, float duty) Hold(int ch, double pct, int settleS, int measureS)
        {
            lock (gate) { if (!sio.SetPercent(ch, pct)) sio.SetPercent(ch, pct); }
            var end = DateTime.UtcNow.AddSeconds(settleS); while (DateTime.UtcNow < end) { Thread.Sleep(250); Tick(); if (Rpm(ch) < 1 && pct < 50) break; }
            double sum = 0; int n = 0; end = DateTime.UtcNow.AddSeconds(measureS);
            while (DateTime.UtcNow < end) { Thread.Sleep(250); Tick(); sum += Rpm(ch); n++; }
            return ((float)(sum / Math.Max(1, n)), hm.Headers.First(h => h.Index == ch).DutyPercent ?? 0);
        }

        try
        {
            foreach (var ch in new[] { 0, 7 })
            {
                var name = ch == 0 ? "CPU_FAN1" : "SYS_FAN6";
                touched[ch] = sio.ReadBiosState(ch) ?? throw new Exception("Bus belegt");
                W($"\n{name}: Tastgrad -> Drehzahl (abwärts)");
                double lastOk = 100; bool stalled = false;
                foreach (var p in new double[] { 100, 80, 60, 50, 40, 35, 30, 25, 20, 15, 10 })
                {
                    var (rpm, duty) = Hold(ch, p, p >= 60 ? 5 : 6, 2);
                    W($"  {p,3} %  {rpm,5:0} U/min   (Ausgabe {duty:0} %)");
                    if (rpm < 300) { stalled = true; W($"  -> Stillstand bei {p} %"); break; }
                    lastOk = p;
                }
                if (stalled)
                {
                    W($"{name}: Anlauf aus dem Stillstand (aufwärts)");
                    foreach (var p in new double[] { 15, 20, 25, 30, 35, 40 })
                    {
                        if (p <= 10) continue;
                        var (rpm, _) = Hold(ch, p, 4, 2);
                        W($"  {p,3} %  {rpm,5:0} U/min");
                        if (rpm > 300) { W($"  -> läuft wieder an bei {p} %"); break; }
                    }
                }
                lock (gate) { sio.RestoreBiosAll([touched[ch]]); touched.Remove(ch); }
                W($"{name}: zurück auf BIOS");
                for (int i = 0; i < 12; i++) { Thread.Sleep(250); Tick(); }
            }

            touched[1] = sio.ReadBiosState(1) ?? throw new Exception("Bus belegt");
            W("\nPUMP_FAN1 (nur 70–100 %):");
            foreach (var p in new double[] { 100, 90, 80, 70 })
            {
                var (rpm, duty) = Hold(1, p, 5, 2);
                W($"  {p,3} %  {rpm,5:0} U/min   (Ausgabe {duty:0} %)");
            }
            lock (gate) { sio.RestoreBiosAll([touched[1]]); touched.Remove(1); }
            W("PUMP_FAN1: zurück auf BIOS");
            // noch 60 s Temperaturverlauf aufzeichnen
            var stop = DateTime.UtcNow.AddSeconds(60); while (DateTime.UtcNow < stop) { Thread.Sleep(250); Tick(); }
        }
        finally { RestoreAll("Ende"); c.Close(); File.WriteAllLines(traceFile, trace); }
        W($"\nModusregister am Ende: 0x{sio.ReadModeRegister():X2}");
    }
}
