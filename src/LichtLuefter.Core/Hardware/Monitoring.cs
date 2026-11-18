using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace LichtLuefter.Core.Hardware;

/// <summary>Ein angezeigter Messwert mit Minimum/Maximum seit Start bzw. seit dem letzten Zurücksetzen.</summary>
public sealed record MonitorSensor(string Key, string Label, float? Value, float? Min, float? Max, string Unit, int Decimals);

/// <summary>Eine Gerätegruppe im Hardwaremonitor (CPU, GPU, Mainboard …).</summary>
public sealed record MonitorGroup(string Id, string Title, string Device, IReadOnlyList<MonitorSensor> Sensors);

/// <summary>Ein Zeitpunkt der Verlaufsdiagramme (Unix-Millisekunden, Werte je Reihenschlüssel).</summary>
public sealed record MonitorSample(long T, IReadOnlyDictionary<string, float?> V);

/// <summary>Merkt sich Minimum und Maximum je Schlüssel. Nur aus einem Thread benutzen.</summary>
public sealed class MinMaxTracker
{
    private readonly Dictionary<string, (float min, float max)> _mm = new();
    private volatile bool _reset;

    /// <summary>Aus jedem Thread aufrufbar; wird beim nächsten Erfassen ausgeführt.</summary>
    public void RequestReset() => _reset = true;

    public MonitorSensor Track(string key, string label, float? value, string unit, int decimals)
    {
        if (_reset) { _mm.Clear(); _reset = false; }
        if (value is float v && float.IsFinite(v))
        {
            _mm[key] = _mm.TryGetValue(key, out var m) ? (Math.Min(m.min, v), Math.Max(m.max, v)) : (v, v);
        }
        else value = null;
        var have = _mm.TryGetValue(key, out var r);
        return new MonitorSensor(key, label, value, have ? r.min : null, have ? r.max : null, unit, decimals);
    }
}

/// <summary>
/// Stellt aus den LHM-Sensoren die Gruppen für den Hardwaremonitor zusammen (kuratiert, deutsch beschriftet).
/// Läuft im Thread, dem die jeweilige LHM-Instanz gehört.
/// </summary>
public static class MonitorBuilder
{
    private static ISensor? Find(IHardware hw, SensorType t, string name) =>
        hw.Sensors.FirstOrDefault(s => s.SensorType == t && s.Name == name);

    private static float? Max(IHardware hw, SensorType t, string prefix)
    {
        float? m = null;
        foreach (var s in hw.Sensors)
            if (s.SensorType == t && s.Name.StartsWith(prefix, StringComparison.Ordinal) && s.Value is float v)
                m = m is float x ? Math.Max(x, v) : v;
        return m;
    }

    /// <summary>Temperaturen ≤ 0 °C oder unplausibel hoch sind „kein Sensor angeschlossen“.</summary>
    private static float? Temp(ISensor? s) => s?.Value is float v && v > 0 && v < 150 ? v : null;

