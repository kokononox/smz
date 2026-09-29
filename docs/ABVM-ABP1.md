# AMS Bytecode VM — ABP1 phase-0 contract

ABVM compiles an AMSJ pipeline on the PC and runs a verified application program on the Pico. `ABP1` is application bytecode; it is not Python or MPY bytecode.

## Phase-0 boundaries

- The production exporter is unchanged.
- The first compiler targets the real `Game` and `Whisper` trees.
- The host reference VM is the semantic oracle for the future Pico VM.
- Firmware deployment begins only after compiler, verifier, and differential tests are stable.

## Image layout

All integer fields are little-endian.

| Section | Shape |
| --- | --- |
| Header | fixed 64 bytes |
| Code | fixed 16-byte instructions |
| Constants | typed TLV records, four-byte aligned |
| Routes | fixed 16-byte route entries |

The whole image is protected by CRC32. The header also carries the first 16 bytes of the canonical AMSJ SHA-256, VM ABI, maximum frame depth, and maximum lane count.

The initial firmware contract is:

- maximum explicit frames: 8
- maximum concurrent lanes: 2
- one controller Watch lane per Race group
- no nested Watch
- no imports, closures, or generators in route execution

## Core opcodes

| Opcode | Purpose |
| --- | --- |
| `END` | terminate a route |
| `DELAY` | yield until a sampled deadline |
| `KEY`, `KDOWN`, `KUP` | Pico keyboard operations |
| `TYPE` | stream a typed constant with humanization settings |
| `RMOUSE` | stream a flash-backed relative mouse specification |
| `LOOP_ENTER`, `LOOP_NEXT` | counted or deadline loop |
| `RPKG_ENTER`, `ITEM_END` | selected package-item ranges |
| `RACE_BEGIN`, `LANE_END` | two-lane race with one controller lane |
| `WATCH` | detected falls through; timeout jumps past the response |
| `JUMP` | absolute instruction jump |

The fishing Parallel Group is compiled as a Race, not a normal Join. The sound lane is the controller: detection or timeout cancels the mouse lane and resumes the parent at the next cast.

## Verification before deployment

An image is rejected when CRC or bounds are invalid, an opcode or jump is unknown, frame depth exceeds 8, lane count exceeds 2, package/race ranges are malformed, Watch is nested, or TYPE lacks its header capability.

## Portability target

`program.abp` is independent of CircuitPython's MPY format. The fixed ABVM firmware may later be distributed as version-pinned MPY for RP2040 together with a matching CircuitPython UF2. After provisioning, the board executes the program without a PC connection.
