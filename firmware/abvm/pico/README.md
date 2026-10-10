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
to start by itself. Two things therefore have to reach the board without a PC
running: the authored windows, and the current time.

* The authored schedule travels inside the flashed program as the same `SFT2`
  shift descriptor the round uses, so the board reads it at boot with
  `abvm_find_constant(ABVM_CONST_SHIFT)` and configures the scheduler before USB
  is even attached. Waiting for a round to reach its shift check would close a
  circle: that check belongs to the round the board was supposed to start, so a
  freshly flashed board could never arm the first window it had to wake. Boot
  reports the result as
  `EVT|WAKE|schedule|source=program|enabled=|day=|night=|lead=`, or `source=none`
  when the flashed project carries no schedule at all.
* The clock has two independent sources and needs neither at any given moment.
  The temporary Windows bridge reports the local wall clock on every shift
  identity check, and `TIME!|HH:MM` stamps it once on demand from any host tool —
  no bridge, no round, no software left running. The stamp retires the recovery
  budget exactly like an accepted bridge sample, so it is the one click that
  breaks the cold-start circle after a flash or a brownout.
* The firmware converts whichever sample it gets into a monotonic deadline
  instead of asking for a battery-backed RTC:

* `wake_scheduler_sync()` turns "the next window start is at minute X" into
  `deadline = now + (X - minute - lead)`. The nearest of the two window starts
  wins, gaps between windows are ignored, and a sample taken exactly on a start
  targets the other window.
* The deadline is recomputed on every accepted `SHIFT2!` reply, so each round
  re-synchronises the clock and clears the wake attempt counter.
* The same sample anchors the wall clock, so `wake_scheduler_wall_minute()` turns
  any later instant back into a minute of day. Every pulse log therefore carries
  both the wall time and the authored target (`wall=07:58|target=08:00`), which
  is what makes a hardware bring-up readable instead of guesswork.
* Two independent wake sources sit on the host bus, and one pulse fires both. The
  Pico is itself a HID keyboard whose descriptor advertises remote wake-up, so
  `tud_remote_wakeup()` resumes the bus directly. The Arduino board raises
  `RMWKUP` from its own suspended mouse interface, using one authored
  `MMOVE|1,0,rel,2` on the internal lane. `WAKE_USE_PICO_WAKEUP` and
  `WAKE_USE_ARM_PULSE` isolate a single path during bring-up. The Pico path is
  gated on `tud_suspend_cb(remote_wakeup_en)`, because driving resume on a bus the
  host never armed would be a protocol violation; `STATUS` reports that verdict
  as `pico-rw`.
* The Arduino board reports `EVT|HOSTUSB|UP|SUSPEND|DOWN`, so the wake is
  verified rather than assumed: the board waits for `UP`, settles for
  `WAKE_SETTLE_MS`, and only then starts the authored Startup route. If the
  host never reports `UP` the attempt is retried after `WAKE_RETRY_MS`, up to
  `WAKE_MAX_ATTEMPTS`.
* A host that is already awake skips the pulse and starts the round directly.
  That verdict comes from the bridge, though, and the bridge re-announces it
  every two seconds, so a single `UP` report can be that old. The shortcut is
  therefore taken only after the host has been reported awake for
  `WAKE_HOST_AWAKE_GRACE_MS`, so a machine that suspends right at the deadline
  still gets its pulse instead of silently losing the only wake of the window.
* Every deadline logs one
  `EVT|WAKE|due|manual=|dry=|attempts=|usb=|pico-usb=|pico-rw=` line naming the
  inputs the decision used, and `WAKE?` reports the whole deadline state on
  demand (`armed`, `manual`, `dry`, `synced`, `schedule`, `target`, `due-ms`,
  `attempts`, `phase`, `recovery`, `host`, `pico-usb`, `pico-rw`), so a wake that
  never left the board explains itself instead of looking like a dead wake path.
  Every blocker reports itself once per deadline as
  `ERR|WAKE|blocked|reason=round|recovery|calibration|state=|phase=|recovery=`,
  because a deadline that fires nothing is otherwise invisible.
* The wake path only stands down for a round that is actually driving the host
  (`ABVM_STATUS_RUNNING` or `PAUSED`). Gating it on `STOPPED` disabled the whole
  wake path after every flash, because `abvm_init()` leaves the VM in
  `ABVM_STATUS_IDLE`: the board fired no pulse and wrote no line until an
  operator pressed start/stop once.
* A bridge clock sample never cancels an operator `WAKE!` test arm while the
  authored schedule is off: there is nothing to replace it with, and disarming
  there silently cancelled the only wake of the test.
* A verified wake leaves the machine on the Windows lock screen, and the authored
  macro language has no click opcode at all (ABVM knows motion, keys and typing
  only), so nothing in the project can dismiss it. After the settle the firmware
  therefore presses `VK_RETURN` `WAKE_DISMISS_PRESSES` times, paced by
  `WAKE_DISMISS_GAP_MS`: hardware testing on the target showed that Enter alone
  drops the lock screen and signs the machine in, so the pointer is never moved
  and the round starts from the cursor position it expects. Measured on the
  target, the bridge reports `UP` about 5 s after the resume, so `WAKE_SETTLE_MS`
  is 15 s and the whole pulse-to-round sequence takes about 24 s. The step runs only
  for a host this board actually woke; `WAKE_DISMISS_ENABLED 0` removes it for a
  machine that does not lock on wake.
