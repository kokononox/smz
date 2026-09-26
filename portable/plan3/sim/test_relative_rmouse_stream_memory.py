#!/usr/bin/env python3
"""Relative RMOUSE must not allocate the dense desktop mouse plan on Pico."""
from pathlib import Path
import random
import sys

ROOT = Path(__file__).resolve().parents[1]
MODERN = ROOT / "CIRCUITPY-MODERN"
sys.path.insert(0, str(MODERN))
for name in ("plan_engine", "plan_engine_parse", "plan_engine_human",
             "plan_engine_exec"):
    sys.modules.pop(name, None)
import plan_engine
assert "plan_engine_human" not in sys.modules
assert "plan_engine_parallel" not in sys.modules
assert "plan_engine_exec" not in sys.modules

# Natural-v1 keeps the configured duration as the medium-distance baseline and
# changes only path-level timing.  The low-level relative transport remains bounded.
random.seed(901)
short_ms = plan_engine._natural_move_ms(800, 800, 80)
random.seed(901)
medium_ms = plan_engine._natural_move_ms(800, 800, 325)
random.seed(901)
long_ms = plan_engine._natural_move_ms(800, 800, 650)
assert short_ms < medium_ms < long_ms, (short_ms, medium_ms, long_ms)


class RelativeCtx:
    plan_api = 3
    mouse_mode = "relative"
    screen_w = 1920
    screen_h = 1080
    speed_min = 0
    speed_max = 2000

    def __init__(self):
        self.rel = []
        self.logs = []
        self.t = 0

    def now(self): return self.t
    def gate(self): return True
    def sleep_ms(self, ms): self.t += ms / 1000.0; return True
    def setres(self, w, h): self.screen_w, self.screen_h = w, h
    def get_mouse_pos(self): return None
    def set_mouse_pos(self, x, y):
        raise AssertionError("relative RMOUSE persisted a fake absolute cursor")
    def mmove(self, x, y):
        raise AssertionError("relative RMOUSE sent absolute coordinates")
    def mmove_relative(self, dx, dy): self.rel.append((int(dx), int(dy)))
    def log(self, text): self.logs.append(text)


route = "\n".join((
    "PLAN|2",
    "SCREEN|1920,1080",
    "SPEED|300,2000",
    "RMOUSE|region=1301,0,378,1049|before=0,0|after=0,0|"
    "curve=1,200|mid=50:142,528|over=29|speed=515,1725|"
    "idle=5,12:0,0",
))
ops = plan_engine.parse_plan(route)
for seed in range(200):
    random.seed(seed)
    ctx = RelativeCtx()
    plan_engine.run_plan(ops, ctx)
    assert 1 <= len(ctx.rel) <= 128, (seed, len(ctx.rel))
    dx = sum(p[0] for p in ctx.rel)
    dy = sum(p[1] for p in ctx.rel)
    assert 125 <= abs(dx) <= 377, (seed, dx, dy)
    assert 90 <= abs(dy) <= 270, (seed, dx, dy)
    assert all(abs(x) <= 127 and abs(y) <= 127 for x, y in ctx.rel)
    assert any("rmouse-rel-stream" in line for line in ctx.logs)
    assert any("plan-lite-relative" in line for line in ctx.logs)

assert "plan_engine_human" not in sys.modules
assert "plan_engine_parallel" not in sys.modules
assert "plan_engine_exec" not in sys.modules
print("relative RMOUSE bounded stream: 200 passed, 0 failed")
