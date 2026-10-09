#!/usr/bin/env python3
"""A fresh GP4 Start must execute the currently visible ordered section."""
from pathlib import Path
import importlib.util

ROOT = Path(__file__).resolve().parents[1]
path = ROOT / "CIRCUITPY-MODERN" / "guard_transition.py"
spec = importlib.util.spec_from_file_location("modern_guard_transition", path)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
GuardTransition = module.GuardTransition

ordered = (
    ("desktop", 1),
    ("login-or-dc", 2),
    ("character-dashboard", 3),
    ("entering-game-loading", 4),
    ("game", 5),
)

for profile, stage in ordered:
    guard = GuardTransition()
    decision = guard.observe(profile)
    assert decision["execute"] is True, (profile, decision)
    assert decision["profile"] == profile
    assert decision["stage"] == stage and guard.stage == stage
    assert decision["reason"] == "start-at-current-state"
    assert decision["context"] == ("login" if profile == "login-or-dc" else "normal")
print("PASS fresh Start executes each current ordered section")

# Ordering still applies after the entry point.
guard = GuardTransition()
assert guard.observe("character-dashboard")["execute"]
assert guard.observe_unknown() is None
assert guard.observe("entering-game-loading")["execute"]
assert guard.observe_unknown() is None
assert guard.observe("game")["execute"]
assert guard.observe_unknown() is None
assert guard.observe("targeted")["execute"]
print("PASS ordered continuation and Targeted gate remain intact")

# Targeted is a side-state, never a direct startup state.
guard = GuardTransition()
denied = guard.observe("targeted")
assert denied["execute"] is False and denied["reason"] == "targeted-only-from-game"
print("PASS Targeted remains denied as a fresh entry point")

# Starting at dashboard and later seeing Login/DC is still a DC fallback.
guard = GuardTransition()
guard.observe("character-dashboard")
guard.observe_unknown()
dc = guard.observe("login-or-dc")
assert dc["execute"] is True and dc["context"] == "dc" and dc["stage"] == 2
print("PASS DC fallback remains intact")