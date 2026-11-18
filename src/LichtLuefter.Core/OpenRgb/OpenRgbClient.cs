using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace LichtLuefter.Core.OpenRgb;

/// <summary>
/// Minimaler Client für das OpenRGB-SDK (TCP, Standard 127.0.0.1:6742).
/// Bewusst NICHT implementiert: SETCUSTOMMODE (schaltet auf Direct), SAVEMODE (schreibt in Geräteflash)
/// und alle Profil-/Einstellungs-Pakete – die App verändert so weder OpenRGB-Konfiguration noch Geräteflash.
/// </summary>
public sealed class OpenRgbClient : IDisposable
{
    private const uint PktRequestControllerCount = 0;
    private const uint PktRequestControllerData = 1;
    private const uint PktRequestProtocolVersion = 40;
    private const uint PktSetClientName = 50;
    private const uint PktDeviceListUpdated = 100;
    private const uint PktUpdateLeds = 1050;
    private const uint PktUpdateMode = 1101;

    /// <summary>Höchste Protokollversion, deren Format dieser Client sicher kennt.</summary>
    public const uint ClientProtocolVersion = 4;

    private readonly object _io = new();
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public uint ServerProtocolVersion { get; private set; }
    public uint ProtocolVersion { get; private set; }
    public bool IsConnected => _tcp?.Connected == true && _stream != null;

    /// <summary>Wird gesetzt, wenn OpenRGB meldet, dass sich die Geräteliste geändert hat.</summary>
    public bool DeviceListChanged { get; set; }

