#!/usr/bin/env python3
from pathlib import Path
root=Path(__file__).resolve().parents[3]
exporter=(root/'ams-shell/src/Ams.UI/Services/PlanExporter.cs').read_text()
arm=(root/'firmware/arm28/ams_board26_impl.h').read_text()
arm28=(root/'firmware/arm28/ams_board28.ino').read_text()
runtime=(root/'portable/plan3/CIRCUITPY-MODERN/combined_guard_runtime.py').read_text()
executor=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_exec.py'
parallel=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_parallel.py'
human=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_human.py'
game=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_game.py'
login=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_login.py'
facade=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine.py'
bundle=(root/'ams-shell/src/Ams.UI/Services/ModernAutoCycleFirmwareBundle.cs').read_text()
assert '"forLoop" or "randomPackage"' in exporter
assert 'n.Type=="waitForSound"' in exporter
assert 'ParallelLeaf=new(){"randomMousePosition","mouseMove"' in exporter
# ARM 2.8.2 keeps relative HID and adds non-blocking sound preemption.
assert '#define FW_VER   "2.8.2"' in arm
assert 'if (!strcmp(cmd, "SCAL"))' in arm
for token in ('ASND|', 'ASNDCANCEL', 'ASND=1', 'EVT|ASND|'):
    assert token in arm28, token
for token in ('def sound_start','def sound_poll','def sound_cancel','def sound_parallel_safe','async_sound','sound_result','ASND|','ASNDCANCEL','def type_char','SCAL|10'):
    assert token in runtime, token
assert '"polls": 0' in runtime and 'state["polls"] >= 32' in runtime
assert 'for field in reply.split("|")' not in runtime
assert parallel.exists() and parallel.stat().st_size > 8000
assert executor.stat().st_size < 20000, executor.stat().st_size
assert 'from plan_engine_parallel import run_parallel' in executor.read_text()
assert game.exists() and game.stat().st_size < 14000
assert 'plan_engine_parse' not in game.read_text() and 'plan_engine_exec' not in game.read_text()
assert 'sound_parallel_safe' in game.read_text()
assert login.exists() and login.stat().st_size < 14000
assert 'plan_engine_parallel.py' in bundle and 'plan_engine_game.py' in bundle and 'plan_engine_login.py' in bundle
assert 'manifestNames.Length != 28' in bundle
assert 'def _parallel_relative_mouse_events' in parallel.read_text()
assert 'relative_mouse_events' in parallel.read_text()
assert 'segments = max(8, min(128' in human.read_text()
assert 'plan-lite-relative' in facade.read_text()
assert 'timeout - cancel group' in parallel.read_text()
assert 'self.r.arm.flush()' in runtime and 'SCAL rejected: ERR|BUSY' in runtime
code=(root/'portable/plan3/CIRCUITPY-MODERN/code.py').read_text()
assert 'self.keyboard.release_all()' in code and 'GP3", "pause"' in code
assert 'except runtime.plan_engine.PlanAbort:' in code
assert '_release_plan_heap(self)' in code
assert 'other["moving"] for other in tasks' in parallel.read_text()
print('parallel export/firmware contract: async sound passed')