* The two wake sources are independent and neither is gated on the other. The
  Pico resumes the host bus from its own suspended port and needs nothing from
  the Arduino board, so an unprobed, busy or faulted board can no longer stop the
  machine from being woken — which is what used to happen, silently, because the
  pulse path returned before it reached the log. Every skip is now reported:
  `ERR|WAKE|pulse|pico-skipped|usb=|rw=` when the host never armed this board's
  own remote wake-up, `ERR|WAKE|pulse|arm-skipped|ready=|busy=` when only the
  Pico path went out, and `ERR|WAKE|pulse|wait|...` once while nothing can go out
  yet.
* The recovery path cannot park the wake machine: it is blocked on an
  unreachable board or an unreadable store only until its deadline, and then
  reports `ERR|WAKE|recovery|skipped|reason=arm|store` and lets the normal path
  run again.
* Logs survive the host being asleep. While the CDC is disconnected every line
  used to be dropped, which is exactly the window the wake decision happens in;
  the last `LOG_REPLAY_BYTES` of output are now kept in RAM and replayed, marked
  `RPL|begin` … `RPL|end`, as soon as the host is back. `CFG_TUD_CDC_TX_BUFSIZE`
  was raised so a full `STATUS` line (over 360 characters) is written in one
  piece instead of being cut at the endpoint buffer.
* The dismiss phase is bounded, because a key report is only delivered while the
  host keeps this board's own port resumed and the host can resume through the
  Arduino board instead. `WAKE_DISMISS_TIMEOUT_MS` caps the phase; on expiry the
  firmware logs `ERR|WAKE|dismiss|stall` and starts the round anyway, so a
  keyboard that cannot be delivered can never park the wake machine and cost a
  shift. While this board's own bus is still suspended the same key is also sent
  to the Arduino board (`WAKE_DISMISS_ARM_FALLBACK`,
  `WAKE_DISMISS_ARM_COMMAND`), which is the board that certainly resumed, and
  `tud_remote_wakeup()` is re-issued to ask for the port back. `STATUS` reports
  the current phase as `wake-phase`.
* `WAKE!<seconds>`, `WAKE!<seconds>!dry` and `WAKE!OFF` arm, arm-without-starting
  or clear a one-shot deadline on demand, so the path can be exercised without
  waiting for a real window. The `!dry` form wakes the host and then stops, which
  is the safe first hardware test: no authored round starts while nobody is
  watching the desktop. The next accepted clock sample replaces either form with
  the authored schedule. `TIME!|HH:MM` stamps the wall clock once and re-arms from
  the schedule the board read at boot, which is what lets a freshly flashed board
  wake its first window with no host software involved.
* A consumed test arm hands the deadline straight back to the authored windows.
  `wake_scheduler_rearm()` runs where the deadline is consumed, so one `WAKE!` run
  cannot leave the board unable to wake for the real window until some round
  happens to run another shift check — a failure that writes no line at all,
  because nothing is armed and nothing fires. A window already inside the lead
  time is deliberately left unarmed, since `wake_scheduler_sync()` falls back to
  "one minute from now" there and re-arming would become a wake loop. `WAKE!OFF`,
  an exhausted attempt budget and a failed pulse all stay disarmed on purpose.

### After a board reset

The monotonic deadline itself cannot be persisted: a reset restarts the Pico
clock and the board owns no battery-backed RTC. What is persisted in the
calibration record (version 6) is the *decision* a reset cannot undo — the host
was asleep, a wake was still owed, and how many recovery pulses were already
spent:

* On boot `wake_scheduler_recovery_needed()` decides whether the clock is really
  lost. A host that is already awake cancels the recovery.
* Otherwise the board sends one pulse that is not meant to start a round: it
  brings the host up so the bridge can hand the wall clock back. The authored
  deadline is then re-armed and the machine is free to sleep again until the real
  window. After a brownout the machine may therefore wake once, early.
* The attempt counter is persisted *before* the pulse and bounded by
  `WAKE_RECOVERY_MAX_ATTEMPTS`, so a brownout loop can never become a wake storm.
  The next accepted clock sample clears it.
* A recovery boot cannot wait for USB enumeration — a port the host suspended
  never mounts — so the mount wait is bounded by `WAKE_MOUNT_TIMEOUT_MS` and the
  recovery then runs from the normal service loop over the private UART link.
* The record is written only when the recovery decision changes, and never more
  often than `WAKE_STORE_MIN_INTERVAL_MS`, because every write erases a 4 KiB
  sector.

### When the machine is fully off

Remote wake-up only resumes a bus the host suspended, so a PC in soft-off cannot
be reached over USB by anything: neither the Pico's own resume nor the Arduino
board's `RMWKUP` has a bus to drive. The one line that still reaches it is its own
power button, which the ATX standby rail keeps alive whenever the PSU has mains.

