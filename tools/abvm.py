#!/usr/bin/env python3
"""PC-side ABP1 compiler, verifier, and deterministic reference VM."""
from __future__ import annotations

import argparse
import hashlib
import json
import random
import struct
import sys
import zlib
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

MAGIC = b"ABP1"
FORMAT_VERSION = VM_ABI = 1
MAX_FRAMES, MAX_LANES = 8, 2
HEADER = struct.Struct("<4sHHIIIIIIIIHHI16sI")  # 64 bytes
INSTRUCTION = struct.Struct("<BBHIII")           # 16 bytes
ROUTE = struct.Struct("<HHIII")                  # 16 bytes
CONST_HEADER = struct.Struct("<BBHI")

FLAG_HAS_TYPE, FLAG_HAS_RACE, FLAG_HAS_SOUND = 1, 2, 4
CONST_UTF8, CONST_TYPE, CONST_MOUSE, CONST_RANGES, CONST_RACE = range(1, 6)
OP_END, OP_DELAY, OP_KEY, OP_KDOWN, OP_KUP, OP_TYPE, OP_RMOUSE = range(7)
OP_LOOP_ENTER, OP_LOOP_NEXT, OP_RPKG_ENTER, OP_ITEM_END = 10, 11, 12, 13
OP_RACE_BEGIN, OP_LANE_END, OP_WATCH, OP_JUMP = 20, 21, 22, 30

ROUTE_IDS = {
    "Desktop": 1, "Launch": 2, "Startup": 3, "LoginOrDc": 4,
    "LaunchRecovery": 5, "CharacterDashboard": 6,
    "EnteringGameLoading": 7, "Game": 8, "Targeted": 9,
    "Whisper": 10, "Splash": 11,
}


class AbvmError(ValueError):
    pass


@dataclass
class Ins:
    op: int
    flags: int = 0
    a: int = 0
    b: int = 0
    c: int = 0
    d: int = 0

    def pack(self) -> bytes:
        return INSTRUCTION.pack(self.op, self.flags, self.a, self.b, self.c, self.d)


@dataclass
class RouteInfo:
    route_id: int
    flags: int
    pc: int
    length: int
    name_const: int


@dataclass
class Program:
    image: bytes
    instructions: list[Ins]
    constants: list[tuple[int, int, bytes]]
    routes: list[RouteInfo]
    max_frames: int
    max_lanes: int
    flags: int


class Pool:
    def __init__(self) -> None:
        self.items: list[tuple[int, int, bytes]] = []
        self._ids: dict[tuple[int, int, bytes], int] = {}

    def add(self, kind: int, payload: bytes, flags: int = 0) -> int:
        key = kind, flags, payload
        if key in self._ids:
            return self._ids[key]
        index = len(self.items)
        if index > 0xFFFF:
            raise AbvmError("constant pool exceeds 65535 entries")
        self.items.append(key)
        self._ids[key] = index
        return index

    def text(self, value: str) -> int:
        return self.add(CONST_UTF8, value.encode())

    def obj(self, kind: int, value: Any) -> int:
        raw = json.dumps(
            value, ensure_ascii=False, sort_keys=True, separators=(",", ":")
        ).encode()
        return self.add(kind, raw)


def props(node: dict[str, Any]) -> dict[str, Any]:
    return node.get("Props") or node.get("props") or {}


def children(node: dict[str, Any]) -> list[dict[str, Any]]:
    return list(node.get("Children") or node.get("children") or [])


def step_type(node: dict[str, Any]) -> str:
    return str(node.get("Type") or node.get("type") or "")


def disabled(node: dict[str, Any]) -> bool:
    return bool(node.get("IsDisabled") or node.get("isDisabled"))


def integer(value: Any, default: int = 0) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def ordered(a: Any, b: Any, da: int = 0, db: int = 0) -> tuple[int, int]:
    lo, hi = integer(a, da), integer(b, db)
    return (lo, hi) if hi >= lo else (hi, lo)


