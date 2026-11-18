using System.Buffers.Binary;
using System.Net.Sockets;
using LichtLuefter.Core.OpenRgb;
/// <summary>Sendet dem RAM 6 s lang (25/s) seine aktuellen Farben und zählt, was OpenRGB dabei an beide Verbindungen schickt.</summary>
static class Listen2
{
    public static void Run(Action<string> W)
    {
        using var a = new OpenRgbClient();
        a.Connect(clientName: "Licht & Lüfter (Prüfung A)");
        var rams = a.GetAllControllers().Where(d => d.Type == DeviceType.Dram).ToList();
        using var tcp = new TcpClient("127.0.0.1", 6742);
        var s = tcp.GetStream(); tcp.ReceiveTimeout = 30;
        void Send(uint id, byte[] p) { var h = new byte[16]; "ORGB"u8.CopyTo(h); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), id); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), (uint)p.Length); s.Write(h); s.Write(p); }
        var v = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(v, 4); Send(40, v); Send(50, "Licht & Lüfter (Prüfung B)\0"u8.ToArray());
        var counts = new Dictionary<uint, int>(); var log = new List<string>();
        var t0 = DateTime.UtcNow; int frames = 0; long maxSend = 0;
        while ((DateTime.UtcNow - t0).TotalSeconds < 6)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var r in rams) a.UpdateLeds(r.Index, r.Colors);
            maxSend = Math.Max(maxSend, sw.ElapsedMilliseconds); frames++;
            a.DeviceListChanged = false; a.Poll(); if (a.DeviceListChanged) { counts[1000u] = counts.GetValueOrDefault(1000u) + 1; if (log.Count < 10) log.Add($"{(DateTime.UtcNow - t0).TotalSeconds:0.00}s A: Geräteliste geändert"); }
            try
            {
                while (s.DataAvailable)
                {
                    var h = new byte[16]; int o = 0; while (o < 16) o += s.Read(h, o, 16 - o);
                    uint id = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8)), size = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12));
                    var p = new byte[size]; o = 0; while (o < size) o += s.Read(p, o, (int)size - o);
                    counts[id] = counts.GetValueOrDefault(id) + 1;
                    if (log.Count < 10 && id != 40) log.Add($"{(DateTime.UtcNow - t0).TotalSeconds:0.00}s B: id {id}");
                }
            }
            catch (IOException) { }
            Thread.Sleep(40);
        }
        // Rundlaufzeit: wie lange braucht der Server, bis er nach den Frames antwortet?
        var rt = System.Diagnostics.Stopwatch.StartNew(); a.GetControllerCount(); 
        W($"{frames} Frames an {rams.Count} RAM-Module, längstes Senden {maxSend} ms, Antwortzeit danach {rt.ElapsedMilliseconds} ms");
        W("Gezählt (1000 = Verbindung A meldet Geräteliste geändert): " + string.Join(", ", counts.Select(kv => $"id {kv.Key}: {kv.Value}x")));
        foreach (var l in log) W("  " + l);
    }
}
