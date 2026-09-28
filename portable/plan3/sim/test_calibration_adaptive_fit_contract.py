from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
runtime = (ROOT / "portable/plan3/CIRCUITPY-MODERN"
           / "combined_guard_runtime.py").read_text(encoding="utf-8")
protocol = (ROOT / "portable/plan3/CIRCUITPY-MODERN"
            / "guard_calibration_protocol.py").read_text(encoding="utf-8")

assert "def fit_calibration_profiles(" in protocol
assert "CAL_FIT_SAFETY_GAP_LUX = 0.25" in protocol
assert "CAL_FIT_MIN_TOLERANCE_LUX = 0.5" in protocol
assert "Centres never move." in protocol
assert "fit_calibration_profiles," in runtime
assert "profiles, fit = fit_calibration_profiles(" in runtime
assert "EVT|CAL|FIT|id=%s|requested=%.3f|applied=%.3f|" in runtime
assert "EVT|CAL|FIT-PAIR|new=%s:%.3f->%.3f|" in runtime
assert "adjusted=%s:%.3f->%.3f|gap=%.3f" in runtime
assert "ERR|CAL|FIT|id=%s|with=%s|reason=%s|gap=%.3f" in runtime
assert runtime.index("fit_calibration_profiles(") < runtime.index(
    "calibration_nvm.save(", runtime.index("def _publish_calibration"))

print("adaptive one/two-sided calibration fit contract passed")