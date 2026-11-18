using LibreHardwareMonitor.Hardware;

namespace LichtLuefter.Core.Hardware;

/// <summary>Ein Lüfter-/Pumpenanschluss des Mainboards.</summary>
public sealed class FanHeader
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public float? Rpm { get; set; }
    public float? DutyPercent { get; set; }
}

/// <summary>
/// Liest Temperaturen und Lüfter über LibreHardwareMonitorLib.
/// Aktiv: CPU, GPU, Mainboard (Super-I/O über LPC) und Laufwerke. Bewusst AUS: Speicher (SPD über SMBus –
/// würde mit OpenRGBs RAM-Beleuchtung auf demselben Bus konkurrieren), Controller, Netzwerk.
/// Alle Aufrufe müssen aus demselben Thread kommen – Ausnahme: die Laufwerke, die ausschließlich der
/// Laufwerksthread aktualisiert. Es darf nur EINE LHM-Instanz pro Prozess geben: LHM hält Mutexe und
/// Treiberzugang statisch, Close() einer zweiten Instanz würde sie auch dieser entziehen.
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMotherboardEnabled = true,
        IsMemoryEnabled = false,
        IsControllerEnabled = false,
        IsNetworkEnabled = false,
        // Laufwerke: aktiv, aber NUR vom eigenen Laufwerksthread aktualisiert (siehe UpdateRaw/ExtraMonitor).
        IsStorageEnabled = true,
        IsPsuEnabled = false,
        IsBatteryEnabled = false,
        IsPowerMonitorEnabled = false,
    };

    private ISensor? _cpuTemp;
    private ISensor? _cpuAvg;
    private ISensor? _gpuTemp;
    private IHardware? _superIoHw;
    private readonly Dictionary<int, ISensor> _fanSensors = new();
    private readonly Dictionary<int, ISensor> _controlSensors = new();

    public SuperIoAccess? SuperIo { get; private set; }
    public List<FanHeader> Headers { get; } = new();
    /// <summary>„CPU Package“ = momentan heißester Kern (springt stark, für Notfallgrenzen).</summary>
    public float? CpuTemp { get; private set; }
    /// <summary>Durchschnitt aller Kerne (ruhig, für Lüfterkurven). Fällt auf Package zurück, wenn nicht vorhanden.</summary>
    public float? CpuAvgTemp { get; private set; }
    public float? GpuTemp { get; private set; }
    public string CpuName { get; private set; } = "";
    public string GpuName { get; private set; } = "";
    public string BoardName { get; private set; } = "";
    public bool IsOpen { get; private set; }

    /// <summary>LHM-Hardwarebaum (nur im besitzenden Thread benutzen) – für den Hardwaremonitor.</summary>
    public IEnumerable<IHardware> Hardware => _computer.Hardware;

    /// <summary>Laufwerke (nur vom Laufwerksthread aktualisieren, siehe <see cref="ExtraMonitor"/>).</summary>
    public List<IHardware> StorageHardware { get; private set; } = new();

    public void Open()
    {
        _computer.Open();
        IsOpen = true;
        // Zweimal komplett aktualisieren (inkl. Sub-Hardware): LHM aktiviert Lüftersensoren erst,
        // nachdem sie einmal einen Wert geliefert haben.
        UpdateRaw();
        UpdateRaw();

        foreach (var hw in _computer.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    CpuName = hw.Name;
                    _cpuTemp = Pick(hw, SensorType.Temperature, "CPU Package", "Core Max", "Core Average");
                    _cpuAvg = hw.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature && x.Name == "Core Average");
                    break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel when GpuName == "":
                    if (hw.HardwareType == HardwareType.GpuIntel && _gpuTemp != null) break;
                    GpuName = hw.Name;
                    _gpuTemp = Pick(hw, SensorType.Temperature, "GPU Core", "GPU Hot Spot") ?? _gpuTemp;
                    break;
                case HardwareType.Motherboard:
                    BoardName = hw.Name;
                    foreach (var sub in hw.SubHardware)
                    {
                        if (sub.HardwareType != HardwareType.SuperIO) continue;
                        _superIoHw = sub;
                        foreach (var s in sub.Sensors)
                        {
                            if (s.SensorType == SensorType.Fan) _fanSensors[s.Index] = s;
                            if (s.SensorType == SensorType.Control) _controlSensors[s.Index] = s;
                        }
                    }
                    break;
            }
        }

        SuperIo = SuperIoAccess.Find(_computer);
        StorageHardware = _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage).ToList();
        foreach (var (idx, cs) in _controlSensors.OrderBy(k => k.Key))
            Headers.Add(new FanHeader { Index = idx, Name = cs.Name });
        Update();
    }

    private static ISensor? Pick(IHardware hw, SensorType type, params string[] names)
    {
        foreach (var n in names)
        {
            var s = hw.Sensors.FirstOrDefault(x => x.SensorType == type && x.Name == n);
            if (s != null) return s;
        }
        return hw.Sensors.FirstOrDefault(x => x.SensorType == type);
    }

    private void UpdateRaw()
    {
        foreach (var hw in _computer.Hardware)
        {
            // Laufwerke nie im Regelthread: ein Laufwerk im Standby kann die Abfrage für Sekunden blockieren.
            if (hw.HardwareType == HardwareType.Storage) continue;
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
        }
    }

    public void Update()
    {
        UpdateRaw();
        if (_superIoHw != null)
            foreach (var s in _superIoHw.Sensors)
                if (s.SensorType == SensorType.Fan) _fanSensors.TryAdd(s.Index, s);
        CpuTemp = Valid(_cpuTemp?.Value);
        CpuAvgTemp = _cpuAvg != null ? Valid(_cpuAvg.Value) : CpuTemp;
        GpuTemp = Valid(_gpuTemp?.Value);
        foreach (var h in Headers)
        {
            h.Rpm = _fanSensors.TryGetValue(h.Index, out var f) ? f.Value : null;
            h.DutyPercent = _controlSensors.TryGetValue(h.Index, out var c) ? c.Value : null;
        }
    }

    private static float? Valid(float? t) => t is > 0 and < 125 ? t : null;

    /// <summary>Alle Sensoren als Text (Diagnose).</summary>
    public string Dump()
    {
        var sb = new System.Text.StringBuilder();
        void Hw(IHardware hw, string ind)
        {
            sb.AppendLine($"{ind}{hw.HardwareType}: {hw.Name}");
            foreach (var s in hw.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
                sb.AppendLine($"{ind}   {s.SensorType,-12} #{s.Index,-2} {s.Name,-28} {s.Value?.ToString("0.0") ?? "-"}");
            foreach (var sub in hw.SubHardware) Hw(sub, ind + "   ");
        }
        foreach (var hw in _computer.Hardware) Hw(hw, "");
        return sb.ToString();
    }

    public void Dispose()
    {
        if (!IsOpen) return;
        IsOpen = false;
        // Computer.Close() ruft für alle Kanäle SetControl(null) = BIOS-Kurve (zusätzliche Absicherung).
        try { _computer.Close(); } catch { }
    }
}
