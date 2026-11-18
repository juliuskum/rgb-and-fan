using System.Reflection;
using LibreHardwareMonitor.Hardware;

namespace LichtLuefter.Core.Hardware;

/// <summary>Gesicherter BIOS-Zustand eines Kanals (vor der Übernahme gelesen).</summary>
public readonly record struct BiosChannelState(int Index, byte Command);

/// <summary>
/// Direkter, verifizierter Zugriff auf den Lüfter-Controller Nuvoton NCT6687D (MSI MAG Z790 TOMAHAWK WIFI),
/// aufgebaut auf LibreHardwareMonitorLib 0.9.6 (Treiberzugriff über PawnIO).
///
/// Register (EC-Adressraum, gemessen auf diesem Board):
///   0xA00        Modus, ein Bit je Kanal: 0 = BIOS-Kurve (Smart Fan), 1 = manuell
///   0xA01        Konfigurationsphase: 0x80 anfordern, 0x40 abschließen
///   0xA28 + i    manueller PWM-Befehl (0–255)
///   0x160 + i    tatsächlich ausgegebener PWM-Wert
///
/// Warum nicht LHMs IControl.SetSoftware()/SetDefault()?
///   * LHM 0.9.6 schreibt beim Zurücksetzen das Modusbit AUSSERHALB der Konfigurationsphase. Der MSI-EC
///     ignoriert das: Die Kanäle bleiben manuell auf dem alten Befehlswert stehen. Auf diesem Board
///     nachgewiesen (0xA00 blieb 0xFF, alle Kanäle inkl. Pumpe fest auf 40 %).
///   * LHM bricht bei belegtem ISA-Mutex stillschweigend ab; verlorene Schreibvorgänge wären unsichtbar.
/// Daher wird hier jeder Schreibvorgang in der Phase ausgeführt und anschließend zurückgelesen.
///
/// Interne LHM-Methoden (ReadByte/WriteByte) werden per Reflection genutzt. Die Paketversion ist im csproj
/// fest gepinnt; fehlt etwas, meldet <see cref="IsSupported"/> false und die App greift nicht ein.
/// </summary>
public sealed class SuperIoAccess
{
    public const ushort RegMode = 0xA00, RegRequest = 0xA01, RegCmdBase = 0xA28, RegOutBase = 0x160;
    private const byte CfgRequest = 0x80, CfgDone = 0x40;
    public const string IsaMutexName = @"Global\Access_ISABUS.HTP.Method";

    private readonly object _superIo;
    private readonly PropertyInfo? _controls;
    private readonly PropertyInfo? _fans;
    private readonly MethodInfo? _read;
    private readonly MethodInfo? _write;

    public string ChipName { get; }
    public string? UnsupportedReason { get; }
    public bool IsSupported => UnsupportedReason == null;
    public int ChannelCount => 8;

    private SuperIoAccess(object superIo)
    {
        _superIo = superIo;
        var t = superIo.GetType();
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        _controls = t.GetProperty("Controls", inst);
        _fans = t.GetProperty("Fans", inst);
        _read = t.GetMethod("ReadByte", inst, [typeof(ushort)]);
        _write = t.GetMethod("WriteByte", inst, [typeof(ushort), typeof(byte)]);
        ChipName = t.GetProperty("Chip", inst)?.GetValue(superIo)?.ToString() ?? "?";

        if (t.Name != "Nct677X") UnsupportedReason = $"Unbekannter Super-I/O-Treiber {t.Name}.";
        else if (ChipName != "NCT6687D") UnsupportedReason = $"Chip {ChipName} ist nicht für dieses Board geprüft (erwartet NCT6687D).";
        else if (_controls == null || _fans == null || _read == null || _write == null)
            UnsupportedReason = "LibreHardwareMonitorLib-Version passt nicht (interne Methoden fehlen).";
    }

    /// <summary>Sucht den Super-I/O-Chip im LHM-Hardwarebaum.</summary>
    public static SuperIoAccess? Find(IComputer computer)
    {
        foreach (var hw in computer.Hardware)
        {
            if (hw.HardwareType != HardwareType.Motherboard) continue;
            foreach (var sub in hw.SubHardware)
            {
                if (sub.HardwareType != HardwareType.SuperIO) continue;
                var f = sub.GetType().GetField("_superIO", BindingFlags.Instance | BindingFlags.NonPublic);
                var sio = f?.GetValue(sub);
                if (sio != null) return new SuperIoAccess(sio);
            }
        }
        return null;
    }