* An optocoupler across the front-panel `PWR_BTN` header turns that button into a
  floating contact this board can close. A PC817/EL817 is enough: the button
  circuit is a pull-up with microamps through it, far inside the part's 50 mA /
  35 V output rating, and because it is an optocoupler the PC's ground never meets
  this board's.
* `POWER_BUTTON_PIN` defaults to GP7, which is free: the firmware claims GP3 and
  GP4 for the buttons, GP6 for the buzzer, GP16/GP17 for the ARM UART and
  GP20/GP21 for the light sensor's I2C0.
* Wiring: `GPIOn → 330–470 Ω → PC817 pin 1 (anode)`, `PC817 pin 2 (cathode) →
  board GND`, `PC817 pin 4 (collector) → PWR_BTN +`, `PC817 pin 3 (emitter) →
  PWR_BTN −`. If the header polarity is unknown, swap pins 3 and 4, or use two
  PC817s back to back so the contact is polarity-free.
* The press is momentary and bounded on both sides (`POWER_BUTTON_MIN_MS` 100 …
  `POWER_BUTTON_MAX_MS` 1500, default `POWER_BUTTON_MS` 300) and is always
  released from the main loop: holding the button for four seconds is a forced
  power-off.
* The board presses it only when nothing is on its own USB at all
  (`!tud_mounted()`). A host that is merely suspended keeps the port mounted and
  is woken over the bus instead, so a machine that is already running only sees a
  press if its own USB cable is out. Set Windows to ignore the power button
  ("Do nothing") before wiring this line: a stray press is then a no-op, while a
  real one still boots a machine that is off.
* A host that is still in POST looks exactly like a host that is off: until its own
  USB stack comes up, nothing of ours is on its bus either. A board that powered up
  together with the machine — the power cut that restarted both — would therefore
  read "off" a few seconds in and press a button into a running POST, and a machine
  answers that by shutting down again, undoing the very BIOS setting that brought it
  back. The press is held back for `POWER_BUTTON_GRACE_DEFAULT_MS` (60 s) instead,
  and the grace is the machine's own cold start rather than a vendor's: the longest
  time this board has watched it take to put USB up, plus
  `POWER_BUTTON_GRACE_MARGIN_MS`, clamped to `POWER_BUTTON_GRACE_MIN_MS` …
  `POWER_BUTTON_GRACE_MAX_MS` (30–150 s) and persisted in the wake record, so it
  survives the same power cut that needs it. A stored value that could not have come
  from a POST counts as nothing learned, so a migrated record can only lengthen the
  wait. `HOST_BOOT_LEARN_MIN_S` is deliberately far above this board's own
  enumeration time, because a board that restarted under a running host would
  otherwise read its own few seconds as the machine's cold start — the one sample
  that would shorten the wait into a press on a POST. `WAKE?` reports `host-boot-s=` and `pwr-grace=`, a new sample logs
  `EVT|PWRBTN|host-boot|learned-s=`, and a wait names itself once as
  `EVT|WAKE|recovery|skipped|reason=boot-grace|grace=`. Nothing here is
  vendor-specific, and the wait spends no recovery attempt: only a press does. A bus
  the Arduino board reports suspended is a host that is present and asleep, so the
  press that wakes it is never delayed.
* The press spends the same persisted recovery budget as the bus pulses
  (`WAKE_RECOVERY_MAX_ATTEMPTS`), so a brownout loop cannot become a storm of
  button presses, and it names itself as
  `EVT|WAKE|recovery=power-button|attempt=|ms=|pressed=|reason=` followed by
  `EVT|PWRBTN|press` and `EVT|PWRBTN|release`. `WAKE?` reports `pwr-presses=`.
* The line can be exercised without cutting any power: `PWRBTN` presses for the
  default hold and `PWRBTN|<ms>` for an explicit one, both inside the same bounds.
  Run it with the target machine off, or with Windows set to ignore its power
  button, and read the two `EVT|PWRBTN` lines back.
* If this board is powered from the PC's own USB port, the feature needs that port
  to keep its 5 V in soft-off: leave ErP/EuP disabled and USB standby power
  enabled in the BIOS. A board that is dead while the PC is off cannot press
  anything, so a separate 5 V supply is the alternative.

## Build one identity

```bash
python tools/abvm.py compile autocycle.amsj /tmp/program.abp --routes Game Whisper
export PICO_SDK_PATH=/path/to/pico-sdk
cmake -S firmware/abvm/pico -B /tmp/abvm-pico -DPICO_BOARD=pico -DABVM_PROGRAM=/tmp/program.abp -DABVM_FIRMWARE_CONFIG=/absolute/path/to/pico1-config.json
cmake --build /tmp/abvm-pico --parallel
```

CDC commands: `PING`, `STATUS`, `LUX?`, `LCAL|3000`, `GUARD|ON`, `GUARD|OFF`, `CALSTATUS`, `PAUSE`, `RESUME`, `WHISPER`, `SOUND 2`.
