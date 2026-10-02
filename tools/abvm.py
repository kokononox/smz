

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
MAX_PACKAGE_ITEMS = 32
MAX_ACTORS, MAX_EVENTS, MAX_INTERRUPTS = 4, 4, 1
MAX_SOUND_PROFILES, MAX_SOUND_LISTENERS, MAX_PWM_CHANNELS = 8, 1, 1
# 128 bytes: identity/version, section directory, CRC, limits, source and
# program SHA-256.  Program SHA is calculated with its own field and CRC zero.
HEADER = struct.Struct("<4sHHIIIIIIIIIIIIHH32s32sI")
INSTRUCTION = struct.Struct("<BBHIII")           # 16 bytes
ROUTE = struct.Struct("<HHIII")                  # 16 bytes
CONST_HEADER = struct.Struct("<BBHI")
RESOURCE = struct.Struct("<HHHHHHHHHHIIII")      # 36 bytes
SOUND = struct.Struct("<HHI")                      # profile, threshold, minimum ms
LIGHT = struct.Struct("<IIIB3x")                    # low/high lux, stable ms, mode
GUARD_HEADER = struct.Struct("<BBBBI")               # version/count/mode/reserved/timeout
GUARD_PROFILE_V1 = struct.Struct("<BBHIIII")          # id/enabled/route/low/high/stable/hysteresis
GUARD_PROFILE = struct.Struct("<BBHIIIII")            # id/cue/route/low/high/stable/hysteresis/cooldown
GUARD_CUE_META = struct.Struct("<BBBB")                # custom count/volume/envelope/reserved
GUARD_CUE_TONE = struct.Struct("<HHH")                 # frequency/duration/gap
GUARD_CUSTOM_TONES = 8
BUZZER_SYSTEM_CUE_COUNT = 23
CYCLE = struct.Struct("<BBBBIIHHI")                  # version/flags/limit/reserved/run range/routes/USB stable

FLAG_HAS_TYPE, FLAG_HAS_SCOPE, FLAG_HAS_SOUND, FLAG_HAS_LIGHT, FLAG_HAS_GUARD, FLAG_HAS_CYCLE = 1, 2, 4, 8, 16, 32
CONST_UTF8, CONST_TYPE, CONST_MOUSE, CONST_RANGES, CONST_SCOPE, CONST_SOUND, CONST_LIGHT, CONST_GUARD, CONST_CYCLE = range(1, 10)
OP_END, OP_DELAY, OP_KEY, OP_KDOWN, OP_KUP, OP_TYPE, OP_RMOUSE, OP_BEEP = range(8)
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
    "Desktop": 1, "Launch": 2, "Restart": 2, "Startup": 3,
    "LoginOrDc": 4, "LaunchRecovery": 5, "Dc": 5,
    "CharacterDashboard": 6,
    "EnteringGameLoading": 7, "Game": 8, "Targeted": 9,
    "Whisper": 10, "Splash": 11, "WhisperRepeat": 12,
}

ROUTE_POLICY_BY_NAME = {
    "Desktop": ROUTE_ABORT_AND_START,
    "Launch": ROUTE_ABORT_AND_START,
    "Restart": ROUTE_ABORT_AND_START,
    "Startup": ROUTE_ABORT_AND_START,
    "LoginOrDc": ROUTE_ABORT_AND_START,
    "LaunchRecovery": ROUTE_ABORT_AND_START,
    "Dc": ROUTE_ABORT_AND_START,
    "CharacterDashboard": ROUTE_ABORT_AND_START,
    "EnteringGameLoading": ROUTE_ABORT_AND_START,
    "Game": ROUTE_ABORT_AND_RESTART,
    "Targeted": ROUTE_INTERRUPT_AND_RESUME,
    "Whisper": ROUTE_INTERRUPT_AND_RESUME,
    "WhisperRepeat": ROUTE_INTERRUPT_AND_RESUME,
    "Splash": ROUTE_CANCEL_SCOPE_AND_CONTINUE,
}

# Real-time routes keep absolute deadlines while paused and while an interrupt
# route executes.  ACTIVE is reserved for workflows whose timers must freeze.
ROUTE_CLOCK_BY_NAME = {name: ROUTE_CLOCK_WALL for name in ROUTE_IDS}
LEGACY_ROUTE_ALIASES = {"Restart": "Launch", "Dc": "LaunchRecovery"}

OPCODES = {
    "END": OP_END, "DELAY": OP_DELAY, "KEY": OP_KEY,
    "KDOWN": OP_KDOWN, "KUP": OP_KUP, "TYPE": OP_TYPE,
    "RMOUSE": OP_RMOUSE, "BEEP": OP_BEEP, "LOOP_ENTER": OP_LOOP_ENTER,
    "LOOP_NEXT": OP_LOOP_NEXT, "RPKG_ENTER": OP_RPKG_ENTER,
    "ITEM_END": OP_ITEM_END, "SCOPE_BEGIN": OP_SCOPE_BEGIN,
    "LANE_END": OP_LANE_END, "WATCH": OP_WATCH, "JUMP": OP_JUMP,
}

