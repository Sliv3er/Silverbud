using System;
using System.Text;

namespace Silverbud.Core;

public enum Placement
{
    Disconnected = 0,
    Wearing = 1,
    Idle = 2,
    InCase = 3,
    ClosedCase = 4
}

/// <summary>Battery + placement, carried by both STATUS_UPDATED and EXTENDED_STATUS_UPDATED.</summary>
public class BatteryStatus
{
    public int Left { get; set; }
    public int Right { get; set; }
    public int Case { get; set; }
    public Placement PlacementL { get; set; }
    public Placement PlacementR { get; set; }
    public bool ChargingL { get; set; }
    public bool ChargingR { get; set; }
    public bool ChargingCase { get; set; }
}

/// <summary>Full settings snapshot the buds send after connecting and whenever something changes.</summary>
public sealed class ExtendedStatus : BatteryStatus
{
    public int Revision { get; set; }
    public bool GameMode { get; set; }
    public int EqMode { get; set; }
    public bool TouchEnabled { get; set; }
    public bool PinchOn { get; set; }
    public bool DoublePinchOn { get; set; }
    public bool TriplePinchOn { get; set; }
    public bool PinchHoldOn { get; set; }
    public bool DoublePinchCallOn { get; set; }
    public bool PinchHoldCallOn { get; set; }
    public int HoldActionL { get; set; }
    public int HoldActionR { get; set; }
    public int NoiseControl { get; set; }
    public bool Seamless { get; set; }
    public int AmbientVolume { get; set; }
    public int Balance { get; set; } = 16;
    public bool NoiseControlsWithOneEarbud { get; set; }
    public bool AmbientCustomOn { get; set; }
    public int AmbientCustomL { get; set; }
    public int AmbientCustomR { get; set; }
    public int AmbientTone { get; set; }
    public bool DoubleTapEdgeVolume { get; set; }
    public bool Sidetone { get; set; }
    public bool CallPathControl { get; set; }
}

public sealed class DeviceDiagnostics
{
    public string Firmware { get; set; } = "";
    public string Hardware { get; set; } = "";
    public double? TemperatureL { get; set; }
    public double? TemperatureR { get; set; }
    public double? VoltageL { get; set; }
    public double? VoltageR { get; set; }
}

public static class BudsModel
{
    /// <summary>
    /// Ambient sound exists on the Buds3 Pro only. The open-fit Buds3 (SM-R530, firmware "R530…") already let the room in,
    /// and answer a switch to ambient by staying in their current mode. Unknown until the firmware version arrives.
    /// </summary>
    public static bool HasAmbientSound(string firmware) =>
        firmware.Length >= 4 && char.IsLetter(firmware[0]) && !firmware.StartsWith("R530", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Payload layouts for the Galaxy Buds3 (Buds3 Pro shares everything read here).</summary>
public static class StatusParser
{
    public static BatteryStatus ParseStatus(byte[] p)
    {
        // [0] revision [1] L [2] R [3] coupled [4] main side [5] placement L|R [6] case [7] charging bits
        var s = new BatteryStatus();
        if (p.Length < 7)
            return s;
        s.Left = p[1];
        s.Right = p[2];
        SetPlacement(s, p[5]);
        s.Case = p[6];
        if (p.Length > 7)
            SetCharging(s, p[7]);
        return s;
    }

    public static ExtendedStatus ParseExtended(byte[] p)
    {
        byte At(int i) => i < p.Length ? p[i] : (byte)0;
        bool Bit(int i, int bit) => (At(i) & (1 << bit)) != 0;

        var s = new ExtendedStatus
        {
            Revision = At(0),
            Left = At(2),
            Right = At(3),
            Case = At(7),
            GameMode = At(8) == 1,
            EqMode = At(9),
            PinchHoldOn = Bit(10, 0),
            TriplePinchOn = Bit(10, 1),
            DoublePinchOn = Bit(10, 2),
            PinchOn = Bit(10, 3),
            DoublePinchCallOn = Bit(10, 4),
            PinchHoldCallOn = Bit(10, 5),
            TouchEnabled = Bit(10, 7),
            HoldActionL = (At(11) & 0xF0) >> 4,
            HoldActionR = At(11) & 0x0F,
            NoiseControl = At(12),
            Seamless = At(19) == 0,
            AmbientVolume = At(23),
            Balance = p.Length > 25 ? p[25] : 16,
            NoiseControlsWithOneEarbud = At(28) == 1,
            AmbientCustomOn = At(29) == 1,
            AmbientCustomL = (At(30) & 0xF0) >> 4,
            AmbientCustomR = At(30) & 0x0F,
            AmbientTone = At(31),
            DoubleTapEdgeVolume = At(32) == 1,
            Sidetone = At(33) == 1,
            CallPathControl = At(34) == 0,
        };
        SetPlacement(s, At(6));
        if (p.Length > 42)
            SetCharging(s, p[42]);
        return s;
    }

    public static DeviceDiagnostics ParseDebugData(byte[] p)
    {
        var d = new DeviceDiagnostics();
        if (p.Length < 22)
            return d;
        d.Hardware = $"rev{(p[1] & 0xF0) >> 4:X}.{p[1] & 0x0F:X}";
        // Buds3 and newer send the full firmware name as 20 ASCII bytes
        var sb = new StringBuilder();
        for (int i = 2; i < 22; i++)
            if (p[i] is >= 0x20 and < 0x7F)
                sb.Append((char)p[i]);
        d.Firmware = sb.ToString();

        const int sw = 20;
        if (p.Length > sw + 48)
        {
            double tl = BitConverter.ToInt16(p, sw + 35) * 0.1, tr = BitConverter.ToInt16(p, sw + 37) * 0.1;
            double vl = BitConverter.ToInt16(p, sw + 41) * 0.01, vr = BitConverter.ToInt16(p, sw + 47) * 0.01;
            // Buds3 firmware R530XXU0AYG3 leaves the sensor block zeroed; treat zero as "not reported"
            d.TemperatureL = tl is > 1 and < 90 ? tl : null;
            d.TemperatureR = tr is > 1 and < 90 ? tr : null;
            d.VoltageL = vl is > 2 and < 5 ? vl : null;
            d.VoltageR = vr is > 2 and < 5 ? vr : null;
        }
        return d;
    }

    private static void SetPlacement(BatteryStatus s, byte b)
    {
        s.PlacementL = (Placement)((b & 0xF0) >> 4);
        s.PlacementR = (Placement)(b & 0x0F);
    }

    private static void SetCharging(BatteryStatus s, byte b)
    {
        s.ChargingL = (b & 0x10) != 0;
        s.ChargingR = (b & 0x04) != 0;
        s.ChargingCase = (b & 0x01) != 0;
    }
}
