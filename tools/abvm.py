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
FORMAT_VERSION, VM_ABI = 2, 1
MAX_FRAMES, MAX_LANES = 8, 2
MAX_ACTORS, MAX_EVENTS, MAX_INTERRUPTS = 4, 4, 1
MAX_SOUND_PROFILES, MAX_SOUND_LISTENERS, MAX_PWM_CHANNELS = 8, 1, 1
# 128 bytes: identity/version, section directory, CRC, limits, source and
# program SHA-256.  Program SHA is calculated with its own field and CRC zero.
HEADER = struct.Struct("<4sHHIIIIIIIIIIIIHH32s32sI")
INSTRUCTION = struct.Struct("<BBHIII")           # 16 bytes
ROUTE = struct.Struct("<HHIII")                  # 16 bytes
CONST_HEADER = struct.Struct("<BBHI")
RESOURCE = struct.Struct("<HHHHHHHHHHIIII")      # 36 bytes

FLAG_HAS_TYPE, FLAG_HAS_SCOPE, FLAG_HAS_SOUND = 1, 2, 4
CONST_UTF8, CONST_TYPE, CONST_MOUSE, CONST_RANGES, CONST_SCOPE = range(1, 6)
OP_END, OP_DELAY, OP_KEY, OP_KDOWN, OP_KUP, OP_TYPE, OP_RMOUSE = range(7)
OP_LOOP_ENTER, OP_LOOP_NEXT, OP_RPKG_ENTER, OP_ITEM_END = 10, 11, 12, 13
OP_SCOPE_BEGIN, OP_LANE_END, OP_WATCH, OP_JUMP = 20, 21, 22, 30

SCOPE_JOIN_ALL = 1
SCOPE_CANCEL_ON_ANY = 2
SCOPE_CANCEL_ON_TERMINAL_LANE = 3
SCOPE_KEEP_RUNNING_UNTIL_CANCELLED = 4
SCOPE_POLICIES = {
    SCOPE_JOIN_ALL: "JOIN_ALL",
    SCOPE_CANCEL_ON_ANY: "CANCEL_ON_ANY",
    SCOPE_CANCEL_ON_TERMINAL_LANE: "CANCEL_ON_TERMINAL_LANE",
    SCOPE_KEEP_RUNNING_UNTIL_CANCELLED: "KEEP_RUNNING_UNTIL_CANCELLED",
}

ROUTE_DENY = 0
ROUTE_ABORT_AND_START = 1
ROUTE_INTERRUPT_AND_RESUME = 2
ROUTE_CANCEL_SCOPE_AND_CONTINUE = 3
ROUTE_ABORT_AND_RESTART = 4
ROUTE_POLICY_MASK = 0x00FF
ROUTE_CLOCK_WALL = 0x0000
ROUTE_CLOCK_ACTIVE = 0x0100
ROUTE_CLOCK_MASK = 0x0100
ROUTE_PAUSE_RELEASE_HID = 0x0200
ROUTE_ALLOWED_FLAGS = (
    ROUTE_POLICY_MASK | ROUTE_CLOCK_MASK | ROUTE_PAUSE_RELEASE_HID
)
ROUTE_POLICIES = {
    ROUTE_DENY: "DENY",
    ROUTE_ABORT_AND_START: "ABORT_AND_START",
    ROUTE_INTERRUPT_AND_RESUME: "INTERRUPT_AND_RESUME",
    ROUTE_CANCEL_SCOPE_AND_CONTINUE: "CANCEL_SCOPE_AND_CONTINUE",
    ROUTE_ABORT_AND_RESTART: "ABORT_AND_RESTART",
}

ROUTE_IDS = {
    "Desktop": 1, "Launch": 2, "Startup": 3, "LoginOrDc": 4,
    "LaunchRecovery": 5, "CharacterDashboard": 6,
    "EnteringGameLoading": 7, "Game": 8, "Targeted": 9,
    "Whisper": 10, "Splash": 11,
}

ROUTE_POLICY_BY_NAME = {
    "Desktop": ROUTE_ABORT_AND_START,
    "Launch": ROUTE_ABORT_AND_START,
    "Startup": ROUTE_ABORT_AND_START,
    "LoginOrDc": ROUTE_ABORT_AND_START,
    "LaunchRecovery": ROUTE_ABORT_AND_START,
    "CharacterDashboard": ROUTE_ABORT_AND_START,
    "EnteringGameLoading": ROUTE_ABORT_AND_START,
    "Game": ROUTE_ABORT_AND_RESTART,
    "Targeted": ROUTE_ABORT_AND_START,
    "Whisper": ROUTE_INTERRUPT_AND_RESUME,
    "Splash": ROUTE_CANCEL_SCOPE_AND_CONTINUE,
}

