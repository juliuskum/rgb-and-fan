using LichtLuefter.Core.Hardware;
using LichtLuefter.Core.Settings;

namespace LichtLuefter.Core.Fans;

public enum FanMode { Starting, Bios, Active, Fault, Unsupported, Suspended }

/// <param name="MinPercent">Kleinster Tastgrad, den die App diesem Anschluss je gibt (Pumpe 70 %, Lüfter gemessen).</param>
public sealed record HeaderSnapshot(int Index, string Name, string Label, ChannelRole Role, float? Rpm, float? Duty, double? Target, bool Controlled, double MinPercent);

/// <param name="CpuTemp">Geglättet (wie für die Regelung verwendet). Null, wenn der Sensor gerade nichts liefert.</param>
/// <param name="CpuTempRaw">Momentaner Rohwert („CPU Package“ = heißester Kern, springt stark).</param>
public sealed record FanSnapshot(
    FanMode Mode, string Profile, string Message, float? CpuTemp, float? GpuTemp,
    IReadOnlyList<HeaderSnapshot> Headers, string CpuName, string GpuName, string BoardName, bool Emergency,
    float? CpuTempRaw, float? GpuTempRaw);

/// <summary>
/// Liest Sensoren (immer) und regelt die Lüfter (nur wenn ein Profil aktiv ist).
/// Läuft auf einem eigenen Thread; alle Hardwarezugriffe passieren dort oder unter <see cref="_hw"/>.
///
/// Sicherheitslogik (siehe <see cref="Safety"/>):
///  * Pumpe nie unter 70 %, Lüfter nie unter 30 % – auch wenn ein Profil weniger verlangt.
///  * CPU ≥ 90 °C oder GPU ≥ 86 °C: alle geregelten Kanäle sofort 100 %.
///  * Temperatursensor fällt > 5 s aus, Pumpe/Lüfter bleibt stehen, Chip bestätigt Befehle nicht:
///    alles zurück auf BIOS-Kurve.
///  * Beenden, Ruhezustand, Abmelden, Absturz: zurück auf BIOS (Absturz/Hänger übernimmt der Watchdog).
/// </summary>
public sealed class FanService : IDisposable
{
    private readonly Func<FanSettings> _settings;
    private readonly object _hw = new();
    private readonly Thread _thread;
    private volatile bool _stop;
    private volatile bool _suspended;
    private DateTime _resumeAt = DateTime.MinValue;

    private HardwareMonitor? _mon;
    private SuperIoAccess? _sio;
    private Heartbeat? _heartbeat;

    // Regelzustand
    private readonly Dictionary<int, ControlledChannel> _controlled = new();
    private FanMode _mode = FanMode.Starting;
    private string _message = "";
    private string? _faultProfile;
    private DateTime _lastCpuValid = DateTime.UtcNow, _lastGpuValid = DateTime.UtcNow;
    private DateTime _sensorFaultSince = DateTime.MinValue, _sensorOkSince = DateTime.MinValue;
    private string? _lastTickError;
    private double? _cpuSmooth, _gpuSmooth, _cpuAvgSmooth;
    private bool _gpuSeen;
    private bool _emergency;

    public FanSnapshot Snapshot { get; private set; } =
        new(FanMode.Starting, "bios", "Sensoren werden gestartet …", null, null, [], "", "", "", false, null, null);

    public event Action<FanSnapshot>? Updated;
    /// <summary>Wichtige Ereignisse für eine Benachrichtigung (z. B. Pumpe ausgefallen).</summary>
    public event Action<string>? Alert;

    private sealed class ControlledChannel
    {
        public required BiosChannelState Bios;
        public required ChannelRole Role;
        public double Current;           // zuletzt geschriebener Wert
        public DateTime LastWrite = DateTime.MinValue;
        public int WriteFailures;
        public float StartRpm;
        public DateTime StallSince = DateTime.MinValue;
        /// <summary>Soll ans BIOS zurück, Rücksetzen aber noch nicht bestätigt: wird nicht mehr geregelt.</summary>
        public bool Releasing;
    }

