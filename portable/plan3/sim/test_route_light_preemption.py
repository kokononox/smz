#!/usr/bin/env python3
"""A stable new light state must safely preempt the route currently running."""
import ast
import importlib.util
import sys
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[3]
FW = ROOT / "portable/plan3/CIRCUITPY-MODERN"
sys.path.insert(0, str(FW))

spec = importlib.util.spec_from_file_location("preempt_guard", FW / "live_light_guard.py")
guard_module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard_module)

code_path = FW / "code.py"
tree = ast.parse(code_path.read_text(encoding="utf-8"))
tick_node = next(node for node in tree.body
                 if isinstance(node, ast.FunctionDef)
                 and node.name == "_cycle_controls_tick")
clock = SimpleNamespace(value=0.3, monotonic=lambda: clock.value)
events = []
namespace = {
    "runtime": SimpleNamespace(time=clock),
    "_debug_event": lambda *args, **kwargs: events.append(("debug", args[1:])),
}
exec(compile(ast.Module(body=[tick_node], type_ignores=[]), str(code_path), "exec"), namespace)

def state(name, lo, hi):
    return {"id": name, "lo": lo, "hi": hi, "route": name + "_steps.txt"}

guard = guard_module.LightStateGuard([
    state("desktop", 40, 50), state("login-or-dc", 1, 3),
    state("character-dashboard", 12, 19), state("entering-game-loading", 37, 40),
    state("game", 23, 27), state("targeted", 19, 21),
], stable_ms=300, hysteresis=1, sensor_timeout_ms=1500)
# Establish Login as the current stable state/stage and consume its decision.
guard.update(2, 0)
assert guard.update(2, 300) == "login-or-dc"
guard.last_decision = None

class Controls:
    running = True
    aborted = False

class Keyboard:
    released = False
    def release_all(self): self.released = True

class Arm:
    aborted = False
    def abort(self): self.aborted = True

class Runtime:
    def __init__(self):
        self.controls = Controls()
        self.keyboard = Keyboard()
        self.arm = Arm()
        self.guard = guard
        self.sensor = SimpleNamespace(lux=lambda: 16.7)
        self.cycle = SimpleNamespace(route_tick=lambda: None)
        self.route_active_profile = "login-or-dc"
        self.route_light_last = 0
    def buttons(self): pass
    def emit(self, line): events.append(("emit", line))

board = Runtime()
tick = namespace["_cycle_controls_tick"]
# First Dashboard observation starts debounce and must not abort Login yet.
tick(board)
assert not board.controls.aborted
clock.value = 0.7
tick(board)
assert board.controls.aborted
assert board.controls.running, "preemption must preserve the overall Guard run"
assert board.keyboard.released and board.arm.aborted
assert guard.last_decision is not None
assert guard.last_decision["execute"] is True
assert guard.last_decision["profile"] == "character-dashboard"
assert any("EVT|GUARD|PREEMPT|from=login-or-dc|to=character-dashboard" in item[1]
           for item in events if item[0] == "emit")

source = code_path.read_text(encoding="utf-8")
assert 'self.route_active_profile = decision.get("profile")' in source
assert 'self.route_active_profile = None' in source
assert 'if self.controls.running:\n                        self.controls.aborted = False' in source
assert 'commands = name if name == "game_steps.txt" else _light_route_file(name)' in source
assert "plan_engine_game.run_game_file(commands, ctx)" in source
assert 'with open("/" + name, "r") as fh: text = fh.read()' not in source
print("stable light-state route preemption and streaming route read passed")
