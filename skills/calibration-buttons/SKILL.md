# Calibration Button Behavior Skill

## Purpose

This document is the authoritative contract for the physical calibration controls of the combined Pico Guard runtime. It preserves the behavior agreed during the SMC sessions and must be used when rebuilding the Pico bundle or changing the button state machine.

## Hardware mapping

- **Blue / GP4**: Start/Stop outside calibration; enter, advance, or exit calibration inside calibration.
- **Yellow / GP3**: Take and validate a light sample for the current calibration profile.
- **GP6**: Passive piezo/buzzer for feedback notes.

## GP4 behavior outside calibration

1. A short press and release means **Start** when stopped, or **Stop** when running.
2. A press held for about **3 seconds and then released** means **enter calibration**; it must not also trigger Start and must not play the normal Start note.
3. The button must be edge-driven and debounced. A press is classified once, on release, so one physical action cannot produce both a short and long action.
4. Start/Stop handling must remain responsive and non-blocking. While waiting for a long-press decision, the runtime must continue USB, UART, and safety polling.

## Calibration state machine

```text
NORMAL + GP4 short  -> START or STOP
NORMAL + GP4 long   -> CALIBRATION(profile 1)

CALIBRATION + GP3 short -> SAMPLE current profile
CALIBRATION + GP4 short -> NEXT profile (only after current profile is valid)
CALIBRATION + GP4 long  -> EXIT calibration (partial exit allowed)
```

A partial exit is allowed intentionally: an operator may need to adjust only one screen/profile. The runtime must preserve previously valid profiles and must not require all profiles to be completed before a long GP4 exit.

## Yellow / GP3 sample action

On a short GP3 press while calibrating:

1. Read a bounded set of light samples after the configured settle interval.
2. Calculate the candidate centre/tolerance/hysteresis values for the current profile.
3. Validate the candidate against every other enabled profile.
4. If the candidate overlaps another profile:
   - play the refusal/error note;
   - do **not** save or replace the current profile;
   - keep the operator on the same profile;
   - remain responsive to another GP3 sample or GP4 action.
5. If validation succeeds:
   - commit the candidate atomically in RAM/NVM;
   - play the success/save note;
   - mark the current profile valid;
   - remain in calibration and keep polling buttons.

A second GP3 press on the same profile must start a fresh sample and must not deadlock, reset USB, or require opening Thonny. Audio playback must be bounded and non-blocking with respect to button polling.

## Blue / GP4 actions inside calibration

- **Short press/release**: advance to the next profile after the current profile has a valid sample. Play the normal next-profile note.
- **Long press/release (about 3 seconds)**: exit calibration. Play the calibration-exit note. Partial completion is valid.
- A long press must not be interpreted as a short press after the release.
- If the current profile is invalid and the operator attempts a short advance, refuse the advance with a short error note and remain on that profile.

## Persistence rules

- Never write an overlapping or otherwise invalid candidate.
- Preserve the last known valid calibration when sampling fails.
- Use an atomic write/replace strategy where the filesystem supports it.
- After saving, update the in-memory profile and the displayed/returned calibration status consistently.
- Calibration feedback must not block the main loop long enough to miss GP3 or GP4 edges.

## Audio contract

The exact frequencies may remain configurable, but the semantic notes are mandatory:

- calibration entry
- sample accepted / saved
- overlap or sample refused
- next profile
- calibration exit
- normal start
- normal stop

The normal start note must not sound when GP4 is held for calibration entry. The refusal note must be distinct from the success/save note.

## Safety and regression requirements

- GP4 Stop must release keyboard keys, mouse buttons, and UART/ARM work immediately and safely.
- GP3 must not sample outside calibration unless an explicit diagnostic mode is active.
- Exceptions during sampling, persistence, or audio must return to the calibration loop rather than leaving the Pico apparently hung.
- Tests must cover: short GP4, long GP4, GP3 success, repeated GP3, overlap refusal/no-save, GP4 next, long GP4 partial exit, Stop during sampling, and recovery after an exception.
- Do not add `adafruit_hid` to the combined Pico runtime; the established memory-safe internal HID implementation remains the baseline.

## Acceptance checklist

- [ ] Short GP4 starts/stops exactly once.
- [ ] Long GP4 enters calibration without a Start note.
- [ ] GP3 samples and gives a success note.
- [ ] Repeated GP3 resamples without lockup.
- [ ] Overlap gives refusal note and does not save.
- [ ] Short GP4 advances only from a valid profile.
- [ ] Long GP4 exits, including partial calibration.
- [ ] After every note, GP3/GP4 still respond.
- [ ] No `MemoryError`, USB reconnect, Thonny intervention, or stale button state occurs.
