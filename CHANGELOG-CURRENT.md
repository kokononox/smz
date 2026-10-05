## v0.9.68 — portable human-mouse v3 and Native infinite-loop fix

- Fixed Native `forLoop` compilation so `mode=infinite` always emits the VM's
  canonical zero count, even when the editor retains a stale finite count.
- Added four slowly varying, board-local movement states: focused, normal,
  idle, and fatigued. A state persists for 6–18 moves rather than changing
  independently on every action.
- Correlated each state with bounded selections inside the authored speed,
  duration, curvature, and pause ranges; correction probability remains capped.
- Added compact two-leg geometry for selected movements of at least 450 pixels.
  Waypoints stay on-screen, the authored destination is preserved exactly, and
  overshoot still uses the existing bounded correction phase.
- Mixed the first movement's board-local uptime into the Pico PRNG so cold
  boots do not replay the same sequence. No host cursor, OS, network, or vision
  dependency was introduced.
- Added deterministic contracts for stale-count infinite loops, state cadence,
  per-boot variation, multi-leg endpoint conservation, and hostless operation.

## ABVM native R30 — Classroom Light Watch accepts Native sample age

- Classroom's strict light-telemetry parser now accepts the Native Pico reply
  `OK|LUX|lux=...|sensor=ok|age=...`.
- The optional age remains validated as a nonnegative 32-bit millisecond value;
  malformed or unknown compact fields are still rejected.
- Added coverage in both the Windows TestRunner and typed light-telemetry suite
  for the exact hardware response observed during Light Watch.

## ABVM native R29 — single-stage light save and adaptive overlap fit

- Each five-second Native light sample is now saved immediately; calibrating
  the other five stages or pressing Yellow a second time is no longer required.
- Ported the approved adaptive overlap policy: profile centers remain fixed,
  the new candidate shrinks first, then only the conflicting neighbour, with a
  0.3-lux integer-safe gap and a 0.5-lux minimum half-width.
- Candidate and adjusted neighbours are committed in one dual-sector,
  CRC-verified transaction while unrelated light, sound, and cycle values are
  preserved.
- `CAL|mode=saved` now reports whether fitting occurred and how many neighbours
  changed. Irreconcilably close centers return `centers-too-close` without
  changing flash or active Guard ranges.

## ABVM native R28 — expressive Buzzer step and live preview

- Expanded Buzzer presets with notification, error, rising, and falling sounds.
- Added per-step volume from 1–100% and four edge modes: sharp, smooth,
  fade-in, and fade-out.
- Added a preview button in the Buzzer editor. It plays the currently edited
  pattern on the connected GP6 buzzer without saving or running the route.
- Extended both Native ABVM and portable `BEEP` while preserving the legacy
  two-field `BEEP|frequency,duration` command and old ABVM images.
- Native envelopes are nonblocking and update PWM duty every 4 ms.

## ABVM native R27 — generation-safe cycle boundary

At natural cycle expiry, the physical keyboard and mouse release reports still
run, but completion tokens belonging to the aborted Game VM generation are now
discarded before the After route starts. This removes the observed
`ERR|HID|complete|lane=0` boundary warning and prevents any stale actor
completion from being applied to After or Startup.

## ABVM native R26 — approved persistent After/Startup cycle

### Source contract

Ported the hardware-approved `A-marker-usb-fused-v2` cycle to Native ABVM
without changing the authored After or Startup pipelines.

### Behavior

- Native UF2 export embeds the configured wall-clock Restart range, a five-cycle
  safety limit, the After/Restart route, the Startup route, and the two-second
  USB-stability contract.
- A manual Guard start clears stale restart authority and chooses one cycle
  deadline. Pause does not extend it.
- Natural expiry releases HID/watch actors, writes a program-bound persistent
  marker, and executes the existing humanized After route.
- Pro Micro `HOSTUSB DOWN/SUSPEND/UP` events are consumed on the private UART.
  Pico USB mount state remains the fallback when no ARM lifecycle event exists.
- Only an armed marker can start Startup after USB returns. Startup runs once,
  clears the one-shot marker, resumes Guard at stage 1, skips Desktop, and waits
  for Login/DC before starting a fresh deadline.
- Manual stop/override and light, ARM, Guard, ABVM, or Startup failures clear
  the marker. The marker counter blocks more than five automatic restarts.

### Persistence and compatibility

The marker reuses two reserved bytes in the existing dual-sector,
CRC-protected, program-SHA-bound calibration record. Existing light and sound
calibration layout and values are unchanged. Current `Launch` documents retain
their established route-ID 2 alias for the authored After tab.

### Validation

- Added compiler/verifier coverage for the versioned Native Cycle descriptor.
- Added a contract test for the approved After/Startup route aliases, NVM
  authority, USB lifecycle, five-restart limit, and stage-1 resume.
- Recompiled the current fishing project with all ten Native routes and the
  cycle descriptor.

## ABVM native R25 — audible route cues and frame-safe text typing

### Problem observed

After the sound-threshold fix, Guard route changes no longer produced an audible buzzer cue, and Type Text could appear to arrive in bursts in a game/login field.

### Root cause

Guard started its transition sweep and the VM emitted its mandatory route-entry `RELEASE_ALL` in the same service cycle. The generic release helper silenced every buzzer pattern, so the transition tone was cancelled before it became audible. Type Text used an 8 ms press: valid for USB HID, but shorter than a typical 60 Hz game/UI input frame.

### Change

- Route-entry release now preserves independent Guard/calibration cues while still cancelling project/direct BEEP actions that own a suspended lane or reply.
- USB unmount and suspend continue to silence the buzzer unconditionally.
- Added a `BUZZER|cue=transition|profile=...|stage=...` diagnostic when a confirmed Guard transition schedules its cue.
- Increased Type Text key hold from 8 ms to 24 ms while retaining the configured per-character humanized gap.

### Validation

- Added a release-boundary contract proving system cues survive while owned BEEP actions are cancelled.
- Strengthened the native HID smoke test to require a release report between every press and at least a 20 ms key hold.
- Audited the supplied UF2: it contains no BEEP instruction. Its Login/DC route deliberately returns to `label1`, and its password Type Text enables one synthetic typo every 7–12 eligible characters.

### Next test

Flash a newly exported UF2, start Guard, and confirm a later route transition emits both `BUZZER|cue=transition` and an audible short sweep. For clean password entry, set `typoEveryMin` and `typoEveryMax` to `0`; remove the final `gotoLabel label1` only if repeated login retries are not desired.

## ABVM native R24 — authoritative sound threshold and Catch response audit

### Problem observed

The Game Catch Wait For Sound armed correctly but repeatedly timed out at `peak=5`, so its child F keystroke never ran. Classroom calibration completed, yet Native export could still retain the old 76-unit detector threshold. Direct Classroom tests also disconnected at unsupported `SETRES` before reaching WSND.

### Root cause

The Native compiler selected legacy `peakMin` ahead of the Classroom-calibrated `threshold`, and the Pico ARM actor then unconditionally replaced the compiled value with any saved physical calibration. The F response bytecode itself was correct: profile-2 detection advances directly from WATCH to the F instruction, while timeout jumps past it.

### Change

- Made the Wait For Sound `threshold` field authoritative for Native ABVM; `peakMin` remains legacy classification metadata.
- Threshold `0` now explicitly selects the saved physical profile. Nonzero project thresholds are no longer silently overwritten by flash calibration.
- Added effective `threshold`, `min`, and `config=project|saved` to WATCH and SOUND diagnostics.
- Added bounded Native direct-run compatibility for `SETRES`, asynchronous `WSND`, and nonblocking `BEEP`, so Classroom sensor tests no longer disconnect before execution.
- Updated the Persian/English threshold labels to document the zero-value saved-calibration mode.

### Validation

- Compiled a profile-2 test where `threshold=8` and stale `peakMin=76`; the SOUND descriptor correctly contains 8.
- Reference-VM regression proves a profile-2 hit executes child key F and a timeout skips it.
- Added direct-run parser/actor contracts and reran all ABVM tests.

### Next test

In the real Catch step press Calibrate, confirm the threshold changes from 76, save, export and flash a new UF2. The hardware log must now show the effective threshold/config on WATCH; on a splash it should emit `OK|SOUND|profile=2` followed by `HID|keyboard|accepted` for F.

## ABVM native R23 — Native Buzzer step execution

### Problem observed

A quick Wait For Sound sensor test with Success and Warning Buzzer branches could not export a Native UF2 and stopped with `unsupported ABVM step: buzzer`.

### Root cause

Classroom Studio exposed and serialized the GP6 Buzzer step, and the native firmware already owned a nonblocking PWM buzzer, but ABVM ABI 1 had no Buzzer instruction or runtime actor binding.

### Change

- Added the bounded `BEEP` ABVM opcode with 30–20000 Hz and 1–60000 ms validation.
- Native compilation now expands short, double, warning, success, and custom Buzzer patterns into cooperative BEEP/DELAY instructions.
- Added a single-channel GP6 PWM resource certificate and a nonblocking Pico Buzzer actor that resumes the originating VM lane after the tone duration.
- Pause, Stop, route cancellation, USB loss, and release boundaries cancel an in-flight custom tone safely.

### Validation

- Added compile/verify coverage for Success and Warning patterns and PWM resource accounting.
- Added firmware contracts for bounded bytecode dispatch, asynchronous lane completion, and the GP6 tone actor.
- Full Pico SDK, Windows TestRunner, portable, and packaging gates run in CI.

### Next test

Export the Desktop Wait For Sound test with Success/Warning Buzzer branches, flash the new UF2, and verify a sound above the calibrated threshold selects Success while timeout selects Warning.

## ABVM native R22 — responsive Classroom sound calibration

### Problem observed

Pressing Calibrate in the Wait For Sound dialog played the calibration-exit cue, then Classroom Studio became unresponsive until it was closed.

### Root cause

Classroom requested `SCAL|2000`, while the ARM calibration protocol and native Pico proxy accept at most 1000 ms, so the request was rejected and entered the slower legacy-probe path. In addition, the serial reader synchronously invoked the WPF dispatcher before parsing each reply, creating a reverse wait between the modal UI and the reply reader.

### Change

- Request a protocol-valid one-second `SCAL|1000` sample with a bounded four-second command timeout.
- Changed the serial `LineReceived` callback to queue its WPF log update with `Dispatcher.BeginInvoke`, so reply parsing never waits for the UI thread; other logging semantics stay unchanged.
- Kept the restored legacy Guard and physical light/sound calibration note sets unchanged.

### Validation

- Updated the existing SCAL source contract to require the ARM/Pico 1000 ms protocol limit and verified that `LineReceived` queues rather than blocks before reply parsing.
- Existing native SCAL proxy, firmware, Windows, and packaging gates remain enabled in CI.

### Next test

Open Wait For Sound, press Calibrate once, and confirm the dialog stays responsive, returns a threshold in about one second, and the log shows `OK|SCAL|avg=…|max=…` without WSND fallback.

## ABVM native R21 — Classroom SCAL compatibility proxy

### Problem observed

Classroom Studio connected to the native Pico successfully, but Wait For Sound calibration logged `ERR|TIMEOUT|SCAL` and fell back to legacy WSND probing.

### Root cause

The native runtime supported physical two-step sound calibration internally but did not expose the existing bounded `SCAL|ms` CDC compatibility command used by the Classroom calibration dialog.

### Change

- Added asynchronous `SCAL|ms` validation and forwarding to the Leonardo ARM sound-calibration actor.
- Return the legacy-compatible `OK|SCAL|avg=…|max=…` response without blocking CDC, HID, Guard, or buttons.
- Reject malformed, overlapping, or physical-calibration-conflicting requests and bound missing replies with `ERR|TIMEOUT|SCAL`.

### Validation

- Added regression contracts for command parsing, bounded pending state, ARM result collection, success response, and timeout response.
- Physical button calibration continues to consume ARM calibration results only when no Classroom SCAL request owns them.
- Full Pico SDK and Windows gates run in CI.

### Next test

Open Wait For Sound, press the Classroom calibration button, and confirm the log receives `OK|SCAL|avg=…|max=…` instead of falling back to WSND probing. Then complete one physical sound-calibration run to verify both paths remain independent.

## ABVM native R20 — restore legacy Guard and calibration score

### Problem observed

The restored generic Buzzer presets still did not match the richer audio feedback used by the previous Guard, light calibration, and two-profile sound calibration systems.

### Root cause

The generic `short/double/warning/success` step presets were not the same contract as the legacy physical-control score embedded in the prior combined runtime.

### Change

- Restored exact Guard Start, Stop, Pause, and Resume patterns.
- Restored six light-calibration position notes, record-start, stage-complete, save-success, save-error, all-profile-complete, and enter/exit melodies.
- Restored sound-calibration ID 1/2 notes, silence-start and target-start tones, plus shared completion/error feedback.
- Added a distinct profile-dependent 150 ms rising sweep for confirmed Guard transitions after the initial state; only this transition cue uses the newer smooth style, while Guard controls and both calibration systems retain their exact legacy scores.
- The initial `start-at-current-state` transition stays silent because the Guard Start melody already confirms activation.
- Kept the GP6/S8050 wiring and nonblocking fixed-state playback.

### Validation

- Regression contracts assert every legacy frequency, duration, and pause.
- Calibration events are mapped to the exact prior cue points.
- The buzzer translation unit compiles with strict C11 warnings as errors; full Pico and Windows gates run in CI.

### Next test

Verify Guard Start/Stop/Pause/Resume, cycle through all six light-calibration profiles, and run both sound-calibration IDs. The audible sequences should match the previous system while CDC heartbeats continue without interruption.

## ABVM native R19 — restore original GP6 buzzer presets

### Problem observed

Hardware build 440 produced smooth sweep cues that did not match the established buzzer sounds used by the previous system.

### Root cause

The new native buzzer actor introduced newly designed glides instead of preserving the existing `short`, `double`, `warning`, and `success` frequency/duration contract.

### Change

- Removed the new sweep/fade note set.
- Restored the exact original presets: short `1000:180`; double `1000:140,100;1000:140`; warning `700:180,90;700:180,90;700:300`; success `900:120,70;1300:220`.
- Retained the nonblocking fixed-state actor, GP6 PWM output, priority handling, and existing S8050 wiring.

### Validation

- Added exact frequency, duration, and pause assertions for all four legacy presets.
- The buzzer translation unit compiles with `-std=c11 -Wall -Wextra -Werror`.
- Full Pico SDK and Windows gates run in CI.

### Next test

Flash the next UF2 and verify that Start/Catch/Calibration use the old success notes, Resume uses double, Stage/Pause/Stop use short, and Timeout/Error use warning.

## ABVM native R18 — bounded Sound/Mouse backpressure and GP6 buzzer

### Problem observed

On real hardware, Route 8 cast once and then stopped with `ERR|ARM|submit|lane=0|reason=2` immediately after the parallel sound watch armed.

### Root cause

The UART actor had a one-command deferred mouse queue, but an early `state != ARM_IDLE` guard rejected the exact Sound-arm/Mouse overlap that the queue was intended to serialize.

### Change

- Admit one bounded deferred mouse command behind an in-flight Move or Sound-arm frame while still rejecting Probe, calibration, Halt, fault, and queue overflow.
- Add an allocation-free, nonblocking passive-piezo driver on GP6 using hardware PWM, continuous frequency sweeps, and attack/release envelopes.
- Add prioritized smooth and patterned cues for Start/Stop, Pause/Resume, Guard stages 1–6, accepted Splash/catch, timeout, calibration success, and faults.
- Keep the buzzer fail-independent: no delay or sleep is introduced into USB, HID, UART, sensor, or VM service paths.

### Validation

- Added a regression contract that rejects the old unreachable deferred-queue guard.
- Added buzzer contracts for GP6, PWM, bounded priority preemption, and the absence of sleeping/blocking calls.
- The buzzer translation unit compiles locally with `-std=c11 -Wall -Wextra -Werror`; full Pico SDK and Windows gates run in CI.

### Next test

Flash the next UF2, start Guard in Game, and allow at least three fishing casts. Each Splash should produce the catch cue and continue the loop without `ERR|ARM|submit|reason=2`; stage, Pause/Resume, timeout, and Stop cues should remain responsive while COM telemetry continues.

## ABVM phase 1 R5 — Pico SDK bring-up UF2

- Added a Pico SDK target that embeds one verified `program.abp` directly in
  flash and boots without CIRCUITPY, FAT, Python, or a connected PC.
- Added USB CDC commands for PING, status, start, Pause/Resume, Stop, Whisper,
  and diagnostic sound detection.
- Added debounced GP3 Pause/Resume and GP4 Start/Stop control.
- Connected the native VM to the RP2040 millisecond clock and nonblocking
  board loop.
- Kept HID, Type, Mouse, and release-all as explicitly logged safe stubs; this
  UF2 cannot emit user input.
- Added a CI Pico SDK/ARM build and uploaded UF2/ELF/program artifacts.

## ABVM phase 1 R4 — native fixed-state loader and scheduler

- Added a dependency-free C11 ABVM core with no runtime heap allocation.
- Added native CRC32/SHA-256, canonical section, resource, route, opcode,
  constant, Loop, Random Package, Scope, and Watch verification.
- Added fixed arrays for lanes/frames plus one suspended interrupt context.
- Added nonblocking action events as the future HID/UART actor boundary.
- Added native Pause/Resume/Stop and Whisper interrupt/resume scheduling.
- Added a host smoke test that executes the real Game/Whisper ABP and rejects
  a corrupted image; the Portable CI now compiles and runs this native core.

## ABVM phase 0 R3 — deterministic control semantics

- Added explicit `WALL` and `ACTIVE` route clock policies.
- Added fixed-state Pause/Resume: HID is released, opcode dispatch stops, and
  the same PCs and frame state resume without recursion or callback work.
- Added terminal Stop semantics that cancel current and suspended lanes and
  always release HID.
- Added one-slot global `INTERRUPT_AND_RESUME` execution for Whisper; nested
  interrupts are rejected by the runtime/resource contract.
- Added reference tests for Pause during a held key, wall/active deadline
  behavior, Whisper interruption, exact Game resume, Stop inside Scope, and
  50-cycle Pause/Resume plus 50-boundary Stop stress.

## ABVM phase 0 R2 — explicit runtime contract

- Replaced the ambiguous two-lane Race contract with structured scopes and an
  explicit `CANCEL_ON_TERMINAL_LANE` policy for fishing.
- Added fail-closed route transition policies, a fixed resource certificate,
  full program SHA-256, and an external PC-only source map.
- Added generated ABI registries and packed layouts for Python, C#, and native
  firmware consumers.
- Kept the 16-byte instruction encoding; current real images remain only a few
  KiB and do not justify an operand-constrained compact encoding.

## ABVM phase 0 — host compiler and ABP1 contract

- Added the first PC-side AMSJ compiler for a binary `ABP1` application image.
- Added fixed-width opcodes for Game/Whisper, loops, Random Package, a two-lane sound-controlled Race, scoped Watch, relative mouse, keyboard, and humanized Type Text.
- Added a fail-closed verifier for CRC, section/range integrity, frame depth, lane count, nested Watch, and capability declarations.
- Added a deterministic host reference VM before any Pico firmware is changed.
- Production Classroom export and the current Pico runtime remain unchanged.

# Classroom Studio — Current Hardware Changelog

این سند مرجع سریع وضعیت شاخهٔ پایدار `stable/natural-mouse-v1` است. ترتیب ورودی‌ها معکوس زمانی است؛ جدیدترین Build همیشه بالاتر قرار می‌گیرد.

## Build 119 R17 — بازگشت کنترل‌شدهٔ Catch Wait و شروع مجدد تمیز

**Previous build:** 118 R15 / Classroom release 221
**Status:** selective UX fallback; restart-A preserved; hardware retest required

### Problem observed

نسخهٔ سبک از MemoryError عبور کرد، اما Route بازی بدون مسلح‌شدن صدای Catch بلافاصله Complete می‌شد و پس از Stop/Start نیز اجرای قابل‌مشاهده‌ای نداشت. Classroom گاهی تا قطع و وصل کابل، PING پیکو را دریافت نمی‌کرد. همچنین انتزاع Build 95 محل واقعی انتظار برای صدای چلپ و تنظیمات ID 2 را از پروژه پنهان کرده بود.

### Root cause

`Splash Listener` فقط یک Marker بدون تنظیم بود و پاسخ Catch در تب جداگانه نگه‌داری می‌شد؛ در نتیجه ساختار پروژه از مدل واقعی «پس از رهاکردن قلاب، همین‌جا منتظر صدای ID 2 بمان» فاصله گرفت. شروع مجدد دستی نیز Heap/نتیجهٔ Async Sound را صریح پاک نمی‌کرد و Bridge فقط یک PING اولیه می‌فرستاد.

### Change

- استپ صریح `Wait For Sound` با ID، Peak، Min duration، Cooldown و Timeout به منو برگشت؛ Catch فقط با ID 2 و فقط در همان محل Cast فعال می‌شود.
- پاسخ Catch به Child همان استپ منتقل می‌شود؛ تب Splash از UI حذف و فقط برای مهاجرت فایل‌های Build 95–118 نگه‌داری می‌شود.
- ID 1/Whisper تنها شنوندهٔ سراسری Game باقی ماند و پس از واکنش همان Cursor و Deadline را ادامه می‌دهد.
- فایل‌های قدیمی `splashListener` هنگام بازشدن به Wait For Sound صریح تبدیل و اکشن‌های تب Splash، از جمله F، به Child آن کپی می‌شوند.
- Stop → Start ماژول‌های Route، کلیدها و نتیجهٔ Async Sound را پاک می‌کند تا هر اجرا بدون قطع کابل از محیط تازه آغاز شود.
- Bridge روی همان COM تا چهار PING بی‌خطر می‌فرستد و برای بازیابی اتصال به Cable cycle نیاز ندارد.
- منطق موفق Restart-A و قاعدهٔ «After فقط با Deadline چرخه» از R11 بدون تغییر حفظ شد.

### Validation

- پروژهٔ واقعی ارسالی تأیید شد: ID 2 برابر Splash با Peak `76..511` است، Marker در شاخهٔ Game قرار دارد و پاسخ تب قدیمی کلید F است.
- قراردادهای Explicit Catch، Scoped Runtime، Whisper resume، Stop/Start و Restart-A سبز هستند.
- تست Bridge تأیید می‌کند PING دوم روی همان COM31 بدون Close/Reopen متصل می‌شود.
- Python compile و بررسی whitespace سبز است؛ TestRunner دات‌نت در CI ویندوز اجرا خواهد شد.

### Next test

پس از Export پروژهٔ مهاجرت‌یافته، لاگ باید در هر Cast شامل `SOUNDWATCH|armed` و سپس `detected` یا `timeout` باشد؛ با تشخیص ID 2 کلید F اجرا و Cast بعدی با همان Deadline ادامه یابد. سپس Stop/Start و اتصال مجدد Classroom را بدون قطع کابل چند بار تکرار کنید.

## Build 118 R15 — شکستن واحد کامپایل Runtime

**Previous build:** 118 R14 / Classroom release 221
**Status:** compiler-sharding candidate; 55 portable contracts green; hardware retest required

### Problem observed

Bundle 710 با وجود R14 و `free=20992` دوباره دقیقاً در `runtime-import` با خطای تخصیص پیوستهٔ ۱۳۳۶ بایت متوقف شد. بنابراین کاهش ۱۶۵ بایتی فایل Runtime برای پایین‌آوردن Peak کامپایل CircuitPython کافی نبود.

### Root cause

مسئله کمبود مجموع Heap نیست؛ Runtime یک واحد کامپایل ۶۴۲۵ بایتی بود که پس از Preload شدن Sound، Parallel، Events، Response و Core باید یک‌جا کامپایل می‌شد. Heap آزاد کافی بود، اما بلوک پیوستهٔ موقت موردنیاز Compiler در Heap قطعه‌قطعه‌شده وجود نداشت.

### Change

- عملیات ترتیبی Game شامل `DELAY/KEY/RMOUSE/SOUNDWATCH/WPROFILE/WSND` به Shard مستقل `plan_engine_game_actions.py` منتقل شد.
- اندازهٔ واحد Runtime از ۶۴۲۵ به ۳۴۹۱ بایت کاهش یافت؛ Shard عملیات نیز فقط ۳۷۶۷ بایت است.
- Actions پیش از Core/Runtime روی Heap تازه‌تر Preload می‌شود؛ در زمان اجرا فقط Bind سبک Callbackها انجام می‌گیرد.
- Cursor، Deadline، Scoped Winner، Response فایل‌محور، Random Package، ARM و Cadence بدون تغییر معنایی حفظ شدند.
- موجودی Bundle و Manifest از ۴۳ به ۴۴ فایل افزایش یافت و Export/Verify/Calibration cleanup با آن همگام شد.

### Validation

- هر ۵۵ قرارداد Portable و Python compile سبز است.
- قراردادهای File-backed Game، Scoped Splash، SoundWatch، Export و Calibration Heap سبز هستند.
- شبیه‌ساز داخلی: ۳ Cast، ۲ Catch و Deadline ثابت، بدون Restart یا `KeyError`.
- TestRunner محلی اجرا نشد چون SDK دات‌نت در محیط عامل موجود نبود؛ Gate رسمی Windows روی PR اجرا خواهد شد.

### Next test

روی Pico، Bundle جدید باید `after-actions-preload` و سپس `after-runtime-import` را ثبت کند. بعد از آن صدا و Response باید اجرا شوند و حداقل دو Cast متوالی بدون `MemoryError`، `KeyError('_game_cursor')` یا Restart کامل Game ادامه یابند.

## Build 118 R14 — کاهش Peak کامپایل Runtime

**Previous build:** 118 R13 / Classroom release 220
**Status:** compiler-memory hotfix candidate; local simulation green; hardware retest required

### Problem observed

Bundle 700 پیش از اجرای Game و پیش از رسیدن به Random Package در مرحلهٔ `runtime-import` با `free=20656` و خطای تخصیص ۱۳۳۶ بایت متوقف شد.

### Root cause

اصلاح معنایی R13 درست بود، اما ساخت Signal خروجی و نگهداری Cursor در واحد کامپایل Runtime، Peak حافظهٔ کامپایل CircuitPython را از حد تخصیص پیوسته عبور داد. بستهٔ ۱۷۶ حرکتی علت مستقیم نیست؛ فقط بعد از بارگذاری می‌تواند فشار اجرایی را بیشتر کند.

### Change

- ساخت Exit Signal به ماژول ازپیش‌بارگذاری‌شدهٔ Events منتقل شد.
- Runtime فقط Cursor و Labelها را به Events واگذار می‌کند.
- رفتار Scoped Winner، Resume و Response فایل‌محور R13 بدون تغییر حفظ شد.
- Route، Random Package، ARM و Cadence دست‌نخورده باقی ماندند.

### Validation

- اندازهٔ Runtime از ۶۵۹۰ به ۶۴۲۵ بایت LF کاهش یافت و از Runtime موفق R12 نیز کوچک‌تر شد.
- تست Scoped Splash/SoundWatch و کامپایل Python سبز است.
- شبیه‌ساز داخلی: ۳ Cast، ۲ Catch و Deadline ثابت.
- Route واقعی ۳۶۹ فرمانی Bundle 700 با مدل Pump: ۶ Cast، ۵ Catch و بدون `MemoryError` یا `KeyError`.

### Next test

روی Pico، Bundle جدید باید از `after-runtime-import` عبور کند، صدا را تشخیص دهد، Response را اجرا کند و حداقل دو Cast متوالی را بدون `MemoryError` یا `KeyError('_game_cursor')` کامل کند.

## Build 118 R13 — حفظ Scoped Winner تا Resume

**Previous build:** 118 R12 / Classroom release 216
**Status:** hardware hotfix candidate; CI green; hardware retest required

### Problem observed

Bundle 670 صدای واقعی را با `peak=102` تشخیص داد و Response فایل‌محور تا `after-response-index|commands=5` پیش رفت، اما Route بلافاصله Complete شد و ورود بعدی Game با `KeyError('_game_cursor')` متوقف گردید.

### Root cause

روی سخت‌افزار، ASND ممکن است فقط هنگام `arm.pump()` داخل `resolve_sound_watch()` دریافت شود. در این مسیر Fast path نتیجهٔ خام را نمی‌دید، Scoped Winner داخل Scheduler مصرف می‌شد و `finally/end_profile_wait` آن را پیش از Resume پاک می‌کرد. Signal بعدی نیز بدون Cursor به Facade می‌رسید.

