using Silverbud.Core;

int failures = 0;

void Check(string name, bool condition)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {name}");
    if (!condition)
        failures++;
}

// CRC-16/XMODEM reference value
Check("crc16 of \"123456789\" is 0x31C3", Crc16.Compute("123456789"u8) == 0x31C3);

// A real EXTENDED_STATUS_UPDATED frame captured from Galaxy Buds (GalaxyBudsClient test data, BudsFe rev 2).
// Bytes 0..41 of its payload use the same layout as the Buds3.
byte[] captured = Convert.FromHexString(
    "FD3110610206" + "3A59010011000000" + "8F3300004A014A0103000465" +
    "0001001000000101110300000000E80300009D0E1D005000C2CFDD");

Check("captured frame parses (SOM, size, CRC, EOM)", FrameReader.TryParse(captured, out var frame));
Check("message id is EXTENDED_STATUS_UPDATED", frame?.Id == MsgId.ExtendedStatusUpdated);
Check("payload is 46 bytes", frame?.Payload.Length == 46);

var s = StatusParser.ParseExtended(frame!.Payload);
Check("battery L 58 / R 89 / case 0", s.Left == 58 && s.Right == 89 && s.Case == 0);
Check("both buds worn", s.PlacementL == Placement.Wearing && s.PlacementR == Placement.Wearing);
Check("EQ normal, noise control off", s.EqMode == 0 && s.NoiseControl == 0);
Check("touch controls on, all gestures on, call gestures off",
    s.TouchEnabled && s.PinchOn && s.DoublePinchOn && s.TriplePinchOn && s.PinchHoldOn && !s.DoublePinchCallOn && !s.PinchHoldCallOn);
Check("pinch-and-hold = volume on both sides", s.HoldActionL == 3 && s.HoldActionR == 3);
Check("seamless connection on", s.Seamless);
Check("ambient volume 1, balance centred", s.AmbientVolume == 1 && s.Balance == 16);
Check("noise controls with one earbud on", s.NoiseControlsWithOneEarbud);
Check("custom ambient on, L1 R1 tone 3", s.AmbientCustomOn && s.AmbientCustomL == 1 && s.AmbientCustomR == 1 && s.AmbientTone == 3);
Check("edge double-tap off, sidetone off, in-ear call detection on", !s.DoubleTapEdgeVolume && !s.Sidetone && s.CallPathControl);

// Re-encoding the captured message must give the same id/payload/CRC (header type bit aside)
var reencoded = new SppFrame(frame.Id, frame.Payload).Encode();
Check("re-encoded CRC matches the captured CRC", reencoded[^3] == captured[^3] && reencoded[^2] == captured[^2]);

// Outgoing command: noise canceling on → FD 04 00 78 01 <crc> DD
var nc = new SppFrame(MsgId.NoiseControls, 1).Encode();
Check("NOISE_CONTROLS frame layout", nc.Length == 8 && nc[0] == 0xFD && nc[1] == 4 && nc[2] == 0 && nc[3] == 120 && nc[4] == 1 && nc[^1] == 0xDD);
Check("NOISE_CONTROLS frame round-trips", FrameReader.TryParse(nc, out var back) && back.Id == 120 && back.Payload.SequenceEqual(new byte[] { 1 }));

// Stream reassembly: garbage, a frame split across reads, then two frames in one read
var reader = new FrameReader();
var got = new List<SppFrame>();
got.AddRange(reader.Push(new byte[] { 0x00, 0x13, 0xFD, 0xFD }));
got.AddRange(reader.Push(captured.AsSpan(0, 20)));
got.AddRange(reader.Push(captured.AsSpan(20)));
var status = new SppFrame(MsgId.StatusUpdated, 1, 70, 71, 1, 0, 0x33, 55, 0x11).Encode();
got.AddRange(reader.Push(status.Concat(nc).ToArray()));
Check("reader recovers 3 frames from noisy, split stream", got.Count == 3 && got[0].Id == 97 && got[1].Id == 96 && got[2].Id == 120);

var battery = StatusParser.ParseStatus(got[1].Payload);
Check("STATUS_UPDATED: L70 R71 case55, in case, L+case charging",
    battery.Left == 70 && battery.Right == 71 && battery.Case == 55 && battery.PlacementL == Placement.InCase &&
    battery.ChargingL && !battery.ChargingR && battery.ChargingCase);

Check("corrupted CRC is rejected", !FrameReader.TryParse(captured.Select((b, i) => i == 10 ? (byte)(b ^ 0xFF) : b).ToArray(), out _));

// Live capture from a Galaxy Buds3 (firmware R530XXU0AYG3), 2026-11-01.
// Right bud worn, left out of ear, Bass boost, all pinch gestures on, pinch-and-hold = noise controls.
var buds3 = StatusParser.ParseExtended(Convert.FromHexString(
    "04056449010121160001FF2200004D014D01000004CC000004100001000000000000000000000000000000000000000000000000010100000000"));
Check("Buds3 live: L100 R73 case22", buds3.Left == 100 && buds3.Right == 73 && buds3.Case == 22);
Check("Buds3 live: left out of ear, right worn, nothing charging",
    buds3.PlacementL == Placement.Idle && buds3.PlacementR == Placement.Wearing && !buds3.ChargingL && !buds3.ChargingR && !buds3.ChargingCase);
Check("Buds3 live: bass boost, noise control off, game mode off", buds3.EqMode == 1 && buds3.NoiseControl == 0 && !buds3.GameMode);
Check("Buds3 live: every pinch gesture on", buds3.TouchEnabled && buds3.PinchOn && buds3.DoublePinchOn && buds3.TriplePinchOn &&
    buds3.PinchHoldOn && buds3.DoublePinchCallOn && buds3.PinchHoldCallOn);
Check("Buds3 live: pinch-and-hold = noise controls on both", buds3.HoldActionL == 2 && buds3.HoldActionR == 2);
Check("Buds3 live: seamless on, balance centred, in-ear call detection on", buds3.Seamless && buds3.Balance == 16 && buds3.CallPathControl);

// Live acknowledgement from the Buds3 whose header carries sequence bits (0x50): "noise canceling applied"
Check("Buds3 live: ack with header flag bits parses",
    FrameReader.TryParse(Convert.FromHexString("FD05504278011CE2DD"), out var ack) &&
    ack.Id == MsgId.Ack && ack.Payload.SequenceEqual(new byte[] { MsgId.NoiseControls, 1 }));
var seqReader = new FrameReader();
var seqFrames = seqReader.Push(Convert.FromHexString("FD05504278011CE2DD" + "FD05504278011CE2DD"));
Check("Buds3 live: back-to-back flagged frames both read", seqFrames.Count == 2);

var info = StatusParser.ParseDebugData(Convert.FromHexString(
    "0000523533305858553041594733" + new string('0', 222)));
Check("Buds3 live: firmware R530XXU0AYG3, zeroed sensors reported as unknown",
    info.Firmware == "R530XXU0AYG3" && info.TemperatureL is null && info.VoltageL is null);

Console.WriteLine(failures == 0 ? "\nAll protocol checks passed." : $"\n{failures} check(s) failed.");
return failures == 0 ? 0 : 1;
