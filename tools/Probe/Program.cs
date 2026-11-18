using LichtLuefter.Core.OpenRgb;

var mode = args.Length > 0 ? args[0] : "sdk";
var outFile = args.Length > 1 ? args[1] : null;
var log = new StringWriter();
void W(string s) { Console.WriteLine(s); log.WriteLine(s); }

try
{
    switch (mode)
    {
        case "sdk": ProbeSdk(); break;
        case "fansweep": FanSweep.Run(W, args.Length > 2 ? args[2] : "trace.csv"); break;
        case "latency": Latency.Run(W); break;
        case "switch": Switch.Run(W); break;
        case "temps": Temps.Run(W); break;
        case "listen4": Listen4.Run(W); break;
        case "listen3": Listen3.Run(W); break;
        case "listen2": Listen2.Run(W); break;
        case "listen": Listen.Run(W); break;
        case "readback": Readback.Run(W); break;
        case "sensors": FanTest.Run(W, false); break;
        case "fantest": FanTest.Run(W, true); break;
        case "regs": Regs.Run(W, args.Length > 2 ? int.Parse(args[2]) : 15); break;
        default: W("Unbekannter Modus " + mode); break;
    }
}
catch (Exception ex) { W("FEHLER: " + ex); }
finally { if (outFile != null) File.WriteAllText(outFile, log.ToString()); }

void ProbeSdk()
{
    using var c = new OpenRgbClient();
    c.Connect(clientName: "Licht & Lüfter (Probe)");
    W($"Server-Protokoll {c.ServerProtocolVersion}, genutzt {c.ProtocolVersion}");
    foreach (var d in c.GetAllControllers())
    {
        W($"\n[{d.Index}] {d.Name} | Typ {d.Type} | Hersteller {d.Vendor} | aktiver Modus {d.ActiveMode} ({(d.ActiveMode >= 0 && d.ActiveMode < d.Modes.Count ? d.Modes[d.ActiveMode].Name : "?")})");
        W($"    Ort: {d.Location} | Version {d.Version}");
        for (int i = 0; i < d.Modes.Count; i++)
        {
            var m = d.Modes[i];
            W($"    Modus {i,2}: {m.Name,-24} val={m.Value,-3} flags=[{m.Flags}] colorMode={m.ColorMode} colors={m.ColorsMin}-{m.ColorsMax} ({m.Colors.Count}) speed={m.SpeedMin}-{m.SpeedMax}:{m.Speed} bright={m.BrightnessMin}-{m.BrightnessMax}:{m.Brightness} dir={m.Direction}");
        }
        foreach (var z in d.Zones) W($"    Zone: {z.Name} type={z.Type} leds={z.LedsCount} ({z.LedsMin}-{z.LedsMax})");
        W($"    LEDs: {d.Leds.Count}, Farben: {d.Colors.Count}");
    }
}
