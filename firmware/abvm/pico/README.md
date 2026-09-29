# Pico bring-up UF2

This is the first RP2040 adapter around the native ABVM core. It embeds one
verified `program.abp` directly in the UF2, so the board remains portable and
does not require a PC connection after flashing.

## Safety boundary

This bring-up image **does not send keyboard or mouse input**. Key, Type, and
Mouse opcodes are reported as `ACTION|stub` over USB CDC and acknowledged
nonblockingly. `HID|release-all|stub` marks the exact boundary where the
TinyUSB HID adapter will be connected next.

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
  -DABVM_PROGRAM=/tmp/program.abp
cmake --build /tmp/abvm-pico --parallel
```

Output:

```text
/tmp/abvm-pico/ams_abvm_pico.uf2
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