    public void Connect(string host = "127.0.0.1", int port = 6742, string clientName = "Licht & Lüfter", int timeoutMs = 2000)
    {
        lock (_io)
        {
            CloseNoLock();
            var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 5000, SendTimeout = 5000 };
            bool connected;
            try
            {
                connected = tcp.ConnectAsync(host, port).Wait(timeoutMs) && tcp.Connected;
            }
            catch (AggregateException ae)
            {
                // Abgelehnte Verbindung (Server aus) kommt als AggregateException – einheitlich als IOException melden.
                tcp.Dispose();
                throw new IOException($"OpenRGB-SDK-Server unter {host}:{port} nicht erreichbar.", ae.InnerException);
            }
            if (!connected)
            {
                tcp.Dispose();
                throw new IOException($"OpenRGB-SDK-Server unter {host}:{port} nicht erreichbar.");
            }
            _tcp = tcp;
            _stream = tcp.GetStream();

            // Protokollversion aushandeln. Sehr alte Server (v0) antworten nicht.
            var verPayload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(verPayload, ClientProtocolVersion);
            SendNoLock(0, PktRequestProtocolVersion, verPayload);
            try
            {
                var reply = ReceiveNoLock(PktRequestProtocolVersion, 1500);
                ServerProtocolVersion = BinaryPrimitives.ReadUInt32LittleEndian(reply);
            }
            catch (TimeoutException)
            {
                ServerProtocolVersion = 0;
            }
            ProtocolVersion = Math.Min(ServerProtocolVersion, ClientProtocolVersion);

            var name = Encoding.UTF8.GetBytes(clientName + "\0");
            SendNoLock(0, PktSetClientName, name);
        }
    }

    public int GetControllerCount()
    {
        lock (_io)
        {
            SendNoLock(0, PktRequestControllerCount, Array.Empty<byte>());
            return (int)BinaryPrimitives.ReadUInt32LittleEndian(ReceiveNoLock(PktRequestControllerCount));
        }
    }

    public RgbDevice GetController(int index)
    {
        lock (_io)
        {
            var p = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(p, ProtocolVersion);
            SendNoLock((uint)index, PktRequestControllerData, p);
            var data = ReceiveNoLock(PktRequestControllerData);
            var dev = ParseController(data, ProtocolVersion);
            dev.Index = index;
            return dev;
        }
    }

    public List<RgbDevice> GetAllControllers()
    {
        int n = GetControllerCount();
        var list = new List<RgbDevice>(n);
        for (int i = 0; i < n; i++) list.Add(GetController(i));
        DeviceListChanged = false;
        return list;
    }

    /// <summary>Setzt den Modus für das gesamte Gerät (entspricht "Entire Device" in der OpenRGB-Oberfläche).</summary>
    public void UpdateMode(int deviceIndex, int modeIndex, RgbMode mode)
    {
        var w = new PacketWriter();
        w.U32(0); // Platzhalter data_size
        w.I32(modeIndex);
        WriteMode(w, mode, ProtocolVersion);
        var buf = w.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)buf.Length);
        lock (_io) SendNoLock((uint)deviceIndex, PktUpdateMode, buf);
    }

    /// <summary>Setzt alle LEDs des Geräts (gesamtes Gerät).</summary>
    public void UpdateLeds(int deviceIndex, IReadOnlyList<RgbColor> colors)
    {
        var w = new PacketWriter();
        w.U32(0);
        w.U16((ushort)colors.Count);
        foreach (var c in colors) w.U32(c.ToWire());
        var buf = w.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)buf.Length);
        lock (_io) SendNoLock((uint)deviceIndex, PktUpdateLeds, buf);
    }

    /// <summary>Liest asynchron eingetroffene Pakete (z. B. Geräteliste geändert), ohne zu blockieren.</summary>
    public void Poll()
    {
        lock (_io)
        {
            if (_stream == null) return;
            while (_stream.DataAvailable)
            {
                var (_, id, payload) = ReadPacketNoLock();
                if (id == PktDeviceListUpdated) DeviceListChanged = true;
                _ = payload;
            }
        }
    }

    // ---------------------------------------------------------------- Transport

    private void SendNoLock(uint devIdx, uint pktId, byte[] payload)
    {
        if (_stream == null) throw new IOException("Nicht verbunden.");
        var header = new byte[16];
        header[0] = (byte)'O'; header[1] = (byte)'R'; header[2] = (byte)'G'; header[3] = (byte)'B';
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), devIdx);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), pktId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)payload.Length);
        try
        {
            _stream.Write(header);
            if (payload.Length > 0) _stream.Write(payload);
        }
        catch
        {
            CloseNoLock();
            throw;
        }
    }

    private byte[] ReceiveNoLock(uint expectedId, int timeoutMs = 5000)
    {
        if (_tcp == null) throw new IOException("Nicht verbunden.");
        var old = _tcp.ReceiveTimeout;
        _tcp.ReceiveTimeout = timeoutMs;
        try
        {
            while (true)
            {
                var (_, id, payload) = ReadPacketNoLock();
                if (id == expectedId) return payload;
                if (id == PktDeviceListUpdated) DeviceListChanged = true;
            }
        }
        catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
        {
            throw new TimeoutException("Keine Antwort vom OpenRGB-Server.", ex);
        }
        finally
        {
            if (_tcp != null) _tcp.ReceiveTimeout = old;
        }
    }

    private (uint dev, uint id, byte[] payload) ReadPacketNoLock()
    {
        var s = _stream ?? throw new IOException("Nicht verbunden.");
        var header = new byte[16];
        ReadExactly(s, header);
        if (header[0] != 'O' || header[1] != 'R' || header[2] != 'G' || header[3] != 'B')
        {
            CloseNoLock();
            throw new IOException("Ungültiger Paketkopf vom OpenRGB-Server.");
        }
        uint dev = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (size > 16 * 1024 * 1024) { CloseNoLock(); throw new IOException("Paket zu groß."); }
        var payload = new byte[size];
        ReadExactly(s, payload);
        return (dev, id, payload);
    }

    private void ReadExactly(NetworkStream s, byte[] buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n;
            try { n = s.Read(buf, off, buf.Length - off); }
            catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut }) { throw; }
            catch { CloseNoLock(); throw; }
            if (n <= 0) { CloseNoLock(); throw new IOException("Verbindung von OpenRGB getrennt."); }
            off += n;
        }
    }

    private void CloseNoLock()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
    }

    public void Dispose()
    {
        lock (_io) CloseNoLock();
    }

    // ---------------------------------------------------------------- Serialisierung

    internal static RgbDevice ParseController(byte[] data, uint proto)
    {
        var r = new PacketReader(data);
        r.U32(); // data_size
        var d = new RgbDevice { Type = (DeviceType)r.I32(), Name = r.Str() };
        if (proto >= 1) d.Vendor = r.Str();
        d.Description = r.Str();
        d.Version = r.Str();
        d.Serial = r.Str();
        d.Location = r.Str();

        int numModes = r.U16();
        d.ActiveMode = r.I32();
        for (int i = 0; i < numModes; i++) d.Modes.Add(ReadMode(r, proto));

        int numZones = r.U16();
        for (int i = 0; i < numZones; i++)
        {
            var z = new RgbZone { Name = r.Str(), Type = r.I32(), LedsMin = r.U32(), LedsMax = r.U32(), LedsCount = r.U32() };
            int matrixLen = r.U16();
            if (matrixLen > 0) r.Skip(matrixLen);
            if (proto >= 4)
            {
                int segs = r.U16();
                for (int s = 0; s < segs; s++) { r.Str(); r.I32(); r.U32(); r.U32(); }
            }
            d.Zones.Add(z);
        }

        int numLeds = r.U16();
        for (int i = 0; i < numLeds; i++) { d.Leds.Add(r.Str()); r.U32(); }

        int numColors = r.U16();
        for (int i = 0; i < numColors; i++) d.Colors.Add(RgbColor.FromWire(r.U32()));
        return d;
    }

    private static RgbMode ReadMode(PacketReader r, uint proto)
    {
        var m = new RgbMode { Name = r.Str(), Value = r.I32(), Flags = (ModeFlags)r.U32(), SpeedMin = r.U32(), SpeedMax = r.U32() };
        if (proto >= 3) { m.BrightnessMin = r.U32(); m.BrightnessMax = r.U32(); }
        m.ColorsMin = r.U32();
        m.ColorsMax = r.U32();
        m.Speed = r.U32();
        if (proto >= 3) m.Brightness = r.U32();
        m.Direction = r.U32();
        m.ColorMode = (ColorMode)r.U32();
        int n = r.U16();
        for (int i = 0; i < n; i++) m.Colors.Add(RgbColor.FromWire(r.U32()));
        return m;
    }

    private static void WriteMode(PacketWriter w, RgbMode m, uint proto)
    {
        w.Str(m.Name);
        w.I32(m.Value);
        w.U32((uint)m.Flags);
        w.U32(m.SpeedMin);
        w.U32(m.SpeedMax);
        if (proto >= 3) { w.U32(m.BrightnessMin); w.U32(m.BrightnessMax); }
        w.U32(m.ColorsMin);
        w.U32(m.ColorsMax);
        w.U32(m.Speed);
        if (proto >= 3) w.U32(m.Brightness);
        w.U32(m.Direction);
        w.U32((uint)m.ColorMode);
        w.U16((ushort)m.Colors.Count);
        foreach (var c in m.Colors) w.U32(c.ToWire());
    }

    private sealed class PacketReader(byte[] data)
    {
        private int _pos;
        private ReadOnlySpan<byte> Take(int n)
        {
            if (_pos + n > data.Length) throw new InvalidDataException($"Gerätedaten zu kurz (Offset {_pos}, benötigt {n}, Länge {data.Length}).");
            var s = data.AsSpan(_pos, n);
            _pos += n;
            return s;
        }
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public void Skip(int n) => Take(n);
        public string Str()
        {
            int len = U16();
            var bytes = Take(len);
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
    }

    private sealed class PacketWriter
    {
        private readonly MemoryStream _ms = new();
        public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); _ms.Write(b); }
        public void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); _ms.Write(b); }
        public void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); _ms.Write(b); }
        public void Str(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s + "\0");
            U16((ushort)bytes.Length);
            _ms.Write(bytes);
        }
        public byte[] ToArray() => _ms.ToArray();
    }
}
