"""Build 120: deep-stack-safe button cues and Type Text in Game responses."""
import ast
import os
import sys
import tempfile
import types

ROOT = os.path.dirname(os.path.dirname(__file__))
MODERN = os.path.join(ROOT, "CIRCUITPY-MODERN")
sys.path.insert(0, MODERN)


def function_source(path, name):
    source = open(path, encoding="utf-8").read()
    tree = ast.parse(source)
    node = next(item for item in tree.body
                if isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef))
                and item.name == name)
    return ast.get_source_segment(source, node)


code_path = os.path.join(MODERN, "code.py")
for name in ("_guard_start_tone", "_guard_stop_tone",
             "_guard_pause_tone", "_guard_resume_tone"):
    body = function_source(code_path, name)
    assert "_queue_guard_cue" in body
    assert "_guard_pattern" not in body
    assert "sleep(" not in body

stop_body = function_source(code_path, "_immediate_audible_stop")
assert "self.controls.running = False" in stop_body
assert "self.keyboard.release_all()" in stop_body
assert "self.guard_stop_tone()" in stop_body

clock = types.SimpleNamespace(value=10.0, monotonic=lambda: clock.value)
tones = []
class Tone:
    def __init__(self, pin, duty_cycle, frequency, variable_frequency):
        self.frequency = frequency
        self.duty_cycle = duty_cycle
        self.closed = False
        tones.append(self)
    def deinit(self):
        self.closed = True

real_time = sys.modules.get("time")
sys.modules["board"] = types.SimpleNamespace(GP6=6)
sys.modules["pwmio"] = types.SimpleNamespace(PWMOut=Tone)
sys.modules["time"] = clock
import guard_button_cues as cue
if real_time is not None:
    sys.modules["time"] = real_time
board = types.SimpleNamespace(
    guard_cue_name=None, guard_cue_index=0, guard_cue_next=0,
    guard_cue_tone=None, emit=lambda value: None)
cue.queue(board, "pause")
cue.tick(board)
assert tones[-1].frequency == 523 and not tones[-1].closed
clock.value += .180
cue.tick(board)
assert tones[-1].closed and board.guard_cue_tone is None
clock.value += .100
cue.tick(board)
assert tones[-1].frequency == 523 and not tones[-1].closed

import plan_engine_game_actions as actions

events = []


class Core:
    @staticmethod
    def _type(ctx):
        def run(args, run_ctx):
            events.append((args, run_ctx))
        return run


ctx = object()
actions.bind(Core, None, None)
assert actions.leaf("TYPE", "text=hi%20there|h=80,220", ctx, {}) is True
assert events == [("text=hi%20there|h=80,220", ctx)]

game_path = os.path.join(MODERN, "plan_engine_game.py")
game_source = open(game_path, encoding="utf-8").read()
inventory_source = open(os.path.join(
    MODERN, "plan_engine_game_inventory.py"), encoding="utf-8").read()
assert "before-type-preload" in inventory_source
assert "_has_type(response)" in inventory_source

import plan_engine_game_inventory as inventory
with tempfile.NamedTemporaryFile("w", delete=False) as response:
    response.write("PLAN|2\nTYPE|text=whisper%20reply|h=80,220\n")
    response_name = response.name.lstrip("/")
with tempfile.NamedTemporaryFile("w", delete=False) as route:
    route.write("PLAN|2\nSOUNDWATCH|whisper,1,511,20,10,900,%s,global\n"
                % response_name)
    route_name = route.name.lstrip("/")
try:
    parallel, needs_type, offsets = inventory.scan(route_name)
    assert not parallel and needs_type and len(offsets) == 8
finally:
    os.unlink("/" + route_name)
    os.unlink("/" + response_name)

print("Build 120 button cue and Game Type Text contracts: 15 passed, 0 failed")