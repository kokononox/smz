# Project profile: smz

Purpose: build Classroom Studio plus Pico/Pro Micro automation firmware with deterministic export, recovery, light-state routing, sound reactions, and natural mouse execution.

Invariants:
- `Golden 100` remains unchanged unless the task explicitly targets it.
- The active Classroom release branch is updated only through a green Pull Request.
- Pico code must remain memory-bounded: stream large routes, avoid nested top-level imports, release deferred modules, and respect Windows CRLF size gates.
- `SHA256SUMS.txt`, runtime inventory, exported files, and read-back verification must agree exactly.
- Preserve `.amsj` backward compatibility and migration behavior.
- Preserve verified Natural Mouse, ARM sound cadence, restart/resume, calibration NVM, and light preemption unless the task explicitly changes them.
- GuardHardwareMonitor remains safe-portable diagnostics; no automatic Guard/HID commands.
- A release is a candidate until hardware logs confirm the changed path.

Minimum firmware evidence: focused simulator contract, all portable contracts, Windows TestRunner, ARM compile, security gate, manifest/hash validation, and a versioned Release ZIP.
