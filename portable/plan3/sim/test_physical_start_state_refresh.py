from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
code = (ROOT / "portable/plan3/CIRCUITPY-MODERN/code.py").read_text(
    encoding="utf-8")

start = code.index("if getattr(self, \"blue_start_pending\", False):")
end = code.index("elif not stop_consumed and not start_consumed", start)
physical_start = code[start:end]

assert "self.guard.reset()" in physical_start
assert "self.guard.last_decision = None" in physical_start
assert 'self.debug_last_state = "__start__"' in physical_start
assert "self.debug_last_denied = None" in physical_start
assert "EVT|CALSTATUS|source=%s|id=character-dashboard|" in physical_start
assert 'getattr(self, "calibration_source", "file")' in physical_start

assert 'EVT|CALSTATUS|source=%s|count=%d' in code

print("physical GP4 Start refreshes state telemetry and reports effective calibration")