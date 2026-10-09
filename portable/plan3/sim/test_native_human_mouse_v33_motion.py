#!/usr/bin/env python3
"""Native hand-motion source contract: jittered authored progress, continuous
three-pixel physical reports, closed endpoints, C1 circular joins, and the
post-idle warm-up all live in the Pico engine."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
source = (ROOT / "firmware/abvm/pico/arm_uart_mouse.c").read_text(
    encoding="utf-8")
harness = (ROOT / "firmware/abvm/tests/abvm_human_mouse_smoke.c").read_text(
    encoding="utf-8")
fixture = (ROOT / "firmware/abvm/tests/abvm_human_mouse_smoke.amsj")

for token in (
    # jittered progress along an unchanged authored path
    "progress_q16",
    "ease_q16(progress)",
    "45u + ((random_next() % 111u) +",
    # bounded flicks inside the recorded hand's reach
    "flick_chance",
    "flick_left",
    "jpct = 200u + random_next() % 101u",
    "72u << 16",
    # endpoint stays exact regardless of jitter
    "progress = 65536u",
    # lateral tremor with zero at both ends
    "wander_amp",
    "wander_cycles_q8",
    "envelope",
    # leg joins: partial blend ordinarily, exact C1 for circular arcs
    "human_leg_continue",
    "human_exit_distance",
    "human_side_lock",
    # post-idle warm-up
    "human_last_move_end",
    "warmup_steps",
    "idle_gap>=2200u",
    "pause*8u/5u+1u",
    # every physical report is capped on Pico before UART transport
    "HUMAN_REPORT_MAX_DISTANCE_SQ 9u",
    "HUMAN_SUBSTEP_INTERVAL_MS 2u",
    "human_cap_report",
    "point_pending",
    "distance_sq>HUMAN_REPORT_MAX_DISTANCE_SQ",
):
    assert token in source, token

# The ~6 px authored-point density remains a velocity/curve control. Physical
# output is independently expanded to <=3 px reports by the contract above.
assert "(distance + 5u) / 6u" in source

# The host harness loops the ARM UART in software and logs every MMOVE tick.
for token in ("uart_putc_raw", "OK|HVER|harness|REL=1|ASND=1",
              "MMOVE|", "checksum", "BEGIN|", "END|"):
    assert token in harness, token

# The behavioural fixture carries a synthetic 30 s profile (never the user's
# real trace) and five distinct move kinds.
import json
doc = json.loads(fixture.read_text(encoding="utf-8"))
profile = doc["humanMouseProfile"]
assert profile["DurationMs"] == 30000
assert profile["EncodedSample"].startswith("v1|30000|512,512|")
intents = [step["Props"].get("motionIntent")
           for step in doc["pipelines"]["Game"]]
assert intents.count("targetRegion") == 3
assert "microTwitch" in intents and "mediumTwitch" in intents
curves = [(s["Props"]["curveMinPct"], s["Props"]["curveMaxPct"])
          for s in doc["pipelines"]["Game"]]
assert (190, 200) in curves, "circular-authored move required"

print("v3.3 human-mouse source contract passed")
