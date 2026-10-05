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
