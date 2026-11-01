# Galaxy Buds3 control protocol: notes

These notes describe the parts of Samsung's earbud protocol that Silverbud uses. The message ids and base layouts come from the reverse-engineering in [GalaxyBudsClient](https://github.com/timschneeb/GalaxyBudsClient). Everything marked **observed** was checked on real Galaxy Buds3 hardware (firmware `R530XXU0AYG3`, Windows 11, LE Audio pairing).

## Transport

| | |
|---|---|
| Bluetooth profile | RFCOMM (classic Bluetooth), even when audio uses LE Audio |
| Service UUID | `2e73a4ad-332d-41fc-90e2-16bef06523f2`. Windows lists it as **GEARMANAGER**. |
| Who speaks first | The buds send `EXTENDED_STATUS_UPDATED` right after the socket opens |

Opening the RFCOMM socket brings up the classic Bluetooth link on demand. It works while Windows streams audio to the same buds over LE Audio (**observed**).

## Frame format

```
 FD | size (2 bytes, little endian) | id | payload … | crc16 (2 bytes, LE) | DD
```

- `size` = 1 (id) + payload length + 2 (crc). Only the **low 10 bits** are the size.
- The remaining header bits are flags:
  - `0x1000`: message type (request/response)
  - `0x2000`: fragment
  - **`0x4000` / `0x8000`: a rotating sequence counter (observed).** Frames arrive with `0x40`, `0x80` and `0xC0` in the high byte. A parser that rejects frames with these bits set silently drops about half of all acknowledgements.
- CRC is **CRC-16/XMODEM** (poly `0x1021`, init `0`, no reflection) over `id + payload`. Check value: `"123456789"` → `0x31C3`.
- Requests sent from the PC use a header with no flag bits.

Example: noise canceling on → `FD 04 00 78 01 <crc lo> <crc hi> DD`.

## Messages Silverbud uses