    public FanService(Func<FanSettings> settings)
    {
        _settings = settings;
        _thread = new Thread(Run) { IsBackground = true, Name = "Lüfter", Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();

    /// <summary>Erkannte Kanäle mit Drehzahl beim Start – Grundlage für die Standard-Rollen.</summary>
    public List<(int index, string name, float? rpm)> DetectedHeaders { get; private set; } = new();

    // ------------------------------------------------------------------ Lebenszyklus

    private void Run()
    {
        try
        {
            lock (_hw)
            {
                _mon = new HardwareMonitor();
                _mon.Open();
                _sio = _mon.SuperIo;
                DetectedHeaders = _mon.Headers.Select(h => (h.Index, h.Name, h.Rpm)).ToList();
                Log.Write($"Hardware: {_mon.BoardName} | {_mon.CpuName} | {_mon.GpuName} | Super-I/O {_sio?.ChipName ?? "-"} {_sio?.UnsupportedReason}");
                Log.Write("Kanäle: " + string.Join(", ", DetectedHeaders.Select(h => $"{h.index} {h.name} {h.rpm:0} U/min")));
                RecoverFromPreviousRun();
            }
            _heartbeat = new Heartbeat();
            _mode = _sio?.IsSupported == true ? FanMode.Bios : FanMode.Unsupported;
            _message = _sio?.IsSupported == true ? "" : "Lüftersteuerung nicht verfügbar: " + (_sio?.UnsupportedReason ?? "kein Super-I/O-Chip gefunden.");
        }
        catch (Exception ex)
        {
            Log.Write("Sensorstart fehlgeschlagen: " + ex);
            _mode = FanMode.Unsupported;
            _message = "Sensoren konnten nicht gestartet werden (Adminrechte? PawnIO?): " + ex.Message;
            Publish();
            return;
        }

        while (!_stop)
        {
            var t0 = DateTime.UtcNow;
            try
            {
                lock (_hw) Tick();
            }
            catch (Exception ex)
            {
                // Gleiche Meldung nicht jede Sekunde ins Log schreiben.
                if (ex.Message != _lastTickError) Log.Write("Fehler im Regelzyklus: " + ex);
                _lastTickError = ex.Message;
                SafeReleaseAll("Interner Fehler – BIOS übernimmt: " + ex.Message, fault: true);
                // Störung an das aktuelle Profil binden, sonst würde sie im nächsten Zyklus sofort quittiert.
                try { _faultProfile = _settings().ActiveProfile; } catch { _faultProfile = "bios"; }
            }
            try { _heartbeat?.Beat(); } catch { }
            Publish();
            var wait = 1000 - (int)(DateTime.UtcNow - t0).TotalMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
        }

        lock (_hw) ReleaseAll("App beendet");
    }

    /// <summary>Falls die App zuvor abgestürzt ist und der Watchdog nicht aufgeräumt hat.</summary>
    private void RecoverFromPreviousRun()
    {
        var prev = FanStateFile.Load();
        if (prev == null || _sio == null || !_sio.IsSupported) return;
        Log.Write($"Zustandsdatei vom letzten Lauf gefunden (PID {prev.OwnerPid}) – stelle Kanäle {string.Join(",", prev.Channels.Select(c => c.Index))} auf BIOS zurück.");
        var failed = _sio.RestoreBiosAll(prev.States);
        if (failed.Count == 0) FanStateFile.Delete();
        else Log.Write("Zurücksetzen fehlgeschlagen für Kanäle " + string.Join(",", failed));
    }

    // ------------------------------------------------------------------ Regelzyklus

    private void Tick()
    {
        var mon = _mon!;
        mon.Update();
        var now = DateTime.UtcNow;

        const double k = Safety.TempSmoothing;
        if (mon.CpuTemp is float c) { _lastCpuValid = now; _cpuSmooth = _cpuSmooth is double s ? s + (c - s) * k : c; }
        // Lüfterkurven folgen dem Kerndurchschnitt: „Package“ (heißester Kern) springt schon im Leerlauf um 20 °C und
        // ließ die Lüfter in der Simulation beim Spielen 16× pro Minute hoch und runter laufen. Notfall bleibt am Package.
        if (mon.CpuAvgTemp is float ca) _cpuAvgSmooth = _cpuAvgSmooth is double sa ? sa + (ca - sa) * k : ca;
        if (mon.GpuTemp is float g) { _lastGpuValid = now; _gpuSeen = true; _gpuSmooth = _gpuSmooth is double s2 ? s2 + (g - s2) * k : g; }

        var settings = _settings();
        bool sensorsOk = (now - _lastCpuValid).TotalSeconds <= Safety.SensorLossSeconds
                         && (!_gpuSeen || (now - _lastGpuValid).TotalSeconds <= Safety.SensorLossSeconds);
        // Nach einem Sensorausfall erst wieder übernehmen, wenn die Werte 5 s am Stück gültig waren.
        if (!sensorsOk)
        {
            if (_sensorFaultSince == DateTime.MinValue) _sensorFaultSince = now;
            _sensorOkSince = DateTime.MinValue;
        }
        else if (_sensorFaultSince != DateTime.MinValue)
        {
            if (_sensorOkSince == DateTime.MinValue) _sensorOkSince = now;
            if ((now - _sensorOkSince).TotalSeconds >= 5) { _sensorFaultSince = DateTime.MinValue; _sensorOkSince = DateTime.MinValue; }
        }

        // Fehlerzustand wird durch Profilwechsel quittiert.
        if (_mode == FanMode.Fault && settings.ActiveProfile != _faultProfile) { _mode = FanMode.Bios; _message = ""; }

        // Ruhezustand / Beenden: nichts mehr schreiben, aber noch gehaltene Kanäle zurückgeben.
        if (_suspended || _stop)
        {
            if (_controlled.Count > 0) ReleaseAll(_suspended ? "Ruhezustand – BIOS-Kurve aktiv." : "App beendet");
            if (_suspended) _mode = FanMode.Suspended;
            return;
        }
        if (_mode == FanMode.Suspended) _mode = FanMode.Bios;

        // Zustandsdatei muss existieren, solange Kanäle gesteuert werden (z. B. nach „--restore-bios“ von außen).
        if (_controlled.Count > 0 && !File.Exists(AppPaths.FanState))
        {
            Log.Write("Zustandsdatei fehlte während der Regelung – neu geschrieben.");
            FanStateFile.Save(_controlled.Values.Select(v => v.Bios));
        }

        var profile = settings.Profiles.FirstOrDefault(p => p.Id == settings.ActiveProfile);
        bool want = _sio?.IsSupported == true
                    && settings.ActiveProfile != "bios" && profile != null
                    && now >= _resumeAt
                    && _mode != FanMode.Fault && _mode != FanMode.Unsupported
                    && sensorsOk && _sensorFaultSince == DateTime.MinValue;

        if (!want)
        {
            if (_controlled.Count > 0)
                ReleaseAll(!sensorsOk ? "Temperatursensor ausgefallen – BIOS-Kurve aktiv." : "BIOS-Kurve aktiv.", fault: false);
            if (_mode == FanMode.Active) _mode = FanMode.Bios;
            if (!sensorsOk && settings.ActiveProfile != "bios" && _mode != FanMode.Fault)
                _message = "Temperatursensor liefert keine Werte – BIOS regelt, bis er wieder da ist.";
            return;
        }

        if (profile == null) return; // durch want bereits ausgeschlossen; für die Nullable-Analyse
        // Nur Kanäle, die es auf dem Board wirklich gibt.
        var wanted = ChannelsToControl(settings).Where(kv => mon.Headers.Any(h => h.Index == kv.Key)).ToDictionary();

        // Kanäle abgeben, die nicht mehr geregelt werden sollen. Nur bei bestätigtem Rücksetzen aus der
        // Zustandsdatei entfernen; sonst bleibt der Kanal "in Abgabe" und wird jeden Zyklus erneut versucht.
        foreach (var idx in _controlled.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            var ch = _controlled[idx];
            if (_sio!.RestoreBiosAll([ch.Bios], attempts: 3).Count == 0)
            {
                _controlled.Remove(idx);
                FanStateFile.Save(_controlled.Values.Select(v => v.Bios));
                if (_controlled.Count == 0) FanStateFile.Delete();
                Log.Write($"Kanal {idx} an BIOS abgegeben – bestätigt.");
            }
            else if (!ch.Releasing)
            {
                ch.Releasing = true;
                Log.Write($"Kanal {idx}: Abgabe an BIOS nicht bestätigt – neuer Versuch im nächsten Zyklus.");
            }
        }
        foreach (var (idx, ch) in _controlled) if (ch.Releasing && wanted.ContainsKey(idx)) ch.Releasing = false;

        // Neue Kanäle übernehmen: erst BIOS-Zustand lesen und auf Platte sichern, dann schreiben.
        var newOnes = wanted.Where(w => !_controlled.ContainsKey(w.Key)).ToList();
        if (newOnes.Count > 0)
        {
            var add = new List<ControlledChannel>();
            foreach (var (idx, role) in newOnes)
            {
                var bios = _sio!.ReadBiosState(idx);
                if (bios == null) { _message = "Lüfter-Controller belegt – neuer Versuch."; return; }
                var hdr = mon.Headers.First(h => h.Index == idx);
                add.Add(new ControlledChannel { Bios = bios.Value, Role = role, Current = hdr.DutyPercent ?? 100, StartRpm = hdr.Rpm ?? 0 });
            }
            FanStateFile.Save(_controlled.Values.Select(v => v.Bios).Concat(add.Select(a => a.Bios)));
            foreach (var a in add) _controlled[a.Bios.Index] = a;
            Log.Write($"Übernehme Kanäle {string.Join(",", add.Select(a => a.Bios.Index))} (Profil {profile.Name}).");
        }

        double cpu = _cpuSmooth ?? 100, gpu = _gpuSmooth ?? 0;
        _emergency = cpu >= Safety.CpuEmergencyC || gpu >= Safety.GpuEmergencyC
                     || (mon.CpuTemp ?? 0) >= Safety.CpuEmergencyRawC || (mon.GpuTemp ?? 0) >= Safety.GpuEmergencyRawC;

        foreach (var (idx, ch) in _controlled)
        {
            if (ch.Releasing) continue;
            if (_stop || _suspended) return; // Ruhezustand/Beenden kam während des Zyklus: nichts mehr schreiben
            var hdr = mon.Headers.FirstOrDefault(h => h.Index == idx)
                      ?? throw new InvalidOperationException($"Kanal {idx} fehlt in den Sensordaten.");

            // --- Ausfallerkennung
            if (ch.Role == ChannelRole.Pump && hdr.Rpm is float pr && pr < Safety.PumpStallRpm)
            {
                if (ch.StallSince == DateTime.MinValue) ch.StallSince = now;
                if ((now - ch.StallSince).TotalSeconds >= Safety.StallSeconds)
                { Fault($"Pumpe meldet nur {pr:0} U/min – BIOS-Kurve übernimmt. Bitte Pumpe prüfen.", settings.ActiveProfile); return; }
            }
            else if (ch.Role == ChannelRole.Fan && ch.StartRpm > 300 && hdr.Rpm is float fr && fr < Safety.FanStallRpm)
            {
                if (ch.StallSince == DateTime.MinValue) ch.StallSince = now;
                if ((now - ch.StallSince).TotalSeconds >= Safety.StallSeconds)
                { Fault($"{Label(settings, idx, hdr.Name)} steht ({fr:0} U/min) – BIOS-Kurve übernimmt.", settings.ActiveProfile); return; }
            }
            else ch.StallSince = DateTime.MinValue;

            // --- Sollwert (CPU-Achse = Kerndurchschnitt)
            double cpuCurve = _cpuAvgSmooth ?? cpu;
            double target = ch.Role == ChannelRole.Pump
                ? Curve.Eval(profile.Pump, cpuCurve)
                : Math.Max(Curve.Eval(profile.FanCpu, cpuCurve), Curve.Eval(profile.FanGpu, gpu));
            double floor = ch.Role == ChannelRole.Pump ? Safety.PumpMinPercent : Safety.FanMinFor(idx);
            target = Math.Clamp(target, floor, 100);
            if (_emergency) target = 100;
            // Totband: kleine Änderungen ignorieren (sonst hörbares Auf und Ab), außer der aktuelle Wert liegt unter dem Minimum.
            // Minimum und 100 % werden immer genau erreicht.
            else if (Math.Abs(target - ch.Current) < Safety.DeadbandPercent && ch.Current >= floor && target > floor && target < 100) target = ch.Current;

            // --- Rampe: zügig hoch, langsam runter; im Notfall sofort 100 %
            double next = target > ch.Current ? Math.Min(target, ch.Current + Safety.RampUpPerSecond) : Math.Max(target, ch.Current - Safety.RampDownPerSecond);
            if (_emergency) next = 100;
            next = Math.Clamp(Math.Round(next, 1), floor, 100);

            // --- Schreiben + prüfen (auch periodisch, falls der EC zurückgesetzt wurde)
            bool lostManual = (now - ch.LastWrite).TotalSeconds >= 5 && _sio!.IsManual(idx) == false;
            if (Math.Abs(next - ch.Current) >= 1 || (now - ch.LastWrite).TotalSeconds >= 15 || lostManual || ch.LastWrite == DateTime.MinValue)
            {
                if (_sio!.SetPercent(idx, next))
                {
                    ch.Current = next;
                    ch.LastWrite = now;
                    ch.WriteFailures = 0;
                }
                else if (++ch.WriteFailures >= 3)
                { Fault($"Lüfter-Controller bestätigt Befehle für {Label(settings, idx, hdr.Name)} nicht – BIOS-Kurve übernimmt.", settings.ActiveProfile); return; }
            }
        }

        _mode = FanMode.Active;
        _message = _emergency ? "Temperatur kritisch – alle geregelten Lüfter und die Pumpe auf 100 %." : "";
    }

    private static string Label(FanSettings s, int idx, string fallback) =>
        s.Channels.FirstOrDefault(c => c.Index == idx)?.Label is { Length: > 0 } l ? l : fallback;

    /// <summary>Welche Kanäle mit welcher Rolle geregelt werden. PUMP_FAN ist immer Pumpe.</summary>
    private Dictionary<int, ChannelRole> ChannelsToControl(FanSettings s)
    {
        var d = new Dictionary<int, ChannelRole>();
        foreach (var c in s.Channels)
        {
            if (c.Role == ChannelRole.Off || c.Index < 0 || c.Index >= 8) continue;
            d[c.Index] = c.Index == Safety.PumpHeaderIndex ? ChannelRole.Pump : c.Role;
        }
        return d;
    }

    private void Fault(string msg, string profile)
    {
        Log.Write("STÖRUNG: " + msg);
        ReleaseAll(msg, fault: true);
        _faultProfile = profile;
        Alert?.Invoke(msg);
    }

    /// <summary>Alle Kanäle zurück auf BIOS. Zustandsdatei wird nur gelöscht, wenn alle bestätigt sind.</summary>
    private void ReleaseAll(string msg, bool fault = false)
    {
        if (_controlled.Count > 0 && _sio != null)
        {
            var failed = _sio.RestoreBiosAll(_controlled.Values.Select(c => c.Bios));
            Log.Write($"Zurück auf BIOS ({msg}): Kanäle {string.Join(",", _controlled.Keys)}" + (failed.Count > 0 ? $" – FEHLGESCHLAGEN: {string.Join(",", failed)}" : " – bestätigt"));
            if (failed.Count == 0)
            {
                _controlled.Clear();
                FanStateFile.Delete();
            }
            else
            {
                foreach (var k in _controlled.Keys.Except(failed).ToList()) _controlled.Remove(k);
                FanStateFile.Save(_controlled.Values.Select(c => c.Bios));
                msg += " Zurücksetzen nicht bestätigt – der Watchdog versucht es weiter.";
            }
        }
        // Eine bestehende Störung wird durch ein späteres, normales Abgeben nicht aufgehoben –
        // nur der Nutzer quittiert sie (Profilwechsel oder „Erneut versuchen“).
        bool keepFault = !fault && _mode == FanMode.Fault;
        _mode = fault || keepFault ? FanMode.Fault : (_mode == FanMode.Unsupported ? FanMode.Unsupported : FanMode.Bios);
        const string pending = " Zurücksetzen nicht bestätigt – der Watchdog versucht es weiter.";
        if (!keepFault) _message = msg;
        else if (_controlled.Count > 0 && !_message.Contains(pending)) _message += pending;
        else if (_controlled.Count == 0) _message = _message.Replace(pending, "");
        _emergency = false;
    }

    /// <summary>
    /// Aus fremden Threads (Absturz, Abmelden, Ruhezustand): versucht das Lock, stellt notfalls direkt zurück.
    /// Der Notfallpfad ohne Lock fasst die Kanalliste des Regelthreads nicht an, sondern liest die Kanäle aus der
    /// Zustandsdatei und lässt sie bestehen – aufräumen darf nur, wer das Lock hält. Watchdog bzw. nächster
    /// Start stellen dann notfalls erneut zurück.
    /// </summary>
    private void SafeReleaseAll(string msg, bool fault)
    {
        if (Monitor.TryEnter(_hw, 3000))
        {
            try { ReleaseAll(msg, fault); }
            finally { Monitor.Exit(_hw); }
        }
        else if (_sio != null)
        {
            var st = FanStateFile.Load();
            if (st == null) return;
            var failed = _sio.RestoreBiosAll(st.States);
            Log.Write("Notfall-Rücksetzung ohne Regel-Lock: " + (failed.Count == 0 ? "bestätigt" : "fehlgeschlagen für " + string.Join(",", failed)));
        }
    }

    // ------------------------------------------------------------------ Steuerung von außen

    /// <summary>Ruhezustand: sofort zurück aufs BIOS.</summary>
    public void Suspend()
    {
        _suspended = true;
        SafeReleaseAll("Ruhezustand – BIOS-Kurve aktiv.", fault: false);
    }

    /// <summary>Nach dem Aufwachen 10 s warten, bis EC und Sensoren stabil sind.</summary>
    public void Resume()
    {
        _resumeAt = DateTime.UtcNow.AddSeconds(10);
        _suspended = false;
    }

    /// <summary>Synchrones Zurücksetzen (Abmelden/Herunterfahren/Absturz).</summary>
    public void EmergencyRelease(string why) => SafeReleaseAll(why, fault: false);

    public void Stop()
    {
        _stop = true;
        if (_thread.IsAlive && !_thread.Join(5000))
            SafeReleaseAll("App beendet (Regelthread reagiert nicht)", fault: false);
    }

    public void Dispose()
    {
        Stop();
        // Nicht unbegrenzt warten: hängt der Regelthread in einem Treiberaufruf, würde sonst das Beenden
        // (und damit Abmelden/Herunterfahren) blockieren. Die Lüfter sind zu diesem Zeitpunkt bereits zurückgestellt.
        if (Monitor.TryEnter(_hw, 3000))
        {
            try { _mon?.Dispose(); _mon = null; }
            finally { Monitor.Exit(_hw); }
        }
        else Log.Write("Regelthread reagiert nicht – Sensorzugriff wird beim Prozessende freigegeben.");
        try { _heartbeat?.Dispose(); } catch { }
    }

    /// <summary>Fehlerzustand quittieren (Knopf „Erneut versuchen“).</summary>
    public void ClearFault()
    {
        lock (_hw)
        {
            if (_mode != FanMode.Fault) return;
            _mode = FanMode.Bios;
            _message = "";
            _faultProfile = null;
        }
    }

    private void Publish()
    {
        FanSnapshot snap;
        lock (_hw)
        {
            var mon = _mon;
            var s = _settings();
            var headers = mon == null ? [] : mon.Headers.Select(h =>
            {
                var cfg = s.Channels.FirstOrDefault(c => c.Index == h.Index);
                _controlled.TryGetValue(h.Index, out var cc);
                var role = cfg?.Role ?? ChannelRole.Off;
                if (h.Index == Safety.PumpHeaderIndex && role != ChannelRole.Off) role = ChannelRole.Pump;
                return new HeaderSnapshot(h.Index, h.Name, cfg?.Label is { Length: > 0 } l ? l : h.Name, role, h.Rpm, h.DutyPercent, cc?.Current, cc != null,
                    role == ChannelRole.Pump ? Safety.PumpMinPercent : Safety.FanMinFor(h.Index));
            }).ToList();
            // Angezeigt wird, was die Kurven verwenden: geglätteter Kerndurchschnitt. Rohwert = heißester Kern (Tooltip).
            float? cpuS = mon?.CpuAvgTemp != null && _cpuAvgSmooth is double cs ? (float)cs : null;
            float? gpuS = mon?.GpuTemp != null && _gpuSmooth is double gs ? (float)gs : null;
            snap = new FanSnapshot(_mode, s.ActiveProfile, _message, cpuS, gpuS, headers,
                mon?.CpuName ?? "", mon?.GpuName ?? "", mon?.BoardName ?? "", _emergency, mon?.CpuTemp, mon?.GpuTemp);
            Snapshot = snap;
        }
        try { Updated?.Invoke(snap); } catch (Exception ex) { Log.Write("UI-Update: " + ex.Message); }
    }
}
