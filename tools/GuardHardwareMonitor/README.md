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

## ARM diagnostics (2.1)

The monitor interprets existing PONG and ARM error events and reports the evidence and plausible checks. USB state is cached on the Pico, not a live electrical test. A failure does not identify a particular wire or component. PING does not include the fault detail; an ERR event must have been observed to report it. No ARM probing, Guard, HID, mouse, keyboard, calibration, or firmware changes are performed. Existing logs are unchanged; ARM-DIAG is also saved to JSONL and the session summary. On reconnect diagnosis is reset, since cached state may be stale.

Use `Start-Pico-Port.cmd` and enter the Pico COM port to avoid scanning other devices. The legacy `Start-Monitor.cmd` scans all serial ports and opens them with DTR: some devices can reset on open and each candidate receives PING. Do not run either while another application needs the serial port. No physical wiring should be changed while powered.

Analyze a previous monitor or Classroom text log without opening any serial port: `GuardHardwareMonitor.exe --analyze-log "C:\path\to\log.txt"`. It recognizes Pico PONG and ARM error records; unsupported lines are ignored.
