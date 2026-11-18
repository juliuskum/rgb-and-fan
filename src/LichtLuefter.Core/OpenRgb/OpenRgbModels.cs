namespace LichtLuefter.Core.OpenRgb;

/// <summary>Farbe im OpenRGB-Format (0x00BBGGRR).</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public uint ToWire() => (uint)(R | (G << 8) | (B << 16));
    public static RgbColor FromWire(uint v) => new((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));

    public static RgbColor FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return new RgbColor(255, 255, 255);
        return new RgbColor(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public RgbColor Scale(double f)
    {
        f = Math.Clamp(f, 0, 1);
        return new((byte)Math.Round(R * f), (byte)Math.Round(G * f), (byte)Math.Round(B * f));
    }

    public static RgbColor Lerp(RgbColor a, RgbColor b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>h in Grad [0,360), s/v in [0,1].</summary>
    public static RgbColor FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        return new((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

public enum DeviceType
{
    Motherboard = 0, Dram = 1, Gpu = 2, Cooler = 3, LedStrip = 4, Keyboard = 5, Mouse = 6, MouseMat = 7,
    Headset = 8, HeadsetStand = 9, Gamepad = 10, Light = 11, Speaker = 12, Virtual = 13, Storage = 14,
    Case = 15, Microphone = 16, Accessory = 17, Keypad = 18, Unknown = 19
}

[Flags]
public enum ModeFlags : uint
{
    None = 0,
    HasSpeed = 1 << 0,
    HasDirectionLR = 1 << 1,
    HasDirectionUD = 1 << 2,
    HasDirectionHV = 1 << 3,
    HasBrightness = 1 << 4,
    HasPerLedColor = 1 << 5,
    HasModeSpecificColor = 1 << 6,
    HasRandomColor = 1 << 7,
    ManualSave = 1 << 8,
    AutomaticSave = 1 << 9,
}

public enum ColorMode : uint { None = 0, PerLed = 1, ModeSpecific = 2, Random = 3 }

public sealed class RgbMode
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
    public ModeFlags Flags { get; set; }
    public uint SpeedMin { get; set; }
    public uint SpeedMax { get; set; }
    public uint BrightnessMin { get; set; }
    public uint BrightnessMax { get; set; }
    public uint ColorsMin { get; set; }
    public uint ColorsMax { get; set; }
    public uint Speed { get; set; }
    public uint Brightness { get; set; }
    public uint Direction { get; set; }
    public ColorMode ColorMode { get; set; }
    public List<RgbColor> Colors { get; set; } = new();

    public RgbMode Clone() => new()
    {
        Name = Name, Value = Value, Flags = Flags, SpeedMin = SpeedMin, SpeedMax = SpeedMax,
        BrightnessMin = BrightnessMin, BrightnessMax = BrightnessMax, ColorsMin = ColorsMin, ColorsMax = ColorsMax,
        Speed = Speed, Brightness = Brightness, Direction = Direction, ColorMode = ColorMode, Colors = new(Colors)
    };

    public bool Has(ModeFlags f) => (Flags & f) == f;
}

public sealed class RgbZone
{
    public string Name { get; set; } = "";
    public int Type { get; set; }
    public uint LedsMin { get; set; }
    public uint LedsMax { get; set; }
    public uint LedsCount { get; set; }
}

public sealed class RgbDevice
{
    public int Index { get; set; }
    public DeviceType Type { get; set; }
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Location { get; set; } = "";
    public int ActiveMode { get; set; }
    public List<RgbMode> Modes { get; set; } = new();
    public List<RgbZone> Zones { get; set; } = new();
    public List<string> Leds { get; set; } = new();
    public List<RgbColor> Colors { get; set; } = new();

    public int FindMode(string name) => Modes.FindIndex(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
}
