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
    def relative_batch_ready(self): return True
    def mmove_relative_batch(self, payload):
        assert len(payload) % 10 == 0
        parts = []
        for offset in range(0, len(payload), 10):
            part = payload[offset:offset + 10]
            delay = int(part[:2], 16)
            dx = int(part[2:6], 16); dx = dx - 0x10000 if dx & 0x8000 else dx
            dy = int(part[6:10], 16); dy = dy - 0x10000 if dy & 0x8000 else dy
            parts.append((delay, dx, dy))
        assert len(parts) <= 6
        assert sum(part[0] for part in parts) <= 64
        batch_events.append(parts)
        self.elapsed += sum(part[0] for part in parts) / 1000.0
    def gate(self): return True

route = "PLAN|2\nHANDPATH|" + ";".join("8,2,1" for _ in range(10)) + "\n"
commands = namespace["_light_route_lines"](route)
namespace["_run_light_route"](BatchCtx(), commands)
flat = [part for batch in batch_events for part in batch]
assert len(batch_events) == 2, batch_events
assert flat == [(8, 2, 1)] * 10, flat
print("hand-sample bounded batch route: 8 passed, 0 failed")