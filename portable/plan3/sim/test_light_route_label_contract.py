"""Low-memory LABEL/GOTO contract for the modern Pico route runner."""

import ast
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
SOURCE = ROOT / "portable" / "plan3" / "CIRCUITPY-MODERN" / "code.py"
tree = ast.parse(SOURCE.read_text(encoding="utf-8"))
selected = [
    node for node in tree.body
    if (isinstance(node, ast.FunctionDef)
        and node.name in {"_light_route_lines", "_run_light_route"})
    or (isinstance(node, ast.Assign)
        and any(isinstance(target, ast.Name) and target.id == "_LIGHT_ROUTE_COMMANDS"
                for target in node.targets))
]
namespace = {}


class _Random:
    @staticmethod
    def randint(lo, hi):
        return lo


class _Runtime:
    random = _Random()


namespace.update(runtime=_Runtime(), gc=type("_Gc", (), {"collect": staticmethod(lambda: None)})(),
                 _debug_event=lambda *args, **kwargs: None)
exec(compile(ast.Module(body=selected, type_ignores=[]), str(SOURCE), "exec"), namespace)

parse = namespace["_light_route_lines"]
run = namespace["_run_light_route"]
commands = parse("PLAN|2\nLABEL|again\nDELAY|1\nGOTO|again\n")
assert commands is not None and [item[0] for item in commands] == [
    "PLAN", "LABEL", "DELAY", "GOTO"
]


class _Context:
    def __init__(self):
        self.sleeps = 0

    def sleep_ms(self, _milliseconds):
        self.sleeps += 1
        return self.sleeps < 3


ctx = _Context()
try:
    run(ctx, commands)
    raise AssertionError("backward GOTO must repeat until the test gate aborts")
except RuntimeError as exc:
    assert str(exc) == "route aborted"
assert ctx.sleeps == 3

for invalid in (
    parse("PLAN|2\nLABEL|same\nLABEL|same\n"),
    parse("PLAN|2\nGOTO|missing\n"),
):
    try:
        run(_Context(), invalid)
        raise AssertionError("invalid LABEL/GOTO route was accepted")
    except ValueError:
        pass

print("light-route LABEL/GOTO contract passed")
