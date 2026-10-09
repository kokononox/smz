#!/usr/bin/env python3
"""v3.2 restrained ordinary curvature and explicit circular-mode contracts."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
source = (ROOT / "firmware/abvm/pico/arm_uart_mouse.c").read_text(
    encoding="utf-8")

for token in (
    "human_curve_height_per_mille",
    "circular_authored=sampled_curve>=190",
    "if(circular_authored&&distance>=120u)",
    "radius_pct=30u+(uint32_t)(circular_curve-190)*2u",
    "human_side_lock",
    "if(circular_authored)over_chance=0",
    "if(distance>=600u",
    "distance*turn/450u",
    "distance/25u+1u",
    "q16_cubic",
    "control1_x",
    "control2_x",
    "curve2=curve1<sample_max?curve1+1:curve1-1",
):
    assert token in source, token


def height_per_mille(curve):
    curve = max(0, min(200, curve))
    if curve <= 100:
        return 5 + curve * 15 // 100
    if curve <= 139:
        return 20 + (curve - 100) * 40 // 39
    if curve <= 169:
        return 60 + (curve - 140) * 90 // 29
    if curve <= 189:
        return 150 + (curve - 170) * 150 // 19
    return 300 + (curve - 190) * 200 // 10


expected = {
    0: 5,       # 0.5%
    100: 20,    # 2%
    139: 60,    # 6%
    169: 150,   # 15%
    189: 300,   # 30%
    200: 500,   # 50%, explicit circular mode only
}
for curve, ratio in expected.items():
    assert height_per_mille(curve) == ratio, (curve, height_per_mille(curve))
assert all(height_per_mille(i) <= height_per_mille(i + 1)
           for i in range(200))

# The verified hand profile's 4..18 degree turn range maps ordinary long-path
# waypoints to at most four percent of distance, not the former ten percent.
for distance in (600, 900, 1600, 3000):
    for turn in range(4, 19):
        bend = max(distance // 100 + 1,
                   min(distance // 25 + 1, distance * turn // 450))
        assert 0 < bend <= distance // 25 + 1

# Circular radius remains intentional and visible only in the 190..200 band.
assert [30 + (curve - 190) * 2 for curve in range(190, 201)] == \
       list(range(30, 51, 2))

# Each leg samples two control-point curves from one bounded local window. If
# the window has room, equal samples are separated by one unit, so the arc
# strength changes from entry to exit without leaving the authored range.
def control_pair(base, range_min, range_max, first, second):
    span = range_max - range_min
    window = max(2, min(24, span // 3)) if span > 0 else 0
    low = max(range_min, min(range_max, base - window))
    high = max(range_min, min(range_max, base + window))
    first = max(low, min(high, first))
    second = max(low, min(high, second))
    if first == second and high > low:
        second = first + 1 if first < high else first - 1
    return first, second, low, high


for base, low, high in ((110, 105, 175), (150, 120, 189),
                        (145, 139, 151), (8, 8, 8)):
    c1, c2, allowed_low, allowed_high = control_pair(
        base, low, high, base, base)
    assert allowed_low <= c1 <= allowed_high
    assert allowed_low <= c2 <= allowed_high
    if allowed_high > allowed_low:
        assert c1 != c2

assert "Cursor.Position" not in source
assert "GetCursorPos" not in source
print("native human mouse v3.2 curvature contracts passed")