from pathlib import Path
import importlib
import sys

ROOT = Path(__file__).resolve().parents[3]
FW = ROOT / "portable/plan3/CIRCUITPY-MODERN"
sys.path.insert(0, str(FW))
modules = ("plan_engine_game", "plan_engine_game_core", "plan_engine_game_runtime",
           "plan_engine_game_parallel", "plan_engine_login")
for name in modules:
    sys.modules.pop(name, None)

game = importlib.import_module("plan_engine_game")
assert all(name not in sys.modules for name in modules[1:])

class Runtime:
    def emit(self, value): pass

class Context:
    def __init__(self):
        self.r = Runtime(); self.screen_w = 1920; self.screen_h = 1080
    def gate(self): return True
    def now(self): return 0
    def sleep_ms(self, value): return True
    def mmove_relative(self, dx, dy): pass

ctx = Context()
core, runtime = game._load(ctx)
assert core is sys.modules["plan_engine_game_core"]
assert runtime is sys.modules["plan_engine_game_runtime"]
assert "plan_engine_game_parallel" not in sys.modules
assert "plan_engine_login" not in sys.modules
state = {"speed": [0, 2000], "pos": [960, 540], "pauses": None}
helper = core._mouse(ctx, state)
assert helper is sys.modules["plan_engine_login"]
assert state["pauses"] is not None

code = (FW / "code.py").read_text(encoding="utf-8")
for stage in ("before-engine-import", "engine-import-memoryerror", "after-engine-import"):
    assert stage in code
facade = (FW / "plan_engine_game.py").read_text(encoding="utf-8")
for stage in ("before-core-import", "after-core-import", "core-import-memoryerror",
              "after-runtime-import", "runtime-import-memoryerror"):
    assert stage in facade
core_source = (FW / "plan_engine_game_core.py").read_text(encoding="utf-8")
assert '__import__("plan_engine_login")' in core_source
for stage in ("before-mouse-import", "after-mouse-import", "mouse-import-memoryerror"):
    assert stage in core_source
runtime_source = (FW / "plan_engine_game_runtime.py").read_text(encoding="utf-8")
for stage in ("before-parallel-import", "after-parallel-import", "parallel-import-memoryerror"):
    assert stage in runtime_source

def windows_size(name):
    data = (FW / name).read_bytes()
    return len(data) + data.count(b"\n")

assert windows_size("plan_engine_game.py") < 3000
assert windows_size("plan_engine_game_core.py") < 10000
assert windows_size("plan_engine_game_runtime.py") < 12000
assert windows_size("plan_engine_game_parallel.py") < 9000
print("Game facade loads core/runtime/mouse/parallel sequentially")
