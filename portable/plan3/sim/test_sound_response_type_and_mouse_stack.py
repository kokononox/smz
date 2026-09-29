#!/usr/bin/env python3
"""Sound responses type text; Catch/Resume mouse path stays pystack-flat."""
import sys
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[3]
FW = ROOT / "portable/plan3/CIRCUITPY-MODERN"
sys.path.insert(0, str(FW))

import plan_engine_game_actions as actions
import plan_engine_login_core as login_core
import plan_engine_login_type as login_type_module


class Keyboard:
    def __init__(self):
        self.pressed = []
        self.released = []

    def press(self, code):
        self.pressed.append(code)

    def release(self, code):
        self.released.append(code)


class Context:
    def __init__(self):
        self.r = SimpleNamespace(keyboard=Keyboard())

    def gate(self):
        return True

    def sleep_ms(self, milliseconds):
        return True


core = SimpleNamespace()
actions.bind(core, None, None)
ctx = Context()
login_type_module.bind(login_core)
login_type_module.run_type("text=hi%20%3A%29|h=90,180", ctx)
assert ctx.r.keyboard.pressed == [11, 12, 44, 225, 51, 225, 39]
assert ctx.r.keyboard.released == [11, 12, 44, 51, 225, 39, 225]

response = (FW / "plan_engine_game_response.py").read_text(encoding="utf-8")
game_core = (FW / "plan_engine_game_core.py").read_text(encoding="utf-8")
login_mouse = (FW / "plan_engine_login_mouse.py").read_text(encoding="utf-8")
login_type = (FW / "plan_engine_login_type.py").read_text(encoding="utf-8")
assert '"TYPE"' in response
assert "helper.run_type(args, ctx)" in (FW / "plan_engine_game_actions.py").read_text(encoding="utf-8")
assert "def _send_text(" in login_type and '"!@#$%^&*()"' in login_type
assert "return helper.mouse_events(args, ctx" in game_core
assert "return _mouse_events(pos, tx, ty, cfg, pauses)" in login_mouse
assert "for event in helper.mouse_events" not in game_core
assert "for event in _mouse_events(pos, tx, ty, cfg, pauses)" not in login_mouse

print("sound Type Text and flat Catch/Resume mouse generator path passed")