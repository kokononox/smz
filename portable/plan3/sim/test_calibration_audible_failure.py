#!/usr/bin/env python3
"""Physical calibration must never fail or reject a press silently."""
from pathlib import Path
import ast
import types

ROOT = Path(__file__).resolve().parents[1]
CODE = ROOT / "CIRCUITPY-MODERN" / "code.py"
tree = ast.parse(CODE.read_text(encoding="utf-8"))
node = next(item for item in tree.body
            if isinstance(item, ast.FunctionDef)
            and item.name == "_audible_cal_tick")

runtime = types.SimpleNamespace(
    PROFILES=("desktop", "login-or-dc", "character-dashboard",
              "entering-game-loading", "game", "targeted"))


def original_tick(owner):
    owner.result = None
    owner.emit("ERR|CAL|UNSTABLE|stage=3|spread=6.1")


def debug_event(owner, kind, detail="", persist=False):
    owner.debug.append((kind, detail, persist))


fake = types.SimpleNamespace(
    result="sampling", stage=2, events=[], error_tones=0, debug=[])
fake.emit = fake.events.append
fake.cal_save_error_tone = lambda: setattr(
    fake, "error_tones", fake.error_tones + 1)

ns = {
    "runtime": runtime,
    "_original_cal_tick": original_tick,
    "_prepare_calibration_heap": lambda owner: None,
    "_debug_event": debug_event,
}
exec(compile(ast.Module(body=[node], type_ignores=[]), str(CODE), "exec"), ns)
ns["_audible_cal_tick"](fake)

assert fake.error_tones == 1
assert fake.debug == [("CAL", "sample-failed stage=3 id=character-dashboard", True)]
print("calibration audible failure: unstable sample produces error tone and telemetry")
