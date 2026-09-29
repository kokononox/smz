#!/usr/bin/env python3
"""Release packaging selects MPY Game modules without weakening hash checks."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
runtime = ROOT / "portable/plan3/CIRCUITPY-MODERN"
code = (runtime / "code.py").read_text(encoding="utf-8")
exporter = (ROOT / "ams-shell/src/Ams.UI/Services/ModernAutoCycleFirmwareBundle.cs").read_text(
    encoding="utf-8")
workflow = (ROOT / ".github/workflows/classroom-studio-current.yml").read_text(
    encoding="utf-8")
tool = (ROOT / "tools/build_circuitpython_mpy_runtime.py").read_text(encoding="utf-8")

assert '".mpy" if _PathCompat.isfile("/" + _stem + ".mpy") else ".py"' in code
assert 'Files.Concat(manifestNames)' in exporter
assert 'return selectedFiles.Select' in exporter
assert '"plan_engine_game.py", "plan_engine_game.mpy"' in exporter
assert '"plan_engine_game_core.py", "plan_engine_game_core.mpy"' in exporter
assert 'mpy-cross-windows-10.3.0.static.exe' in workflow
assert 'build_circuitpython_mpy_runtime.py --compiler' in workflow
for stem in (
    "plan_engine_game", "plan_engine_game_core", "plan_engine_game_runtime",
    "plan_engine_game_actions", "plan_engine_game_events",
    "plan_engine_game_response", "plan_engine_game_parallel",
    "plan_engine_game_sound",
):
    assert f'"{stem}"' in tool
assert 'len(lines) != 44' in tool
assert 'data[:1] != b"C"' in tool
print("MPY release bundle contract: PASS")