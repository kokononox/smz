#!/usr/bin/env python3
from pathlib import Path
root=Path(__file__).resolve().parents[3]
exporter=(root/'ams-shell/src/Ams.UI/Services/PlanExporter.cs').read_text()
arm=(root/'firmware/arm28/ams_board26_impl.h').read_text()
arm28=(root/'firmware/arm28/ams_board28.ino').read_text()
runtime=(root/'portable/plan3/CIRCUITPY-MODERN/combined_guard_runtime.py').read_text()
cycle=(root/'portable/plan3/CIRCUITPY-MODERN/restart_cycle.py').read_text()
executor=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_exec.py'
parallel=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_parallel.py'
human=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_human.py'
game=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_game.py'
game_core=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_game_core.py'
game_runtime=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_game_runtime.py'
game_events=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_game_events.py'
game_response=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_game_response.py'
game_parallel=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_game_parallel.py'
login=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_login.py'
login_core=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_login_core.py'
login_mouse=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_login_mouse.py'
login_type=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_login_type.py'
facade=root/'portable/plan3/CIRCUITPY-MODERN/plan_engine.py'
bundle=(root/'ams-shell/src/Ams.UI/Services/ModernAutoCycleFirmwareBundle.cs').read_text()
assert '"forLoop" or "randomPackage"' in exporter
assert 'n.Type=="waitForSound"' in exporter
assert 'ParallelLeaf=new(){"randomMousePosition","mouseMove"' in exporter
assert 'case "buzzer":EmitBuzzer(n)' in exporter
assert '"comment","buzzer"' in exporter
assert 'StepDefinitions.BuildBuzzerCommands' in exporter
# ARM 2.8.2-S4 keeps relative HID, latches ADC peaks outside HID cadence, and
# never treats an open-but-idle CDC port as a pending secure handshake.
assert '#define FW_VER   "2.8.2-S4"' in arm
assert 'if (!strcmp(cmd, "SCAL"))' in arm
for token in ('ASND|', 'ASNDCANCEL', 'ASND=1', 'EVT|ASND|'):
    assert token in arm28, token
move_steps = arm.split("static void mouse_move_steps", 1)[1].split(
    "static void mouse_move_relative_native", 1)[0]
assert "ARM_SOUND_TICK()" not in move_steps
main_loop = arm28.split("void loop()", 1)[1]
assert "arm28_sound_tick();" in main_loop
assert "OK|HVER|2.8.2-S4|REL=1|ASND=1" in arm28
assert "if(!g_secure){if(Serial.available())do_handshake(40);else delay(1);return;}" in arm28
for token in ('ISR(ADC_vect)', 'ASND_WINDOW_SAMPLES 96U', '_BV(ADATE)', '_BV(ADIE)',
              'asndObservedPeak', 'asndSustainedMs', 'EVT|ASND|'):
    assert token in arm28, token
assert 'analogRead(SND_PIN)' not in move_steps
assert 'line.startswith("EVT|ASND|DETECTED")' in runtime
assert 'EVT|SOUND|listen|source=async' in runtime
assert 'mode=async' in runtime
assert "if(!g_secure){if(Serial)do_handshake(40)" not in arm28
assert "if (Serial.available()) do_handshake(40);" in arm
assert "if (Serial) do_handshake(40);" not in arm
for token in ('def sound_start','def sound_poll','def sound_peak','def sound_cancel','def sound_parallel_safe','async_sound','sound_result','ASND|','ASNDCANCEL','def type_char','SCAL|10'):
    assert token in runtime, token
