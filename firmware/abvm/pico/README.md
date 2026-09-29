# Pico bring-up UF2

This is the RP2040 adapter around the native ABVM core. It embeds one verified
`program.abp` directly in the UF2, so the board remains portable and does not
require a PC connection after flashing.

Firmware identity is validated by the pinned
[`nekirovoix/pico1`](https://github.com/nekirovoix/pico1) generator before the
native Pico SDK build. The Pico SDK adapter consumes pico1's canonical
`build-config.json` to generate TinyUSB VID/PID, manufacturer, product, and a
serial number made from the configured prefix plus the RP2040 unique ID.
CircuitPython-only storage settings are retained in the audit manifest but are
not applied: this native UF2 has no FAT or USB mass-storage interface. CMake
fetches the exact commit recorded in `PICO1_REVISION`; an already-pinned local
checkout may instead be supplied with `-DPICO1_ROOT=/path/to/pico1`.

USB identity is configuration-driven; the native runtime has no compiled-in
VID/PID allowlist. The committed experimental batch contains 30 independently
validated identities imported from `pico1-firmware-batch30-verified.zip`.
Build all variants with:

```bash
firmware/abvm/pico/build_batch.sh /tmp/program.abp \
  firmware/abvm/pico/batch30.json /tmp/abvm-batch30
```

Each output directory contains its personalized UF2, canonical configuration,
pico1 manifest, native ABVM manifest, and SHA-256 list.

## Safety boundary

The image exposes TinyUSB CDC plus a real HID keyboard actor. It also routes
`RMOUSE` to the ARM board over UART0 on GP16/GP17 at 57600 baud using checksum
frames. Relative motion completes only after `OK|MMOVE`; malformed replies,
ARM errors, RX overflow, and ACK timeout fail closed. Release-all emits both a
zero keyboard report and framed `HALT`. Type remains a safe stub until its
dedicated actor is connected.

Implemented on board:

- native ABP verification at boot;
- millisecond scheduler clock;
- USB CDC diagnostics and control;
- TinyUSB keyboard Key/KDown/KUp actor;
- bounded relative mouse endpoint generation with no absolute cursor ledger;
- nonblocking ARM UART TX/RX and `OK|MMOVE` action completion;
- framed `HALT` on Pause, Stop, interrupt, route completion, fault, USB unmount,
  and USB suspend;
- GP3 debounced Pause/Resume;
- GP4 debounced Start/Stop;
- Game start, Pause/Resume/Stop, Watch timeout/detection, and Whisper
  interrupt/resume;
- fail-closed boot and transport behavior.

## Build one identity

```bash
python tools/abvm.py compile autocycle.amsj /tmp/program.abp \
  --routes Game Whisper

export PICO_SDK_PATH=/path/to/pico-sdk
cmake -S firmware/abvm/pico -B /tmp/abvm-pico \
  -DPICO_BOARD=pico \
  -DABVM_PROGRAM=/tmp/program.abp \
  -DABVM_FIRMWARE_CONFIG=/absolute/path/to/pico1-config.json
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