### Change

- Scoped Winner پیش از پاک‌سازی Profile در Signal خروجی ذخیره می‌شود.
- Response فقط پس از Unwind کامل Scheduler اجرا می‌گردد.
- تمام خروجی‌های SoundWatch، Cursor، Labelها و Game state را حمل می‌کنند.
- شبیه‌ساز داخلی صدا را فقط از مسیر واقعی `arm.pump()` دریافت می‌کند، نه با تزریق مستقیم هنگام Sleep.
- Package بزرگ و Route فایل‌محور بدون تغییر حفظ شده‌اند.

### Validation

- همهٔ Gateهای PR #75 سبز هستند: Portable، Windows TestRunner، ARM 2.8، Plan2 و Security.
- شبیه‌ساز فشرده: ۳ Cast، ۲ Catch و یک Deadline ثابت.
- Route واقعی ۳۶۹ فرمانی Bundle 670 با مدل Pump: ۶ Cast، ۵ Catch و Deadline ثابت `617.074s`.
- هیچ `MemoryError`، Restart Game یا `KeyError('_game_cursor')` رخ نداد.

### Next test

در سخت‌افزار واقعی باید پس از `SOUNDWATCH|detected`، تله‌متری Response و اجرای F ثبت شود؛ سپس بدون `ROUTE/complete` زودهنگام، Cast بعدی با همان Deadline و بدون تکرار Buffها ادامه یابد.

## Build 118 R12 — ادامهٔ همان Cursor پس از Splash

- پاسخ Scoped Splash پس از Unwind امن Scheduler اجرا می‌شود و سپس همان Cursor صریح، Frameهای Loop و Deadline اصلی `LOOPTIME` روی فایل Flash بازشده Resume می‌شوند.
- پایان Catch دیگر `game_steps.txt` را از ابتدا اجرا نمی‌کند؛ Buffها تکرار و تایمر ده‌دقیقه‌ای Reset نمی‌شوند.
- نتیجهٔ خام ASND پیش از `ASNDCANCEL` ثبت می‌شود تا Splash هنگام خروج از `PGROUP` از دست نرود.
- شبیه‌ساز ماهیگیری خودکار Cast با 7، حرکت موازی Mouse، Pause/Resume، Splash هشت‌ثانیه‌ای، اجرای F و Cast بعدی را کنترل می‌کند.
- فایل واقعی ۳۶۹ فرمانی Bundle 440 در شبیه‌ساز ۶ Cast و ۵ Catch را با یک Deadline ثابت اجرا کرد.
- Resume session بین Runtime و Events تقسیم شده است تا Runtime حتی با CRLF ویندوز زیر سقف Compiler حافظهٔ Pico باقی بماند.
- تمام Gateها سبزند: Portable contracts، Windows TestRunner، ARM 2.8، Plan2 و Security.

## وضعیت فعلی در یک نگاه

- **Candidate Build 118 — Architectural:** Patchهای Stack متوقف شدند. اجرای ترتیبی Game اکنون یک VM تکرارشونده با `Cursor` و Stack صریح برای `LOOP/LOOPTIME/RPKG` است؛ هیچ فراخوانی بازگشتی `_run` باقی نمانده است. SoundWatch بدون Callback با Poll مستقیم Context کار می‌کند و Response پس از بازگشت Scheduler روی Stack تخت اجرا می‌شود.
- **Candidate Build 117:** Bundle 440 تمام Importها و Bind را پاس کرد، اما پس از peak=95 باز هم پیش از callback telemetry شکست خورد. حتی Queue function پایتونی در Stack عمیق `sleep_ms` یک Frame اضافی بود. Callback/Closure به‌طور کامل حذف شد: `PlanContext.sleep_ms` مستقیماً SoundWatch را Poll می‌کند، Winner را در Slot داخلی نگه می‌دارد و Scheduler پس از Unwind آن را با `take_sound_watch()` تحویل می‌گیرد.
- **Candidate Build 116:** Bundle 420 همهٔ Preload/Importها و Bind زودهنگام Response را پاس کرد، اما پس از peak=105 هنوز پیش از `before-response-callback` شکست خورد. علت باقی‌مانده دو فراخوانی اضافی روی pystack محدود بود: `suspend_sound_watch()` هنوز داخل callback اجرا می‌شد و Scheduler برای سرویس Winner وارد Wrapper دیگری می‌شد. Callback اکنون فقط Winner را در Slot ازقبل‌موجود می‌گذارد؛ Scheduler پاسخ را Inline و مستقیماً با Runner ازپیش Bindشده اجرا می‌کند.
- **Candidate Build 115:** Bundle 410 تمام Importها، Index ۳۶۹فرمانی، Parallel، نخستین RMOUSE و تشخیص واقعی Sound با peak=178 را پاس کرد، اما Response هنوز مستقیماً از callback تو‌در‌توی `sleep_ms` اجرا می‌شد و پیش از `before-response-file` با `MemoryError('')` شکست خورد. Callback اکنون فقط Winner را Queue می‌کند؛ ماژول Response پیش از اجرای Route یک‌بار Bind/Cache می‌شود و Runner پس از بازگشت callback در Scheduler اجرا می‌گردد. Telemetry مرزی Bind/Callback اضافه شده و Mouse/ARM/Cadence تغییر نکرده‌اند.
- **Candidate Build 114:** افزودن Flash-backed Response در Build 113 ناخواسته Runtime را دوباره به 8.6KB رساند و Compiler peak جدید allocation=1336 پیش از اجرا ایجاد کرد. Response اکنون در `plan_engine_game_response.py` مستقل زیر 4KB قرار دارد و همراه Parallel/Events پیش از Core/Runtime Preload می‌شود؛ Runtime اصلی دوباره کوچک و bounded است.
- **Candidate Build 113:** Build 112 تمام Importها، Index، Parallel و اولین RMOUSE را پاس کرد؛ SoundWatch نیز Splash واقعی را با peak=101 تشخیص داد. شکست فقط هنگام Response بود، چون `_response_commands` فایل Splash را کامل به متن و لیست Tuple در RAM تبدیل می‌کرد. Response اکنون با `_FileCommands` مستقیماً از Flash اجرا و پس از پایان بسته می‌شود.
- **Candidate Build 112:** Bundle 390 هر دو ماژول Parallel/Event و Core را پاس کرد، اما Runtime کوچک‌شده همچنان دقیقاً allocation=1180 می‌خواست؛ بنابراین Peak مربوط به Code Object تابع بزرگ `_run` بود، نه اندازهٔ فایل. Dispatch اکنون به سه تابع bounded `_sound`، `_leaf` و `_run` تقسیم شده و هر Code Object زیر سقف تست‌شده است.
- **Candidate Build 111:** Bundle 388 ترتیب Reserve → Parallel → Core را پاس کرد، اما Compile ماژول 10.9KB Runtime با allocation=1180 شکست خورد. Generator تکرارشوندهٔ Event به ماژول مستقل زیر 5KB منتقل و هر دو واحد Parallel پیش از Core/Runtime Preload می‌شوند؛ Runtime اصلی اکنون زیر 8KB است.
- **Candidate Build 110:** Bundle 381 رزرو Index را پاس کرد، اما چون Core و Runtime پیش از Parallel بارگذاری شدند، Compile Scheduler با free=46,768 و allocation=1388 شکست خورد. ترتیب Peak اکنون Reserve → Parallel preload → Core → Runtime است؛ هیچ منطق Scheduler، Sound، Mouse یا ARM تغییر نکرده است.
- **Candidate Build 109:** Build 108 Parallel را با موفقیت روی Heap تازه Preload کرد، اما Bundle 380 بلافاصله بعد از آن هنگام ساخت Offset index برای ۳۶۹ فرمان با allocation برابر 1336 بایت شکست خورد. Index فشرده اکنون پیش از هر Import موتور Game روی Heap تازه رزرو و سپس بدون Scan/Allocation مجدد به FileCommands منتقل می‌شود؛ Parallel، Sound و Mouse تغییر نکرده‌اند.
- **Candidate Build 108:** Build 107 خطای pystack را حذف کرد، اما Bundle 371 پس از ۲۱ ثانیه Prelude و با وجود 45KB Heap آزاد، هنگام Compile دیرهنگام ماژول Parallel به‌دلیل Fragmentation نتوانست بلوک پیوستهٔ 1388 بایتی بگیرد. Route فایل‌محور پیش از Load اسکن می‌شود و در صورت داشتن PGROUP، همان Scheduler موجود بلافاصله پس از Game Runtime و روی Heap تازه Preload می‌شود؛ منطق Parallel، Sound، Mouse و ARM تغییر نکرده‌اند.
- **Candidate Build 107:** Build 106 Poll تو‌در‌توی SoundWatch را حذف کرد و Listener از مرحلهٔ Arm عبور کرد، اما Bundle 355 هنگام اولین RMOUSE در ساختار `PGROUP→LOOP→RPKG` و پس از Lazy import کامل موس با `pystack exhausted` متوقف شد. Generator بازگشتی Containerهای Game با Stack تکرارشوندهٔ صریح جایگزین شد؛ تولید/ارسال موس، ARM و Cadence بدون تغییرند.
- **Candidate Build 106:** Bundle 350 کل مسیر Desktop→Login→Dashboard→Loading→Game، Split Type و ایندکس ۳۶۹ فرمان را پاس کرد. در نخستین `WPROFILE`، Poll همان SoundWatch هم داخل scoped waiter و هم از callback تعاونی `sleep_ms` انجام می‌شد و با وجود 43KB Heap آزاد، pystack را خالی می‌کرد. scoped waiter اکنون فقط نتیجه را می‌خواند و `sleep_ms` تنها مالک Poll callback است؛ ARM، Natural Mouse و Cadence تغییر نکرده‌اند.
- **Candidate Build 105:** Bundle 340 ثابت کرد Mouse و Type جدید با حاشیهٔ مناسب Import می‌شوند، اما اولین Typo به‌دلیل جاافتادن ثابت `_QWERTY_ROWS` از فایل Split Type با `NameError` متوقف شد. جدول QWERTY و تست اجرای واقعی Neighbor به `plan_engine_login_type.py` اضافه شدند؛ الگوریتم Typo، Natural Mouse، ARM و Sound تغییر نکرده‌اند.
- **Candidate Build 104:** Bundle 330 تمام Importهای تقسیم‌شده Game، ایندکس ۳۶۹ فرمان، Parallel و مرحلهٔ قلاب را پاس کرد؛ آخرین شکست در Import یک‌تکهٔ 12.8KB Login/Mouse با free=42,912 رخ داد. Login به Facade، Core، Mouse و Type زیر 5.6KB تقسیم شد و هر بخش ترتیبی/تنبل با تله‌متری مستقل بارگذاری می‌شود.
- **Candidate Build 103:** Bundle 320 ثابت کرد خود Import یک‌تکهٔ موتور 23KB Game حافظه را از 60,144 به 444 بایت می‌رساند و پیش از `file-index` برای allocation=1784 شکست می‌خورد. موتور به Facade، Core، Runtime و Parallel با Import ترتیبی/تنبل و تله‌متری هر مرز تقسیم شد؛ بزرگ‌ترین ماژول اکنون زیر 10KB است.
- **Candidate Build 102:** شکست Bundle 310 پیش از `file-index` به Import تو‌در‌توی Game→Login محدود شد. Runner موس دیگر هنگام Compile موتور Game وارد نمی‌شود؛ پس از تثبیت Engine و ایندکس Flash، فقط با اولین `RMOUSE` و بین دو GC بارگذاری می‌شود و تله‌متری مرحله‌ای Heap محل هر شکست احتمالی را مشخص می‌کند.
- **Candidate Build 101:** Game بزرگ دیگر به لیست Tupleهای RAM تبدیل نمی‌شود؛ خطوط روی Flash می‌مانند و فقط Offset چهار‌بایتی نگه‌داری می‌شود. Random Packageهای بزرگ نیز با Reservoir Sampling فقط همان ۱–۲ گزینهٔ لازم را نگه می‌دارند و فهرست تمام ۱۶۵ آیتم را نمی‌سازند.
- **Candidate Build 100:** Guard اکنون در طول Route نیز نور را با Debounce کامل پایش می‌کند؛ تغییر پایدار محیط Route قبلی را با آزادسازی Keyboard/Mouse قطع و Route وضعیت جدید را اجرا می‌کند. Route بزرگ Game نیز به‌صورت خط‌به‌خط از Flash خوانده می‌شود تا تخصیص پیوستهٔ 6400 بایتی حذف شود.
- **Candidate Build 99:** Package نور دوباره مرجع قابل‌کنترل شد: NVM فقط تا وقتی اعمال می‌شود که Revision پروفایل‌های Package تغییر نکرده باشد. Fit نیز از Import لحظهٔ Save خارج و در ماژول ازقبل‌بارگذاری‌شدهٔ NVM اجرا می‌شود تا توقف بی‌لاگ پس از نمونه‌گیری رخ ندهد.
- **Candidate Build 98:** MemoryError بوت Bundle 303 رفع شد؛ منطق Adaptive Fit از Import اولیه خارج و فقط هنگام ذخیرهٔ کالیبراسیون Lazy-load می‌شود. اندازهٔ Runtime بوت به کمتر از Baseline Build 96 برگشت و رفتار Fit/NVM/Telemetry بدون تغییر حفظ شد.
- **Candidate Build 97:** رد فوری هم‌پوشانی کالیبراسیون با Fit تطبیقی جایگزین شد؛ ابتدا دامنهٔ جدید و در صورت Center-inside دامنهٔ مجاور فقط از Tolerance عقب می‌روند، Centerها ثابت و ذخیرهٔ دوطرفه اتمیک است. حداقل Tolerance برابر ۰٫۵ و Gap برابر ۰٫۲۵ lux حفظ می‌شود.
- **Candidate Build 96:** Start فیزیکی GP4 اکنون همیشه سنجش/Telemetry تازه ایجاد و منبع مؤثر کالیبراسیون NVM/File و بازهٔ Dashboard را گزارش می‌کند؛ حاشیهٔ Drift پس از نمونه‌گیری از ۰٫۵ به ۱٫۰ lux افزایش یافت و همچنان با پروفایل‌های مجاور Cap می‌شود.
- **Candidate Build 95:** `Wait For Sound` عمومی از مسیر ساخت پروژهٔ جدید خارج شد. استپ اختصاصی `Splash Listener` فقط محل Scoped هر Cast را مشخص می‌کند و Timeout حداقل/حداکثر اکنون مستقیماً در کارت پروفایل Splash تنظیم می‌شود. فایل‌های قدیمی `responseRoute=splash` هنگام بازشدن خودکار Migration می‌شوند؛ قرارداد `WPROFILE` و Runtime/ARM/Mouse تغییر نکرده‌اند.
- **Candidate Build 94:** خطای Boot خروجی Build 93 رفع شد؛ `splash_steps.txt` و `whisper_steps.txt` اکنون پیش از اعتبارسنجی Manifest وارد موجودی Hash Runtime می‌شوند. Routeهای نوری Guard، فریمور Pro Micro و مسیر Natural Mouse هیچ تغییری نکرده‌اند.
- **Candidate Build 93:** Whisper فقط در تمام مدت حضور در Game شنوندهٔ سراسری است و پس از واکنش همان Iterator را ادامه می‌دهد؛ Splash فقط هنگام `Wait For Sound` هر Cast با Timeout تصادفی پیش‌فرض ۱۸–۲۲ ثانیه مسلح می‌شود و چه با شنیدن صدا و چه با Timeout، Cast جاری را تمام می‌کند و به Cast بعدی می‌رود. Deadlineهای حلقهٔ ۱۰ دقیقه‌ای و چرخهٔ ۱۱۰–۱۳۰ دقیقه‌ای حفظ می‌شوند.
- **Candidate Build 92:** شنوندهٔ واحد صدا در تمام تب Game فعال می‌ماند؛ Peak را با بازه و Priority دسته‌بندی می‌کند، تب Whisper یا Splash را به‌صورت وقفه اجرا می‌کند و سپس همان Iterator محیط بازی را ادامه می‌دهد. تنظیم بازه‌ها و Cooldown کنار خروجی Pico قرار گرفت.
- **Candidate Build 91:** دو Wait For Sound هم‌زمان دیگر Listener دوم روی ARM باز نمی‌کنند. Scheduler یک Listener فیزیکی با پایین‌ترین Threshold می‌سازد و با Peak گزارش‌شده، بالاترین پروفایل منطبق را برای اجرای Buzzer انتخاب می‌کند.
- **Candidate Build 90:** پروژهٔ `s1.amsj` دیگر به‌خاطر دو Buzzer داخل شاخه‌های موازی Wait For Sound مسدود نمی‌شود؛ Buzzer اکنون به `BEEP/DELAY` قابل‌اجرای Pico تبدیل می‌شود و متن خطاهای واقعی نیز مستقیماً در پنجرهٔ Export نمایش داده می‌شود.
- **Hardware-passed Build 89:** بستهٔ E اتصال مجدد CDC ویندوز را تشخیص داد، Pico را یک‌بار Reset کرد، `CIRCUITPY` دوباره قابل‌نوشتن شد و Startup/Mouse ادامه یافت. نوت‌های مرحله‌ای پذیرفته‌شده نیز به Runtime استاندارد منتقل شدند. کالیبراسیون صدا هنوز تست سخت‌افزاری نشده است.
- **Hardware-passed Build 88:** بستهٔ تشخیصی A پس از Warm Restart بدون Start دستی زنده ماند و Startup را اجرا کرد. همین مسیر Marker + USB fusion اکنون مسیر استاندارد خروجی Classroom است.
- **Candidate Build 87:** USB DOWN/UP که حین انتهای Route After رخ می‌دهد دیگر پاک نمی‌شود؛ Startup پس از بازگشت Windows ادامه می‌یابد. صدای Save کالیبراسیون نیز به یک الگوی سه‌نتی واضح‌تر ارتقا یافت و نتیجهٔ Save در NVM Debug ثبت می‌شود.
- **Candidate Build 86:** صدای خطای کالیبراسیون برای Sample ناپایدار و فشار زرد هنگام Busy اضافه شد؛ بازهٔ کامل چرخه با پیش‌فرض ۱۱۰–۱۳۰ دقیقه به UI و Runtime برگشت؛ فایل شش‌پروفایلی به‌روز با Dashboard برابر `13.3 ± 3.0 lux` همیشه داخل بستهٔ Classroom قرار می‌گیرد.
- **Candidate Build 85:** کالیبراسیون فیزیکی نور دیگر به Revision خروجی وابسته نیست؛ Snapshot قدیمی CAL1 بازیابی/مهاجرت می‌شود و منبع مؤثر با `CALSTATUS source=nvm` قابل مشاهده است.
- **Candidate Build 84:** چرخهٔ زمان‌محور قدیمی حذف شد؛ پایان Game فوراً After را اجرا می‌کند، Marker پس از Restart تب Startup را یک‌بار اجرا می‌کند، Desktop رد می‌شود و مسیر از Login/DC ادامه می‌یابد. CIRCUITPY نیز دوباره در اختیار Windows است و کالیبراسیون فیزیکی در NVM کنترل‌شده ذخیره می‌شود.
- **Candidate Build 83:** Runtime مدرن اکنون `RUNFOR/AUTORESUME/POSTLAUNCH` را اجرا می‌کند؛ Deadline مسیر جاری را متوقف، Restart ویندوز را ارسال، Marker را در NVM نگه‌داری و پس از USB Down/Up و تأخیر تنظیم‌شده برنامهٔ Pin‌شده را اجرا می‌کند.
- **Candidate Build 82:** Tolerance کالیبراسیون نور اکنون فاصلهٔ هر سمت از Median را مستقل محاسبه می‌کند؛ نمونهٔ نامتقارن Dashboard دیگر بلافاصله پس از Save به `unknown` تبدیل نمی‌شود.
- **Candidate Build 81:** خروجی Classroom Studio اکنون با تست صریح بسته‌بندی کنترل می‌شود تا Runtime سازگار با ARM 2.8.2-S4 شامل شروع ASND، تشخیص/Timeout و Telemetry موازی باشد؛ این Build جایگزین Release قدیمی Build 98 می‌شود.
- **Candidate Build 80:** ARM 2.8.2-S4 با ADC آزادِ پس‌زمینه، Peak صدا را حین حرکت بدون قرار دادن `analogRead` در Cadence موس نگه می‌دارد؛ مقیاس ASND دوباره با SCAL یکسان است.
- **Candidate Build 79:** WSND اکنون Peak واقعی را در تشخیص/Timeout گزارش می‌کند و Timeout عادی دیگر Guard Failure نیست؛ مسیر موس و Cadence تغییر نکرده‌اند.

- **Candidate Build 78:** ARM 2.8.2-S2 مانع توقف ۴۰–۵۲ms ناشی از بازبودن COM بدون HELLO می‌شود؛ پایش صدا و Cadence یک‌میلی‌ثانیه‌ای حفظ شده‌اند.
- **Candidate Build 77:** ARM 2.8.2-S1 پایش صدا را از حلقهٔ Micro-step خارج می‌کند تا Cadence نرم 2.8.1 برگردد؛ Typo نیز دوباره فاصلهٔ کاراکتری واقعی است.
- **Baseline سخت‌افزاری:** Build 68 مسیرهای Desktop/Login/DC/Game را بدون MemoryError روی Pico اجرا کرد؛ حرکت Natural Mouse v1 حفظ شد.
- **مسئلهٔ باز Build 68:** برای جلوگیری از `ERR|BUSY`، Sound فقط هنگام توقف Mouse Poll می‌شد و واکنش F به صدای قلاب دیر می‌رسید.
- **راه‌حل Build 69:** ARM 2.8.2 صدا را درون حلقهٔ Mouse به‌صورت Async پایش می‌کند، حرکت را همان لحظه متوقف می‌کند و Pico کلید F را بدون انتظار برای پایان Mouse می‌زند.
- **اصل معماری:** Pico مسئول Keyboard/Guard/Route است؛ Pro Micro مسئول Mouse HID و پایش Sound هم‌زمان است.
- **کالیبراسیون Game:** در Export مدرن از پروفایل ذخیره‌شدهٔ فعلی Classroom استفاده می‌شود؛ مقدار ثابت Template دیگر منبع اجرا نیست.
- **Golden 100:** جدا و بدون تغییر باقی مانده است.

