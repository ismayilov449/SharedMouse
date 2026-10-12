# SharedMouse

Use one laptop's keyboard and mouse on a second laptop, over Bluetooth, with a shared clipboard
and file copy/paste. Both laptops keep their own Wi-Fi/internet. One small `.exe`; nothing to install
(needs .NET Framework 4.8, which ships with Windows 10/11).

## Setup

1. Pair the two laptops once in Windows Bluetooth settings, and keep Bluetooth on.
2. Build: `dotnet build -c Release` → `bin\Release\net48\SharedMouse.exe`
3. Copy `SharedMouse.exe` to both laptops. Both must run the same version.

## Run

| Laptop | Command | Or double-click and choose |
|---|---|---|
| Second laptop (gets controlled) | `SharedMouse.exe second` | `2` |
| Main laptop (keyboard + mouse) | `SharedMouse.exe main --side top` | `1`, then `L`/`R`/`T`/`B` |

`--side` says where the second laptop is, seen from the main one: `left`, `right` (default), `top` or `bottom`.
The main laptop finds the second one among its paired Bluetooth computers by itself.

- **Switching:** push the mouse against that screen edge for a moment (0.15 s) → you control the second laptop.
  Push against the opposite edge on the second laptop → back. A quick flick to the edge, or sliding along it
  (browser tabs, taskbar), does *not* switch.
- **Ctrl+Alt+Space** switches at any time.
- **Clipboard:** copy text or an image on either laptop, paste on the other.
- **Files:** copy files/folders in Explorer (Ctrl+C) on either laptop, paste (Ctrl+V) in a folder on the other.
  They are sent right after copying; the window shows progress. Limit: 100 MB over Bluetooth.
- Close the window to stop. If the connection drops, control falls back to the main laptop.

## Options

- `--delay ms` (main): how long to push against the edge before switching. `0` = instantly.
- `--no-clipboard` / `--no-files` (either laptop): turn off clipboard / file sharing.
- `--peer 8C:F8:C5:11:C9:5B` (main): connect to this Bluetooth address instead of searching.
- `--tcp host:port` (main) / `--tcp [host:]port` (second): use a network cable or LAN instead of Bluetooth
  (faster; file limit 4 GB).
- `SharedMouse.exe devices`: list paired Bluetooth computers.

## Less lag

The window title shows the live connection delay (there and back), e.g. `delay 18 ms (worst 40 ms)`.

- Put both laptops' Wi-Fi on **5 GHz or 6 GHz**. On most laptops Wi-Fi and Bluetooth share one radio, and busy
  2.4 GHz Wi-Fi makes Bluetooth wait.
- Bluetooth headphones playing audio on either laptop compete for the same radio.
- Fastest: a network cable between the laptops with `--tcp` (well under 1 ms).

## Limits

- Windows doesn't let any app type into admin windows (Task Manager, installers) unless the
  app itself runs as admin. Run `second` as administrator if you need that.
- The lock screen, UAC prompts and Ctrl+Alt+Del can't be controlled remotely.
- Keyboard layout: keys are sent by physical position, so the second laptop's own layout decides the character.
- Bluetooth carries roughly 100–200 KB/s, so big files take a while (≈1 minute per 10 MB).
- Received files wait in `%TEMP%\SharedMouse\incoming` until pasted and are removed after a day.
