from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
fw = ROOT / "portable/plan3/CIRCUITPY-MODERN"
runtime = (fw / "combined_guard_runtime.py").read_text(encoding="utf-8")
protocol = (fw / "guard_calibration_protocol.py").read_text(encoding="utf-8")
nvm = (fw / "calibration_nvm.py").read_text(encoding="utf-8")
code = (fw / "code.py").read_text(encoding="utf-8")

assert "def fit_calibration_profiles(" not in protocol
assert "def fit_profiles(" in nvm
assert "FIT_GAP = 0.25" in nvm
assert "FIT_MIN = 0.5" in nvm
assert "Fixed centres" in nvm
assert "reason=centers-identical" in nvm
assert "calibration_nvm.fit_profiles(" in runtime
assert "self.last_cal_fit" in runtime
assert '__import__("calibration_fit")' not in runtime
assert '"calibration_fit.py"' not in code
assert runtime.index("calibration_nvm.fit_profiles(") < runtime.index(
    "calibration_nvm.save(", runtime.index("def _publish_calibration"))
assert len(runtime.encode()) < 40000
assert len(protocol.encode()) < 5500
assert len(nvm.encode()) < 6500
assert "_CAL_OVERLAP_ADJUSTED_PATTERN" in code
assert "self.cal_overlap_adjusted_tone()" in code

print("preloaded adaptive calibration fit and boot/save-memory contract passed")