    public float?[] Controls => (float?[])_controls!.GetValue(_superIo)!;
    public float?[] Fans => (float?[])_fans!.GetValue(_superIo)!;

    private byte Read(ushort a) => (byte)_read!.Invoke(_superIo, [a])!;
    private void Write(ushort a, byte v) => _write!.Invoke(_superIo, [a, v]);

    /// <summary>Führt eine Aktion unter dem globalen ISA-Bus-Mutex aus (derselbe wie LHM, HWiNFO, OpenRGB).</summary>
    private static bool WithIsaBus(Action a, int timeoutMs)
    {
        using var m = new Mutex(false, IsaMutexName);
        bool got;
        try { got = m.WaitOne(timeoutMs); }
        catch (AbandonedMutexException) { got = true; }
        if (!got) return false;
        try { a(); }
        finally { m.ReleaseMutex(); }
        return true;
    }

    /// <summary>Liest Modusregister. Bit i gesetzt = Kanal i manuell. null = Bus belegt.</summary>
    public byte? ReadModeRegister()
    {
        byte v = 0;
        return WithIsaBus(() => v = Read(RegMode), 500) ? v : null;
    }

    public bool? IsManual(int index) => ReadModeRegister() is byte m ? (m & (1 << index)) != 0 : null;

    /// <summary>Sichert den Zustand eines Kanals vor der Übernahme.</summary>
    public BiosChannelState? ReadBiosState(int index)
    {
        byte cmd = 0;
        return WithIsaBus(() => cmd = Read((ushort)(RegCmdBase + index)), 500) ? new BiosChannelState(index, cmd) : null;
    }

    /// <summary>Setzt Kanal auf manuell mit Tastgrad in Prozent. true = vom Chip bestätigt.</summary>
    public bool SetPercent(int index, double percent)
    {
        byte value = (byte)Math.Clamp(Math.Round(percent * 2.55), 0, 255);
        byte bit = (byte)(1 << index);
        bool ok = WithIsaBus(() =>
        {
            Write(RegRequest, CfgRequest);
            Thread.Sleep(50);
            Write(RegMode, (byte)(Read(RegMode) | bit));
            Write((ushort)(RegCmdBase + index), value);
            Write(RegRequest, CfgDone);
            Thread.Sleep(50);
        }, 500);
        if (!ok) return false;

        bool confirmed = false;
        WithIsaBus(() => confirmed = (Read(RegMode) & bit) != 0 && Read((ushort)(RegCmdBase + index)) == value, 500);
        return confirmed;
    }

    /// <summary>
    /// Gibt den Kanal an die BIOS-Lüfterkurve zurück (Modusbit 0) und stellt das gesicherte Befehlsregister
    /// wieder her. true = Modusbit nachweislich gelöscht.
    /// </summary>
    public bool RestoreBios(int index, byte command)
    {
        byte bit = (byte)(1 << index);
        bool ok = WithIsaBus(() =>
        {
            Write(RegRequest, CfgRequest);
            Thread.Sleep(50);
            Write(RegMode, (byte)(Read(RegMode) & ~bit));
            Write((ushort)(RegCmdBase + index), command);
            Write(RegRequest, CfgDone);
            Thread.Sleep(50);
        }, 500);
        if (!ok) return false;
        return IsManual(index) == false;
    }

    /// <summary>Gibt mehrere Kanäle zurück, mit Wiederholungen. Liefert die Kanäle, bei denen es NICHT gelang.</summary>
    public List<int> RestoreBiosAll(IEnumerable<BiosChannelState> states, int attempts = 10)
    {
        var failed = new List<int>();
        foreach (var s in states)
        {
            bool ok = false;
            for (int a = 0; a < attempts && !ok; a++)
            {
                try { ok = RestoreBios(s.Index, s.Command); } catch { ok = false; }
                if (!ok) Thread.Sleep(100);
            }
            if (!ok) failed.Add(s.Index);
        }
        return failed;
    }
}
