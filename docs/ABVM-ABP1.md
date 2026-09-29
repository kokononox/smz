# AMS Bytecode VM — ABP1-R2 contract

ABVM compiles an AMSJ/`StepNode` tree on the PC and runs a verified application image on the Pico. `ABP1` is application bytecode; it is not Python or MPY bytecode. Plan2 text remains a test oracle only and is never parsed by ABVM firmware.

## Current boundary

- The production exporter and Pico runtime remain unchanged.
- The compiler currently targets the real `Game` and `Whisper` trees.
- The host reference VM remains the semantic oracle.
- The native core implements one fixed `INTERRUPT_AND_RESUME` slot.
- A safe Pico SDK bring-up UF2 now runs the core with CDC/buttons and
  deliberately stubbed input actors.

## Native fixed-state core

`firmware/abvm` now contains the first dependency-free C11 implementation of
the production-shaped loader, verifier, and scheduler. It is compiled on the
host today and is designed to move unchanged behind Pico SDK/TinyUSB adapters.

The core:

- uses caller-owned program bytes and performs no heap allocation;
- verifies header, canonical section layout, CRC32, full program SHA-256,
  resource limits, route policies, opcodes, constants, loops, packages,
  scope lanes, Watch ranges, and non-nesting rules;
- keeps all lanes, frames, scope state, PRNG, and the single suspended
  interrupt context in fixed-size structs;
- executes Loop, Random Package, structured Scope, Delay, Watch, Jump, and
  route completion;
- emits nonblocking action events for Key, Type, and Mouse instead of touching
  hardware;
- supports fixed-state Pause/Resume/Stop and Whisper interrupt/resume;
- rejects corrupted images before route start.

The host smoke test runs a committed fishing-shaped Game/Whisper ABP through
this native core, including Pause, interrupt, Watch detection, exact resume,
completion, Stop, and corrupted-image rejection. The supplied current
2,152-byte Game/Whisper image passes the same native smoke binary. HID, UART
mouse, ADC sound, Guard buttons, flash slots, and TinyUSB are intentionally
adapter work, not part of this portable scheduler.

## Pico SDK bring-up image

`firmware/abvm/pico` wraps the native core in the first RP2040 UF2:

- `program.abp` is verified on the PC, converted to a `const` flash array at
  build time, and verified again by the board at boot;
- program and runtime are entirely inside the UF2, with no CIRCUITPY or FAT;
- Pico's monotonic millisecond clock drives the scheduler;
- USB CDC exposes PING/status/control and diagnostic sound injection;
- GP3 is a debounced Pause/Resume button;
- GP4 is a debounced Start/Stop button;
- all work is serviced by a nonblocking one-millisecond board loop.

This is deliberately a safe bring-up build. Key, Type, Mouse, and release-all
events are logged as `ACTION|stub` / `HID|release-all|stub` and immediately
acknowledged; the UF2 cannot emit keyboard or mouse input. CI builds with Pico
SDK 2.1.1 and publishes UF2, ELF, and embedded ABP artifacts.

The next adapter layer replaces only those stubs with TinyUSB keyboard and ARM
UART actors. It must not change ABP bytecode or scheduler semantics.

## Single-image layout

All integer fields are little-endian.

| Section | Shape |
| --- | --- |
| Header | fixed 128 bytes |
| Code | fixed 16-byte instructions |
| Constants | typed TLV records, four-byte aligned |
| Routes | fixed 16-byte route entries |
| Resource certificate | fixed 36 bytes |

The header contains format and VM ABI versions, every section's bounds, maximum frame/lane use, canonical AMSJ SHA-256, full program-image SHA-256, and CRC32. Program SHA-256 is calculated with both its own field and CRC zeroed. CRC32 is calculated afterward with only CRC zeroed. Both are verified before any instruction is interpreted.

`program.abp.map.json` is emitted beside the program on the PC. It maps `route + pc` to the original Step ID/path/type and is not copied to Pico. A board diagnostic therefore needs only:

```text
program hash + route id + pc + opcode
```

## Explicit structured concurrency

`parallelGroup` is lowered to `SCOPE_BEGIN` and `LANE_END`. Every scope names one policy:

| Policy | Completion rule |
| --- | --- |
| `JOIN_ALL` | Resume after every lane finishes |
| `CANCEL_ON_ANY` | First completed lane cancels its siblings |
| `CANCEL_ON_TERMINAL_LANE` | Only the designated terminal lane cancels workers |
| `KEEP_RUNNING_UNTIL_CANCELLED` | No lane completion resumes the parent |

The fishing group uses `CANCEL_ON_TERMINAL_LANE`: mouse is a worker; sound is terminal; sound detection or timeout cancels mouse work before the next cast. It is not a generic first-lane race.

## Route transition policy

Every route carries one fail-closed policy:

