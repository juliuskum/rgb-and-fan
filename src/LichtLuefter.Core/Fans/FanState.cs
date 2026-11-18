using System.IO.MemoryMappedFiles;
using System.Text.Json;
using LichtLuefter.Core.Hardware;

namespace LichtLuefter.Core.Fans;

/// <summary>
/// Auf Platte gesicherter Zustand: Welche Kanäle steuert die App, und welcher BIOS-Befehlswert gehört dazu.
/// Wird geschrieben, BEVOR der erste Kanal übernommen wird, und erst gelöscht, NACHDEM alle zurückgegeben sind.
/// Damit kann der Watchdog (oder der nächste App-Start) nach einem Absturz sicher zurück aufs BIOS stellen.
/// </summary>
public sealed class FanStateFile
{
    public int OwnerPid { get; set; }
    public DateTime WrittenUtc { get; set; }
    public List<ChannelEntry> Channels { get; set; } = new();

    public sealed class ChannelEntry
    {
        public int Index { get; set; }
        public byte Command { get; set; }
    }

    public IEnumerable<BiosChannelState> States => Channels.Select(c => new BiosChannelState(c.Index, c.Command));

    public static FanStateFile? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.FanState)) return null;
            return JsonSerializer.Deserialize<FanStateFile>(File.ReadAllText(AppPaths.FanState));
        }
        catch (Exception ex)
        {
            Log.Write("Zustandsdatei unlesbar: " + ex.Message);
            // Unlesbar heißt: wir wissen nicht, was gesteuert wurde -> der Aufrufer stellt vorsorglich alles zurück.
            return AllChannels();
        }
    }

    /// <summary>
    /// Ersatz, wenn nichts Genaueres bekannt ist: alle 8 Kanäle, Befehlsregister 0x66 (auf diesem Board vor jeder
    /// Übernahme gemessen; im BIOS-Modus ohnehin ohne Wirkung).
    /// </summary>
    public static FanStateFile AllChannels() => new()
    {
        OwnerPid = -1,
        Channels = Enumerable.Range(0, 8).Select(i => new ChannelEntry { Index = i, Command = 0x66 }).ToList(),
    };

    public static void Save(IEnumerable<BiosChannelState> states)
    {
        Directory.CreateDirectory(AppPaths.Root);
        var f = new FanStateFile
        {
            OwnerPid = Environment.ProcessId,
            WrittenUtc = DateTime.UtcNow,
            Channels = states.Select(s => new ChannelEntry { Index = s.Index, Command = s.Command }).ToList(),
        };
        var tmp = AppPaths.FanState + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(f));
        File.Move(tmp, AppPaths.FanState, true);
    }

    public static void Delete()
    {
        try { File.Delete(AppPaths.FanState); } catch { }
    }
}

/// <summary>Lebenszeichen der App im gemeinsamen Speicher (kein Plattenzugriff im Sekundentakt).</summary>
public sealed class Heartbeat : IDisposable
{
    private const string Name = @"Local\LichtLuefter.Heartbeat";
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;

    public Heartbeat()
    {
        _mmf = MemoryMappedFile.CreateOrOpen(Name, 16);
        _view = _mmf.CreateViewAccessor(0, 16);
    }

    /// <summary>
    /// Monotone Systemzeit in 100-ns-Schritten, für alle Prozesse gleich, ohne Ruhezustand.
    /// Bewusst nicht DateTime.UtcNow: ein Sprung der Uhr (Zeitabgleich) würde die App fälschlich als hängend erscheinen lassen.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool QueryUnbiasedInterruptTime(out long time);

    private static long Now() => QueryUnbiasedInterruptTime(out long t) ? t : Environment.TickCount64 * 10_000;

    public void Beat() => _view.Write(0, Now());

    public TimeSpan Age
    {
        get
        {
            long t = _view.ReadInt64(0);
            if (t == 0) return TimeSpan.Zero;
            long d = Now() - t;
            return d < 0 ? TimeSpan.Zero : TimeSpan.FromTicks(d);
        }
    }

    public void Dispose()
    {
        _view.Dispose();
        _mmf.Dispose();
    }
}
