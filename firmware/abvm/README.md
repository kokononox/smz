# Native ABVM core

This directory is the allocation-free C implementation of the ABP1 loader,
verifier, and scheduler. It has no Pico SDK, TinyUSB, filesystem, or operating
system dependency.

## Current boundary

Implemented:

- ABP1 identity, canonical layout, CRC32, and SHA-256 verification
- resource, route, opcode, constant, control-range, and nesting checks
- fixed Loop and Random Package frames
- two-lane structured Scope
- asynchronous Watch state
- nonblocking action boundary for Key, Type, and Mouse
- Pause/Resume/Stop
- one fixed Whisper interrupt context

Not implemented here:

- USB HID
- ARM UART mouse transport
- ADC/sound sampling
- Guard button service
- cue PWM
- flash A/B deployment
- watchdog/rollback

Those are adapters around this core. They must call `abvm_tick()` regularly,
execute `ABVM_EVENT_ACTION` without blocking, and report completion through
`abvm_complete_action()`. Sound detection is delivered with
`abvm_sound_detected()`; typed light detections use `abvm_light_detected()`. Pause, Stop, and interrupt entry first produce
`ABVM_EVENT_RELEASE_ALL`.

## Host smoke test

```bash
python tools/abvm.py compile \
  firmware/abvm/tests/abvm_smoke.amsj /tmp/program.abp \
  --routes Game Whisper

cc -std=c11 -Wall -Wextra -Werror \
  -Ifirmware/abvm/include \
  firmware/abvm/src/abvm_vm.c \
  firmware/abvm/tests/abvm_vm_smoke.c \
  -o /tmp/abvm-smoke

/tmp/abvm-smoke /tmp/program.abp
```

`AbvmVm` currently occupies 1,272 bytes on the Linux CI ABI. Program bytes
remain caller-owned and can later be read directly from RP2040 XIP flash. The
smoke test also injects 100 deterministic single-bit corruptions and requires
every image to fail closed.