# Real-time routes keep absolute deadlines while paused and while an interrupt
# route executes.  ACTIVE is reserved for workflows whose timers must freeze.
ROUTE_CLOCK_BY_NAME = {name: ROUTE_CLOCK_WALL for name in ROUTE_IDS}

OPCODES = {
    "END": OP_END, "DELAY": OP_DELAY, "KEY": OP_KEY,
    "KDOWN": OP_KDOWN, "KUP": OP_KUP, "TYPE": OP_TYPE,
    "RMOUSE": OP_RMOUSE, "LOOP_ENTER": OP_LOOP_ENTER,
    "LOOP_NEXT": OP_LOOP_NEXT, "RPKG_ENTER": OP_RPKG_ENTER,
    "ITEM_END": OP_ITEM_END, "SCOPE_BEGIN": OP_SCOPE_BEGIN,
    "LANE_END": OP_LANE_END, "WATCH": OP_WATCH, "JUMP": OP_JUMP,
}

CONSTANT_KINDS = {
    "UTF8": CONST_UTF8, "TYPE": CONST_TYPE, "MOUSE": CONST_MOUSE,
    "RANGES": CONST_RANGES, "SCOPE": CONST_SCOPE,
}


def abi_registry() -> dict[str, Any]:
    """Single machine-readable registry used by host and firmware codegen."""
    return {
        "magic": MAGIC.decode(),
        "formatVersion": FORMAT_VERSION,
        "vmAbi": VM_ABI,
        "sizes": {
            "header": HEADER.size,
            "instruction": INSTRUCTION.size,
            "route": ROUTE.size,
            "resourceCertificate": RESOURCE.size,
        },
        "limits": {
            "frames": MAX_FRAMES,
            "lanes": MAX_LANES,
            "actors": MAX_ACTORS,
            "events": MAX_EVENTS,
            "interrupts": MAX_INTERRUPTS,
            "soundProfiles": MAX_SOUND_PROFILES,
            "soundListeners": MAX_SOUND_LISTENERS,
            "pwmChannels": MAX_PWM_CHANNELS,
        },
        "opcodes": OPCODES,
        "constantKinds": CONSTANT_KINDS,
        "scopePolicies": {
            name: value for value, name in SCOPE_POLICIES.items()
        },
        "routePolicies": {
            name: value for value, name in ROUTE_POLICIES.items()
        },
        "routeClockPolicies": {
            "WALL": ROUTE_CLOCK_WALL,
            "ACTIVE": ROUTE_CLOCK_ACTIVE,
        },
        "routeFlags": {
            "PAUSE_RELEASE_HID": ROUTE_PAUSE_RELEASE_HID,
        },
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


@dataclass(frozen=True)
class ResourceCertificate:
    max_frames: int
    max_lanes: int
    max_actors: int
    max_events: int
    max_interrupts: int
    sound_profiles: int
    sound_listeners: int
    pwm_channels: int
    max_const_bytes: int
    max_type_bytes: int
    max_mouse_bytes: int
    capabilities: int

    def pack(self) -> bytes:
        return RESOURCE.pack(
            1, RESOURCE.size, self.max_frames, self.max_lanes,
            self.max_actors, self.max_events, self.max_interrupts,
            self.sound_profiles, self.sound_listeners, self.pwm_channels,
            self.max_const_bytes, self.max_type_bytes, self.max_mouse_bytes,
            self.capabilities,
        )


@dataclass
class Program:
    image: bytes
    instructions: list[Ins]
    constants: list[tuple[int, int, bytes]]
    routes: list[RouteInfo]
    max_frames: int
    max_lanes: int
    flags: int
    resources: ResourceCertificate
    source_map: dict[str, Any]
    source_sha256: str
    program_sha256: str


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
        self.sound_profiles: set[int] = set()
        self.source_entries: dict[int, dict[str, Any]] = {}
        self.current_route = ""
        self.scope_depth = 0

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
            self.current_route = name
            self.compile_nodes(nodes, 0, ())
            self.emit(OP_END)
            self.routes.append(RouteInfo(
                ROUTE_IDS.get(name, 100 + len(self.routes)),
                ROUTE_POLICY_BY_NAME.get(name, ROUTE_DENY) |
                ROUTE_CLOCK_BY_NAME.get(name, ROUTE_CLOCK_WALL) |
                ROUTE_PAUSE_RELEASE_HID,
                start,
                len(self.code) - start, self.pool.text(name)))
        if not self.routes:
            raise AbvmError("none of the requested routes exist")
        return self.finish(source)

    def frame(self, depth: int) -> None:
        self.max_frames = max(self.max_frames, depth)
        if depth > MAX_FRAMES:
            raise AbvmError(f"frame depth {depth} exceeds {MAX_FRAMES}")

    @staticmethod
    def source_id(node: dict[str, Any], path: tuple[int, ...]) -> str:
        explicit = (node.get("Id") or node.get("id") or
                    node.get("StepId") or node.get("stepId"))
        return str(explicit) if explicit else ".".join(map(str, path))

    def map_range(self, first: int, last: int, node: dict[str, Any],
                  path: tuple[int, ...]) -> None:
        entry = {
            "route": self.current_route,
            "stepId": self.source_id(node, path),
            "path": list(path),
            "type": step_type(node),
        }
        for pc in range(first, last):
            # Inner nodes are more precise than their enclosing container.
            self.source_entries.setdefault(pc, entry)

    def compile_nodes(self, nodes: list[dict[str, Any]], depth: int,
                      path: tuple[int, ...]) -> None:
        self.frame(depth)
        for index, node in enumerate(nodes):
            if disabled(node) or step_type(node) == "comment":
                continue
            node_path = path + (index,)
            first = len(self.code)
            self.compile_node(node, depth, node_path)
            delay = integer(node.get("Delay") or node.get("delay"))
            delay_max = integer(node.get("DelayMax") or node.get("delayMax"), delay)
            if step_type(node) != "delay" and (delay or delay_max):
                lo, hi = ordered(delay, delay_max)
                self.emit(OP_DELAY, b=lo, c=hi)
            self.map_range(first, len(self.code), node, node_path)

    def compile_node(self, node: dict[str, Any], depth: int,
                     path: tuple[int, ...]) -> None:
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
            self.compile_loop(node, depth, path)
        elif kind == "randomPackage":
            self.compile_package(node, depth, path)
        elif kind == "parallelGroup":
            self.compile_scope(node, depth, path)
        elif kind == "waitForSound":
            self.compile_watch(node, depth, path)
        else:
            raise AbvmError("unsupported ABVM step: " + kind)

    def compile_loop(self, node: dict[str, Any], depth: int,
                     path: tuple[int, ...]) -> None:
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
        self.compile_nodes(children(node), depth + 1, path)
        self.emit(OP_LOOP_NEXT, b=body)
        self.patch(enter, d=len(self.code))

    def compile_package(self, node: dict[str, Any], depth: int,
                        path: tuple[int, ...]) -> None:
        p = props(node)
        mode_text = str(p.get("mode") or "seq").lower()
        mode = 2 if mode_text in ("randomsubset", "pick") else \
            1 if mode_text in ("shuffleall", "all") else 0
        lo, hi = ordered(p.get("minCount"), p.get("maxCount"), 1, 1)
        enter = self.emit(OP_RPKG_ENTER, flags=mode, b=max(0, lo), c=max(0, hi))
        ranges = []
        for index, child in enumerate(children(node)):
            if disabled(child) or step_type(child) == "comment":
                continue
            start = len(self.code)
            self.compile_nodes([child], depth + 1, path + (index,))
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

    def compile_scope(self, node: dict[str, Any], depth: int,
                      path: tuple[int, ...]) -> None:
        if self.scope_depth:
            raise AbvmError("nested Parallel Group is forbidden")
        lanes = self.split_lanes(children(node))
        if len(lanes) != 2:
            raise AbvmError("phase-0 Parallel Group requires exactly two lanes")
        terminals = [i for i, lane in enumerate(lanes)
                     if any(self.contains(item, "waitForSound") for item in lane)]
        if len(terminals) != 1:
            raise AbvmError("Parallel Group requires one terminal Sound lane")
        terminal = terminals[0]
        self.flags |= FLAG_HAS_SCOPE
        self.max_lanes = 2
        policy = SCOPE_CANCEL_ON_TERMINAL_LANE
        begin = self.emit(OP_SCOPE_BEGIN, flags=policy, a=2)
        ranges = []
        self.scope_depth += 1
        try:
            for index, lane in enumerate(lanes):
                start = len(self.code)
                self.compile_nodes(lane, depth + 1, path + (index,))
                end = len(self.code)
                self.emit(OP_LANE_END, flags=int(index == terminal))
                ranges.append((start, end))
        finally:
            self.scope_depth -= 1
        raw = struct.pack("<BBBB", 2, policy, terminal, 0) + b"".join(
            struct.pack("<II", start, end) for start, end in ranges)
        self.patch(begin, a=self.pool.add(CONST_SCOPE, raw), d=len(self.code))

    def compile_watch(self, node: dict[str, Any], depth: int,
                      path: tuple[int, ...]) -> None:
        p = props(node)
        profile = integer(p.get("calibrationId"))
        lo = integer(p.get("timeoutMinSec")) * 1000
        hi = integer(p.get("timeoutMaxSec")) * 1000
        if not lo and not hi:
            lo = hi = integer(p.get("timeoutMs"))
        lo, hi = ordered(lo, hi)
        if profile <= 0 or hi <= 0:
            raise AbvmError("Wait For Sound needs calibrationId and timeout")
        self.sound_profiles.add(profile)
        self.flags |= FLAG_HAS_SOUND
        watch = self.emit(OP_WATCH, flags=1, a=profile, b=lo, c=hi)
        self.compile_nodes(children(node), depth + 1, path)
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
        payload_sizes = [len(payload) for _, _, payload in self.pool.items]
        type_sizes = [len(payload) for kind, _, payload in self.pool.items
                      if kind == CONST_TYPE]
        mouse_sizes = [len(payload) for kind, _, payload in self.pool.items
                       if kind == CONST_MOUSE]
        resources = ResourceCertificate(
            max_frames=self.max_frames,
            max_lanes=self.max_lanes,
            max_actors=max(1, self.max_lanes),
            max_events=2 if self.flags & FLAG_HAS_SOUND else 1,
            max_interrupts=int(any(
                (route.flags & ROUTE_POLICY_MASK) ==
                ROUTE_INTERRUPT_AND_RESUME for route in self.routes)),
            sound_profiles=len(self.sound_profiles),
            sound_listeners=1 if self.sound_profiles else 0,
            pwm_channels=0,
            max_const_bytes=max(payload_sizes, default=0),
            max_type_bytes=max(type_sizes, default=0),
            max_mouse_bytes=max(mouse_sizes, default=0),
            capabilities=self.flags,
        )
        resource_bytes = resources.pack()
        code_off = HEADER.size
        const_off = code_off + len(code)
        route_off = const_off + len(constants)
        resource_off = route_off + len(routes)
        file_size = resource_off + len(resource_bytes)
        source_digest = hashlib.sha256(json.dumps(
            source, ensure_ascii=False, sort_keys=True, separators=(",", ":")
        ).encode()).digest()
        fields = [MAGIC, FORMAT_VERSION, VM_ABI, self.flags, file_size,
                  HEADER.size, code_off, len(self.code), const_off,
                  len(constants), route_off, len(self.routes), resource_off,
                  len(resource_bytes), 0, self.max_frames, self.max_lanes,
                  source_digest, bytes(32), 0]
        payload = code + constants + routes + resource_bytes
        canonical = HEADER.pack(*fields) + payload
        program_digest = hashlib.sha256(canonical).digest()
        fields[18] = program_digest
        image_without_crc = HEADER.pack(*fields) + payload
        fields[14] = zlib.crc32(image_without_crc) & 0xFFFFFFFF
        image = HEADER.pack(*fields) + payload
        source_map = {
            "format": "ABP1-MAP",
            "formatVersion": 1,
            "sourceSha256": source_digest.hex(),
            "programSha256": program_digest.hex(),
            "entries": [
                {"pc": pc, **entry}
                for pc, entry in sorted(self.source_entries.items())
            ],
        }
        result = Program(image, self.code, self.pool.items, self.routes,
                         self.max_frames, self.max_lanes, self.flags, resources,
                         source_map, source_digest.hex(), program_digest.hex())
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
        if fields[5] != HEADER.size:
            raise AbvmError("ABP1 header size mismatch")
        expected = fields[14]
        fields[14] = 0
        if zlib.crc32(HEADER.pack(*fields) + data[HEADER.size:]) & 0xFFFFFFFF != expected:
            raise AbvmError("ABP1 CRC mismatch")
        program_digest = fields[18]
        fields[18] = bytes(32)
        if hashlib.sha256(HEADER.pack(*fields) + data[HEADER.size:]).digest() != program_digest:
            raise AbvmError("ABP1 program SHA-256 mismatch")
        self.data, self.flags = data, fields[3]
        self.code_off, self.code_count = fields[6], fields[7]
        self.const_off, self.const_size = fields[8], fields[9]
        self.route_off, self.route_count = fields[10], fields[11]
        self.resource_off, self.resource_size = fields[12], fields[13]
        self.max_frames, self.max_lanes = fields[15], fields[16]
        self.source_sha256 = fields[17].hex()
        self.program_sha256 = program_digest.hex()
        code_end = self.code_off + self.code_count * INSTRUCTION.size
        route_end = self.route_off + self.route_count * ROUTE.size
        if self.code_off != HEADER.size or code_end > len(data) or \
                self.const_off != code_end or \
                self.route_off != self.const_off + self.const_size or \
                self.resource_off != route_end or \
                self.resource_off + self.resource_size != len(data):
            raise AbvmError("non-canonical or out-of-range ABP1 sections")
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
            if cursor > end:
                raise AbvmError("constant alignment exceeds section")
        self.routes = [
            RouteInfo(*ROUTE.unpack_from(data, self.route_off + i * ROUTE.size))
            for i in range(self.route_count)
        ]
        if self.resource_size != RESOURCE.size:
            raise AbvmError("unsupported resource certificate size")
        resource_fields = RESOURCE.unpack_from(data, self.resource_off)
        if resource_fields[:2] != (1, RESOURCE.size):
            raise AbvmError("unsupported resource certificate")
        self.resources = ResourceCertificate(*resource_fields[2:])

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
        resource = image.resources
        if resource.max_frames != image.max_frames or \
                resource.max_lanes != image.max_lanes:
            raise AbvmError("resource certificate/header limit mismatch")
        limits = (
            (resource.max_actors, MAX_ACTORS, "actors"),
            (resource.max_events, MAX_EVENTS, "events"),
            (resource.max_interrupts, MAX_INTERRUPTS, "interrupts"),
            (resource.sound_profiles, MAX_SOUND_PROFILES, "sound profiles"),
            (resource.sound_listeners, MAX_SOUND_LISTENERS, "sound listeners"),
            (resource.pwm_channels, MAX_PWM_CHANNELS, "PWM channels"),
        )
        for declared, maximum, label in limits:
            if declared > maximum:
                raise AbvmError(f"resource certificate exceeds {label} contract")
        if resource.capabilities != image.flags:
            raise AbvmError("resource capability/header mismatch")
        code_end = image.code_off + image.code_count * INSTRUCTION.size
        if image.code_off != HEADER.size or code_end > len(data):
            raise AbvmError("invalid code bounds")
        if image.const_off != code_end or \
                image.route_off != image.const_off + image.const_size:
            raise AbvmError("non-canonical ABP1 section layout")
        if image.resource_off != image.route_off + image.route_count * ROUTE.size or \
                image.resource_off + image.resource_size != len(data):
            raise AbvmError("invalid route table bounds")
        measured = 0
        measured_profiles: set[int] = set()
        measured_lanes = 1
        measured_flags = 0

        def walk(start: int, end: int, depth: int, watch_depth: int = 0,
                 scope_depth: int = 0) -> None:
            nonlocal measured, measured_lanes, measured_flags
            measured = max(measured, depth)
            if depth > MAX_FRAMES:
                raise AbvmError("verified frame depth exceeds contract")
            pc = start
            while pc < end:
                if not 0 <= pc < len(image.instructions):
                    raise AbvmError("PC out of range")
                ins = image.instructions[pc]
                if ins.op == OP_DELAY and ins.b > ins.c:
                    raise AbvmError("invalid Delay range")
                if ins.op == OP_KEY and not 1 <= ins.flags <= 4:
                    raise AbvmError("invalid KEY width")
                if ins.op == OP_LOOP_ENTER:
                    if ins.flags not in (0, 1):
                        raise AbvmError("invalid Loop mode")
                    if not pc + 1 < ins.d <= end or \
                            image.instructions[ins.d - 1].op != OP_LOOP_NEXT:
                        raise AbvmError("invalid loop bounds")
                    walk(pc + 1, ins.d - 1, depth + 1,
                         watch_depth, scope_depth)
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
                        walk(first, last, depth + 1,
                             watch_depth, scope_depth)
                    pc = ins.d
                    continue
                if ins.op == OP_SCOPE_BEGIN:
                    if scope_depth:
                        raise AbvmError("nested Scope is forbidden")
                    measured_flags |= FLAG_HAS_SCOPE
                    raw = image.const(ins.a, CONST_SCOPE)
                    lanes, policy, terminal, _ = struct.unpack_from("<BBBB", raw)
                    if not 2 <= lanes <= MAX_LANES or \
                            policy not in SCOPE_POLICIES or \
                            (terminal != 0xFF and terminal >= lanes) or \
                            len(raw) != 4 + lanes * 8:
                        raise AbvmError("invalid Scope descriptor")
                    if ins.flags != policy or ins.a >= len(image.constants):
                        raise AbvmError("Scope instruction/descriptor mismatch")
                    if policy == SCOPE_CANCEL_ON_TERMINAL_LANE and \
                            terminal >= lanes:
                        raise AbvmError("Scope terminal lane is missing")
                    measured_lanes = max(measured_lanes, lanes)
                    for i in range(lanes):
                        first, last = struct.unpack_from("<II", raw, 4 + i * 8)
                        if not pc < first <= last < ins.d:
                            raise AbvmError("invalid Scope lane bounds")
                        lane_end = image.instructions[last]
                        if lane_end.op != OP_LANE_END or \
                                bool(lane_end.flags & 1) != \
                                (terminal != 0xFF and i == terminal):
                            raise AbvmError("invalid Scope terminal lane")
                        walk(first, last, depth + 1,
                             watch_depth, scope_depth + 1)
                    pc = ins.d
                    continue
                if ins.op == OP_WATCH:
                    measured_flags |= FLAG_HAS_SOUND
                    if watch_depth:
                        raise AbvmError("nested Watch is forbidden")
                    if not pc < ins.d <= end or ins.b > ins.c or not ins.a:
                        raise AbvmError("invalid Watch response bounds")
                    measured_profiles.add(ins.a)
                    walk(pc + 1, ins.d, depth + 1,
                         watch_depth + 1, scope_depth)
                    pc = ins.d
                    continue
                if ins.op == OP_TYPE:
                    measured_flags |= FLAG_HAS_TYPE
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

        route_ids: set[int] = set()
        route_ranges: list[tuple[int, int]] = []
        measured_interrupts = 0
        for route in image.routes:
            if route.route_id in route_ids:
                raise AbvmError("duplicate route id")
            route_ids.add(route.route_id)
            if route.flags & ~ROUTE_ALLOWED_FLAGS:
                raise AbvmError("unknown route flags")
            policy = route.flags & ROUTE_POLICY_MASK
            if policy not in ROUTE_POLICIES:
                raise AbvmError("unknown route transition policy")
            if not route.flags & ROUTE_PAUSE_RELEASE_HID:
                raise AbvmError("route must release HID on Pause")
            if policy == ROUTE_INTERRUPT_AND_RESUME:
                measured_interrupts += 1
            image.const(route.name_const, CONST_UTF8)
            if route.length <= 0 or route.pc + route.length > len(image.instructions):
                raise AbvmError("route bounds invalid")
            if image.instructions[route.pc + route.length - 1].op != OP_END:
                raise AbvmError("route does not end with END")
            current_range = (route.pc, route.pc + route.length)
            if any(current_range[0] < previous[1] and
                   previous[0] < current_range[1]
                   for previous in route_ranges):
                raise AbvmError("overlapping route ranges")
            route_ranges.append(current_range)
            walk(route.pc, route.pc + route.length, 0)
        if measured != image.max_frames:
            raise AbvmError(
                f"declared frame depth {image.max_frames} != verified {measured}")
        if measured_lanes != image.max_lanes:
            raise AbvmError(
                f"declared lanes {image.max_lanes} != verified {measured_lanes}")
        if len(measured_profiles) != resource.sound_profiles:
            raise AbvmError("resource sound-profile count mismatch")
        if resource.sound_listeners != int(bool(measured_profiles)):
            raise AbvmError("resource sound-listener count mismatch")
        if resource.max_actors < measured_lanes:
            raise AbvmError("resource actor count is too small")
        if resource.max_interrupts != int(bool(measured_interrupts)):
            raise AbvmError("resource interrupt count mismatch")
        if measured_flags != image.flags:
            raise AbvmError("header capabilities do not match bytecode")
        payload_sizes = [len(payload) for _, _, payload in image.constants]
        type_sizes = [len(payload) for kind, _, payload in image.constants
                      if kind == CONST_TYPE]
        mouse_sizes = [len(payload) for kind, _, payload in image.constants
                       if kind == CONST_MOUSE]
        if resource.max_const_bytes != max(payload_sizes, default=0) or \
                resource.max_type_bytes != max(type_sizes, default=0) or \
                resource.max_mouse_bytes != max(mouse_sizes, default=0):
            raise AbvmError("resource constant-size certificate mismatch")
        return image


@dataclass
class Lane:
    pc: int
    end: int
    due: int = 0
    frames: list[dict[str, Any]] | None = None
    active: bool = True
    group: dict[str, Any] | None = None
    terminal: bool = False

    def __post_init__(self) -> None:
        if self.frames is None:
            self.frames = []


@dataclass
class VmContext:
    route: RouteInfo
    route_name: str
    lanes: list[Lane]


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
        self.paused = False
        self.paused_at: int | None = None
        self.pressed_keys: set[int] = set()
        self.lanes: list[Lane] = []
        self.current_route: RouteInfo | None = None
        self.current_route_name = ""
        self.suspended: list[VmContext] = []

    def start(self, route_name: str, clear_events: bool = True) -> None:
        route = self.image.route(route_name)
        if clear_events:
            self.events.clear()
        self.release_hid("start")
        for lane in self.lanes:
            lane.active = False
        for context in self.suspended:
            for lane in context.lanes:
                lane.active = False
        self.lanes = [Lane(route.pc, route.pc + route.length)]
        self.current_route = route
        self.current_route_name = route_name
        self.suspended.clear()
        self.running = True
        self.paused = False
        self.paused_at = None
        self.events.append(("ROUTE_START", route_name, self.now))

    def run(self, route_name: str,
            max_fetches: int = 100_000) -> list[tuple[Any, ...]]:
        self.start(route_name)
        self.run_until_idle(max_fetches)
        return self.events

    def run_until_idle(self, max_fetches: int = 100_000) -> None:
        fetches = 0
        while self.step_next():
            fetches += 1
            if fetches > max_fetches:
                raise AbvmError("reference VM fetch budget exhausted")
        if self.paused:
            raise AbvmError("reference VM is paused")

    def step_next(self) -> bool:
        if not self.running or self.paused:
            return False
        active = [lane for lane in self.lanes if lane.active]
        if not active:
            if self.suspended:
                finished = self.current_route_name
                context = self.suspended.pop()
                self.current_route = context.route
                self.current_route_name = context.route_name
                self.lanes = context.lanes
                self.events.append((
                    "INTERRUPT_RESUME", finished,
                    self.current_route_name, self.now))
                return True
            self.running = False
            self.events.append(("ROUTE_COMPLETE", self.current_route_name, self.now))
            return False
        self.now = max(self.now, min(lane.due for lane in active))
        lane = next(item for item in active if item.due <= self.now)
        self.step(lane)
        return True

    def advance(self, milliseconds: int) -> None:
        if milliseconds < 0:
            raise AbvmError("clock cannot move backwards")
        self.now += milliseconds
        self.events.append(("CLOCK_ADVANCE", milliseconds, self.now))

    def release_hid(self, reason: str) -> None:
        released = tuple(sorted(self.pressed_keys))
        self.pressed_keys.clear()
        self.events.append(("HID_RELEASE_ALL", reason, released, self.now))

    @staticmethod
    def shift_context_deadlines(lanes: list[Lane], delta: int) -> None:
        for lane in lanes:
            lane.due += delta
            for frame in lane.frames or []:
                if frame.get("deadline") is not None:
                    frame["deadline"] += delta

    def pause(self) -> bool:
        if not self.running or self.paused:
            return False
        if self.current_route is None or \
                not self.current_route.flags & ROUTE_PAUSE_RELEASE_HID:
            raise AbvmError("current route has no safe Pause policy")
        self.release_hid("pause")
        self.paused = True
        self.paused_at = self.now
        self.events.append((
            "PAUSE", self.current_route_name, self.now,
            tuple(lane.pc for lane in self.lanes if lane.active)))
        return True

    def resume(self) -> bool:
        if not self.running or not self.paused:
            return False
        paused_at = self.paused_at if self.paused_at is not None else self.now
        elapsed = self.now - paused_at
        if self.current_route is not None and \
                (self.current_route.flags & ROUTE_CLOCK_MASK) == ROUTE_CLOCK_ACTIVE:
            self.shift_context_deadlines(self.lanes, elapsed)
        self.paused = False
        self.paused_at = None
        self.events.append((
            "RESUME", self.current_route_name, self.now, elapsed,
            tuple(lane.pc for lane in self.lanes if lane.active)))
        return True

    def interrupt(self, route_name: str) -> None:
        if not self.running or self.paused:
            raise AbvmError("interrupt requires a running VM")
        if self.suspended:
            raise AbvmError("nested interrupt exceeds firmware contract")
        route = self.image.route(route_name)
        if (route.flags & ROUTE_POLICY_MASK) != ROUTE_INTERRUPT_AND_RESUME:
            raise AbvmError("route is not an interrupt-and-resume route")
        if self.current_route is None:
            raise AbvmError("current route is missing")
        self.release_hid("interrupt")
        self.suspended.append(VmContext(
            self.current_route, self.current_route_name, self.lanes))
        previous = self.current_route_name
        self.current_route = route
        self.current_route_name = route_name
        self.lanes = [Lane(route.pc, route.pc + route.length, self.now)]
        self.events.append(("INTERRUPT_START", previous, route_name, self.now))

    def stop(self) -> None:
        self.release_hid("stop")
        self.running = False
        self.paused = False
        self.paused_at = None
        for lane in self.lanes:
            lane.active = False
        for context in self.suspended:
            for lane in context.lanes:
                lane.active = False
        self.suspended.clear()
        self.events.append(("STOP", self.current_route_name, self.now))

    def finish_lane(self, lane: Lane) -> None:
        lane.active = False
        if lane.group is None:
            return
        group = lane.group
        policy = group["policy"]
        resume = policy == SCOPE_CANCEL_ON_ANY or \
            (policy == SCOPE_CANCEL_ON_TERMINAL_LANE and lane.terminal)
        if resume:
            for sibling in group["children"]:
                sibling.active = False
            parent = group["parent"]
            parent.active, parent.due = True, self.now
            self.events.append(("SCOPE_RESUME", SCOPE_POLICIES[policy], self.now))
        elif policy == SCOPE_JOIN_ALL and \
                not any(child.active for child in group["children"]):
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
            if ins.op == OP_KDOWN:
                self.pressed_keys.add(ins.a)
            else:
                self.pressed_keys.discard(ins.a)
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
        elif ins.op == OP_SCOPE_BEGIN:
            raw = self.image.const(ins.a, CONST_SCOPE)
            count, policy, terminal, _ = struct.unpack_from("<BBBB", raw)
            parent = lane
            parent.pc, parent.active = ins.d, False
            group: dict[str, Any] = {
                "parent": parent, "children": [], "policy": policy,
            }
            for i in range(count):
                start, end = struct.unpack_from("<II", raw, 4 + i * 8)
                child = Lane(start, end, self.now, group=group,
                             terminal=i == terminal)
                group["children"].append(child)
                self.lanes.append(child)
            self.events.append(
                ("SCOPE_BEGIN", count, SCOPE_POLICIES[policy], terminal))
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


def compile_file(source: Path, target: Path, routes: list[str],
                 map_target: Path | None = None) -> Program:
    data = json.loads(source.read_text(encoding="utf-8-sig"))
    program = Compiler().compile_amsj(data, routes)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(program.image)
    if map_target is None:
        map_target = Path(str(target) + ".map.json")
    map_target.parent.mkdir(parents=True, exist_ok=True)
    map_target.write_text(
        json.dumps(program.source_map, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    return program


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="abvm")
    commands = parser.add_subparsers(dest="command", required=True)
    build = commands.add_parser("compile")
    build.add_argument("source", type=Path)
    build.add_argument("target", type=Path)
    build.add_argument("--routes", nargs="+", default=["Game", "Whisper"])
    build.add_argument("--map", dest="map_target", type=Path)
    verify = commands.add_parser("verify")
    verify.add_argument("program", type=Path)
    trace = commands.add_parser("trace")
    trace.add_argument("program", type=Path)
    trace.add_argument("--route", default="Game")
    trace.add_argument("--detected", type=int, nargs="*", default=[])
    abi = commands.add_parser("abi")
    abi.add_argument("--output", type=Path)
    args = parser.parse_args(argv)
    try:
        if args.command == "compile":
            result = compile_file(
                args.source, args.target, args.routes, args.map_target)
            print(json.dumps({
                "bytes": len(result.image), "instructions": len(result.instructions),
                "constants": len(result.constants), "routes": len(result.routes),
                "maxFrames": result.max_frames, "maxLanes": result.max_lanes,
                "programSha256": result.program_sha256,
                "sourceMap": str(args.map_target or
                                 Path(str(args.target) + ".map.json")),
            }, sort_keys=True))
        elif args.command == "verify":
            image = Verifier.verify(args.program.read_bytes())
            print(json.dumps({
                "bytes": len(image.data), "instructions": image.code_count,
                "constants": len(image.constants), "routes": image.route_count,
                "maxFrames": image.max_frames, "maxLanes": image.max_lanes,
                "programSha256": image.program_sha256,
                "resources": image.resources.__dict__,
            }, sort_keys=True))
        elif args.command == "trace":
            vm = ReferenceVm(args.program.read_bytes(),
                             detected_profiles=args.detected)
            for event in vm.run(args.route):
                print(json.dumps(event, ensure_ascii=False))
        else:
            rendered = json.dumps(
                abi_registry(), ensure_ascii=False, indent=2, sort_keys=True
            ) + "\n"
            if args.output:
                args.output.parent.mkdir(parents=True, exist_ok=True)
                args.output.write_text(rendered, encoding="utf-8")
            else:
                print(rendered, end="")
        return 0
    except (AbvmError, OSError, json.JSONDecodeError) as exc:
        print("ABVM error:", exc, file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())