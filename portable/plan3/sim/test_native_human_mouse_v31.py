#!/usr/bin/env python3
"""Personal-profile v3.1 pause, turn, long-move and state contracts."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
source = (ROOT / "firmware/abvm/pico/arm_uart_mouse.c").read_text(
    encoding="utf-8")

for token in (
    "HumanPersonalProfile",
    '"handPauseP50Ms"',
    '"handPauseP90Ms"',
    '"handTurnP50Deg"',
    '"handTurnP90Deg"',
    '"handLongPct"',
    "human_personal_pause",
    "human_turn_p90_deg",
    "human_refresh_mode(",
    "personal.long_pct",
    "distance*turn/450u",
):
    assert token in source, token

# The user's verified phase-one UF2 carries this compact profile:
# pauses 399/399 ms, turns 4/18 degrees, and 67% long movements.
pause_p50 = pause_p90 = 399
turn_p50, turn_p90, long_pct = 4, 18, 67


def personal_pause(sampled, low, high, mode):
    target = pause_p50
    if mode == "focused":
        target = target * 3 // 4
    elif mode == "idle":
        target = (pause_p50 + pause_p90) // 2
    elif mode == "fatigued":
        target = pause_p90
    target = max(low, min(high, target))
    return (sampled * 2 + target) // 3


for mode in ("focused", "normal", "idle", "fatigued"):
    for sampled in (100, 250, 500):
        value = personal_pause(sampled, 100, 500, mode)
        assert 100 <= value <= 500


def state_cutoffs(long_value):
    focused = max(12, min(20, 20 - long_value // 10))
    normal = max(62, min(72, 72 - long_value // 12))
    idle = max(82, min(88, 88 - long_value // 25))
    return focused, normal, idle


assert state_cutoffs(long_pct) == (14, 67, 86)
for value in range(101):
    focused, normal, idle = state_cutoffs(value)
    assert 0 < focused < normal < idle < 100

for distance in (600, 900, 1600, 3000):
    for turn in range(turn_p50, turn_p90 + 1):
        bend = max(distance // 100 + 1,
                   min(distance // 25 + 1, distance * turn // 450))
        assert 0 < bend <= distance // 25 + 1

assert "(multi_chance*2u+personal.long_pct)/3u" not in source
assert "HUMAN_MODE_FOCUSED?10u" in source

assert "Cursor.Position" not in source
assert "GetCursorPos" not in source
print("native human mouse v3.1 personalization contracts passed")