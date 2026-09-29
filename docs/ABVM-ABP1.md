# AMS Bytecode VM — ABP1-R2 contract

ABVM compiles an AMSJ/`StepNode` tree on the PC and runs a verified application image on the Pico. `ABP1` is application bytecode; it is not Python or MPY bytecode. Plan2 text remains a test oracle only and is never parsed by ABVM firmware.

## Current boundary

- The production exporter and Pico runtime remain unchanged.
- The compiler currently targets the real `Game` and `Whisper` trees.
- The host reference VM is the semantic oracle for future firmware.
- `Whisper` has an explicit `INTERRUPT_AND_RESUME` route policy but global interrupt/resume execution is not implemented yet.
- Firmware deployment starts only after compiler, verifier, fuzz tests, and differential tests are stable.

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

Unknown values are rejected. Clock and Pause accounting will be added before firmware execution is enabled; it must not remain implicit.

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

The transitional target is two FAT program slots with the active selector stored as redundant, generation-numbered NVM records. Production moves slots and selector records to raw flash, reads program data through XIP, and uses TinyUSB for HID/CDC. A slot activates only after size, CRC, program SHA-256, ABI, capabilities, and resource certificate pass.
