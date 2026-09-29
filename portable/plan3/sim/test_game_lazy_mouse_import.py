from pathlib import Path
import importlib
import sys

ROOT = Path(__file__).resolve().parents[3]
FW = ROOT / "portable/plan3/CIRCUITPY-MODERN"
sys.path.insert(0, str(FW))
modules = ("plan_engine_game", "plan_engine_game_core", "plan_engine_game_runtime",
           "plan_engine_game_events", "plan_engine_game_response",
           "plan_engine_game_parallel", "plan_engine_login",
           "plan_engine_login_core", "plan_engine_login_mouse",
           "plan_engine_login_type")
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
assert "plan_engine_game_events" not in sys.modules
assert "plan_engine_game_response" not in sys.modules
assert "plan_engine_game_parallel" not in sys.modules
assert "plan_engine_login" not in sys.modules
state = {"speed": [0, 2000], "pos": [960, 540], "pauses": None}
helper = core._mouse(ctx, state)
assert helper is sys.modules["plan_engine_login"]
assert "plan_engine_login_core" in sys.modules
assert "plan_engine_login_mouse" not in sys.modules
assert "plan_engine_login_type" not in sys.modules
assert state["pauses"] is not None
helper._mouse(ctx)
assert "plan_engine_login_mouse" in sys.modules
assert "plan_engine_login_type" not in sys.modules
helper._typing(ctx)
assert "plan_engine_login_type" in sys.modules
typing = sys.modules["plan_engine_login_type"]
assert typing._QWERTY_ROWS == ("1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm")
assert typing._qwerty_neighbor("q") == "w"

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
login_source = (FW / "plan_engine_login.py").read_text(encoding="utf-8")
for stage in ("before-core-import", "after-core-import", "before-mouse-runtime-import",
              "after-mouse-runtime-import", "before-type-import", "after-type-import"):
    assert stage in login_source
runtime_source = (FW / "plan_engine_game_runtime.py").read_text(encoding="utf-8")
for stage in ("before-parallel-import", "after-parallel-import", "parallel-import-memoryerror"):
    assert stage in runtime_source

def windows_size(name):
    data = (FW / name).read_bytes()
    return len(data) + data.count(b"\n")

assert windows_size("plan_engine_game.py") < 3500
assert windows_size("plan_engine_game_core.py") < 10000
assert windows_size("plan_engine_game_runtime.py") < 9000
assert windows_size("plan_engine_game_events.py") < 9000
assert windows_size("plan_engine_game_response.py") < 4000
assert windows_size("plan_engine_game_parallel.py") < 9000
assert windows_size("plan_engine_login.py") < 4000
assert windows_size("plan_engine_login_core.py") < 5000
assert windows_size("plan_engine_login_mouse.py") < 7000
assert windows_size("plan_engine_login_type.py") < 7000
# Keep the CircuitPython compiler from recreating the 1180-byte monolithic
# dispatch code object observed on Bundle 390.
for name in ("_run_response", "_profile", "_wait_sound", "_sound", "_beep", "_basic", "_leaf", "_run"):
    assert hasattr(runtime, name)
    assert len(getattr(runtime, name).__code__.co_code) < 1000, name
import plan_engine_game_events as game_events
for name in ("_resume", "next"):
    assert len(getattr(game_events.Cursor, name).__code__.co_code) < 1000, name
print("Game facade loads bounded handlers and modules sequentially")
