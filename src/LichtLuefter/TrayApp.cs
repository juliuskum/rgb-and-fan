using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LichtLuefter.Core;
using LichtLuefter.Core.Fans;
using LichtLuefter.Core.Lighting;
using LichtLuefter.Core.Settings;
using Microsoft.Win32;

namespace LichtLuefter;

internal sealed class TrayApp : ApplicationContext
{
    private readonly SettingsStore _store = new(AppPaths.Settings);
    private AppSettings _settings;
    private volatile FanSettings _fanCopy;
    private volatile GeneralSettings _generalCopy;

    private readonly FanService _fans;
    private readonly LightingService _light;
    private readonly NotifyIcon _tray;
    private readonly Control _invoker = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    private readonly EventWaitHandle _showEvent = Program.UserEvent(Program.ShowEventName);
    private readonly EventWaitHandle _exitEvent = Program.UserEvent(Program.ExitEventName);
    private readonly EventWaitHandle _biosEvent = Program.UserEvent(Program.BiosEventName);
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 400 };
    private bool _autostart;
    private readonly Icon _iconOk, _iconWarn;
    private MainForm? _form;
    private Process? _watchdog;
    private int _shutdown;
    private DateTime _lastOpenRgbStart = DateTime.MinValue;
    private string? _lastBackup;

    public TrayApp(bool startHidden)
    {
        _invoker.CreateControl();
        _settings = _store.Load();
        _fanCopy = Clone(_settings.Fans);
        _generalCopy = Clone(_settings.General);

        _lastBackup = OpenRgbBackup.Run();

        _fans = new FanService(() => _fanCopy);
        _fans.Updated += snap => Post(() => { _form?.Send("fan", snap); UpdateTray(snap); });
        _fans.Alert += msg => Post(() => _tray?.ShowBalloonTip(8000, "Lüfter", msg, ToolTipIcon.Warning));
        _fans.Start();
        _watchdog = WatchdogLauncher.Start();

        _light = new LightingService(Clone(_settings.Light), () => _generalCopy, () => (_fans.Snapshot.CpuTemp, _fans.Snapshot.GpuTemp));
        _light.Updated += snap => Post(() => _form?.Send("light", snap));
        _light.OnServerUnreachable = () => Post(MaybeStartOpenRgb);
        MaybeStartOpenRgb();
        _light.Start();

        _iconOk = new Icon(Path.Combine(AppContext.BaseDirectory, "app.ico"), SystemInformation.SmallIconSize);
        _iconWarn = TrayIcons.Warning(_iconOk);
        _tray = new NotifyIcon { Icon = _iconOk, Text = "Licht & Lüfter", Visible = true };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        BuildMenu();

        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.SessionEnded += OnSessionEnded;

        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Post(ShowWindow), null, -1, false);
        ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => Post(ExitApp), null, -1, true);
        ThreadPool.RegisterWaitForSingleObject(_biosEvent, (_, _) => Post(() => { Log.Write("Signal: BIOS-Kurve"); SetFanProfile("bios"); }), null, -1, false);

        _autostart = Autostart.IsEnabled();
        _saveTimer.Tick += (_, _) => FlushSave();
        new Thread(CommandPipeLoop) { IsBackground = true, Name = "Befehle" }.Start();

        _timer.Tick += (_, _) => Housekeeping();
        _timer.Start();

        if (!startHidden) ShowWindow();
    }

    // ------------------------------------------------------------------ Fenster & Tray

    private void ShowWindow()
    {
        if (_form == null || _form.IsDisposed)
        {
            _form = new MainForm(HandleMessage);
            _form.FormClosed += (_, _) => _form = null;
        }
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    private void BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Öffnen", null, (_, _) => ShowWindow());
        m.Items.Add(new ToolStripSeparator());

        var light = new ToolStripMenuItem("Licht");
        var on = new ToolStripMenuItem("Beleuchtung an") { Checked = _settings.Light.Enabled, CheckOnClick = false };
        on.Click += (_, _) => { _settings.Light.Enabled = !_settings.Light.Enabled; Changed(); };
        light.DropDownItems.Add(on);
        light.DropDownItems.Add(new ToolStripSeparator());
        foreach (var p in _settings.Light.Saved)
        {
            var name = p.Name;
            light.DropDownItems.Add(name, null, (_, _) => LoadLightProfile(name));
        }
        m.Items.Add(light);

        var fans = new ToolStripMenuItem("Lüfter");
        void AddFan(string id, string label)
        {
            var it = new ToolStripMenuItem(label) { Checked = _settings.Fans.ActiveProfile == id };
            it.Click += (_, _) => SetFanProfile(id);
            fans.DropDownItems.Add(it);
        }
        AddFan("bios", "BIOS-Kurve");
        foreach (var p in _settings.Fans.Profiles) AddFan(p.Id, p.Name);
        m.Items.Add(fans);

        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Beenden", null, (_, _) => ExitApp());
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = m;
        old?.Dispose();
    }

    private void UpdateTray(FanSnapshot s)
    {
        if (_shutdown == 1) return; // Tray wird gerade entsorgt
        var prof = s.Profile == "bios" ? "BIOS-Kurve" : _settings.Fans.Profiles.FirstOrDefault(p => p.Id == s.Profile)?.Name ?? s.Profile;
        var state = s.Mode switch
        {
            FanMode.Fault => "Störung",
            FanMode.Active => prof,
            FanMode.Unsupported => "nicht verfügbar",
            _ => "BIOS-Kurve",
        };
        var text = $"Licht & Lüfter\nCPU {s.CpuTemp:0} °C, GPU {s.GpuTemp:0} °C\nLüfter: {state}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
        _tray.Icon = s.Mode == FanMode.Fault || s.Emergency ? _iconWarn : _iconOk;
    }

    // ------------------------------------------------------------------ Nachrichten der Oberfläche

    private void HandleMessage(JsonObject msg)
    {
        var type = (string?)msg["type"];
        try
        {
            switch (type)
            {
                case "ready":
                    SendState();
                    _form?.Send("fan", _fans.Snapshot);
                    _form?.Send("light", _light.Snapshot);
                    break;
                case "light.set":
                    _settings.Light.Current = msg["profile"].Deserialize<LightProfile>(AppSettings.Json) ?? _settings.Light.Current;
                    if (msg["enabled"] is JsonNode en) _settings.Light.Enabled = (bool)en;
                    Changed(rebuildMenu: msg["enabled"] != null);
                    break;
                case "light.saveProfile":
                {
                    var name = ((string?)msg["name"])?.Trim();
                    if (string.IsNullOrEmpty(name)) break;
                    var p = _settings.Light.Current.Clone();
                    p.Name = name;
                    _settings.Light.Saved.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    _settings.Light.Saved.Add(p);
                    _settings.Light.Current.Name = name;
                    Changed(rebuildMenu: true);
                    _form?.Send("toast", new { text = $"Profil „{name}“ gespeichert." });
                    break;
                }
                case "light.loadProfile":
                    LoadLightProfile((string?)msg["name"] ?? "");
                    break;
                case "light.deleteProfile":
                {
                    var name = (string?)msg["name"] ?? "";
                    _settings.Light.Saved.RemoveAll(x => x.Name == name);
                    Changed(rebuildMenu: true);
                    _form?.Send("toast", new { text = $"Profil „{name}“ gelöscht." });
                    break;
                }
                case "fan.setProfile":
                    SetFanProfile((string?)msg["id"] ?? "bios");
                    break;
                case "fan.updateProfile":
                {
                    var p = msg["profile"].Deserialize<FanProfile>(AppSettings.Json);
                    if (p == null) break;
                    var i = _settings.Fans.Profiles.FindIndex(x => x.Id == p.Id);
                    if (i < 0) break;
                    p.Name = _settings.Fans.Profiles[i].Name;
                    _settings.Fans.Profiles[i] = SanitizeCurves(p);
                    Changed();
                    break;
                }
                case "fan.resetProfile":
                {
                    var id = (string?)msg["id"];
                    var d = FanProfile.Defaults().FirstOrDefault(x => x.Id == id);
                    var i = _settings.Fans.Profiles.FindIndex(x => x.Id == id);
                    if (d != null && i >= 0) { _settings.Fans.Profiles[i] = d; Changed(); _form?.Send("toast", new { text = $"Kurven von „{d.Name}“ zurückgesetzt." }); }
                    break;
                }
                case "fan.setChannel":
                {
                    int idx = (int)msg["index"]!;
                    var role = Enum.Parse<ChannelRole>((string)msg["role"]!, true);
                    if (idx == Safety.PumpHeaderIndex && role == ChannelRole.Fan) role = ChannelRole.Pump; // PUMP_FAN1 bleibt Pumpe
                    var ch = _settings.Fans.Channels.FirstOrDefault(c => c.Index == idx);
                    if (ch == null) _settings.Fans.Channels.Add(ch = new ChannelConfig { Index = idx, Label = HeaderLabel(idx) });
                    ch.Role = role;
                    Changed();
                    break;
                }
                case "fan.retry":
                    _fans.ClearFault();
                    break;
                case "autostart.set":
                {
                    var (ok, output) = Autostart.Set((bool)msg["enabled"]!);
                    _autostart = Autostart.IsEnabled();
                    _form?.Send("toast", new { text = ok ? ((bool)msg["enabled"]! ? "Autostart eingerichtet." : "Autostart entfernt.") : "Autostart fehlgeschlagen: " + output });
                    SendState();
                    break;
                }
                case "general.set":
                    if (msg["startOpenRgb"] is JsonNode so) _settings.General.StartOpenRgb = (bool)so;
                    Changed();
                    break;
                case "openrgb.restart":
                {
                    var path = OpenRgbHelper.FindPath(_settings.General.OpenRgbPath);
                    if (path == null) { _form?.Send("toast", new { text = "OpenRGB.exe nicht gefunden." }); break; }
                    _settings.General.OpenRgbPath = path;
                    OpenRgbHelper.Restart(path);
                    _lastOpenRgbStart = DateTime.UtcNow;
                    _light.Reapply();
                    _form?.Send("toast", new { text = "OpenRGB wird mit SDK-Server neu gestartet." });
                    break;
                }
                case "open.folder":
                {
                    var which = (string?)msg["which"];
                    var dir = which == "backups" ? AppPaths.Backups : AppPaths.Logs;
                    Directory.CreateDirectory(dir);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                    break;
                }
                case "app.exit":
                    ExitApp();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Nachricht {type}: {ex}");
            _form?.Send("toast", new { text = "Aktion fehlgeschlagen: " + ex.Message });
        }
    }

    private static FanProfile SanitizeCurves(FanProfile p)
    {
        static List<CurvePoint> S(List<CurvePoint>? l, double floor) => (l ?? [])
            .Select(x => new CurvePoint(Math.Clamp(Math.Round(x.T), 20, 100), Math.Clamp(Math.Round(x.P), floor, 100)))
            .OrderBy(x => x.T).Take(8).ToList();
        p.FanCpu = S(p.FanCpu, Safety.FanMinPercent);
        p.FanGpu = S(p.FanGpu, Safety.FanMinPercent);
        p.Pump = S(p.Pump, Safety.PumpMinPercent);
        return p;
    }

    private void LoadLightProfile(string name)
    {
        var p = _settings.Light.Saved.FirstOrDefault(x => x.Name == name);
        if (p == null) return;
        _settings.Light.Current = p.Clone();
        _settings.Light.Enabled = true;
        Changed(rebuildMenu: true);
    }

    private void SetFanProfile(string id)
    {
        if (id != "bios" && !_settings.Fans.Profiles.Any(p => p.Id == id)) return;
        if (_settings.Fans.ActiveProfile == id) _fans.ClearFault();
        _settings.Fans.ActiveProfile = id;
        Changed(rebuildMenu: true);
    }

    private static string HeaderLabel(int idx) => idx switch
    {
        0 => "CPU_FAN1",
        1 => "PUMP_FAN1",
        _ => $"SYS_FAN{idx - 1}",
    };

    /// <summary>
    /// Übernimmt eine Änderung sofort in Licht- und Lüfterdienst. Das Schreiben der Datei ist entprellt
    /// (Schieberegler senden viele Änderungen); beim Beenden wird es nachgeholt.
    /// </summary>
    private void Changed(bool rebuildMenu = false)
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        _fanCopy = Clone(_settings.Fans);
        _generalCopy = Clone(_settings.General);
        _light.Apply(Clone(_settings.Light));
        if (rebuildMenu) BuildMenu();
        SendState();
    }

    private void FlushSave()
    {
        _saveTimer.Stop();
        try { _store.Save(_settings); } catch (Exception ex) { Log.Write("Speichern fehlgeschlagen: " + ex.Message); }
    }

    private void SendState()
    {
        _form?.Send("state", new
        {
            settings = _settings,
            autostart = _autostart,
            safety = new { fanMin = Safety.FanMinPercent, pumpMin = Safety.PumpMinPercent, cpuEmergency = Safety.CpuEmergencyC, gpuEmergency = Safety.GpuEmergencyC, cpuEmergencyRaw = Safety.CpuEmergencyRawC, gpuEmergencyRaw = Safety.GpuEmergencyRawC },
            backupDir = AppPaths.Backups,
            lastBackup = _lastBackup,
            version = Application.ProductVersion,
        });
    }

    // ------------------------------------------------------------------ Hintergrund

    private void Housekeeping()
    {
        // Standard-Rollen beim ersten Start aus den gemessenen Drehzahlen ableiten.
        if (_settings.Fans.Channels.Count == 0 && _fans.DetectedHeaders.Count > 0)
        {
            foreach (var (index, _, rpm) in _fans.DetectedHeaders)
            {
                var role = rpm is > 0 ? (index == Safety.PumpHeaderIndex ? ChannelRole.Pump : ChannelRole.Fan) : ChannelRole.Off;
                _settings.Fans.Channels.Add(new ChannelConfig { Index = index, Label = HeaderLabel(index), Role = role });
            }
            Log.Write("Kanalrollen festgelegt: " + string.Join(", ", _settings.Fans.Channels.Select(c => $"{c.Label}={c.Role}")));
            Changed();
        }

        // Watchdog am Leben halten.
        if (_shutdown == 0 && (_watchdog == null || _watchdog.HasExited))
        {
            Log.Write("Watchdog läuft nicht – starte neu.");
            _watchdog = WatchdogLauncher.Start();
        }

        // Prozesse, die parallel an RGB/Lüftern drehen würden.
        var conflicts = new[] { "iCUE", "Corsair.Service", "MSI.CentralServer", "MSI Center", "MysticLight", "SignalRgb", "SignalRgbLauncher", "FanControl" }
            .Where(n => Process.GetProcessesByName(n).Length > 0).ToList();
        _form?.Send("conflicts", conflicts);
    }

    private void MaybeStartOpenRgb()
    {
        if (!_settings.General.StartOpenRgb || OpenRgbHelper.IsRunning()) return;
        if ((DateTime.UtcNow - _lastOpenRgbStart).TotalSeconds < 30) return;
        var path = OpenRgbHelper.FindPath(_settings.General.OpenRgbPath);
        if (path == null) return;
        _lastOpenRgbStart = DateTime.UtcNow;
        _lastBackup = OpenRgbBackup.Run() ?? _lastBackup; // vor jedem Start, den die App auslöst
        OpenRgbHelper.Start(path);
    }

    private void OnPower(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) { Log.Write("Ruhezustand"); _fans.Suspend(); }
        else if (e.Mode == PowerModes.Resume)
        {
            Log.Write("Aufgewacht");
            _fans.Resume();
            var t = new System.Windows.Forms.Timer { Interval = 6000 };
            t.Tick += (_, _) => { t.Dispose(); _light.Reapply(); };
            t.Start();
        }
    }

    // SessionEnding lässt sich noch abbrechen (z. B. ein Programm blockiert das Abmelden). Darum hier nur speichern;
    // die Hardware wird erst bei SessionEnded zurückgegeben, wenn die Sitzung wirklich endet.
    private void OnSessionEnding(object? s, SessionEndingEventArgs e)
    {
        Log.Write("Abmelden/Herunterfahren angekündigt: " + e.Reason);
        FlushSave();
    }

    private void OnSessionEnded(object? s, SessionEndedEventArgs e)
    {
        Log.Write("Sitzung endet: " + e.Reason);
        ShutdownHardware("Sitzung endet");
    }

    // ------------------------------------------------------------------ Befehle von außen (z. B. Verknüpfungen)

    /// <summary>
    /// Benannte Pipe für „--light-effect“, „--light-profile“, „--fan-profile“. Nur der angemeldete Benutzer darf schreiben.
    /// Über die Pipe lassen sich nur Profile wählen – alle Lüfterprofile halten die festen Sicherheitsgrenzen ein.
    /// </summary>
    private void CommandPipeLoop()
    {
        var sec = new System.IO.Pipes.PipeSecurity();
        sec.AddAccessRule(new System.IO.Pipes.PipeAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.IO.Pipes.PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow));
        while (_shutdown == 0)
        {
            try
            {
                using var srv = System.IO.Pipes.NamedPipeServerStreamAcl.Create(Program.CommandPipeName, System.IO.Pipes.PipeDirection.In, 1,
                    System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.None, 0, 0, sec);
                srv.WaitForConnection();
                using var r = new StreamReader(srv);
                var line = r.ReadLine();
                if (!string.IsNullOrWhiteSpace(line)) Post(() => RunCommand(line.Trim()));
            }
            catch (Exception ex) { Log.Write("Befehls-Pipe: " + ex.Message); Thread.Sleep(1000); }
        }
    }

    private void RunCommand(string line)
    {
        int sp = line.IndexOf(' ');
        var cmd = sp < 0 ? line : line[..sp];
        var arg = sp < 0 ? "" : line[(sp + 1)..].Trim().Trim('"');
        Log.Write($"Befehl: {cmd} {arg}");
        switch (cmd)
        {
            case "light-effect" when Enum.TryParse<LightEffect>(arg, true, out var e):
                _settings.Light.Current.Effect = e;
                _settings.Light.Enabled = true;
                Changed(rebuildMenu: true);
                break;
            case "light-profile":
                LoadLightProfile(arg);
                break;
            case "fan-profile":
                SetFanProfile(arg);
                break;
            default:
                Log.Write("Unbekannter Befehl: " + line);
                break;
        }
    }

    // ------------------------------------------------------------------ Beenden

    public void ShutdownHardware(string why)
    {
        if (Interlocked.Exchange(ref _shutdown, 1) == 1) return;
        Log.Write("Beende Hardwarezugriff: " + why);
        try { _store.Save(_settings); } catch { }
        try { _fans.Stop(); } catch (Exception ex) { Log.Write("Lüfter stoppen: " + ex.Message); }
        try { using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, Watchdog.StopEventName); stop.Set(); } catch { }
        try { _light.Dispose(); } catch { }
        try { _fans.Dispose(); } catch { }
    }

    public void OnFatal(Exception? ex)
    {
        Log.Write("ABSTURZ: " + ex);
        ShutdownHardware("Absturz");
    }

    public void OnUiError(Exception ex)
    {
        Log.Write("UI-Fehler: " + ex);
        Post(() => _tray?.ShowBalloonTip(5000, "Licht & Lüfter", "Fehler in der Oberfläche: " + ex.Message, ToolTipIcon.Error));
    }

    private void ExitApp()
    {
        _form?.CloseForReal();
        ShutdownHardware("Beenden");
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ShutdownHardware("Dispose");
            _timer.Dispose();
            _saveTimer.Dispose();
            _tray.Dispose();
            _showEvent.Dispose();
            _exitEvent.Dispose();
            _biosEvent.Dispose();
        }
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ Hilfen

    private void Post(Action a)
    {
        if (_shutdown == 1 || _invoker.IsDisposed || !_invoker.IsHandleCreated) return;
        try { _invoker.BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    private static T Clone<T>(T o) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(o, AppSettings.Json), AppSettings.Json)!;
}

internal static class TrayIcons
{
    /// <summary>Warn-Variante: roter Punkt unten rechts.</summary>
    public static Icon Warning(Icon baseIcon)
    {
        using var bmp = baseIcon.ToBitmap();
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Color.FromArgb(240, 82, 74));
            g.FillEllipse(b, bmp.Width * 0.45f, bmp.Height * 0.45f, bmp.Width * 0.55f, bmp.Height * 0.55f);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
