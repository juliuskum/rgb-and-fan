using LichtLuefter.Core.OpenRgb;
/// <summary>Sendet dem Board EINMAL seinen aktuellen Modus (unverändert) und beobachtet 5 s, ob OpenRGB "Geräteliste geändert" meldet.</summary>
static class Listen3
{
    public static void Run(Action<string> W)
    {
        using var a = new OpenRgbClient();
        a.Connect(clientName: "Licht & Lüfter (Prüfung Board)");
        var board = a.GetAllControllers().First(d => d.Type == DeviceType.Motherboard);
        var m = board.Modes[board.ActiveMode];
        if (m.Name is "Direct" or "Custom") { W("Aktiver Modus ist " + m.Name + " – Abbruch."); return; }
        W($"Board aktiv: {m.Name}, sende denselben Modus einmal erneut.");
        a.UpdateMode(board.Index, board.ActiveMode, m);
        var t0 = DateTime.UtcNow; int hits = 0;
        while ((DateTime.UtcNow - t0).TotalSeconds < 5)
        {
            a.DeviceListChanged = false; a.Poll();
            if (a.DeviceListChanged) { hits++; W($"  {(DateTime.UtcNow - t0).TotalMilliseconds:0} ms: Geräteliste geändert"); }
            Thread.Sleep(20);
        }
        W($"Meldungen: {hits}");
        var after = a.GetController(board.Index);
        W($"Danach: {after.Modes[after.ActiveMode].Name}, Zonen: {string.Join(", ", after.Zones.Select(z => $"{z.Name}={z.LedsCount}"))}");
    }
}
