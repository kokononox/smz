#!/usr/bin/env python3
"""Native ABVM Light Watch/compiler semantic contract."""
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[3]))
from tools import abvm


def step(kind, props=None, children=None):
    return {"Type": kind, "Props": props or {}, "Children": children or [],
            "Delay": 0, "IsDisabled": False}


light = step("waitForLight", {
    "luxCenter": 100, "luxTolerance": 10, "stableSec": 0.5,
    "sampleMode": "lowres", "timeoutMs": 2500,
}, [step("keystroke", {"key": "E", "holdMin": 30, "holdMax": 60})])
source = {"pipelines": {"Game": [light]}}
program = abvm.Compiler().compile_amsj(source, ("Game",))
image = abvm.Verifier.verify(program.image)
watch = next(ins for ins in image.instructions if ins.op == abvm.OP_WATCH)
assert watch.flags == 3 and watch.b == watch.c == 2500
assert abvm.LIGHT.unpack(image.const(watch.a, abvm.CONST_LIGHT)) == (90, 110, 500, 1)
assert image.flags & abvm.FLAG_HAS_LIGHT
assert not image.resources.sound_profiles and not image.resources.sound_listeners

hit = abvm.ReferenceVm(program.image, light_detected=True).run("Game")
miss = abvm.ReferenceVm(program.image, light_detected=False).run("Game")
assert any(event[:2] == ("WATCH", "LIGHT") and event[-1] == "detected" for event in hit)
assert any(event[0] == "KEY" for event in hit)
assert any(event[:2] == ("WATCH", "LIGHT") and event[-1] == "timeout" for event in miss)
assert not any(event[0] == "KEY" for event in miss)

armed = step("waitForLight", {
    "luxCenter": 50, "luxTolerance": 5, "stableSec": 0,
    "timeoutMs": 1000, "armed": True, "key": "F2",
    "reactMin": 10, "reactMax": 20, "holdMin": 30, "holdMax": 40,
})
armed_program = abvm.Compiler().compile_amsj(
    {"pipelines": {"Game": [armed]}}, ("Game",))
armed_events = abvm.ReferenceVm(
    armed_program.image, light_detected=True).run("Game")
assert any(event[0] == "KEY" and event[1] == (113,) for event in armed_events)
print("ABVM native BH1750 Light Watch contract passed")
