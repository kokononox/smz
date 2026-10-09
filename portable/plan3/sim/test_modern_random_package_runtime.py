#!/usr/bin/env python3
"""Regression: Modern split executor must run shuffled Random Package plans."""
import random
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MODERN = ROOT / "CIRCUITPY-MODERN"
sys.path.insert(0, str(MODERN))
for name in ("plan_engine", "plan_engine_exec", "plan_engine_human", "plan_engine_parse"):
    sys.modules.pop(name, None)
import plan_engine

class Ctx:
    plan_api = 3
    screen_w = 1920
    screen_h = 1080
    speed_min = 300
    speed_max = 2000
    mouse_mode = "relative"

    def __init__(self):
        self.delays = []
        self.logs = []
        self.t = 0.0

    def now(self): return self.t
    def gate(self): return True
    def sleep_ms(self, ms):
        self.delays.append(ms)
        self.t += ms / 1000.0
        return True
    def log(self, message): self.logs.append(message)

text = "\n".join((
    "PLAN|2",
    "RPKG|all,1,2",
    "DELAY|11,11",
    "PKGITEM",
    "DELAY|22,22",
    "ENDPKG",
))
ctx = Ctx()
random.seed(7)
plan_engine.run_plan(plan_engine.parse_plan(text), ctx)
assert sorted(ctx.delays) == [11, 22], ctx.delays
assert any(message == "package: 2 of 2" for message in ctx.logs), ctx.logs
print("modern random package runtime: 2 passed, 0 failed")
