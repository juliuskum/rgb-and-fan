using System.Diagnostics;
using LichtLuefter.Core.OpenRgb;
/// <summary>Misst, wie lange der OpenRGB-Server nach einem Board-Moduswechsel nicht antwortet. Stellt am Ende Static #FF8A00 her.</summary>
static class Latency
{
    public static void Run(Action<string> W)
    {
        using var a = new OpenRgbClient(); a.Connect(clientName: "Licht & Lüfter (Prüfung Latenz)");
        var board = a.GetAllControllers().First(d => d.Type == DeviceType.Motherboard);
        long Ping() { var sw = Stopwatch.StartNew(); a.GetControllerCount(); return sw.ElapsedMilliseconds; }
        W($"Ruhe: Antwort in {Ping()} ms");
        foreach (var n in new[] { "Static", "Rainbow wave", "Static", "Color shift", "Static", "Fire", "Static", "Breathing", "Static" })
        {
            int i = board.FindMode(n); var m = board.Modes[i].Clone();
            var sw = Stopwatch.StartNew();
            a.UpdateMode(board.Index, i, m);
            if (m.ColorMode == ColorMode.PerLed) a.UpdateLeds(board.Index, Enumerable.Repeat(new RgbColor(255, 138, 0), board.Leds.Count).ToArray());
            long first = Ping();
            W($"{n,-13}: erste Antwort nach {first,5} ms, danach {Ping()} ms");
            Thread.Sleep(1500);
        }
        var r = a.GetController(board.Index); W($"Endzustand: {r.Modes[r.ActiveMode].Name} {r.Colors[^1].ToHex()}");
    }
}
