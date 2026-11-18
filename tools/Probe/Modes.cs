using LichtLuefter.Core.OpenRgb;
/// <summary>Testet Board-Hardware-Modi mit eigener Farbe (je 3 s Orange), liest zurück. Stellt den Ausgangszustand her.</summary>
static class Modes
{
    public static void Run(Action<string> W)
    {
        using var a = new OpenRgbClient(); a.Connect(clientName: "Licht & Lüfter (Prüfung Modi)");
        var board = a.GetAllControllers().First(d => d.Type == DeviceType.Motherboard);
        int origIdx = board.ActiveMode; var orig = board.Modes[origIdx].Clone(); var origColors = board.Colors.ToList();
        if (orig.Name is "Direct" or "Custom") { W("Abbruch"); return; }
        var orange = new RgbColor(255, 120, 0);
        foreach (var n in new[] { "Flashing", "Double flashing", "Lightning", "Meteor", "Blink", "Color pulse", "Color wave", "Marquee", "Visor", "Stack" })
        {
            int i = board.FindMode(n); if (i < 0) { W($"{n}: nicht vorhanden"); continue; }
            var m = board.Modes[i].Clone();
            m.ColorMode = ColorMode.PerLed;
            var t0 = DateTime.Now;
            a.UpdateMode(board.Index, i, m);
            a.UpdateLeds(board.Index, Enumerable.Repeat(orange, board.Leds.Count).ToArray());
            Thread.Sleep(3000);
            RgbDevice? d = null; try { d = a.GetController(board.Index); } catch (Exception ex) { W($"{n}: Rücklesen fehlgeschlagen ({ex.GetType().Name})"); a.Dispose(); return; }
            W($"{t0:HH:mm:ss.fff} {n,-16} -> OpenRGB meldet {d.Modes[d.ActiveMode].Name,-16} Farbe {d.Colors[^1].ToHex()}");
        }
        a.UpdateMode(board.Index, origIdx, orig);
        if (orig.ColorMode == ColorMode.PerLed) a.UpdateLeds(board.Index, origColors);
        Thread.Sleep(1500);
        var r = a.GetController(board.Index); W($"Wiederhergestellt: {r.Modes[r.ActiveMode].Name} {r.Colors[^1].ToHex()}");
    }
}
