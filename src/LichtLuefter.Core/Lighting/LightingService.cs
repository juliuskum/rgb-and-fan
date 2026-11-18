using LichtLuefter.Core.OpenRgb;
using LichtLuefter.Core.Settings;

namespace LichtLuefter.Core.Lighting;

public sealed record LightDeviceInfo(string Name, string Kind, string Mode);
public sealed record LightSnapshot(bool Connected, string Message, IReadOnlyList<LightDeviceInfo> Devices, string PreviewColor);

/// <summary>
/// Beleuchtung über den OpenRGB-SDK-Server.
///
/// Gerätespezifische Regeln (aus den Tests auf diesem PC):
///  * Mainboard (MSI Mystic Light, steuert Light-Wings-Hub an JRAINBOW3 und Light-Loop-Kühlkopf an JRAINBOW2):
///    ausschließlich Hardware-Modi auf das gesamte Gerät. Direct ist kaputt und wird nie gesendet.
///  * RAM (Corsair Vengeance RGB DDR5): ausschließlich Direct mit Software-Effekten. Die Hardware-Modi der Module
///    speichern bei jedem Wechsel automatisch in den Modul-Flash (AutomaticSave) – das wird vermieden.
///  * Kein "Apply all devices", kein SaveMode, keine Profil- oder Einstellungsbefehle an OpenRGB.
/// </summary>
public sealed class LightingService : IDisposable
{
    private readonly Func<GeneralSettings> _general;
    private readonly Func<(float? cpu, float? gpu)> _temps;
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile LightSettings _settings;
    private volatile bool _dirty = true;

    private OpenRgbClient? _client;
    private RgbDevice? _board;
    private readonly List<RgbDevice> _rams = new();
    private DateTime _nextConnect = DateTime.MinValue;
    private RgbColor? _lastBoardTempColor;
    private DateTime _lastBoardTempWrite = DateTime.MinValue;
    private readonly Random _rng = new();
    private double[] _fire = new double[10];

    public LightSnapshot Snapshot { get; private set; } = new(false, "Verbinde mit OpenRGB …", [], "#000000");
    public event Action<LightSnapshot>? Updated;

    /// <summary>Wird aufgerufen, wenn OpenRGB nicht erreichbar ist (die App kann es dann starten).</summary>
    public Action? OnServerUnreachable { get; set; }

    public LightingService(LightSettings initial, Func<GeneralSettings> general, Func<(float? cpu, float? gpu)> temps)
    {
        _settings = initial;
        _general = general;
        _temps = temps;
        _thread = new Thread(Run) { IsBackground = true, Name = "Licht" };
    }

    public void Start() => _thread.Start();

    public void Apply(LightSettings s)
    {
        _settings = s;
        _dirty = true;
    }

    /// <summary>Nach Ruhezustand oder OpenRGB-Neustart alles neu senden.</summary>
    public void Reapply() { _dirty = true; _nextConnect = DateTime.MinValue; }

