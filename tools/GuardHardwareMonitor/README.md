# GuardHardwareMonitor 2.0

Safe, read-only-by-default Windows diagnostics for the Portable Pico Guard.

## Safety contract

- Automatically sends only `PING`.
- Never sends Guard, HID, calibration, mouse, keyboard, stop, or start commands.
- Scans COM ports one at a time and accepts only a `role=brain` PONG.

## Improvements over the original monitor

- Automatic reconnect if the COM number changes.
- Timestamped and color-coded STATE, ROUTE, memory, calibration, sound, button, ARM, warning, and failure events.
- Raw log, structured JSONL, and end-of-session summary.
- Warns when GP4 Start is accepted but no stable state/route follows.
- Loads `guard-calibration.json` automatically from CIRCUITPY, or from a supplied Bundle ZIP, and reports the nearest profile/range for observed lux.
- Tracks route completion, duration, state counts, denied reasons, heap range, and lux range.
- Interactive safe keys: `S` summary, `P` PING, `R` reconnect, `Q` quit.

## Usage

Run `GuardHardwareMonitor.exe` directly, or:

```powershell
.\GuardHardwareMonitor.exe --bundle .\190.zip
.\GuardHardwareMonitor.exe --port COM31 --bundle E:\
```

All output is written under `diagnostics`.
