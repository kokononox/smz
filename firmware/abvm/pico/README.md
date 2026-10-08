# Pico bring-up UF2

This is the RP2040 adapter around the native ABVM core. It embeds one verified `program.abp` in a fixed flash slot, so the board remains portable and does not require a PC connection after flashing.

Firmware identity is validated by pinned [`nekirovoix/pico1`](https://github.com/nekirovoix/pico1). The native adapter consumes its canonical configuration to generate TinyUSB VID/PID, manufacturer, product, and a serial number composed from the configured prefix and RP2040 unique ID. CircuitPython storage settings remain in the audit manifest but are not applied: this UF2 has no FAT or USB mass-storage interface.

USB identity is configuration-driven and has no compiled-in VID/PID allowlist. `batch30.json` contains 30 independently validated experimental identities. `build_batch.sh` builds all variants.

## Fast per-project UF2 export

Native builds contain a validated 128 KiB ABP flash slot. GitHub only needs to build a runtime/identity template when firmware changes. Classroom Studio or a browser can replace the project locally without Pico SDK or an ARM compiler:

```bash
python tools/abvm.py compile project.amsj /tmp/program.abp --routes Game Whisper
python tools/abvm_uf2.py runtime-NB01.uf2 /tmp/program.abp project-NB01.uf2
```

The patcher validates UF2 framing, requires exactly one compatible slot, checks capacity, rewrites program length and SHA-256, clears unused slot bytes, and verifies the result before saving. At boot, ABVM still verifies the ABP CRC, SHA, format, resources, and opcodes.

## Safety boundary

The image exposes TinyUSB CDC and a real HID keyboard actor. `RMOUSE` is routed to the ARM board over UART0 on GP16/GP17 at 57600 baud with checksum framing. Relative motion completes only after `OK|MMOVE`; malformed replies, ARM errors, RX overflow, and ACK timeout fail closed. Release-all emits a zero keyboard report and framed `HALT`. `TYPE` is a real allocation-free, nonblocking TinyUSB actor: it preserves held modifiers, supports printable US-ASCII plus Enter/Tab, applies per-key, word, punctuation and thinking delays, and performs bounded typo/backspace correction. Clipboard, secret, malformed JSON, and non-ASCII payloads fail closed instead of being silently typed.

Human `RMOUSE` paths enforce physical pixel continuity before UART transport:
every Pico-authored `MMOVE` satisfies `dx*dx + dy*dy <= 9`. A farther Bezier
point is drained through ACK-paced Pico substeps before authored progress
advances. ARM 2.8.3 therefore remains one command to one HID report; exact
endpoints, curves, corrections and timing texture are preserved without
restoring the hidden ARM-side 1 ms subdivision.

`WATCH` carries typed descriptors. Sound descriptors contain profile ID, threshold, and sustained-duration requirements; Pico arms ARM 2.8 with framed `ASND`, continues mouse service while the ADC listener runs, consumes `EVT|ASND|DETECTED/TIMEOUT`, and feeds detections directly into `abvm_sound_detected()`. Light descriptors contain the BH1750 lux range, stable duration, and high/low-resolution mode. The native I2C0 actor on GP20/GP21 samples without sleeping, requires an uninterrupted in-range window, and resumes the exact VM lane through `abvm_light_detected()`. Missing sensors fail closed instead of skipping a guard.

The same native BH1750 actor serves read-only `LUX?` telemetry and asynchronous `LCAL|ms` calibration. Calibration accumulates min/max/average in fixed state while USB, HID, ARM UART, buttons, and the VM continue to run. Pause/Stop/route boundaries cancel active watches; ARM sound is additionally cancelled through framed `HALT`.

Classroom direct-run diagnostics retain bounded compatibility commands:
`SETRES|w,h` is acknowledged as metadata because Native mouse movement is
relative, `WSND|threshold,minMs,timeoutMs` uses the same asynchronous ARM ADC
actor as ABVM, and `BEEP|hz,durationMs` uses the nonblocking GP6 PWM actor.
For ABVM Wait For Sound, the step's `threshold` is authoritative; zero opts
into the persisted physical calibration for that profile.

A typed `GUARD` constant embeds the nine calibrated optical profiles exported by Classroom Studio. The allocation-free global Guard applies unique-range classification, per-profile stability, hysteresis, sensor freshness, ordered Desktop → Login → Dashboard → Loading → Game progression, split Targeted New/Repeat Game side-states, and the dedicated DC fallback route. GP4 or `GUARD|ON` starts Guard at the physical state currently visible; `GUARD|OFF`/`HALT` stops it. Missing/ambiguous light never invents a state, sensor timeout stops execution, and every accepted transition starts the matching verified ABVM route.

Implemented: native ABP verification, millisecond scheduler, CDC control, Key/KDown/KUp/Type, bounded relative mouse, nonblocking ARM UART sound watch, nonblocking BH1750 Light Watch/calibration, global nine-profile Guard routing, HALT on every release boundary, GP3 Pause/Resume, GP4 Guard Start/Stop, independent Targeted New/Repeat and Whisper New/Repeat interrupts, nonblocking original GP6 passive-buzzer presets, and fail-closed boot/transport behavior.

Whisper New and Whisper Repeat are bounded, non-preemptible overlays. Guard
ignores all optical scene changes—including a temporary return to Desktop—until
the complete Whisper route finishes and the suspended route is restored. It
then evaluates the latest sensor state normally. The rule applies regardless of
whether the Whisper interrupt originated from light, sound, or manual control.
The sole exception is a stable Login/DC profile: disconnect recovery has
priority, cancels the transient Whisper overlay, and starts the DC route.

Targeted New and Targeted Repeat use the same bounded optical lock. Once
either light signature starts its interrupt route, Game, Desktop, Whisper,
the other Targeted signature, and unknown/ambiguous light cannot preempt it.
All Targeted steps finish before the suspended Game cursor is restored. The
sole optical exception is a stable Login/DC profile, which immediately
replaces Targeted with the disconnect-recovery route.

## Passive buzzer on GP6

The native adapter preserves physical feedback on the existing GP6 → resistor → S8050 circuit. Guard Start/Stop/Pause/Resume use the legacy multi-note patterns. Every confirmed transition after the initial state emits a distinct 150 ms profile-dependent rising sweep. Light calibration uses nine memorable 3–4 note motifs; Classroom Studio exposes a separate assignment menu so every environment can use any motif and preview it on the connected board. The selected cue IDs are stored in the Native Guard descriptor and require no PC helper at runtime. Sound calibration retains ID 1/2/3 selection, silence-start, target-start, save/error, and enter/exit feedback.

Whisper New and Whisper Repeat also carry independent board-only optical cooldowns in the Native Guard descriptor. A brief return to the same light signature during cooldown is ignored even when both global sound listeners are disabled. Classroom Studio accepts up to 3,600,000 ms (60 minutes) per optical Whisper cooldown.

After an automatic restart, a stage watchdog requires the ordered
Login/DC → Character Dashboard → Entering Game Loading → Game sequence. If the
expected stage is not seen within the configured window, Guard and the Cycle
timer are held, all input actors are released, and GP6 emits a repeating
ambulance-style siren. The board never skips a macro automatically. After the
operator fixes the scene, a manual Resume silences the alarm, preserves the
same expected stage, and re-arms the full watchdog window. Diagnostic events
include the expected profile, stage elapsed time, watchdog timeout, and Cycle
count.

When a completed Whisper interrupt restores the fishing Game cursor but the
light sensor sees Targeted instead of Game, Guard grants a special continuous
60-second grace. Route 9 is not started during this window, so the fishing loop
continues from its exact suspended cursor. A stable Game signature cancels the
grace immediately. If Targeted remains stable for the full minute, the same
operator watchdog pauses Guard/VM/Cycle, releases input, and starts the
ambulance alarm. Stable Login/DC retains immediate priority and never waits for
this grace.

Playback is allocation-free and nonblocking; no additional hardware is required.

## Autonomous shift wake (hostless)

A portable board has no clock and no host helper, yet the next shift still has
to start by itself. The temporary Windows bridge already reports the local
wall clock on every shift identity check, so the firmware converts that single
sample into a monotonic deadline instead of asking for a battery-backed RTC:

* `wake_scheduler_sync()` turns "the next window start is at minute X" into
  `deadline = now + (X - minute - lead)`. The nearest of the two window starts
  wins, gaps between windows are ignored, and a sample taken exactly on a start
  targets the other window.
* The deadline is recomputed on every accepted `SHIFT2!` reply, so each round
  re-synchronises the clock and clears the wake attempt counter.
* When the deadline expires while no round is running, the firmware sends one
  authored `MMOVE|1,0,rel,2` on the internal lane. The Arduino core turns that
  report into USB remote wake-up signalling, so the host resumes.
* The Arduino board reports `EVT|HOSTUSB|UP|SUSPEND|DOWN`, so the wake is
  verified rather than assumed: the board waits for `UP`, settles for
  `WAKE_SETTLE_MS`, and only then starts the authored Startup route. If the
  host never reports `UP` the attempt is retried after `WAKE_RETRY_MS`, up to
  `WAKE_MAX_ATTEMPTS`.
* A host that is already awake skips the pulse and starts the round directly.

The deadline lives in RAM: it survives host sleep and reboot, but a Pico power
loss clears it and the board waits for the next clock sample before it can wake
anything again. Persisting it in the calibration store is a separate step.

## Build one identity

```bash
python tools/abvm.py compile autocycle.amsj /tmp/program.abp --routes Game Whisper
export PICO_SDK_PATH=/path/to/pico-sdk
cmake -S firmware/abvm/pico -B /tmp/abvm-pico -DPICO_BOARD=pico -DABVM_PROGRAM=/tmp/program.abp -DABVM_FIRMWARE_CONFIG=/absolute/path/to/pico1-config.json
cmake --build /tmp/abvm-pico --parallel
```

CDC commands: `PING`, `STATUS`, `LUX?`, `LCAL|3000`, `GUARD|ON`, `GUARD|OFF`, `CALSTATUS`, `PAUSE`, `RESUME`, `WHISPER`, `SOUND 2`.