assert '"polls": 0' in runtime and 'state["polls"] >= 32' in runtime
assert 'for field in reply.split("|")' not in runtime
assert parallel.exists() and parallel.stat().st_size > 8000
assert executor.stat().st_size < 20000, executor.stat().st_size
assert 'from plan_engine_parallel import run_parallel' in executor.read_text()
assert game.exists() and game.stat().st_size < 3500
assert game_core.exists() and game_core.stat().st_size < 10000
assert game_runtime.exists() and game_runtime.stat().st_size < 9000
assert game_events.exists() and game_events.stat().st_size < 6000
assert game_response.exists() and game_response.stat().st_size < 4000
assert game_parallel.exists() and game_parallel.stat().st_size < 9000
assert 'plan_engine_parse' not in game.read_text() and 'plan_engine_exec' not in game.read_text()
assert 'before-core-import' in game.read_text() and 'after-runtime-import' in game.read_text()
assert 'class _FileCommands' in game_core.read_text() and 'def _pick_items' in game_core.read_text()
assert 'sound_parallel_safe' in game_parallel.read_text()
assert login.exists() and login.stat().st_size < 4000
assert login_core.exists() and login_core.stat().st_size < 5000
assert login_mouse.exists() and login_mouse.stat().st_size < 7000
assert login_type.exists() and login_type.stat().st_size < 7000
assert 'before-mouse-runtime-import' in login.read_text()
assert all(name in bundle for name in ('plan_engine_parallel.py', 'plan_engine_game.py',
    'plan_engine_game_core.py', 'plan_engine_game_runtime.py',
    'plan_engine_game_events.py', 'plan_engine_game_response.py', 'plan_engine_game_parallel.py', 'plan_engine_login.py',
    'plan_engine_login_core.py', 'plan_engine_login_mouse.py',
    'plan_engine_login_type.py'))
assert 'manifestNames.Length != 42' in bundle
for token in ('AFTER_ROUTE', 'STARTUP_ROUTE', 'class Marker',
              'phase = "wait-usb"', 'startup-in=', 'MAX_RESTARTS = 5'):
    assert token in cycle, token
assert 'line.startswith("EVT|HOSTUSB|")' in runtime
sound_calibration=root/'portable/plan3/CIRCUITPY-MODERN/sound_step_calibration.py'
assert sound_calibration.exists() and sound_calibration.stat().st_size > 4000
assert 'def _parallel_relative_mouse_events' in parallel.read_text()
assert 'relative_mouse_events' in parallel.read_text()
assert 'segments = max(8, min(128' in human.read_text()
assert 'plan-lite-relative' in facade.read_text()
assert 'timeout - cancel group' in parallel.read_text()
assert 'parallel wsnd profile range=' in parallel.read_text()
assert 'SOUNDWATCH' in (root/'portable/plan3/CIRCUITPY-MODERN/plan_engine_parse.py').read_text()
assert 'WPROFILE' in game_runtime.read_text()
assert 'def _leaf(' in game_runtime.read_text() and 'def _sound(' in game_runtime.read_text()
assert '_core._FileCommands(name)' in game_response.read_text()
assert 'before-response-bind' in game_runtime.read_text()
assert 'before-response-callback' in game_runtime.read_text()
assert 'state["_response"] = winner' in game_runtime.read_text()
assert 'service_pending_response(ctx, state)' in game_parallel.read_text()
assert 'scoped splash timeout -> next cast' in game_parallel.read_text()
assert 'only one WSND listener may be active' not in parallel.read_text()
assert 'self.r.arm.flush()' in runtime and 'SCAL rejected: ERR|BUSY' in runtime
code=(root/'portable/plan3/CIRCUITPY-MODERN/code.py').read_text()
assert 'self.keyboard.release_all()' in code and 'GP3", "pause"' in code
assert 'except runtime.plan_engine.PlanAbort:' in code
assert '_release_plan_heap(self)' in code
assert 'other["moving"] for other in tasks' in parallel.read_text()

# Build 79: blocking WSND reports the observed peak and a timeout remains a normal plan result.
assert 'ERR|TIMEOUT|WSND|max=%u' in arm
assert 'OK|WSND|DETECTED|peak=%u|t=%lu' in arm
assert 'listen_loop(uint16_t thr, uint16_t minMs, uint32_t timeoutMs, uint16_t* observedPeak)' in arm
assert 'ARM_SOUND_TICK()' not in move_steps
assert 'head == "WSND" and reply.startswith("ERR|TIMEOUT|WSND")' in runtime
assert 'EVT|SOUND|result=timeout|' in runtime
print('parallel export/firmware contract: continuous peak-latched async sound passed')
