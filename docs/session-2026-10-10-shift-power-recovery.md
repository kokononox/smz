# Session handoff — shift boundary and power-on recovery on the Pico

Date: 2026-10-10  
Repository: `kokononox/smz`  
Branch: `feat/pico-boot-grace-power-button` (PR #34), tip `d0232ab`  
Base: `main` at `7651e5a1`

## What this branch changes

Firmware, tests and documentation only. No `ams-shell` file is touched, so none
of this needs a new Classroom Studio build — a new UF2 is the whole delivery.
Fifteen files: `firmware/abvm/pico/main.c`, `shift_identity_runtime.{c,h}`,
`calibration_store.{c,h}`, `firmware/abvm/pico/README.md`,
`firmware/abvm/tests/{test_abvm_shift_wake.py,test_shift_identity.py,test_shift_schedule.py,shift_identity_smoke.c,shift_schedule_store_smoke.c}`,
`docs/machine-power-recovery-fa.txt`, `docs/shift-identity-guide-fa.txt`,
`tools/wake-test/README-FA.txt`, `CHANGELOG-CURRENT.md`.

Nine commits, in order:

1. `5a271b45` Wait out the machine's own POST before pressing its power button
2. `6b012951` Note the USB-wake option shape in the machine-independent recovery doc
3. `5d1ccddd` Never learn a cold start the board did not watch
4. `36da3f40` Let a replay say what the helper board saw and when
5. `5300f3a5` Give a machine that is off one signal that does not need it
6. `fb7097f5` Never park the board in the boot wait when the machine is off
7. `7e292077` Never let a recovery's own wait cancel the press it owes
8. `1d1cd02f` Let the board glow instead of blinking at the room
9. `d0232ab5` Let the last round decide which shift it belongs to

## Behaviour the board now owns

- **Boot grace before the power button.** A host still in POST looks exactly like
  a host that is off, so the button path waits out this machine's own cold start:
  the longest time this board has watched the host put its USB up, plus
  `POWER_BUTTON_GRACE_MARGIN_MS`, clamped 30–150 s, and 60 s until something has
  been measured. Samples below `HOST_BOOT_LEARN_MIN_S` (8 s) are not samples —
  a board that restarted under a running host would otherwise learn its own
  enumeration. The value is persisted (`CAL_VERSION` 7) and the wait spends no
  recovery attempt.
- **A bounded boot wait.** The wait for the host's USB is bounded at
  `WAKE_MOUNT_TIMEOUT_MS` even when nothing is owed, so a board that powered up
  with the machine off still reaches the loop that presses the button.
- **A dim steady LED.** GP25 is now a PWM channel (slice 4, channel B; wrap 999
  ≈ 1 kHz; level 20 ≈ 2% duty) instead of a two-second blink. Bright flashes are
  reserved for this board's own power-up and for a real power-button press, the
  one action that is invisible from outside the machine. `LED!<0-999>` sets the
  level live and is deliberately volatile.
- **The owed press survives its own wait.** The record that arms a recovery is
  held for as long as the recovery is armed, so the grace cannot overwrite the
  decision it is being waited out for.
- **The last round decides which shift it belongs to.** At the cycle deadline the
  board compares the window that is open now (`wake_scheduler_wall_minute()`,
  anchored by the last accepted bridge sample) with the identity the *current*
  round verified at its own shift check (`shift_identity.selected`, reloaded at
  every check). A turned-over window runs the authored switch route instead of the
  authored Finish, with the attempt persisted before that route's first step. No
  flashed schedule, no wall anchor, an uncovered minute, an unverified identity or
  an unchanged window all keep the authored Finish. Nothing here needs the bridge,
  a hotkey or a reply timeout, and a switch that cannot be taken stops loudly
  (`ERR|CYCLE|finish|action=switch-blocked|reason=…`) rather than sleeping through
  a shift.

## Hardware results from the operator's machines

- **Acer — sleep and wake confirmed.** `EVT|WAKE|state=pulse` →
  `EVT|USB|resume|remote-wakeup=1` → `EVT|WAKE|state=host-up|settle-ms=15000` →
  three dismiss enters → `EVT|WAKE|rearm|skipped|reason=no-clock`, with
  `pwr-presses=0` and `READY` naming `pwrbtn=GP7-momentary`.
- **Acer — BIOS route confirmed.** `Restore On AC Power Loss = on` with
  `Deep Power Off Mode = disable` cold-boots the machine after a real cut.
  `Wake Up by USB KB/Mouse` offers `Disabled / S3 / S4`: **S3** is the working
  choice, S4 breaks the pulse path.
- **Acer — the grace has never triggered.** This machine puts its USB up ~2 s
  after the board boots, so the board is never "host absent":
  `EVT|USB|mount|at-s=2`, `EVT|HOST|usb=UP|mounted=1|at-s=3`,
  `skipped|reason=host-up`.
- **Acer — the LED is confirmed on hardware.** It was originally never driven;
  the operator confirmed the blink on the v4 build, and the dim glow is what v7
  and later run.
- **Charger test on a 5 V phone supply.** The LED kept working on the charger
  (operator-confirmed), so the board was neither parked nor dead — the missing
  press was the bookkeeping bug fixed in `7e292077`, not a hang. That test is the
  reason the bug was found at all.
- **HP Gen 7.** The Pico loses power with the machine (its LED goes dark when the
  machine is off), so the USB/power-button route is pointless there unless the
  board gets its own 5 V supply; the BIOS route is what works. `Advanced → Boot
  Options → After Power Loss` = **Power On** (older generations:
  `Advanced → Power-On Options`), greyed out while `Advanced → Power Management
  Options → Power On From Keyboard Ports` is enabled. Whether this machine
  exposes that option is still unanswered.

## Findings fixed on this branch

1. A board that restarted under a running host learned a 2 s "cold start" and
   shortened its own grace to the floor. The 8 s learn floor and the 30 s grace
   floor exist because of that field sample.
2. A verdict was unreadable after the fact: the replay could not say what the
   helper board had seen or when. Every verdict now carries its inputs
   (`EVT|HOST|usb=|mounted=|at-s=`, `EVT|USB|mount|at-s=`, `at-s=` on both skip
   lines).
3. A board powered while the host was off never reached the loop that presses the
   button, because the boot wait for the host's USB was unbounded.
4. The press a recovery owed was cancelled by the board's own record refresh
   inside the grace, and the recovery then skipped as `reason=limit` after the
   full wait. Fixed by holding the arming flags while a recovery is armed.
5. The status LED was never driven at all; then it blinked at the room. It is now
   a dim steady glow with two permitted flashes.
6. The last round could sleep the machine through a whole shift when it ended
   after the next window had opened.

## Operator decisions recorded this session

- **`Finish` puts the machine to Sleep** — operator-confirmed on the real
  machine. The `Win+X → U → ↑↑ → Enter → ↓ → Enter` chain is the Windows Sleep
  item itself and no separate sleep step exists in the file. An earlier inference
  by the agent, drawn from the menu order in the macro, was wrong and must not be
  re-derived.
- **The last-round decision belongs to the board**, independent of the bridge: no
  macro edit, no `shiftCheck` step inside `Finish`.
- **The PC817 power-button route is parked, not deleted.** It is recorded in the
  operator's backlog and is only needed to start a machine that was deliberately
  shut down.
- **Manual power-on during the rest window must never be obstructed.** The board
  never sleeps or shuts a host down; no such code exists in it.

## Still unproven on hardware

- **A real scheduled window firing by itself** — without a manual `WAKE!`:
  `EVT|WAKE|state=pulse|target=18:30` → `EVT|WAKE|state=host-up` → three dismiss
  enters → `EVT|WAKE|state=start` → `EVT|WAKE|rearmed`.
- **Automatic macro continuation after a reboot** — the Startup route plus a
  bridge clock: `OK|WAKE|armed|window=…` and `EVT|CYCLE|armed-at-boot`.
- **The grace itself** — cut the board's power while the machine is off or in
  POST: `EVT|WAKE|recovery|armed|reason=clock`, then
  `EVT|WAKE|recovery|skipped|reason=boot-grace|grace=…` while it waits, then
  `EVT|PWRBTN|host-boot|learned-s=…` when the host comes up on its own, and no
  `EVT|PWRBTN|press` while the machine is still in POST.
- **Power return after a real cut** — the host boots by itself, the board presses
  nothing (`skipped|reason=host-up`), and the round continues.
- **The last-round switch** — a real night→day boundary at 04:30 with the fifth
  round ending after it: `EVT|CYCLE|finish|action=switch|…|origin=board`; a round
  ending before it gives `action=finish|reason=window-same` and the machine
  sleeps.
- **A reset while the host is asleep** — `reason=owed` on the recovery line and
  `wake-recovery=1` in `STATUS`.
- **The power-button line itself** — needs one PC817, a 330–470 Ω resistor and a
  wire to the front-panel header.

The board's console is its own USB, so lines written while the host was asleep
arrive afterwards as an `RPL|begin … RPL|end` replay.

## Local build recipe (reproduced from CI)

`cmake` and `ninja` are not preinstalled in this sandbox; `pip3 install cmake
ninja` provides both. The Pico SDK and the ARM toolchain are at `/data/pico-sdk`
and `/data/arm-toolchain`.

```bash
export PICO_SDK_PATH=/data/pico-sdk
export PATH="/data/arm-toolchain/bin:$PATH"
python3 tools/abvm.py compile firmware/abvm/tests/abvm_smoke.amsj "$OUT/program.abp" --routes Game Whisper
cmake -S firmware/abvm/pico -B "$BUILD" -G Ninja -DPICO_BOARD=pico \
      -DPICO_SDK_PATH="$PICO_SDK_PATH" -DABVM_PROGRAM="$OUT/program.abp"
ninja -C "$BUILD"
bash firmware/abvm/pico/build_batch.sh "$OUT/program.abp" \
     firmware/abvm/pico/batch30.json "$TEMPLATES" "$BUILD/_deps/pico1-src"
```

`build_batch.sh` writes one bundle per identity into its own directory under
`$TEMPLATES`; the operator package flattens them into `native-runtime/` as
`runtime-<CODE>.uf2` plus `index.json`, whose `sha256` is of the copied file.

## Quality gates on `d0232ab`

- `test_abvm_shift_wake.py` 33 passed (four new), `test_shift_schedule.py` 9,
  `test_shift_identity.py` 6, `test_wake_tools.py` 8, plus the ABVM, buzzer,
  flash-guard, game-buff, human-mouse, mouse-region, mouse-speed and uf2 suites.
- `shift_identity_smoke.c` is new and runs the window function over the
  operator's own schedule (day 04:30–14:30, night 18:30–04:30): both boundaries,
  the uncovered gap, a midnight wrap, an out-of-range minute and an unconfigured
  descriptor, and it cross-checks `shift_identity_expected()` against it minute by
  minute.
- `test_shift_identity.py` and `test_shift_schedule.py` extract the production
  shift-check adapter by string anchor. The new forward declaration of
  `fail_shift_check()` sits above that slice and would have hijacked the bare-name
  anchor, so both now anchor on the full definition; the extracted slice is
  byte-identical to before.
- CI on `d0232ab`: ABVM / Pico UF2 bring-up ✅ (compiles the firmware and
  flattens all 30 templates), portable contracts ✅, Windows TestRunner ✅,
  changelog entry ✅, ARM 2.8 compile ✅, sensitive-guard ✅. The `build` job
  (`final-fixes-build.yml`, step "Patch final behavior") fails on every PR into
  `main` and is unrelated to this branch.

## Session artifacts deliberately not published

- The operator's `.amsj`. It carries a plaintext Windows credential in its
  login route and a personal hand-movement profile, so it stays a session
  artifact. Never commit it to this public repository.
- The firmware packages handed over during the session (v1 … v8) and the
  operator's own exported UF2. The branch carries the source; the packages are
  convenience copies.

## Open items

- HP Gen 7: is `After Power Loss` present and settable?
- Do the Pro Micro LEDs stay lit while the machine is fully off? That decides
  whether the PC817 route is worth wiring on a machine that keeps 5 V in
  soft-off.
- The rest-window power-cut exception (18:00 cut, 18:15 return, no shift active)
  still has two missing pieces: a clock-only bridge run at Windows login, and the
  Windows idle-sleep setting. Deferred by the operator.
- PR #19 (`feat/windows-identity-report`) is still open as a draft on its own
  base.
- The legacy `apply-*` workflows stay untouched by explicit operator request.