| Build | نتیجهٔ سخت‌افزاری | مسئله/تغییر اصلی | وضعیت |
| --- | --- | --- | --- |
| 118 | جایگزین حرفه‌ای Patchهای 115–117 | VM ترتیبی Iterative با Stack صریح + SoundWatch بدون Callback | Architectural candidate؛ hardware retest pending |
| 117 | Bundle 440: Bind پاس؛ peak=95 سپس شکست پیش از callback telemetry | حذف کامل callback/closure؛ Poll مستقیم Context و تحویل با `take_sound_watch()` | Local candidate؛ hardware retest pending |
| 116 | Bundle 420: Response bind پاس؛ peak=105 سپس شکست پیش از callback telemetry | Queue کاملاً بدون فراخوانی + اجرای Inline Runner مستقیم در Scheduler | Local candidate؛ hardware retest pending |
| 115 | Bundle 410: همهٔ Importها/RMOUSE/Sound detect پاس؛ MemoryError پیش از ورود به Response | Queue-only callback + Response runner ازپیش Bind/Cache و اجرای پس از unwind | Local candidate؛ hardware retest pending |
| 114 | Bundle 401: Runtime پس از افزودن Response با allocation=1336 شکست خورد | انتقال کامل Response به ماژول مستقل preloaded؛ موجودی ۴۲فایلی | Local candidate؛ response behavior unchanged |
| 113 | Build 112: Engine/Index/RMOUSE/Sound detect پاس؛ MemoryError پس از peak=101 | اجرای Flash-backed فایل Splash/Whisper بدون read()/splitlines()/tuple list | Local candidate؛ response behavior unchanged |
| 112 | Bundle 390: Parallel، Events و Core پاس؛ Runtime همچنان allocation=1180 | تقسیم تابع monolithic `_run` به Sound/Leaf/Container handlerهای bounded | Local candidate؛ behavior unchanged |
| 111 | Bundle 388: Index، Parallel و Core پاس؛ Runtime یک‌تکه با allocation=1180 شکست خورد | Split معماری Event generator و Runtime به دو واحد bounded و Preload زودهنگام Event | Local candidate؛ behavior unchanged |
| 110 | Bundle 381: Index رزرو شد؛ Parallel پس از Core/Runtime با allocation=1388 شکست خورد | جابه‌جایی Preload Scheduler به بلافاصله پس از Reserve و پیش از Core/Runtime | Local candidate؛ behavior unchanged |
| 109 | Bundle 380: Parallel preload پاس؛ ساخت Index پس از Compile با allocation=1336 شکست خورد | رزرو Offset bytearray پیش از Importهای Game و تحویل بدون Allocation مجدد | Local candidate؛ runtime behavior unchanged |
| 108 | Bundle 371: انتقال‌ها و file-index پاس؛ Compile دیرهنگام Parallel با free=45952 و allocation=1388 شکست خورد | Preload مشروط Scheduler پیش از File index و Prelude روی Heap تازه | Local candidate؛ scheduler/mouse/ARM unchanged |
| 107 | Bundle 355: SoundWatch arm و همهٔ Lazy importها پاس؛ اولین RMOUSE داخل LOOP/RPKG با `pystack exhausted` متوقف شد | تبدیل Generator بازگشتی Containerهای Game به Stack Iterative | Local candidate؛ ARM/Mouse path unchanged |
| 106 | Bundle 350: تمام انتقال‌ها، Login Type، Game index و Parallel import پاس؛ نخستین WPROFILE با `pystack exhausted` متوقف شد | حذف Poll تو‌در‌توی callback از scoped waiter؛ `sleep_ms` تنها مالک سرویس SoundWatch | Local candidate؛ ARM/Mouse unchanged |
| 105 | Bundle 340: Mouse و Type import پاس؛ اولین Typo با `_QWERTY_ROWS` NameError متوقف شد | بازیابی جدول QWERTY در ماژول Split Type و تست Neighbor واقعی | Local candidate؛ Mouse/ARM unchanged |
| 104 | Bundle 330: Game/Core/Runtime/File-index/Parallel پاس؛ اولین Mouse import با free=42912 و allocation=896 شکست خورد | تقسیم Login به Facade 2.3KB، Core 2.7KB، Mouse 4.6KB و Type 5.5KB | CI candidate؛ hardware retest pending |
| 103 | Bundle 320: Route read پاس؛ `before-engine-import=60144` سپس import یک‌تکه با free=444 شکست خورد | Facade 1KB + Core 6.7KB + Runtime 10KB + Parallel 6.8KB با Import ترتیبی | CI candidate؛ hardware retest pending |
| 102 | Bundle 310: Preemption و خواندن فایل پاس؛ شکست پیش از `file-index` با allocation=1386 | حذف Import تو‌در‌توی Login و Lazy-load موس در اولین RMOUSE با تله‌متری Heap | CI candidate؛ Game hardware retest pending |
| 101 | Bundle 308: Preemption تمام انتقال‌ها را پاس کرد؛ Game پس از Parse ۳۶۹ فرمان با Heap حدود 40KB شکست خورد | فرمان‌های فایل‌محور با Offset فشرده و Reservoir Sampling برای RPKG بزرگ | CI candidate؛ Game hardware retest pending |
| 100 | Login پس از ورود به Dashboard ادامه می‌یافت؛ Game هنگام read با allocation=6400 شکست خورد | Stable-light route preemption + streaming Game route read | CI candidate؛ hardware transition retest pending |
| 99 | Bundle 304 فایل 15.3±3 داشت ولی NVM قدیمی 14.2±1 اعمال شد؛ Save دوم پس از complete-stage متوقف ماند | Revision-authoritative Package و Fit ازقبل‌بارگذاری‌شده بدون Import لحظه‌ای | CI candidate؛ hardware retest pending |
| 98 | Bundle 303 در Import با تخصیص 1244 بایت شکست خورد | انتقال Fit به ماژول Lazy؛ Boot runtime زیر 40KB و Manifest 35 فایلی | CI candidate؛ hardware boot pending |
| 97 | تست کالیبراسیون هم‌پوشان لازم است | Fit یک‌طرفه/دوطرفهٔ اتمیک با Center ثابت، Min=0.5 و Gap=0.25 | CI candidate؛ Mouse/ARM unchanged |
| 96 | تست Dashboard و تکرار Stop/Start لازم است | رفع suppression در unknown→unknown و افزایش کنترل‌شدهٔ حاشیه Drift کالیبراسیون | CI candidate؛ Mouse/ARM unchanged |
| 95 | تست Export و Migration پروژهٔ ماهیگیری لازم است | حذف Wait For Sound از منو، Splash Listener اختصاصی و انتقال Timeout به پروفایل Splash | Local candidate؛ Runtime/ARM/Mouse unchanged |
| 94 | تست Boot و اجرای Game لازم است | پذیرش دو Route جدید Whisper/Splash در موجودی ۳۴فایلی Boot verifier | Local candidate؛ Mouse/ARM unchanged |
| 93 | تست سخت‌افزاری صدا، Heap و نرمی موس لازم است | Whisper سراسری Game؛ Splash محدود به Cast با Timeout تصادفی ۱۸–۲۲ ثانیه و رفتن به Cast بعدی بدون ریست Deadlineها | CI candidate؛ Hardware pending |
| 92 | تست سخت‌افزاری صدا لازم است | شنوندهٔ سراسری Game، بازه/اولویت، تب‌های Whisper و Splash و بازگشت به همان نقطه | CI candidate؛ Sound pending |
| 91 | Build 143: Runtime با `only one WSND listener is allowed` متوقف شد | Listener مشترک ADC و انتخاب پروفایل با Peak | CI candidate؛ Sound pending |
| 90 | `s1.amsj`: Export با ۲ خطا Block شد | پشتیبانی Buzzer داخل ForLoop شاخه‌های Parallel و نمایش جزئیات خطا | CI candidate؛ Sound pending |
| 89 | بستهٔ E پاس: Startup، Mouse، نوت‌ها و نوشتن/حذف TEST.txt؛ Sound calibration تست نشده | Reset یک‌بارهٔ Pico پس از CDC reconnect برای Remount قابل‌نوشتن | Hardware pass؛ Sound pending |
| 88 | بستهٔ A پاس: Restart، Resume خودکار و اجرای Startup بدون Start دستی | Resume با Marker معتبر حتی وقتی Windows هیچ USB DOWN گزارش نمی‌کند | Hardware pass |
| 87 | Build 124: Restart انجام شد ولی Startup خودکار اجرا نشد؛ Tone ذخیره شنیده نشد | حفظ USB transition حین After و تقویت/ثبت Tone ذخیره | CI candidate |
| 86 | تست سخت‌افزاری لازم است | بازخورد صوتی Fail کالیبراسیون، بازهٔ ۱۱۰–۱۳۰ دقیقه و پروفایل نور همراه بسته | CI candidate |
| 85 | تست سخت‌افزاری لازم است | ماندگاری کالیبراسیون فیزیکی بین Exportها و Telemetry منبع NVM | CI candidate |
| 84 | تست سخت‌افزاری لازم است | After/Startup مستقل، حذف تایمرهای قدیمی، Desktop skip و NVM calibration | CI candidate |
| 83 | تست سخت‌افزاری لازم است | اجرای واقعی Restart Cycle، NVM Marker، HOSTUSB و Auto Resume | CI candidate |
| 82 | Dashboard با center=13.3، spread=2.5 و live=15.8 به unknown رفت | محاسبهٔ دامنهٔ نامتقارن P5/P95 نسبت به Median | CI candidate |
| 81 | S4 + Bundle 203 دستی Catch را پاس کرد | انتشار Classroom با Runtime داخلی سازگار با S4 | CI candidate |
| 80 | S4 + Bundle 203 دستی Catch را پاس کرد | بازیابی شنیدن پیوسته بدون شکستن نرمی موس | Hardware pass |
| 79 | تست سخت‌افزاری لازم است | Peak telemetry برای WSND و Timeout غیرخطایی | CI candidate |
| 78 | Build 95: میانهٔ 51ms و 1,194 وقفهٔ حداقل 40ms | USB handshake فقط با بایت واقعی؛ حذف stall هنگام بازبودن COM | Local candidate |
| 77 | Build 94: حرکت پس از ARM 2.8.2 شکسته و Typo 7–12 تقریباً روی هر حرف اجرا شد | بازیابی Cadence 2.8.1 با Sound بین فرمان‌ها؛ Typo با فاصلهٔ کاراکتری | Local candidate |
| 76 | Bundle 175: دو Guard JSON با Debug/FAT cross-link خراب شدند | حذف Debug file write، پاسخ سریع دکمه و Read-back کامل Export | Local candidate |
| 75 | Build 74: Game ابتدا اجرا شد؛ بازگشت بعد از Lux spike شکست خورد | Game re-entry، کالیبراسیون مقاوم Game/Target و بازیابی کامل Bridge | Local candidate |
| 74 | تست سخت‌افزاری لازم است | کالیبراسیون پرتابل دو Step صوتی با GP3/GP4 و Binding Hash | Local candidate |
| 73 | تست سخت‌افزاری لازم است | انتقال پروفایل‌های نور فعلی Classroom به Pico | CI candidate |
| 72 | تست سخت‌افزاری لازم است | Proxy صدا از Pico به Pro Micro و حذف نویز Cursor | CI candidate |
| 71 | تست سخت‌افزاری لازم است | رفع اتصال سبز کاذب و کالیبراسیون صوتی روی Bridge قطع‌شده | CI candidate |
| 70 | تست سخت‌افزاری لازم است | بازیابی امن LABEL/GOTO و Light Watch | CI candidate |
| 69 | تست سخت‌افزاری حرکت بعداً Regression نشان داد | پایش Async صدا داخل Micro-step و توقف فوری Mouse پیش از F | Superseded by 77 |
| 68 | Desktop/Login/DC/Game پاس | Runner سبک Game؛ تأخیر Sound هنگام حرکت | Hardware pass؛ Sound superseded |
| 67 | Desktop و Login/DC سبک پاس؛ Calibration ذخیره شد | Runner سبک Streaming برای Login/DC | Hardware pass؛ Game superseded |
| 66 | Desktop و Natural Mouse پاس؛ Login/DC MemoryError | Natural Mouse v1 روی Baseline 50 | Mouse-stable baseline |
| 49 | Route تست A کامل شد | Runner سبک RMOUSE بدون Executor کامل | Functional pass؛ کیفیت حرکت در حال تیون |
| 48 | Import و Parse موفق؛ اجرا شکست خورد | Lazy import Parser/Executor | Superseded by 49 |
| 47 | تست A در Import شکست خورد | Fishing timeout + cadence 128-point | Superseded by 48/49 |
| 46 | Login RMOUSE و Stop تأیید شدند | Streaming RMOUSE عادی | Verified foundation |
| 45 | بازیابی Heap و حذف SCAL وسط حرکت | Stop cleanup | Verified foundation |
| 43 | Pause release، SCAL BUSY و sampled speed | Timing/UART fixes | Superseded |
| 41 | Parallel memory و پروفایل‌های نور | Streaming parallel RMOUSE | Superseded |
| 40 | Retry کالیبراسیون overlap | Calibration UX | Verified |
| 39 | Facade صحیح در Export پروژهٔ جاری | Export ordering | Verified foundation |
| 38 | Split executor اولیه | کاهش فشار Import | Superseded by 39 |

## Build 118 — VM تکرارشوندهٔ Game و حذف ریشه‌ای Stack تو‌در‌تو

**Previous build:** 116 / Classroom release 215
**Status:** architectural candidate; hardware retest required

### Problem observed

سه Build پیاپی محل شکست را دقیق‌تر کردند، اما Patch کردن مرز Callback کافی نبود.
Bundle 440 پس از تمام Importها و Bind، Sound را با `peak=95` تشخیص داد و هنوز
پیش از نخستین Response telemetry با `MemoryError('')` متوقف شد.

### Root cause

مسئلهٔ ریشه‌ای Callback نبود؛ مفسر ترتیبی Game برای `LOOPTIME` و `RPKG` تابع
`_run` را بازگشتی فراخوانی می‌کرد و سپس `PGROUP → scheduler → sleep → sound`
روی همان pystack محدود CircuitPython قرار می‌گرفت. حذف چند Frame فقط آستانه را
جابه‌جا می‌کرد و معماری همچنان شکننده بود.

### Change

- پیمایش ترتیبی Route به VM تکرارشوندهٔ `Cursor` منتقل شد.
- `LOOP`، `LOOPTIME` و `RPKG` با Stack صریح داده‌ای اجرا می‌شوند.
- هیچ فراخوانی بازگشتی `_run(commands, ...)` باقی نمانده است.
- Cursor قابل Resume است و PGROUP را با Range دقیق به Scheduler تحویل می‌دهد.
- GOTO با پاک‌کردن Frameهای Container و Jump روی Root range اجرا می‌شود.
- SoundWatch Callback/Closure ندارد؛ Context مستقیماً Poll و Winner را نگه می‌دارد.
- Response پس از Unwind Scheduler و روی Stack تخت با Runner ازپیش Bindشده اجرا می‌شود.
- File-backed Response، Scoped Splash، Cooldown و ادامهٔ Whisper حفظ شده‌اند.
- Mouse، ARM، DDA، Cadence، Guard و Exporter تغییر نکرده‌اند.

### Validation

- قرارداد صریح عدم Recursion در `_run` و وجود `Cursor` اضافه شد.
- Code Objectهای `Cursor.next`، `Cursor._resume` و Handlerهای Runtime زیر ۱۰۰۰ بایت قفل شدند.
- Route واقعی شامل LOOPTIME → PGROUP → LOOP → RPKG در تست‌های Portable اجرا می‌شود.
- همهٔ Hashها، Compile، Security و Windows TestRunner باید پیش از Merge پاس شوند.

### Next test

پس از Sound detection باید Response telemetry و F اجرا شوند. علاوه بر آن Route
باید حداقل چند Cast ادامه پیدا کند تا Resume شدن Cursor، Timeout و Loop ده‌دقیقه‌ای
بدون MemoryError یا تغییر Cadence تأیید شوند.

## Build 117 — حذف کامل Callback پایتونی SoundWatch

**Previous build:** 116 / Classroom release 215
**Status:** local candidate; hardware retest required

### Problem observed

Bundle 440 و هر ۴۲ Hash آن سالم بودند. تمام Importها، Index، Parallel، Mouse و
Bind زودهنگام Response پاس شدند. SoundWatch صدای واقعی را با `peak=95` تشخیص
داد، اما همچنان پیش از `before-response-callback` با `MemoryError('')` متوقف شد.

### Root cause

Build 116 عملیات داخل Callback را به یک Assignment کاهش داد، اما خود فراخوانی
Closure پایتونی از `PlanContext.sleep_ms()` هنوز یک Frame اضافی روی pystack
فعال `LOOP/RPKG/PGROUP` می‌ساخت. شکست پیش از Telemetry ثابت کرد Response،
File parser و Scheduler inline هنوز وارد اجرا نشده‌اند.

### Change

- Callback و Closure پایتونی SoundWatch به‌طور کامل حذف شدند.
- `PlanContext.sleep_ms()` مستقیماً `poll_sound_watch()` را فراخوانی می‌کند.
- Winner داخل Slot داخلی `_sound_watch_pending` نگه‌داری می‌شود.
- Scheduler پس از بازگشت Sleep با `take_sound_watch()` Winner را تحویل می‌گیرد.
- هنگام Pending شدن Winner، Sleep زودتر برمی‌گردد تا Response بدون تأخیر سرویس شود.
- File-backed Response، Scoped Splash، Cooldown و Whisper continuation حفظ شده‌اند.
- Mouse، ARM، DDA، Cadence، Guard و Exporter تغییر نکرده‌اند.

### Validation

- Bundle 440: ۴۲ از ۴۲ Hash معتبر.
- قرارداد Runtime نبود کامل `_sound_watch_callback` و `_queue_sound_watch` را قفل می‌کند.
- تست SoundWatch تضمین می‌کند Response داخل polling Sleep اجرا نمی‌شود.
- همهٔ قراردادهای Portable، Hash، Compile و Security باید پیش از Merge پاس شوند.

### Next test

پس از تشخیص صدا باید برای نخستین بار `before-response-callback` دیده شود و سپس
`before-response-file`، `after-response-index`، اجرای F و
`after-response-callback` ثبت شوند. MemoryError نباید تکرار شود.

## Build 116 — Callback کاملاً بدون فراخوانی و Runner مستقیم

**Previous build:** 115 / Classroom release 214
**Status:** local candidate; hardware retest required

### Problem observed

Bundle 420 و هر ۴۲ Hash آن سالم بودند. تمام Importها، Index، Parallel، Mouse و
`before/after-response-bind` پاس شدند. SoundWatch صدای واقعی را با `peak=105`
تشخیص داد، اما پیش از نخستین `before-response-callback` با `MemoryError('')`
متوقف شد.

### Root cause

Build 115 اجرای کامل Response را از callback خارج کرد، ولی callback هنوز
`suspend_sound_watch()` را فراخوانی می‌کرد و Scheduler نیز برای سرویس Winner
وارد Wrapper جداگانه می‌شد. روی pystack محدود CircuitPython، شکست پیش از اولین
دستور Wrapper نشان داد حتی این Call boundary باقی‌مانده در Stack فعال
`LOOP/RPKG/PGROUP/sleep_ms` کافی است.

### Change

- callback فقط Slot ازقبل‌موجود `_response` را بررسی و Winner را در آن قرار می‌دهد.
- هیچ Suspend، UART، Emit یا Response call داخل callback انجام نمی‌شود.
- Scheduler پس از بازگشت callback، Suspend و Telemetry را Inline اجرا می‌کند.
- Runner ازپیش Bindشده مستقیماً فراخوانی می‌شود؛ Wrapper میانی حذف شده است.
- Scoped Splash، Cooldown، File-backed Response و رفتار ادامهٔ Whisper حفظ شده‌اند.
- Mouse، ARM، DDA، Cadence، Guard و Exporter تغییر نکرده‌اند.

### Validation

- Bundle 420: ۴۲ از ۴۲ Hash معتبر و ۳۶۹ فرمان Game.
- تست Callback تضمین می‌کند `suspend_sound_watch` داخل Queue function وجود ندارد.
- قرارداد Scheduler فراخوانی مستقیم `response_runner(..., execute)` را قفل می‌کند.
- همهٔ قراردادهای Portable، Hash، Compile و Security باید پیش از Merge پاس شوند.

### Next test

پس از تشخیص صدای واقعی باید `before-response-callback`، سپس
`before-response-file` و `after-response-index` ثبت شوند، کلید F اجرا شود و
`after-response-callback` دیده شود. `MemoryError` و `pystack exhausted` نباید
تکرار شوند.

## Build 114 — جداسازی کامل ماژول Flash Response

**Previous build:** 113 / Classroom release 212
**Status:** local candidate; hardware retest required

### Problem observed

Bundle 401 و هر ۴۱ Hash آن سالم بودند، اما اضافه‌شدن منطق Flash-backed Response
به فایل Runtime اندازهٔ Windows آن را به حدود 8.6KB رساند. Runtime پیش از اجرا
در `allocating 1336 bytes` شکست خورد؛ بنابراین Build 113 مسیر Sound را اصلاً
آزمایش نکرد.

### Root cause

اصلاح Response درست بود، اما محل قرارگیری آن اشتباه بود. Parser و Runner پاسخ
به همان Compiler unit حساس Runtime اضافه شدند و Code Object/constant peak تازه
ایجاد کردند.

### Change

- Parser و Runner پاسخ به `plan_engine_game_response.py` مستقل منتقل شدند.
- ماژول Response همراه Parallel و Events پیش از Core/Runtime Preload می‌شود.
- Runtime فقط Wrapper کوچک `_run_response` را نگه می‌دارد.
- موجودی Classroom، Manifest، Boot verifier و Heap cleanup به ۴۲ فایل رسید.
- اجرای Flash-backed و بستن Handle در `finally` بدون تغییر حفظ شده است.

### Validation

- Runtime زیر 8KB و Response زیر 4KB با قرارداد Windows قفل می‌شوند.
- ترتیب Reserve → Parallel → Events → Response → Core → Runtime تست می‌شود.
- تست سخت‌افزار-مانند همچنان عدم فراخوانی `read_plan_file` را تضمین می‌کند.

### Next test

در Game باید `after-response-preload` پیش از `before-core-import` دیده شود، سپس
`after-runtime-import` و `file-index` پاس شوند. پس از تشخیص صدا، Response باید
مستقیم از Flash اجرا و F ارسال شود.

## Build 113 — اجرای Flash-backed پاسخ Splash/Whisper

**Previous build:** 112 / Classroom release 211
**Status:** local candidate; hardware retest required

### Problem observed

Build 112 تمام مرزهای Compiler را پاس کرد، Index با ۳۶۹ فرمان ساخته شد،
Parallel و Mouse اجرا شدند و SoundWatch با `peak=101` صدای واقعی Splash را
تشخیص داد. MemoryError بدون اندازه بلافاصله هنگام شروع Response رخ داد.

### Root cause

`_response_commands` از `ctx.read_plan_file(name)` استفاده می‌کرد؛ در نتیجه
فایل Response ابتدا کامل در یک String و سپس دوباره به List از Tupleها تبدیل
می‌شد. این تخصیص هم‌زمان پس از اجرای RMOUSE/Sound روی Heap Fragmented انجام
می‌شد، هرچند فایل Splash فقط 251 بایت بود.

### Change

- روی Pico، پاسخ Splash/Whisper با `_FileCommands` مستقیم از Flash اجرا می‌شود.
- فقط Offsetهای فشرده نگه‌داری و فایل در `finally` بسته می‌شود.
- مسیر مجازی `read_plan_file` فقط برای Host simulation باقی مانده است.
- تله‌متری `before-response-file` و `after-response-index|commands=N` اضافه شد.
- Keyboard response، Delay، Loop/RPKG، SoundWatch، Mouse و ARM تغییر نکرده‌اند.

### Validation

- تست سخت‌افزار-مانند تضمین می‌کند Response بدون `read_plan_file` اجرا و Handle
  فایل بسته می‌شود.
- سقف Code Objectهای Build 112 و موجودی ۴۱فایلی حفظ می‌شوند.

### Next test

پس از `SOUND|result=detected` باید `before-response-file`،
`after-response-index|commands=5`، `sound response start` و
`sound response done` ثبت شوند. کلید F باید اجرا و Cast بعدی شروع شود.

## Build 112 — تقسیم Code Object تابع Dispatch Game

**Previous build:** 111 / Classroom release 210
**Status:** local candidate; hardware retest required

### Problem observed

Bundle 390 و هر ۴۱ Hash آن سالم بودند. Parallel، Event module و Core همگی
Compile شدند، اما Runtime با وجود کاهش فایل به حدود 7.3KB دوباره دقیقاً در
`allocating 1180 bytes` شکست خورد.

### Root cause

ثابت‌ماندن اندازهٔ allocation پس از Split فایل نشان داد بلوک 1,180 بایتی
مربوط به Code Object تابع monolithic `_run` است. این تابع Leafها، Sound و
Containerها را در یک Dispatch بزرگ کامپایل می‌کرد.

### Change

- منطق Sound به `_sound` منتقل شد.
- فرمان‌های Leaf شامل Keyboard، Delay، RMOUSE و BEEP در `_leaf` قرار گرفتند.
- `_run` فقط کنترل Containerهای RPKG/LOOP/PGROUP و LABEL/GOTO را نگه می‌دارد.
- هر سه Code Object با تست اندازهٔ مستقل زیر سقف bounded قفل شده‌اند.
- ترتیب اجرا، Random Package، Deadline، SoundWatch، Splash، Natural Mouse،
  ARM، DDA و Cadence تغییر نکرده‌اند.

### Validation

- تست مستقیم اندازهٔ Bytecode برای `_sound`، `_leaf` و `_run` اضافه شد.
- ۴۱ فایل Manifest و Split معماری Build 111 بدون تغییر حفظ شدند.

### Next test

پس از `after-core-import` باید `after-runtime-import` و سپس `file-index` ثبت
شوند. بعد Prelude و اولین RMOUSE باید اجرا شوند؛ سپس Splash واقعی و Timeout
Cast بررسی شوند.

## Build 111 — Split معماری Game Runtime و Event generator

**Previous build:** 110 / Classroom release 209
**Status:** local candidate; hardware retest required

### Problem observed

Bundle 388 و هر ۴۰ Hash آن سالم بودند. Index رزرو شد، Parallel با موفقیت
Preload شد و Core نیز کامل Import شد. سپس ماژول یک‌تکهٔ Runtime با اندازهٔ
Windows حدود 11.1KB و `free=48992` در تخصیص پیوستهٔ 1,180 بایت شکست خورد.

### Root cause

جابه‌جایی‌های Buildهای 109 و 110 هر دو تخصیص هدف را رفع کردند، اما مجموع کد
Generator تکرارشوندهٔ Event و Executor اصلی هنوز در یک واحد Compiler قرار داشت.
این آخرین Compiler peak بزرگ Game بود و دیگر با تغییر ترتیب پایدار نمی‌شد.

### Change

- Generator تکرارشوندهٔ LOOP/LOOPTIME/RPKG/RMOUSE به
  `plan_engine_game_events.py` مستقل منتقل شد.
- Event module و Parallel scheduler پیش از Core/Runtime و هرکدام میان دو GC
  Preload می‌شوند.
- Runtime اصلی زیر 8KB و Event module زیر 6KB قفل شده‌اند.
- Generator همچنان Iterative است؛ ترتیب Package، Deadline، Sound، Mouse و
  پاسخ Splash بدون تغییر مانده‌اند.
- موجودی Classroom، Manifest، Boot verifier و Heap cleanup با فایل ۴۱ام
  همگام شدند.

### Validation

- قرارداد اندازهٔ Windows برای هر دو واحد جدید اعمال می‌شود.
- تست فایل‌محور ترتیب Reserve → Parallel → Events → Core → Runtime را قفل می‌کند.
- تست Nested LOOP/RPKG/RMOUSE همچنان نبود recursion را کنترل می‌کند.

### Next test

در Game باید `after-parallel-preload`، سپس `before/after-events-preload`،
`before/after-core-import`، `after-runtime-import` و `file-index` ثبت شوند.
پس از آن Prelude، اولین RMOUSE، Splash واقعی و Timeout Cast بررسی شوند.

## Build 110 — Preload Scheduler پیش از Core/Runtime

**Previous build:** 109 / Classroom release 208
**Status:** local candidate; hardware retest required

### Problem observed

Bundle 381 و هر ۴۰ Hash آن سالم بودند. رزرو Index کامل شد:
`after-file-index-reserve|commands=369|offset-bytes=1476|free=55600`.
سپس Core و Runtime بارگذاری شدند و Heap به 46,768 بایت رسید؛ Compile
Scheduler در همان نقطه برای بلوک پیوستهٔ 1,388 بایت شکست خورد.

### Root cause

Build 109 تخصیص Index را زودهنگام کرد، اما Parallel را همچنان پس از دو Compile
دیگر بارگذاری می‌کرد. Index رزروشده مشکل مستقلی نداشت؛ ترتیب Importها آخرین
بلوک پیوستهٔ لازم برای Compiler Parallel را پیش از Preload خرد می‌کرد.

### Change

- ترتیب Game اکنون Reserve Index → Parallel preload → Core → Runtime → اجرا است.
- Parallel روی Heap با حدود 55.6KB آزاد Compile می‌شود، نه پس از افت به 46.8KB.
- همان Bytearray رزروشده بدون Scan یا Allocation دوباره به FileCommands می‌رسد.
- Scheduler، SoundWatch، Natural Mouse، ARM، DDA و Cadence تغییر نکرده‌اند.

### Validation

- تست فایل‌محور ترتیب دقیق Reserve → Parallel → Core → Runtime را قفل می‌کند.
- قراردادهای File index، Nested LOOP/RPKG/RMOUSE و Hashهای Bundle حفظ می‌شوند.

### Next test

Bundle را با Release بعدی کامل بازسازی کنید. در Game باید
`after-file-index-reserve` سپس `before/after-parallel-preload` و بعد
`before/after-core-import` و `after-runtime-import` ثبت شوند. پس از `file-index`
باید Prelude و اولین RMOUSE اجرا شوند؛ سپس Splash واقعی و Timeout بررسی شوند.

## Build 109 — رزرو File Index پیش از Importهای Game

**Previous build:** 108 / Classroom release 207
**Status:** local candidate; index-reserve hardware retest required

### Problem observed

Bundle 380 تله‌متری موفق
`before-parallel-preload=48816 → after-parallel-preload=45968` را ثبت کرد؛
بنابراین اصلاح Build 108 پاس شد. بلافاصله بعد از Preload، ساخت Bytearray
فشردهٔ Offsetهای ۳۶۹ فرمان به بلوک پیوستهٔ 1336 بایت نیاز داشت و با
`MemoryError` متوقف شد.

### Root cause

Index فایل پس از Compile ماژول‌های Game/Core/Runtime/Parallel ساخته می‌شد.
با وجود حافظهٔ آزاد کافی، Heap پس از Compiler بلوک پیوستهٔ لازم برای رشد
Bytearray را نداشت.

### Change

- Route پیش از هر Import Game یک بار اسکن و Offset چهاربایتی هر فرمان در
  Bytearray نهایی رزرو می‌شود.
- همان Scan وجود `PGROUP` را نیز مشخص می‌کند.
- Buffer آماده پس از Import و Preload مستقیماً به `_FileCommands` منتقل
  می‌شود؛ فایل دوباره Scan و Index دوباره Allocate نمی‌شود.
- تله‌متری `before/after-file-index-reserve` تعداد فرمان و اندازهٔ Offset را
  ثبت می‌کند.
- Scheduler، SoundWatch، Natural Mouse، ARM، DDA و Cadence تغییر نکرده‌اند.

### Validation

- تست فایل‌محور هویت همان Bytearray رزروشده را در `_FileCommands` کنترل
  می‌کند.
- تست PGROUP ترتیب Reserve → Engine load → Parallel preload → اجرا را پوشش
  می‌دهد.

### Next test

با Build 109 Bundle را کامل بازسازی کنید. در Game باید ابتدا
`after-file-index-reserve|commands=369|offset-bytes=1476`، سپس
`after-runtime-import` و `after-parallel-preload` ثبت شوند. خطای allocation
1336 نباید تکرار شود و Route باید وارد اولین RMOUSE شود. سپس Splash واقعی،
Timeout و نرمی موس بررسی شوند.

## Build 108 — پیش‌بارگذاری Parallel روی Heap تازه

**Previous build:** 107 / Classroom release 206
**Status:** local candidate; Parallel preload hardware retest required

### Problem observed

Bundle 371 مسیر کامل تا Game، Runtime و ایندکس ۳۶۹ فرمان را پاس کرد. پس از
حدود ۲۱ ثانیه Prelude، مقدار `before-parallel-import=45952` بود، اما Compile
ماژول Parallel Heap را Fragment کرد و تخصیص پیوستهٔ 1388 بایت با
`parallel-import-memoryerror` شکست خورد.

### Root cause

مجموع حافظه کافی بود، ولی Scheduler درست در لحظهٔ رسیدن به PGROUP و پس از
Delay/Packageهای متعدد برای اولین بار Compile می‌شد. تخصیص‌های موقت Compiler
به بلوک پیوسته‌ای بزرگ‌تر از موجودی Fragment‌شده نیاز داشتند.

### Change

- فایل Game پیش از بارگذاری Runtime فقط برای وجود `PGROUP` اسکن می‌شود.
- در Routeهای دارای Parallel، همان `plan_engine_game_parallel` موجود بلافاصله
  پس از Game Runtime و پیش از File index/Prelude روی Heap تازه Compile می‌شود.
- هنگام رسیدن واقعی به PGROUP، Import از Cache انجام می‌شود.
- Scheduler، SoundWatch، Natural Mouse، Firmware ARM، DDA و Cadence تغییر
  نکرده‌اند.
- تله‌متری `before/after-parallel-preload` و تست فایل‌محور اضافه شد.

### Validation

- تست فایل‌محور وجود PGROUP را تشخیص می‌دهد، Preload را ثبت می‌کند و اجرای
  Parallel را کامل می‌کند.
- تست Nested Container/RMOUSE و همهٔ قراردادهای قبلی باید سبز بمانند.

### Next test

با Build 108 Bundle را کامل بازسازی کنید. در Game باید
`before-parallel-preload` و `after-parallel-preload` پیش از `file-index`
ثبت شوند. رسیدن بعدی به PGROUP نباید `MemoryError` بدهد؛ سپس اولین RMOUSE،
Splash واقعی، Timeout هجده تا بیست‌ودو ثانیه‌ای و نرمی موس بررسی شوند.

