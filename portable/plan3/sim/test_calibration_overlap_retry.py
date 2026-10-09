from pathlib import Path
import ast
import types

ROOT = Path(__file__).resolve().parents[1]
CODE = ROOT / "CIRCUITPY-MODERN" / "code.py"
source = CODE.read_text(encoding="utf-8")
tree = ast.parse(source)

wanted = {
    "_audible_next_cal",
    "_audible_save_cal",
    "_repeatable_yellow_action",
}
selected = [node for node in tree.body
            if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef))
            and node.name in wanted]
assert {node.name for node in selected} == wanted

clock = types.SimpleNamespace(monotonic=lambda: 123.5)
runtime = types.SimpleNamespace(
    PROFILES=("desktop", "login-or-dc", "character-dashboard",
              "entering-game-loading", "game", "targeted"),
    time=clock,
)


def original_save(self):
    # Mirror a rejected overlap from Combined.save_cal().
    self.last_cal_error = "OVERLAP:character-dashboard:0.417"
    self.saved = False


def original_yellow(self):
    raise AssertionError("overlap retry must not replay the rejected save")

def original_next(self):
    self.emit("ERR|CAL|UNSAVED|stage=%d" % (self.stage + 1))


def debug_event(owner, kind, detail="", persist=False):
    owner.debug.append((kind, detail, persist))


ns = {
    "runtime": runtime,
    "_original_next_cal": original_next,
    "_original_save_cal": original_save,
    "_original_yellow_action": original_yellow,
    "_debug_event": debug_event,
}
exec(compile(ast.Module(body=selected, type_ignores=[]), str(CODE), "exec"), ns)


class Fake:
    def __init__(self):
        self.stage = 4
        self.saved_ids = set()
        self.result = {"center": 22.5, "tolerance": 5.0, "stable_ms": 750}
        self.saved = False
        self.last_cal_error = None
        self.samples = [1.0, 2.0]
        self.sample_started = 0
        self.calibrating = True
        self.controls = types.SimpleNamespace(paused=False, running=False)
        self.events = []
        self.debug = []
        self.error_tones = 0
        self.record_tones = 0

    def emit(self, line):
        self.events.append(line)

    def cal_save_error_tone(self):
        self.error_tones += 1

    def cal_record_start_tone(self):
        self.record_tones += 1

    def cal_position_tone(self):
        raise AssertionError("blocked stage must not play the next-position tone")


fake = Fake()
ns["_audible_save_cal"](fake)
assert fake.error_tones == 1
assert fake.last_cal_error.startswith("OVERLAP:")
assert fake.saved is False
assert fake.debug == [
    ("CAL", "save-failed stage=5 id=game error=OVERLAP:character-dashboard:0.417", True)
]

ns["_audible_next_cal"](fake)
assert fake.stage == 4
assert fake.error_tones == 2
assert fake.events[-1] == "ERR|CAL|UNSAVED|stage=5"

ns["_repeatable_yellow_action"](fake)
assert fake.result == "sampling"
assert fake.samples == []
assert fake.sample_started == 123.5
assert fake.last_cal_error is None
assert fake.record_tones == 1
assert fake.events[-1] == (
    "EVT|CAL|mode=started|stage=5|id=game|seconds=5|saved=0|retry=1"
)

print("calibration overlap retry: 12 passed, 0 failed")
