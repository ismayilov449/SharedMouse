# SharedMouse

Use one laptop's keyboard and mouse on a second laptop, over Bluetooth, with a shared clipboard
and file copy/paste. Both laptops keep their own Wi-Fi/internet. One small `.exe`; nothing to install
(needs .NET Framework 4.8, which ships with Windows 10/11).

## Setup

1. Pair the two laptops once in Windows Bluetooth settings, and keep Bluetooth on.
2. Build: `dotnet build -c Release` → `bin\Release\net48\SharedMouse.exe`
3. Copy `SharedMouse.exe` to both laptops, into a folder where it can stay (e.g. `Documents\SharedMouse`):
   "Start with Windows" runs it from there. Both laptops must run the same version.
4. Double-click it on each laptop. The first time, a setup window asks:
   - whether this is the **main** laptop (whose keyboard and mouse are shared) or the **second** one;
   - on the main laptop: where the second laptop stands (above, below, left or right);
   - clipboard / file sharing, and whether to start with Windows (on by default).

After that SharedMouse runs as an icon next to the clock and the laptops find each other by themselves.
With "Start with Windows" on, you never start it by hand again.

## The icon next to the clock

| Colour | Meaning |
|---|---|
| Grey | Looking for the other laptop |
| Green | Connected |
| Blue | The keyboard and mouse are on the second laptop right now |

- **Hover** over it: who it's connected to and the delay, e.g. `Connected to MSI · 18 ms`.
- **Right-click**: *Switch to the other laptop*, *Settings...*, *Show log*, *Start with Windows*, *Exit*.
- Windows 11 hides new icons: click **^** next to the clock and drag the mouse icon onto the taskbar to keep it in view.
- Starting `SharedMouse.exe` again while it's running opens its settings.
- It pops up a short notice when it connects, when the connection stays lost, and when files arrive.

## Using it

- **Switching:** push the mouse against that screen edge for a moment (0.15 s) → you control the second laptop.
  Push against the opposite edge on the second laptop → back. A quick flick to the edge, or sliding along it
  (browser tabs, taskbar), does *not* switch.
- **Ctrl+Alt+Space** switches at any time.
- **Clipboard:** copy text or an image on either laptop, paste on the other.
- **Files:** copy files/folders in Explorer (Ctrl+C) on either laptop, paste (Ctrl+V) in a folder on the other.
  They are sent right after copying. Limit: 100 MB over Bluetooth.
- If the connection drops, control falls back to the main laptop, and it reconnects by itself.

## Advanced

- Settings are saved in `%APPDATA%\SharedMouse\settings.ini`. Besides what the settings window shows, it has:
  - `delay=150` (main): how long to push against the edge before switching, in ms. `0` = instantly.
  - `peer=00:11:22:33:44:55` (main): connect to this Bluetooth address instead of searching.
  - `tcp=host:port` (main) / `tcp=[host:]port` (second): use a network cable or LAN instead of Bluetooth
    (faster; file limit 4 GB).
- The log: right-click the icon > *Show log*, or `%LOCALAPPDATA%\SharedMouse\SharedMouse.log`.
- Command line, instead of the saved settings (e.g. for testing):
  `SharedMouse.exe main --side top [--delay 150] [--peer ...] [--tcp ...] [--no-clipboard] [--no-files]`,
  `SharedMouse.exe second [...]`, and `SharedMouse.exe devices` to list paired Bluetooth computers.

## Less lag

Hover over the icon to see the live connection delay (there and back).

- Put both laptops' Wi-Fi on **5 GHz or 6 GHz**. On most laptops Wi-Fi and Bluetooth share one radio, and busy
  2.4 GHz Wi-Fi makes Bluetooth wait.
- Bluetooth headphones playing audio on either laptop compete for the same radio.
- Fastest: a network cable between the laptops with `tcp=` (well under 1 ms).

## Limits

- Windows doesn't let any app type into admin windows (Task Manager, installers) unless the
  app itself runs as admin. Run SharedMouse as administrator on the second laptop if you need that.
- The lock screen, UAC prompts and Ctrl+Alt+Del can't be controlled remotely.
- Keyboard layout: keys are sent by physical position, so the second laptop's own layout decides the character.
- Bluetooth carries roughly 100–200 KB/s, so big files take a while (≈1 minute per 10 MB).
- Received files wait in `%TEMP%\SharedMouse\incoming` until pasted and are removed after a day.
