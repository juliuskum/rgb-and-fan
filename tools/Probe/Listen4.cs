using LichtLuefter.Core.OpenRgb;
/// <summary>Bildet die App nach (Board Rainbow wave + RAM-Software-Regenbogen 25/s) und zählt Meldungen. Stellt danach den Ausgangszustand wieder her.</summary>
static class Listen4
{
    public static void Run(Action<string> W)
    {
        using var a = new OpenRgbClient(); a.Connect(clientName: "Licht & Lüfter (Prüfung A)");
        using var b = new OpenRgbClient(); b.Connect(clientName: "Licht & Lüfter (Prüfung B)");
        var all = a.GetAllControllers();
        var board = all.First(d => d.Type == DeviceType.Motherboard);
        var rams = all.Where(d => d.Type == DeviceType.Dram).ToList();
        var origMode = board.ActiveMode; var origModeData = board.Modes[origMode].Clone(); var origColors = board.Colors.ToList();
        if (origModeData.Name is "Direct" or "Custom") { W("Abbruch: Board in " + origModeData.Name); return; }
        var ramOrig = rams.Select(r => r.Colors.ToList()).ToList();
        int rw = board.FindMode("Rainbow wave");
        a.UpdateMode(board.Index, rw, board.Modes[rw]);
        var t0 = DateTime.UtcNow; int hitsA = 0, hitsB = 0; var log = new List<string>();
        while ((DateTime.UtcNow - t0).TotalSeconds < 6)
        {
            double t = (DateTime.UtcNow - t0).TotalSeconds;
            for (int m = 0; m < rams.Count; m++)
                a.UpdateLeds(rams[m].Index, Enumerable.Range(0, rams[m].Leds.Count).Select(i => RgbColor.FromHsv(t * 70 + i * 36 + m * 18, 1, 1)).ToArray());
            a.DeviceListChanged = false; a.Poll(); if (a.DeviceListChanged) { hitsA++; if (log.Count < 8) log.Add($"{t:0.00}s A"); }
            b.DeviceListChanged = false; b.Poll(); if (b.DeviceListChanged) { hitsB++; if (log.Count < 8) log.Add($"{t:0.00}s B"); }
            Thread.Sleep(40);
        }
        // Ausgangszustand wiederherstellen
        a.UpdateMode(board.Index, origMode, origModeData);
        if (origModeData.ColorMode == ColorMode.PerLed) a.UpdateLeds(board.Index, origColors);
        for (int m = 0; m < rams.Count; m++) a.UpdateLeds(rams[m].Index, ramOrig[m]);
        W($"Meldungen 'Geräteliste geändert': A={hitsA}, B={hitsB}");
        foreach (var l in log) W("  " + l);
        var chk = a.GetController(board.Index);
        W($"Wiederhergestellt: Board {chk.Modes[chk.ActiveMode].Name}, Farbe {chk.Colors[^1].ToHex()}");
    }
}