def vk(value: Any) -> int:
    text = str(value or "").strip().upper()
    aliases = {
        "ENTER": 13, "RETURN": 13, "ESC": 27, "ESCAPE": 27,
        "BACKSPACE": 8, "TAB": 9, "SPACE": 32,
        "LEFT": 37, "UP": 38, "RIGHT": 39, "DOWN": 40,
        "SHIFT": 160, "CTRL": 162, "CONTROL": 162,
        "ALT": 164, "WIN": 91, "WINDOWS": 91,
    }
    if text in aliases:
        return aliases[text]
    if len(text) == 1 and text.isalnum():
        return ord(text)
    if text.startswith("F") and text[1:].isdigit() and 1 <= int(text[1:]) <= 12:
        return 111 + int(text[1:])
    raise AbvmError("unsupported key: " + text)


class Compiler:
    def __init__(self) -> None:
        self.code: list[Ins] = []
        self.pool = Pool()
        self.routes: list[RouteInfo] = []
        self.max_frames, self.max_lanes, self.flags = 0, 1, 0

    def emit(self, op: int, flags: int = 0, a: int = 0,
             b: int = 0, c: int = 0, d: int = 0) -> int:
        if not 0 <= a <= 0xFFFF or any(
                not 0 <= value <= 0xFFFFFFFF for value in (b, c, d)):
            raise AbvmError("operand overflow")
        self.code.append(Ins(op, flags, a, b, c, d))
        return len(self.code) - 1

    def patch(self, index: int, **values: int) -> None:
        for key, value in values.items():
            setattr(self.code[index], key, value)

    def compile_amsj(self, source: dict[str, Any],
                     route_names: Iterable[str] = ("Game", "Whisper")) -> Program:
        pipelines = source.get("pipelines") or source.get("Pipelines")
        if not isinstance(pipelines, dict):
            raise AbvmError("AMSJ pipelines object is missing")
        for name in route_names:
            nodes = pipelines.get(name)
            if nodes is None:
                continue
            if not isinstance(nodes, list):
                raise AbvmError("pipeline is not a list: " + name)
            start = len(self.code)
            self.compile_nodes(nodes, 0)
            self.emit(OP_END)
            self.routes.append(RouteInfo(
                ROUTE_IDS.get(name, 100 + len(self.routes)), 0, start,
                len(self.code) - start, self.pool.text(name)))
        if not self.routes:
            raise AbvmError("none of the requested routes exist")
        return self.finish(source)

    def frame(self, depth: int) -> None:
        self.max_frames = max(self.max_frames, depth)
        if depth > MAX_FRAMES:
            raise AbvmError(f"frame depth {depth} exceeds {MAX_FRAMES}")

    def compile_nodes(self, nodes: list[dict[str, Any]], depth: int) -> None:
        self.frame(depth)
        for node in nodes:
            if disabled(node) or step_type(node) == "comment":
                continue
            self.compile_node(node, depth)
            delay = integer(node.get("Delay") or node.get("delay"))
            delay_max = integer(node.get("DelayMax") or node.get("delayMax"), delay)
            if step_type(node) != "delay" and (delay or delay_max):
                lo, hi = ordered(delay, delay_max)
                self.emit(OP_DELAY, b=lo, c=hi)

    def compile_node(self, node: dict[str, Any], depth: int) -> None:
        kind, p = step_type(node), props(node)
        if kind == "delay":
            lo, hi = ordered(p.get("minMs"), p.get("maxMs"))
            self.emit(OP_DELAY, b=lo, c=hi)
        elif kind == "keystroke":
            keys = ([162] if p.get("modCtrl") else []) + \
                   ([160] if p.get("modShift") else []) + \
                   ([164] if p.get("modAlt") else []) + \
                   ([91] if p.get("modWin") else []) + [vk(p.get("key"))]
            if len(keys) > 4:
                raise AbvmError("at most four keys per combo")
            packed = sum((key & 0xFF) << (i * 8) for i, key in enumerate(keys))
            lo, hi = ordered(p.get("holdMin"), p.get("holdMax"))
            self.emit(OP_KEY, flags=len(keys), b=packed, c=lo, d=hi)
        elif kind in ("keyDown", "keyUp"):
            self.emit(OP_KDOWN if kind == "keyDown" else OP_KUP, a=vk(p.get("key")))
        elif kind == "typeText":
            self.flags |= FLAG_HAS_TYPE
            spec = dict(p)
            spec["text"] = str(p.get("text") or "")
            self.emit(OP_TYPE, a=self.pool.obj(CONST_TYPE, spec))
        elif kind == "randomMousePosition":
            self.emit(OP_RMOUSE, a=self.pool.obj(CONST_MOUSE, p))
        elif kind == "forLoop":
            self.compile_loop(node, depth)
        elif kind == "randomPackage":
            self.compile_package(node, depth)
        elif kind == "parallelGroup":
            self.compile_race(node, depth)
        elif kind == "waitForSound":
            self.compile_watch(node, depth)
        else:
            raise AbvmError("unsupported ABVM step: " + kind)

    def compile_loop(self, node: dict[str, Any], depth: int) -> None:
        p = props(node)
        timed = str(p.get("mode") or "count").lower() == "time"
        if timed:
            unit = str(p.get("timeUnit") or "second").lower()
            scale = 60_000 if unit.startswith("min") else \
                3_600_000 if unit.startswith("hour") else 1000
            value = integer(p.get("timeValue"), 1) * scale
        else:
            value = max(0, integer(p.get("count"), 1))
        enter = self.emit(OP_LOOP_ENTER, flags=int(timed), b=value)
        body = len(self.code)
        self.compile_nodes(children(node), depth + 1)
        self.emit(OP_LOOP_NEXT, b=body)
        self.patch(enter, d=len(self.code))

    def compile_package(self, node: dict[str, Any], depth: int) -> None:
        p = props(node)
        mode_text = str(p.get("mode") or "seq").lower()
        mode = 2 if mode_text in ("randomsubset", "pick") else \
            1 if mode_text in ("shuffleall", "all") else 0
        lo, hi = ordered(p.get("minCount"), p.get("maxCount"), 1, 1)
        enter = self.emit(OP_RPKG_ENTER, flags=mode, b=max(0, lo), c=max(0, hi))
        ranges = []
        for child in children(node):
            if disabled(child) or step_type(child) == "comment":
                continue
            start = len(self.code)
            self.compile_nodes([child], depth + 1)
            end = len(self.code)
            self.emit(OP_ITEM_END)
            ranges.append((start, end))
        if not ranges:
            raise AbvmError("Random Package has no executable items")
        raw = struct.pack("<H", len(ranges)) + b"".join(
            struct.pack("<II", start, end) for start, end in ranges)
        self.patch(enter, a=self.pool.add(CONST_RANGES, raw), d=len(self.code))

    @staticmethod
    def split_lanes(items: list[dict[str, Any]]) -> list[list[dict[str, Any]]]:
        lanes: list[list[dict[str, Any]]] = [[]]
        for item in items:
            if step_type(item) == "comment" and \
                    str(props(item).get("text") or "").strip().lower() == "next":
                if lanes[-1]:
                    lanes.append([])
            else:
                lanes[-1].append(item)
        return [lane for lane in lanes if any(not disabled(item) for item in lane)]

    @staticmethod
    def contains(node: dict[str, Any], kind: str) -> bool:
        return step_type(node) == kind or any(
            Compiler.contains(child, kind) for child in children(node))

    def compile_race(self, node: dict[str, Any], depth: int) -> None:
        lanes = self.split_lanes(children(node))
        if len(lanes) != 2:
            raise AbvmError("phase-0 Parallel Group requires exactly two lanes")
        controllers = [i for i, lane in enumerate(lanes)
                       if any(self.contains(item, "waitForSound") for item in lane)]
        if len(controllers) != 1:
            raise AbvmError("phase-0 Parallel Group requires one Sound controller lane")
        controller = controllers[0]
        self.flags |= FLAG_HAS_RACE
        self.max_lanes = 2
        begin = self.emit(OP_RACE_BEGIN, flags=controller, a=2)
        ranges = []
        for index, lane in enumerate(lanes):
            start = len(self.code)
            self.compile_nodes(lane, depth + 1)
            end = len(self.code)
            self.emit(OP_LANE_END, flags=int(index == controller))
            ranges.append((start, end))
        raw = struct.pack("<BBH", 2, controller, 0) + b"".join(
            struct.pack("<II", start, end) for start, end in ranges)
        self.patch(begin, a=self.pool.add(CONST_RACE, raw), d=len(self.code))

    def compile_watch(self, node: dict[str, Any], depth: int) -> None:
        p = props(node)
        profile = integer(p.get("calibrationId"))
        lo = integer(p.get("timeoutMinSec")) * 1000
        hi = integer(p.get("timeoutMaxSec")) * 1000
        if not lo and not hi:
            lo = hi = integer(p.get("timeoutMs"))
        lo, hi = ordered(lo, hi)
        if profile <= 0 or hi <= 0:
            raise AbvmError("Wait For Sound needs calibrationId and timeout")
        self.flags |= FLAG_HAS_SOUND
        watch = self.emit(OP_WATCH, flags=1, a=profile, b=lo, c=hi)
        self.compile_nodes(children(node), depth + 1)
        self.patch(watch, d=len(self.code))

    def finish(self, source: dict[str, Any]) -> Program:
        code = b"".join(ins.pack() for ins in self.code)
        constants = bytearray()
        for kind, flags, payload in self.pool.items:
            constants.extend(CONST_HEADER.pack(kind, flags, 0, len(payload)))
            constants.extend(payload)
            while len(constants) & 3:
                constants.append(0)
        routes = b"".join(ROUTE.pack(
            route.route_id, route.flags, route.pc, route.length, route.name_const
        ) for route in self.routes)
        code_off = HEADER.size
        const_off = code_off + len(code)
        route_off = const_off + len(constants)
        file_size = route_off + len(routes)
        digest = hashlib.sha256(json.dumps(
            source, ensure_ascii=False, sort_keys=True, separators=(",", ":")
        ).encode()).digest()[:16]
        fields = [MAGIC, FORMAT_VERSION, VM_ABI, self.flags, file_size,
                  code_off, len(self.code), const_off, len(constants),
                  route_off, len(self.routes), self.max_frames, self.max_lanes,
                  0, digest, 0]
        image = HEADER.pack(*fields) + code + constants + routes
        fields[13] = zlib.crc32(image) & 0xFFFFFFFF
        image = HEADER.pack(*fields) + code + constants + routes
        result = Program(image, self.code, self.pool.items, self.routes,
                         self.max_frames, self.max_lanes, self.flags)
        Verifier.verify(image)
        return result


