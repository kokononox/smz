#!/usr/bin/env python3
"""The 10-second hand path must not import/parse the large PLAN engine on RP2040."""
import ast
from pathlib import Path
from types import SimpleNamespace

code_path = Path(__file__).resolve().parents[1] / "CIRCUITPY-MODERN" / "code.py"
tree = ast.parse(code_path.read_text(encoding="utf-8"))
selected = []
for node in tree.body:
    if isinstance(node, ast.Assign) and any(
        isinstance(target, ast.Name) and target.id == "_LIGHT_ROUTE_COMMANDS"
        for target in node.targets
    ):
        selected.append(node)
    elif isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name in {
        "_light_route_lines", "_run_light_route"
    }:
        selected.append(node)

events = []

class Ctx:
    screen_w = 0
    screen_h = 0
    r = object()
    elapsed = 0.0

    def raw(self, line):
        events.append(("raw", line))
        return "OK|RAW"

    def mmove_relative(self, dx, dy):
        events.append(("relative", dx, dy))

    def relative_batch_start(self, started): return False

    def sleep_ms(self, milliseconds):
        events.append(("delay", milliseconds))
        self.elapsed += milliseconds / 1000.0
        return True

    def now(self):
        return self.elapsed

    def key_combo(self, *args): events.append(("key",) + args)
    def kdown(self, value): events.append(("down", value))
    def kup(self, value): events.append(("up", value))
    def beep(self, *args): events.append(("beep",) + args)

namespace = {
    "runtime": SimpleNamespace(random=SimpleNamespace(randint=lambda lo, hi: lo)),
    "_debug_event": lambda *args, **kwargs: None,
}
exec(compile(ast.Module(body=selected, type_ignores=[]), str(code_path), "exec"), namespace)

route = """PLAN|2
SCREEN|1920,1080
SPEED|300,2000
LOOPTIME|0.4
HANDPATH|100,47,-5;100,47,-6
ENDLOOP
"""
commands = namespace["_light_route_lines"](route)
assert commands is not None, "HANDPATH LOOPTIME path fell back to the full plan engine"
assert [command for command, _ in commands].count("HANDPATH") == 1
ctx = Ctx()
namespace["_run_light_route"](ctx, commands)
assert events == [
    ("delay", 100),
    ("relative", 47, -5),
    ("delay", 100),
    ("relative", 47, -6),
    ("delay", 100),
    ("relative", 47, -5),
    ("delay", 100),
    ("relative", 47, -6),
], events
print("hand-sample HANDPATH light route: 10 passed, 0 failed")

batch_events = []

class BatchCtx(Ctx):
    elapsed = 0.0
    def relative_batch_start(self, started): return True
    def relative_batch_add(self, target_due, dx, dy):
        batch_events.append((target_due, dx, dy))
    def relative_batch_flush(self): batch_events.append(("flush",))
    def gate(self): return True

route = "PLAN|2\nHANDPATH|" + ";".join("8,2,1" for _ in range(10)) + "\n"
commands = namespace["_light_route_lines"](route)
namespace["_run_light_route"](BatchCtx(), commands)
assert batch_events[:-1] == [(8 * i, 2, 1) for i in range(1, 11)], batch_events
assert batch_events[-1] == ("flush",)
print("hand-sample bounded batch route: 8 passed, 0 failed")