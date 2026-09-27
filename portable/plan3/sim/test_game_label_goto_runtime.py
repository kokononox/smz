#!/usr/bin/env python3
"""Game light runner must keep LABEL/GOTO on the low-memory path."""
import sys
from pathlib import Path
MODERN = Path(__file__).resolve().parents[1] / "CIRCUITPY-MODERN"
sys.path.insert(0, str(MODERN))
import plan_engine_game

class Context:
    screen_w = 1920
    screen_h = 1080
    def __init__(self): self.keys=[]; self.time_ms=0
    def now(self): return self.time_ms / 1000
    def gate(self): return True
    def sleep_ms(self, value): self.time_ms += value; return True
    def key_combo(self, values, hold_min, hold_max): self.keys.append(tuple(values))
    def mmove_relative(self, dx, dy): pass
    def log(self, value): pass
    def sound_start(self, threshold, minimum, timeout): pass
    def sound_poll(self): return False
    def sound_cancel(self): pass

commands=[
 ("PLAN","2"),("LABEL","start"),("KEY","combo=65|hold=0,0"),
 ("GOTO","end"),("KEY","combo=66|hold=0,0"),
 ("LABEL","end"),("KEY","combo=67|hold=0,0"),
]
ctx=Context(); plan_engine_game.run_game(commands,ctx)
assert ctx.keys == [(65,), (67,)], ctx.keys
try:
 plan_engine_game.run_game([("GOTO","missing")],Context())
except ValueError as exc:
 assert "label not found" in str(exc)
else:
 raise AssertionError("missing GOTO target was accepted")
print("game LABEL/GOTO runtime: PASS")