CONSTANT_KINDS = {
    "UTF8": CONST_UTF8, "TYPE": CONST_TYPE, "MOUSE": CONST_MOUSE,
    "RANGES": CONST_RANGES, "SCOPE": CONST_SCOPE, "SOUND": CONST_SOUND,
    "LIGHT": CONST_LIGHT, "GUARD": CONST_GUARD, "CYCLE": CONST_CYCLE,
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
            "packageItems": MAX_PACKAGE_ITEMS,
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
        "NUMPADMULTIPLY": 106, "MULTIPLY": 106,
        "NUMPADADD": 107, "ADD": 107,
        "NUMPADSUBTRACT": 109, "SUBTRACT": 109,
        "NUMPADDECIMAL": 110, "DECIMAL": 110,
        "NUMPADDIVIDE": 111, "DIVIDE": 111,
    }
    if text in aliases:
        return aliases[text]
    if text.startswith("NUMPAD") and text[6:].isdigit() and \
            0 <= int(text[6:]) <= 9:
        return 96 + int(text[6:])
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
        self.uses_pwm = False
        self.source_entries: dict[int, dict[str, Any]] = {}
        self.current_route = ""
        self.scope_depth = 0
        self.labels: dict[str, int] = {}
        self.gotos: list[tuple[int, str]] = []
        self.human_mouse_profile: dict[str, int] = {}

    @staticmethod
    def compact_human_mouse_profile(source: dict[str, Any]) -> dict[str, int]:
        profile = source.get("humanMouseProfile") or {}
        encoded = str(profile.get("EncodedSample") or
                      profile.get("encodedSample") or "")
        duration = integer(profile.get("DurationMs") or
                           profile.get("durationMs"))
        if not profile:
            return {}
        if duration < 30_000 or not encoded.startswith("v1|"):
            raise AbvmError(
                "Native Export needs a valid 30-second human mouse profile")
        fields = encoded.split("|", 4)
        if len(fields) != 5:
            raise AbvmError("human mouse profile payload is malformed")
        delays: list[int] = []
        speeds: list[float] = []
        for token in fields[4].split(";"):
            try:
                dt_text, dx_text, dy_text = token.split(",")
                dt, dx, dy = int(dt_text), int(dx_text), int(dy_text)
            except (ValueError, TypeError):
                raise AbvmError("human mouse profile segment is malformed")
            if not 1 <= dt <= 60_000 or abs(dx) > 8192 or abs(dy) > 8192:
                raise AbvmError("human mouse profile segment is out of range")
            if dx or dy:
                delays.append(dt)
                if dt >= 4:
                    speeds.append(((dx * dx + dy * dy) ** .5) * 1000.0 / dt)
        if len(delays) < 20 or len(speeds) < 5:
            raise AbvmError("human mouse profile has too little movement")
        delays.sort(); speeds.sort()
        percentile = lambda values, p: values[(len(values) - 1) * p // 100]
        signature = zlib.crc32(encoded.encode()) & 0xffff
        return {
            "handSignature": signature,
            "handTempoMs": max(2, min(20, int(percentile(delays, 50)))),
            "handSpeedMin": max(150, min(3000,
                int(round(percentile(speeds, 20))))),
            "handSpeedMax": max(150, min(3000,
                int(round(percentile(speeds, 80))))),
        }

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

    def compile_global_whisper(self, source: dict[str, Any]) -> None:
        """Persist enabled game-wide Whisper classifiers in the image.

        The native sound actor still owns only one physical listener.  A scoped
        Catch watch therefore supplies the samples, while Pico 1 compares each
        detected peak with this descriptor and interrupts route 10 when the
        Whisper range wins.  Keeping this descriptor first also lets firmware
        discover it without changing ABI-1.
        """
        profiles = source.get("soundProfiles") or source.get("SoundProfiles") or []
        if not isinstance(profiles, list):
            raise AbvmError("soundProfiles must be a list")
        compiled = []
        expected = {
            1: ("Whisper", "whisper", 9, "9"),
            3: ("WhisperRepeat", "whisperrepeat", 11, "11"),
        }
        for item in profiles:
            if not isinstance(item, dict):
                continue
            profile = integer(item.get("Id", item.get("id")), 0)
            enabled = bool(item.get("Enabled", item.get("enabled", False)))
            response = item.get("ResponseTab", item.get("responseTab"))
            if profile not in expected or not enabled or \
                    response not in expected[profile]:
                continue
            threshold = integer(
                item.get("PeakMin", item.get("peakMin")), 0)
            maximum = integer(
                item.get("PeakMax", item.get("peakMax")), 1023)
            minimum = max(1, integer(
                item.get("MinDurationMs", item.get("minDurationMs")), 60))
            cooldown = integer(
                item.get("CooldownMs", item.get("cooldownMs")), 1800)
            if (threshold < 0 or threshold > 1023 or maximum < threshold or
                    maximum > 1023 or minimum > 65_535 or
                    cooldown < 0 or cooldown > 60_000):
                raise AbvmError(
                    "global Whisper range, duration, or cooldown is out of range")
            compiled.append((profile, threshold, maximum, minimum, cooldown))
        compiled.sort()
        for index, left in enumerate(compiled):
            for right in compiled[index + 1:]:
                if max(left[1], right[1]) <= min(left[2], right[2]):
                    raise AbvmError(
                        "global Whisper sound ranges overlap: "
                        f"ID {left[0]} and ID {right[0]}")
        for profile, threshold, maximum, minimum, cooldown in compiled:
            # Bits 16..25 carry the upper edge. Bits 26..31 carry cooldown
            # seconds (0..60). Legacy descriptors used only a <=1023 upper
            # word, so this remains backwards-compatible with ABI-1 images.
            cooldown_seconds = (cooldown + 999) // 1000
            self.pool.add(
                CONST_SOUND,
                SOUND.pack(profile, threshold, minimum |
                           (maximum << 16) | (cooldown_seconds << 26)))

    def compile_amsj(self, source: dict[str, Any],
                     route_names: Iterable[str] = ("Game", "Whisper")) -> Program:
        pipelines = source.get("pipelines") or source.get("Pipelines")
        if not isinstance(pipelines, dict):
            raise AbvmError("AMSJ pipelines object is missing")
        self.human_mouse_profile = self.compact_human_mouse_profile(source)
        self.compile_global_whisper(source)
        for name in route_names:
            nodes = pipelines.get(name)
            if nodes is None:
                nodes = pipelines.get(LEGACY_ROUTE_ALIASES.get(name, ""))
            if nodes is None:
                continue
            if not isinstance(nodes, list):
                raise AbvmError("pipeline is not a list: " + name)
            start = len(self.code)
            self.current_route = name
            self.labels = {}
            self.gotos = []
            self.compile_nodes(nodes, 0, ())
            for pc, label in self.gotos:
                if label not in self.labels:
                    raise AbvmError(f"Go To Label target is missing in {name}: {label}")
                self.patch(pc, d=self.labels[label])
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
        self.compile_guard(source)
        self.compile_cycle(source)
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
        if kind == "label":
            label = str(p.get("label") or "").strip()
            if not label or label in self.labels:
                raise AbvmError("empty or duplicate Label: " + label)
            self.labels[label] = len(self.code)
        elif kind == "gotoLabel":
            label = str(p.get("label") or "").strip()
            if not label:
                raise AbvmError("Go To Label has no target")
            self.gotos.append((self.emit(OP_JUMP), label))
        elif kind == "delay":
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
            spec = dict(p)
            spec.update(self.human_mouse_profile)
            intent = str(spec.get("motionIntent") or "targetRegion")
            if intent in ("microTwitch", "mediumTwitch"):
                defaults = (2, 12) if intent == "microTwitch" else (20, 80)
                lo, hi = ordered(
                    spec.get("twitchMinPx"), spec.get("twitchMaxPx"),
                    defaults[0], defaults[1])
                spec["relativeMode"] = 1
                spec["relativeMin"] = max(1, lo)
                spec["relativeMax"] = max(1, hi)
            self.emit(OP_RMOUSE, a=self.pool.obj(CONST_MOUSE, spec))
        elif kind == "buzzer":
            self.compile_buzzer(p)
        elif kind == "forLoop":
            self.compile_loop(node, depth, path)
        elif kind == "randomPackage":
            self.compile_package(node, depth, path)
        elif kind == "parallelGroup":
            self.compile_scope(node, depth, path)
        elif kind == "waitForSound":
            self.compile_watch(node, depth, path)
        elif kind == "waitForLight":
            self.compile_light_watch(node, depth, path)
        else:
            raise AbvmError("unsupported ABVM step: " + kind)

    def compile_buzzer(self, p: dict[str, Any]) -> None:
        preset = str(p.get("preset") or "short").strip().lower()
        patterns = {
            "short": "1000:180",
            "double": "1000:140,100;1000:140",
            "notification": "880:110,45;1175:170",
            "warning": "700:180,90;700:180,90;700:300",
            "success": "900:120,70;1300:220",
            "error": "440:180,70;330:240",
            "rising": "523:90,35;659:90,35;784:160",
            "falling": "784:90,35;659:90,35;523:160",
        }
        if preset == "custom":
            pattern = str(p.get("pattern") or "900:150")
        elif preset in patterns:
            pattern = patterns[preset]
        else:
            raise AbvmError("unknown buzzer preset: " + preset)
        tones = [part.strip() for part in pattern.split(";") if part.strip()]
        if not tones:
            raise AbvmError("buzzer pattern is empty")
        volume = integer(p.get("volume"), 100)
        if not 1 <= volume <= 100:
            raise AbvmError("buzzer volume must be 1..100 percent")
        tempo = integer(p.get("tempo"), 100)
        if not 25 <= tempo <= 400:
            raise AbvmError("buzzer note speed must be 25..400 percent")
        envelope_name = str(p.get("envelope") or "sharp").strip().lower()
        envelopes = {"sharp": 0, "smooth": 1, "fade-in": 2, "fade-out": 3}
        if envelope_name not in envelopes:
            raise AbvmError("unknown buzzer envelope: " + envelope_name)
        tone_style = volume | (envelopes[envelope_name] << 8)
        for tone in tones:
            try:
                frequency_text, timing_text = tone.split(":", 1)
                timing = [part.strip() for part in timing_text.split(",")]
                if len(timing) not in (1, 2):
                    raise ValueError
                frequency = int(frequency_text.strip())
                duration = int(timing[0])
                pause = int(timing[1]) if len(timing) == 2 else 0
            except (TypeError, ValueError):
                raise AbvmError(
                    "buzzer pattern must be freq:duration,pause;... (Hz/ms)")
            if not 30 <= frequency <= 20000:
                raise AbvmError("buzzer frequency must be 30..20000 Hz")
            if not 1 <= duration <= 60000 or not 0 <= pause <= 60000:
                raise AbvmError(
                    "buzzer duration must be 1..60000 ms and pause 0..60000 ms")
            duration = max(1, (duration * 100 + tempo // 2) // tempo)
            if pause:
                pause = max(1, (pause * 100 + tempo // 2) // tempo)
            self.emit(OP_BEEP, a=frequency, b=duration, c=tone_style)
            if pause:
                self.emit(OP_DELAY, b=pause, c=pause)
        self.uses_pwm = True

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
            if disabled(child) or step_type(child) in ("comment", "label"):
                continue
            start = len(self.code)
            self.compile_nodes([child], depth + 1, path + (index,))
            end = len(self.code)
            self.emit(OP_ITEM_END)
            ranges.append((start, end))
        if not ranges:
            raise AbvmError("Random Package has no executable items")
        if len(ranges) > MAX_PACKAGE_ITEMS:
            raise AbvmError(
                f"Random Package exceeds {MAX_PACKAGE_ITEMS} items")
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
                     if any(self.contains(item, "waitForSound") or
                            self.contains(item, "waitForLight") for item in lane)]
        if len(terminals) > 1:
            raise AbvmError(
                "Parallel Group supports at most one terminal Watch lane")
        terminal = terminals[0] if terminals else 0xFF
        self.flags |= FLAG_HAS_SCOPE
        self.max_lanes = 2
        policy = SCOPE_CANCEL_ON_TERMINAL_LANE if terminals else SCOPE_JOIN_ALL
        begin = self.emit(OP_SCOPE_BEGIN, flags=policy, a=2)
        ranges = []
        self.scope_depth += 1
        try:
            for index, lane in enumerate(lanes):
                start = len(self.code)
                self.compile_nodes(lane, depth + 1, path + (index,))
                end = len(self.code)
                self.emit(OP_LANE_END, flags=int(
                    terminal != 0xFF and index == terminal))
                ranges.append((start, end))
        finally:
            self.scope_depth -= 1
        raw = struct.pack("<BBBB", 2, policy, terminal, 0) + b"".join(
            struct.pack("<II", start, end) for start, end in ranges)
        self.patch(begin, a=self.pool.add(CONST_SCOPE, raw), d=len(self.code))

    def compile_watch(self, node: dict[str, Any], depth: int,
                      path: tuple[int, ...]) -> None:
        p = props(node)
        profile = integer(p.get("calibrationId"), 1)
        lo = integer(p.get("timeoutMinSec")) * 1000
        hi = integer(p.get("timeoutMaxSec")) * 1000
        if not lo and not hi:
            lo = hi = integer(p.get("timeoutMs"))
        lo, hi = ordered(lo, hi)
        if profile <= 0 or hi <= 0:
            raise AbvmError("Wait For Sound needs calibrationId and timeout")
        # `threshold` is the detector threshold edited by Classroom Calibrate.
        # peakMin/peakMax are legacy classification metadata and must not
        # silently override it. Zero explicitly selects saved physical
        # calibration for this profile in the Pico actor.
        threshold = integer(p.get("threshold"), 60)
        minimum = max(1, integer(p.get("minDurationMs"), 60))
        if threshold < 0 or threshold > 1023 or minimum > 65_535:
            raise AbvmError("Wait For Sound threshold or duration is out of range")
        descriptor = SOUND.pack(profile, threshold, minimum)
        self.sound_profiles.add(profile)
        self.flags |= FLAG_HAS_SOUND
        watch = self.emit(OP_WATCH, flags=2,
                          a=self.pool.add(CONST_SOUND, descriptor), b=lo, c=hi)
        # Detection response order is deliberate: execute the Catch children
        # first (normally the F keystroke), then play the configured feedback.
        # A timeout jumps to watch.d and skips both the response and the cue.
        self.compile_nodes(children(node), depth + 1, path)
        catch_preset = str(p.get("armCuePreset") or "off").strip().lower()
        if catch_preset != "off":
            self.compile_buzzer({
                "preset": catch_preset,
                "volume": integer(p.get("armCueVolume"), 60),
                "envelope": str(p.get("armCueEnvelope") or "smooth"),
                "tempo": integer(p.get("armCueTempo"), 100),
                "pattern": str(p.get("armCuePattern") or
                               "880:100,40;1175:150"),
            })
        self.patch(watch, d=len(self.code))

    def compile_light_watch(self, node: dict[str, Any], depth: int,
                            path: tuple[int, ...]) -> None:
        p = props(node)
        center = max(0, integer(p.get("luxCenter"), 1250))
        tolerance = max(1, integer(p.get("luxTolerance"), 50))
        low, high = max(0, center - tolerance), center + tolerance
        stable = max(0, round(float(p.get("stableSec") or 2) * 1000))
        timeout = integer(p.get("timeoutMs"), 20_000)
        mode = 1 if str(p.get("sampleMode") or "hires").lower() == "lowres" else 0
        if high > 1_000_000 or stable > 3_600_000 or timeout <= 0:
            raise AbvmError("Wait For Light range, stability, or timeout is out of range")
        descriptor = LIGHT.pack(low, high, stable, mode)
        self.flags |= FLAG_HAS_LIGHT
        watch = self.emit(OP_WATCH, flags=3,
                          a=self.pool.add(CONST_LIGHT, descriptor),
                          b=timeout, c=timeout)
        self.compile_nodes(children(node), depth + 1, path)
        if not children(node) and bool(p.get("armed")) and not bool(p.get("insertIfElse")):
            react_lo, react_hi = ordered(p.get("reactMin"), p.get("reactMax"), 80, 180)
            hold_lo, hold_hi = ordered(p.get("holdMin"), p.get("holdMax"), 30, 90)
            self.emit(OP_DELAY, b=max(0, react_lo), c=max(0, react_hi))
            self.emit(OP_KEY, flags=1, b=vk(p.get("key") or "E"),
                      c=max(0, hold_lo), d=max(0, hold_hi))
        self.patch(watch, d=len(self.code))

    def compile_guard(self, source: dict[str, Any]) -> None:
        guard = source.get("nativeGuard")
        if not isinstance(guard, dict) or not guard.get("enabled", False):
            return
        expected = {
            "desktop": (1, "Desktop"),
            "login-or-dc": (2, "LoginOrDc"),
            "character-dashboard": (3, "CharacterDashboard"),
            "entering-game-loading": (4, "EnteringGameLoading"),
            "game": (5, "Game"),
            "targeted": (6, "Targeted"),
            "whisper": (7, "Whisper"),
            "whisper-repeat": (8, "WhisperRepeat"),
        }
        profiles = guard.get("profiles")
        if not isinstance(profiles, list) or len(profiles) != len(expected):
            raise AbvmError("Native Guard needs exactly eight profiles")
        by_id = {str(item.get("id") or ""): item for item in profiles
                 if isinstance(item, dict)}
        if set(by_id) != set(expected):
            raise AbvmError("Native Guard profile IDs are incomplete or duplicated")
        compiled_routes = {route.route_id for route in self.routes}
        required_routes = {ROUTE_IDS[name] for _, name in expected.values()} | {ROUTE_IDS["Dc"]}
        if not required_routes.issubset(compiled_routes):
            missing = sorted(required_routes - compiled_routes)
            raise AbvmError("Native Guard routes are missing: " + ",".join(map(str, missing)))
        watchdog_minutes = integer(guard.get("stageWatchdogMinutes"), 2)
        if watchdog_minutes not in range(1, 61):
            raise AbvmError("Native Guard stage watchdog must be 1..60 minutes")
        buzzer_cues = guard.get("buzzerCues")
        has_buzzer_cues = isinstance(buzzer_cues, list)
        packed = bytearray(GUARD_HEADER.pack(
            5 if has_buzzer_cues else 4, len(expected),
            1 if str(guard.get("sampleMode") or "hires").lower() == "lowres" else 0,
            watchdog_minutes,
            max(250, integer(guard.get("sensorTimeoutMs"), 1500))))
        ranges = []
        for profile_id, (numeric_id, route_name) in expected.items():
            item = by_id[profile_id]
            if not item.get("enabled", True):
                raise AbvmError("Native Guard profile is disabled: " + profile_id)
            center = float(item.get("luxCenter", item.get("center", 0)))
            tolerance = float(item.get("luxTolerance", item.get("tolerance", 0)))
            hysteresis = float(item.get("hysteresisLux", item.get("hysteresis", 1)))
            stable = integer(item.get("stableDurationMs", item.get("stableMs", 750)))
            cooldown = integer(item.get("lightCooldownMs", 0))
            cue = integer(item.get("calibrationCue"), numeric_id)
            if not all(value >= 0 for value in (center, tolerance, hysteresis, stable, cooldown)):
                raise AbvmError("Native Guard profile has a negative value: " + profile_id)
            low = max(0, int(round((center - tolerance) * 10)))
            high = int(round((center + tolerance) * 10))
            if high > 10_000_000 or stable > 3_600_000 or cooldown > 3_600_000:
                raise AbvmError("Native Guard profile is out of range: " + profile_id)
            if cue not in range(0, 101):
                raise AbvmError("Native Guard calibration cue is out of range: " + profile_id)
            if numeric_id not in (7, 8) and cooldown:
                raise AbvmError("Native Guard light cooldown is only valid for Whisper profiles")
            ranges.append((profile_id, low, high))
            packed.extend(GUARD_PROFILE.pack(
                numeric_id, cue, ROUTE_IDS[route_name], low, high, stable,
                int(round(hysteresis * 10)), cooldown))
            custom = []
            volume = integer(item.get("calibrationCueVolume"), 100)
            tempo = integer(item.get("calibrationCueTempo"), 100)
            envelope_name = str(item.get("calibrationCueEnvelope") or "sharp").lower()
            envelopes = {"sharp": 0, "smooth": 1, "fade-in": 2, "fade-out": 3}
            if volume not in range(1, 101) or tempo not in range(25, 401) or envelope_name not in envelopes:
                raise AbvmError("Native Guard calibration cue style is invalid: " + profile_id)
            pattern_text = str(item.get("calibrationCuePattern") or "")
            # Classroom Studio resolves presets to their concrete pattern.
            # Persist that pattern even when cue keeps its preset ID so tempo
            # can be compiled once and playback stays fully board-local.
            if cue == 0 or pattern_text.strip():
                for token in pattern_text.split(";"):
                    token = token.strip()
                    if not token:
                        continue
                    try:
                        frequency_text, timing_text = token.split(":", 1)
                        timing = [part.strip() for part in timing_text.split(",")]
                        if len(timing) not in (1, 2):
                            raise ValueError()
                        frequency = int(frequency_text)
                        duration = int(timing[0])
                        gap = int(timing[1]) if len(timing) == 2 else 0
                    except (ValueError, TypeError):
                        raise AbvmError("Native Guard custom cue syntax is invalid: " + profile_id)
                    if frequency not in range(30, 20001) or duration <= 0 or gap < 0:
                        raise AbvmError("Native Guard custom cue note is out of range: " + profile_id)
                    duration = max(1, round(duration * 100 / tempo))
                    gap = max(1, round(gap * 100 / tempo)) if gap else 0
                    if duration > 65535 or gap > 65535:
                        raise AbvmError("Native Guard custom cue timing is too long: " + profile_id)
                    custom.append((frequency, duration, gap))
                if not custom or len(custom) > GUARD_CUSTOM_TONES:
                    raise AbvmError("Native Guard custom cue needs 1..8 notes: " + profile_id)
            packed.extend(GUARD_CUE_META.pack(
                len(custom), volume, envelopes[envelope_name], 0))
            for index in range(GUARD_CUSTOM_TONES):
                packed.extend(GUARD_CUE_TONE.pack(*(custom[index] if index < len(custom) else (0, 0, 0))))
        for i, (left_id, left_low, left_high) in enumerate(ranges):
            for right_id, right_low, right_high in ranges[i + 1:]:
                if max(left_low, right_low) <= min(left_high, right_high):
                    raise AbvmError(f"Native Guard profiles overlap: {left_id}/{right_id}")
        if has_buzzer_cues:
            by_cue_id = {integer(item.get("id"), 0): item for item in buzzer_cues
                         if isinstance(item, dict)}
            if set(by_cue_id) != set(range(1, BUZZER_SYSTEM_CUE_COUNT + 1)):
                raise AbvmError("Native buzzer settings need exactly 23 unique system cues")
            packed.extend(GUARD_CUE_META.pack(BUZZER_SYSTEM_CUE_COUNT, 0, 0, 0))
            envelopes = {"sharp": 0, "smooth": 1, "fade-in": 2, "fade-out": 3}
            for cue_id in range(1, BUZZER_SYSTEM_CUE_COUNT + 1):
                item = by_cue_id[cue_id]
                volume = integer(item.get("volume"), 100)
                tempo = integer(item.get("tempo"), 100)
                envelope_name = str(item.get("envelope") or "sharp").lower()
                if volume not in range(1, 101) or tempo not in range(25, 401) or envelope_name not in envelopes:
                    raise AbvmError(f"Native system buzzer style is invalid: {cue_id}")
                notes = []
                for token in str(item.get("pattern") or "").split(";"):
                    token = token.strip()
                    if not token:
                        continue
                    try:
                        frequency_text, timing_text = token.split(":", 1)
                        timing = [part.strip() for part in timing_text.split(",")]
                        if len(timing) not in (1, 2):
                            raise ValueError()
                        frequency = int(frequency_text)
                        duration = int(timing[0])
                        gap = int(timing[1]) if len(timing) == 2 else 0
                    except (ValueError, TypeError):
                        raise AbvmError(f"Native system buzzer syntax is invalid: {cue_id}")
                    duration = max(1, round(duration * 100 / tempo))
                    gap = max(1, round(gap * 100 / tempo)) if gap else 0
                    if frequency not in range(30, 20001) or duration not in range(1, 65536) or gap not in range(0, 65536):
                        raise AbvmError(f"Native system buzzer note is out of range: {cue_id}")
                    notes.append((frequency, duration, gap))
                if not notes or len(notes) > GUARD_CUSTOM_TONES:
                    raise AbvmError(f"Native system buzzer cue needs 1..8 notes: {cue_id}")
                packed.extend(GUARD_CUE_META.pack(cue_id, len(notes), volume, envelopes[envelope_name]))
                for index in range(GUARD_CUSTOM_TONES):
                    packed.extend(GUARD_CUE_TONE.pack(*(notes[index] if index < len(notes) else (0, 0, 0))))
        self.pool.add(CONST_GUARD, bytes(packed))
        self.flags |= FLAG_HAS_GUARD

    def compile_cycle(self, source: dict[str, Any]) -> None:
        cycle = source.get("nativeCycle")
        if not isinstance(cycle, dict) or not cycle.get("enabled", False):
            return
        compiled_routes = {route.route_id for route in self.routes}
        after_route = ROUTE_IDS["Restart"]
        startup_route = ROUTE_IDS["Startup"]
        if not {after_route, startup_route}.issubset(compiled_routes):
            raise AbvmError("Native Cycle needs Restart/After and Startup routes")
        run_min = integer(cycle.get("runMinSeconds"), 110 * 60)
        run_max = integer(cycle.get("runMaxSeconds"), 130 * 60)
        if run_max < run_min:
            run_min, run_max = run_max, run_min
        max_restarts = integer(cycle.get("maxRestarts"), 5)
        usb_stable = integer(cycle.get("usbStableMs"), 2000)
        if run_min <= 0 or run_max > 7 * 24 * 60 * 60:
            raise AbvmError("Native Cycle run range must be 1 second..7 days")
        if not 1 <= max_restarts <= 32:
            raise AbvmError("Native Cycle restart limit must be 1..32")
        if not 250 <= usb_stable <= 300_000:
            raise AbvmError("Native Cycle USB stable window must be 250..300000 ms")
        self.pool.add(CONST_CYCLE, CYCLE.pack(
            1, 1 if cycle.get("autoResume", True) else 0,
            max_restarts, 0, run_min * 1000, run_max * 1000,
            after_route, startup_route, usb_stable))
        self.flags |= FLAG_HAS_CYCLE

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
            max_events=2 if self.flags & (FLAG_HAS_SOUND | FLAG_HAS_LIGHT) else 1,
            max_interrupts=int(any(
                (route.flags & ROUTE_POLICY_MASK) ==
                ROUTE_INTERRUPT_AND_RESUME for route in self.routes)),
            sound_profiles=len(self.sound_profiles),
            sound_listeners=1 if self.sound_profiles else 0,
            pwm_channels=1 if self.uses_pwm else 0,
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
        guard_constants = [payload for kind, _, payload in image.constants
                           if kind == CONST_GUARD]
        if guard_constants:
            if len(guard_constants) != 1:
                raise AbvmError("multiple Native Guard descriptors")
            raw = guard_constants[0]
            version, count, mode, reserved, timeout = GUARD_HEADER.unpack_from(raw)
            profile_struct = GUARD_PROFILE if version in (2, 3, 4, 5) else GUARD_PROFILE_V1
            profile_size = profile_struct.size + (GUARD_CUE_META.size + GUARD_CUSTOM_TONES * GUARD_CUE_TONE.size if version in (4, 5) else 0)
            base_size = GUARD_HEADER.size + 8 * profile_size
            expected_size = base_size + (GUARD_CUE_META.size + BUZZER_SYSTEM_CUE_COUNT * (GUARD_CUE_META.size + GUARD_CUSTOM_TONES * GUARD_CUE_TONE.size) if version == 5 else 0)
            if len(raw) != expected_size:
                raise AbvmError("invalid Native Guard descriptor size")
            if version not in (1, 2, 3, 4, 5) or count != 8 or mode not in (0, 1) or \
                    (version < 3 and reserved) or \
                    (version >= 3 and reserved not in range(1, 61)) or timeout < 250:
                raise AbvmError("invalid Native Guard descriptor header")
            ids = set()
            for index in range(count):
                item = profile_struct.unpack_from(
                    raw, GUARD_HEADER.size + index * profile_size)
                if version in (2, 3, 4, 5):
                    profile_id, cue, route_id, low, high, stable, hysteresis, cooldown = item
                else:
                    profile_id, enabled, route_id, low, high, stable, hysteresis = item
                    cue, cooldown = profile_id, 0
                    if enabled != 1:
                        raise AbvmError("invalid Native Guard profile")
                if profile_id not in range(1, 9) or profile_id in ids or \
                        cue not in range(0 if version >= 4 else 1, 101 if version >= 4 else 9) or route_id not in ROUTE_IDS.values() or \
                        low > high or stable > 3_600_000 or \
                        hysteresis > 1_000_000 or cooldown > 3_600_000 or \
                        (profile_id not in (7, 8) and cooldown):
                    raise AbvmError("invalid Native Guard profile")
                if version in (4, 5):
                    custom_offset = GUARD_HEADER.size + index * profile_size + GUARD_PROFILE.size
                    custom_count, volume, envelope, cue_reserved = GUARD_CUE_META.unpack_from(raw, custom_offset)
                    if custom_count > GUARD_CUSTOM_TONES or volume not in range(1, 101) or envelope > 3 or cue_reserved:
                        raise AbvmError("invalid Native Guard custom cue metadata")
                    if cue == 0 and custom_count == 0:
                        raise AbvmError("invalid Native Guard custom cue selection")
                    for tone_index in range(custom_count):
                        frequency, duration, gap = GUARD_CUE_TONE.unpack_from(
                            raw, custom_offset + GUARD_CUE_META.size + tone_index * GUARD_CUE_TONE.size)
                        if frequency not in range(30, 20001) or not duration:
                            raise AbvmError("invalid Native Guard custom cue tone")
                ids.add(profile_id)
            if ids != set(range(1, 9)):
                raise AbvmError("Native Guard profile set mismatch")
            if version == 5:
                cue_count, r1, r2, r3 = GUARD_CUE_META.unpack_from(raw, base_size)
                if cue_count != BUZZER_SYSTEM_CUE_COUNT or r1 or r2 or r3:
                    raise AbvmError("invalid Native system buzzer header")
                record_size = GUARD_CUE_META.size + GUARD_CUSTOM_TONES * GUARD_CUE_TONE.size
                cue_ids = set()
                for index in range(cue_count):
                    offset = base_size + GUARD_CUE_META.size + index * record_size
                    cue_id, note_count, volume, envelope = GUARD_CUE_META.unpack_from(raw, offset)
                    if cue_id not in range(1, BUZZER_SYSTEM_CUE_COUNT + 1) or cue_id in cue_ids or \
                            note_count not in range(1, GUARD_CUSTOM_TONES + 1) or \
                            volume not in range(1, 101) or envelope > 3:
                        raise AbvmError("invalid Native system buzzer cue")
                    for tone_index in range(note_count):
                        frequency, duration, gap = GUARD_CUE_TONE.unpack_from(
                            raw, offset + GUARD_CUE_META.size + tone_index * GUARD_CUE_TONE.size)
                        if frequency not in range(30, 20001) or not duration:
                            raise AbvmError("invalid Native system buzzer tone")
                    cue_ids.add(cue_id)
                if cue_ids != set(range(1, BUZZER_SYSTEM_CUE_COUNT + 1)):
                    raise AbvmError("Native system buzzer cue set mismatch")
            measured_flags |= FLAG_HAS_GUARD
        cycle_constants = [payload for kind, _, payload in image.constants
                           if kind == CONST_CYCLE]
        if cycle_constants:
            if len(cycle_constants) != 1:
                raise AbvmError("multiple Native Cycle descriptors")
            raw = cycle_constants[0]
            if len(raw) != CYCLE.size:
                raise AbvmError("invalid Native Cycle descriptor size")
            version, flags, limit, reserved, run_min, run_max, after, startup, stable = CYCLE.unpack(raw)
            compiled_routes = {route.route_id for route in image.routes}
            if version != 1 or flags & ~1 or not 1 <= limit <= 32 or reserved or \
                    not run_min or run_min > run_max or run_max > 7 * 24 * 60 * 60 * 1000 or \
                    after not in compiled_routes or startup not in compiled_routes or \
                    not 250 <= stable <= 300_000:
                raise AbvmError("invalid Native Cycle descriptor")
            measured_flags |= FLAG_HAS_CYCLE

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
                if ins.op == OP_BEEP:
                    volume, envelope = ins.c & 0xFF, (ins.c >> 8) & 0xFF
                    style_ok = ins.c == 0 or (
                        1 <= volume <= 100 and envelope <= 3 and ins.c < 0x10000)
                    if (not 30 <= ins.a <= 20000 or
                            not 1 <= ins.b <= 60000 or
                            ins.flags or not style_ok or ins.d):
                        raise AbvmError("invalid BEEP operands")
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
                    if not count or count > MAX_PACKAGE_ITEMS or \
                            len(raw) != 2 + count * 8:
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
                    if watch_depth:
                        raise AbvmError("nested Watch is forbidden")
                    if not pc < ins.d <= end or ins.b > ins.c:
                        raise AbvmError("invalid Watch response bounds")
                    if ins.flags == 3:
                        measured_flags |= FLAG_HAS_LIGHT
                        raw = image.const(ins.a, CONST_LIGHT)
                        if len(raw) != LIGHT.size:
                            raise AbvmError("invalid Light descriptor size")
                        low, high, stable, mode = LIGHT.unpack(raw)
                        if low > high or high > 1_000_000 or stable > 3_600_000 or mode not in (0, 1):
                            raise AbvmError("invalid Light descriptor")
                    elif ins.flags == 2:
                        measured_flags |= FLAG_HAS_SOUND
                        raw = image.const(ins.a, CONST_SOUND)
                        if len(raw) != SOUND.size:
                            raise AbvmError("invalid Sound descriptor size")
                        profile, threshold, minimum = SOUND.unpack(raw)
                        if not profile or not 0 <= threshold <= 1023 or not minimum:
                            raise AbvmError("invalid Sound descriptor")
                        measured_profiles.add(profile)
                    elif ins.flags == 1 and ins.a:
                        measured_flags |= FLAG_HAS_SOUND
                        measured_profiles.add(ins.a)
                    else:
                        raise AbvmError("unknown Watch descriptor")
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
                    OP_BEEP, OP_LOOP_NEXT, OP_ITEM_END, OP_LANE_END,
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
        measured_pwm = int(any(
            ins.op == OP_BEEP for ins in image.instructions))
        if resource.pwm_channels != measured_pwm:
            raise AbvmError("resource PWM-channel count mismatch")
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
                 detected_profiles: Iterable[int] = (),
                 light_detected: bool = False) -> None:
        self.image = Verifier.verify(data)
        self.rng = random.Random(seed)
        self.detected = set(detected_profiles)
        self.light_detected = light_detected
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
        elif ins.op == OP_BEEP:
            self.events.append(("BEEP", ins.a, ins.b))
            lane.pc += 1
            lane.due = self.now + ins.b
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
            if ins.flags == 3:
                low, high, stable, mode = LIGHT.unpack(
                    self.image.const(ins.a, CONST_LIGHT))
                profile = (low, high, stable, mode)
                hit = self.light_detected
                kind = "LIGHT"
            else:
                if ins.flags == 2:
                    profile, _, _ = SOUND.unpack(self.image.const(ins.a, CONST_SOUND))
                else:
                    profile = ins.a
                hit = profile in self.detected
                kind = "SOUND"
            if kind == "LIGHT":
                self.events.append(("WATCH", kind, profile,
                                    "detected" if hit else "timeout"))
            else:
                self.events.append(("WATCH", profile,
                                    "detected" if hit else "timeout"))
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