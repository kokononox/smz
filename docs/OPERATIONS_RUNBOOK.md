# smz operations runbook

## Before a firmware or release run
- Confirm the workflow SHA contains the intended fix and targets the correct release branch.
- Record Build, Bundle, project file, Pico/ARM versions, calibration source, and expected route.
- Verify no overlapping publish/corrective workflow can write the same branch, tag, or Release.
- Run focused tests, full applicable CI, manifest/hash validation, and a one-device canary.

## Hardware test capture
- Preserve the GuardHardwareMonitor RAW and JSONL logs.
- Record state transitions, route stages, heap telemetry, sound events, restart/resume, and the first failure.
- Do not treat a manual stop, skipped job, or superseded SHA as the original failure.
- Redact usernames, private paths, and credentials before sharing.

## Failure handling
1. Classify the failure: application, Pico memory, ARM transport, light state, sound, restart, packaging, or CI contract.
2. Keep the first decisive error and nearby telemetry.
3. After two failed attempts with one strategy, stop and change the hypothesis.
4. Fix the root cause and add a regression test.
5. Rebuild from the corrected SHA; never rerun an obsolete artifact as proof.

## Release checklist
- Required checks green.
- Runtime inventory and all hashes valid.
- ZIP contents and checksum recorded.
- Release targets the merged SHA.
- Hardware test instructions and expected telemetry documented.
- Candidate remains pending until the changed hardware path passes.
