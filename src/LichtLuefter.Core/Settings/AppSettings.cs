using System.Text.Json;
using System.Text.Json.Serialization;

namespace LichtLuefter.Core.Settings;

/// <summary>
/// Feste Sicherheitsgrenzen. Bewusst Konstanten – keine Einstellung und keine Profildatei kann sie unterschreiten.
/// </summary>
public static class Safety
{
    /// <summary>
    /// Mindest-Tastgrad je Lüfteranschluss, gemessen am 18.11.2026 (tools\Probe fansweep):
    ///  * CPU_FAN1 (Kanal 0): läuft stabil bis 15 % (311 U/min), steht bei 10 %, läuft aus dem Stand ab 20 % an → 20 %.
    ///  * SYS_FAN6 (Kanal 7): läuft bei 30 % (1196 U/min), steht schlagartig bei 25 %, läuft erst ab 30 % an → 35 %.
    ///  * Nicht gemessene Anschlüsse: vorsichtig 40 % (ein anderer Lüfter kann schon bei 30 % abreißen).
    /// Keine Einstellung und keine Profildatei kann diese Werte unterschreiten.
    /// </summary>
    public static double FanMinFor(int headerIndex) => headerIndex switch
    {
        0 => 20,
        7 => 35,
        _ => 40,
    };

    /// <summary>Kleinster Wert, den ein Kurvenpunkt für Lüfter überhaupt haben darf (pro Anschluss gilt zusätzlich <see cref="FanMinFor"/>).</summary>
    public const double FanMinPercent = 20;

    // Regelverhalten (aus Simulation auf gemessenen Temperaturverläufen, siehe README)
    public const double RampUpPerSecond = 4;
    public const double RampDownPerSecond = 1.5;
    /// <summary>Sollwertänderungen kleiner als dies werden ignoriert (verhindert hörbares Auf und Ab).</summary>
    public const double DeadbandPercent = 3;
    /// <summary>Mindest-Tastgrad der Pumpe. Gemessen: Light Loop erreicht bei ~79 % ihr Maximum (≈2850 U/min).</summary>
    public const double PumpMinPercent = 70;
    /// <summary>
    /// Ab hier (geglättete Temperatur) laufen alle geregelten Kanäle auf 100 %, unabhängig vom Profil.
    /// Zusätzlich sofort bei einem einzelnen Rohwert ab *RawC (kurz vor der Drosselgrenze des i7-14700KF bei 100 °C).
    /// Gemessen: „CPU Package“ springt schon bei 15 % Last um bis zu 22 °C in 250 ms – Rohwerte allein wären unbrauchbar.
    /// </summary>
    public const double CpuEmergencyC = 90;
    public const double CpuEmergencyRawC = 98;
    public const double GpuEmergencyC = 86;
    public const double GpuEmergencyRawC = 92;
    /// <summary>Glättung der Temperaturen je Regelzyklus (1 s): 0,2 ≈ Zeitkonstante 4,5 s.</summary>
    public const double TempSmoothing = 0.2;
    /// <summary>So lange darf ein Temperatursensor ausfallen, dann übernimmt das BIOS.</summary>
    public const int SensorLossSeconds = 5;
    /// <summary>Unterhalb dieser Drehzahl gilt die Pumpe als ausgefallen (BIOS übernimmt, Warnung).</summary>
    public const double PumpStallRpm = 1000;
    /// <summary>Unterhalb dieser Drehzahl gilt ein vorher drehender Lüfter als blockiert.</summary>
    public const double FanStallRpm = 150;
    public const int StallSeconds = 6;
    /// <summary>Watchdog: so lange ohne Lebenszeichen der App, dann zurück aufs BIOS.</summary>
    public const int HeartbeatTimeoutSeconds = 15;
    /// <summary>Header-Index der PUMP_FAN-Buchse. Gilt immer als Pumpe, egal was konfiguriert ist.</summary>
    public const int PumpHeaderIndex = 1;
}

public enum ChannelRole { Off, Fan, Pump }

public sealed class CurvePoint
{
    public double T { get; set; }
    public double P { get; set; }
    public CurvePoint() { }
    public CurvePoint(double t, double p) { T = t; P = p; }
}

