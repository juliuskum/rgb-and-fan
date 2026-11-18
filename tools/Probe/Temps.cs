using LibreHardwareMonitor.Hardware;
/// <summary>Liest 15 s lang alle 250 ms CPU-/GPU-Temperaturquellen und Auslastung (nur lesen).</summary>
static class Temps
{
    public static void Run(Action<string> W)
    {
        var c = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true };
        c.Open();
        ISensor? F(HardwareType t, SensorType st, string n) => c.Hardware.Concat(c.Hardware.SelectMany(h => h.SubHardware))
            .Where(h => h.HardwareType == t || (t == HardwareType.SuperIO && h.HardwareType == HardwareType.SuperIO)).SelectMany(h => h.Sensors).FirstOrDefault(s => s.SensorType == st && s.Name == n);
        void Upd() { foreach (var h in c.Hardware) { h.Update(); foreach (var s in h.SubHardware) s.Update(); } }
        Upd(); Upd();
        var src = new (string label, ISensor? s)[] {
            ("Package", F(HardwareType.Cpu, SensorType.Temperature, "CPU Package")),
            ("Kern max", F(HardwareType.Cpu, SensorType.Temperature, "Core Max")),
            ("Kern Ø", F(HardwareType.Cpu, SensorType.Temperature, "Core Average")),
            ("Board CPU", F(HardwareType.SuperIO, SensorType.Temperature, "CPU")),
            ("Last %", F(HardwareType.Cpu, SensorType.Load, "CPU Total")),
            ("GPU", F(HardwareType.GpuNvidia, SensorType.Temperature, "GPU Core")) };
        var data = src.Select(_ => new List<float>()).ToArray();
        W(string.Join(" | ", src.Select(x => x.label.PadLeft(9))));
        for (int i = 0; i < 60; i++)
        {
            Upd();
            for (int k = 0; k < src.Length; k++) data[k].Add(src[k].s?.Value ?? float.NaN);
            if (i % 2 == 0) W(string.Join(" | ", data.Select(d => d[^1].ToString("0.0").PadLeft(9))));
            Thread.Sleep(250);
        }
        W("");
        for (int k = 0; k < src.Length; k++)
        {
            var d = data[k]; if (d.Any(float.IsNaN)) { W($"{src[k].label}: nicht verfügbar"); continue; }
            var jumps = d.Zip(d.Skip(1), (x, y) => Math.Abs(y - x)).ToList();
            W($"{src[k].label,-9}: min {d.Min():0.0}  max {d.Max():0.0}  Spanne {d.Max() - d.Min():0.0}  größter Sprung in 250 ms {jumps.Max():0.0}");
        }
        c.Close();
    }
}
