from pathlib import Path
import importlib
import sys

ROOT = Path(__file__).resolve().parents[3]
FW = ROOT / "portable/plan3/CIRCUITPY-MODERN"
sys.path.insert(0, str(FW))
for name in ("plan_engine_game", "plan_engine_login"):
    sys.modules.pop(name, None)

game = importlib.import_module("plan_engine_game")
assert "plan_engine_login" not in sys.modules
assert game._mouse_module is None

class Runtime:
    def emit(self, value):
        pass

class Context:
    def __init__(self):
        self.r = Runtime()
        self.screen_w = 1920
        self.screen_h = 1080
        self.moves = []
    def gate(self): return True
    def now(self): return 0
    def sleep_ms(self, value): return True
    def mmove_relative(self, dx, dy): self.moves.append((dx, dy))

ctx = Context()
state = {"speed": [0, 2000], "pos": [960, 540], "pauses": None}
helper = game._mouse(ctx, state)
assert helper is sys.modules["plan_engine_login"]
assert game._mouse_module is helper
assert state["pauses"] is not None

source = (FW / "code.py").read_text(encoding="utf-8")
assert 'def _game_heap(ctx, stage):' in source
assert '_game_heap(ctx, "before-engine-import")' in source
assert '_game_heap(ctx, "engine-import-memoryerror")' in source
assert '_game_heap(ctx, "after-engine-import")' in source

game_source = (FW / "plan_engine_game.py").read_text(encoding="utf-8")
assert "import plan_engine_login as mouse" not in game_source
assert '__import__("plan_engine_login")' in game_source
assert '_emit_heap(ctx, "before-mouse-import")' in game_source
assert '_emit_heap(ctx, "after-mouse-import")' in game_source
assert '_emit_heap(ctx, "mouse-import-memoryerror")' in game_source
print("Game imports mouse helper lazily after engine/index stabilization")
