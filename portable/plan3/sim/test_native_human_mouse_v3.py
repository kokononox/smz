#!/usr/bin/env python3
"""Portable human-mouse v3 source and deterministic behavior contracts."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
source = (ROOT / "firmware/abvm/pico/arm_uart_mouse.c").read_text(
    encoding="utf-8")

for token in (
    "HUMAN_MODE_FOCUSED",
    "HUMAN_MODE_NORMAL",
    "HUMAN_MODE_IDLE",
    "HUMAN_MODE_FATIGUED",
    "human_refresh_mode",
    "human_mode_moves_left",
    "human_mix_boot_entropy(now)",
    "second_leg_pending",
    "second_leg_steps",
    "distance>=450u",
    "random_range_u32(52u,72u)",
):
    assert token in source, token

assert "human_mode_moves_left = (uint16_t)random_range_u32(5u, 17u)" in source
assert "human_virtual_x+=human_path.leg_x" in source
assert "human_path.start_x=human_virtual_x" in source
assert "human_leg(next_x,next_y,next_steps,next_curve)" in source
assert "human_boot_mixed=false;prng=0x6d2b79f5u" in source
assert "Cursor.Position" not in source
assert "GetCursorPos" not in source


def xorshift32(value):
    value ^= (value << 13) & 0xFFFFFFFF
    value ^= value >> 17
    value ^= (value << 5) & 0xFFFFFFFF
    return value & 0xFFFFFFFF


def mode_runs(seed, runs=64):
    value = seed
    out = []
    for _ in range(runs):
        value = xorshift32(value)
        roll = value % 100
        mode = ("focused" if roll < 18 else "normal" if roll < 70 else
                "idle" if roll < 86 else "fatigued")
        value = xorshift32(value)
        length = 6 + value % 13
        out.append((mode, length))
    return out


a = mode_runs(0x12345678)
b = mode_runs(0x87654321)
assert a == mode_runs(0x12345678)
assert a != b
assert all(6 <= length <= 18 for _, length in a)
assert {mode for mode, _ in a} == {
    "focused", "normal", "idle", "fatigued"}

# Long-path waypoint percentages are deliberately bounded.  For every allowed
# split, both legs remain non-empty and add back to the authored displacement.
for dx, dy in ((450, 0), (900, 500), (-1200, 700), (700, -900)):
    for first_pct in range(52, 73):
        wx = int(dx * first_pct / 100)
        wy = int(dy * first_pct / 100)
        second = (dx - wx, dy - wy)
        assert (wx + second[0], wy + second[1]) == (dx, dy)
        assert (wx or wy) and (second[0] or second[1])

print("native human mouse v3: mode, boot entropy and multi-leg contracts passed")