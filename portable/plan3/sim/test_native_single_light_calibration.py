#!/usr/bin/env python3
"""Native light calibration saves nominal ranges and fits shared boundaries."""
from pathlib import Path

root = Path(__file__).resolve().parents[3]
pico = root / "firmware" / "abvm" / "pico"
runtime = (pico / "calibration_runtime.c").read_text(encoding="utf-8")
store = (pico / "calibration_store.c").read_text(encoding="utf-8")
store_h = (pico / "calibration_store.h").read_text(encoding="utf-8")
guard = (pico / "guard_runtime.c").read_text(encoding="utf-8")
guard_h = (pico / "guard_runtime.h").read_text(encoding="utf-8")
fit = (pico / "light_range_fit.h").read_text(encoding="utf-8")

# The completed five-second sample is committed immediately. A second Yellow
# press and a six-stage calibration pass are not part of the save contract.
service = runtime.split("static void service_light(void)", 1)[1]
yellow = runtime.split(
    "bool calibration_runtime_yellow_short(uint32_t now)", 1
)[1].split("static uint32_t maximum", 1)[0]
assert "fit_and_save_light(center,low,high,tolerance,r.samples)" in service
assert "uint32_t tolerance=calibration_store_light_tolerance();" in service
assert "PHASE_LIGHT_RESULT" not in runtime
assert "calibration_store_light_set" not in yellow
assert "mode=saved|kind=light|stage=%u|count=%u|profile=%u|id=%s" in runtime
assert "saved=0" not in runtime

# Calibration stores the original requested intervals and measured centres,
# then derives the effective intervals by splitting each overlap at the
# midpoint. No profile gets priority merely because it was selected last.
assert "light_ranges_fit(fitted_low,fitted_high,centers,&changed)" in runtime
assert "base_low[selected]=low;base_high[selected]=high;centers[selected]=center;" in runtime
assert "calibration_store_light_sync(0x1ffu,base_low,base_high,centers,(uint16_t)tolerance)" in runtime
assert "light_ranges_fit_preserving_selected" not in runtime
assert "light_ranges_fit_preserving_selected" not in fit
assert "center[a]+(center[b]-center[a])/2u" in fit
assert "hi[a]=split" in fit and "lo[b]=split+1u" in fit
assert "calibration_runtime_light_base_snapshot" in runtime
assert "if(!light_ranges_fit(fitted_low,fitted_high,centers,NULL))return;" in runtime
assert "calibration_store_light_tolerance()" in runtime
assert "r.average_tenths" in runtime
assert "changed=%s|revision=%lu" in runtime

# Concrete A/B/C fixture: centres 10/15/20 Lux and +/-3 Lux are represented
# in tenths. Shared midpoint boundaries are deterministic and centres remain
# inside their fitted intervals: A=[7,12.5], B=[12.6,17.5], C=[17.6,23].
centres = [100, 150, 200]
lows = [centre - 30 for centre in centres]
highs = [centre + 30 for centre in centres]
for left, right in ((0, 1), (1, 2)):
    split = centres[left] + (centres[right] - centres[left]) // 2
    highs[left] = min(highs[left], split)
    lows[right] = max(lows[right], split + 1)
assert list(zip(lows, highs)) == [(70, 125), (126, 175), (176, 230)]
assert all(low <= centre <= high for low, centre, high in zip(lows, centres, highs))

# A successful auto-fit has its own audible acknowledgement, while an exact
# centre collision stays on the error path and is not saved.
main = (pico / "main.c").read_text(encoding="utf-8")
buzzer = (pico / "buzzer.c").read_text(encoding="utf-8")
buzzer_h = (pico / "buzzer.h").read_text(encoding="utf-8")
for text in (main, buzzer, buzzer_h):
    assert "buzzer_calibration_overlap_adjusted" in text
assert 'strstr(event,"|fit=1|")' in main
assert "calibration_overlap_adjusted" in buzzer

# Candidate and nominal profile bounds are written in one CRC-protected flash
# transaction; unrelated saved profiles and sound/cycle calibration survive.
for text in (store, store_h):
    assert "calibration_store_light_sync" in text
assert "CalibrationRecord before=current" in store
assert "current.payload.light_mask|=mask" in store
assert "if(persist())return true" in store
assert "current=before;active_offset=before_offset" in store

# Fitting considers the effective Guard ranges, including project defaults
# that have not previously been calibrated; fitted ranges are applied together.
for text in (guard, guard_h):
    assert "guard_runtime_get_profile_range" in text
assert "guard_runtime_get_profile_range(id,&base_low[i],&base_high[i])" in runtime
assert "guard_runtime_set_profile_range((uint8_t)(i+1u),fitted_low[i],fitted_high[i])" in runtime

print("native single-stage light calibration: shared midpoint and persistence contracts pass")
