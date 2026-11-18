using System.Reflection;
using LichtLuefter.Core.Hardware;

/// <summary>Liest NCT6687D-Register (nur lesen) unter dem globalen ISA-Mutex.</summary>
static class Regs
{
    public static void Run(Action<string> W, int seconds)
    {
        using var hm = new HardwareMonitor();
        hm.Open();
        var sio = hm.SuperIo!;
        var obj = typeof(SuperIoAccess).GetField("_superIo", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sio)!;
        var rb = obj.GetType().GetMethod("ReadByte", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(ushort)])!;
        using var mtx = Mutex.OpenExisting(@"Global\Access_ISABUS.HTP.Method");
        byte R(int a) => (byte)rb.Invoke(obj, [(ushort)a])!;
        for (int s = 0; s < seconds; s += 3)
        {
            hm.Update();
            string line;
            mtx.WaitOne(1000);
            try
            {
                line = $"mode A00=0x{R(0xA00):X2} req A01=0x{R(0xA01):X2} | cmd A28..: {string.Join(" ", Enumerable.Range(0, 8).Select(i => R(0xA28 + i).ToString("X2")))} | out 160..: {string.Join(" ", Enumerable.Range(0, 8).Select(i => R(0x160 + i).ToString("X2")))}";
            }
            finally { mtx.ReleaseMutex(); }
            W($"CPU {hm.CpuTemp:0} °C GPU {hm.GpuTemp:0} °C | RPM {string.Join(" ", hm.Headers.Where(h => h.Rpm > 0).Select(h => $"{h.Index}:{h.Rpm:0}"))} | {line}");
            Thread.Sleep(3000);
        }
    }
}
