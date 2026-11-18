using LichtLuefter.Core.OpenRgb;
static class Readback
{
    public static void Run(Action<string> W)
    {
        using var c = new OpenRgbClient();
        c.Connect(clientName: "Licht & Lüfter (Prüfung)");
        foreach (var d in c.GetAllControllers())
        {
            var m = d.ActiveMode >= 0 && d.ActiveMode < d.Modes.Count ? d.Modes[d.ActiveMode] : null;
            var distinct = d.Colors.Select(x => x.ToHex()).GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}x{g.Count()}");
            W($"{d.Name} [{d.Type}] Modus={m?.Name} Helligkeit={m?.Brightness} Tempo={m?.Speed} Farben: {string.Join(" ", distinct.Take(4))}");
        }
    }
}
