using System.Buffers.Binary;
using System.Net.Sockets;
/// <summary>Lauscht 12 s am OpenRGB-SDK und zählt unaufgefordert eintreffende Pakete (sendet nichts außer Name/Version).</summary>
static class Listen
{
    public static void Run(Action<string> W)
    {
        using var tcp = new TcpClient("127.0.0.1", 6742);
        var s = tcp.GetStream();
        void Send(uint id, byte[] p) { var h = new byte[16]; "ORGB"u8.CopyTo(h); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), id); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), (uint)p.Length); s.Write(h); s.Write(p); }
        var v = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(v, 4); Send(40, v);
        Send(50, "Licht & Lüfter (Lauschen)\0"u8.ToArray());
        var counts = new Dictionary<uint, int>(); var t0 = DateTime.UtcNow; var times = new List<string>();
        tcp.ReceiveTimeout = 500;
        while ((DateTime.UtcNow - t0).TotalSeconds < 12)
        {
            try
            {
                var h = new byte[16]; int o = 0; while (o < 16) { int n = s.Read(h, o, 16 - o); if (n <= 0) return; o += n; }
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8)), size = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12)), dev = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
                var p = new byte[size]; o = 0; while (o < size) o += s.Read(p, o, (int)size - o);
                counts[id] = counts.GetValueOrDefault(id) + 1;
                if (times.Count < 15) times.Add($"{(DateTime.UtcNow - t0).TotalSeconds:0.00}s id={id} dev={dev} len={size}");
            }
            catch (IOException) { }
        }
        W("Pakete in 12 s: " + string.Join(", ", counts.Select(kv => $"id {kv.Key}: {kv.Value}x")));
        foreach (var t in times) W("  " + t);
    }
}
