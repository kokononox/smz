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

`WATCH` carries typed descriptors. Sound descriptors contain profile ID, threshold, and sustained-duration requirements; Pico arms ARM 2.8 with framed `ASND`, continues mouse service while the ADC listener runs, consumes `EVT|ASND|DETECTED/TIMEOUT`, and feeds detections directly into `abvm_sound_detected()`. Light descriptors contain the BH1750 lux range, stable duration, and high/low-resolution mode. The native I2C0 actor on GP20/GP21 samples without sleeping, requires an uninterrupted in-range window, and resumes the exact VM lane through `abvm_light_detected()`. Missing sensors fail closed instead of skipping a guard.

The same native BH1750 actor serves read-only `LUX?` telemetry and asynchronous `LCAL|ms` calibration. Calibration accumulates min/max/average in fixed state while USB, HID, ARM UART, buttons, and the VM continue to run. Pause/Stop/route boundaries cancel active watches; ARM sound is additionally cancelled through framed `HALT`.

Classroom direct-run diagnostics retain bounded compatibility commands:
`SETRES|w,h` is acknowledged as metadata because Native mouse movement is
relative, `WSND|threshold,minMs,timeoutMs` uses the same asynchronous ARM ADC
actor as ABVM, and `BEEP|hz,durationMs` uses the nonblocking GP6 PWM actor.
For ABVM Wait For Sound, the step's `threshold` is authoritative; zero opts
into the persisted physical calibration for that profile.

A typed `GUARD` constant embeds the six calibrated optical profiles exported by Classroom Studio. The allocation-free global Guard applies unique-range classification, per-profile stability, hysteresis, sensor freshness, ordered Desktop → Login → Dashboard → Loading → Game progression, Targeted as a Game side-state, and the dedicated DC fallback route. GP4 or `GUARD|ON` starts Guard at the physical state currently visible; `GUARD|OFF`/`HALT` stops it. Missing/ambiguous light never invents a state, sensor timeout stops execution, and every accepted transition starts the matching verified ABVM route.

Implemented: native ABP verification, millisecond scheduler, CDC control, Key/KDown/KUp/Type, bounded relative mouse, nonblocking ARM UART sound watch, nonblocking BH1750 Light Watch/calibration, global seven-profile Guard routing, HALT on every release boundary, GP3 Pause/Resume, GP4 Guard Start/Stop, Whisper interrupts, nonblocking original GP6 passive-buzzer presets, and fail-closed boot/transport behavior.

## Passive buzzer on GP6

The native adapter preserves the original physical-feedback score on the existing GP6 → resistor → S8050 circuit. Guard Start/Stop/Pause/Resume use the exact legacy multi-note patterns. Every confirmed transition after the initial state emits a distinct 150 ms profile-dependent rising sweep, updated every 4 ms, so transition feedback cannot be confused with the restored calibration notes. Light calibration restores the six position notes (262/294/330/349/392/440 Hz), record-start, per-stage completion, rising save-success, low save-error, all-profile completion, and enter/exit melodies. Sound calibration restores ID 1/2 selection at 660/880 Hz, silence-start at 523 Hz, target-start at 988 Hz, plus the same save/error and enter/exit feedback.

Playback is allocation-free and nonblocking; no additional hardware is required.

## Build one identity

```bash
python tools/abvm.py compile autocycle.amsj /tmp/program.abp --routes Game Whisper
export PICO_SDK_PATH=/path/to/pico-sdk
cmake -S firmware/abvm/pico -B /tmp/abvm-pico -DPICO_BOARD=pico -DABVM_PROGRAM=/tmp/program.abp -DABVM_FIRMWARE_CONFIG=/absolute/path/to/pico1-config.json
cmake --build /tmp/abvm-pico --parallel
```

CDC commands: `PING`, `STATUS`, `LUX?`, `LCAL|3000`, `GUARD|ON`, `GUARD|OFF`, `CALSTATUS`, `PAUSE`, `RESUME`, `WHISPER`, `SOUND 2`.