    public static List<MonitorGroup> Build(IEnumerable<IHardware> hardware, IReadOnlyList<FanHeader> headers,
        Func<int, string> headerLabel, MinMaxTracker mm)
    {
        var groups = new List<MonitorGroup>();
        foreach (var hw in hardware)
        {
            var list = new List<MonitorSensor>();
            void Add(string key, string label, float? v, string unit, int dec) => list.Add(mm.Track(key, label, v, unit, dec));
            void AddIf(string key, string label, ISensor? s, string unit, int dec, bool temp = false)
            {
                if (s == null) return;
                list.Add(mm.Track(key, label, temp ? Temp(s) : s.Value, unit, dec));
            }

            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    AddIf("cpu.avg", "Kerne Durchschnitt", Find(hw, SensorType.Temperature, "Core Average"), "°C", 0, true);
                    AddIf("cpu.pkg", "Heißester Kern (Package)", Find(hw, SensorType.Temperature, "CPU Package"), "°C", 0, true);
                    AddIf("cpu.load", "Auslastung", Find(hw, SensorType.Load, "CPU Total"), "%", 0);
                    AddIf("cpu.loadmax", "Auslastung stärkster Kern", Find(hw, SensorType.Load, "CPU Core Max"), "%", 0);
                    AddIf("cpu.power", "Leistungsaufnahme", Find(hw, SensorType.Power, "CPU Package"), "W", 0);
                    if (Max(hw, SensorType.Clock, "P-Core") is float pc) Add("cpu.pclk", "Takt P-Kerne (höchster)", pc, "MHz", 0);
                    if (Max(hw, SensorType.Clock, "E-Core") is float ec) Add("cpu.eclk", "Takt E-Kerne (höchster)", ec, "MHz", 0);
                    AddIf("cpu.vcore", "Kernspannung", Find(hw, SensorType.Voltage, "CPU Core"), "V", 3);
                    groups.Add(new MonitorGroup("cpu", "CPU", hw.Name, list));
                    break;

                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                    AddIf("gpu.temp", "Chip", Find(hw, SensorType.Temperature, "GPU Core"), "°C", 0, true);
                    AddIf("gpu.hot", "Hotspot", Find(hw, SensorType.Temperature, "GPU Hot Spot"), "°C", 0, true);
                    AddIf("gpu.mem", "Speicher (Junction)", Find(hw, SensorType.Temperature, "GPU Memory Junction"), "°C", 0, true);
                    AddIf("gpu.load", "Auslastung", Find(hw, SensorType.Load, "GPU Core"), "%", 0);
                    AddIf("gpu.memload", "Auslastung Speichercontroller", Find(hw, SensorType.Load, "GPU Memory Controller"), "%", 0);
                    AddIf("gpu.power", "Leistungsaufnahme", Find(hw, SensorType.Power, "GPU Package"), "W", 0);
                    AddIf("gpu.clk", "Takt Chip", Find(hw, SensorType.Clock, "GPU Core"), "MHz", 0);
                    AddIf("gpu.memclk", "Takt Speicher", Find(hw, SensorType.Clock, "GPU Memory"), "MHz", 0);
                    var used = Find(hw, SensorType.SmallData, "GPU Memory Used");
                    var total = Find(hw, SensorType.SmallData, "GPU Memory Total");
                    if (used != null)
                        list.Add(mm.Track("gpu.vram", total?.Value is float t ? $"Grafikspeicher belegt (von {t / 1024:0} GB)" : "Grafikspeicher belegt",
                            used.Value / 1024f, "GB", 1));
                    foreach (var f in hw.Sensors.Where(s => s.SensorType == SensorType.Fan).OrderBy(s => s.Index))
                        list.Add(mm.Track($"gpu.fan{f.Index}", f.Name.Replace("GPU Fan", "Lüfter"), f.Value, "U/min", 0));
                    groups.Add(new MonitorGroup("gpu", "GPU", hw.Name, list));
                    break;

                case HardwareType.Motherboard:
                    foreach (var sub in hw.SubHardware.Where(s => s.HardwareType == HardwareType.SuperIO))
                    {
                        foreach (var (name, label) in new[] { ("CPU", "CPU (Mainboard-Sensor)"), ("System", "System"), ("VRM MOS", "Spannungswandler (VRM)"),
                                     ("PCH", "Chipsatz (PCH)"), ("CPU Socket", "CPU-Sockel"), ("M2 #1", "M.2-Steckplatz 1") })
                        {
                            var s = Find(sub, SensorType.Temperature, name);
                            if (Temp(s) != null) list.Add(mm.Track($"mb.t.{name}", label, Temp(s), "°C", 0));
                        }
                        foreach (var (name, label) in new[] { ("+12V", "+12 V"), ("+5V", "+5 V"), ("AVCC3", "+3,3 V"), ("Vcore", "Vcore"), ("DIMM", "Arbeitsspeicher (DIMM)"), ("CMOS Battery", "CMOS-Batterie") })
                            AddIf($"mb.v.{name}", label, Find(sub, SensorType.Voltage, name), "V", name is "+12V" or "+5V" or "AVCC3" or "CMOS Battery" ? 2 : 3);
                    }
                    groups.Add(new MonitorGroup("board", "Mainboard", hw.Name, list));

                    var fans = new List<MonitorSensor>();
                    foreach (var h in headers.Where(h => h.Rpm is > 0))
                    {
                        fans.Add(mm.Track($"fan.rpm{h.Index}", headerLabel(h.Index), h.Rpm, "U/min", 0));
                        fans.Add(mm.Track($"fan.duty{h.Index}", $"{headerLabel(h.Index)} Ansteuerung", h.DutyPercent, "%", 0));
                    }
                    groups.Add(new MonitorGroup("fans", "Lüfter und Pumpe", "Mainboard-Anschlüsse", fans));
                    break;
            }
        }
        // Reihenfolge fest: CPU, GPU, Lüfter, Mainboard
        string[] order = ["cpu", "gpu", "fans", "board"];
        return groups.OrderBy(g => Array.IndexOf(order, g.Id) is var i && i < 0 ? 99 : i).ToList();
    }

    /// <summary>Werte für die Verlaufsdiagramme (Schlüssel wie in ui/app.js).</summary>
    public static Dictionary<string, float?> Sample(IReadOnlyList<MonitorGroup> groups)
    {
        float? V(string key) => groups.SelectMany(g => g.Sensors).FirstOrDefault(s => s.Key == key)?.Value;
        var d = new Dictionary<string, float?>
        {
            ["cpuAvg"] = V("cpu.avg"), ["cpuPkg"] = V("cpu.pkg"), ["gpu"] = V("gpu.temp"),
            ["cpuLoad"] = V("cpu.load"), ["gpuLoad"] = V("gpu.load"),
            ["cpuPower"] = V("cpu.power"), ["gpuPower"] = V("gpu.power"),
        };
        foreach (var s in groups.FirstOrDefault(g => g.Id == "fans")?.Sensors ?? [])
            if (s.Key.StartsWith("fan.rpm", StringComparison.Ordinal)) d["fan" + s.Key["fan.rpm".Length..]] = s.Value;
        return d;
    }
}

