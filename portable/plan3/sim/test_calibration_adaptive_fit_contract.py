from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
fw = ROOT / "portable/plan3/CIRCUITPY-MODERN"
runtime = (fw / "combined_guard_runtime.py").read_text(encoding="utf-8")
protocol = (fw / "guard_calibration_protocol.py").read_text(encoding="utf-8")
fitter = (fw / "calibration_fit.py").read_text(encoding="utf-8")
code = (fw / "code.py").read_text(encoding="utf-8")

assert "def fit_calibration_profiles(" not in protocol
assert "def prepare(" in fitter
assert "SAFETY_GAP = 0.25" in fitter
assert "MINIMUM = 0.5" in fitter
assert "Centres remain fixed." in fitter
assert 'fitter = __import__("calibration_fit")' in runtime
assert '__import__("sys").modules.pop("calibration_fit", None)' in runtime
assert '"calibration_fit.py"' in code
assert runtime.index('__import__("calibration_fit")') < runtime.index(
    "calibration_nvm.save(", runtime.index("def _publish_calibration"))
assert len(runtime.encode()) < 40000
assert len(protocol.encode()) < 5500

print("lazy adaptive calibration fit and boot-memory contract passed")
