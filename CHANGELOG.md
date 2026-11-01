# Changelog

## 1.0.2 (2026-11-01)

- **Connects by itself when the buds are already connected.** Silverbud no longer gives up after three tries. While your buds are connected to the PC it keeps retrying in the background (4 s, 8 s, 15 s, 30 s, then every minute), so you don't need to press Connect.
- **Reliable Connect for LE Audio buds.** Connect now brings the buds' audio back first: a classic audio request plus a Bluetooth LE wake-up. It waits until Windows has working audio for the buds, then attaches the settings link. Before, the buds dropped the settings link after about 10 seconds when their audio wasn't connected.
- **Turns Bluetooth on.** If Bluetooth is off when Silverbud starts, or when you select Connect, Silverbud turns it on and connects your buds. You can switch this off with **Turn on Bluetooth automatically** in the Device tab.
- **Clearer messages** when the buds refuse the settings connection. This usually means the Galaxy Wearable app on your phone holds it, or the buds need 10 seconds in the case.
- New `--quit` switch closes a running Silverbud cleanly, so updates don't need to force-close it.

## 1.0.1 (2026-11-01)

- When the buds refuse noise canceling or ambient sound because only one bud is in your ear, the Noise control card now says so, naming the bud that's in. Its **Allow with one bud** button turns on *noise controls with one earbud* and tries again. Before, the reason only appeared as small text under the device name.

## 1.0.0 (2026-11-01)

First release.

- Pair, connect, disconnect and forget Galaxy Buds3 from Windows, with LE Audio and classic Bluetooth.
- Connects automatically when the buds come out of the case.
- Live battery for left, right and case, with in-ear, case and charging state, plus a low-battery notification.
- Noise control (off, noise canceling, ambient), ambient level and custom ambient.
- Equalizer presets and an experimental 9-band custom EQ.
- Left/right balance and game mode.
- Pinch gesture switches, pinch-and-hold action per ear, double-tap edge volume.
- Find my buds with left/right mute.
- Call settings, seamless connection, pairing mode and factory reset.
- Firmware and hardware information.
- Notification-area icon with battery tooltip and quick controls. Start with Windows, and keep running when closed.