    private void Run()
    {
        var start = DateTime.UtcNow;
        while (!_stop)
        {
            int sleep = 40; // 25 Bilder/s für RAM-Effekte
            try
            {
                if (_client?.IsConnected != true)
                {
                    sleep = 500;
                    if (DateTime.UtcNow >= _nextConnect) TryConnect();
                }
                else
                {
                    _client.Poll();
                    // OpenRGB schickt "Geräteliste geändert" auch nach jedem Moduswechsel am MSI-Board (gemessen:
                    // 0,5 s und 1,1 s danach), obwohl sich an den Geräten nichts ändert. Würde die App darauf neu senden,
                    // entstünde eine Schleife, die animierte Hardware-Effekte jede Sekunde neu startet. Deshalb: abwarten,
                    // bis die Meldungen 1,5 s ruhen, neu einlesen, und nur bei tatsächlich anderen Geräten neu senden.
                    //
                    // Zusätzlich trennt sich das MSI-Board ~0,2 s nach einem Wechsel auf „Rainbow wave“ oder „Fire“ für
                    // ~0,5 s vom USB (OpenRGB-Log: HID disconnected/Unregistering/Registering). Befehle in diesem Fenster
                    // gehen verloren. Darum wird nach jeder Ruhephase der tatsächliche Board-Zustand geprüft (VerifyBoard).
                    if (_client.DeviceListChanged) { _client.DeviceListChanged = false; _deviceListDueAt = DateTime.UtcNow.AddSeconds(1.5); }
                    if (_deviceListDueAt != DateTime.MinValue && DateTime.UtcNow >= _deviceListDueAt)
                    {
                        _deviceListDueAt = DateTime.MinValue;
                        var before = DeviceSignature();
                        Enumerate();
                        if (DeviceSignature() != before) { Log.Write("OpenRGB: Geräte haben sich geändert – sende Beleuchtung neu."); _dirty = true; }
                        else VerifyBoard();
                    }
                    else if (_verifyAt != DateTime.MinValue && DateTime.UtcNow >= _verifyAt && _deviceListDueAt == DateTime.MinValue)
                    {
                        Enumerate();
                        VerifyBoard();
                    }
                    var s = _settings;
                    double t = (DateTime.UtcNow - start).TotalSeconds;
                    if (_dirty) { _dirty = false; ApplyBoard(s, t); _lastRamFrames.Clear(); }
                    RenderFrame(s, t);
                    if (!Animated(s)) sleep = 250;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Licht: " + ex.Message);
                SetStatus(false, "Verbindung zu OpenRGB verloren – neuer Versuch …");
                try { _client?.Dispose(); } catch { }
                _client = null;
                _nextConnect = DateTime.UtcNow.AddSeconds(3);
            }
            Thread.Sleep(sleep);
        }
    }

    private DateTime _deviceListDueAt = DateTime.MinValue;

    /// <summary>Welche Geräte (Index, Typ, Name, Ort, LED-Zahl) gerade da sind – ohne Farben/aktiven Modus.</summary>
    private string DeviceSignature() =>
        string.Join("|", new[] { _board }.Concat(_rams).Where(d => d != null)
            .Select(d => $"{d!.Index}:{d.Type}:{d.Name}:{d.Location}:{d.Leds.Count}"));

    // Temperatur-Effekt wird laufend nachgeführt (sanfter Übergang) und zählt deshalb als "animiert".
    private static bool Animated(LightSettings s) => s.Enabled && s.Current.Effect is LightEffect.Breathing or LightEffect.ColorCycle or LightEffect.Rainbow or LightEffect.Fire or LightEffect.Temperature;

    private void TryConnect()
    {
        var g = _general();
        try
        {
            var c = new OpenRgbClient();
            c.Connect(g.OpenRgbHost, g.OpenRgbPort);
            _client = c;
            Enumerate();
            _dirty = true;
            Log.Write($"OpenRGB verbunden (Protokoll {c.ProtocolVersion}/{c.ServerProtocolVersion}). Board: {_board?.Name ?? "-"}, RAM-Module: {_rams.Count}");
        }
        catch (Exception ex)
        {
            _client = null;
            _nextConnect = DateTime.UtcNow.AddSeconds(3);
            SetStatus(false, $"OpenRGB-SDK-Server nicht erreichbar ({g.OpenRgbHost}:{g.OpenRgbPort}).");
            if (ex is IOException) OnServerUnreachable?.Invoke();
        }
    }

    private void Enumerate()
    {
        var all = _client!.GetAllControllers();
        _board = all.FirstOrDefault(d => d.Type == DeviceType.Motherboard);
        _rams.Clear();
        _rams.AddRange(all.Where(d => d.Type == DeviceType.Dram));
    }

    // ------------------------------------------------------------------ Mainboard (nur Hardware-Modi)

    // Auf diesem Board getestet: „Color shift“ bleibt bei einer Farbe stehen, „Fire“ kennt in OpenRGB nur zufällige
    // Farben (zeigt Regenbogen). Farbwechsel und Feuer laufen am Board daher im Hardware-Modus „Static“; die App
    // führt die Farbe selbst nach (wie bei Temperatur). Kein Direct.
    private static readonly Dictionary<LightEffect, string[]> BoardModeNames = new()
    {
        [LightEffect.Static] = ["Static"],
        [LightEffect.Temperature] = ["Static"],
        [LightEffect.ColorCycle] = ["Static"],
        [LightEffect.Fire] = ["Static"],
        [LightEffect.Off] = ["Static"],
        [LightEffect.Breathing] = ["Breathing"],
        [LightEffect.Rainbow] = ["Rainbow wave", "Rainbow Wave", "Rainbow"],
    };

    /// <summary>Effekte, bei denen die App die Board-Farbe laufend selbst setzt (Hardware-Modus Static).</summary>
    private static bool BoardSoftware(LightEffect e) => e is LightEffect.Temperature or LightEffect.ColorCycle or LightEffect.Fire;

    /// <summary>Farbwechsel in Grad Farbton pro Sekunde (langsam/mittel/schnell) – Board und RAM synchron.</summary>
    private static readonly double[] CycleDegPerSec = [6, 15, 36];

    private string _boardModeName = "-";
    private bool _boardModeSpecific;

    // Nachprüfen: was die App zuletzt ans Board geschickt hat.
    private int _intendedMode = -1;
    private RgbColor? _intendedColor;      // nur bei festen Per-LED-Farben (Statisch/Atmen/Aus)
    private DateTime _verifyAt = DateTime.MinValue;
    private int _verifyTries;
    private bool _userChange = true;

    /// <summary>
    /// Vergleicht den von OpenRGB gemeldeten Board-Zustand mit dem gesendeten. Bei Abweichung (z. B. Befehl ging
    /// während der USB-Neuanmeldung des Boards verloren) bis zu 3× neu senden.
    /// </summary>
    private void VerifyBoard()
    {
        if (_verifyAt == DateTime.MinValue || _board == null || _intendedMode < 0) { _verifyAt = DateTime.MinValue; return; }
        bool modeOk = _board.ActiveMode == _intendedMode;
        bool colorOk = _intendedColor is not RgbColor want || _board.Colors.Count == 0
                       || _board.Colors.All(c => Math.Abs(c.R - want.R) + Math.Abs(c.G - want.G) + Math.Abs(c.B - want.B) <= 6);
        if (modeOk && colorOk)
        {
            if (_verifyTries > 0) Log.Write($"Board: Zustand nach {_verifyTries} Wiederholung(en) bestätigt.");
            _verifyAt = DateTime.MinValue;
            _verifyTries = 0;
            return;
        }
        string got = _board.ActiveMode >= 0 && _board.ActiveMode < _board.Modes.Count ? _board.Modes[_board.ActiveMode].Name : "?";
        if (++_verifyTries > 3)
        {
            Log.Write($"Board: meldet weiterhin {got} statt {_board.Modes[_intendedMode].Name} – gebe nach 3 Wiederholungen auf.");
            _verifyAt = DateTime.MinValue;
            _verifyTries = 0;
            return;
        }
        Log.Write($"Board: meldet {got}{(colorOk ? "" : " mit anderer Farbe")} statt {_board.Modes[_intendedMode].Name} – sende erneut ({_verifyTries}/3).");
        _userChange = false;
        _dirty = true;
    }

    private void ApplyBoard(LightSettings s, double t)
    {
        if (_board == null) { PublishDevices(); return; }
        var p = s.Current;
        var effect = s.Enabled ? p.Effect : LightEffect.Off;

        int idx = -1;
        foreach (var n in BoardModeNames[effect]) { idx = _board.FindMode(n); if (idx >= 0) break; }
        if (idx < 0) idx = _board.FindMode("Static");
        if (idx < 0) { Log.Write("Board: kein passender Hardware-Modus gefunden."); return; }

        var mode = _board.Modes[idx].Clone();
        // Harte Sperre: Direct (und Custom) niemals ans Mainboard senden.
        if (mode.Name.Equals("Direct", StringComparison.OrdinalIgnoreCase) || mode.Name.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Direct-Modus am Mainboard ist gesperrt.");

        // Bereiche können in OpenRGB umgekehrt sein (min > max) – daher in double rechnen, nicht in uint.
        static uint Scale(uint min, uint max, double f) => (uint)Math.Round(Math.Clamp(min + ((double)max - min) * f, Math.Min(min, max), Math.Max(min, max)));
        if (mode.Has(ModeFlags.HasBrightness) && mode.BrightnessMax != mode.BrightnessMin)
            mode.Brightness = Scale(mode.BrightnessMin, mode.BrightnessMax, (effect == LightEffect.Off ? 0 : Math.Clamp(p.Brightness, 0, 100)) / 100.0);
        if (mode.Has(ModeFlags.HasSpeed))
            mode.Speed = Scale(mode.SpeedMin, mode.SpeedMax, Math.Clamp(p.Speed, 0, 2) / 2.0);

        var color = effect switch
        {
            LightEffect.Off => new RgbColor(0, 0, 0),
            _ when BoardSoftware(effect) => BoardSoftwareColor(p, effect, t),
            _ => RgbColor.FromHex(p.Color),
        };
        _boardModeSpecific = mode.Has(ModeFlags.HasModeSpecificColor) && mode.ColorsMax > 0;
        if (_boardModeSpecific)
        {
            mode.ColorMode = ColorMode.ModeSpecific;
            mode.Colors = Enumerable.Repeat(color, (int)Math.Max(1, mode.ColorsMin)).ToList();
        }

        _client!.UpdateMode(_board.Index, idx, mode);
        _boardModeName = BoardSoftware(effect) ? $"{mode.Name}, Farbe von der App nachgeführt" : mode.Name;
        _lastBoardTempColor = color;
        _lastBoardTempWrite = DateTime.UtcNow;

        if (mode.ColorMode == ColorMode.PerLed)
            _client.UpdateLeds(_board.Index, Enumerable.Repeat(color, _board.Leds.Count).ToArray());

        // Nach dem Senden nachprüfen (nach einer Ruhephase von OpenRGB).
        _intendedMode = idx;
        _intendedColor = mode.ColorMode == ColorMode.PerLed && !BoardSoftware(effect) ? color : null;
        _verifyAt = DateTime.UtcNow.AddSeconds(2);
        if (_userChange) _verifyTries = 0;
        _userChange = true;

        Log.Write($"Board: Modus {mode.Name} (Helligkeit {mode.Brightness}, Tempo {mode.Speed}){(BoardSoftware(effect) ? ", Farbe von der App nachgeführt" : "")}.");
        PublishDevices();
    }

    /// <summary>Board-Farbe für die Effekte, die die App selbst nachführt.</summary>
    private RgbColor BoardSoftwareColor(LightProfile p, LightEffect effect, double t) => effect switch
    {
        LightEffect.Temperature => TempColor(p),
        LightEffect.ColorCycle => RgbColor.FromHsv(t * CycleDegPerSec[Math.Clamp(p.Speed, 0, 2)], 1, 1),
        LightEffect.Fire => EmberColor(p, t),
        _ => RgbColor.FromHex(p.Color),
    };

    private double _ember = 0.5, _emberTarget = 0.5;
    private DateTime _emberNext = DateTime.MinValue;

    /// <summary>Glut fürs Board: wandert langsam und zufällig zwischen tiefem Rot und Orange (2 Schritte/s).</summary>
    private RgbColor EmberColor(LightProfile p, double t)
    {
        var now = DateTime.UtcNow;
        if (now >= _emberNext) { _emberTarget = 0.15 + _rng.NextDouble() * 0.85; _emberNext = now.AddSeconds(1.2 - 0.35 * Math.Clamp(p.Speed, 0, 2)); }
        _ember += (_emberTarget - _ember) * 0.35;
        return RgbColor.Lerp(new RgbColor(200, 20, 0), new RgbColor(255, 110, 0), _ember);
    }

    /// <summary>Für Temperatur/Farbwechsel/Feuer: neue Board-Farbe senden (Hardware-Modus bleibt Static).</summary>
    private void UpdateBoardSoftware(LightProfile p, LightEffect effect, double t)
    {
        if (_board == null) return;
        var c = BoardSoftwareColor(p, effect, t);
        // Temperatur: höchstens 1×/s (sie ändert sich ohnehin langsam). Farbwechsel/Feuer: 2×/s.
        // Kleine Schritte – das Board blendet so sichtbar weich über. Schnellere Updates sind auf diesem Board ungeprüft.
        double minInterval = effect == LightEffect.Temperature ? 1.0 : 0.5;
        bool changed = _lastBoardTempColor is not RgbColor last || Math.Abs(last.R - c.R) + Math.Abs(last.G - c.G) + Math.Abs(last.B - c.B) > 3;
        if (!changed || (DateTime.UtcNow - _lastBoardTempWrite).TotalSeconds < minInterval) return;
        // Modi mit modusspezifischer Farbe ignorieren LED-Updates: dann den Modus mit neuer Farbe neu senden.
        if (_boardModeSpecific) { _dirty = true; _lastBoardTempWrite = DateTime.UtcNow; return; }
        _client!.UpdateLeds(_board.Index, Enumerable.Repeat(c, _board.Leds.Count).ToArray());
        _lastBoardTempColor = c;
        _lastBoardTempWrite = DateTime.UtcNow;
    }

    // ------------------------------------------------------------------ RAM (nur Direct)

    private void EnsureRamDirect(RgbDevice ram)
    {
        int direct = ram.FindMode("Direct");
        if (direct < 0) return;
        if (ram.ActiveMode != direct)
        {
            _client!.UpdateMode(ram.Index, direct, ram.Modes[direct]);
            ram.ActiveMode = direct;
        }
    }

    private void RenderFrame(LightSettings s, double t)
    {
        var p = s.Current;
        var effect = s.Enabled ? p.Effect : LightEffect.Off;
        double bright = Math.Clamp(p.Brightness, 0, 100) / 100.0;
        int[] periods = [7, 4, 2];
        double speed = Math.Clamp(p.Speed, 0, 2);
        var baseColor = RgbColor.FromHex(p.Color);

        if (effect == LightEffect.Temperature) StepTemperature(p);
        if (BoardSoftware(effect)) UpdateBoardSoftware(p, effect, t);

        RgbColor[] Frame(int n, int module)
        {
            var f = new RgbColor[n];
            for (int i = 0; i < n; i++)
            {
                f[i] = effect switch
                {
                    LightEffect.Off => new RgbColor(0, 0, 0),
                    LightEffect.Static => baseColor.Scale(bright),
                    LightEffect.Temperature => TempColor(p).Scale(bright),
                    LightEffect.Breathing => baseColor.Scale(bright * Math.Max(0.02, Math.Pow((1 - Math.Cos(2 * Math.PI * t / periods[(int)speed])) / 2, 1.6))),
                    LightEffect.ColorCycle => RgbColor.FromHsv(t * CycleDegPerSec[(int)speed], 1, bright),
                    LightEffect.Rainbow => RgbColor.FromHsv(t * (new[] { 30.0, 70, 140 })[(int)speed] + i * 360.0 / n + module * 18, 1, bright),
                    LightEffect.Fire => FireColor(i, bright),
                    _ => baseColor,
                };
            }
            return f;
        }

        if (effect == LightEffect.Fire) StepFire(speed);

        for (int m = 0; m < _rams.Count; m++)
        {
            var ram = _rams[m];
            EnsureRamDirect(ram);
            var frame = Frame(ram.Leds.Count, m);
            // Nur senden, wenn sich das Bild dieses Moduls ändert (statisch/Temperatur erzeugen so kaum SMBus-Verkehr).
            if (_lastRamFrames.TryGetValue(ram.Index, out var last) && last.SequenceEqual(frame)) continue;
            _client!.UpdateLeds(ram.Index, frame);
            _lastRamFrames[ram.Index] = frame;
        }

        var preview = Frame(1, 0)[0];
        if (Snapshot.PreviewColor != preview.ToHex() && (DateTime.UtcNow - _lastPreview).TotalMilliseconds > 400)
        {
            _lastPreview = DateTime.UtcNow;
            Snapshot = Snapshot with { PreviewColor = preview.ToHex(), Connected = true };
        }
    }

    private DateTime _lastPreview = DateTime.MinValue;

    private void StepFire(double speed)
    {
        if (_fire.Length != 10) _fire = new double[10];
        double k = 0.15 + 0.12 * speed;
        for (int i = 0; i < _fire.Length; i++)
            _fire[i] += (_rng.NextDouble() - _fire[i]) * k;
    }

    private RgbColor FireColor(int i, double bright)
    {
        double heat = _fire[i % _fire.Length];
        // dunkles Rot -> Orange -> Gelb
        var c = heat < 0.5
            ? RgbColor.Lerp(new RgbColor(120, 8, 0), new RgbColor(255, 70, 0), heat * 2)
            : RgbColor.Lerp(new RgbColor(255, 70, 0), new RgbColor(255, 170, 20), (heat - 0.5) * 2);
        return c.Scale(bright * (0.55 + 0.45 * heat));
    }

    private readonly Dictionary<int, RgbColor[]> _lastRamFrames = new();
    private double? _tempShown;
    private DateTime _tempLast = DateTime.MinValue;
    /// <summary>Zeitkonstante, mit der die Lichtfarbe der Temperatur folgt (zusätzlich zur Glättung der Sensorwerte).</summary>
    private const double TempFadeSeconds = 6;

    private double? SourceTemp(LightProfile p)
    {
        var (cpu, gpu) = _temps();
        return p.TempSource switch
        {
            TempSource.Cpu => cpu,
            TempSource.Gpu => gpu,
            _ => Math.Max(cpu ?? 0, gpu ?? 0) is var m && m > 0 ? m : null,
        };
    }

    /// <summary>Führt die angezeigte Temperatur weich nach (exponentiell, unabhängig von der Bildrate).</summary>
    private void StepTemperature(LightProfile p)
    {
        if (SourceTemp(p) is not double raw) return;
        var now = DateTime.UtcNow;
        double dt = _tempLast == DateTime.MinValue ? double.MaxValue : (now - _tempLast).TotalSeconds;
        _tempLast = now;
        _tempShown = _tempShown is double shown && dt < 5 ? shown + (raw - shown) * (1 - Math.Exp(-dt / TempFadeSeconds)) : raw;
    }

    /// <summary>Farbe nach Temperatur: Farbton wandert von der kalten zur heißen Farbe (über Grün/Gelb).</summary>
    public RgbColor TempColor(LightProfile p)
    {
        double? t = _tempShown ?? SourceTemp(p);
        double f = t is double v ? Math.Clamp((v - p.ColdC) / Math.Max(1, p.HotC - p.ColdC), 0, 1) : 0;
        return LerpHue(RgbColor.FromHex(p.ColdColor), RgbColor.FromHex(p.HotColor), f);
    }

    public static RgbColor LerpHue(RgbColor a, RgbColor b, double f)
    {
        var (ha, sa, va) = ToHsv(a);
        var (hb, sb, vb) = ToHsv(b);
        // Von kalt nach heiß mit abnehmendem Farbton (Blau -> Cyan -> Grün -> Gelb -> Rot).
        if (hb > ha) hb -= 360;
        return RgbColor.FromHsv(ha + (hb - ha) * f, sa + (sb - sa) * f, va + (vb - va) * f);
    }

    private static (double h, double s, double v) ToHsv(RgbColor c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    // ------------------------------------------------------------------ Status

    private void PublishDevices()
    {
        var list = new List<LightDeviceInfo>();
        if (_board != null) list.Add(new LightDeviceInfo(_board.Name, "Mainboard (Lüfter-Hub und Kühlkopf)", _boardModeName));
        foreach (var r in _rams) list.Add(new LightDeviceInfo(r.Name, "Arbeitsspeicher", "Direct (Software-Effekt)"));
        SetStatus(true, _board == null ? "Mainboard nicht in OpenRGB gefunden." : "", list);
    }

    private void SetStatus(bool connected, string msg, IReadOnlyList<LightDeviceInfo>? devices = null)
    {
        Snapshot = Snapshot with { Connected = connected, Message = msg, Devices = devices ?? (connected ? Snapshot.Devices : []) };
        try { Updated?.Invoke(Snapshot); } catch { }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
        _client?.Dispose();
    }
}