class Image:
    def __init__(self, data: bytes) -> None:
        if len(data) < HEADER.size:
            raise AbvmError("truncated ABP1 header")
        fields = list(HEADER.unpack_from(data))
        if fields[:3] != [MAGIC, FORMAT_VERSION, VM_ABI]:
            raise AbvmError("unsupported ABP1 magic/version/ABI")
        if fields[4] != len(data):
            raise AbvmError("ABP1 file size mismatch")
        expected = fields[13]
        fields[13] = 0
        if zlib.crc32(HEADER.pack(*fields) + data[HEADER.size:]) & 0xFFFFFFFF != expected:
            raise AbvmError("ABP1 CRC mismatch")
        self.data, self.flags = data, fields[3]
        self.code_off, self.code_count = fields[5], fields[6]
        self.const_off, self.const_size = fields[7], fields[8]
        self.route_off, self.route_count = fields[9], fields[10]
        self.max_frames, self.max_lanes = fields[11], fields[12]
        self.instructions = [
            Ins(*INSTRUCTION.unpack_from(data, self.code_off + i * INSTRUCTION.size))
            for i in range(self.code_count)
        ]
        self.constants: list[tuple[int, int, bytes]] = []
        cursor, end = self.const_off, self.const_off + self.const_size
        while cursor < end:
            if cursor + CONST_HEADER.size > end:
                raise AbvmError("truncated constant header")
            kind, flags, _, size = CONST_HEADER.unpack_from(data, cursor)
            cursor += CONST_HEADER.size
            if cursor + size > end:
                raise AbvmError("truncated constant payload")
            self.constants.append((kind, flags, data[cursor:cursor + size]))
            cursor = (cursor + size + 3) & ~3
        self.routes = [
            RouteInfo(*ROUTE.unpack_from(data, self.route_off + i * ROUTE.size))
            for i in range(self.route_count)
        ]

    def const(self, index: int, kind: int | None = None) -> bytes:
        if not 0 <= index < len(self.constants):
            raise AbvmError("constant index out of range")
        actual, _, payload = self.constants[index]
        if kind is not None and actual != kind:
            raise AbvmError("constant type mismatch")
        return payload

    def route(self, name: str) -> RouteInfo:
        for route in self.routes:
            if self.const(route.name_const, CONST_UTF8).decode() == name:
                return route
        raise AbvmError("route not found: " + name)


