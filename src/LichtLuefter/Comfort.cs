using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace LichtLuefter;

/// <summary>
/// Meldet, wenn der Bildschirm aus- oder angeht (Energieeinstellung GUID_CONSOLE_DISPLAY_STATE).
/// Unsichtbares Nachrichtenfenster, lebt im UI-Thread.
/// </summary>
internal sealed class DisplayStateWatcher : NativeWindow, IDisposable
{
    private static Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private const int WM_POWERBROADCAST = 0x0218, PBT_POWERSETTINGCHANGE = 0x8013;
    private IntPtr _reg;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, int flags);
    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POWERBROADCAST_SETTING { public Guid PowerSetting; public uint DataLength; public byte Data; }

    /// <summary>true = Bildschirm an (oder gedimmt), false = aus.</summary>
    public event Action<bool>? DisplayChanged;

    public DisplayStateWatcher()
    {
        CreateHandle(new CreateParams { Caption = "LichtLuefter.Display", Parent = new IntPtr(-3) /* HWND_MESSAGE */ });
        _reg = RegisterPowerSettingNotification(Handle, ref GUID_CONSOLE_DISPLAY_STATE, 0);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_POWERBROADCAST && (int)m.WParam == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
        {
            var s = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(m.LParam);
            if (s.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
                DisplayChanged?.Invoke(s.Data != 0); // 0 = aus, 1 = an, 2 = gedimmt
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_reg != IntPtr.Zero) { UnregisterPowerSettingNotification(_reg); _reg = IntPtr.Zero; }
        DestroyHandle();
    }
}

internal static class Fullscreen
{
    private enum QUERY_USER_NOTIFICATION_STATE { NotPresent = 1, Busy = 2, RunningD3DFullScreen = 3, PresentationMode = 4, AcceptsNotifications = 5, QuietTime = 6, App = 7 }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE state);

    /// <summary>
    /// true, wenn gerade ein Vollbild-Spiel, eine Präsentation oder eine Vollbild-App läuft – also der Zustand,
    /// in dem auch Windows selbst Benachrichtigungen zurückhält.
    /// </summary>
    public static bool IsActive()
    {
        try
        {
            return SHQueryUserNotificationState(out var s) == 0
                   && s is QUERY_USER_NOTIFICATION_STATE.Busy or QUERY_USER_NOTIFICATION_STATE.RunningD3DFullScreen or QUERY_USER_NOTIFICATION_STATE.PresentationMode;
        }
        catch { return false; }
    }
}

/// <summary>Zeichnet die Temperatur als Tray-Symbol: Zahl auf farbigem Grund (kühl/warm/heiß), auf jeder Taskleiste lesbar.</summary>
internal sealed class TrayTempIcon : IDisposable
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    private IntPtr _handle;
    private Icon? _icon;
    private string _key = "";

    /// <summary>Liefert ein Symbol für den Wert; baut nur neu, wenn sich Zahl oder Farbstufe ändern.</summary>
    public Icon? For(float? temp, bool warn)
    {
        string text = temp is float t ? Math.Round(t).ToString("0") : "–";
        int band = warn ? 3 : temp is null ? 0 : temp < 60 ? 1 : temp < 80 ? 2 : 3;
        var key = text + "|" + band;
        if (key == _key && _icon != null) return _icon;
        _key = key;

        int size = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            var bg = band switch
            {
                1 => Color.FromArgb(0x1E, 0x6F, 0xA8),   // kühl
                2 => Color.FromArgb(0xB0, 0x76, 0x0C),   // warm
                3 => Color.FromArgb(0xC2, 0x2F, 0x28),   // heiß / Warnung
                _ => Color.FromArgb(0x55, 0x5E, 0x68),
            };
            using (var path = RoundRect(new RectangleF(0, 0, size, size), size * 0.22f))
            using (var b = new SolidBrush(bg))
                g.FillPath(b, path);
            float fs = size * (text.Length >= 3 ? 0.50f : 0.66f);
            using var font = new Font("Bahnschrift SemiCondensed", fs, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var tb = new SolidBrush(Color.White);
            g.DrawString(text, font, tb, new RectangleF(0, size * 0.04f, size, size), sf);
        }
        var h = bmp.GetHicon();
        var icon = Icon.FromHandle(h);
        // Altes Symbol erst nach dem Tausch freigeben (der Aufrufer setzt das neue sofort).
        var oldHandle = _handle; var oldIcon = _icon;
        _handle = h; _icon = icon;
        oldIcon?.Dispose();
        if (oldHandle != IntPtr.Zero) DestroyIcon(oldHandle);
        return icon;
    }

    private static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _icon?.Dispose();
        if (_handle != IntPtr.Zero) DestroyIcon(_handle);
        _handle = IntPtr.Zero;
    }
}
