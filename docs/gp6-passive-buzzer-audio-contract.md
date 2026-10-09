# GP6 passive buzzer audio contract

Status: design/software contract. Practical hardware buzzer test is deferred by user decision.

## Hardware contract

Only this wiring is valid for the passive buzzer path:

- Raspberry Pi Pico `GP6` / physical pin `9` -> `R4 1 kΩ` -> `Q1 Base`
- Pico `3V3 OUT` / physical pin `36` -> `BZ1 +`
- `BZ1 -` -> `Q1 Collector`
- `Q1 Emitter` -> Pico/common `GND`
- `Q1`: NPN low-side switch such as S8050 or 2N2222
- `BZ1`: passive buzzer, not active buzzer

Forbidden wiring:

- No Pro Micro `D9` buzzer path
- No direct Pico GPIO drive into the buzzer load
- No `GP5` buzzer references except historical notes

## Firmware contract

The Pico owns audio cues. The Pro Micro remains the mouse/sound-sensor arm and must not drive the buzzer.

Commands:

```text
BEEP|freq,ms
BEEP|freq,ms,volume,envelope
```

Rules:

- `freq`: integer Hz, valid range `30..20000`
- `ms`: integer duration in milliseconds, `>= 0`
- `volume`: optional integer percent, `1..100` (default `100`)
- `envelope`: optional `sharp`, `smooth`, `fade-in`, or `fade-out` (default `sharp`)
- Implementation pin: `BUZZER_PIN = board.GP6`
- If a runtime has no buzzer context, `BEEP` must fail safely or be skipped explicitly; it must not crash unrelated plan execution.
- `BEEP` must be implemented on the Pico side only.

## App/exporter contract

The app may expose Pico audio cues only after the firmware side is wired and tested in software.

Required software checks before giving a hardware-test file to the user:

1. Parser accepts both legacy `BEEP|freq,ms` and styled `BEEP|freq,ms,volume,envelope`.
2. Parser rejects frequencies outside `30..20000`.
3. Exporter emits `BEEP|freq,ms` only for Pico-buzzer/audio-cue steps.
4. Exporter never emits Pro Micro/D9 buzzer instructions.
5. Tests assert `GP6`, not `GP5`.
6. Existing `PLAN|2` behavior and AutoCycle/Restart remain unchanged.
7. CI/TestRunner ends with `0 failed`.

## Deferred practical test

Do not ask for the physical buzzer test until a release artifact is ready. The first user-facing test package should include:

- app release zip
- exact SHA-256
- instructions to add the passive buzzer circuit from the hardware contract
- a short beep-only plan or UI cue flow
- expected audible result and safe failure observations
