# Session handoff — timeBudget and portable human mouse

Date: 2026-10-05  
Repository: `kokononox/smz`  
Integration branch: `arch/abvm-phase0`

## Integrated source state

- PR #124 added explicit Parallel Group completion policies: `waitAll`, `watchLane`, and `firstCompleted`.
- PR #125 added authoritative `timeBudget` and is integrated on `arch/abvm-phase0` at commit `e72643a`.
- The GitHub-hosted jobs for the follow-up release were not acquired by runners. A local Windows x64 build succeeded with zero compile errors.
- No new firmware/runtime feature build was created after this handoff update.

## Validated local gates

- Windows x64 application build: passed.
- TestRunner compile: passed.
- LightTelemetryTests compile: passed.
- Pico bridge transport: 18 passed, 0 failed.
- Portable/ABVM/C contracts: 66 test files passed.
- Native Pico UF2 template build: passed; 30 identity templates generated locally.

## Current user project contract

The latest reviewed AMSJ remains a session artifact because it contains a personal 30-second hand-movement profile and must not be published to this public repository without separate explicit consent.

- Session filename: `autocycle5-80-times-v7-all-human-mouse.amsj`
- SHA-256: `56156fd2dc5a26a56a21c82fa638fcb067a9d09f55291c5247d009d953eb0c63`
- Fishing Parallel Group: `timeBudget = 9 minutes`.
- Fishing worker: at most 80 iterations.
- Mouse worker: timed 9-minute loop, independent of the fishing iteration count.
- Catch: threshold 40, minimum duration 20 ms, post-cast arm delay 800–1200 ms.
- Soft Boundary: 4%.
- All 295 authored random-mouse actions across Login/DC, Launch Recovery, Character Dashboard, Entering Game Loading, and Game were normalized to context-specific human profiles.

## Important Native compiler finding

Native `compile_loop` currently treats every mode except `time` as a count loop. Therefore `mode=infinite` consumes the stale `count` field instead of emitting count zero. The current AMSJ safely works around this by using a timed mouse lane equal to the enclosing `timeBudget`. A future source fix must add a regression test before removing this workaround.

## Approved next portable-only mouse phase

Keep Pico as the autonomous brain and Pro Micro as the mouse/sound arm. No host-side cursor reading, vision, network, or OS agent may be introduced.

Candidate implementation scope:

1. Multi-leg paths only for longer movements, retaining bounded overshoot correction.
2. Slowly varying session states: focused, normal, idle, and fatigued.
3. Correlated distance, duration, curvature, pause, and overshoot parameters.
4. Richer compact statistics derived at export from the 30-second hand profile.
5. Per-boot sequence variation from board-local state while keeping all ranges bounded and testable.

## Quality gate for the next build

- Fix and test Native `infinite` loop semantics first.
- Add deterministic host tests and C smoke coverage for every new mouse state transition.
- Build Pico UF2 and all identity templates locally.
- Compile the Windows application and both test projects.
- Run portable/ABVM/C contracts.
- Inspect the packaged artifact before delivery.
- Commit and push source plus this handoff update before distributing a new build.

## v0.9.68 local build result

The approved portable-only phase was implemented on
`feature/portable-human-mouse`.

- Native `mode=infinite` now always compiles to the VM's zero-count encoding;
  a retained editor count can no longer stop the mouse worker after two runs.
- The Pico mouse actor now keeps one of four board-local states for 6–18 moves:
  focused, normal, idle, or fatigued.
- Speed, duration, curvature, pauses, and correction probability are sampled
  coherently inside bounded authored/profile ranges.
- Selected moves of at least 450 pixels use an on-screen two-leg path while
  retaining exact final targeting and bounded overshoot correction.
- First-use board uptime is mixed into the PRNG, avoiding identical cold-boot
  sequences without host cursor, OS, network, or vision input.
- Classroom Studio was advanced to `0.9.68`.

Validation:

- 67 Portable/ABVM contract files passed.
- Pico Bridge transport: 18 passed, 0 failed.
- ABVM C smoke tests passed.
- Pico firmware compiled; 30 non-empty identity templates were generated.
- Windows application compiled with 0 errors.
- TestRunner and LightTelemetryTests compiled with 0 errors. Their Windows
  Desktop executables cannot run on the Linux build host.

Private local deliverables:

- `autocycle5-80-times-v8-human-mouse-v3.amsj`
  - SHA-256: `99d78195cb9cb4e6c40c0cd20c22cc33eadf574ec4190437dfa8d3d94545cda5`
  - All 15 mouse siblings inside fishing `timeBudget` groups use true
    `infinite`; every fishing sibling remains capped at 80 iterations.