class Verifier:
    @staticmethod
    def verify(data: bytes) -> Image:
        image = Image(data)
        if image.max_frames > MAX_FRAMES or image.max_lanes > MAX_LANES:
            raise AbvmError("declared VM limits exceed firmware contract")
        code_end = image.code_off + image.code_count * INSTRUCTION.size
        if image.code_off != HEADER.size or code_end > len(data):
            raise AbvmError("invalid code bounds")
        if image.const_off != code_end or \
                image.route_off != image.const_off + image.const_size:
            raise AbvmError("non-canonical ABP1 section layout")
        if image.route_off + image.route_count * ROUTE.size != len(data):
            raise AbvmError("invalid route table bounds")
        measured = 0

        def walk(start: int, end: int, depth: int, watch_depth: int = 0) -> None:
            nonlocal measured
            measured = max(measured, depth)
            if depth > MAX_FRAMES:
                raise AbvmError("verified frame depth exceeds contract")
            pc = start
            while pc < end:
                if not 0 <= pc < len(image.instructions):
                    raise AbvmError("PC out of range")
                ins = image.instructions[pc]
                if ins.op == OP_LOOP_ENTER:
                    if not pc + 1 < ins.d <= end or \
                            image.instructions[ins.d - 1].op != OP_LOOP_NEXT:
                        raise AbvmError("invalid loop bounds")
                    walk(pc + 1, ins.d - 1, depth + 1, watch_depth)
                    pc = ins.d
                    continue
                if ins.op == OP_RPKG_ENTER:
                    raw = image.const(ins.a, CONST_RANGES)
                    count = struct.unpack_from("<H", raw)[0]
                    if not count or len(raw) != 2 + count * 8:
                        raise AbvmError("invalid Random Package table")
                    for i in range(count):
                        first, last = struct.unpack_from("<II", raw, 2 + i * 8)
                        if not pc < first <= last < ins.d or \
                                image.instructions[last].op != OP_ITEM_END:
                            raise AbvmError("invalid package item bounds")
                        walk(first, last, depth + 1, watch_depth)
                    pc = ins.d
                    continue
                if ins.op == OP_RACE_BEGIN:
                    raw = image.const(ins.a, CONST_RACE)
                    lanes, controller, _ = struct.unpack_from("<BBH", raw)
                    if not 2 <= lanes <= MAX_LANES or controller >= lanes or \
                            len(raw) != 4 + lanes * 8:
                        raise AbvmError("invalid Race descriptor")
                    for i in range(lanes):
                        first, last = struct.unpack_from("<II", raw, 4 + i * 8)
                        if not pc < first <= last < ins.d:
                            raise AbvmError("invalid Race lane bounds")
                        terminal = image.instructions[last]
                        if terminal.op != OP_LANE_END or \
                                bool(terminal.flags & 1) != (i == controller):
                            raise AbvmError("invalid Race controller lane")
                        walk(first, last, depth + 1, watch_depth)
                    pc = ins.d
                    continue
                if ins.op == OP_WATCH:
                    if watch_depth:
                        raise AbvmError("nested Watch is forbidden")
                    if not pc < ins.d <= end:
                        raise AbvmError("invalid Watch response bounds")
                    walk(pc + 1, ins.d, depth + 1, watch_depth + 1)
                    pc = ins.d
                    continue
                if ins.op == OP_TYPE:
                    image.const(ins.a, CONST_TYPE)
                    if not image.flags & FLAG_HAS_TYPE:
                        raise AbvmError("TYPE used without header capability")
                elif ins.op == OP_RMOUSE:
                    image.const(ins.a, CONST_MOUSE)
                elif ins.op == OP_JUMP:
                    if not 0 <= ins.d < len(image.instructions):
                        raise AbvmError("jump target out of range")
                elif ins.op not in {
                    OP_END, OP_DELAY, OP_KEY, OP_KDOWN, OP_KUP,
                    OP_LOOP_NEXT, OP_ITEM_END, OP_LANE_END,
                }:
                    raise AbvmError("unknown opcode: " + str(ins.op))
                pc += 1

        for route in image.routes:
            if route.length <= 0 or route.pc + route.length > len(image.instructions):
                raise AbvmError("route bounds invalid")
            if image.instructions[route.pc + route.length - 1].op != OP_END:
                raise AbvmError("route does not end with END")
            walk(route.pc, route.pc + route.length, 0)
        if measured != image.max_frames:
            raise AbvmError(
                f"declared frame depth {image.max_frames} != verified {measured}")
        return image