## Build 107 — Stack تکرارشوندهٔ Containerهای Game

**Previous build:** 106 / Classroom release 205
**Status:** local candidate; first fishing RMOUSE hardware retest required

### Problem observed

Bundle 355 ثابت کرد Build 106 از `SOUNDWATCH|armed` عبور می‌کند. سپس
`plan_engine_login`, Core و Mouse Runtime نیز با موفقیت و با حدود 36KB Heap
آزاد بارگذاری شدند، اما هنگام تولید اولین RMOUSE داخل شاخهٔ ماهیگیری با
`RuntimeError('pystack exhausted')` متوقف شد.

### Root cause

مفسر رویدادهای Parallel برای هر `LOOP` و `RPKG` یک Generator `_events`
جدید را به‌صورت بازگشتی ایجاد می‌کرد. مسیر واقعی
`PGROUP → LOOP → RPKG → RMOUSE → mouse_events` چند Generator فعال را پیش از
ورود Planner موس روی pystack محدود CircuitPython نگه می‌داشت. Heap و Importها
سالم بودند.

### Change

- پیمایش `LOOP/LOOPTIME/RPKG` در `_events` با Frameهای کوچک روی یک Stack صریح
  انجام می‌شود و دیگر `_events` خودش را فراخوانی نمی‌کند.
- ترتیب Random Package، تعداد Loop، Deadline حلقهٔ زمانی و Streaming رویدادها
  حفظ شده‌اند.
- Planner موس، Relative HID، Firmware ARM، DDA، Cadence و Humanization تغییر
  نکرده‌اند.
- تست جدید دو دور `LOOP` و Package ترتیبی شامل RMOUSE را اجرا و نبود فراخوانی
  بازگشتی `_events` را قفل می‌کند.

### Validation

- تست فایل‌محور Game، Reservoir Sampling و ترتیب رویدادهای Nested Container
  باید پاس شوند.
- مجموعهٔ کامل Portable و Manifest مدرن پس از تغییر کنترل می‌شوند.

### Next test

با Build 107 Bundle را کامل بازسازی و همان پروژه را اجرا کنید. بعد از
`after-mouse-runtime-import` باید اولین RMOUSE اجرا شود و
`pystack exhausted` رخ ندهد. سپس Timeout هجده تا بیست‌ودو ثانیه‌ای Splash،
صدای واقعی Splash و ادامهٔ Cast بعدی بررسی شوند. نرمی موس باید بدون تغییر
باقی بماند.

## Build 106 — حذف بازگشت تو‌در‌توی SoundWatch از WPROFILE

**Previous build:** 105 / Classroom release 204
**Status:** local candidate; scoped Splash hardware retest required

### Problem observed

Bundle 350 مسیر کامل Desktop، Login/DC، Character Dashboard و Loading را بدون
`MemoryError` طی کرد. Game نیز Core/Runtime/File-index و Parallel را با
`GAME|after-parallel-import|free=43856` بارگذاری کرد، اما بلافاصله پس از
`SOUNDWATCH|armed` با `RuntimeError('pystack exhausted')` متوقف شد.

### Root cause

در حالت scoped، `poll_profile_wait()` callback عمومی Game را مستقیماً اجرا
می‌کرد؛ همان callback از `sleep_ms()` تعاونی نیز سرویس می‌شد. این مسیر درون
`PGROUP → LOOP → RPKG → WPROFILE` فریم‌های Python را تو‌در‌تو می‌کرد. خطا از
Python stack بود، نه Heap و نه Firmware Pro Micro.

### Change

- `poll_profile_wait()` فقط نتیجهٔ scoped را می‌خواند.
- Poll کردن callback منحصراً در `sleep_ms()` باقی ماند تا در Deadlineهای
  Scheduler به‌صورت تخت و تعاونی انجام شود.
- تست قراردادی مانع بازگشت callback مستقیم به scoped waiter می‌شود.
- Firmware ARM، مسیر Relative Mouse، DDA، Cadence و Humanization تغییر نکردند.

### Validation

- تست scoped Splash باید همچنان Whisper global، Splash response، Timeout و
  Next-cast را پوشش دهد.
- Manifest Runtime پس از تغییر بازسازی و کامل کنترل می‌شود.

### Next test

با Build 106 همان پروژه و Bundle را کامل بازسازی کنید. در Game باید پس از
`SOUNDWATCH|armed` اجرای Parallel ادامه یابد و `pystack exhausted` رخ ندهد.
یک Splash واقعی باید `splash_steps.txt` را اجرا کند؛ Timeout ۱۸–۲۲ ثانیه‌ای
باید Cast جاری را تمام و Cast بعدی را آغاز کند. نرمی موس نیز باید بدون تغییر
باقی بماند.

## Build 105 — بازیابی جدول QWERTY در Human Type Split

**Previous build:** 104 / Classroom release 202
**Status:** local candidate; Login Typo hardware retest required

### Problem observed

Bundle 340 تقسیم حافظهٔ Build 104 را تأیید کرد:
`LOGIN|after-mouse-runtime-import|free=53648` و
`LOGIN|after-type-import|free=50704` ثبت شدند. بااین‌حال اولین TYPE دارای Typo با
`NameError: name '_QWERTY_ROWS' isn't defined` متوقف شد.

### Root cause

تابع `_qwerty_neighbor` به فایل Split جدید `plan_engine_login_type.py` منتقل شده
بود، اما ثابت داده‌ای `_QWERTY_ROWS` همراه آن منتقل نشده بود. تست Build 104 فقط
Lazy import ماژول Type و وجود توابع را کنترل می‌کرد و تابع Neighbor را اجرا
نمی‌کرد.

### Change

- جدول ثابت چهار ردیف QWERTY به `plan_engine_login_type.py` اضافه شد.
- تست Lazy import اکنون `_qwerty_neighbor("q") == "w"` را واقعاً اجرا می‌کند.
- Windows TestRunner وجود ثابت را در Bundle نهایی کنترل می‌کند.
- Hash فایل Type در Manifest مدرن بازسازی شد.
- الگوریتم انتخاب همسایه، تعداد/فاصلهٔ Typo، Natural Mouse، ARM و Sound تغییر نکرده‌اند.

### Validation

- تست اجرای واقعی QWERTY Neighbor باید سبز شود.
- 53 قرارداد Portable، Windows TestRunner، Manifest 40/40 و Read-back باید پیش از انتشار سبز شوند.

### Next test

Build 105 را نصب و Bundle پروژه را کامل دوباره روی CIRCUITPY بساز. در Login/DC
باید `after-mouse-runtime-import` و `after-type-import` ثبت شوند، TYPE دارای Typo
بدون `_QWERTY_ROWS` NameError کامل شود و سپس مسیر تا Game و حرکت قلاب ادامه یابد.

## Build 104 — تقسیم ترتیبی Login، Natural Mouse و Human Type

**Previous build:** 103 / Classroom release 201
**Status:** local candidate; Bundle 330 hardware retest required

### Problem observed

Bundle 330 تمام مراحل جدید Game را پاس کرد: Facade، Core، Runtime، ایندکس 369 فرمان با 1,476 بایت Offset و Parallel همگی بدون MemoryError بارگذاری شدند و ماکرو تا قلاب‌انداختن پیش رفت. شکست نهایی هنگام اولین RMOUSE بود: `before-mouse-import=42912` و Import یک‌تکهٔ فایل 12.8KB Login/Mouse برای تخصیص 896 بایت شکست خورد.

### Root cause

Natural Mouse یا تعداد پترن‌ها اجرا نشده بود؛ Compiler فایل ترکیبی Login/Mouse/Typing روی Heap ازقبل‌مصرف‌شدهٔ Game به بلوک پیوستهٔ کافی دسترسی نداشت.

### Change

- `plan_engine_login.py` به Facade حدود 2.3KB تبدیل شد.
- `plan_engine_login_core.py` حدود 2.7KB شامل Rangeها و PausePlanner است.
- `plan_engine_login_mouse.py` حدود 4.6KB همان الگوریتم Natural Mouse را بدون تغییر نگه می‌دارد.
- `plan_engine_login_type.py` حدود 5.5KB فقط هنگام اولین TYPE بارگذاری می‌شود.
- Core، Mouse و Type ترتیبی و تنبل بارگذاری می‌شوند و هر مرز before/after/failure تله‌متری دارد.
- cleanup پس از Route و کالیبراسیون هر چهار ماژول Login را آزاد می‌کند.
- Manifest، Exporter و Read-back از 37 به 40 فایل ارتقا یافت.
- مسیر، سرعت، مکث‌ها، Typo، ARM، Sound، Light و Cycle تغییر نکرده‌اند.

### Validation

- Facade هیچ Core/Mouse/Type را زودهنگام وارد نمی‌کند.
- PausePlanner فقط Core، اولین RMOUSE فقط Mouse و اولین TYPE فقط Type را بارگذاری می‌کند.
- size gate ویندوز: Facade <4KB، Core <5KB، Mouse <7KB و Type <7KB.
- تمام تست‌های Game/Parallel/Splash/Sound و قرارداد Natural Mouse حفظ شده‌اند.

### Next test

پس از `before-mouse-import` و `after-mouse-import` باید Stageهای `LOGIN|before-core-import`، `after-core-import`، سپس `before-mouse-runtime-import` و `after-mouse-runtime-import` دیده شوند و حرکت قلاب بدون MemoryError ادامه یابد.

## Build 103 — تقسیم Compiler Peak موتور Game به Importهای ترتیبی

**Previous build:** 102 / Classroom release 200
**Status:** local candidate; Bundle 320 hardware retest required

### Problem observed

Bundle 320 اصلاح فایل‌محور را دوباره تأیید کرد: Heap در خواندن `game_steps.txt` فقط از 48,320 به 48,144 بایت افتاد. تله‌متری جدید محل شکست را قطعی کرد: درست پیش از Import موتور 60,144 بایت آزاد بود، اما Compile ماژول یک‌تکهٔ `plan_engine_game.py` حافظه را به 444 بایت رساند و تخصیص 1,784 بایتی شکست خورد. هیچ `file-index`، Random Package یا RMOUSE هنوز اجرا نشده بود.

### Root cause

Build 102 Import تو‌در‌توی Login را حذف کرد، اما خود فایل 23KB موتور Game همچنان باید در یک Compile واحد به bytecode و objectهای CircuitPython تبدیل می‌شد. Peak Compiler یک‌تکه، نه تعداد 165–176 پترن موس، علت این شکست بود.

### Change

- `plan_engine_game.py` به Facade حدود 1KB تبدیل شد و هیچ موتور فرعی را در سطح ماژول Import نمی‌کند.
- `plan_engine_game_core.py` حدود 6.7KB است و File index، Range/Package و Lazy Mouse را نگه می‌دارد.
- `plan_engine_game_runtime.py` حدود 10KB است و Interpreter اصلی را پس از آزادشدن Compiler Core بارگذاری می‌کند.
- `plan_engine_game_parallel.py` حدود 6.8KB است و فقط هنگام اولین `PGROUP` وارد می‌شود.
- بین تمام Importها GC کامل و Stageهای before/after/failure ثبت می‌شود.
- cleanup کالیبراسیون و Route هر چهار ماژول Game را از cache آزاد می‌کند.
- Manifest/Exporter/Read-back از 34 به 37 فایل ارتقا یافت.
- Natural Mouse، ARM، SoundWatch، Splash/Whisper، Light Preemption، Cycle و پروژهٔ کاربر تغییر نکرده‌اند.

### Validation

- Import Facade هیچ Core/Runtime/Parallel/Login را زودهنگام بارگذاری نمی‌کند.
- Core و Runtime ترتیبی بارگذاری می‌شوند؛ Login فقط در اولین RMOUSE و Parallel فقط در اولین PGROUP وارد می‌شود.
- size gate ویندوز: Facade <3KB، Core <10KB، Runtime <12KB و Parallel <9KB.
- Game فایل‌محور 165 آیتمی، LABEL/GOTO، Async Sound، Scoped Splash، Shared Listener و Parallel scheduler پاس شدند.
- Boot verifier و Target read-back موجودی 37فایلی و تمام SHA256ها را می‌پذیرند.

### Next test

پس از `ROUTE/start game_steps.txt` باید به‌ترتیب `before-engine-import`، `after-engine-import`، `before-core-import`، `after-core-import`، `after-runtime-import` و `file-index` دیده شود. هنگام اولین گروه موازی نیز `before-parallel-import` و `after-parallel-import` و هنگام اولین حرکت `before-mouse-import` و `after-mouse-import` باید بدون MemoryError ثبت شوند.

## Build 102 — حذف Peak واردکردن تو‌در‌توی موتور Game

**Previous build:** 101 / Classroom release 199
**Status:** local candidate; Bundle 310 Game hardware retest required

### Problem observed

Bundle 310 هر سه Preemption نور Login→Dashboard، Dashboard→Loading و Loading→Game را پاس کرد. اصلاح فایل‌محور Build 101 نیز موفق بود: Heap بین `before-route-read` و `after-route-read` فقط از 55,888 به 55,712 بایت افتاد. بااین‌حال، پیش از ثبت `GAME|stage=file-index`، Import موتور با `MemoryError` برای تخصیص 1,386 بایت شکست خورد.

### Root cause

`plan_engine_game.py` در سطح ماژول بلافاصله `plan_engine_login.py` را Import می‌کرد. در نتیجه هنگام Compile/Import فایل 23KB موتور Game، موقت‌های Compiler هنوز زنده بودند که Import و Compile فایل 12KB حرکت موس شروع می‌شد. فشار اصلی دیگر پروژهٔ 369 فرمانی یا Random Package نبود؛ Peak واردکردن تو‌در‌تو و fragmentation Heap بود.

### Change

- Import سطح‌بالای `plan_engine_login` از موتور Game حذف شد.
- State بازی بدون `PausePlanner` آغاز می‌شود و Helper موس فقط هنگام اولین `RMOUSE` بارگذاری می‌شود.
- پیش و پس از Import موس GC کامل اجرا می‌شود؛ پس از بارگذاری، همان Helper و PausePlanner برای ادامهٔ Route استفاده می‌شوند.
- در `code.py` تله‌متری `before-engine-import`، `after-engine-import` و `engine-import-memoryerror` اضافه شد.
- در Runner تله‌متری `before-mouse-import`، `after-mouse-import` و `mouse-import-memoryerror` اضافه شد.
- رفتار Natural Mouse، SoundWatch، Splash/Whisper، Preemption، چرخه و فایل پروژه تغییر نکرده است.

### Validation

- تست رگرسیون ثابت می‌کند Import موتور Game، `plan_engine_login` را وارد نمی‌کند و اولین درخواست موس آن را Lazy-load می‌کند.
- تست فایل‌محور 165 آیتمی، Preemption و مسیرهای Sound/Parallel پاس شدند.
- هر 53 قرارداد رسمی Portable با overlay دقیق CI موفق شدند.
- اندازهٔ `plan_engine_game.py` با پایان‌خط ویندوز نیز زیر سقف 24KB باقی ماند.

### Next test

Bundle Build 102 را روی Pico بریزید و همان پروژهٔ ماهیگیری را اجرا کنید. ترتیب مورد انتظار پس از `ROUTE/start game_steps.txt` عبارت است از `before-engine-import`، `after-engine-import`، `file-index`، سپس هنگام نخستین حرکت `before-mouse-import` و `after-mouse-import`. پس از آن Route باید بدون `MemoryError` وارد حرکت/Delay پکیج شود.

## Build 101 — اجرای فایل‌محور Game و Random Package کم‌حافظه

**Previous build:** 100 / Classroom release 198
**Status:** local candidate; Bundle 308 Game hardware retest required

### Problem observed

Bundle 308 اصلاح Preemption را سخت‌افزاری تأیید کرد: Login→Dashboard، Dashboard→Loading و Loading→Game همگی با `GUARD|PREEMPT` مسیر قبلی را متوقف و Route جدید را فوراً اجرا کردند. اما Game با وجود حذف `fh.read()` باز هم پس از `after-route-read` و `light-route` با `MemoryError` بدون متن شکست خورد. هنگام خواندن ۳۶۹ فرمان، Heap از 52,208 به 27,296 بایت افتاد و پس از GC فقط 41,600 بایت برای Import و Scheduler باقی ماند.

### Root cause

- Streaming Build 100 رشتهٔ 11.5KB را حذف کرد، ولی همچنان هر خط را به Tuple `(op,args)` در یک List تبدیل می‌کرد. ۳۶۹ فرمان Bundle 308 حدود 25KB Heap موقت/ماندگار مصرف کردند.
- هنگام رسیدن به Random Package ماهیگیری، Scheduler برای ۱۶۵ آیتم هم فهرست همهٔ Rangeها و هم فهرست کامل ترتیب Shuffle را می‌ساخت، درحالی‌که قرارداد فقط ۱ تا ۲ آیتم می‌خواست.

### Change

- `game_steps.txt` مستقیماً به Runner فایل‌محور تحویل داده می‌شود و دیگر در `code.py` به List فرمان تبدیل نمی‌شود.
- Runner در یک اسکن، فقط Offset چهار‌بایتی خطوط معتبر را داخل `bytearray` ثبت می‌کند؛ متن هر فرمان فقط هنگام دسترسی از Flash خوانده می‌شود.
- رابط فایل‌محور همان عملیات `len`، Index و Iteration موردنیاز Engine را فراهم می‌کند، بنابراین LOOP، PGROUP، LABEL/GOTO و Sound semantics تغییر نکرده‌اند.
- حالت `RPKG|pick` با Reservoir Sampling تنها تعداد درخواستی آیتم‌ها را نگه می‌دارد؛ انتخاب یکنواخت و ترتیب تصادفی حفظ می‌شود.
- حالت‌های `all` و `seq` برای Packageهای کوچک موجود بدون تغییر باقی مانده‌اند.
- فایل، Offsetها و Context در تمام مسیرهای Success، Abort و Exception بسته و آزاد می‌شوند.
- Firmware ARM، Natural Mouse، Sound، Preemption نور و محتوای پروژه تغییر نکرده‌اند.

### Validation

- `game_steps.txt` واقعی Bundle 308 شامل ۳۶۹ فرمان با تنها ۱٬۴۷۶ بایت Offset ایندکس شد.
- Package اول هشت‌تایی طبق حالت `all` حفظ شد و Package ماهیگیری ۱۶۵‌تایی فقط دو Range انتخاب‌شده ساخت.
- تست کامل فایل‌محور با ۱۶۵ آیتم اجرا شد و هیچ List سراسری از فرمان‌ها یا آیتم‌های Package ساخته نشد.
- تست‌های Preemption، Whisper/Splash، Shared Listener، HANDPATH و Parser پاس شدند.
- هر ۵۲ قرارداد رسمی Portable موفق شدند.
- اندازهٔ Runner سبک Game حدود 22.2KB است و سقف CI با CRLF ویندوز روی 24KB قفل شد.

### Next test

Bundle را با Build 101 بازسازی و Game را اجرا کنید. بعد از `ROUTE/start game_steps.txt` باید `after-route-read` افت بسیار کوچک‌تری نسبت به Bundle 308 نشان دهد، سپس `SOUNDWATCH|armed`، حرکت/Delay پکیج و `WPROFILE` بدون MemoryError اجرا شوند. انتقال پایدار Game→Targeted نیز باید همچنان `GUARD|PREEMPT` ثبت و Route بازی را ایمن متوقف کند.

## Build 100 — توقف ایمن Route با تغییر پایدار نور و خواندن Streaming بازی

**Previous build:** 99 / Classroom release 197
**Status:** local candidate; Login→Dashboard and Game hardware retest required

### Problem observed

لاگ Bundle 306 تأیید کرد اصلاح Revision موفق است: `CALSTATUS source=file` مقدار Dashboard برابر `15.3 ± 3.0` را گزارش کرد و Lux=16.7 Route داشبورد را اجرا کرد. اما Route طولانی Login تا فشار دستی GP4 ادامه یافت، چون Guard هنگام اجرای همگام Route دیگر نور را نمونه‌گیری نمی‌کرد. سپس Game پیش از `after-route-read` با `MemoryError` تخصیص 6400 بایت شکست خورد.

### Root cause

- Main Loop فقط بین Routeها `guard.update` را اجرا می‌کرد. Tick تعاونی داخل Delay/TYPE/Mouse فقط دکمه‌ها و Deadline چرخه را بررسی می‌کرد.
- `game_steps.txt` حدود 11.5KB بود و با `fh.read()` باید به یک رشتهٔ پیوسته تبدیل می‌شد؛ Heap با وجود 49KB آزاد، بلوک پیوستهٔ 6400 بایتی نداشت.

### Change

- Tick تعاونی Route هر 100ms سنسور نور را نمونه‌گیری و همان Debounce/Transition رسمی Guard را اجرا می‌کند.
- فقط پس از پایدارشدن وضعیت جدید، رویداد `EVT|GUARD|PREEMPT|from=...|to=...|lux=...` ثبت می‌شود. Spike یا Unknown کوتاه Route را قطع نمی‌کند.
- Preemption فقط Route جاری را Abort می‌کند و Run اصلی فعال می‌ماند؛ تصمیم ذخیره‌شدهٔ Guard در دور بعد Route محیط جدید را اجرا می‌کند.
- پیش از خروج، تمام Keyboard keyها آزاد و فرمان Abort به ARM ارسال می‌شود. Pause و Stop دستی همچنان اولویت دارند و Stop هرگز خودکار دوباره فعال نمی‌شود.
- Routeهای Light/Game خط‌به‌خط از Flash Parse می‌شوند؛ فقط Route واقعاً ناسازگار به Parser کامل و `fh.read()` fallback می‌کند.
- Parser مشترک نگه داشته شد تا `code.py` در Checkout ویندوز نیز زیر سقف 55KB باقی بماند.
- ARM Firmware، Natural Mouse، Sound و محتوای Routeهای کاربر تغییر نکرده‌اند.

### Validation

- شبیه‌سازی Login→Dashboard ثابت کرد قبل از 300ms Route ادامه دارد و پس از پایداری، Run روشن می‌ماند ولی Route Abort، Keyboard آزاد، ARM متوقف و تصمیم Dashboard آماده می‌شود.
- Route مصنوعی بیش از 360 فرمان بدون رشتهٔ بزرگ به‌صورت Streaming Parse شد.
- 51 قرارداد رسمی Portable موفق شدند و هر 34 Hash Manifest معتبر است.
- Windows contract وجود Preemption، آزادسازی منابع و Streaming read را قفل می‌کند.

### Next test

1. Login Route را شروع کنید و وارد Dashboard شوید؛ باید حداکثر حدود 0.8–1.0 ثانیه بعد `GUARD|PREEMPT|from=login-or-dc|to=character-dashboard` و `ROUTE/aborted login_or_dc_steps.txt` دیده شود.
2. بلافاصله باید `ROUTE/start character_dashboard_steps.txt` ثبت شود و هیچ Step دیگری از Login اجرا نشود.
3. مسیر Loading→Game را ادامه دهید؛ `after-route-read` و `light-route` باید ثبت شوند و MemoryError تخصیص 6400 بایت نباید تکرار شود.
4. Pause، Resume و GP4 Stop را جداگانه بررسی کنید؛ Stop نباید Route بعدی را خودکار فعال کند.

## Build 99 — مرجع‌شدن Revision پروفایل Package و ذخیره بدون Import لحظه‌ای

**Previous build:** 98 / Classroom release 196
**Status:** local candidate; Dashboard detection and physical-save hardware retest required

### Problem observed

Bundle 304 و هر 35 Hash سالم بود و فایل‌ها Dashboard را `15.3 ± 3.0` داشتند، اما `CALSTATUS` نشان داد Guard هنوز NVM قدیمی `14.2 ± 1.0` را اعمال می‌کند. در نتیجه نور `16.7` ناشناخته ماند. در تلاش بعدی، نمونه‌گیری در `complete-stage` و `heap-ready` تمام شد اما هیچ `saved-stage` یا خطای ذخیره‌ای ثبت نشد و Heartbeat ادامه یافت.

### Root cause

- CAL2 عمداً Revision فایل را نادیده می‌گرفت؛ بنابراین تغییر پروفایل در Package هیچ‌وقت NVM قدیمی را کنار نمی‌زد.
- Build 98 ماژول `calibration_fit.py` را هنگام Save روی Heap تکه‌تکه Import/Compile می‌کرد. MemoryError همان Import می‌توانست حتی ساخت Telemetry خطا را نیز ناکام کند، در حالی که Main Loop و Heartbeat زنده می‌ماندند.
- Diagnostic برای `nearest` از فایل Bundle استفاده می‌کرد، ولی Guard از NVM استفاده می‌کرد؛ به همین دلیل `nearest=12.3..18.3` در کنار `CALSTATUS source=nvm range=13.2..15.2` دیده شد.

### Change

- NVM اکنون `base_revision` پروفایل نور را همراه Snapshot ذخیره می‌کند و فقط برای همان Revision بارگذاری می‌شود.
- تغییر Center/Tolerance در Classroom یک Revision جدید می‌سازد؛ Package جدید خودکار مرجع می‌شود و NVM قدیمی اعمال نمی‌شود.
- Export مجدد Routeها با پروفایل نور یکسان همان Revision را نگه می‌دارد و کالیبراسیون فیزیکی NVM حفظ می‌شود.
- Snapshotهای قدیمی CAL1/CAL2 بدون Revision منطبق Stale هستند و یک‌بار به فایل Package برمی‌گردند.
- «ذخیره پروفایل‌ها» هیچ فرمانی به برد و NVM ارسال نمی‌کند؛ فقط Package خروجی را تنظیم می‌کند.
- Fit به `calibration_nvm.py` منتقل شد؛ این ماژول در Boot از قبل بارگذاری می‌شود و Save دیگر Import/Compile لحظه‌ای ندارد.
- Manifest دوباره 34 فایلی است و فایل موقت Build 98 هنگام Export از درایوهای قدیمی پاک می‌شود.
- ARM، Mouse، Sound و Routeهای کاربر تغییر نکرده‌اند.

### Validation

- 50 قرارداد رسمی Portable موفق شدند.
- تست NVM تأیید می‌کند Revision برابر Snapshot را نگه می‌دارد و Revision جدید Package، NVM قدیمی را رد می‌کند.
- تست‌های Fit عادی، Center-inside و centers-too-close بدون Import لحظه‌ای موفق شدند.
- هر 34 Hash Manifest با بایت نهایی تطبیق دارد.

### Next test

1. با همان پروفایل Dashboard برابر `15.3 ± 3.0` Bundle را کامل بسازید؛ Export باید `34/34 hashes and Guard revisions OK` بدهد.
2. پس از Soft Reboot، Start باید `CALSTATUS source=file` و Dashboard مؤثر نزدیک `12.3..18.3` را نشان دهد و Lux=16.7 را بشناسد.
3. یک کالیبراسیون فیزیکی Dashboard انجام دهید؛ پس از `complete-stage` باید `CAL|FIT` در صورت نیاز، سپس `storage=nvm` و `saved-stage` دیده شود.
4. یک Soft Reboot دیگر بدون تغییر پروفایل انجام دهید؛ این بار `CALSTATUS source=nvm` باید همان Snapshot تازه را بارگذاری کند.

## Build 98 — رفع MemoryError بوت پس از Adaptive Fit

**Previous build:** 97 / Classroom release 195
**Status:** local candidate; Bundle boot hardware retest required

### Problem observed

Bundle 303 در Soft Reboot پیش از ساخت Runtime و در خط `import combined_guard_runtime` با `MemoryError` هنگام تخصیص 1244 بایت متوقف شد.

### Root cause

Bundle و هر 34 Hash سالم بودند. Build 97 حدود 3.6KB به `guard_calibration_protocol.py` و حدود 2.2KB به `combined_guard_runtime.py` افزوده بود. CircuitPython مجبور بود منطق Fit را در Boot Parse/Import کند، هرچند Fit فقط هنگام ذخیرهٔ کالیبراسیون لازم است؛ اوج Heap تکه‌تکه از ظرفیت تخصیص پیوسته عبور کرد.

### Change

