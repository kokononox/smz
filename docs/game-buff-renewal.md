# Pico Game buff renewal — implementation contract

Status: experimental implementation. Scheduler, Native compiler/VM, Pico keyboard
adapter, Status editor, persistence and matching build pipeline are implemented.
Host tests link the actual C scheduler, VM and Pico keyboard actor. Hardware
verification is still required. Existing projects remain opt-in; missing or empty
`gameBuffs` leaves their behavior unchanged.

## Confirmed behavior

- Pico owns timing and keyboard delivery; Windows is configuration/display only.
- Fresh Game entry consumes all enabled buffs before the first cast.
- Every batch is Fisher–Yates shuffled; repeated permutations are possible by
  chance. Before/hold/after ranges preserve the existing intermediate and food
  consumption waits, rather than compressing all buffs into rapid key presses.
- After waits finish, sample a new configurable interval per buff. Defaults to
  the user's selected 55/28/9 minute groups; interval bounds are editable
  (54–56/27–29/8–10 are suggestions, not fixed firmware constants).
- Catch plus response completion OR Timeout provides a safe boundary before the
  next cast. A due buff never interrupts the current fishing attempt.
- Optical priorities win. An interrupted batch retains its unfinished order.
- Pause never shifts buff deadlines. No keys are sent during Pause.
- Login/DC/Restart/Stop followed by a fresh Game run resets the buff session.
  Temporary Targeted/Whisper and Pause/Resume must not call new-game reset.
- Retain the existing cycle controller, random 110–130 minute deadlines, five
  rounds, After/Startup/Finish behavior and restart-critical input protection.
- No extra physical button or "consume now" control.
- One continuous Game fishing section replaces the fifteen sections. Mouse
  fatigue progresses by Game fishing time instead of section index and resets
  at each new cycle. This progression is applied to mixed-speed Game movements.

## Integration and delivery

- Project format 13 adds `gameBuffs` (Keystroke nodes with before/hold/after and
  editable renewal ranges) and `gameMouseFatigueMinutes` (default 135).
- Native descriptor `GBF1`, constant kind 10, opcode 31, uses validated fixed-size
  binary rows. The compiler inserts the initial all-buff checkpoint at fresh
  Game entry. Explicit `buffCheckpoint` nodes must be placed BEFORE casts,
  never in the middle of a Catch wait or its response.
- Pico owns keyboard submission and key-release completion. The VM is held at
  its safe checkpoint while the batch executes; optical interrupt routes can
  still execute. KEY sentinel 253 does not collide with live/internal HID lanes.
- VM fresh-start generation distinguishes new Game runs from interrupt restore.
- Pause releases in-flight keys and retries unfinished key delivery on Resume.
  Completed after-waits keep their true wall-clock timestamp. Optical input
  after key release cancels an unfinished food wait and retries that buff;
  completed entries are not repeated.
- The UF2 patcher rejects buff programs when the selected template lacks the
  matching Pico buff runtime. No silent downgrade to a legacy template.
- Old Desktop/PowerShell/PLAN/CircuitPython execution is not supported for these
  projects; use Export Native UF2. No extra physical button is added.
- Status displays Pico-reported times (every 5 seconds), not a Windows-driven
  scheduler. Last command completion is not confirmation of an in-game effect.
- Timers are volatile; Stop/Restart/fresh Game resets them. No RTC or persistence
  across power loss is implied.
- Existing cycle deadline and four restarts/five rounds are unchanged.
- The user's migrated macro is delivered privately, NOT committed to GitHub.

## Runtime core

`game_buff_runtime.c` is allocation-free and uses monotonic uint32 milliseconds,
rollover-safe deadlines and bounded arrays (16 buffs, 6 virtual keys per combo).
The adapter supplies Game/paused/optical/input/safe gates. KEY_REQUEST is only a
request: acceptance and release completion are separate callbacks. Cooldown
origin is the end of the after-wait even if service runs later during Pause.

CMake/main wiring uses the same tested runtime. The experimental package is gated
on native Pico compilation, portable contracts, ARM compile compatibility and
Windows tests. No automatic hardware flashing or merge to stable is performed.