public static class Curve
{
    public static double Eval(IReadOnlyList<CurvePoint> pts, double t)
    {
        if (pts.Count == 0) return 100;
        var s = pts.OrderBy(p => p.T).ToList();
        if (t <= s[0].T) return s[0].P;
        if (t >= s[^1].T) return s[^1].P;
        for (int i = 1; i < s.Count; i++)
        {
            if (t <= s[i].T)
            {
                var a = s[i - 1]; var b = s[i];
                double f = (t - a.T) / Math.Max(0.001, b.T - a.T);
                return a.P + (b.P - a.P) * f;
            }
        }
        return s[^1].P;
    }
}

public sealed class FanProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Lüfter nach CPU-Temperatur.</summary>
    public List<CurvePoint> FanCpu { get; set; } = new();
    /// <summary>Lüfter nach GPU-Temperatur. Es zählt der höhere der beiden Werte.</summary>
    public List<CurvePoint> FanGpu { get; set; } = new();
    /// <summary>Pumpe nach CPU-Temperatur (Untergrenze 70 % wird immer erzwungen).</summary>
    public List<CurvePoint> Pump { get; set; } = new();

    /// <summary>
    /// Standardkurven (Version 2). Die CPU-Achse ist der Kerndurchschnitt (nicht „Package“, siehe FanService).
    /// Werte unter dem Minimum eines Anschlusses werden dort angehoben (SYS_FAN6 läuft also nie unter 35 %).
    /// </summary>
    public static List<FanProfile> Defaults() =>
    [
        new()
        {
            Id = "quiet", Name = "Leise",
            FanCpu = [new(45, 20), new(62, 30), new(76, 55), new(88, 100)],
            FanGpu = [new(50, 20), new(65, 35), new(75, 60), new(83, 100)],
            Pump = [new(60, 70), new(80, 85), new(88, 100)],
        },
        new()
        {
            Id = "balanced", Name = "Ausgewogen",
            FanCpu = [new(40, 25), new(60, 45), new(75, 70), new(85, 100)],
            FanGpu = [new(45, 25), new(62, 45), new(72, 70), new(80, 100)],
            Pump = [new(50, 75), new(75, 90), new(85, 100)],
        },
        new()
        {
            Id = "performance", Name = "Leistung",
            FanCpu = [new(40, 50), new(60, 70), new(72, 90), new(80, 100)],
            FanGpu = [new(45, 50), new(60, 70), new(70, 90), new(78, 100)],
            Pump = [new(40, 100), new(90, 100)],
        },
    ];
}

public sealed class ChannelConfig
{
    public int Index { get; set; }
    public string Label { get; set; } = "";
    public ChannelRole Role { get; set; }
}

public sealed class FanSettings
{
    /// <summary>"bios" = keine Regelung durch die App, sonst Id eines Profils.</summary>
    public string ActiveProfile { get; set; } = "bios";
    public List<FanProfile> Profiles { get; set; } = FanProfile.Defaults();
    public List<ChannelConfig> Channels { get; set; } = new();
}

public enum LightEffect { Static, Breathing, ColorCycle, Rainbow, Temperature, Fire, Off }
public enum TempSource { Cpu, Gpu, Max }

public sealed class LightProfile
{
    public string Name { get; set; } = "Standard";
    public LightEffect Effect { get; set; } = LightEffect.Static;
    public string Color { get; set; } = "#3FA9F5";
    /// <summary>0 = langsam, 1 = mittel, 2 = schnell.</summary>
    public int Speed { get; set; } = 1;
    /// <summary>0–100.</summary>
    public int Brightness { get; set; } = 100;
    public TempSource TempSource { get; set; } = TempSource.Cpu;
    public string ColdColor { get; set; } = "#2F80ED";
    public string HotColor { get; set; } = "#EB3B2F";
    public double ColdC { get; set; } = 40;
    public double HotC { get; set; } = 80;

    public LightProfile Clone() => (LightProfile)MemberwiseClone();
}

public sealed class LightSettings
{
    public bool Enabled { get; set; } = true;
    public LightProfile Current { get; set; } = new();
    public List<LightProfile> Saved { get; set; } =
    [
        new() { Name = "Eisblau", Effect = LightEffect.Static, Color = "#3FA9F5" },
        new() { Name = "Ruhig atmen", Effect = LightEffect.Breathing, Color = "#8E6CF0", Speed = 0 },
        new() { Name = "Regenbogen", Effect = LightEffect.Rainbow, Speed = 1 },
        new() { Name = "Thermometer", Effect = LightEffect.Temperature, TempSource = TempSource.Max },
    ];
}