- Adaptive Fit به ماژول مستقل `calibration_fit.py` منتقل شد و فقط داخل `_publish_calibration` Lazy-load می‌شود.
- ماژول Fit پیش از نوشتن NVM از `sys.modules` خارج و Garbage Collection اجرا می‌شود.
- `combined_guard_runtime.py` از 41,449 به 39,241 بایت و `guard_calibration_protocol.py` از 8,853 به 4,933 بایت کاهش یافت؛ هر دو از اندازهٔ Boot نسخهٔ قبل از Build 97 کوچک‌ترند.
- Center ثابت، حداقل Tolerance برابر 0.5، Gap برابر 0.25، Fit یک‌طرفه/دوطرفه، ذخیرهٔ اتمیک و Fail-Closed بدون تغییر حفظ شدند.
- Manifest و Exporter به موجودی 35 فایلی ارتقا یافتند و Read-back روی CIRCUITPY اکنون `35/35 hashes and Guard revisions OK` را الزام می‌کند.
- ARM، Mouse، Sound، Route و پروژهٔ کاربر تغییر نکرده‌اند.

### Validation

- 50 قرارداد رسمی Portable موفق شدند.
- تست‌های Fit عادی، Center-inside و centers-too-close موفق شدند.
- قرارداد Boot الزام می‌کند Runtime با نرمال‌سازی LF/CRLF کمتر از 40KB، Protocol کمتر از 5.5KB و Import Fit فقط Lazy باشد.
- قرارداد Windows برای Checkoutهای CRLF اصلاح شد تا فقط بایت‌های معنایی Runtime را بسنجد، نه افزایش مصنوعی یک بایت در هر خط.
- هر 35 Hash Manifest با بایت نهایی تطبیق دارد.

### Next test

1. Bundle را با Build جدید کامل بازسازی و روی CIRCUITPY جایگزین کنید؛ Soft Reboot باید بدون MemoryError وارد PONG شود.
2. یک کالیبراسیون هم‌پوشان انجام دهید؛ `CAL|FIT` یا `CAL|FIT-PAIR` و سپس `saved-stage` باید ثبت شود.
3. پیام Export باید `35/35 hashes and Guard revisions OK` باشد.

## Build 97 — Fit تطبیقی یک‌طرفه و دوطرفهٔ کالیبراسیون

**Previous build:** 96 / Classroom release 194
**Status:** CI candidate; overlapping calibration hardware retest required

### Problem observed

وقتی Tolerance پیشنهادی وارد دامنهٔ دیگری می‌شد، Save فوراً با `CAL|OVERLAP` رد می‌شد. حتی در حالتی که با کاهش کنترل‌شدهٔ دامنهٔ جدید یا عقب‌بردن محدود دامنهٔ مجاور می‌شد دو پروفایل امن ساخت، کاربر مجبور به نمونه‌گیری دوباره بود.

### Root cause

Publish فقط یک Guard دودویی داشت: Candidate بدون تغییر پذیرفته یا رد می‌شد. Cap اولیه نیز فقط دامنهٔ جدید را در حالتی که Center بیرون دامنهٔ دیگر بود محدود می‌کرد و Center-inside را حل نمی‌کرد.

### Change

- قبل از ذخیره، Fit اتمیک روی مجموعهٔ کامل پروفایل‌ها اجرا می‌شود.
- ابتدا Tolerance دامنهٔ جدید تا حداقل `0.5 lux` کاهش می‌یابد.
- اگر هنوز هم‌پوشانی باقی باشد، Tolerance دامنهٔ مجاور فقط به‌اندازهٔ لازم عقب می‌رود؛ هیچ Centerای جابه‌جا نمی‌شود.
- بین دو دامنه Gap ثابت `0.25 lux` حفظ می‌شود و پروفایل‌های قدیمی باریک هرگز بزرگ نمی‌شوند.
- تغییرات با `CAL|FIT` یا `CAL|FIT-PAIR` شامل مقدار درخواستی/اعمال‌شده و دامنهٔ مجاور ثبت می‌شوند.
- اگر فاصلهٔ Centerها برای دو دامنهٔ حداقلی به‌علاوهٔ Gap کافی نباشد، ذخیره همچنان با `CAL|FIT|reason=centers-too-close` Fail-Closed می‌شود.
- همهٔ تغییرات یک‌بار در NVM ذخیره و سپس هم‌زمان روی Guard فعال اعمال می‌شوند.
- Firmware Pro Micro، ARM، Mouse، Sound و Routeها تغییر نکرده‌اند.

### Validation

- سناریوی هم‌پوشانی عادی فقط Candidate را تا مرز امن عقب می‌برد.
- سناریوی Center-inside، Candidate و همسایه را با Min/Gap ثابت Fit می‌کند.
- سناریوی Centerهای بسیار نزدیک بدون نوشتن NVM رد می‌شود.
- قرارداد ترتیب Fit پیش از `calibration_nvm.save` و Telemetry یک‌طرفه/دوطرفه تست می‌شود.
- قرارداد Windows TestRunner از Cap قدیمی داخل `calibrated_profile` به Fit تطبیقی مرحلهٔ Publish به‌روزرسانی شد؛ Min/Gap و وجود Helper جدید را قفل می‌کند.

### Next test

1. یک Stage با Tolerance هم‌پوشان ذخیره کنید؛ به‌جای `OVERLAP` باید `CAL|FIT` یا `CAL|FIT-PAIR` و سپس `saved-stage` دیده شود.
2. `CALSTATUS` را بررسی کنید؛ Centerها باید ثابت و فقط Toleranceهای گزارش‌شده کاهش یافته باشند.
3. هر دو محیط در دو Start جدا باید State صحیح خود را بگیرند و بین Rangeهای گزارش‌شده حداقل ۰٫۲۵ lux فاصله باشد.

## Build 96 — سنجش تازه در Start فیزیکی و حاشیهٔ Drift کالیبراسیون

**Previous build:** 95 / Classroom release 193
**Status:** local candidate; Dashboard hardware retest required

### Problem observed

در Bundle 301 مقدار Dashboard برابر `13.3 ± 3.0` بود، اما نور زندهٔ `16.7` فقط ۰٫۴ lux بیرون بازه قرار گرفت و `unknown` شد. پس از Stop/Start فیزیکی نیز خط جدید State دیده نمی‌شد و فقط reconnect کابل آن را ظاهر می‌کرد.

### Root cause

حاشیهٔ پس از Envelope نمونه‌گیری فقط ۰٫۵ lux بود و Drift کوتاه پس از کالیبراسیون را کامل پوشش نمی‌داد. همچنین Start فیزیکی Guard را Reset می‌کرد، اما برخلاف `GUARD|ON`، مقادیر `debug_last_state` و `debug_last_denied` را Reset نمی‌کرد؛ بنابراین سنجش `unknown → unknown` انجام ولی در Telemetry حذف می‌شد.

### Change

- Start کوتاه GP4 علاوه بر Guard، `last_decision` و Debug State/Denied را نیز Reset می‌کند.
- Boot و هر Start مقدار دقیق `EVT|CALSTATUS|source=nvm|file` را ثبت می‌کنند؛ Start فیزیکی Center/Tolerance/Range واقعی Dashboard را نیز همراه آن می‌فرستد.
- حاشیهٔ Drift در فرمول کالیبراسیون از `deviation + 0.5` به `deviation + 1.0` افزایش یافت.
- Cap نزدیک‌ترین پروفایل و کنترل Overlap بدون تغییر و همچنان Fail-Closed باقی مانده‌اند.
- Runtime موس، ARM، Cadence، Sound و مسیرهای پروژه تغییر نکرده‌اند.

### Validation

- Regression واقعی `13.3/15.8 → live 16.7` اکنون Tolerance حداقل ۳٫۵ می‌سازد و بدون Overlap پذیرفته می‌شود.
- تست اختصاصی Start فیزیکی Reset کامل Telemetry و گزارش منبع کالیبراسیون را قفل می‌کند.
- Manifest Runtime پس از تغییر فایل‌های Hash‌شده بازسازی و کامل بررسی می‌شود.

### Next test

1. Stage Dashboard را یک‌بار در نور پایدار Save کنید؛ در Start بعدی باید `active-source=nvm` و بازهٔ مؤثر ثبت شود.
2. در Dashboard، Stop و دوباره Start کنید؛ بدون قطع کابل باید فوراً `STATE/character-dashboard` یا `STATE/unknown lux=...` تازه دیده شود.
3. اگر مقدار زنده داخل Range گزارش‌شده بود، Route Dashboard باید اجرا شود؛ اگر بیرون بود، همان مقدار برای تیون بعدی ارسال شود.

## Build 95 — بازنشستگی UI قدیمی Wait For Sound برای Splash

**Previous build:** 94 / Classroom release 192
**Status:** local candidate; Export and old-project migration test required

### Problem observed

معماری جدید Splash از پروفایل مستقل و Route واکنش اختصاصی استفاده می‌کرد، اما
Timeout هر Cast هنوز داخل دیالوگ عمومی `Wait For Sound` پنهان بود. این رابط با
مدل جدید تناقض داشت و کاربر به‌درستی انتظار داشت Wait For Sound منسوخ شده باشد.

### Root cause

برای حفظ قرارداد `WPROFILE`، پیاده‌سازی Build 93 همان نود قدیمی
`waitForSound` را با `responseRoute=splash` دوباره استفاده کرده بود. Runtime
جدید بود، اما مدل و ورودی رابط قدیمی باقی مانده بود.

### Change

- استپ جدید و صریح `Splash Listener (Scoped)` به منو، Rail و منوی راست‌کلیک اضافه شد.
- `Wait For Sound` از تمام مسیرهای درج پروژهٔ جدید حذف و فقط به‌عنوان Legacy loader نگه‌داری شد.
- Timeout حداقل/حداکثر هر Cast به کارت پروفایل Splash کنار خروجی Pico منتقل شد.
- Exporter مقدارهای پروفایل را روی فرمان `WPROFILE|splash,min,max` اعمال می‌کند.
- فایل‌های Format 1–4 که `waitForSound + responseRoute=splash` دارند، هنگام Load
  به `splashListener` و پروفایل Timeout جدید Migration می‌شوند.
- Listener خارج از تب Game و بازهٔ Timeout نامعتبر به‌صورت Fail-Closed رد می‌شوند.
- Firmware Pro Micro، ARM، DDA/Cadence، Natural Mouse و Runtime Pico تغییر نکرده‌اند.

### Validation

- قرارداد منبع جدید، حذف Wait For Sound از سه مسیر درج UI، وجود Migration،
  انتقال Timeout و Fail-Closed خارج Game را کنترل می‌کند.
- Rail contract با مدل جدید همگام شد: `splashListener` باید قابل درج باشد و `waitForSound` فقط Legacy/load-only باقی می‌ماند.
- مجموعهٔ کامل Portable و Windows TestRunner باید پیش از انتشار سبز شود.

### Next test

1. Build جدید را در پوشه‌ای تازه باز و پروژهٔ ماهیگیری قدیمی را Load کنید؛ `Wait For Sound + responseRoute=splash` باید خودکار به `Splash Listener` تبدیل شود.
2. در کارت Splash بازهٔ Timeout را تغییر دهید و Export کنید؛ `game_steps.txt` باید همان بازه را در `WPROFILE|splash,min,max` داشته باشد.
3. در Game، Detection باید Route تب Splash را اجرا و Timeout باید بدون F به Cast بعدی برود؛ نرمی موس و Whisper سراسری نباید تغییر کنند.

## Build 94 — رفع رد شدن Manifest در Boot

**Previous build:** 93 / Classroom release 190
**Status:** local candidate; Boot and Game hardware test required

### Problem observed

Bundle 300 با وجود ۳۴ Hash صحیح، هنگام Boot با
`GuardBundleError: unexpected or duplicate SHA256SUMS file: splash_steps.txt`
متوقف شد.

### Root cause

Exporter دو Route جدید `splash_steps.txt` و `whisper_steps.txt` را درست تولید و
Hash می‌کرد، اما موجودی Boot Runtime پیش از `load_guard_bundle("/")` هنوز این
دو فایل را نمی‌شناخت. این دو فایل Route نوری Guard نیستند و نباید به
`guard-transition.json` افزوده شوند.

### Change

- هر دو Route واکنش صوتی پیش از بارگذاری Bundle به موجودی Hash Runtime افزوده شدند.
- تست رگرسیون جدید برابری دقیق موجودی ۳۴فایلی Manifest و Boot verifier را کنترل می‌کند.
- Guard transition، فریمور Pro Micro، DDA/Cadence و Natural Mouse بدون تغییر مانده‌اند.

### Validation

- تمام ۴۷ تست Portable روی نسخهٔ دقیق Remote Branch پاس شدند.
- Windows TestRunner، Portable contracts، ARM 2.8 compile، Plan2، Golden، Hashes و Sensitive Guard همگی سبز شدند.
- `code.py` Remote با نسخهٔ محلی بایت‌به‌بایت برابر و SHA256 آن `e06891815631e9588f27b80a442a012f12d5c4221fb057557f373d9433f1b013` است.

### Next test

خروجی پروژهٔ Build 94 باید بدون خطای Manifest Boot شود؛ سپس Game باید Whisper
سراسری و Splash محدود به Cast را اجرا کند.

## Build 93 — Whisper سراسری و Splash محدود به هر Cast

**Previous build:** 92 / Classroom release 177
**Status:** CI candidate; physical sound, heap and mouse-smoothness test required
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

در Build 92 هر دو پروفایل Whisper و Splash به‌صورت وقفهٔ سراسری Game مدل شده بودند. این رفتار برای Whisper درست است، اما Splash باید فقط از داخل مرحلهٔ انتظار صدای همان Cast فعال باشد؛ همچنین Timeout معمول ماهیگیری باید حدود ۲۰ ثانیه بماند تا اگر صدایی نیامد، برنامه دوباره قلاب بیندازد.

### Root cause

قرارداد سراسری قبلی محل و عمر Listener منطقی Splash را از ساختار Cast جدا کرده بود. بنابراین Splash می‌توانست بیرون از Wait For Sound فعال شود و مسیر Detection/Timeout معنای «پایان Cast جاری و رفتن به Cast بعدی» را به‌صورت مستقل حمل نمی‌کرد.

### Change

- Whisper با حالت `global` فقط در طول اجرای روت Game فعال است؛ روت‌های Targeted، Restart، خروج اضطراری و سایر موقعیت‌ها شنونده ندارند.
- Splash با حالت `scoped` فقط هنگام رسیدن Cast به Wait For Sound مسلح می‌شود.
- Wait For Sound گزینهٔ واکنش `inline` یا `splash` و بازهٔ Timeout تصادفی مستقل دارد؛ پیش‌فرض Splash برابر ۱۸ تا ۲۲ ثانیه است.
- در Detection، تب Splash اجرا می‌شود و سپس Cast جاری پایان می‌یابد؛ در Timeout تب Splash اجرا نمی‌شود و Cast جاری باز هم پایان می‌یابد. هر دو مسیر به Cast بعدی می‌روند.
- یک Listener فیزیکی ADC هر دو سیاست را سرویس می‌دهد. هنگام Wait For Sound، Peak بین Whisper سراسری و Splash موقت با Range/Priority دسته‌بندی می‌شود.
- وقفهٔ Whisper دقیقاً همان Iterator را ادامه می‌دهد؛ Splash یا Timeout فقط شاخه‌های موازی Cast جاری را لغو می‌کند.
- زمان‌بندی حلقهٔ بیرونی و AutoCycle مبتنی بر ساعت دیواری باقی می‌ماند؛ واکنش، Cooldown و Timeout هیچ Deadline ده‌دقیقه‌ای یا ۱۱۰–۱۳۰ دقیقه‌ای را از نو شروع نمی‌کنند.
- هنگام خروج از Game، Listener صدا صریحاً بسته می‌شود تا هیچ واکنشی در وضعیت‌های دیگر باقی نماند.

### Validation

- شبیه‌ساز اختصاصی تأیید کرد Whisper حین Wait For Sound اجرا می‌شود و پس از آن انتظار Splash همان Cast ادامه می‌یابد.
- Detection پروفایل Splash، روت Splash شامل کلید F را اجرا و سپس به فرمان Cast بعدی می‌رود.
- Timeout تصادفی بدون اجرای F، Cast جاری را خاتمه و فرمان Cast بعدی را اجرا می‌کند.
- قرارداد Export شامل `SOUNDWATCH` با Whisper سراسری، Splash محدود و `WPROFILE|splash,18000,22000` است.
- تست‌های Parser، Shared Listener، Parallel runtime، USB FAT و Calibration heap در شبیه‌سازی پاس شدند.
- اندازهٔ Runner سبک Game به حدود ۱۹٫۳KB رسیده است؛ تست واقعی Heap، صدا و نرمی موس هنوز لازم است.

### Next test

روی سخت‌افزار، داخل Game یک Cast واقعی اجرا شود: Whisper باید در هر نقطه فقط تب Whisper را اجرا و همان حرکت را ادامه دهد؛ Splash باید فقط در بازهٔ Wait For Sound باعث F و Cast بعدی شود؛ نبود صدا باید پس از یک مقدار تصادفی ۱۸–۲۲ ثانیه بدون F به Cast بعدی برود. هم‌زمان باید Heap telemetry، نرمی موس و حفظ Deadlineهای ۱۰ دقیقه و ۱۱۰–۱۳۰ دقیقه بررسی شوند.

## Build 92 — وقفهٔ صوتی سراسری و قابل‌بازگشت در Game

**Previous build:** 91 / Classroom release 147
**Status:** CI candidate; physical sound and mouse-smoothness test required
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

پروفایل‌های صدای ویسپر و چلپ فقط داخل شاخه‌های Wait For Sound قابل‌اجرا بودند؛ واکنش ویسپر می‌توانست شاخهٔ ماهیگیری را لغو کند و ادامهٔ دقیق Game حفظ نمی‌شد. تنظیم بازه و اقدام واکنش نیز در یک محل مخلوط بود.

### Root cause

Scheduler قبلی Detection را پایان یک Race می‌دانست و همهٔ Iteratorهای غیر‌برنده را حذف می‌کرد. قرارداد WSNDP نیز فقط Threshold داشت و مسیر واکنش، بازهٔ دوطرفه، Priority و Cooldown را حمل نمی‌کرد.

### Change

- دو تب ثابت `Whisper` و `Splash` برای تعریف نوت، Buzzer و اقدام واکنش اضافه شد.
- کارت «شنوندهٔ سراسری صدا در محیط بازی» کنار دکمهٔ ساخت و کپی Pico قرار گرفت و برای هر پروفایل Peak Min/Max، Priority و Cooldown مستقل دارد.
- خروجی Game به‌طور خودکار با یک Parallel listener کم‌حافظه بسته‌بندی می‌شود؛ فقط یک ADC Listener فیزیکی برای هر دو پروفایل باز می‌ماند.
- در هم‌پوشانی بازه‌ها Priority بزرگ‌تر برنده است؛ در تساوی، Threshold بالاتر انتخاب می‌شود.
- واکنش صوتی یک Interrupt است: Scheduler همهٔ Iteratorها را در جای خود نگه می‌دارد، روت واکنش را اجرا می‌کند، در Cooldown شنود را خاموش نگه می‌دارد و سپس Game را از همان نقطه ادامه می‌دهد.
- Buzzer پاسخ نمی‌تواند خودش را دوباره Trigger کند، چون Listener پیش از اجرای روت بسته و پس از پایان Cooldown دوباره فعال می‌شود.
- قرارداد پروژه به نسخهٔ ۴ و Manifest مدرن به ۳۴ فایل ارتقا یافت؛ دو روت واکنش نیز Hash و Read-back می‌شوند.
- WSNDP پنج‌فیلدی قدیمی همچنان سازگار است؛ قراردادهای ۸‌فیلدی بازه و ۱۰‌فیلدی روت واکنش نیز پذیرفته می‌شوند.

### Validation

- شبیه‌سازی Peak=75 در بازهٔ هم‌پوشان، Whisper با Priority=10 را به‌جای Splash با Priority=5 انتخاب کرد.
- Buzzer ویسپر بین دو حرکت/کلید Game اجرا شد و پس از آن Iterator اصلی ادامه یافت.
- تست‌های Shared Listener، Timeout، Mouse streaming، Sound calibration، USB FAT read-back و Restart cycle پاس شدند.
- تست سخت‌افزاری نهایی صدا و بررسی چشمی نرمی موس هنوز لازم است.

### Next test

روی سخت‌افزار، هم‌زمانی حرکت طولانی و نرم موس با Peakهای واقعی Whisper/Splash تست شود؛ باید فقط تب با Priority درست اجرا شود، Buzzer خودتحریک ایجاد نکند و پس از Cooldown حرکت Game بدون پرش از همان نقطه ادامه یابد.

- مجموعهٔ نهایی ۳۴ فایل Bundle و ۲۸ فایل تغییر‌یافته پس از انتشار با نسخهٔ تست‌شده تطبیق داده شد؛ سقف قرارداد Runner سبک Game نیز با اندازهٔ جدید ۱۵٫۸KB و تبدیل CRLF ویندوز همگام شد.

## Build 91 — Listener مشترک برای Parallel Sound

**Previous build:** 90 / Classroom release 143
**Status:** CI candidate; two-profile physical sound test required
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

Build 143 پروژهٔ `s1.amsj` را با موفقیت Export کرد، اما در اجرای Desktop هر دو شاخهٔ Parallel تقریباً هم‌زمان به Wait For Sound رسیدند. Listener اول با Threshold 130 فعال شد و Listener دوم باعث `ValueError('only one WSND listener is allowed')` و توقف Guard شد.

### Root cause

Pro Micro فقط یک ADC و یک State ماشین `ASND` دارد. Scheduler هر Wait For Sound منطقی را به‌عنوان Listener فیزیکی مستقل اجرا می‌کرد؛ بنابراین ساختار معتبر دو پروفایلی پروژه با محدودیت سخت‌افزار برخورد می‌کرد.

### Change

- همهٔ Wait For Soundهای هم‌زمان یک Parallel Group در یک Listener فیزیکی ادغام می‌شوند.
- Listener مشترک با پایین‌ترین Threshold، کوتاه‌ترین Minimum و نزدیک‌ترین Timeout شروع می‌شود.
- اگر Listener دوم در همان دور Scheduler برسد، Listener اولیه Cancel و فوراً با قرارداد مشترک Restart می‌شود.
- پس از Detection، Peak واقعی ARM خوانده می‌شود و بالاترین Threshold منطبق بر Peak برنده می‌شود.
- فقط شاخهٔ برنده ادامه پیدا می‌کند و Buzzer مربوط به همان پروفایل اجرا می‌شود.
- اگر بیش از یک پروفایل وجود داشته باشد ولی Firmware Peak telemetry ندهد، مسیر Fail-closed باقی می‌ماند.

### Validation

- تست Peak برابر ۱۴۰ تأیید کرد شاخهٔ Threshold 130 و Buzzer هشدار اجرا می‌شود.
- تست Peak برابر ۸۰ تأیید کرد شاخهٔ Threshold 30 و Buzzer موفقیت اجرا می‌شود.
- تست Listener تکی، Mouse هم‌زمان، Timeout، Cancel و ARM Async Sound همچنان پاس شد.
- خطای قدیمی `only one WSND listener may be active` از Scheduler حذف شد.
- کالیبراسیون صدای فیزیکی هنوز تست نشده و Hardware pass آن ادعا نمی‌شود.

### Next test

پس از کالیبراسیون IDهای ۱ و ۲، پروژهٔ `s1.amsj` را اجرا کنید. صدای با Peak بالاتر از پروفایل ۱ باید فقط Tone هشدار و صدای بین Thresholdهای ۲ و ۱ باید فقط Tone موفقیت را اجرا کند؛ Guard نباید متوقف شود.

## Build 90 — Buzzer داخل Parallel Sound

**Previous build:** 89 / Classroom release 139
**Status:** CI candidate; s1 export and physical sound calibration retest required
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

خروجی استاندارد پروژهٔ `s1.amsj` با پیام `plan export blocked: 2 problem(s)` متوقف شد. پروژه شامل دو شاخهٔ ForLoop در Parallel Group بود؛ هر شاخه یک Wait For Sound ساده و یک Buzzer داشت.

### Root cause

Scheduler پیکو از `BEEP` در Parallel پشتیبانی می‌کرد، اما `PlanExporter` نوع `buzzer` را نه در فهرست Cooperative leafها پذیرفته بود و نه به فرمان‌های `BEEP/DELAY` تبدیل می‌کرد. بنابراین هر یک از دو شاخه یک خطای سازگاری ایجاد می‌کرد. پنجرهٔ Current project export نیز فقط تعداد خطاها را نشان می‌داد و جزئیات را در Log پنهان می‌کرد.

### Change

- `buzzer` به مجموعهٔ Stepهای مجاز داخل Parallel Group اضافه شد.
- Preset یا Pattern سفارشی Buzzer با همان Validation موجود به `BEEP|frequency,duration` تبدیل می‌شود.
- Pause میان نت‌ها به `DELAY|min,max` قابل‌اجرای Plan تبدیل می‌شود.
- تست دقیق ساختار پروژهٔ s1 دو شاخهٔ `ForLoop → Wait For Sound → Buzzer` را پوشش می‌دهد.
- پنجرهٔ Current project Pico export تا شش خطای واقعی را مستقیماً نمایش می‌دهد.

### Validation

- قرارداد Python برای سازگاری Parallel/Buzzer پاس شد.
- Runtime موازی `BEEP` را از مسیر عمومی Executor اجرا می‌کند و Wait For Sound ساده همچنان Cooperative است.
- دو ID کالیبراسیون ۱ و ۲ در پروژه یکتا و معتبرند؛ خطا از Calibration ID نبود.
- کالیبراسیون صدای فیزیکی همچنان تست نشده و Hardware pass آن ادعا نمی‌شود.

### Next test

پروژهٔ `s1.amsj` را در Build جدید باز و Current project Pico export کنید. پس از خروجی موفق، Sound Calibration برای IDهای ۱ و ۲ را انجام دهید و اجرای هم‌زمان دو شاخه، Tone هشدار/موفقیت و Timeout را روی سخت‌افزار بررسی کنید.

## Build 89 — Remount قابل‌نوشتن پس از Restart

**Previous build:** 88 / Classroom release 136
**Status:** Hardware passed with diagnostic package E; sound calibration remains untested
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

بستهٔ A چرخه را پس از Restart زنده نگه داشت، اما `CIRCUITPY` پس از بازگشت Windows همچنان Write-protected بود. بستهٔ D با Reset زمان‌محور نیز نه Startup/Mouse را اجرا کرد و نه Read-only را رفع کرد.

### Root cause

روی RP2040 روشن‌شده از USB پایدار، Restart میزبان الزاماً Pico را Reset نمی‌کند. CircuitPython ممکن است Mass Storage را هنگام بازگشت میزبان به‌صورت Read-only معرفی کند. Reset باید بعد از بازگشت واقعی Windows انجام شود؛ تایمر ثابت به‌تنهایی با زمان بوت میزبان هم‌تراز نیست.

### Change

- Controller پس از After منتظر قطع و اتصال مجدد CDC می‌ماند.
- پنج ثانیه پس از CDC reconnect، Marker را با `RESET_DONE` علامت می‌زند و Pico را یک‌بار Reset می‌کند.
- اگر CDC reconnect تشخیص داده نشود، Fallback پس از ۱۲۰ ثانیه Reset را انجام می‌دهد.
- Boot بعدی Marker را بازیابی، از حلقهٔ Reset جلوگیری و مسیر استاندارد Startup را اجرا می‌کند.
- نوت‌های پذیرفته‌شدهٔ تست E در خروجی استاندارد حفظ شدند: یک نت بم هنگام مسلح‌شدن Watcher، دو نت پیش از Reset، سه نت پس از بازیابی Marker و ملودی صعودی پس از تکمیل Startup.
- فشار دستی Start در فاز انتظار، چرخهٔ Pending را لغو و یک Run تازه آغاز می‌کند.

### Validation

- بستهٔ E روی سخت‌افزار پاس شد: تمام نوت‌های مرحله‌ای، Startup و حرکت Mouse اجرا شدند و `TEST.txt` روی `CIRCUITPY` ساخته و حذف شد.
- Regression مسیر `CDC DOWN → UP → stable 5s → one-shot reset → boot marker → Startup` پاس شد.
- Fallback صدوبیست‌ثانیه‌ای، Manual override، USB stale، Deadline، Desktop skip و Marker reserved region پوشش داده شدند.
- Manifest مدرن ۳۲/۳۲ صحیح و تست‌های FAT isolation، NVM calibration و بازخورد صوتی کالیبراسیون پاس شدند.
- **کالیبراسیون صدای فیزیکی هنوز توسط کاربر تست نشده و Hardware pass آن ادعا نمی‌شود.**

