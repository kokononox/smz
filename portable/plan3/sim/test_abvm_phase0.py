#!/usr/bin/env python3
"""ABVM phase-0 compiler, verifier, and fixed-state reference VM."""
import copy
import hashlib
import importlib.util
import json
import random
import struct
import sys
import tempfile
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location("abvm", ROOT / "tools/abvm.py")
abvm = importlib.util.module_from_spec(spec)
sys.modules["abvm"] = abvm
spec.loader.exec_module(abvm)

registry = json.loads(
    (ROOT / "spec/abvm/abi1.json").read_text(encoding="utf-8"))
assert registry == abvm.abi_registry()


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
assert image.flags & abvm.FLAG_HAS_SCOPE
assert image.flags & abvm.FLAG_HAS_SOUND
assert image.resources.max_lanes == 2
assert image.resources.sound_profiles == 1
assert image.resources.sound_listeners == 1
assert len(image.program_sha256) == 64 and len(image.source_sha256) == 64
assert image.route("Game").flags & abvm.ROUTE_POLICY_MASK == \
    abvm.ROUTE_ABORT_AND_RESTART
assert image.route("Whisper").flags & abvm.ROUTE_POLICY_MASK == \
    abvm.ROUTE_INTERRUPT_AND_RESUME
assert image.route("Game").flags & abvm.ROUTE_CLOCK_MASK == \
    abvm.ROUTE_CLOCK_WALL
assert image.route("Game").flags & abvm.ROUTE_PAUSE_RELEASE_HID
assert image.resources.max_interrupts == 1
assert compiled.source_map["programSha256"] == image.program_sha256
assert any(entry["type"] == "waitForSound"
           for entry in compiled.source_map["entries"])
assert len(compiled.image) < 8192

detected = abvm.ReferenceVm(compiled.image, seed=3, detected_profiles=[2])
detected_events = detected.run("Game")
assert ("WATCH", 2, "detected") in detected_events
assert any(event[0] == "KEY" and 70 in event[1] for event in detected_events)
assert any(event[0] == "SCOPE_RESUME" and
           event[1] == "CANCEL_ON_TERMINAL_LANE"
           for event in detected_events)

timeout = abvm.ReferenceVm(compiled.image, seed=3)
timeout_events = timeout.run("Game")
assert ("WATCH", 2, "timeout") in timeout_events
assert not any(event[0] == "KEY" and 70 in event[1] for event in timeout_events)
assert ("TYPE", "hi :)") in abvm.ReferenceVm(compiled.image).run("Whisper")

# Pause releases held HID state, preserves the exact PC, and leaves absolute
# Game deadlines unchanged under the WALL clock policy.
control_project = {"pipelines": {
    "Game": [node("forLoop", {
        "mode": "time", "timeValue": 10, "timeUnit": "second"}, [
            node("keyDown", {"key": "A"}),
            node("delay", {"minMs": 1000, "maxMs": 1000}),
            node("keyUp", {"key": "A"})])],
    "Whisper": [
        node("delay", {"minMs": 3000, "maxMs": 3000}),
        node("typeText", {"text": "interrupt"})],
}}
control = abvm.Compiler().compile_amsj(control_project)
paused = abvm.ReferenceVm(control.image, seed=1)
paused.start("Game")
assert paused.step_next() and paused.step_next()
assert paused.pressed_keys == {65}
game_lane = next(lane for lane in paused.lanes if lane.active)
saved_pc = game_lane.pc
saved_deadline = game_lane.frames[0]["deadline"]
assert paused.pause()
assert not paused.pressed_keys
paused.advance(5000)
assert game_lane.pc == saved_pc
assert game_lane.frames[0]["deadline"] == saved_deadline
assert paused.resume()
assert game_lane.pc == saved_pc
assert game_lane.frames[0]["deadline"] == saved_deadline
assert any(event[:2] == ("HID_RELEASE_ALL", "pause")
           for event in paused.events)

# ACTIVE clock routes shift pending due times and loop deadlines by the exact
# pause duration. This policy is supported but is not used by current Game.
abvm.ROUTE_CLOCK_BY_NAME["Game"] = abvm.ROUTE_CLOCK_ACTIVE
try:
    active_program = abvm.Compiler().compile_amsj(control_project)
finally:
    abvm.ROUTE_CLOCK_BY_NAME["Game"] = abvm.ROUTE_CLOCK_WALL
active_clock = abvm.ReferenceVm(active_program.image, seed=1)
active_clock.start("Game")
assert active_clock.step_next() and active_clock.step_next()
active_lane = active_clock.lanes[0]
active_deadline = active_lane.frames[0]["deadline"]
assert active_clock.pause()
active_clock.advance(5000)
assert active_clock.resume()
assert active_lane.frames[0]["deadline"] == active_deadline + 5000

# Whisper executes as one bounded global interrupt. Game PC/deadline stay
# intact, and its three seconds count against the wall-clock Game deadline.
interrupted = abvm.ReferenceVm(control.image, seed=1)
interrupted.start("Game")
assert interrupted.step_next() and interrupted.step_next()
assert interrupted.pressed_keys == {65}
game_context_lane = interrupted.lanes[0]
interrupt_pc = game_context_lane.pc
interrupt_deadline = game_context_lane.frames[0]["deadline"]
interrupted.interrupt("Whisper")
assert not interrupted.pressed_keys
assert any(event[:2] == ("HID_RELEASE_ALL", "interrupt")
           for event in interrupted.events)
try:
    interrupted.interrupt("Whisper")
    raise AssertionError("nested interrupt was accepted")
