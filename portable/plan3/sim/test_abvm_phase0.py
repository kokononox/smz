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
assert image.route("Game").flags == abvm.ROUTE_ABORT_AND_RESTART
assert image.route("Whisper").flags == abvm.ROUTE_INTERRUPT_AND_RESUME
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