### Next test

خروجی استاندارد **Current project Pico export** را با چرخهٔ کوتاه تست کنید و سپس کالیبراسیون صدای فیزیکی را جداگانه کامل کنید: ورود به Sound Calibration، انتخاب هر Profile، Sample/Save، خروج و اجرای یک Step صوتی واقعی. نتیجهٔ Sound calibration باید جداگانه ثبت شود.

## Build 88 — حذف وابستگی Resume به USB DOWN

**Previous build:** 87 / Classroom release 128
**Status:** Hardware passed with diagnostic package A; promoted to standard export
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

در تست Build 128، کالیبراسیون و Tone جدید پاس شد؛ اما پس از Restart و بالا آمدن Windows، Startup خودکار اجرا نشد و فشار دستی Start چرخه را از نو آغاز کرد. Bundle 225 همان Runtime منتشرشدهٔ Build 87 را داشت و ۳۲/۳۲ Hash آن صحیح بود.

### Root cause

روی این سخت‌افزار Warm Restart برق USB و وضعیت Configured پیکو را قطع نمی‌کند. در نتیجه نه `supervisor.runtime.usb_connected` و نه Pro Micro الزاماً لبهٔ `DOWN` تولید نمی‌کنند. Build 87 لبه‌های سریع DOWN/UP را حفظ می‌کرد، اما همچنان اجرای Startup را مشروط به مشاهدهٔ DOWN کرده بود؛ بنابراین با USB دائماً `UP`، Controller برای همیشه در `wait-usb` می‌ماند.

### Change

- تکمیل موفق Route After و Marker مسلح NVM اکنون مجوز معتبر Resume است، حتی اگر USB هیچ DOWN گزارش نکند.
- اگر Pro Micro روی `DOWN` قدیمی مانده باشد، Marker مسلح همراه `Pico UP` آن State منقضی را کنار می‌زند.
- در حالت USB پایدار `UP`، Controller دو ثانیه پایداری را کنترل می‌کند و سپس Route Startup را آغاز می‌کند.
- Route Startup همچنان Delay داخلی ۳۰–۶۰ ثانیه دارد؛ بنابراین آغاز Controller به معنی ارسال فوری Win+1 هنگام خاموش‌شدن Windows نیست.
- Telemetry این مسیر را با `source=marker-no-down` از مسیر دارای لبهٔ واقعی USB متمایز می‌کند.
- مسیر معمول DOWN/UP و بازیابی پس از Reset واقعی Pico بدون تغییر باقی مانده است.

### Validation

- Regression جدید سناریوی «After کامل، Marker مسلح، USB همیشه UP» را اجرا می‌کند و تأیید می‌کند Startup بدون Start دستی اجرا و Marker پاک می‌شود.
- سناریوی DOWN/UP حین After، Boot با Marker، Deadline، پایان طبیعی Game و Desktop skip همچنان پاس می‌شوند.
- Bundle 225 ارسالی مستقل بررسی شد: ۳۲/۳۲ Hash صحیح و محتوای Runtime با Build 87 یکسان بود.
- تست سخت‌افزاری بستهٔ A پاس شد: چرخه پس از Restart بدون فشار مجدد Start زنده ماند. نسخه‌های تشخیصی B و C لازم نشدند.

### Next test

خروجی عادی **Current project Pico export** از این Build به بعد همان منطق پذیرفته‌شدهٔ بستهٔ A را در `restart_cycle.py` و `SHA256SUMS.txt` قرار می‌دهد. فایل‌های تشخیصی، حرکت موس ۲۰ثانیه‌ای و Tone آزمایشی وارد خروجی استاندارد نشده‌اند؛ Route واقعی Startup پروژه بدون تغییر صادر می‌شود. تست بعدی باید با خروجی استاندارد پروژه و بازهٔ واقعی ۱۱۰–۱۳۰ دقیقه انجام شود.

## Build 87 — حفظ USB Transition و تأیید واضح Save

**Previous build:** 86 / Classroom release 124
**Status:** CI candidate; restart-resume and physical calibration retest required
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

در تست Build 124، Windows Restart شد اما پس از بازگشت، Route `startup_steps.txt` خودکار اجرا نشد و چرخه فقط با فشار دستی Start دوباره فعال شد. در کالیبراسیون Dashboard نیز پس از فشار زرد، صدای ذخیره به‌اندازهٔ کافی قابل‌تشخیص نبود.

### Root cause

USB می‌توانست در همان چند ثانیهٔ پایانی Route After از DOWN به UP برگردد. `route_tick()` در فاز After این تغییر را ثبت نمی‌کرد و `_perform_after()` نیز هنگام ورود به `wait-usb` متغیرهای `down_seen/up_since` را پاک می‌کرد؛ بنابراین Controller منتظر DOWN دیگری می‌ماند که دیگر رخ نمی‌داد. علاوه‌براین وضعیت `HOSTUSB|DOWN` در Pro Micro می‌توانست پس از بازگشت Windows stale بماند و سیگنال UP خود Pico را بپوشاند. Tone موفق قبلی فقط دو نت کوتاه ۲۷۰ms بود.

### Change

- USB DOWN/UP در طول Delayهای Route After اکنون Poll و ثبت می‌شود.
- Evidence ثبت‌شده هنگام ورود به `wait-usb` حفظ می‌شود و در `after-complete` مقدار `down-seen` گزارش می‌شود.
- اگر Pro Micro روی DOWN قدیمی بماند، USB خود Pico پس از مشاهدهٔ DOWN واقعی یا Boot با Marker معتبر می‌تواند State را به UP ارتقا دهد.
- الگوی Save موفق به سه نت صعودی و واضح‌تر با مجموع حدود ۸۰۰ms تغییر کرد.
- `CAL|save-ok` و `CAL|save-failed` همراه Stage، Profile ID و منبع NVM در Debug پایدار ثبت می‌شوند.
- فشار کوتاه زرد نیز `calibration-start ... wait=5s` را ثبت می‌کند تا مشخص باشد Sample واقعاً آغاز شده است.

### Validation

- تست Regression سناریوی `DOWN → UP` حین After و اجرای Startup پس از دو ثانیه پاس شد.
- تست Boot با Marker، Deadline، پایان طبیعی Game و Desktop skip حفظ شد.
- Bundle 220 ارسالی ۳۲/۳۲ Hash صحیح و Dashboard برابر `13.3 ± 3.0 lux` داشت.
- `boot.py` عمداً `readonly=True` نگه داشته شد: این تنظیم CircuitPython را Read-only و مالکیت نوشتن FAT را به Windows می‌دهد؛ برگرداندن آن Host را Read-only می‌کند.

### Next test

1. بازه را ۳ تا ۶ دقیقه نگه دارید و چرخه را Start کنید.
2. پس از After باید `CYCLE|usb|state=DOWN|during=after` یا DOWN معمولی، سپس `state=UP` و `startup-in=2` دیده شود.
3. پس از بازگشت Windows، بدون فشار Start باید Route Startup اجرا و بعد `startup-complete|next=login-or-dc|desktop=skip` ثبت شود.
4. در Calibration Stage 3 زرد را کوتاه بزنید و پنج ثانیه صبر کنید؛ Success باید سه نت صعودی واضح بدهد. سپس آبی کوتاه باید Stage بعد و آبی بلند باید خروج را اعلام کند.

## Build 86 — بازخورد کالیبراسیون، Deadline چرخه و پروفایل همراه

**Previous build:** 85 / Classroom release 107
**Status:** CI candidate; calibration and timed-cycle hardware retest required
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

در کالیبراسیون فیزیکی Dashboard، اگر Sample ناپایدار می‌شد فقط رخداد CDC ثبت می‌شد و کاربر هیچ صدای خطایی نمی‌شنید؛ فشار دوبارهٔ زرد هنگام Sampling نیز از نظر صوتی ساکت بود. هم‌زمان حذف زمان‌بندی‌های قدیمی، بازهٔ لازم چرخهٔ ۱۱۰ تا ۱۳۰ دقیقه را نیز از UI و Runtime حذف کرده بود. فایل `light-state-profiles.json` ارسالی هم Dashboard قدیمی `15.8 ± 1.0` را داشت و همیشه داخل ZIP برنامه نبود.

### Root cause

Wrapper صوتی فقط مسیر موفق `result=dict` را پوشش می‌داد و انتقال `sampling → None` را نادیده می‌گرفت. در معماری Build 84 همهٔ Headerهای چرخه، از جمله `RUNFOR`، با Resume/PostLaunch قدیمی یکجا Strip شدند. پروفایل‌های پیش‌فرض نیز فقط در کد بودند و فایل قابل‌ویرایش پروفایل به Output پروژه اضافه نشده بود.

### Change

- Sample ناپایدار و فشار زرد هنگام Busy اکنون Tone خطا و Telemetry پایدار تولید می‌کنند؛ Save موفق همچنان Tone صعودی قبلی را دارد.
- کنترل بازهٔ چرخه به Play Options برگشت و پیش‌فرض آن ۱۱۰ تا ۱۳۰ دقیقه است.
- فقط `RUNFOR|min,max` در `plan.txt` مدرن حفظ می‌شود؛ `AUTORESUME` و `POSTLAUNCH` قدیمی همچنان حذف می‌شوند.
- Runtime در Start یک Deadline تصادفی از بازه انتخاب می‌کند؛ در انقضا Route را تمیز Abort می‌کند و پس از آزادشدن Parser، تب After را اجرا می‌کند.
- پایان طبیعی Game نیز همچنان بلافاصله After را اجرا می‌کند.
- فایل شش‌پروفایلی داخل همهٔ بسته‌های Classroom قرار می‌گیرد. Dashboard به مقدار سخت‌افزاری `13.3 ± 3.0 lux` به‌روزرسانی شد؛ سایر بازه‌ها حفظ شدند.

### Validation

- تست Deadline در کران ۱۱۰ دقیقه، پایان طبیعی Game، Marker، Startup و Desktop skip پاس شد.
- تست Tone و Telemetry برای Sample ناپایدار، Overlap retry، CAL2 و NVM migration پاس شد.
- Hashهای Bundle 220 مستقل بررسی شدند: هر ۳۲ فایل سالم بود؛ پروفایل قدیمی Dashboard در آن تأیید شد.
- تست قرارداد UI/Exporter تأیید کرد که RUNFOR حفظ و Headerهای بازنشسته حذف می‌شوند.
- قرارداد Windows برای موجودی Bundle به‌جای قفل‌شدن روی شمارش ثابت، حداقل ۳۶ فایل و حداقل ۳۲ Hash معتبر را کنترل می‌کند؛ قرارداد پروفایل نیز مقادیر پیش‌فرض سخت‌افزاری را مستقل از مسیر Output پروژهٔ تست می‌سنجد.
- پروفایل سخت‌افزاری جدید فقط در فایل همراه `light-state-profiles.json` نگه‌داری می‌شود؛ Seedهای داخلی Phase-four به‌عنوان Fallback اضطراری و برای حفظ قرارداد Classifier دست‌نخورده باقی ماندند.

### Next test

1. Build تازه را Extract کنید و وجود `light-state-profiles.json` با Dashboard برابر `13.3 ± 3.0` را بررسی کنید.
2. برای تست سریع، بازهٔ چرخه را موقتاً ۵ تا ۶ دقیقه تنظیم و Export کنید؛ `plan.txt` باید `RUNFOR|300,360` داشته باشد و پس از Deadline تب After اجرا شود.
3. در کالیبراسیون Stage 3، زرد را بزنید: Save موفق باید Tone صعودی بدهد؛ Sample ناپایدار یا فشار زرد هنگام Busy باید Tone خطا بدهد؛ آبی بلند باید با Tone خروج از Calibration خارج شود.

## Build 85 — ماندگاری کالیبراسیون فیزیکی بین Exportها

**Previous build:** 84 / Classroom release 104
**Status:** CI candidate; Dashboard hardware retest required
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

کالیبراسیون فیزیکی Dashboard با موفقیت Save می‌شد، اما پس از Export عادی پروژه، Runtime دوباره بازهٔ فایل (`14.8..16.8`) را استفاده می‌کرد و نور واقعی نزدیک `13.3` به `unknown` می‌رفت. مانیتور نیز فقط فایل JSON را نشان می‌داد و مشخص نبود منبع مؤثر File است یا NVM.

### Root cause

فرمت CAL1 Snapshot را به Revision دقیق Bundle متصل کرده بود. هر Export Revision را عوض می‌کرد و Loader، Snapshot سالم همان برد و همان Profile IDها را رد می‌کرد. همچنین مقدار اولیهٔ Debug برای State ناشناخته `None` بود و اولین `unknown` همیشه ثبت نمی‌شد.

### Change

- فرمت CAL2 کالیبراسیون را به برد و Profile IDها متصل نگه می‌دارد، نه Revision موقت Export.
- Snapshotهای CAL1 قدیمی بدون نیاز به کالیبراسیون مجدد خوانده و در Save بعدی به CAL2 مهاجرت می‌شوند.
- `CALSTATUS` اکنون `source=nvm|file` را گزارش می‌کند و Boot تعداد Profileهای بازیابی‌شده از NVM را ثبت می‌کند.
- اولین State ناشناخته بعد از Boot، Start و Startup Resume حتماً Telemetry می‌دهد.
- تغییر کاربر از Shuffle All به Random Subset مستقل از این Patch حفظ شده است.

### Validation

- تست استقلال Revision، مهاجرت CAL1، Checksum خراب و مرزهای رزروشدهٔ NVM پاس شد.
- قراردادهای Restart Cycle، Export، FAT isolation، Guard Start و Sound/Mouse پاس شدند؛ Windows TestRunner نیز رشد کنترل‌شدهٔ Entry Point را در سقف ۵۵KB تأیید می‌کند.
- Manifest همهٔ فایل‌های Runtime تغییرکرده را با SHA-256 جدید پوشش می‌دهد.

### Next test

1. Build را بدون پاک‌کردن NVM روی برد Export کنید؛ کالیبراسیون مجدد نباید لازم باشد.
2. در Boot باید `CAL|storage=nvm|loaded=...` و در `CALSTATUS` مقدار `source=nvm` دیده شود.
3. Start در Dashboard باید `STATE/character-dashboard` و Route مربوط را ثبت کند؛ اگر `source=file` بود، یک‌بار Stage 3 را Save کنید.

## Build 84 — چرخهٔ Route-driven After/Startup و مالکیت امن CIRCUITPY

**Previous build:** 83 / Classroom release 102
**Status:** CI candidate; feature-branch packaging enabled; staged hardware test required
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

چرخهٔ Build 83 یک Deadline مستقل `RUNFOR` داشت و پس از پایان آن مستقیماً توالی داخلی Restart را اجرا می‌کرد. در نتیجه مدت Game از استپ‌های خود Game جدا شده بود، تب Restart پروژه عملاً منبع After نبود و پس از بالا آمدن Windows نیز Resume با تأخیرهای قدیمی اجرا می‌شد. همچنین `boot.py` مالکیت نوشتن FAT را به CircuitPython داده بود و Windows درایو را Read-only می‌دید.

### Root cause

چرخهٔ مدرن هنوز مدل قدیمیِ زمان‌محور را منبع حقیقت می‌دانست و پایان واقعی Route بازی را به مرحلهٔ After متصل نمی‌کرد. Resume نیز به‌جای یک Route مستقل Startup، از تأخیرها و Launch قدیمی استفاده می‌کرد. سیاست `boot.py` نیز برای ذخیرهٔ JSON کالیبراسیون، مالکیت FAT را از Windows گرفته بود.

### Change

- زمان اجرا فقط داخل `game_steps.txt` و استپ‌هایی مانند `LOOPTIME` تعریف می‌شود؛ هیچ تایمر سراسری پیش از Restart وجود ندارد.
- با پایان موفق Game، `restart_steps.txt` با عنوان **After** فوراً اجرا می‌شود و Marker قبل از آن در NVM ثبت می‌گردد.
- تب و فایل مستقل `startup_steps.txt` اضافه شد؛ پس از Restart و USB پایدار دقیقاً یک‌بار اجرا می‌شود.
- پس از Startup، Stage روی Login تنظیم می‌شود؛ Desktop اجرا نمی‌شود و جریان از Login/DC ادامه می‌یابد.
- Resume Essentials، RUNFOR، AUTORESUME و POSTLAUNCH از UI و خروجی مدرن حذف شدند؛ تنظیم‌های قدیمی فقط برای سازگاری فایل تنظیمات باقی مانده‌اند.
- CIRCUITPY به Windows واگذار شد. کالیبراسیون نور در ناحیهٔ میانی NVM با Magic، طول، Checksum و اتصال به Revision پروژه ذخیره می‌شود؛ Debug در ۰..۱۵۳۵ و Marker در ۱۶ بایت انتهایی دست‌نخورده‌اند.
- ساختار After برای روش‌های بعدی Restart آماده است؛ فعلاً محتوای تب After اجرا می‌شود و روش Alt+F4 با Hold تصادفی ۸۸–۱۸۸ms در صف توسعه باقی می‌ماند.

### Validation

- تست چرخه پایان Game → After → USB Down/Up → Startup → Login و Desktop skip پاس شد.
- تست NVM شامل Checksum، Revision binding و عدم تداخل با Debug/Restart Marker پاس شد.
- Manifest مدرن ۳۲ فایل دارد و Startup/NVM module در Hash verification قرار گرفته‌اند.
- تست‌های UI، Export، FAT isolation، Guard transitions و Runtime sound/mouse contract پاس شدند.

### Next test

1. Game با `LOOPTIME` کوتاه تمام شود و لاگ بلافاصله `CYCLE|after-start` را نشان دهد.
2. پس از Restart، لاگ `CYCLE|usb|state=UP|startup-in=2`، سپس `startup-start` و `startup-complete|next=login-or-dc|desktop=skip` را نشان دهد.
3. در Windows، CIRCUITPY قابل‌نوشتن باشد و ذخیرهٔ کالیبراسیون رویداد `CAL|storage=nvm` ایجاد کند.

## Build 83 — Restart Cycle و Auto Resume واقعی در Runtime مدرن

**Previous build:** 82
**Status:** CI candidate; staged hardware test required
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

Exporter تنظیم‌های `RUNFOR|300,600`، `AUTORESUME|1,180,300` و `POSTLAUNCH|1,1,1,3,20,40` را درست داخل `plan.txt` می‌نوشت، اما Runtime مدرن هیچ Parser یا Schedulerای برای آن‌ها نداشت. پس از بیش از ده دقیقه Game هیچ Restart رخ نمی‌داد؛ بازگشت دستی به Desktop نیز چون Guard هنوز در Session قبلی Game بود با `desktop-only-valid-at-start` رد می‌شد.

### Root cause

پس از مهاجرت به Runtime کم‌حافظهٔ Guard، ماژول‌های چرخهٔ قدیمی در Export مدرن حذف شدند و فقط Directiveها باقی ماندند. همچنین رویدادهای `EVT|HOSTUSB|DOWN/SUSPEND/UP` در UART چاپ می‌شدند ولی State آن‌ها برای Auto Resume نگه‌داری نمی‌شد.

### Change

- Parser سبک و مستقل برای Headerهای Root بدون Import کردن Plan Engine کامل اضافه شد.
- Deadline در مسیرهای طولانی نیز از طریق Control Gate بررسی و Route جاری به‌صورت Fail-safe متوقف می‌شود.
- Restart طبیعی با توالی انسانی Windows اجرا می‌شود؛ Stop دستی هرگز Restart ایجاد نمی‌کند.
- پیش از Restart، Marker و شمارندهٔ حداکثر پنج Restart در انتهای NVM ذخیره می‌شود؛ 1536 بایت Debug دست‌نخورده می‌ماند.
- Auto Resume فقط پس از Marker معتبر و USB Down/Suspend سپس UP انجام می‌شود؛ Fallback داخلی Pico نیز حفظ شده است.
- پس از تأخیر ۳ تا ۵ دقیقه، `POSTLAUNCH` با Taskbar Slot تنظیم‌شده اجرا، Guard Reset و RUNFOR تازه مسلح می‌شود.
- Start دستی هنگام انتظار، Marker قبلی را لغو و یک Session تازه ایجاد می‌کند.
- پیام تکراری `duplicate-stable-state` فقط یک بار برای هر Reason/State ثبت می‌شود.

### Validation

- تست واحد Parser، NVM Marker، Deadline، Boot Marker، USB Resume و Manual Override را کنترل می‌کند.
- قرارداد Export وجود دو ماژول Restart، HOSTUSB State و 30 Hash معتبر را بررسی می‌کند.
- تست‌های S4 Sound، Mouse، Heap calibration، FAT isolation و Game re-entry بدون تغییر باید پاس شوند.

### Next test

1. ابتدا با `RUNFOR|60,90` و `AUTORESUME|1,20,30` تست کوتاه انجام شود.
2. لاگ باید `CYCLE|armed`، `CYCLE|deadline`، `CYCLE|restart-sent`، `CYCLE|usb|state=DOWN/UP`، `CYCLE|postlaunch` و `CYCLE|resumed` را نشان دهد.
3. پس از تأیید، تنظیم واقعی ۵ تا ۱۰ دقیقه و Auto Resume سه تا پنج دقیقه دوباره Export شود.

## Build 82 — پوشش نمونه‌های نامتقارن کالیبراسیون نور

**Previous build:** 81
**Status:** CI candidate; Dashboard hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

Dashboard طی نمونه‌برداری `center=13.3` و `spread=2.5` داشت، اما `tolerance=1.0` ذخیره شد. نور پایدار بعدی `15.8` بود؛ بنابراین بازهٔ 12.3 تا 14.3 آن را نپذیرفت و Guard با وجود Save موفق، `STATE/unknown` گزارش کرد.

### Root cause

فرمول قبلی نصف فاصلهٔ P10 تا P90 را به‌عنوان Tolerance دور Median قرار می‌داد. این فقط وقتی صحیح است که Median دقیقاً وسط دو Quantile باشد. در توزیع نامتقارن Dashboard، Median نزدیک لبهٔ پایین بود و نیمهٔ بالایی دامنه حذف شد.

### Change

- Envelope مقاوم از P5/P95 ساخته می‌شود تا نویز منفرد حذف، اما تغییر تکرارشونده حفظ شود.
- Tolerance برابر بیشترین فاصلهٔ `Median→P5` یا `Median→P95` به‌علاوهٔ Margin است.
- Cap پروفایل مجاور و بررسی Overlap بدون تغییر باقی مانده‌اند؛ بازه نمی‌تواند وارد Game یا Targeted شود.

### Validation

- Regression واقعی `18×13.3 + 2×15.8` باید Center برابر 13.3 و Tolerance حداقل 3.0 بسازد.
- مقدار 15.8 باید داخل پروفایل باشد و `find_profile_overlap` همچنان هیچ همپوشانی جدیدی نپذیرد.
- تست Outlier قدیمی Game باید همچنان Tolerance محدود 1.0 تا 1.5 داشته باشد.

### Next test

فقط Stage 3 یعنی Character Dashboard را دوباره نمونه‌برداری و Save کنید. پس از خروج از Calibration، Start باید در همان محیط `STATE/character-dashboard` و سپس Route مربوط را ثبت کند.

## Build 81 — همگام‌سازی Runtime داخلی Classroom با S4

**Previous build:** 80
**Status:** CI candidate; Classroom export hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

ARM 2.8.2-S4 همراه با Pico Bundle 203 هماهنگ، Catch را در تست سخت‌افزاری انجام داد؛ اما آخرین Classroom Studio منتشرشده پیش از Merge شدن S4 ساخته شده بود. بنابراین Export دوبارهٔ همان پروژه با Release قدیمی، Runtime قبلی Pico را روی برد می‌نوشت و Catch از کار می‌افتاد.

### Root cause

تنظیم‌های Threshold/Minimum داخل AMSJ منتقل می‌شوند، ولی پروتکل Async Sound و پردازش `EVT|ASND` بخشی از Runtime داخلی Classroom هستند. Release عمومی موجود مربوط به قبل از Commit پایدار S4 بود؛ در نتیجه فایل پروژه به‌تنهایی نمی‌توانست Runtime را ارتقا دهد.

### Change

- Release جدید Classroom مستقیماً از شاخهٔ پایدار دارای S4 ساخته می‌شود.
- قرارداد TestRunner اکنون روی فایل واقعاً Export‌شده، وجود Start پروتکل ASND، تشخیص، Timeout و Telemetry `mode=async` را کنترل می‌کند.
- منطق Mouse، ADC، Threshold و Route تغییر نکرده است؛ این Build فقط همگام‌سازی و جلوگیری از بازگشت بسته‌بندی قدیمی است.

### Validation

- TestRunner باید ثابت کند `combined_guard_runtime.py` موجود در خروجی Classroom قرارداد کامل S4 را دارد.
- معیار تست سخت‌افزاری: با ARM 2.8.2-S4 و Export مستقیم پروژه از Classroom جدید، Threshold 76 و Minimum 20ms بدون کپی دستی Bundle Catch کند.

### Next test

پس از نصب Classroom جدید، همان پروژهٔ `p-updated-v3-fishing-parallel-natural-v1-S4-sound76-20.amsj` را مستقیم Export کنید و یک چرخهٔ Catch را بدون جایگزینی دستی فایل‌های Pico اجرا کنید.


## Build 80 — ADC پیوسته و Peak-Latch برای Sound موازی

**Previous build:** 79  
**Status:** CI candidate; hardware retest pending  
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

تستر Blocking با SCAL برای چلپ واقعی Peak حدود 143–145 و برای سکوت حداکثر 6 ثبت کرد، اما Listener موازی ASND در Route ماهیگیری فقط Peakهای 12–22 می‌دید و تقریباً از هر سه یا چهار چلپ فقط یکی را Catch می‌کرد. پایین‌آوردن Threshold از مقدار کالیبرهٔ حدود 76 به 12 فقط Workaround بود و نشان می‌داد دو مسیر اندازه‌گیری مقیاس یکسانی ندارند.

### Root cause

Build 77 برای بازیابی نرمی ARM 2.8.1، `ARM_SOUND_TICK()` را به‌درستی از حلقهٔ Micro-step موس حذف کرد، اما ASND پس از آن فقط یک `analogRead` بین فرمان‌های MMOVE انجام می‌داد. هنگام اجرای DDA، ADC عملاً گوش نمی‌داد و بیشتر قلهٔ کوتاه چلپ از دست می‌رفت؛ در مقابل SCAL در پنجره‌های 10ms صدها نمونه می‌گرفت و Peak واقعی را می‌دید.

### Change

- ARM 2.8.2-S4 ADC سخت‌افزاری ATmega32U4 را فقط هنگام ASND در حالت Free-Running فعال می‌کند.
- ISR سبک، پنجره‌های تقریباً 10ms با 96 نمونه می‌سازد؛ Peak، Sustained Duration و بیشینهٔ کل را بدون `analogRead` داخل Micro-step نگه می‌دارد.
- Cadence یک‌میلی‌ثانیه‌ای، DDA و Micro-step سه‌پیکسلی S3 دست‌نخورده‌اند.
- رویدادهای Async اکنون Peak واقعی را گزارش می‌کنند و Pico شروع/نتیجهٔ Listener موازی را در GuardHardwareMonitor ثبت می‌کند.
- HALT، Cancel، Detect و Timeout همگی ADC Interrupt را خاموش می‌کنند.

### Validation

- قرارداد Firmware وجود ADC Free-Running، ISR، پنجرهٔ 96 نمونه‌ای و نبود `ARM_SOUND_TICK()`/`analogRead` در حلقهٔ Mouse را کنترل می‌کند.
- Runtime قالب جدید `EVT|ASND|DETECTED|peak=...` و `TIMEOUT|peak=...` را می‌پذیرد و جزئیات را به لاگ Guard منتقل می‌کند.
- معیار تست سخت‌افزاری: Threshold کالیبرهٔ حدود 76 با Peak نزدیک 143 کار کند، Catch چندباره پایدار باشد و نرمی Mouse نسبت به S3 افت نکند.

## Build 79 — Peak واقعی WSND و Timeout غیرخطایی

**Previous build:** 78  
**Status:** CI green and merged; sound hardware retest pending  
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

تست SCAL صدای بازی را تا Peak 129 می‌دید، اما WSND با Thresholdهای 12، 30 و 68 همگی Timeout می‌شد. Threshold یک فوراً Route را کامل و کلید F را اجرا کرد. پاسخ `ERR|TIMEOUT|WSND` نیز به‌اشتباه کل Guard را Fail می‌کرد.