except abvm.AbvmError as exc:
    assert "nested interrupt" in str(exc)
while interrupted.suspended:
    assert interrupted.step_next()
assert interrupted.current_route_name == "Game"
assert game_context_lane.pc == interrupt_pc
assert game_context_lane.frames[0]["deadline"] == interrupt_deadline
assert interrupted.now == 3000
assert any(event[0] == "INTERRUPT_RESUME" for event in interrupted.events)

# Stop cancels every scope lane and always releases HID.
stopped = abvm.ReferenceVm(compiled.image, seed=2)
stopped.start("Game")
while not any(event[0] == "SCOPE_BEGIN" for event in stopped.events):
    assert stopped.step_next()
stopped.stop()
assert not stopped.running and not stopped.paused
assert not stopped.pressed_keys
assert not any(lane.active for lane in stopped.lanes)
assert stopped.events[-1][0] == "STOP"

# Control-path stress: repeated commands remain bounded and never retain HID.
stress_program = abvm.Compiler().compile_amsj({"pipelines": {
    "Game": [node("forLoop", {"mode": "count", "count": 0}, [
        node("keyDown", {"key": "A"}),
        node("delay", {"minMs": 2, "maxMs": 2}),
        node("keyUp", {"key": "A"})])],
}}, ["Game"])
pause_stress = abvm.ReferenceVm(stress_program.image, seed=9)
pause_stress.start("Game")
for _ in range(50):
    assert pause_stress.step_next()
    assert pause_stress.pause()
    pause_stress.advance(1)
    assert pause_stress.resume()
assert not pause_stress.pressed_keys
pause_stress.stop()

for boundary in range(50):
    stop_stress = abvm.ReferenceVm(stress_program.image, seed=boundary)
    stop_stress.start("Game")
    for _ in range(boundary % 8):
        if not stop_stress.step_next():
            break
    stop_stress.stop()
    assert not stop_stress.running
    assert not stop_stress.pressed_keys
    assert not stop_stress.suspended

corrupt = bytearray(compiled.image)
corrupt[abvm.HEADER.size + 3] ^= 0x40
try:
    abvm.Verifier.verify(bytes(corrupt))
    raise AssertionError("corrupt CRC was accepted")
except abvm.AbvmError as exc:
    assert "CRC" in str(exc)


def resign(data):
    fields = list(abvm.HEADER.unpack_from(data))
    payload = data[abvm.HEADER.size:]
    fields[14] = 0
    fields[18] = bytes(32)
    fields[18] = hashlib.sha256(abvm.HEADER.pack(*fields) + payload).digest()
    fields[14] = zlib.crc32(abvm.HEADER.pack(*fields) + payload) & 0xFFFFFFFF
    return abvm.HEADER.pack(*fields) + payload


bad_hash = bytearray(compiled.image)
bad_hash[abvm.HEADER.size + 4] ^= 1
hash_fields = list(abvm.HEADER.unpack_from(bad_hash))
hash_fields[14] = 0
bad_hash[:abvm.HEADER.size] = abvm.HEADER.pack(*hash_fields)
hash_fields[14] = zlib.crc32(bytes(bad_hash)) & 0xFFFFFFFF
bad_hash[:abvm.HEADER.size] = abvm.HEADER.pack(*hash_fields)
try:
    abvm.Verifier.verify(bytes(bad_hash))
    raise AssertionError("invalid program SHA-256 was accepted")
except abvm.AbvmError as exc:
    assert "SHA-256" in str(exc)

bad_resource = bytearray(compiled.image)
struct.pack_into(
    "<H", bad_resource, image.resource_off + 8, abvm.MAX_ACTORS + 1)
try:
    abvm.Verifier.verify(resign(bytes(bad_resource)))
    raise AssertionError("resource overflow was accepted")
except abvm.AbvmError as exc:
    assert "actors" in str(exc)

rng = random.Random(7)
for _ in range(100):
    damaged = bytearray(compiled.image)
    damaged[rng.randrange(len(damaged))] ^= 1 << rng.randrange(8)
    try:
        abvm.Verifier.verify(bytes(damaged))
        raise AssertionError("corrupt fuzz image was accepted")
    except abvm.AbvmError:
        pass

with tempfile.TemporaryDirectory() as tmp:
    tmp = Path(tmp)
    source = tmp / "project.amsj"
    target = tmp / "program.abp"
    source.write_text(json.dumps(project), encoding="utf-8")
    built = abvm.compile_file(source, target, ["Game", "Whisper"])
    map_path = Path(str(target) + ".map.json")
    assert target.exists() and map_path.exists()
    external_map = json.loads(map_path.read_text(encoding="utf-8"))
    assert external_map["programSha256"] == built.program_sha256

too_deep = node("delay", {"minMs": 1, "maxMs": 1})
for _ in range(9):
    too_deep = node("forLoop", {"mode": "count", "count": 1}, [too_deep])
try:
    abvm.Compiler().compile_amsj({"pipelines": {"Game": [too_deep]}}, ["Game"])
    raise AssertionError("depth overflow was accepted")
except abvm.AbvmError as exc:
    assert "depth" in str(exc)

nested_scope = copy.deepcopy(game[0]["Children"][1])
nested_scope["Children"][2]["Children"].append(
    copy.deepcopy(game[0]["Children"][1]))
try:
    abvm.Compiler().compile_amsj(
        {"pipelines": {"Game": [nested_scope]}}, ["Game"])
    raise AssertionError("nested Parallel Group was accepted")
except abvm.AbvmError as exc:
    assert "nested Parallel" in str(exc)

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
