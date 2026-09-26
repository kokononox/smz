#!/usr/bin/env python3
"""Hostless mouse contract for the modern Pico + ARM 2.8.7 runtime."""
from pathlib import Path
import random
import sys

ROOT = Path(__file__).resolve().parents[1]
MODERN = ROOT / "CIRCUITPY-MODERN"
sys.path.insert(0, str(MODERN))
for name in ("plan_engine", "plan_engine_parse", "plan_engine_human", "plan_engine_exec"):
    sys.modules.pop(name, None)
import plan_engine


class RelativeCtx:
    plan_api = 3
    mouse_mode = "relative"
    screen_w = 1920
    screen_h = 1080
    speed_min = 0
    speed_max = 2000

    def __init__(self):
        self.rel = []
        self.logs = []
        self.t = 0

    def now(self): return self.t
    def gate(self): return True
    def sleep_ms(self, ms): self.t += ms / 1000.0; return True
    def setres(self, w, h): self.screen_w, self.screen_h = w, h
    def get_mouse_pos(self): raise AssertionError("relative RMOUSE queried host cursor")
    def set_mouse_pos(self, x, y): raise AssertionError("relative RMOUSE persisted fake absolute cursor")
    def mmove(self, x, y): raise AssertionError("relative RMOUSE sent absolute MMOVE")
    def mmove_relative(self, dx, dy): self.rel.append((int(dx), int(dy)))
    def log(self, text): self.logs.append(text)


text = "\n".join((
    "PLAN|2",
    "SCREEN|1920,1080",
    "SPEED|300,2000",
    "RMOUSE|region=1301,0,378,1049|before=0,0|after=0,0|curve=15,45|mid=0:0,0|over=0|idle=5,12:0,0",
))
ctx = RelativeCtx()
random.seed(2801)
plan_engine.run_plan(plan_engine.parse_plan(text), ctx)
assert ctx.rel, "relative path emitted no reports"
dx = sum(point[0] for point in ctx.rel)
dy = sum(point[1] for point in ctx.rel)
assert 126 <= abs(dx) <= 377, (dx, dy)
assert 90 <= abs(dy) <= 270, (dx, dy)
assert all(abs(x) <= 127 and abs(y) <= 127 for x, y in ctx.rel), ctx.rel
assert any("rmouse-rel" in line for line in ctx.logs), ctx.logs
print("PASS portable RMOUSE uses native relative deltas", len(ctx.rel), dx, dy)

# Absolute MOVETO must fail instead of silently pretending the virtual centre is
# the real Windows cursor. This protects fully portable runs from cursor jumps.
try:
    plan_engine.run_plan(plan_engine.parse_plan("PLAN|2\nMOVETO|x=100|y=200"), RelativeCtx())
except ValueError as exc:
    assert "absolute cursor origin" in str(exc)
else:
    raise AssertionError("portable MOVETO did not fail closed")
print("PASS portable MOVETO fails closed")

arm_dir = ROOT.parents[1] / "firmware" / "arm28"
source = (arm_dir / "ams_board28.ino").read_text(encoding="utf-8")
impl = (arm_dir / "ams_board26_impl.h").read_text(encoding="utf-8")
portable_hid = (arm_dir / "portable_relative_mouse.h").read_text(encoding="utf-8")
assert "PortableMouse.begin();" in impl
assert "PortableMouse.move(sx, sy, 0);" in impl
assert '#include "portable_relative_mouse.h"' in impl
assert "HID-Project.h" not in impl
assert "class PortableMouse_" in portable_hid
assert '#define FW_VER   "2.8.7"' in impl
assert 'if (compactMove || !strcmp(mode, "rel"))' in impl
rel_branch = impl.split('if (compactMove || !strcmp(mode, "rel"))', 1)[1].split("else {", 1)[0]
assert "mouse_move_relative_native(x, y)" in rel_branch
assert 'bool compactMove = !strcmp(cmd, "MR");' in impl
assert "reply_ok(cmd);" in impl
assert '|REL=1|MR=1|MB=1' in source
assert 'if (!strcmp(cmd, "MB"))' in impl
assert 'reply_ok("MB")' in impl
assert 'waitMs > 64' in impl
assert 'hex_decode(args, g_pt, 25, &size)' in impl
assert 'reply_err("ABS"); return;' in impl
assert "mouse_move_stream(x, y)" not in impl.split('if (compactMove || !strcmp(cmd, "MMOVE"))', 1)[1].split('if (!strcmp(cmd, "MCLICK"))', 1)[0]
assert "mouse_move_abs" not in rel_branch
relative_fn = impl.split("static void mouse_move_relative_native", 1)[1].split(
    "static unsigned long g_relativeReportDueUs", 1
)[0]
step_fn = impl.split("static uint16_t relative_native_steps", 1)[1].split(
    "static void mouse_move_relative_native", 1
)[0]
assert "max((ax + 1U) / 2U, (ay + 1U) / 2U)" in step_fn
assert "(ax + ay + 2U) / 3U" in step_fn
assert "relative_native_steps(dx, dy)" in relative_fn
assert "mouse_move_steps(g_curX + dx, g_curY + dy, steps, 0)" in relative_fn
assert "mouse_delta_report(dx, dy, 0)" not in relative_fn
assert "delay(1)" not in relative_fn
assert "endpoint" in relative_fn
assert "|REL=1" in source
print("PASS ARM 2.8 rel-MMOVE uses native relative HID")


