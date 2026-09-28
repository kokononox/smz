#!/usr/bin/env python3
"""Regression for Bundle 175 FAT cross-link and delayed calibration buttons."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
code = (ROOT / "portable/plan3/CIRCUITPY-MODERN/code.py").read_text(encoding="utf-8")
service = (ROOT / "ams-shell/src/Ams.UI/Services/ModernAutoCycleFirmwareBundle.cs").read_text(
    encoding="utf-8"
)
deploy = (ROOT / "ams-shell/src/Ams.UI/ViewModels/MainViewModel.AutoCycleModern.cs").read_text(
    encoding="utf-8"
)

# Runtime diagnostics remain available over CDC and NVM but never compete with
# Windows mass-storage writes on the same FAT volume.
assert "_DEBUG_FILE" not in code
assert "open(_DEBUG_FILE" not in code
assert "runtime.storage.remount" not in code
assert "NVM and live CDC events are sufficient" in code
assert '_DEBUG_PERSIST_EVENTS = ("BOOT", "ROUTE", "FAIL", "STOP", "CAL")' in code

# Physical down/up/long evidence is live-only. It must not perform a blocking
# 1536-byte NVM persistence before the note/action.
assert '"GP4", "down running=%s calibrating=%s" % (self.controls.running, self.calibrating))' in code
assert '"GP4", "long calibrating=%s" % self.calibrating)' in code
assert '"GP4", "up")' in code
assert '"GP3", "long sound-calibrating=%s" % self.sound_calibrating)' in code

# Classroom cannot report success from source bytes alone.
assert "public static void VerifyExportedTarget(string targetRoot)" in service
assert "CryptographicOperations.FixedTimeEquals" in service
assert "guard-calibration.json" in service and "guard-transition.json" in service
assert 'await _bridge.SendAsync("HALT|SILENT", 3)' in deploy
assert "VerifyExportedTarget(targetRoot)" in deploy
assert "attempt <= 5" in deploy
assert "34/34 hashes and Guard revisions OK" in deploy

print("USB FAT isolation, responsive buttons, and target read-back: 17 passed, 0 failed")