| Id | Name | Direction | Payload |
|---:|---|---|---|
| 38 | `DEBUG_GET_ALL_DATA` | request / response | Response: `[1]` hardware revision nibbles, `[2..21]` firmware name in ASCII (Buds3+). The sensor block that follows is all zeros on R530XXU0AYG3 (**observed**). |
| 66 | `ACK` | buds → PC | `[acked id, applied value]`. See [Acknowledgements](#acknowledgements). |
| 80 | `RESET` | PC → buds | Factory reset |
| 96 | `STATUS_UPDATED` | buds → PC | Battery and placement. See below. |
| 97 | `EXTENDED_STATUS_UPDATED` | buds → PC | Full settings snapshot. See below. |
| 110 | `SET_CALL_PATH_CONTROL` | PC → buds | `[0]` = enabled (**inverted**) |
| 111 | `SET_ANC_WITH_ONE_EARBUD` | PC → buds | `[1/0]` |
| 114 | `PAIRING_MODE` | PC → buds | none. The buds disconnect and become discoverable. |
| 119 | `NOISE_CONTROLS_UPDATE` | buds → PC | `[mode]` when changed on the buds |
| 120 | `NOISE_CONTROLS` | PC → buds | `[mode]`: 0 off, 1 noise canceling, 2 ambient (3 adaptive, Pro only) |
| 130 | `CUSTOMIZE_AMBIENT_SOUND` | PC → buds | `[on, left 0–2, right 0–2, tone 0–4]` |
| 132 | `AMBIENT_VOLUME` | PC → buds | `[0–2]` |
| 133 | `ADJUST_SOUND_SYNC` | PC → buds | Game mode `[1/0]` |
| 134 | `EQUALIZER` | PC → buds | `[0]` normal, `[1..5]` bass boost, soft, dynamic, clear, treble boost; `[7]` custom |
| 136 | `MANAGER_INFO` | PC → buds | `[1, client type (1 = Samsung), Android SDK level]`. The buds reply `[00]`. |
| 137 | `CUSTOM_EQUALIZE_SEND` | PC → buds | `[9, gain × 9]`, signed. **Unverified on Buds3.** |
| 139 | `SET_SIDETONE` | PC → buds | Ambient sound during calls `[1/0]` |
| 143 | `SET_HEARING_ENHANCEMENTS` | PC → buds | Left/right balance `[0–32]`, 16 = centred |
| 144 | `LOCK_TOUCHPAD` | PC → buds | `[enabled, pinch, double, triple, hold, double-in-call, hold-in-call]` |
| 145 | `TOUCH_UPDATED` | buds → PC | `[locked]` |
| 146 | `SET_TOUCHPAD_OPTION` | PC → buds | Pinch-and-hold action `[left, right]`: 1 voice assistant, 2 noise controls, 3 volume, 4 Spotify |
| 149 | `OUTSIDE_DOUBLE_TAP` | PC → buds | Double-tap earbud edge for volume `[1/0]` |
| 161 | `FIND_MY_EARBUDS_STOP` | both | Stop ringing; also sent by the buds when ringing stops |
| 162 | `MUTE_EARBUD` | PC → buds | `[left muted, right muted]` |
| 163 | `MUTE_EARBUD_STATUS_UPDATED` | buds → PC | `[left, right]` |
| 166 | `FIND_MY_EARBUDS_ON_WEARING_START` | PC → buds | Start ringing (rings even when worn) |
| 175 | `SET_SEAMLESS_CONNECTION` | PC → buds | `[0]` = enabled (**inverted**) |

Also seen from Buds3 without a request (**observed**):
- `118 MULTIPOINT_INFO`
- `205 CRADLE_SERIAL_NUMBER` (ASCII)
- `242` / `244` debug events every ~2 seconds. These are safe to ignore.

## `STATUS_UPDATED` (96)

| Offset | Meaning |
|---:|---|
| 0 | revision |
| 1 | left battery % |
| 2 | right battery % |
| 3 | coupled |
| 4 | main earbud (1 = left) |
| 5 | placement: high nibble = left, low nibble = right (0 disconnected, 1 in ear, 2 out of ear, 3 in case, 4 in closed case) |
| 6 | case battery % (0 when unknown) |
| 7 | charging bits: `0x10` left, `0x04` right, `0x01` case |

## `EXTENDED_STATUS_UPDATED` (97), Buds3 layout

| Offset | Meaning |
|---:|---|
| 0 | revision (Buds3: 4) |
| 2 / 3 | left / right battery % |
| 6 | placement (as above) |
| 7 | case battery % |
| 8 | game mode |
| 9 | equalizer (as in message 134) |
| 10 | touch flags: `0x80` touch enabled, `0x08` pinch, `0x04` double, `0x02` triple, `0x01` hold, `0x10` double-in-call, `0x20` hold-in-call |
| 11 | pinch-and-hold action: high nibble = left, low = right |
| 12 | noise control mode |
| 19 | seamless connection (**0 = on**) |
| 21 | noise-control cycle bits |
| 23 | ambient level |
| 25 | left/right balance (16 = centred) |
| 28 | noise controls with one earbud |
| 29 | custom ambient on |
| 30 | custom ambient level: high nibble = left, low = right |
| 31 | custom ambient tone |
| 32 | double-tap edge for volume |
| 33 | ambient sound during calls |
| 34 | in-ear call detection (**0 = on**) |
| 42 | charging bits (as in message 96) |

Example (**observed**), right bud worn, left out, Bass boost, every gesture on:

```
04 05 64 49 01 01 21 16 00 01 FF 22 00 00 4D 01 4D 01 00 00 04 CC 00 00 04 10 00 01 00 …
```

## Acknowledgements

The buds acknowledge simple settings with `ACK [id, value]`, where **value is what the buds actually applied** (**observed**):

- `EQUALIZER [02]` → `ACK [134, 02]`
- `NOISE_CONTROLS [01]` with both buds worn → `ACK [120, 01]`
- `NOISE_CONTROLS [01]` with one bud worn and *noise controls with one earbud* off → `ACK [120, 00]`. The request was **refused**, and the buds stay off.

Silverbud uses the acknowledged value as the truth. It snaps the UI back and explains why when a request is refused.

## Connecting the audio from an app

Windows doesn't offer a public API to connect a paired Bluetooth headset, so Silverbud does what the Settings app does:

1. Enumerate audio endpoints (`IMMDeviceEnumerator`, every state).
2. Keep those whose `PKEY_Device_ContainerId` matches the buds' Bluetooth container, or whose driver filter id contains the Bluetooth address.
3. Through `IDeviceTopology` → `IConnector::GetDeviceIdConnectedTo`, open the driver's KS filter and send `IKsControl::KsProperty`. Use set `KSPROPSETID_BtAudio` (`7FA06C40-B8F6-4C7E-8556-E8C33A12E54D`), id `0` to connect or `1` to disconnect.

With LE Audio, the matching filters are the `apxenum` speaker and microphone filters, and both accept the request (**observed**).
