#!/usr/bin/env python3
"""ABVM phase-0 compiler, verifier, and fixed-state reference VM."""
import copy
import importlib.util
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location("abvm", ROOT / "tools/abvm.py")
abvm = importlib.util.module_from_spec(spec)
sys.modules["abvm"] = abvm
spec.loader.exec_module(abvm)


def node(kind, values=None, nested=None, delay=0, delay_max=0):
    return {"Type": kind, "Props": values or {}, "Children": nested or [],
            "Delay": delay, "DelayMax": delay_max, "IsDisabled": False}


mouse_a = node("randomMousePosition", {
    "x": 1301, "y": 0, "w": 12, "h": 12,
    "moveTimeMin": 55, "moveTimeMax": 77})
mouse_b = node("randomMousePosition", {
    "x": 1301, "y": 0, "w": 104, "h": 40,
    "moveTimeMin": 157, "moveTimeMax": 254})
movement = node("forLoop", {"mode": "count", "count": 2}, [
    node("randomPackage", {
        "mode": "randomSubset", "minCount": 1, "maxCount": 2}, [
            node("delay", {"minMs": 10, "maxMs": 20}), mouse_a,
            node("delay", {"minMs": 5, "maxMs": 9}), mouse_b])])
watch = node("forLoop", {"mode": "count", "count": 1}, [
    node("waitForSound", {
        "calibrationId": 2, "timeoutMinSec": 1, "timeoutMaxSec": 1}, [
            node("keystroke", {"key": "F", "holdMin": 80, "holdMax": 180})])])
game = [node("forLoop", {
    "mode": "time", "timeValue": 1, "timeUnit": "second"}, [
        node("keystroke", {"key": "7", "holdMin": 80, "holdMax": 180}),
        node("parallelGroup", {}, [movement, node("comment", {"text": "Next"}),
                                    watch, node("comment", {"text": "Next"})])])]
whisper = [
    node("keystroke", {"key": "ENTER", "holdMin": 80, "holdMax": 180}),
    node("typeText", {"text": "hi :)", "hmin": 111, "hmax": 250,
                           "wmin": 130, "wmax": 220, "wordPauseChance": 60,
                           "typoEveryMin": 7, "typoEveryMax": 12})]
project = {"pipelineVersion": 6,
           "pipelines": {"Game": game, "Whisper": whisper}}

compiled = abvm.Compiler().compile_amsj(project)
image = abvm.Verifier.verify(compiled.image)
assert compiled.image[:4] == b"ABP1"
assert image.max_frames <= 8 and image.max_lanes == 2
assert image.flags & abvm.FLAG_HAS_TYPE
assert image.flags & abvm.FLAG_HAS_RACE
assert image.flags & abvm.FLAG_HAS_SOUND
assert len(compiled.image) < 8192

detected = abvm.ReferenceVm(compiled.image, seed=3, detected_profiles=[2])
detected_events = detected.run("Game")
assert ("WATCH", 2, "detected") in detected_events
assert any(event[0] == "KEY" and 70 in event[1] for event in detected_events)
assert any(event[0] == "RACE_RESUME" for event in detected_events)

timeout = abvm.ReferenceVm(compiled.image, seed=3)
timeout_events = timeout.run("Game")
assert ("WATCH", 2, "timeout") in timeout_events
assert not any(event[0] == "KEY" and 70 in event[1] for event in timeout_events)
assert ("TYPE", "hi :)") in abvm.ReferenceVm(compiled.image).run("Whisper")

corrupt = bytearray(compiled.image)
corrupt[abvm.HEADER.size + 3] ^= 0x40
try:
    abvm.Verifier.verify(bytes(corrupt))
    raise AssertionError("corrupt CRC was accepted")
except abvm.AbvmError as exc:
    assert "CRC" in str(exc)

too_deep = node("delay", {"minMs": 1, "maxMs": 1})
for _ in range(9):
    too_deep = node("forLoop", {"mode": "count", "count": 1}, [too_deep])
try:
    abvm.Compiler().compile_amsj({"pipelines": {"Game": [too_deep]}}, ["Game"])
    raise AssertionError("depth overflow was accepted")
except abvm.AbvmError as exc:
    assert "depth" in str(exc)

nested = copy.deepcopy(project)
outer = nested["pipelines"]["Game"][0]["Children"][1]["Children"][2]
watch_node = outer["Children"][0]
watch_node["Children"] = [copy.deepcopy(watch_node)]
try:
    abvm.Compiler().compile_amsj(nested)
    raise AssertionError("nested Watch was accepted")
except abvm.AbvmError as exc:
    assert "nested Watch" in str(exc)

print("ABVM phase-0: compiler, ABP1 verifier, and reference VM passed")
