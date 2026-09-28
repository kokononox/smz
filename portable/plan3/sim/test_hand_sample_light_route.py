#!/usr/bin/env python3
"""The 10-second hand path must not import/parse the large PLAN engine on RP2040."""
import ast
import tempfile
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
        "_light_route_rows", "_light_route_lines", "_light_route_file", "_run_light_route"
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
# A large Game-like route must be parsed line-by-line so RP2040 never needs
# one contiguous source-string allocation.
with tempfile.NamedTemporaryFile("w", delete=False) as large:
    large.write("PLAN|2\nSCREEN|1920,1080\nLOOPTIME|600\n")
    large.writelines("DELAY|88,188\n" for _ in range(360))
    large.write("ENDLOOP\n")
    large_name = large.name.lstrip("/")
large_commands = namespace["_light_route_file"](large_name)
Path("/" + large_name).unlink()
assert large_commands is not None and len(large_commands) > 360
assert large_commands[-1][0] == "ENDLOOP"
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