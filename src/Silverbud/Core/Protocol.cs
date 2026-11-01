using System;
using System.Collections.Generic;

namespace Silverbud.Core;

/// <summary>
/// Message ids of the Galaxy Buds control protocol (RFCOMM). Values follow the
/// reverse-engineering work of the open-source GalaxyBudsClient project.
/// </summary>
public static class MsgId
{
    public const byte DebugGetAllData = 38;
    public const byte Ack = 66;
    public const byte Reset = 80;
    public const byte StatusUpdated = 96;
    public const byte ExtendedStatusUpdated = 97;
    public const byte SetCallPathControl = 110;
    public const byte SetAncWithOneEarbud = 111;
    public const byte PairingMode = 114;
    public const byte NoiseControlsUpdate = 119;
    public const byte NoiseControls = 120;
    public const byte CustomizeAmbientSound = 130;
    public const byte AmbientVolume = 132;
    public const byte AdjustSoundSync = 133;
    public const byte Equalizer = 134;
    public const byte ManagerInfo = 136;
    public const byte CustomEqualizerSend = 137;
    public const byte SetSidetone = 139;
    public const byte SetHearingEnhancements = 143;
    public const byte LockTouchpad = 144;
    public const byte TouchUpdated = 145;
    public const byte SetTouchpadOption = 146;
    public const byte OutsideDoubleTap = 149;
    public const byte FindMyEarbudsStart = 160;
    public const byte FindMyEarbudsStop = 161;
    public const byte MuteEarbud = 162;
    public const byte MuteEarbudStatusUpdated = 163;
    public const byte FindMyEarbudsOnWearingStart = 166;
    public const byte SetSeamlessConnection = 175;
    public const byte DebugEvent = 242;
    public const byte DebugEventExtra = 244;
}

/// <summary>CRC-16/XMODEM (poly 0x1021, init 0), as used by the buds.</summary>
public static class Crc16
{
    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int crc = i << 8;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1;
            table[i] = (ushort)crc;
        }
        return table;
    }

    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
            crc = Table[((crc >> 8) ^ b) & 0xFF] ^ (crc << 8);
        return (ushort)crc;
    }
}

/// <summary>
/// One protocol frame: SOM(0xFD) | size (2 bytes LE, 10 bits) | id | payload | crc16 LE | EOM(0xDD).
/// "size" counts id + payload + crc.
/// </summary>
public sealed class SppFrame
{
    public const byte Som = 0xFD;
    public const byte Eom = 0xDD;

    public SppFrame(byte id, params byte[] payload)
    {
        Id = id;
        Payload = payload;
    }

    public byte Id { get; }
    public byte[] Payload { get; }

    public byte[] Encode()
    {
        int size = Payload.Length + 3;
        var packet = new byte[size + 4];
        packet[0] = Som;
        packet[1] = (byte)size;
        packet[2] = (byte)((size >> 8) & 0x03);
        packet[3] = Id;
        Payload.CopyTo(packet, 4);
        ushort crc = Crc16.Compute(packet.AsSpan(3, Payload.Length + 1));
        packet[^3] = (byte)crc;
        packet[^2] = (byte)(crc >> 8);
        packet[^1] = Eom;
        return packet;
    }

    public override string ToString() => $"#{Id} [{Convert.ToHexString(Payload)}]";
}

/// <summary>Reassembles frames from the raw RFCOMM byte stream and resyncs on garbage.</summary>
public sealed class FrameReader
{
    private readonly List<byte> _buffer = new();
    private readonly List<byte> _dropped = new();

    /// <summary>Bytes that never formed a valid frame (for diagnostics).</summary>
    public event Action<byte[]>? Discarded;

    public List<SppFrame> Push(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data.ToArray());
        var frames = new List<SppFrame>();
        while (true)
        {
            int start = _buffer.IndexOf(SppFrame.Som);
            if (start < 0)
            {
                Drop(_buffer.Count);
                break;
            }
            if (start > 0)
                Drop(start);
            if (_buffer.Count < 3)
                break;

            // Only the low 10 bits are the size; the buds use the top bits as flags and a sequence counter.
            int size = (_buffer[1] | (_buffer[2] << 8)) & 0x3FF;
            if (size < 3)
            {
                Drop(1);
                continue;
            }
            int total = size + 4;
            if (_buffer.Count < total)
            {
                // Either a long frame is still arriving, or this SOM was noise (a stray 0xFD would
                // otherwise stall the reader waiting for a bogus long frame). If a complete,
                // CRC-valid frame already starts later in the buffer, it was noise.
                int next = FindCompleteFrame(from: 1);
                if (next < 0)
                    break;
                Drop(next);
                continue;
            }

            if (TryParse(_buffer.GetRange(0, total).ToArray(), out var frame))
            {
                frames.Add(frame);
                _buffer.RemoveRange(0, total);
            }
            else
            {
                Drop(1);
            }
        }
        if (_dropped.Count > 0)
        {
            Discarded?.Invoke(_dropped.ToArray());
            _dropped.Clear();
        }
        return frames;
    }

    private void Drop(int count)
    {
        _dropped.AddRange(_buffer.GetRange(0, count));
        _buffer.RemoveRange(0, count);
    }

    private int FindCompleteFrame(int from)
    {
        for (int i = from; i + 3 <= _buffer.Count; i++)
        {
            if (_buffer[i] != SppFrame.Som)
                continue;
            int total = ((_buffer[i + 1] | (_buffer[i + 2] << 8)) & 0x3FF) + 4;
            if (total >= 7 && i + total <= _buffer.Count && TryParse(_buffer.GetRange(i, total).ToArray(), out _))
                return i;
        }
        return -1;
    }

    public static bool TryParse(byte[] packet, out SppFrame frame)
    {
        frame = null!;
        if (packet.Length < 7 || packet[0] != SppFrame.Som || packet[^1] != SppFrame.Eom)
            return false;
        int size = (packet[1] | (packet[2] << 8)) & 0x3FF;
        if (packet.Length != size + 4)
            return false;
        int payloadLength = size - 3;
        ushort expected = Crc16.Compute(packet.AsSpan(3, payloadLength + 1));
        ushort received = (ushort)(packet[^3] | (packet[^2] << 8));
        if (expected != received)
            return false;
        frame = new SppFrame(packet[3], packet.AsSpan(4, payloadLength).ToArray());
        return true;
    }
}
