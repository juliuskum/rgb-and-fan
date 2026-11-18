using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using LichtLuefter.Core;
using LichtLuefter.Core.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace LichtLuefter;

/// <summary>Hauptfenster: hostet die HTML-Oberfläche (ui/) in WebView2. Schließen versteckt nur ins Tray.</summary>
internal sealed class MainForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Action<JsonObject> _onMessage;
    private bool _ready, _reallyClose;
    private readonly Queue<string> _pending = new();

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    public MainForm(Action<JsonObject> onMessage)
    {
        _onMessage = onMessage;
        Text = "Licht & Lüfter";
        Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "app.ico"));
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(940, 640);
        StartPosition = FormStartPosition.CenterScreen;
        bool dark = IsDarkMode();
        BackColor = dark ? Color.FromArgb(0x1E, 0x23, 0x29) : Color.FromArgb(0xE9, 0xED, 0xF0);
        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);
        HandleCreated += (_, _) => { int v = dark ? 1 : 0; DwmSetWindowAttribute(Handle, 20, ref v, 4); };
        Load += async (_, _) => await InitAsync();
    }

    private static bool IsDarkMode()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0; }
        catch { return true; }
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebView);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("LICHTLUEFTER_DEVTOOLS") == "1";
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.SetVirtualHostNameToFolderMapping("app.lichtluefter", Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.Deny);
            // Nur die eigene Oberfläche darf geladen werden.
            core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://app.lichtluefter/")) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.WebMessageReceived += (_, e) =>
            {
                try
                {
                    if (JsonNode.Parse(e.WebMessageAsJson) is JsonObject o)
                    {
                        if ((string?)o["type"] == "ready") { _ready = true; while (_pending.Count > 0) core.PostWebMessageAsJson(_pending.Dequeue()); }
                        _onMessage(o);
                    }
                }
                catch (Exception ex) { Log.Write("WebMessage: " + ex.Message); }
            };
            core.Navigate("https://app.lichtluefter/index.html");
        }
        catch (Exception ex)
        {
            Log.Write("WebView2: " + ex);
            Controls.Clear();
            Controls.Add(new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Gainsboro,
                Text = "Die Oberfläche konnte nicht geladen werden (WebView2-Runtime fehlt?).\nLüfter und Licht laufen im Hintergrund weiter – Steuerung über das Tray-Symbol.\n\n" + ex.Message,
            });
        }
    }

    public void Send(string type, object payload)
    {
        if (IsDisposed) return;
        var json = JsonSerializer.Serialize(new { type, data = payload }, AppSettings.Json);
        if (!_ready || _web.CoreWebView2 == null) { if (_pending.Count < 20) _pending.Enqueue(json); return; }
        try { _web.CoreWebView2.PostWebMessageAsJson(json); } catch (Exception ex) { Log.Write("Send: " + ex.Message); }
    }

    public void CloseForReal()
    {
        _reallyClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyClose && e.CloseReason == CloseReason.UserClosing)
        {
            // Fenster schließen = ins Tray. Lüfter und Licht laufen weiter.
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