public sealed class GeneralSettings
{
    public bool StartOpenRgb { get; set; } = true;
    public string OpenRgbPath { get; set; } = @"D:\Program Files\OpenRGB\OpenRGB.exe";
    public string OpenRgbHost { get; set; } = "127.0.0.1";
    public int OpenRgbPort { get; set; } = 6742;
}

public sealed class AppSettings
{
    /// <summary>1 = erste Fassung, 2 = Minimum pro Anschluss, Kurven auf Kerndurchschnitt.</summary>
    public int Version { get; set; } = 2;
    public LightSettings Light { get; set; } = new();
    public FanSettings Fans { get; set; } = new();
    public GeneralSettings General { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

public sealed class SettingsStore
{
    public string Path { get; }
    private readonly object _lock = new();

    public SettingsStore(string path) { Path = path; }

    public AppSettings Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(Path))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), AppSettings.Json);
                    if (s != null) return Normalize(s);
                }
            }
            catch (Exception ex)
            {
                // Defekte Datei nicht überschreiben, sondern beiseitelegen.
                try { File.Copy(Path, Path + $".defekt-{DateTime.Now:yyyyMMdd-HHmmss}", true); } catch { }
                Log.Write("Einstellungen konnten nicht gelesen werden: " + ex.Message);
            }
            return Normalize(new AppSettings());
        }
    }

    public void Save(AppSettings s)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, AppSettings.Json));
            File.Move(tmp, Path, true);
        }
    }

    /// <summary>Kurven der Version 1 – nur um unveränderte alte Standardprofile zu erkennen und zu ersetzen.</summary>
    private static readonly Dictionary<string, (double[][] cpu, double[][] gpu, double[][] pump)> V1Defaults = new()
    {
        ["quiet"] = ([[45, 30], [65, 40], [78, 65], [88, 100]], [[50, 30], [65, 40], [75, 65], [83, 100]], [[55, 70], [80, 85], [88, 100]]),
        ["balanced"] = ([[40, 35], [60, 50], [75, 75], [85, 100]], [[45, 35], [62, 50], [72, 75], [80, 100]], [[50, 75], [75, 90], [85, 100]]),
    };

    private static bool Same(List<CurvePoint> a, double[][] b) => a.Count == b.Length && a.Zip(b).All(x => x.First.T == x.Second[0] && x.First.P == x.Second[1]);

    private static AppSettings Normalize(AppSettings s)
    {
        if (s.Version < 2 && s.Fans?.Profiles != null)
        {
            // Migration auf Version 2: nur unveränderte alte Standardkurven ersetzen, eigene Anpassungen bleiben.
            foreach (var d in FanProfile.Defaults())
            {
                int i = s.Fans.Profiles.FindIndex(p => p.Id == d.Id);
                if (i < 0 || !V1Defaults.TryGetValue(d.Id, out var old)) continue;
                var p = s.Fans.Profiles[i];
                if (Same(p.FanCpu, old.cpu) && Same(p.FanGpu, old.gpu) && Same(p.Pump, old.pump))
                {
                    s.Fans.Profiles[i] = d;
                    Log.Write($"Lüfterprofil „{d.Name}“ auf neue Standardkurven aktualisiert.");
                }
                else Log.Write($"Lüfterprofil „{p.Name}“ wurde angepasst – Kurven bleiben unverändert.");
            }
            s.Version = 2;
        }
        s.Light ??= new();
        s.Light.Current ??= new();
        s.Light.Saved ??= new();
        s.Fans ??= new();
        s.General ??= new();
        if (s.Fans.Profiles == null || s.Fans.Profiles.Count == 0) s.Fans.Profiles = FanProfile.Defaults();
        foreach (var d in FanProfile.Defaults())
            if (!s.Fans.Profiles.Any(p => p.Id == d.Id)) s.Fans.Profiles.Add(d);
        s.Fans.Channels ??= new();
        if (s.Fans.ActiveProfile != "bios" && !s.Fans.Profiles.Any(p => p.Id == s.Fans.ActiveProfile))
            s.Fans.ActiveProfile = "bios";
        return s;
    }
}
