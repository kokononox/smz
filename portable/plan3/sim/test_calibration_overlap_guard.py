#!/usr/bin/env python3
import sys
from pathlib import Path
root = Path(__file__).resolve().parents[1] / "CIRCUITPY-MODERN"
sys.path.insert(0, str(root))
from guard_calibration_protocol import find_profile_overlap

profiles = {
    "desktop": {"center": 52.2, "tolerance": 15},
    "login-or-dc": {"center": 5, "tolerance": 3.8},
    "character-dashboard": {"center": 14.2, "tolerance": 3.8},
    "entering-game-loading": {"center": 35.8, "tolerance": .5},
    "game": {"center": 22.5, "tolerance": 3.7},
    "targeted": {"center": 5.8, "tolerance": .5},
}
assert find_profile_overlap(profiles, "character-dashboard", {"center": 14.2, "tolerance": 3.8}) is None
hit = find_profile_overlap(profiles, "character-dashboard", {"center": 20, "tolerance": 4})
assert hit and hit["with"] == "game" and hit["width"] > 0
# Existing login/targeted overlap remains valid when unchanged, but cannot be worsened.
assert find_profile_overlap(profiles, "login-or-dc", profiles["login-or-dc"]) is None
hit = find_profile_overlap(profiles, "targeted", {"center": 10.0, "tolerance": 2.0})
assert hit and hit["with"] == "character-dashboard"
# Boundary contact is not positive-width overlap.
assert find_profile_overlap(profiles, "game", {"center": 28.1, "tolerance": 4.3}) is None
print("calibration overlap guard: 5 passed, 0 failed")
