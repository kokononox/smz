#!/usr/bin/env python3
"""Hardware-log regressions for Game/Target calibration and COM recovery."""
import importlib.util
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
FW = ROOT / "portable/plan3/CIRCUITPY-MODERN"
sys.path.insert(0, str(FW))

from guard_calibration_protocol import calibrated_profile, find_profile_overlap


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


transition = load("guard_transition_reentry", FW / "guard_transition.py")
guard = transition.GuardTransition()

# A run may start while already in Game.
first = guard.observe("game")
assert first["execute"] is True
assert first["stage"] == 5

# A short lux spike produced Unknown in the hardware log. Returning to Game
# must restore the state without replaying the Game macro.
guard.observe_unknown()
returned = guard.observe("game")
assert returned["execute"] is False
assert returned["route"] == "game_steps.txt"
assert returned["reason"] == "game-reentry-after-unknown"
assert returned["stage"] == 5

# Targeted remains a side-state and its return semantics are unchanged.
guard.observe_unknown()
targeted = guard.observe("targeted")
assert targeted["execute"] is True
assert targeted["route"] == "targeted_steps.txt"
guard.observe_unknown()
back = guard.observe("game")
assert back["execute"] is False
assert back["reason"] == "targeted-returned-to-game"

profiles = {
    "desktop": {"center": 45.0, "tolerance": 5.0},
    "login-or-dc": {"center": 2.5, "tolerance": 1.0},
    "character-dashboard": {"center": 15.8, "tolerance": 1.0},
    "entering-game-loading": {"center": 38.3, "tolerance": 1.0},
    "game": {"center": 25.0, "tolerance": 2.0},
    "targeted": {"center": 20.0, "tolerance": 1.0},
}

# Two isolated outliers must not inflate tolerance to 1.5 * full max/min
# spread. The result is also capped at Targeted's upper boundary.
samples = [20.0] + [22.0, 22.2, 22.4, 22.5, 22.6, 22.8, 23.0] * 3 + [25.0]
game = calibrated_profile(samples, profiles, "game")
assert game["center"] == 22.5
assert 1.0 <= game["tolerance"] <= 1.5
assert find_profile_overlap(profiles, "game", game) is None

# Hardware regression: Character Dashboard spent most of the five-second
# window at 13.3 lux but repeatedly reached 15.8. The former symmetric
# half-width formula saved 13.3 +/- 1.0, so the live 15.8 state became unknown.
dashboard_samples = [13.3] * 18 + [15.8] * 2
dashboard = calibrated_profile(dashboard_samples, profiles, "character-dashboard")
assert dashboard["center"] == 13.3
assert dashboard["tolerance"] >= 3.0
assert dashboard["center"] + dashboard["tolerance"] >= 15.8
assert find_profile_overlap(profiles, "character-dashboard", dashboard) is None

bridge = (ROOT / "ams-shell/bridge/bridge.py").read_text(encoding="utf-8")
assert 'stale = state["link"]' in bridge
assert 'failed = state["link"]' in bridge
assert 'state["link"] = None' in bridge
assert "Pico brain not found on any serial port" in bridge

csharp = (ROOT / "ams-shell/src/Ams.UI/Services/PythonBoardBridge.cs").read_text(
    encoding="utf-8"
)
assert "Do not reuse a sidecar" in csharp
assert "failed.Kill(entireProcessTree: true)" in csharp
assert "Port = null;" in csharp

print("game re-entry, robust calibration, and reconnect recovery: 20 passed, 0 failed")