using LichtLuefter.Core.OpenRgb;
/// <summary>Spielt Moduswechsel am Board nach (langsam und schnell hintereinander) und liest jeweils zurück. Stellt danach den Ausgangszustand her.</summary>
static class Switch
{
    public static void Run(Action<string> W)
    {
        using var a = new OpenRgbClient(); a.Connect(clientName: "Licht & Lüfter (Prüfung Wechsel)");
        var board = a.GetAllControllers().First(d => d.Type == DeviceType.Motherboard);
        int origIdx = board.ActiveMode; var orig = board.Modes[origIdx].Clone(); var origColors = board.Colors.ToList();
        if (orig.Name is "Direct" or "Custom") { W("Abbruch"); return; }
        var t0 = DateTime.UtcNow;
        string T() => $"{(DateTime.UtcNow - t0).TotalSeconds,5:0.00}s";
        void Set(string name, RgbColor? col = null)
        {
            int i = board.FindMode(name); var m = board.Modes[i].Clone();
            a.UpdateMode(board.Index, i, m);
            if (col is RgbColor c && m.ColorMode == ColorMode.PerLed) a.UpdateLeds(board.Index, Enumerable.Repeat(c, board.Leds.Count).ToArray());
            W($"{T()} gesendet: {name}");
        }
        void Check(string expect)
        {
            var d = a.GetController(board.Index);
            var got = d.Modes[d.ActiveMode].Name;
            W($"{T()}   OpenRGB meldet: {got} {(got == expect ? "ok" : "<-- ABWEICHUNG, erwartet " + expect)} | LED0 {d.Colors[^1].ToHex()}");
        }
        // 1) langsam: je 3 s
        foreach (var n in new[] { "Static", "Color shift", "Rainbow wave", "Breathing" })
        { Set(n, new RgbColor(255, 0, 0)); Thread.Sleep(600); Check(n); Thread.Sleep(2400); Check(n); }
        // 2) schnell hintereinander (wie mehrere Klicks): 150 ms Abstand
        foreach (var n in new[] { "Rainbow wave", "Static", "Color shift", "Breathing" }) { Set(n, new RgbColor(0, 255, 0)); Thread.Sleep(150); }
        Thread.Sleep(800); Check("Breathing"); Thread.Sleep(2500); Check("Breathing");
        // 3) Static mit Farbe, dann gleich Static mit anderer Farbe
        Set("Static", new RgbColor(0, 0, 255)); Thread.Sleep(80); Set("Static", new RgbColor(255, 255, 0)); Thread.Sleep(2000); Check("Static");
        // Wiederherstellen
        a.UpdateMode(board.Index, origIdx, orig);
        if (orig.ColorMode == ColorMode.PerLed) a.UpdateLeds(board.Index, origColors);
        Thread.Sleep(1500);
        var r = a.GetController(board.Index); W($"Wiederhergestellt: {r.Modes[r.ActiveMode].Name} {r.Colors[^1].ToHex()}");
    }
}