| Policy | Intended use |
| --- | --- |
| `ABORT_AND_START` | Desktop/Login/Dashboard/Loading transitions |
| `INTERRUPT_AND_RESUME` | global Whisper handling |
| `CANCEL_SCOPE_AND_CONTINUE` | scoped Catch/Splash response |
| `ABORT_AND_RESTART` | Game restart |
| `DENY` | unsupported or invalid transitions |

Unknown values are rejected.

## Pause, Resume, Stop, and clock domains

Every route must carry `PAUSE_RELEASE_HID`. Pause is a scheduler state
transition, never a blocking cue or a call made from inside Mouse/Type code:

1. release every held keyboard key and halt the ARM mouse actor;
2. retain route, lane, frame, actor, PC, PRNG, and deadline state;
3. stop opcode dispatch at the next VM boundary;
4. resume from those exact PCs.

Two explicit clock policies exist:

| Clock | Pause behavior |
| --- | --- |
| `WALL` | absolute due times and deadlines continue while paused |
| `ACTIVE` | due times and deadlines shift by the exact pause duration |

Current Game uses `WALL`: Pause, Whisper, Catch, and cooldown time remain part
of its ten-minute deadline. `ACTIVE` is implemented and tested for future
workflows but is not selected by current routes.

Stop is terminal and idempotent: release all HID, cancel every active/suspended
lane and actor, clear interrupt state, and perform no further fetch. It never
runs a melody on the stopped route's stack.

## Global interrupt-and-resume

A route with `INTERRUPT_AND_RESUME` may suspend one running context:

- held HID is released before switching contexts;
- the complete Game lane/frame state remains in its fixed slot;
- the interrupt route runs in the reserved interrupt slot;
- completion restores the original context at the exact PC;
- absolute Game deadlines are not rewritten under `WALL`;
- a nested interrupt is rejected because the certificate declares one slot;
- Stop cancels both current and suspended contexts.

`Whisper` now has this verified host/reference behavior. Hardware sound
delivery and Pico actor implementations remain future firmware work.

## Resource certificate

The compiler records the actual static requirements: frames, lanes, actors, event/interrupt slots, sound profiles/listeners, PWM channels, maximum constant/Type/mouse sizes, and capabilities. The verifier recalculates derivable values and rejects under-declaration, mismatch, or any value above the firmware contract.

```text
frames=8 lanes=2 actors=4 events=4 interrupts=1
soundProfiles=8 soundListeners=1 pwmChannels=1
```

The transitional CircuitPython runtime must perform no intentional allocation, import, parse, generator creation, or closure creation after `VM_START`. Strict zero-allocation is a native-firmware guarantee, not a CircuitPython claim.

## Core opcodes

| Opcode | Purpose |
| --- | --- |
| `END` | terminate a route |
| `DELAY` | yield until a sampled deadline |
| `KEY`, `KDOWN`, `KUP` | Pico keyboard operations |
| `TYPE` | stream a typed constant through fixed actor state |
| `RMOUSE` | stream a flash-backed relative mouse specification |
| `LOOP_ENTER`, `LOOP_NEXT` | counted or deadline loop |
| `RPKG_ENTER`, `ITEM_END` | selected package-item ranges |
| `SCOPE_BEGIN`, `LANE_END` | structured concurrent scope |
| `WATCH` | detected falls through; timeout skips the response |
| `JUMP` | absolute instruction jump |

The fixed 16-byte encoding remains ABP1's baseline. The real program is only a few KiB, so an 8-byte encoding would constrain operands for little benefit. A later encoding version may add a compact representation without changing source semantics.

## Verification before deployment

An image is rejected before activation when any of these checks fail:

- magic, format, ABI, file size, SHA-256, or CRC;
- canonical section order or bounds;
- opcode, jump, route ending, or route policy;
- frame/lane/actor/event/interrupt limits;
- package item or scope lane ranges;
- terminal-lane declaration;
- nested Scope, nested Watch, or sound-listener count;
- constant type, index, or maximum payload size;
- TYPE without its declared capability.

## Generated ABI registry

`tools/generate_abvm_abi.py` generates all bindings from one registry:

- `spec/abvm/abi1.json`
- `firmware/abvm/include/abvm_abi1.h`
- `ams-shell/src/Ams.UI/Services/AbvmAbi.Generated.cs`

Generated files are contracts and must not be edited manually.

## Portability and deployment target

`program.abp` is independent of CircuitPython MPY. Migration can use a version-pinned MPY VM first and later replace it with a native RP2040 UF2 without changing AMSJ or ABP semantics.

The current bring-up UF2 embeds one immutable ABP image and therefore has no FAT dependency. Production will place two program slots plus redundant generation-numbered selector records in raw flash, read program data through XIP, and use TinyUSB for HID/CDC. A slot activates only after size, CRC, program SHA-256, ABI, capabilities, and resource certificate pass.
