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

Implemented: native ABP verification, millisecond scheduler, CDC control, Key/KDown/KUp/Type, bounded relative mouse, nonblocking ARM UART, HALT on every release boundary, GP3 Pause/Resume, GP4 Start/Stop, Game/Whisper routing, and fail-closed boot/transport behavior.

## Build one identity

```bash
python tools/abvm.py compile autocycle.amsj /tmp/program.abp --routes Game Whisper
export PICO_SDK_PATH=/path/to/pico-sdk
cmake -S firmware/abvm/pico -B /tmp/abvm-pico -DPICO_BOARD=pico -DABVM_PROGRAM=/tmp/program.abp -DABVM_FIRMWARE_CONFIG=/absolute/path/to/pico1-config.json
cmake --build /tmp/abvm-pico --parallel
```

CDC commands: `PING`, `STATUS`, `START`, `PAUSE`, `RESUME`, `STOP`, `WHISPER`, `SOUND 2`.
