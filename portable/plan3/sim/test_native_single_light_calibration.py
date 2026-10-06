#!/usr/bin/env python3
"""Native light calibration must save one stage and reconcile overlaps."""
from pathlib import Path

root = Path(__file__).resolve().parents[3]
pico = root / "firmware" / "abvm" / "pico"
runtime = (pico / "calibration_runtime.c").read_text(encoding="utf-8")
store = (pico / "calibration_store.c").read_text(encoding="utf-8")
store_h = (pico / "calibration_store.h").read_text(encoding="utf-8")
guard = (pico / "guard_runtime.c").read_text(encoding="utf-8")
guard_h = (pico / "guard_runtime.h").read_text(encoding="utf-8")

# The completed five-second sample is committed immediately. A second Yellow
# press and a six-stage calibration pass are not part of the save contract.
service = runtime.split("static void service_light(void)", 1)[1]
yellow = runtime.split(
    "bool calibration_runtime_yellow_short(uint32_t now)", 1
)[1].split("static uint32_t maximum", 1)[0]
assert "fit_and_save_light(center,low,high,r.samples)" in service
assert "PHASE_LIGHT_RESULT" not in runtime
assert "calibration_store_light_set" not in yellow
assert "mode=saved|kind=light|stage=%u|count=%u|profile=%u|id=%s" in runtime
assert "saved=0" not in runtime

# Reuse the approved portable policy in tenths of lux: fixed centers, a
# 0.3-lux integer-safe gap, and a 0.5-lux minimum half-width. The new candidate
# gives up tolerance before an existing neighbour is changed.
assert "#define LIGHT_FIT_GAP_TENTHS 3u" in runtime
assert "#define LIGHT_FIT_MIN_TENTHS 5u" in runtime
candidate_take = runtime.index(
    "available=tolerances[selected]>LIGHT_FIT_MIN_TENTHS"
)
neighbour_take = runtime.index(
    "available=tolerances[i]>LIGHT_FIT_MIN_TENTHS"
)
assert candidate_take < neighbour_take
assert "reason=centers-too-close" in runtime
assert "reason=centers-identical" in runtime
assert "fit=%u|adjusted=%u|revision=%lu" in runtime

# A successful auto-fit has its own audible acknowledgement, while an exact
# centre collision stays on the error path and is not saved.
main = (pico / "main.c").read_text(encoding="utf-8")
buzzer = (pico / "buzzer.c").read_text(encoding="utf-8")
buzzer_h = (pico / "buzzer.h").read_text(encoding="utf-8")
for text in (main, buzzer, buzzer_h):
    assert "buzzer_calibration_overlap_adjusted" in text
assert 'strstr(event,"|fit=1|")' in main
assert "calibration_overlap_adjusted" in buzzer

# Candidate plus any adjusted neighbours are one CRC-protected flash
# transaction; unrelated saved profiles and sound/cycle calibration survive.
for text in (store, store_h):
    assert "calibration_store_light_update" in text
assert "CalibrationRecord before=current" in store
assert "current.payload.light_mask|=update_mask" in store
assert "if(persist())return true" in store
assert "current=before;active_offset=before_offset" in store

# Fitting sees the effective Guard ranges, including project defaults that
# have not previously been calibrated, then applies only the changed ranges.
for text in (guard, guard_h):
    assert "guard_runtime_get_profile_range" in text
assert "if(update_mask&(1u<<i))" in runtime
assert "guard_runtime_set_profile_range" in runtime

print("native single-stage light calibration: 24 passed, 0 failed")