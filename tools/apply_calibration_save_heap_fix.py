#!/usr/bin/env python3
"""Apply the low-memory calibration-save patch to the modern Pico entrypoint.

Idempotent by design: CI and maintainers may run it repeatedly. It also rebuilds
the code.py entry in the modern SHA256 manifest from the exact resulting bytes.
"""
from pathlib import Path
import hashlib

ROOT = Path(__file__).resolve().parents[1]
CODE = ROOT / "portable" / "plan3" / "CIRCUITPY-MODERN" / "code.py"
MANIFEST = ROOT / "portable" / "plan3" / "CIRCUITPY-MODERN" / "SHA256SUMS.txt"


def replace_once(text: str, old: str, new: str) -> str:
    if new in text:
        return text
    if text.count(old) != 1:
        raise RuntimeError("calibration heap patch anchor missing or ambiguous")
    return text.replace(old, new, 1)


text = CODE.read_text(encoding="utf-8")
text = replace_once(
    text,
    "def _enter_calibration_from_pending_start(self):\n",
    "def _enter_calibration_from_pending_start(self):\n    _prepare_calibration_heap(self)\n",
)
if "def _release_plan_heap(self, emit_cal=False):" not in text:
    text = replace_once(
        text,
        """_original_yellow_action = runtime.Combined.yellow_action

def _audible_start_cal(self):
    _original_start_cal(self)
""",
        """_original_yellow_action = runtime.Combined.yellow_action

_PLAN_MODULES = (\"plan_engine_exec\", \"plan_engine_game\", \"plan_engine_human\", \"plan_engine_login\", \"plan_engine_parallel\", \"plan_engine_parse\")

def _prepare_calibration_heap(self):
    # Complex routes lazily import the split plan engine. CircuitPython keeps
    # those modules cached after Stop/PlanAbort, leaving a fragmented heap that
    # can fail the 2–3 KB atomic calibration JSON write. Calibration never runs
    # concurrently with a route, so return the deferred proxy to its cold state
    # and release all split-engine modules before sampling or saving.
    proxy = runtime.plan_engine
    try:
        if hasattr(proxy, \"module\"):
            proxy.module = None
    except Exception:
        pass
    for name in _PLAN_MODULES:
        sys.modules.pop(name, None)
    sys.modules[\"plan_engine\"] = proxy
    gc.collect()
    try:
        self.emit(\"EVT|DEBUG|CAL|heap-ready|free=%d\" % gc.mem_free())
    except Exception:
        pass

def _audible_start_cal(self):
    _prepare_calibration_heap(self)
    _original_start_cal(self)
""",
    )
text = replace_once(
    text,
    """    if was_sampling and isinstance(self.result, dict):
        self.save_cal()
""",
    """    if was_sampling and isinstance(self.result, dict):
        self.samples = []
        _prepare_calibration_heap(self)
        self.save_cal()
""",
)

if text.count("_prepare_calibration_heap(self)") != 4:
    raise RuntimeError("unexpected calibration heap helper call count")
CODE.write_text(text, encoding="utf-8")

code_hash = hashlib.sha256(CODE.read_bytes()).hexdigest()
lines = []
found = False
for line in MANIFEST.read_text(encoding="utf-8").splitlines():
    digest, name = line.split("  ", 1)
    if name == "code.py":
        digest = code_hash
        found = True
    lines.append(digest + "  " + name)
if not found or len(lines) != 40:
    raise RuntimeError("modern manifest inventory mismatch")
MANIFEST.write_text("\n".join(lines) + "\n", encoding="utf-8")
print("calibration save heap fix applied", code_hash)
