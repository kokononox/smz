#!/usr/bin/env python3
"""Five-second lux calibration stays bounded and cannot build ~500 floats."""
from pathlib import Path
import ast
import types

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / "CIRCUITPY-MODERN" / "combined_guard_runtime.py"
tree = ast.parse(RUNTIME.read_text(encoding="utf-8"))
combined = next(node for node in tree.body
                if isinstance(node, ast.ClassDef) and node.name == "Combined")
cal_tick = next(node for node in combined.body
                if isinstance(node, ast.FunctionDef) and node.name == "cal_tick")

clock = [0.0]
time = types.SimpleNamespace(monotonic=lambda: clock[0])
events = []


def calibrated_profile(values, profiles, profile_id, stable):
    return {
        "center": values[len(values) // 2],
        "tolerance": 3.0,
        "stableMs": stable,
    }


class Sensor:
    def lux(self):
        return 66.7


fake = types.SimpleNamespace(
    calibrating=True,
    result="sampling",
    sample_started=0.0,
    sample_next=0.0,
    samples=[],
    sensor=Sensor(),
    stage=0,
    bundle={"calibration": {"profiles": {}}},
    saved=True,
    emit=events.append,
)
ns = {
    "time": time,
    "PROFILES": ("desktop",),
    "calibrated_profile": calibrated_profile,
}
exec(compile(ast.Module(body=[cal_tick], type_ignores=[]),
             str(RUNTIME), "exec"), ns)

for tick in range(1041):
    clock[0] = tick * .005
    ns["cal_tick"](fake)

assert isinstance(fake.result, dict), fake.result
assert 45 <= len(fake.samples) <= 52, len(fake.samples)
assert any("mode=complete-stage" in event for event in events), events
print("calibration sampling: five seconds complete with <=52 retained lux values")