/// <summary>
/// Laufwerke und Arbeitsspeicher – bewusst in einem EIGENEN Thread, alle 5 s. Die Laufwerke stammen aus der
/// EINEN LHM-Instanz der App (HardwareMonitor), werden aber nur hier aktualisiert. Keine zweite LHM-Instanz:
/// LHM schließt beim Close() Mutexe und Treiberzugang statisch für den ganzen Prozess.
/// Ein Laufwerk, das aus dem Standby anläuft, kann eine Abfrage für Sekunden blockieren; das darf die
/// Lüfterregelung (und ihr Lebenszeichen für den Watchdog) nie aufhalten.
/// Arbeitsspeicher kommt von Windows (GlobalMemoryStatusEx), NICHT von LHM: dessen Speichermodul liest SPD
/// über den SMBus, auf dem OpenRGB die RAM-Beleuchtung steuert.
/// </summary>
public sealed class ExtraMonitor : IDisposable
{
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly MinMaxTracker _mm = new();
    private bool _logged;
    private readonly Func<IReadOnlyList<IHardware>> _storage;

    public IReadOnlyList<MonitorGroup> Groups { get; private set; } = [];

    public ExtraMonitor(Func<IReadOnlyList<IHardware>> storage)
    {
        _storage = storage;
        _thread = new Thread(Run) { IsBackground = true, Name = "Monitor Laufwerke", Priority = ThreadPriority.BelowNormal };
    }

    public void Start() => _thread.Start();
    public void ResetMinMax() => _mm.RequestReset();

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    private void Run()
    {
        while (!_stop)
        {
            try
            {
                var groups = new List<MonitorGroup>();
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref m))
                {
                    float total = m.ullTotalPhys / 1073741824f, used = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824f;
                    groups.Add(new MonitorGroup("ram", "Arbeitsspeicher", $"{total:0} GB installiert nutzbar", [
                        _mm.Track("ram.used", "Belegt", used, "GB", 1),
                        _mm.Track("ram.load", "Auslastung", m.dwMemoryLoad, "%", 0),
                    ]));
                }

                int n = 0;
                foreach (var hw in _storage())
                    {
                        hw.Update();
                        n++;
                        var list = new List<MonitorSensor>();
                        // Nur Messsensoren („Temperature“, „Temperature #n“). LHM führt auch die Grenzwerte des Laufwerks
                        // („Warning/Critical Temperature“) als Temperatur – die sind keine Messung (gemessen: SN850X 89/93 °C).
                        foreach (var t in hw.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 150
                                                               && (s.Name == "Temperature" || s.Name.StartsWith("Temperature #", StringComparison.Ordinal))).OrderBy(s => s.Index))
                            list.Add(_mm.Track($"disk{n}.t{t.Index}", t.Name == "Temperature" ? "Temperatur" : "Temperatursensor " + t.Name["Temperature #".Length..], t.Value, "°C", 0));
                        var warn = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name == "Warning Temperature")?.Value;
                        if (hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name == "Used Space") is { } us)
                            list.Add(_mm.Track($"disk{n}.used", "Belegt", us.Value, "%", 0));
                        foreach (var (name, label) in new[] { ("Read Rate", "Lesen"), ("Write Rate", "Schreiben") })
                            if (hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Throughput && s.Name == name) is { } r)
                                list.Add(_mm.Track($"disk{n}.{name}", label, r.Value / 1048576f, "MB/s", 1));
                        if (list.Count > 0) groups.Add(new MonitorGroup($"disk{n}", "Laufwerk", warn is > 0 and < 150 ? $"{hw.Name}, Warngrenze {warn:0} °C" : hw.Name, list));
                    }
                Groups = groups;
                if (!_logged && n > 0 && groups.Any(g => g.Id.StartsWith("disk")))
                {
                    _logged = true;
                    Log.Write("Hardwaremonitor Laufwerke/RAM: " + string.Join(" | ", groups.Select(g => $"{g.Device}: " + string.Join(", ", g.Sensors.Select(x => $"{x.Label}={x.Value?.ToString("0.#") ?? "-"} {x.Unit}")))));
                }
            }
            catch (Exception ex) { Log.Write("Laufwerkssensoren: " + ex.Message); }

            for (int i = 0; i < 50 && !_stop; i++) Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(3000);
    }
}