@dataclass
class Lane:
    pc: int
    end: int
    due: int = 0
    frames: list[dict[str, Any]] | None = None
    active: bool = True
    group: dict[str, Any] | None = None
    controller: bool = False

    def __post_init__(self) -> None:
        if self.frames is None:
            self.frames = []


class ReferenceVm:
    """Host semantic oracle; firmware will use fixed arrays for the same state."""
    def __init__(self, data: bytes, seed: int = 1,
                 detected_profiles: Iterable[int] = ()) -> None:
        self.image = Verifier.verify(data)
        self.rng = random.Random(seed)
        self.detected = set(detected_profiles)
        self.now = 0
        self.events: list[tuple[Any, ...]] = []
        self.running = False
        self.lanes: list[Lane] = []

    def run(self, route_name: str, max_fetches: int = 100_000) -> list[tuple[Any, ...]]:
        route = self.image.route(route_name)
        self.lanes = [Lane(route.pc, route.pc + route.length)]
        self.running = True
        fetches = 0
        while self.running and any(lane.active for lane in self.lanes):
            active = [lane for lane in self.lanes if lane.active]
            self.now = max(self.now, min(lane.due for lane in active))
            for lane in [item for item in active if item.due <= self.now]:
                self.step(lane)
                fetches += 1
                if fetches > max_fetches:
                    raise AbvmError("reference VM fetch budget exhausted")
        return self.events

    def stop(self) -> None:
        self.running = False
        for lane in self.lanes:
            lane.active = False
        self.events.append(("STOP", self.now))

    def finish_lane(self, lane: Lane) -> None:
        lane.active = False
        if lane.group is None:
            return
        group = lane.group
        if lane.controller:
            for sibling in group["children"]:
                sibling.active = False
            parent = group["parent"]
            parent.active, parent.due = True, self.now
            self.events.append(("RACE_RESUME", self.now))
        elif not any(child.active for child in group["children"]):
            parent = group["parent"]
            parent.active, parent.due = True, self.now

    def step(self, lane: Lane) -> None:
        if lane.pc >= lane.end:
            self.finish_lane(lane)
            return
        ins = self.image.instructions[lane.pc]
        if ins.op == OP_END:
            self.finish_lane(lane)
        elif ins.op == OP_DELAY:
            delay = self.rng.randint(ins.b, ins.c)
            self.events.append(("DELAY", delay))
            lane.pc += 1
            lane.due = self.now + delay
        elif ins.op == OP_KEY:
            keys = tuple((ins.b >> (i * 8)) & 0xFF for i in range(ins.flags))
            self.events.append(("KEY", keys, ins.c, ins.d))
            lane.pc += 1
        elif ins.op in (OP_KDOWN, OP_KUP):
            self.events.append(("KDOWN" if ins.op == OP_KDOWN else "KUP", ins.a))
            lane.pc += 1
        elif ins.op == OP_TYPE:
            value = json.loads(self.image.const(ins.a, CONST_TYPE))
            self.events.append(("TYPE", value["text"]))
            lane.pc += 1
        elif ins.op == OP_RMOUSE:
            value = json.loads(self.image.const(ins.a, CONST_MOUSE))
            self.events.append(("RMOUSE", integer(value.get("w")), integer(value.get("h"))))
            lane.pc += 1
            lane.due = self.now + max(1, integer(value.get("moveTimeMin"), 1))
        elif ins.op == OP_LOOP_ENTER:
            lane.frames.append({
                "kind": "loop", "body": lane.pc + 1,
                "left": None if ins.flags & 1 or ins.b == 0 else ins.b,
                "deadline": self.now + ins.b if ins.flags & 1 else None,
            })
            lane.pc += 1
        elif ins.op == OP_LOOP_NEXT:
            frame = lane.frames[-1]
            again = self.now < frame["deadline"] if frame["deadline"] is not None \
                else frame["left"] is None or frame["left"] > 1
            if again:
                if frame["left"] is not None:
                    frame["left"] -= 1
                lane.pc = frame["body"]
            else:
                lane.frames.pop()
                lane.pc += 1
        elif ins.op == OP_RPKG_ENTER:
            raw = self.image.const(ins.a, CONST_RANGES)
            count = struct.unpack_from("<H", raw)[0]
            ranges = [struct.unpack_from("<II", raw, 2 + i * 8)
                      for i in range(count)]
            order = list(range(count))
            if ins.flags == 1:
                self.rng.shuffle(order)
            elif ins.flags == 2:
                take = self.rng.randint(min(ins.b, ins.c), max(ins.b, ins.c))
                self.rng.shuffle(order)
                order = order[:min(take, len(order))]
            lane.frames.append({
                "kind": "package", "ranges": ranges, "order": order,
                "next": 1, "after": ins.d,
            })
            lane.pc = ranges[order[0]][0] if order else ins.d
        elif ins.op == OP_ITEM_END:
            frame = lane.frames[-1]
            if frame["next"] < len(frame["order"]):
                chosen = frame["order"][frame["next"]]
                frame["next"] += 1
                lane.pc = frame["ranges"][chosen][0]
            else:
                lane.frames.pop()
                lane.pc = frame["after"]
        elif ins.op == OP_RACE_BEGIN:
            raw = self.image.const(ins.a, CONST_RACE)
            count, controller, _ = struct.unpack_from("<BBH", raw)
            parent = lane
            parent.pc, parent.active = ins.d, False
            group: dict[str, Any] = {"parent": parent, "children": []}
            for i in range(count):
                start, end = struct.unpack_from("<II", raw, 4 + i * 8)
                child = Lane(start, end, self.now, group=group,
                             controller=i == controller)
                group["children"].append(child)
                self.lanes.append(child)
            self.events.append(("RACE_BEGIN", count, controller))
        elif ins.op == OP_LANE_END:
            self.finish_lane(lane)
        elif ins.op == OP_WATCH:
            hit = ins.a in self.detected
            self.events.append(("WATCH", ins.a, "detected" if hit else "timeout"))
            lane.pc = lane.pc + 1 if hit else ins.d
            lane.due = self.now + self.rng.randint(ins.b, ins.c)
        elif ins.op == OP_JUMP:
            lane.pc = ins.d
        else:
            raise AbvmError("reference VM cannot execute opcode " + str(ins.op))
        if len(lane.frames) > MAX_FRAMES:
            raise AbvmError("runtime frame overflow")