### Root cause

Listener قدیمی بیشترین دامنهٔ مشاهده‌شده را نگه نمی‌داشت؛ بنابراین اختلاف SCAL و WSND قابل اندازه‌گیری نبود. همچنین لایهٔ UART تمام پاسخ‌های `ERR`، از جمله Timeout عادی WSND، را Exception بحرانی تلقی می‌کرد.

### Change

- Listener مسدودکنندهٔ WSND بیشترین Peak واقعی همان بازه را نگه می‌دارد.
- تشخیص با `OK|WSND|DETECTED|peak=...` و Timeout با `ERR|TIMEOUT|WSND|max=...` گزارش می‌شود.
- Pico فقط Timeout همین فرمان را نتیجهٔ عادی `False` می‌داند؛ همهٔ خطاهای دیگر ARM همچنان Fail-Closed هستند.
- پیش از Listener، ID، منبع `calibrated/default`، Threshold، Minimum و Timeout مؤثر ثبت می‌شود.
- ARM به 2.8.2-S3 ارتقا یافت؛ `mouse_move_steps`، DDA، Micro-step و Cadence یک‌میلی‌ثانیه‌ای تغییر نکرده‌اند.

### Validation

- Python runtime با `py_compile` معتبر است.
- قراردادها قالب Peak، رفتار Timeout و نبود Sound sampling داخل حلقهٔ Micro-step را قفل می‌کنند.
- کامپایل Leonardo، تست Exhaustive سه‌پیکسلی و بستهٔ Windows به CI سپرده می‌شوند.

### Next test

Firmware ARM 2.8.2-S3 و Bundle جدید را نصب کنید. تست Game را با Threshold 12/20ms اجرا کنید؛ در صورت Timeout، مقدار `max=...` دقیقاً دامنهٔ دیده‌شده داخل WSND را نشان می‌دهد. اگر صدا تشخیص داده شود، `peak=...` ثبت و F اجرا می‌شود.

## Build 78 — حذف توقف ۵۰ms هنگام بازبودن COM

**Previous build:** 77
**Status:** Local candidate; hardware record analyzed; CI and hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

- Record سخت‌افزاری Build 95 شامل 2,230 موقعیت و 2,229 Segment بود.
- فاصلهٔ فعال میانه 51ms، صدک 95 برابر 53ms و 1,194 فاصلهٔ حداقل 40ms ثبت شد.
- Baseline نرم Natural Mouse v1 روی Recordهای قبلی میانهٔ 15–16ms و فقط 1–2 فاصلهٔ حداقل 40ms داشت؛ بنابراین شکستگی گزارش‌شده واقعی و شدید است.

### Root cause

- حلقهٔ ARM 2.8.2-S1 در حالت Session امن‌نشده، صرفاً با بازبودن USB CDC وارد `do_handshake(40)` می‌شد؛ حتی وقتی هیچ HELLO یا بایتی در COM وجود نداشت.
- بازبودن COM توسط Arduino IDE، Serial Monitor، Classroom یا هر Scanner می‌توانست بین فرمان‌های Mouse یک انتظار حدود 40–52ms تزریق کند.
- الگوی ثبت‌شدهٔ غالب 52ms در برابر Burstهای 1ms دقیقاً با همین Timeout منطبق است.

### Change

- Firmware به `ARM 2.8.2-S2` ارتقا یافت.
- Secure USB handshake فقط وقتی اجرا می‌شود که `Serial.available()` بایت واقعی گزارش کند؛ بازبودن سادهٔ DTR/COM مسیر Serial1 و HID را متوقف نمی‌کند.
- Async Sound، DDA، Pace یک‌میلی‌ثانیه‌ای، سقف سه‌پیکسلی، Checksum، Fail-Closed و Keyboard روی Pico تغییر نکرده‌اند.

### Validation

- قرارداد Firmware وجود شرط `Serial.available()` و نبود شرط Blocking قدیمی `if(Serial)` را قفل می‌کند.
- تست Exhaustive Endpoint و سقف سه‌پیکسلی با نسخهٔ S2 حفظ می‌شود.
- Record کاربر مستقلاً Parse شد و شمارش 1,194 وقفهٔ فعال حداقل 40ms از دو مسیر محاسبه یکسان بود.

### Next test

ARM 2.8.2-S2 را روی Pro Micro فلش کنید. Classroom و Arduino Serial Monitor می‌توانند باز بمانند؛ بازبودن COM نباید دیگر حرکت را خراب کند. همان `mouse-tune-C-balanced.amsj` را با Bundle تازه اجرا و Record را ارسال کنید. معیار پذیرش: حذف قلهٔ 51–53ms، بازگشت فاصله‌ها نزدیک Baseline 15–20ms، حداکثر Micro-step سه پیکسل و Route کامل.

## Build 77 — بازیابی نرمی موس و فاصلهٔ واقعی Typo

**Previous build:** 76
**Status:** Local candidate; focused contracts passed; Windows CI and hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

- پس از ARM 2.8.2 حرکت موس نسبت به Baseline سخت‌افزاری Natural Mouse v1 شکسته و خوشه‌ای حس شد.
- Record نمونه، خوشه‌های 1 تا 3 میلی‌ثانیه‌ای را پس از مکث نشان داد؛ تنظیم‌های بلند `idle` بین حرکت‌ها جدا هستند و علت Regression داخل حرکت نیستند.
- تنظیم `typoEveryMin=7` و `typoEveryMax=12` به `typos=7,12` صادر می‌شد. Runtime آن را 7 تا 12 خطا در کل TYPE می‌خواند؛ برای متن 12 کاراکتری تقریباً هر کاراکتر غلط و Backspace می‌شد.

### Root cause

- ARM 2.8.2 تابع `analogRead()` پایش Async Sound را بین تک‌تک Micro-stepهای HID اجرا می‌کرد. این کار مسیر Cadence تأییدشدهٔ ARM 2.8.1 را تغییر داد.
- نام‌های ذخیره‌شدهٔ `typoEveryMin/Max` فاصله را القا می‌کردند، اما Exporter و Runtime آن‌ها را به تعداد کل خطا تغییر داده بودند.

### Change

- ARM 2.8.2-S1 حلقهٔ DDA/Micro-step را به Cadence دقیق 2.8.1 برمی‌گرداند؛ هیچ ADC یا منطق صدا داخل حلقهٔ HID اجرا نمی‌شود.
- پایش Async Sound بین فرمان‌های MMOVE در حلقهٔ اصلی ARM ادامه دارد. با Stream حدود 8ms، تشخیص صدا همچنان سریع و غیرمسدودکننده است، بدون تزریق کار متغیر میان گزارش‌های HID.
- HVER نسخهٔ `2.8.2-S1` را اعلام می‌کند تا فریمور اصلاح‌شده با 2.8.2 قبلی اشتباه نشود.
- Exporter جدید `typochars=min,max` می‌نویسد. Runtime، Login runner، Parallel planner، Split planner و اجرای مستقیم Classroom فاصلهٔ کاراکترهای واجد شرایط را پس از هر اصلاح دوباره قرعه‌کشی می‌کنند.
- `typos=min,max` قدیمی به‌عنوان Count mode و `typo=min,max` قدیمی به‌عنوان فاصلهٔ کلمه‌ای فقط برای سازگاری Planهای قدیمی قابل خواندن می‌مانند.
- متن رابط فارسی و انگلیسی صریحاً «فاصلهٔ بین خطاها برحسب کاراکتر» را نمایش می‌دهد.

### Validation

- قرارداد Firmware ثابت می‌کند `ARM_SOUND_TICK()` داخل `mouse_move_steps` وجود ندارد، ولی Async Sound در مرز فرمان‌ها فعال است.
- تست Exhaustive تمام Deltaهای `-127..127` حفظ Endpoint دقیق و سقف سه‌پیکسلی را تأیید می‌کند.
- تست Typo روی 40 Seed ثابت می‌کند مقدار 7–12 برای متن `zodiak999999` دقیقاً یک اصلاح می‌سازد و متن نهایی صحیح می‌ماند.
- بازهٔ 7–12 روی متن 26 کاراکتری در Seedهای مختلف دو یا سه اصلاح با فاصلهٔ دوباره‌قرعه‌کشی‌شده می‌سازد.
- قراردادهای Legacy Count و Word cadence همچنان پاس می‌شوند.

### Next test

پس از انتشار، ARM 2.8.2-S1 را روی Pro Micro فلش کنید و Build جدید را در پوشه‌ای تازه اجرا کنید. ابتدا همان مسیر موس قبلی را بدون تغییر تنظیم‌ها Record بگیرید؛ نرمی باید به Baseline Natural Mouse v1 برگردد. سپس TYPE با بازهٔ 7 تا 12 را اجرا کنید؛ روی متن 12 کاراکتری باید فقط یک خطای اصلاح‌شونده دیده شود.

## Build 76 — جداسازی FAT و تأیید واقعی Export

**Previous build:** 75
**Status:** Local candidate; focused contracts pending Windows CI and hardware retest
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

- Bundle 175 از نظر ZIP/CRC سالم بود، اما <code>guard-calibration.json</code> با خطوط <code>STATE|denied</code> و <code>guard-transition.json</code> با رویدادهای GP3/GP4 و دادهٔ باینری جایگزین شده بودند.
- SHA واقعی این دو فایل با Manifest متفاوت بود؛ بنابراین Pico هیچ پروفایل معتبر Game برای تشخیص نداشت.
- دکمه‌های کالیبراسیون پیش از پخش Note، Debug را هم روی FAT و هم با نوشتن کامل 1536 بایت NVM Persist می‌کردند و پاسخ حدود یک ثانیه دیر حس می‌شد.

### Root cause

- <code>boot.py</code> برای ذخیرهٔ کالیبراسیون، CIRCUITPY را Writable نگه می‌دارد. Runtime هم‌زمان <code>guard-debug.log</code> را روی همان FAT بازنویسی می‌کرد و Classroom از USB Mass Storage فایل‌های Bundle را جایگزین می‌کرد؛ این مالکیت هم‌زمان باعث Cross-link شدن Sectorها شد.
- Export فقط موفقیت <code>File.Copy</code> را کنترل می‌کرد و بایت‌های نهایی روی درایو، Hashها یا Revision دو Guard JSON را دوباره نمی‌خواند.
- GP4 down/long/up قبل از Action با <code>persist=True</code> مسیر ذخیرهٔ Blocking را اجرا می‌کرد.

### Change

- Debug پایدار Runtime فقط در NVM نگهداری می‌شود و رویداد زنده همچنان از CDC به GuardHardwareMonitor می‌رسد؛ Runtime دیگر هیچ فایل Debug روی CIRCUITPY نمی‌نویسد.
- GP3/GP4 down، up و long فقط Live event هستند و پیش از Note یا Action ذخیرهٔ Blocking ندارند.
- Classroom پیش از Export فرمان <code>HALT|SILENT</code> می‌فرستد و Bridge را می‌بندد.
- پس از کپی، Classroom تا پنج بار 28 فایل Manifest را مستقیماً از CIRCUITPY می‌خواند و SHA-256 هر فایل را کنترل می‌کند.
- دو Guard JSON نیز Parse می‌شوند؛ Revision مشترک و وجود شش پروفایل بررسی می‌شود.
- در هر mismatch، Export موفق اعلام نمی‌شود و پیام بررسی/Reset فایل‌سیستم نمایش داده می‌شود.

### Validation

- تست رگرسیون، Guard JSON دارای متن Debug را عمداً تزریق و رد شدن Read-back را تأیید می‌کند.
- قرارداد Firmware نبودن <code>_DEBUG_FILE</code>، نبودن Remount در مسیر Debug و Live-only بودن رویدادهای فیزیکی را کنترل می‌کند.
- قرارداد Export وجود Quiesce، پنج Retry و پیام <code>28/28 hashes and Guard revisions OK</code> را قفل می‌کند.

### Next test

Build منتشرشده را در پوشهٔ تازه اجرا کنید. Bundle 175 معتبر نیست. Pico را Reset کنید و پروژه را دوباره Export کنید؛ Classroom فقط پس از پیام <code>28/28 hashes and Guard revisions OK</code> باید موفقیت نشان دهد. سپس Start در Game، ورود/انتخاب Calibration و سرعت Note دکمه‌ها را آزمایش و Bundle و Guard log جدید را ارسال کنید.

## Build 75 — بازیابی Game/Target و اتصال بدون بستن Classroom

**Previous build:** 74
**Status:** Local candidate; focused portable tests passed; Windows CI and hardware test pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

- لاگ سخت‌افزاری نشان داد Game در `24.2 lux` درست تشخیص داده و Route کامل شد؛ یک جهش کوتاه به `28.3` حالت را Unknown کرد و بازگشت به `25.0` با `game-not-expected` و سپس `duplicate-stable-state` برای همیشه رد شد.
- کالیبراسیون Game یا Targeted با وجود جدایی پروفایل‌های ذخیره‌شده، به‌علت Tolerance بزرگ‌شده پیام `CAL|OVERLAP` می‌داد.
- پس از `Write timeout` رابط کاربری Disconnected می‌شد، ولی Python sidecar و Serial link قبلی می‌توانستند COM31 را باز نگه دارند. Connect بعدی Pico را پیدا نمی‌کرد، به COM30 می‌افتاد و در ZIP پرتابل به‌اشتباه `ams_key.json` می‌خواست.

### Root cause

- Transition فقط ورود Game از Stage 4 یا بازگشت صریح از Targeted را قبول می‌کرد؛ بازگشت طبیعی `Game → Unknown → Game` در Stage 5 تعریف نشده بود.
- کالیبراسیون از `max-min` و `1.5 × spread` استفاده می‌کرد؛ دو Outlier می‌توانستند بازه‌ای چند برابر دامنهٔ واقعی بسازند و آن را وارد Targeted کنند.
- خطای Send فقط State رابط را عوض می‌کرد. نه لینک Python پاک می‌شد و نه Sidecar C# الزاماً Kill می‌شد؛ بنابراین Retry همان Process خراب و COM handle قبلی را دوباره استفاده می‌کرد.

### Change

- Stage 5 اکنون بازگشت پایدار Game بعد از Unknown را به‌صورت معتبر ولی بدون اجرای دوبارهٔ `game_steps.txt` می‌پذیرد (`game-reentry-after-unknown`).
- Tolerance کالیبراسیون از 80٪ مرکزی نمونه‌ها محاسبه می‌شود، Outlierهای ابتدا/انتها را کنار می‌گذارد و در مرز نزدیک‌ترین پروفایل ذخیره‌شده Cap می‌شود. کنترل مثبت همپوشانی همچنان Fail-Closed باقی مانده است.
- قبل از Connect هر Serial link قدیمی بسته می‌شود؛ خطای Connect/Send/Path لینک را پاک و رویداد Disconnected صادر می‌کند.
- C# روی خطای Transport، Sidecar خراب را Kill و Port/Firmware را پاک می‌کند تا Connect بعدی Process تمیز بسازد.
- ZIP پرتابل بدون کلید خصوصی دیگر به مسیر مستقیم COM30 سقوط نمی‌کند؛ اگر Pico Brain پیدا نشود خطای واضح `Pico brain not found` می‌دهد.

### Validation

- رگرسیون `Game → Unknown → Game` ثابت می‌کند Stage 5 حفظ می‌شود و Macro دوباره اجرا نمی‌شود.
- رگرسیون `Game → Targeted → Game` بدون تغییر پاس می‌شود.
- نمونه‌های Game دارای Outlier دیگر Tolerance مصنوعی بزرگ تولید نمی‌کنند و با Targeted همپوشانی ندارند.
- قراردادهای Cleanup لینک، Kill Sidecar و ممنوعیت fallback بدون کلید اضافه شدند.
- تست‌های متمرکز Transition، Calibration، Overlap، Retry، Start-current و Brain-first محلی پاس شدند.
- TestRunner کامل ویندوز به CI سپرده می‌شود؛ محیط محلی Linux ابزار `dotnet` ندارد.

### Next test

پس از انتشار Build، ابتدا در Game با نور پایدار Start بزنید، سپس نور را موقتاً بیرون بازه ببرید و به Game برگردانید؛ باید `game-reentry-after-unknown` ثبت شود و `game_steps.txt` دوباره اجرا نشود. Game و Targeted را جداگانه کالیبره کنید و مقادیر `center/spread/tolerance` را بفرستید. در پایان کابل یا Transport را یک‌بار هنگام اتصال مختل کنید؛ Connect بعدی باید بدون بستن Classroom، COM31 و `role=brain` را دوباره پیدا کند.

## Build 74 — کالیبراسیون پرتابل دو Step صوتی و اتصال Brain-first Classroom

**Previous build:** 73
**Status:** PR candidate; portable contracts passed; Windows CI and hardware test pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

- کالیبراسیون دو Step صوتی باید مستقل از کامپیوتر و مستقیماً با GP3/GP4 روی Pico انجام شود، اما شاخهٔ اولیه فایل اجرایی `sound_step_calibration.py` را فقط در Manifest و Exporter نام برده بود و خود فایل وجود نداشت.
- Classroom با انتخاب ذخیره‌شدهٔ COM30 ابتدا Pro Micro را باز می‌کرد و بلافاصله سراغ اتصال مستقیم رمزشده می‌رفت؛ در ZIP پرتابل که عمداً `ams_key.json` خصوصی ندارد، اتصال با `No such file ... bridge/ams_key.json` قطع می‌شد، با اینکه Pico Brain روی COM31 حاضر بود.

### Root cause

- قرارداد `WSNDP`، UI، Parser و Runnerها اضافه شده بودند، ولی ماژول Lazy مسئول نمونه‌گیری، Binding، Checksum، Backup و ذخیرهٔ اتمیک به Repository افزوده نشده بود.
- `detect_board_port()` با دیدن اولین پاسخ عمومی `OK/HELLO/PONG` اسکن را تمام می‌کرد؛ بنابراین پورت مستقیم COM30 می‌توانست قبل از رسیدن اسکن به پاسخ `role=brain` روی COM31 انتخاب شود. `open_link()` نیز بعد از شکست Pico روی پورت دستی، پورت‌های دیگر را برای Brain جست‌وجو نمی‌کرد.

### Change

- در تنظیمات `Wait For Sound` فیلد `Calibration ID` با دو مقدار 1 و 2 اضافه شد و Export استفادهٔ تکراری هر ID را در همهٔ تب‌ها رد می‌کند.
- Route فرمان `WSNDP|id,binding,defaultThreshold,defaultMin,timeout` تولید می‌کند؛ Binding دوازده‌رقمی از همان Step ساخته می‌شود تا Calibration قدیمی روی Step نامرتبط اعمال نشود.
- `sound_step_calibration.py` کامل شد: کشف Bindingها از Routeها، سه ثانیه سکوت، سی ثانیه صدای هدف، Threshold میانه، `minDurationMs=20`، جداسازی حداقلی سیگنال، دو پروفایل مستقل و خطاهای Fail-Closed.
- فایل کاربر `sound-step-calibration.json` دارای SHA-256 داخلی است؛ نوشتن با Temp، Readback و Backup انجام می‌شود و خرابی فایل اصلی به Backup معتبر برمی‌گردد. فایل کاربر عضو Manifest ثابت نیست و Export بعدی آن را جایگزین نمی‌کند.
- ماژول کالیبراسیون Lazy است و پس از Resolve یا خروج موفق از حافظه آزاد می‌شود تا با Plan engine روی Heap محدود Pico هم‌زمان نماند.
- اسکن Classroom اکنون پاسخ عمومی COM30 را فقط fallback نگه می‌دارد و جست‌وجو را تا یافتن `role=brain` ادامه می‌دهد. اگر پورت دستی Pico نباشد، پیش از نیاز به کلید خصوصی یک اسکن Brain-first انجام و در صورت وجود به COM31 سوییچ می‌کند.

### Hardware controls

- فقط در حالت Stop، GP3 زرد بلند: ورود یا ذخیره و خروج از Sound Calibration.
- GP4 آبی کوتاه: جابه‌جایی بین ID 1 و ID 2.
- GP3 زرد کوتاه: شروع سه ثانیه سکوت و سپس سی ثانیه صدای هدف برای ID انتخاب‌شده.
- خروج هنگام Sample نتیجهٔ ناقص را دور می‌ریزد؛ ذخیرهٔ نامعتبر مقدار قبلی را حفظ می‌کند.

### Validation

- هر 40 قرارداد Portable و همهٔ `py_compile`ها محلی پاس شدند.
- تست رفتاری COM30/COM31 ثابت می‌کند پاسخ مستقیم COM30 دیگر جلوی کشف Pico Brain روی COM31 را نمی‌گیرد.
- تست‌های جدید دو ID، محاسبه Threshold، Binding mismatch، Checksum، ذخیره و بازیابی Backup را پوشش می‌دهند.
- همهٔ 28 فایل Manifest وجود دارند و SHA-256 آن‌ها با بایت‌های فعلی برابر است.
- Build و TestRunner ویندوز به CI سپرده می‌شود؛ محیط محلی Linux ابزار `dotnet` ندارد.

### Next test

Build منتشرشده را در پوشه‌ای تازه Extract کنید. حتی اگر تنظیم قبلی COM30 است، Connect باید مرحلهٔ `pico_fallback` و اتصال به Pico Brain روی COM31 را نشان دهد و نباید `ams_key.json` بخواهد. سپس پروژه را کامل روی CIRCUITPY Export کنید؛ برای Step چلپ ID 1 و برای Step Whisper ID 2 بگذارید. در حالت Stop با GP3 بلند وارد شوید، هر ID را با GP4 انتخاب و با GP3 کوتاه نمونه‌برداری کنید؛ پس از `mode=complete` برای هر دو ID، GP3 را نگه دارید تا `mode=saved|count=2` و خروج ثبت شود. سپس هر دو Route جداگانه آزمایش شوند.

## Build 73 — انتقال پروفایل‌های نور Classroom به Pico

**Previous build:** 72
**Status:** CI candidate; Guard hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

صفحهٔ وضعیت Classroom محیط Game را با پروفایل ذخیره‌شدهٔ کاربر (`25.8 ± 0.5`) و اطمینان 100٪ تشخیص می‌داد، اما Bundle روی Pico همچنان مقادیر ثابت Template (`22.5 ± 3.7` و سایر پروفایل‌های قدیمی) را داشت. بنابراین نمایش Classroom و تصمیم Guard از دو منبع متفاوت استفاده می‌کردند.

### Root cause

`ExportCurrentProject` فقط Routeها، Plan و Snapshot پروژهٔ باز را جایگزین می‌کرد. فایل‌های `guard-calibration.json` و `guard-transition.json` بدون تغییر از پوشهٔ `portable-modern-runtime` کپی می‌شدند و `light-state-profiles.json` هیچ‌گاه وارد قرارداد Pico نمی‌شد.

### Change

- Export مدرن هر شش پروفایل فعال و معتبر فعلی Classroom را دریافت می‌کند.
- Center، Tolerance و StableDuration در هر دو قرارداد `guard-calibration.json` و `guard-transition.json` نوشته می‌شوند.
- Revision جدید مشترک تولید می‌شود تا Loader اختلاف دو فایل را Fail-Closed تشخیص دهد.
- SHA256 هر دو فایل پس از تولید نهایی در Manifest بازسازی می‌شود.
- اگر یکی از شش پروفایل حذف، غیرفعال یا نامعتبر باشد، Export قبل از نوشتن روی Pico متوقف می‌شود.

### Validation

- تست رگرسیون مقادیر واقعی Game برابر `25.8 ± 0.5` را در هر دو قرارداد و Revision مشترک کنترل می‌کند.
- تست Manifest، Hash نهایی هر دو فایل پروفایل را با بایت‌های Exportشده تطبیق می‌دهد.

### Next test

با Classroom جدید پروژه را دوباره روی CIRCUITPY خروجی بگیرید. سپس روی درایو بررسی کنید `guard-calibration.json` برای Game مقدار `25.8` و `0.5` دارد. پس از Reboot، GP4 را در Game بزنید؛ Guard باید Start-at-current-state و سپس `ROUTE/start game_steps.txt` ثبت کند.

## Build 72 — کالیبراسیون واقعی صدا از مسیر Pico → Pro Micro

**Previous build:** 71
**Status:** CI candidate; sound hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

لاگ واقعی نشان داد اتصال Bridge سالم بود، اما Pico برای هر دو فرمان `SCAL` و `WSND` پاسخ `ERR|UNKNOWN` می‌داد. Classroom این پاسخ نامعتبر را به‌اشتباه «quiet» تلقی کرد و Binary Search همیشه به سقف 200 و Threshold ثابت 300 رسید. هم‌زمان ACK دوره‌ای `OK|CURSOR` هر 250ms لاگ اپراتور را پر می‌کرد.

### Root cause

سنسور صدا روی Pro Micro است، اما Classroom به COM31 و Pico Brain متصل می‌شود. Host command handler پیکو فقط فرمان‌هایی مانند `PING/LUX/CURSOR` را می‌پذیرفت و `SCAL/WSND` را به UART خصوصی Pro Micro منتقل نمی‌کرد. کد کالیبراسیون نیز `ERR|UNKNOWN` را از Timeout واقعی تفکیک نمی‌کرد.

### Change

- Pico اکنون `SCAL` و `WSND` را با Timeout محدود به Pro Micro Proxy می‌کند و پاسخ واقعی را به Classroom برمی‌گرداند.
- Classroom فقط `ERR|TIMEOUT|WSND` را Quiet معتبر می‌داند؛ `UNKNOWN/EXEC` دیگر Threshold 300 تولید نمی‌کند.
- هنگام Bundle قدیمی، پیام روشن برای خروجی‌گرفتن مجدد روی CIRCUITPY نمایش داده می‌شود.
- ACK موفق `OK|CURSOR` از Serial Log مخفی شد؛ خطاهای Cursor همچنان ثبت می‌شوند.
- ARM 2.8.2، الگوریتم سنسور، Async Sound و Natural Mouse تغییر نکرده‌اند.

### Validation

- TestRunner قرارداد Proxy هر دو فرمان، تفکیک Timeout از Unknown و فیلتر Cursor ACK را قفل می‌کند.
- `code.py` با `py_compile` بررسی و SHA256 آن در Manifest بازسازی شد.

### Next test

با Classroom جدید پروژهٔ فعلی را دوباره روی CIRCUITPY خروجی بگیرید؛ صرفاً تعویض EXE کافی نیست. پس از Reboot و Connect، دکمهٔ کالیبره را در سکوت بزنید. پاسخ باید `OK|SCAL|avg=...|max=...` باشد و عدد Threshold از اندازه‌گیری واقعی بیاید.

## Build 71 — اتصال واقعی برای تست و کالیبراسیون سنسور صدا

**Previous build:** 70
**Status:** CI candidate; sound hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

Classroom Studio در نوار وضعیت اتصال سبز نشان می‌داد، اما Bridge داخلی قبلاً قطع شده بود. کالیبراسیون `SCAL` با `Bridge is not connected` شکست می‌خورد و سپس همهٔ Probeهای `WSND` نیز همان لحظه و بدون تماس با سنسور به‌صورت `quiet` ثبت می‌شدند. این خطا مربوط به سنسور یا آستانهٔ 90 نبود.

### Root cause

`PythonBoardBridge` وضعیت واقعی را به `Disconnected` تغییر می‌داد، ولی `MainViewModel` به رویداد `StateChanged` متصل نبود. شرط اولیهٔ کالیبراسیون نیز فقط وضعیت نمایشی UI را کنترل می‌کرد، نه وضعیت واقعی Bridge.

### Change

- UI به `IBoardBridge.StateChanged` متصل شد و با قطع Sidecar/COM فوراً به حالت Connect برمی‌گردد.
- چراغ‌های Pico/Pro Micro و Cursor Sync هنگام قطع واقعی پاک می‌شوند.
- کالیبراسیون صدا پیش از `SCAL/WSND` وضعیت واقعی Bridge را بررسی می‌کند و به‌جای Probe جعلی، درخواست اتصال مجدد می‌دهد.
- Firmware، ARM 2.8.2، Runtime Pico، Natural Mouse و منطق Async Sound دست‌نخورده‌اند.

### Validation

- TestRunner وجود اتصال `StateChanged`، برگشت UI به Disconnected و Guard وضعیت واقعی پیش از کالیبراسیون را قفل می‌کند.
- رفتار مورد انتظار: پس از Fault، دکمه Connect نمایش داده می‌شود؛ کاربر یک‌بار Connect می‌زند و سپس همان دکمهٔ نمونه‌برداری صدا نقش تست مستقل سنسور را دارد.

