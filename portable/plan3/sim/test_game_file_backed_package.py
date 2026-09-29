#!/usr/bin/env python3
"""Large Game packages stay file-backed and sample only the chosen items."""
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
FW = ROOT / "portable/plan3/CIRCUITPY-MODERN"
sys.path.insert(0, str(FW))

import plan_engine_game as game
import plan_engine_game_core as core
import plan_engine_game_runtime as runtime

rows = ["PLAN|2", "SCREEN|1920,1080", "RPKG|pick,1,2"]
for index in range(165):
    if index:
        rows.append("PKGITEM")
    rows.append("DELAY|0,0")
rows.append("ENDPKG")

with tempfile.NamedTemporaryFile("w", delete=False) as route:
    route.write("\n".join(rows) + "\n")
    route_name = route.name.lstrip("/")

commands = core._FileCommands(route_name)
try:
    assert isinstance(commands.offsets, bytearray)
    assert len(commands.offsets) == len(rows) * 4
    assert commands[0] == ("PLAN", "2")
    package_index = 2
    finish, parts, order = core._package(commands, package_index)
    assert commands[finish][0] == "ENDPKG"
    assert 1 <= len(parts) <= 2
    assert len(order) == len(parts)
finally:
    commands.close()


class Context:
    plan_api = 3
    screen_w = 1920
    screen_h = 1080
    speed_min = 0
    speed_max = 2000

    def gate(self): return True
    def now(self): return 0.0
    def sleep_ms(self, _milliseconds): return True
    def log(self, _message): pass


game.run_game_file(route_name, Context())
Path("/" + route_name).unlink()

# File-backed PGROUP routes must compile Parallel before indexing/execution,
# while the heap is still fresh rather than after a long package prelude.
parallel_rows = ["PLAN|2", "PGROUP", "DELAY|0,0", "PARITEM",
                 "DELAY|0,0", "ENDPAR"]
with tempfile.NamedTemporaryFile("w", delete=False) as route:
    route.write("\n".join(parallel_rows) + "\n")
    parallel_name = route.name.lstrip("/")
sys.modules.pop("plan_engine_game_parallel", None)
telemetry = []
class ParallelContext(Context):
    class R:
        def emit(self, value): telemetry.append(value)
    r = R()
game.run_game_file(parallel_name, ParallelContext())
Path("/" + parallel_name).unlink()
assert "plan_engine_game_parallel" in sys.modules
assert any("before-file-index-reserve" in value for value in telemetry), telemetry
assert any("after-file-index-reserve|commands=6|offset-bytes=24" in value
           for value in telemetry), telemetry
assert any("before-parallel-preload" in value for value in telemetry), telemetry
assert any("after-parallel-preload" in value for value in telemetry), telemetry

# The reserved bytearray is accepted without rescanning/reallocating offsets.
with tempfile.NamedTemporaryFile("w", delete=False) as route:
    route.write("PLAN|2\nDELAY|0,0\n")
    reserved_name = route.name.lstrip("/")
needs_parallel, reserved = game._file_inventory(reserved_name)
assert not needs_parallel and isinstance(reserved, bytearray) and len(reserved) == 8
reserved_commands = core._FileCommands(reserved_name, reserved)
try:
    assert reserved_commands.offsets is reserved
    assert reserved_commands[1] == ("DELAY", "0,0")
finally:
    reserved_commands.close()
Path("/" + reserved_name).unlink()

# PGROUP branches must flatten nested LOOP/RPKG containers without recursively
# nesting _events generators around the lazy mouse generator.  That call shape
# exhausted CircuitPython's pystack on the first fishing RMOUSE while heap was
# still healthy.
runtime._core = core
original_mouse_events = core._mouse_events
def fake_mouse_events(_args, _ctx, _state):
    yield ("move", 7, 3, -2)
core._mouse_events = fake_mouse_events
nested = [
    ("LOOP", "2"), ("RPKG", "seq,1,2"), ("RMOUSE", "test"),
    ("PKGITEM", ""), ("DELAY", "5,5"), ("ENDPKG", ""), ("ENDLOOP", "")]
state = {"speed": [0, 2000], "pos": [960, 540], "pauses": None}
events = list(runtime._events(nested, 0, len(nested), Context(), state))
core._mouse_events = original_mouse_events
assert events == [
    ("move", 7, 3, -2), ("wait", 5),
    ("move", 7, 3, -2), ("wait", 5)], events
event_source = (FW / "plan_engine_game_runtime.py").read_text(encoding="utf-8")
event_body = event_source.split("def _events(", 1)[1].split(
    "\n\ndef _response_commands", 1)[0]
assert "_events(commands," not in event_body, event_body

source = (FW / "code.py").read_text(encoding="utf-8")
assert 'commands = name if name == "game_steps.txt"' in source
assert "plan_engine_game.run_game_file(commands, ctx)" in source
assert "file-index|commands=%d|offset-bytes=%d" in (
    FW / "plan_engine_game.py").read_text(encoding="utf-8")
print("file-backed Game route + bounded RPKG + iterative container stack passed")