def compile_file(source: Path, target: Path, routes: list[str]) -> Program:
    data = json.loads(source.read_text(encoding="utf-8-sig"))
    program = Compiler().compile_amsj(data, routes)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(program.image)
    return program


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="abvm")
    commands = parser.add_subparsers(dest="command", required=True)
    build = commands.add_parser("compile")
    build.add_argument("source", type=Path)
    build.add_argument("target", type=Path)
    build.add_argument("--routes", nargs="+", default=["Game", "Whisper"])
    verify = commands.add_parser("verify")
    verify.add_argument("program", type=Path)
    trace = commands.add_parser("trace")
    trace.add_argument("program", type=Path)
    trace.add_argument("--route", default="Game")
    trace.add_argument("--detected", type=int, nargs="*", default=[])
    args = parser.parse_args(argv)
    try:
        if args.command == "compile":
            result = compile_file(args.source, args.target, args.routes)
            print(json.dumps({
                "bytes": len(result.image), "instructions": len(result.instructions),
                "constants": len(result.constants), "routes": len(result.routes),
                "maxFrames": result.max_frames, "maxLanes": result.max_lanes,
            }, sort_keys=True))
        elif args.command == "verify":
            image = Verifier.verify(args.program.read_bytes())
            print(json.dumps({
                "bytes": len(image.data), "instructions": image.code_count,
                "constants": len(image.constants), "routes": image.route_count,
                "maxFrames": image.max_frames, "maxLanes": image.max_lanes,
            }, sort_keys=True))
        else:
            vm = ReferenceVm(args.program.read_bytes(),
                             detected_profiles=args.detected)
            for event in vm.run(args.route):
                print(json.dumps(event, ensure_ascii=False))
        return 0
    except (AbvmError, OSError, json.JSONDecodeError) as exc:
        print("ABVM error:", exc, file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())