- `autocycle5-human-mouse-v3-runtime-NB01.uf2`
  - SHA-256: `ddb6b9ecb33c1eafc5aacd5f4be6d978712a43363f06388b9693a15224c2a696`
  - 12 routes, 3,521 instructions, two lanes, 195,328-byte ABP payload.

The personalized AMSJ and its generated project UF2 remain local session
artifacts and are not committed to the public repository.

### Windows packaging correction

The first `0.9.68` ZIP accidentally contained the extensionless Linux apphost
from a cross-platform `dotnet build`. It is superseded by a `dotnet publish`
for `win-x64` that contains a PE32+ `ClassroomStudio.exe`. The packaged JSON
and emergency fallback now use the approved light profiles: Desktop 59±1,
Login/DC 74±13.5, Character Dashboard 43±1, Entering Game Loading 3±1,
Game 33±0.7, Targeted 36.7±0.5, Whisper 100±1 with 5,000 ms cooldown, and
Whisper Repeat 23±2.5 with 1,000,000 ms cooldown. Every profile keeps 750 ms
stability and 1 lux hysteresis.

## Phase 1 hardware acceptance and phase 2 start

The user reported that the v0.9.68 hardware test completed without a problem
and supplied the exported NB01 UF2 as evidence.

- Tested UF2 SHA-256:
  `88a8adf2563fa71d580a4a8054363cca05d2ee813e419a2f5a1d91f30f3e0e43`
- Extracted ABP SHA-256:
  `23b6ecef75aca280a45e8abf7e28410829e20c0deca079cf0203320b373d8bce`
- ABP: 197,180 bytes, 12 routes, 3,521 instructions, two lanes.
- Native Guard v5 is present with all eight approved ranges, 750 ms stability,
  1 lux hysteresis, 5,000 ms Whisper cooldown, and 1,000,000 ms Whisper Repeat
  cooldown.
- Verified compact personal profile: pause P50/P90 399/399 ms, turn P50/P90
  4/18 degrees, long-move 67%, efficiency 74%, correction 4%, speed
  444–1,641 px/s, and tempo 8 ms.

Phase 2 (`0.9.69`) consumes the previously exported but unused pause, turn, and
long-move statistics. All derived choices remain bounded by authored ranges,
screen limits, and fixed probability caps. No identifiable trace is replayed.

Phase 2 local validation:

- 69 Portable/ABVM contract files passed.
- Pico Bridge transport: 18 passed, 0 failed.
- Native ABVM C smoke passed.
- Pico firmware and all 30 identity templates compiled.
- Windows x64 publish is a verified PE32+ GUI executable.
- TestRunner and LightTelemetryTests compiled with 0 errors.
- Final NB01 v3.1 UF2 SHA-256:
  `bfa41829b71f77d063341c4f120c4aee8f0e1426e50fabc07dee965fc8450b15`
- The v3.1 firmware carries the exact phase-one ABP SHA-256
  `23b6ecef75aca280a45e8abf7e28410829e20c0deca079cf0203320b373d8bce`;
  only the board-side mouse actor changed.

## Phase 2 curvature feedback and v3.2

Hardware feedback on v3.1 reported excessive visible bowing in every movement.
The supplied result UF2 has SHA-256
`1d4d2bbe180da4702ce5f4e49373e0d63b23618f6efb170a7f709d527e7680aa`.
Its firmware bytes outside the ABP slot are identical to the delivered v3.1
firmware, confirming that the curve engine—not Guard or the project—is the
source of the behavior.

Root cause: the previous Native formula mapped curve 100 to a lateral control
height of 31.2% of movement distance. v3.2 changes the mapping to 2% at 100,
6% at 139, 15% at 169, 30% at 189, and reserves 30–50% geometry for sampled
authored values 190–200. The 190–200 band uses a direction-locked two-leg
Circular Mode; ordinary multi-leg paths require at least 600 px and bend no
more than 4%.

The user's tested ABP contains 36 mouse constants. Its authored ranges include
only four 120–195 constants capable of occasionally sampling Circular Mode;
ordinary actions remain in lower ranges. The ABP, Guard, fishing, Catch, and
light profiles remain unchanged for the v3.2 comparison.

v3.2 local validation:

- 70 Portable/ABVM contract files passed.
- Pico Bridge transport: 18 passed, 0 failed.
- Native ABVM C smoke passed.
- Pico firmware and 30 identity templates compiled.
- Windows x64 publish is a verified PE32+ GUI executable.
- TestRunner and LightTelemetryTests compiled with 0 errors.
- Final NB01 v3.2 UF2 SHA-256:
  `07f2d91ced677b0901abdb44de59a806de3cc484efa28707e17ecaefbf9b99ca`
- The final image preserves the user's tested ABP exactly: SHA-256
  `9011f0ec787f563029d329764b4dffc5007574702834268ce6b816a8af72dca8`,
  197,148 bytes, 3,519 instructions, 12 routes, and one Native Guard.
