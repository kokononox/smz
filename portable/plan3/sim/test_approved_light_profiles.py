#!/usr/bin/env python3
"""Approved eight-profile light calibration and Windows package contract."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
profiles = json.loads(
    (ROOT / "ams-shell/src/Ams.UI/light-state-profiles.json").read_text(
        encoding="utf-8"))
by_id = {item["Id"]: item for item in profiles}

expected = {
    "desktop": (59, 1, 0),
    "login-or-dc": (74, 13.5, 0),
    "character-dashboard": (43, 1, 0),
    "entering-game-loading": (3, 1, 0),
    "game": (33, 0.7, 0),
    "targeted": (36.7, 0.5, 0),
    "whisper": (100, 1, 5000),
    "whisper-repeat": (23, 2.5, 1000000),
}

assert set(by_id) == set(expected)
for profile_id, (center, tolerance, cooldown) in expected.items():
    item = by_id[profile_id]
    assert item["Enabled"] is True
    assert item["LuxCenter"] == center
    assert item["LuxTolerance"] == tolerance
    assert item["StableDurationMs"] == 750
    assert item["HysteresisLux"] == 1
    assert item.get("LightCooldownMs", 0) == cooldown

fallback = (
    ROOT / "ams-shell/src/Ams.UI/Models/LightStateProfile.cs"
).read_text(encoding="utf-8")
for token in (
    'Profile("desktop", "دسکتاپ", 59, 1, tolerance: 1)',
    'Profile("login-or-dc", "صفحه لاگین یا DC", 74, 2, tolerance: 13.5)',
    'Profile("whisper", "ویسپر افراد جدید", 100, 7, 5000, tolerance: 1)',
    'Profile("whisper-repeat", "ویسپر افراد تکراری", 23, 8, 1000000, tolerance: 2.5)',
):
    assert token in fallback, token

print("approved eight-profile light calibration contract passed")