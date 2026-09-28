#!/usr/bin/env python3
"""Large Game packages stay file-backed and sample only the chosen items."""
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
FW = ROOT / "portable/plan3/CIRCUITPY-MODERN"
sys.path.insert(0, str(FW))

import plan_engine_game as game

rows = ["PLAN|2", "SCREEN|1920,1080", "RPKG|pick,1,2"]
for index in range(165):
    if index:
        rows.append("PKGITEM")
    rows.append("DELAY|0,0")
rows.append("ENDPKG")

with tempfile.NamedTemporaryFile("w", delete=False) as route:
    route.write("\n".join(rows) + "\n")
    route_name = route.name.lstrip("/")

commands = game._FileCommands(route_name)
try:
    assert isinstance(commands.offsets, bytearray)
    assert len(commands.offsets) == len(rows) * 4
    assert commands[0] == ("PLAN", "2")
    package_index = 2
    finish, parts, order = game._package(commands, package_index)
    assert commands[finish][0] == "ENDPKG"
    assert 1 <= len(parts) <= 2
    assert len(order) == len(parts)
finally:
    commands.close()


class Context:
    plan_api = 3
    screen_w = 1920
    screen_h = 1080
    speed_min = 0
    speed_max = 2000

    def gate(self): return True
    def now(self): return 0.0
    def sleep_ms(self, _milliseconds): return True
    def log(self, _message): pass


game.run_game_file(route_name, Context())
Path("/" + route_name).unlink()

source = (FW / "code.py").read_text(encoding="utf-8")
assert 'commands = name if name == "game_steps.txt"' in source
assert "plan_engine_game.run_game_file(commands, ctx)" in source
assert "stage=file-index|commands=%d|offset-bytes=%d|free=%d" in (
    FW / "plan_engine_game.py").read_text(encoding="utf-8")
print("file-backed Game route + bounded RPKG reservoir sampling passed")