# Match the integer DDA used by ARM 2.8.7 over the complete one-report HID
# range. The step count is the smallest safe integer-DDA count; endpoint
# rounding may produce a diagonal (2,2), still below three pixels.
def arm_microsteps(dx, dy):
    ax, ay = abs(dx), abs(dy)
    if not ax:
        steps = (ay + 2) // 3
    elif not ay:
        steps = (ax + 2) // 3
    else:
        steps = max((ax + 1) // 2, (ay + 1) // 2)
    steps = max(1, steps)
    out = []
    px = py = 0
    for i in range(1, steps + 1):
        # C/C++ signed integer division truncates toward zero.
        x = int(dx * i / steps)
        y = int(dy * i / steps)
        out.append((x - px, y - py))
        px, py = x, y
    return out


for test_dx in range(-127, 128):
    for test_dy in range(-127, 128):
        if not (test_dx or test_dy):
            continue
        reports = arm_microsteps(test_dx, test_dy)
        assert sum(p[0] for p in reports) == test_dx
        assert sum(p[1] for p in reports) == test_dy
        assert all((x * x + y * y) <= 9 for x, y in reports), (
            test_dx, test_dy, reports
        )
print("PASS ARM 2.8.7 relative HID reports are exact and at most three pixels")

old_reports = 0
new_reports = 0
for test_dx in range(-127, 128):
    for test_dy in range(-127, 128):
        if test_dx or test_dy:
            dist = int((test_dx * test_dx + test_dy * test_dy) ** 0.5)
            old_reports += max(1, (dist + 1) // 2)
            new_reports += len(arm_microsteps(test_dx, test_dy))
assert new_reports < old_reports * 0.9, (old_reports, new_reports)
print("PASS ARM 2.8.7 preserves reduced relative HID report count", old_reports, new_reports)

legacy_wire = len("#XX|MMOVE|-127,-127,rel,2\n") + len("OK|MMOVE\n")
compact_wire = len("#XX|MR|-127,-127\n") + len("OK|MR\n")
assert compact_wire <= legacy_wire * 0.67, (legacy_wire, compact_wire)
print("PASS ARM 2.8.4 compact relative wire contract", legacy_wire, compact_wire)

framed_batch = len("#XX|MB|08FF81FF8108FF81FF8108FF81FF8108FF81FF8108FF81FF81\n")
batch_wire = framed_batch + len("OK|MB\n")
five_compact = compact_wire * 5
assert framed_batch <= 64, framed_batch
assert batch_wire < five_compact * 0.65, (batch_wire, five_compact)
print("PASS ARM 2.8.7 five-delta batch fits AVR RX", framed_batch, batch_wire, five_compact)


mb_handler = impl.split('if (!strcmp(cmd, "MB"))', 1)[1].split(
    "bool compactMove", 1
)[0]
assert "mouse_move_relative_deadline(x, y, windowStartUs, windowEndUs)" in mb_handler
assert "startedUs = micros()" in mb_handler
assert "delay(1)" not in mb_handler
deadline_fn = impl.split("static void mouse_move_relative_deadline", 1)[1].split(
    "static void mouse_move_abs", 1
)[0]
assert "g_relativeReportDueUs + 1000UL" in deadline_fn
assert "wait_until_us(dueUs)" in deadline_fn
assert "mouse_report(px, py)" in deadline_fn


def deadline_reports(records):
    """Model ARM 2.8.7 MB pacing without USB execution cost."""
    batch_started_us = 0
    due_us = 0
    last_report_due_us = 0
    out = []
    for wait_ms, dx, dy in records:
        window_start_us = batch_started_us + due_us
        due_us += wait_ms * 1000
        window_end_us = batch_started_us + due_us
        reports = arm_microsteps(dx, dy)
        px = py = 0
        for i, (mx, my) in enumerate(reports, 1):
            sample_due = window_start_us + (
                (window_end_us - window_start_us) * i // len(reports)
            )
            report_due = max(sample_due, last_report_due_us + 1000)
            out.append((report_due, mx, my))
            last_report_due_us = report_due
            px += mx
            py += my
        assert (px, py) == (dx, dy)
    return out


# Dense records used to be emitted as one wait followed by same-timestamp
# reports. Deadline pacing must keep report order and exact deltas while
# preventing zero-time bursts; overload debt continues into later records.
deadline_case = [(8, 40, 9), (8, -49, -10), (8, 2, 1), (8, 18, 4), (8, 0, -1)]
paced = deadline_reports(deadline_case)
assert all(b[0] - a[0] >= 1000 for a, b in zip(paced, paced[1:]))
assert sum(p[1] for p in paced) == sum(r[1] for r in deadline_case)
assert sum(p[2] for p in paced) == sum(r[2] for r in deadline_case)
assert all((p[1] * p[1] + p[2] * p[2]) <= 9 for p in paced)
assert paced[-1][0] > sum(r[0] for r in deadline_case) * 1000
print("PASS ARM 2.8.7 MB reports use absolute deadline pacing without bursts")