### Next test

برنامه را باز کنید، Connect را بزنید و در Step «Wait For Sound» دکمهٔ نمونه‌برداری از سنسور صدا را اجرا کنید. ابتدا دو ثانیه سکوت و سپس صدای واقعی قلاب را آزمایش کنید؛ لاگ باید پاسخ `SCAL` یا Probe واقعی `WSND` را با فاصلهٔ زمانی نشان دهد، نه شش خط هم‌زمان.

## Build 70 — بازیابی امن LABEL/GOTO و Light Watch

**Previous build:** 69
**Status:** CI candidate; hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

- Runner سبک Game با Route دارای `LABEL/GOTO` روی `unsupported Game command LABEL` متوقف شد.
- Classroom Studio پاسخ معتبر و کوتاه `OK|LUX|lux=...|sensor=ok` را رد می‌کرد.
- Light Watch پس از خطای WriteFile روی Handle قدیمی COM به Poll هر 250ms ادامه می‌داد.
- تلاش قبلی روی شاخهٔ آزمایشی، به‌علت ویرایش معیوب وب، متن کامل فایل‌ها را به خودشان چسباند؛ آن شاخه عمداً کنار گذاشته شد و هیچ بخشی از آن Cherry-pick نشد.

### Safe recovery

- اصلاحات از صفر روی `stable/natural-mouse-v1` و فایل‌های سالم با SHA مرجع بازسازی شدند.
- `plan_engine_game.py` اکنون نقشهٔ یکتای Label می‌سازد، `GOTO` را بدون Import Parser کامل اجرا می‌کند و Target ناموجود را Fail-Closed رد می‌کند.
- Parser نور هر دو قرارداد کامل شش‌بخشی و کوتاه چهاربخشی Pico را می‌پذیرد؛ Field تکراری، Sensor نامعتبر و Shapeهای دیگر همچنان رد می‌شوند.
- Light Watch پس از اولین Fault متوقف می‌شود و Bridge روی خطای `send/send_path` وضعیت اتصال را Disconnected می‌کند.
- Export مدرن پیش از کپی روی CIRCUITPY، Light Watch را متوقف می‌کند تا Auto-reload روی Handle باز رخ ندهد.
- Workflow، ARM 2.8.2، Async Sound، Natural Mouse v1 و Golden 100 دست‌نخورده مانده‌اند.

### Validation

- تست مستقل Game اجرای `LABEL → KEY A → GOTO → KEY C` و Skip شدن KEY B را کنترل می‌کند.
- TestRunner قرارداد Compact/Full Lux، توقف Watch، قطع Bridge و توقف Watch پیش از Deploy را قفل می‌کند.
- Manifest Helper بازی با Hash جدید بازسازی شده است.

## Build 69 — واکنش هم‌زمان Sound در حین حرکت Mouse

**Previous build:** 68
**Status:** CI candidate; hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

در Build 68 مسیر Game بدون MemoryError و با حرکت نرم اجرا شد، اما Runner هنگام فعال‌بودن Mouse عمداً Poll صدا را عقب می‌انداخت. علت جلوگیری از تداخل فرمان Blocking `SCAL` با `MMOVE` و خطای `ERR|BUSY` بود. نتیجه این بود که کلید F فقط بعد از ایستادن Mouse اجرا می‌شد و Catch قلاب تأخیر داشت.

### Change

- Firmware به ARM 2.8.2 ارتقا یافت و فرمان‌های `ASND` و `ASNDCANCEL` اضافه شدند.
- Pro Micro هنگام اجرای Micro-stepهای Mouse، ورودی صوتی A0 را پیوسته و غیرمسدودکننده پایش می‌کند.
- با تشخیص صدا، Mouse در آخرین نقطهٔ ارسال‌شده فوراً متوقف می‌شود و رویداد `EVT|ASND|DETECTED` به Pico می‌رسد.
- Pico سپس کلید F را مستقل از Mouse HID می‌زند؛ MMOVEهای صف‌شده تا Arm بعدی صدا ACK و Drop می‌شوند تا قبل از F حرکت ادامه پیدا نکند.
- Firmware قدیمی همچنان از مسیر `SCAL` استفاده می‌کند، اما واکنش هم‌زمان فقط با ARM 2.8.2 فعال است.
- لایهٔ Legacy و بلااستفادهٔ `human_mouse_v3` از Firmware حذف شد تا فضای Flash برای Watcher جدید آزاد شود؛ مسیر Portable Relative بدون تغییر باقی ماند.

### Validation

- تست جدید ثابت می‌کند Sound poll در بازهٔ حرکت فعال انجام می‌شود و F حداکثر تا 40ms در شبیه‌سازی اجرا می‌گردد.
- پس از تشخیص، هیچ حرکت جدیدی بعد از F ثبت نمی‌شود.
- قرارداد Firmware وجود `ASND=1`، رویدادهای Async و Fallback قدیمی `SCAL` را هم‌زمان کنترل می‌کند.
- Hashهای Runtime و Game helper در Manifest بازسازی شدند.

### Next test

ابتدا Pro Micro را با ARM 2.8.2 و برد Arduino Leonardo فلش کنید و `ams_key.h` خصوصی فعلی را نگه دارید. سپس Bundle جدید Pico را بسازید و Route ماهیگیری را اجرا کنید. معیار پذیرش: حرکت Mouse نرم بماند، پس از صدای قلاب Mouse فوری متوقف و F بدون انتظار برای پایان مسیر اجرا شود، و `ERR|BUSY` یا MemoryError رخ ندهد.

## Build 68 — Runner سبک Game/Fishing و Stop عادی

**Previous build:** 67
**Status:** Hardware verified for Desktop/Login/DC/Game; Sound superseded by Build 69
**Commit:** `9c27b113`

### Problem observed

Build 67 مسیر Desktop و Login/DC را با `ROUTE|stage=light-route` کامل کرد و Pause/Resume نیز سالم بود. کالیبراسیون Game پس از یک Retry ذخیره شد. اما Route واقعی Game پس از Import و Parse موفق، با وجود 62,352 بایت آزاد، در Lazy-load مسیر اجرایی کامل با `MemoryError` خالی متوقف شد. Stop در Runner سبک نیز به‌اشتباه `RuntimeError('route aborted')` را به‌عنوان Guard Failure ثبت می‌کرد.

### Root cause

Game شامل `RPKG`، `LOOPTIME`، `PGROUP`، `WSND` و RMOUSE موازی است؛ Runner سبک Build 67 این ساختارها را نمی‌پذیرفت و بنابراین Parser/Executor/Parallel عمومی را روی Heap تکه‌تکه وارد می‌کرد. عدد حافظهٔ آزاد مجموع Heap بود، نه تضمین یک بلوک پیوسته برای Import/Compile ماژول بعدی.

### Change

- `plan_engine_game.py` با حجم کمتر از 10KB اضافه شد و بدون Import Parser یا Executor کامل، Route واقعی ماهیگیری را Streaming اجرا می‌کند.
- Random Package، Loop زمانی، Parallel Group، RMOUSE و WSND با همان قرارداد قبلی حفظ شدند.
- هنگام حرکت فعال Mouse، Sound poll برای جلوگیری از `ERR|BUSY` اجرا نمی‌شد؛ این محدودیت در سخت‌افزار تأخیر Catch ایجاد کرد و در Build 69 جایگزین شد.
- Stop در Runner سبک اکنون `ROUTE/aborted` عادی است و Guard Failure تولید نمی‌کند.
- Cacheهای Helper بعد از هر Route سبک نیز آزاد می‌شوند تا Start بعدی Heap تازه داشته باشد.
- Manifest و Exporter به Inventory 27فایلی و خروجی 31فایلی به‌روزرسانی شدند.

### Validation

- Route واقعی `game_steps.txt` از Bundle 161 در هر دو حالت Sound detected و Timeout شبیه‌سازی شد.
- حالت detected واکنش F را اجرا و شاخهٔ موس را لغو کرد؛ حالت Timeout بدون F پایان یافت و حرکت Streaming داشت.
- Runner جدید `plan_engine_parse` و `plan_engine_exec` را Import نمی‌کند؛ هر سه فایل Python با `py_compile` معتبرند.
- ماژول Game برابر 9.4KB و Helper Login برابر 11.8KB است.
- Hashهای `code.py`، Login helper و Game helper در Manifest جدید بازسازی شدند.

### Next test

تست سخت‌افزاری Bundle 164/166 تأیید کرد Game با `ROUTE|stage=light-route` و Heap کافی اجرا می‌شود، Pause/Resume و Stop سالم‌اند و MemoryError برنگشته است. تأخیر F هنگام حرکت به Build 69 منتقل شد.

## Build 67 — Runner سبک Streaming برای Login/DC

**Previous build:** 66
**Status:** Hardware verified for Desktop/Login/DC; Game superseded by Build 68
**Commit:** `47c992ee`


### Problem observed

Build 66 و Bundle 160 مسیر Desktop و Natural Mouse را کامل اجرا کردند، اما Route واقعی Login/DC پیش از Import Parser با `MemoryError` برای تخصیص 2930 بایت متوقف شد. حافظهٔ آزاد پیش از Import برابر 50620 بایت بود.

### Root cause

Route Login شامل `LABEL/GOTO`، دو `RMOUSE`، `TYPE` انسانی، `KEY` و `KDOWN/KUP` است. Runner سبک Build 66 این مجموعه را نمی‌پذیرفت و در نتیجه Parser کامل 29KB را روی Heap تکه‌تکه Import می‌کرد. بازگشت به Baseline نرم Build 50 اصلاحات مسیر سبک Login را همراه خود نیاورده بود.

### Change

- Module جدید `plan_engine_login.py` فقط منطق لازم Login را با حجم کمتر از 12KB فراهم می‌کند.
- `RMOUSE` همان Natural Mouse v1 و ARM 2.8.1 تأییدشده را حفظ می‌کند.
- `TYPE` شامل Typo/Correction تعدادمحور، Word/Punctuation/Think delay و متن نهایی دقیق است.
- `LABEL/GOTO`، `KEY`، `KDOWN/KUP` و Delayها بدون Import `plan_engine_parse.py` اجرا می‌شوند.
- متن Route پیش از Import Helper آزاد و `gc.collect()` اجرا می‌شود.
- Module جدید داخل Manifest قرار گرفته و پس از پایان Route همراه Cacheهای Plan آزاد می‌شود.

### Validation

- Route واقعی `p-updated-v3-fishing-parallel.amsj#LoginOrDc` با 19 فرمان روی Runner سبک شبیه‌سازی شد.
- هر دو RMOUSE در مجموع 130 نقطهٔ Streaming تولید کردند.
- پس از 3 تا 5 Typo/Correction، متن نهایی دقیقاً `zodiak999999` باقی ماند.
- KDOWN/KUP بدون کلید نگه‌داشته‌شده پایان یافت و Enter اجرا شد.
- `code.py` و `plan_engine_login.py` با `py_compile` معتبرند.
- TestRunner وجود Helper، نبود وابستگی به Parser و Manifest 26فایلی را کنترل می‌کند.
- ابزار Calibration heap نیز Inventory جدید 26فایلی را بدون تغییر رفتار کالیبراسیون بازسازی می‌کند.
- قرارداد شبیه‌سازی Calibration حضور `plan_engine_login.py` و هر 26 Hash را کنترل می‌کند.
- قرارداد Parallel/Exporter نیز ARM 2.8.1 ثابت، Helper سبک و Inventory 26فایلی را هم‌زمان قفل می‌کند.
- Workflow بسته‌بندی روی شاخهٔ پایدار `stable/natural-mouse-v1` فعال است.

### Next test

با ARM 2.8.1 بدون تغییر، Bundle جدید را از پروژهٔ کامل بسازید و Start را در Login/DC بزنید. معیار پذیرش: `ROUTE|stage=light-route` به‌جای `before-plan-engine-import`، اجرای TYPE و هر دو RMOUSE، پاسخ‌گویی Stop/Pause و نبود `MemoryError` یا کلید گیرکرده.

## Build 66 — Natural Mouse v1 روی Baseline نرم Build 50

**Previous build:** 50
**Status:** CI candidate; hardware retest pending
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

مسیرهای Batch نسخه‌های بعدی زمان هدف را بهتر نکردند و حرکت را به Burst/Gap تبدیل کردند. بازگشت آزمایشی نیز نشان داد ترکیب Runtime جدید با Firmware قدیمی Cadence اصلی Build 50 را بازتولید نمی‌کند. پروژهٔ C روی Build 50 دوباره حرکت نرم و پیوسته داد.

### Root cause

تلاش برای برابرکردن زمان اجرا با Hand Sample، بهینه‌سازی را از کیفیت Cadence دور کرد. Batch و Deadline pacing تعداد تراکنش‌ها را کاهش دادند اما فاصله‌های صفر و مکث‌های دوره‌ای ساختند. در مقابل، مسیر سبک Build 50 با ARM 2.8.1 حرکت را پیوسته نگه می‌دارد؛ بنابراین Humanization باید در سطح شکل و زمان کل مسیر انجام شود، نه در لایهٔ HID.

### Change

- Cadence، Micro-step و ARM 2.8.1 دست‌نخورده باقی ماندند.
- مدت حرکت با فاصله مقیاس می‌شود: حرکت کوتاه سریع‌تر، متوسط نزدیک بازهٔ تنظیم‌شده و بلند آهسته‌تر است.
- نقطهٔ اوج سرعت در هر حرکت کمی جلو یا عقب می‌رود؛ Endpoint و مجموع زمان حفظ می‌شوند.
- مکث میان‌مسیر فقط برای حرکت حداقل 300px مجاز است.
- Overshoot فقط برای حرکت حداقل 300px، به اندازهٔ 2–6px و با یک اصلاح 70–160ms انجام می‌شود.
- پروژهٔ `mouse-tune-C-natural-v1.amsj` با احتمال مکث 4٪، Overshoot شش‌درصدی و Curve برابر 3–22٪ اضافه شد.

### Validation

- Runner سبک روی 200 Seed بدون مسیر مطلق، بدون Import موتور کامل و با حداکثر 128 نقطه موفق شد.
- تست فاصله ثابت کرد زمان حرکت کوتاه، متوسط و بلند به‌ترتیب افزایش می‌یابد.
- تست C# تأیید می‌کند حرکت کوتاه Overshoot ندارد و حرکت بلند پس از Overshoot دقیقاً به Endpoint برمی‌گردد.
- قراردادهای قدیمی مدت حرکت با Target جدید وابسته به فاصله همگام شدند.
- Hashهای Runtime مدرن در Manifest بازسازی شدند.
- Workflow رسمی برای Branch آزمایشی فعال شد تا بستهٔ Windows و TestRunner روی GitHub بررسی شوند.

### Next test

ARM 2.8.1 حفظ شود، پروژهٔ `mouse-tune-C-natural-v1.amsj` با Build آزمایشی اجرا و Guard log و Record ارسال شود. معیار پذیرش: نرمی فعلی C حفظ شود، وقفه‌های فنی دوره‌ای برنگردند، Endpoint دقیق بماند و تنها تنوع سطح مسیر افزایش یابد.

## Build {{BUILD_NUMBER}} — Changelog اجباری و تأیید سخت‌افزاری A

**Previous build:** 49
**Status:** CI candidate; Build 49 hardware result recorded
**Commit:** `{{COMMIT_SHA}}`

### Problem observed

اطلاعات علت شکست و اصلاح هر Build در PRها و صفحهٔ داخلی پراکنده بود. متن Release نیز برای همهٔ Buildها یک متن عمومی تکراری داشت؛ بنابراین توسعه‌دهندهٔ بعدی نمی‌توانست وضعیت فعلی را سریع بفهمد.

### Root cause

Workflow انتشار متن Release را به‌صورت ثابت تولید می‌کرد و هیچ بررسی‌ای وجود نداشت که تغییرات Build همراه با ورودی Changelog باشند.

### Change

- این فایل به‌عنوان مرجع تجمعی وضعیت سخت‌افزاری اضافه شد.
- README مستقیماً به این سند و آخرین Release اشاره می‌کند.
- Workflow برای هر Build بخش نخست این فایل را به‌عنوان Release notes استخراج می‌کند.
- هر Commit مؤثر بر Build باید همین Changelog را تغییر دهد؛ در غیر این صورت Job بسته‌بندی Fail می‌شود.
- نتیجهٔ سخت‌افزاری Build 49 و آمار Record آزمون A ثبت شد.

### Validation

- لاگ Build 49: `after-plan-engine-import=68880` و `after-plan-parse=68512`.
- Route `desktop_steps.txt` تا `ROUTE/complete` اجرا شد و `MemoryError` رخ نداد.
- Record آزمون A: 1,928 موقعیت، 1,927 Segment، گام میانه 2.00px، صدک 95 گام 2.24px و بیشینه 2.83px.
- فاصلهٔ زمانی میانه 2ms بود، اما صدک 80 برابر 52ms، صدک 95 برابر 53ms و 862 فاصلهٔ حداقل 40ms ثبت شد؛ بنابراین اندازهٔ Micro-step صحیح است ولی نرمی زمانی هنوز نیازمند مقایسهٔ B/C/D است.

### Next test

پروژه‌های B، C و D را با Build 49 جداگانه اجرا و Record هرکدام را ثبت کنید. معیار انتخاب: کاهش فاصله‌های 40ms به بالا، حفظ Micro-step حداکثر سه پیکسل و نبودن `MemoryError`.

## Build 49 — Runner سبک RMOUSE

**Previous build:** 48
**Status:** Hardware functional pass; motion quality pending
**PR/Commit:** [#16](https://github.com/noonoix/smz/pull/16) / [`2b1919dd`](https://github.com/noonoix/smz/commit/2b1919dda4082d89531723cff82876ba97a69437)
**Release:** [classroom-current-49](https://github.com/noonoix/smz/releases/tag/classroom-current-49)
**SHA256:** `2dc6f75b1c84eafd8ebcccb3ba67c99a8d9a222aacb159533879d3f1b23a56a8`

### Problem observed

Build 48 Parser را با حاشیهٔ خوب Load می‌کرد، اما `run_plan` برای Route سادهٔ A موتور کامل 19.9KB را Import می‌کرد و تخصیص 1,016 بایت شکست می‌خورد. اجرای دوم پس از Fragmentation در تخصیص 776 بایت شکست خورد.

### Root cause

Route سادهٔ `RMOUSE + LOOP + DELAY` بی‌دلیل هزینهٔ Import ماژول‌های Human، Parallel و Executor کامل را می‌پرداخت.

### Change

Runner سبک فقط برای Relative Native و مجموعهٔ محدود `PLAN/SCREEN/SPEED/DELAY/LOOP/LOOPTIME/ENDLOOP/RMOUSE` اضافه شد. Routeهای پیچیده همچنان به موتور کامل می‌روند؛ Fishing و Parallel تغییر نکردند.

### Validation

خروجی Runner سبک و موتور کامل روی 100 Seed برابر بود؛ RMOUSE روی 200 Seed و Parallel روی 17 سناریو موفق شد. تست واقعی A به `ROUTE/complete` رسید.

### Next test

B/C/D برای انتخاب Tempo و Curve مناسب مقایسه شوند.

## Build 48 — جداسازی Import Parser از Executor

**Previous build:** 47
**Status:** Partial hardware pass; superseded by 49
**PR/Commit:** [#15](https://github.com/noonoix/smz/pull/15) / [`3f2a03d7`](https://github.com/noonoix/smz/commit/3f2a03d77281825b093ac132074238bad437b5eb)
**Release:** [classroom-current-48](https://github.com/noonoix/smz/releases/tag/classroom-current-48)
**SHA256:** `048044fdf71dfc8fda0469508e15e59287e30f873e9d09c7e0f9a211fd334bec`

### Problem observed

Build 47 هنگام Import هم‌زمان Parser، Human و Executor با تخصیص 2,344 بایت شکست خورد.

### Root cause

Facade هنگام درخواست اولیهٔ `parse_plan` همهٔ ماژول‌های اجرایی را نیز Import می‌کرد.

### Change

Parser زودهنگام و Executor داخل `run_plan` به‌صورت Lazy بارگذاری شد.

### Validation

در سخت‌افزار Import با 71,328 بایت و Parse با 70,960 بایت آزاد کامل شد؛ شکست بعدی فقط Import Executor بود و در Build 49 رفع شد.

## Build 47 — Fishing timeout و Cadence موس

**Previous build:** 46
**Status:** Fishing semantics fixed; A import failed
**PR/Commit:** [#14](https://github.com/noonoix/smz/pull/14) / [`d324a5b8`](https://github.com/noonoix/smz/commit/d324a5b8afa61db8114f3c1e83a2fcd94980e10d)
**Release:** [classroom-current-47](https://github.com/noonoix/smz/releases/tag/classroom-current-47)
**SHA256:** `efe23c93ff45575b485ec0bdd1fda60f40f3c617f6c7194f2bb4111b4c6483f2`

### Problem observed

پس از Timeout صدای ماهیگیری، شاخهٔ Sound پایان می‌یافت اما Loop بی‌نهایت موس فعال می‌ماند؛ در نتیجه قلاب مجدد اجرا نمی‌شد. حرکت نیز بین نقاط Pico وقفه‌های حدود 40–55ms داشت.

### Root cause

Timeout کل Parallel Group را لغو نمی‌کرد و مسیر Streaming فقط تا 32 نقطهٔ زمانی داشت.

### Change

Timeout کل گروه را لغو می‌کند، واکنش `F` را رد می‌کند و به Loop بیرونی برمی‌گردد. Cadence هدف 8ms و سقف Streaming برابر 128 نقطه شد.

### Validation

تست‌های Parallel 17/17 و Relative RMOUSE روی 200 Seed موفق شدند. تست A بعداً فشار Import مستقل را آشکار کرد.

## Build 46 — Streaming RMOUSE عادی Login/DC

**Previous build:** 45
**Status:** Hardware verified
**PR/Commit:** [#13](https://github.com/noonoix/smz/pull/13) / [`946f736b`](https://github.com/noonoix/smz/commit/946f736bad76b65250a31faa33b8d299cbeef5cc)
**Release:** [classroom-current-46](https://github.com/noonoix/smz/releases/tag/classroom-current-46)
**SHA256:** `04febadd9d9b0d8e387f71d3b085e7b2ac81cbfd1f5f609231b055f50c84d812`

### Problem observed

Login/DC پس از Parse و `SCREEN` در نخستین RMOUSE با تخصیص 2,048 بایت شکست می‌خورد.

### Root cause

RMOUSE غیرموازی هنوز چند لیست متراکم WindMouse را در RAM می‌ساخت؛ اصلاح Streaming فقط در Parallel فعال بود.

### Change

RMOUSE نسبی عادی نیز Streaming شد و متن Route پیش از نخستین حرکت آزاد شد.

### Validation

تست سخت‌افزاری Login/DC با 51,776 بایت و Game با 49,120 بایت پس از Parse موفق شد؛ Stop به‌صورت `ROUTE/aborted` ثبت شد.

## Build 45 — حذف SCAL وسط حرکت و بازیابی Heap

**Status:** Verified foundation
**Commits:** [`1bc0c5c7`](https://github.com/noonoix/smz/commit/1bc0c5c78e2c73fa7e255963955066fdb2d2da69)، [`894bad14`](https://github.com/noonoix/smz/commit/894bad14738601b87dbf7ff91b5f01096c55ab9d)
**Release:** [classroom-current-45](https://github.com/noonoix/smz/releases/tag/classroom-current-45)
**SHA256:** `59e5056286be6000c24695e013974d023c81d68f975efb059ae514d3d9915eaf`

- Poll صوتی `SCAL|10` هنگام Stream فعال موس اجرا نمی‌شود و به نقاط توقف عمدی منتقل شد.
- `PlanAbort` ناشی از Stop دیگر Guard Failure نیست.
- ماژول‌های Plan پس از Route از Cache خارج می‌شوند تا Start بعدی Heap تازه داشته باشد.

## Build 43 — Pause، SCAL BUSY و سرعت Sample

**Status:** Superseded but retained behavior
**Commits:** [`58fff88d`](https://github.com/noonoix/smz/commit/58fff88dcf9e0a3c33a3addafca161590a46a883)، [`55d4b081`](https://github.com/noonoix/smz/commit/55d4b0818e64b259e7a2c6582b1dfb9f871fc78a)
**Release:** [classroom-current-43](https://github.com/noonoix/smz/releases/tag/classroom-current-43)
**SHA256:** `fbde9312b55de60e69324d1dedc9235ec2c2a7ef560940b97b2c107054a1b82b`

- Pause فوراً `keyboard.release_all()` می‌کند.
- پیش از SCAL صف ARM Flush و BUSY گذرا Retry می‌شود.
- Hand Sample منبع Tempo است و هزینهٔ Micro-step دوباره به زمان حرکت افزوده نمی‌شود.

## Build 41 — Parallel streaming و پروفایل نور

**Status:** Superseded
**Commit:** [`13277a93`](https://github.com/noonoix/smz/commit/13277a935b3f0520c045fa12cb11fd658ee9bf01)
**Release:** [classroom-current-41](https://github.com/noonoix/smz/releases/tag/classroom-current-41)
**SHA256:** `0b0677c9a3c1d045f1aacf299be7360fce7042be69e3e83765c7e468afb999d4`

Parallel RMOUSE از لیست متراکم به Curve Streaming منتقل شد و Parsing پاسخ SCAL کم‌Allocation شد. پروفایل‌های Character Dashboard و Game تثبیت شدند.

## Build 40 — Retry کالیبراسیون پس از Overlap

**Status:** Verified
**Commit:** [`034e46c8`](https://github.com/noonoix/smz/commit/034e46c8a175c15804c4b2861c722d86063ff0ec)
**Release:** [classroom-current-40](https://github.com/noonoix/smz/releases/tag/classroom-current-40)
**SHA256:** `a281f4ad03432f5828345ac5197581adc0930d3cf40d96a36b9446246d86bf64`

پس از رد `CAL|OVERLAP`، زرد نمونه‌گیری تازه را آغاز می‌کند؛ آبی کوتاه Stage نامعتبر را رد نمی‌کند و آبی بلند خارج می‌شود. بازخورد صوتی خطا اضافه شد.

## Build 39 — حفظ Facade مدرن در Export پروژهٔ جاری

**Status:** Verified foundation
**Commit:** [`7efce2da`](https://github.com/noonoix/smz/commit/7efce2da545648f17a102afd5a148f596c92bfa6)
**Release:** [classroom-current-39](https://github.com/noonoix/smz/releases/tag/classroom-current-39)
**SHA256:** `ca5ea9c0175ec76bc1271567f0c9c83caf382aede25d3ee885c77a0eb6221369`

Exporter Legacy پس از تولید Routeها، Facade مدرن را با Engine یکپارچهٔ 30KB جایگزین می‌کرد. ترتیب Export اصلاح و Hash نهایی Manifest دوباره ساخته شد.

## Build 38 — Split اولیهٔ Executor و Parallel

**Status:** Superseded by 39
**Commits:** [`c1ad2385`](https://github.com/noonoix/smz/commit/c1ad2385333f0a46f2c945620e5a865bc6f8630d)، [`dfe4102a`](https://github.com/noonoix/smz/commit/dfe4102a421807087a00910a6918c2a2bbe73964)، [`eaa65f96`](https://github.com/noonoix/smz/commit/eaa65f969fd2abd430c04846e1f2ba1ac18fffec)
**Release:** [classroom-current-38](https://github.com/noonoix/smz/releases/tag/classroom-current-38)
**SHA256:** `af7af2a700d17be4254248642ddc883283e105053ce1fd2b28b634f1eafd64b7`

Scheduler Parallel از Executor جدا و Lazy-load شد. Export اشتباه Facade در Build 39 اصلاح شد.

## قانون به‌روزرسانی

برای هر تغییر Build‌ساز:

1. بخش `Build {{BUILD_NUMBER}}` فعلی را با شمارهٔ واقعی Build قبلی تثبیت کنید.
2. یک بخش جدید `Build {{BUILD_NUMBER}}` در بالای تاریخچه اضافه کنید.
3. Problem، Root cause، Change، Validation و Next test را تکمیل کنید.
4. وضعیت Hardware را از CI جدا نگه دارید؛ CI سبز به‌تنهایی به معنی تأیید سخت‌افزاری نیست.
5. Workflow بدون تغییر همین فایل اجازهٔ انتشار Build جدید را نمی‌دهد.
