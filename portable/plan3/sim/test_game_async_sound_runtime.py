#!/usr/bin/env python3
"""Game light runner: async ARM sound detection must preempt active RMOUSE."""
import random
import sys
from pathlib import Path

MODERN = Path(__file__).resolve().parents[1] / "CIRCUITPY-MODERN"
sys.path.insert(0, str(MODERN))
import plan_engine_game


class Context:
    screen_w = 1920
    screen_h = 1080

    def __init__(self):
        self.time_ms = 0
        self.events = []
        self.sound_started = False

    def now(self):
        return self.time_ms / 1000

    def gate(self):
        return True

    def sleep_ms(self, value):
        self.time_ms += value
        return True

    def log(self, value):
        self.events.append(("log", self.time_ms, value))

    def mmove_relative(self, dx, dy):
        self.events.append(("move", self.time_ms, dx, dy))

    def key_combo(self, values, hold_min, hold_max):
        self.events.append(("key", self.time_ms, tuple(values)))

    def sound_start(self, threshold, minimum, timeout):
        self.sound_started = True
        self.events.append(("sound-start", self.time_ms))

    def sound_parallel_safe(self):
        return True

    def sound_poll(self):
        self.events.append(("sound-poll", self.time_ms))
        return True if self.time_ms >= 30 else None

    def sound_cancel(self):
        self.events.append(("sound-cancel", self.time_ms))


commands = [
    ("PLAN", "2"),
    ("SCREEN", "1920,1080"),
    ("PGROUP", ""),
    ("LOOP", "0"),
    (
        "RMOUSE",
        "region=1301,0,378,1049|before=0,0|after=0,0|curve=3,22|"
        "mid=0:0,0|over=0|mt=600,600|idle=99,99:0,0",
    ),
    ("ENDLOOP", ""),
    ("PARITEM", ""),
    ("WSND", "22,60,600000"),
    ("KEY", "combo=70|hold=80,180"),
    ("ENDPAR", ""),
]

ctx = Context()
random.seed(9)
plan_engine_game.run_game(commands, ctx)
moves = [event for event in ctx.events if event[0] == "move"]
polls = [event for event in ctx.events if event[0] == "sound-poll"]
keys = [event for event in ctx.events if event[0] == "key"]
assert moves and polls and keys
assert any(moves[0][1] <= event[1] <= moves[-1][1] for event in polls)
assert keys[0][2] == (70,)
assert max(event[1] for event in moves) <= keys[0][1]
assert keys[0][1] <= 40, ctx.events
print("game async sound runtime: PASS")