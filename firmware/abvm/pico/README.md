# Pico bring-up UF2

This is the first RP2040 adapter around the native ABVM core. It embeds one
verified `program.abp` directly in the UF2, so the board remains portable and
does not require a PC connection after flashing.

Firmware identity is validated by the pinned
[`nekirovoix/pico1`](https://github.com/nekirovoix/pico1) generator before the
native Pico SDK build. The Pico SDK adapter consumes pico1's canonical
`build-config.json` to generate TinyUSB VID/PID, manufacturer, product, and a
serial number made from the configured prefix plus the RP2040 unique ID.
CircuitPython-only storage settings are retained in the audit manifest but are
not applied: this native UF2 has no FAT or USB mass-storage interface. CMake
fetches the exact commit recorded in `PICO1_REVISION`; an already-pinned local
checkout may instead be supplied with `-DPICO1_ROOT=/path/to/pico1`.

## Safety boundary

This image exposes TinyUSB CDC plus a real HID keyboard actor. Key/KDown/KUp
are submitted nonblockingly and release-all emits a zero keyboard report.
Type and Mouse remain safe stubs until their dedicated actors are connected.

Implemented on board:

- native ABP verification at boot;
- millisecond scheduler clock;
- USB CDC diagnostics and control;
- GP3 debounced Pause/Resume;
- GP4 debounced Start/Stop;
- Game start, Pause/Resume/Stop, Watch timeout/detection, and Whisper
  interrupt/resume;
- fail-closed boot when the embedded image is corrupt.

## Build

```bash
python tools/abvm.py compile autocycle.amsj /tmp/program.abp \
  --routes Game Whisper

export PICO_SDK_PATH=/path/to/pico-sdk
cmake -S firmware/abvm/pico -B /tmp/abvm-pico \
  -DPICO_BOARD=pico \
  -DABVM_PROGRAM=/tmp/program.abp \
  -DABVM_FIRMWARE_CONFIG="$PWD/firmware/abvm/pico/pico1.json"
cmake --build /tmp/abvm-pico --parallel
```

Output:

```text
/tmp/abvm-pico/ams_abvm_pico.uf2
/tmp/abvm-pico/pico1/build-config.json
/tmp/abvm-pico/pico1/build-manifest.json
/tmp/abvm-pico/pico1/abvm-firmware-manifest.json
```

## CDC commands

```text
PING
STATUS
START
PAUSE
RESUME
STOP
WHISPER
SOUND 2
```

`SOUND 2` is a temporary diagnostic injection for the Catch profile until the
ADC/sound adapter is connected.
