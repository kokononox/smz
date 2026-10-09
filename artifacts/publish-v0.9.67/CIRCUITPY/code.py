# CI rerun marker: repository transfer validation
# Combined Phase 7 firmware entry point for the regular Raspberry Pi Pico.
# Parse the Guard bundle on a fresh heap, provide CircuitPython compatibility
# shims, and keep all Guard routes on the bounded streaming executor.
import gc
import sys
import os as _real_os
import hashlib as _real_hashlib
try:
    import microcontroller as _microcontroller
except Exception:
    _microcontroller = None

class _PathCompat:
    @staticmethod
    def join(root, name):
        if not root or root == "/":
            return "/" + name.lstrip("/")
        return root.rstrip("/") + "/" + name.lstrip("/")

    @staticmethod
    def isfile(path):
        try:
            return (_real_os.stat(path)[0] & 0x4000) == 0
        except OSError:
            return False

class _OsCompat:
    path = _PathCompat()

    def __getattr__(self, name):
        return getattr(_real_os, name)

if not hasattr(_real_os, "path"):
    sys.modules["os"] = _OsCompat()

class _Sha256Compat:
    def __init__(self, data=None):
        self.hash = _real_hashlib.new("sha256")
        if data:
            self.hash.update(data)

    def update(self, data):
        self.hash.update(data)

    def digest(self):
        return self.hash.digest()

    def hexdigest(self):
        return "".join("%02x" % byte for byte in self.hash.digest())

class _HashlibCompat:
    def sha256(self, data=None):
        return _Sha256Compat(data)

    def __getattr__(self, name):
        return getattr(_real_hashlib, name)

if not hasattr(_real_hashlib, "sha256"):
    if not hasattr(_real_hashlib, "new"):
        raise RuntimeError("CircuitPython hashlib has no SHA-256 implementation")
    sys.modules["hashlib"] = _HashlibCompat()

# plan_engine is intentionally absent from Combined Guard.

# Import the hardware runtime before the verifier and bundle load. The
# verifier is bound after the large runtime module is resident so the RP2040
# keeps a contiguous heap block during boot.
gc.collect()
import combined_guard_runtime as runtime
from combined_guard_runtime import main
runtime.plan_engine = sys.modules["plan_engine"]

gc.collect()
import live_light_guard as _guard_bundle
from guard_calibration_protocol import build_calibration_get, parse_calibration_set
runtime.HASHED_BUNDLE_FILES = _guard_bundle.HASHED_BUNDLE_FILES
runtime.GuardBundleError = _guard_bundle.GuardBundleError
runtime.LightStateGuard = _guard_bundle.LightStateGuard
runtime._file_sha256 = _guard_bundle._file_sha256
runtime.load_guard_bundle = _guard_bundle.load_guard_bundle
runtime.build_calibration_get = build_calibration_get
runtime.parse_calibration_set = parse_calibration_set

# Classroom Studio's complete 21-file export hashes every payload except the
# hash manifest itself. Extend the verifier inventory before loading the bundle.
for _name in ("pico-calibration.json", "README-FLASH.md"):
    if _name not in _guard_bundle.HASHED_BUNDLE_FILES:
        _guard_bundle.HASHED_BUNDLE_FILES += (_name,)
runtime.HASHED_BUNDLE_FILES = _guard_bundle.HASHED_BUNDLE_FILES
_BOOT_BUNDLE = _guard_bundle.load_guard_bundle("/")

def _compact_boot_bundle(bundle):
    manifest = bundle["manifest"]
    calibration = bundle["calibration"]
    profiles = []
    for item in manifest.get("profiles", ()):
        profiles.append({
            "id": item["id"],
            "center": item["center"],
            "tolerance": item["tolerance"],
            "stableMs": item["stableMs"],
        })
    calibration_profiles = {}
    for profile_id, item in calibration.get("profiles", {}).items():
        calibration_profiles[profile_id] = {
            "center": item["center"],
            "tolerance": item["tolerance"],
            "stable_ms": item["stable_ms"],
        }
    return {
        "manifest": {
            "format": manifest.get("format"),
            "runtime": manifest.get("runtime"),
            "calibrationRevision": manifest.get("calibrationRevision"),
            "routes": dict(manifest.get("routes", {})),
            "profiles": profiles,
        },
        "calibration": {
            "format": calibration.get("format"),
            "revision": calibration.get("revision"),
            "profiles": calibration_profiles,
        },
        "revision": bundle["revision"],
        "states": bundle["states"],
        "stable_ms": bundle["stable_ms"],
        "hysteresis": bundle["hysteresis"],
        "sensor_timeout_ms": bundle["sensor_timeout_ms"],
    }

_BOOT_BUNDLE = _compact_boot_bundle(_BOOT_BUNDLE)
del _name, _guard_bundle
gc.collect()


_DEBUG_FILE = "/guard-debug.log"
_DEBUG_MAX_BYTES = 8192
_DEBUG_MAX_LINES = 96
_DEBUG_NVM_BYTES = 1536
_DEBUG_PERSIST_EVENTS = ("BOOT", "GP4", "ROUTE", "FAIL", "STOP", "CAL")


def _debug_trim(text):
    lines = text.splitlines()[-_DEBUG_MAX_LINES:]
    text = "\n".join(lines) + ("\n" if lines else "")
    if len(text) > _DEBUG_MAX_BYTES:
        text = text[-_DEBUG_MAX_BYTES:]
        if "\n" in text:
            text = text[text.index("\n") + 1:]
    return text


def _debug_nvm_read():
    try:
        nvm = getattr(_microcontroller, "nvm", None)
        if nvm is None:
            return ""
        raw = bytes(nvm[:_DEBUG_NVM_BYTES]).rstrip(b"\x00")
        if not raw.startswith(b"CGD1\n"):
            return ""
        return raw[5:].decode("utf-8", "replace")
    except Exception:
        return ""


def _debug_nvm_write(text):
    try:
        nvm = getattr(_microcontroller, "nvm", None)
        if nvm is None:
            return False
        payload = (b"CGD1\n" + _debug_trim(text).encode("utf-8", "replace"))[:_DEBUG_NVM_BYTES]
        padded = payload + (b"\x00" * (_DEBUG_NVM_BYTES - len(payload)))
        nvm[:_DEBUG_NVM_BYTES] = padded
        return bytes(nvm[:len(payload)]) == payload
    except Exception:
        return False


def _debug_file_read():
    try:
        with open(_DEBUG_FILE, "r") as fh:
            return fh.read()
    except Exception:
        return ""


def _debug_persist(self):
    try:
        # Re-open the filesystem for every persistence attempt. A host-side
        # delete/remount can leave CircuitPython with a stale read-only view;
        # doing this only after the NVM write was too late to recreate the file.
        try:
            remount = getattr(runtime.storage, "remount", None)
            if remount is not None:
                remount("/", readonly=False, disable_concurrent_write_protection=True)
        except Exception:
            pass
        # Merge the file and NVM journals. NVM survives reset; the file is the
        # user-visible copy and must be restored whenever it was deleted.
        file_text = _debug_file_read()
        nvm_text = _debug_nvm_read()
        previous = file_text or nvm_text
        payload = _debug_trim(previous + "".join(self.debug_events))
        file_ok = False
        try:
            with open(_DEBUG_FILE, "w") as fh:
                fh.write(payload)
                try: fh.flush()
                except Exception: pass
            file_ok = _debug_file_read() == payload
        except Exception:
            pass
        nvm_ok = _debug_nvm_write(payload)
        # Do not discard pending events merely because NVM succeeded: if the
        # visible file failed, the next boot/event must retry its reconstruction.
        if file_ok:
            self.debug_events = []
        return file_ok or nvm_ok
    except Exception:
        pass
    # Keep pending records for the next event/retry; never lose GP4/FAIL data.
    return False


def _debug_event(self, kind, detail="", persist=False):
    stamp = int(runtime.time.monotonic())
    clean = str(detail).replace("|", "/").replace("\n", " ")[:180]
    line = "%d|%s|%s\n" % (stamp, kind, clean)
    self.debug_events.append(line)
    if len(self.debug_events) > _DEBUG_MAX_LINES:
        self.debug_events = self.debug_events[-_DEBUG_MAX_LINES:]
    # Live evidence is available even when both persistent stores are busy.
    try:
        self.emit("EVT|DEBUG|" + line.rstrip("\n").replace("|", "/"))
    except Exception:
        pass
    if kind in ("BOOT", "FAIL"):
        _debug_persist(self)


def _debug_get(self):
    _debug_persist(self)
    return _debug_file_read() or _debug_nvm_read()


def _debug_exception(exc):
    # Some CircuitPython exceptions have an empty str(); preserve the type and
    # repr so a route failure is actionable instead of appearing as FAIL|guard=.
    kind = type(exc).__name__
    detail = repr(exc)
    if not detail or detail == "''":
        detail = "<empty>"
    return (kind + ":" + detail).replace("\n", " ")[:180]


def _debug_clear(self):
    self.debug_events = []
    _debug_nvm_write("")
    try:
        with open(_DEBUG_FILE, "w") as fh:
            fh.write("")
    except Exception:
        pass


def _debug_emit(self):
    text = _debug_get(self)
    for raw in text.splitlines():
        self.emit("EVT|DEBUG|" + raw.replace("|", "/"))

def _memory_safe_init(self):
    global _BOOT_BUNDLE
    self.debug_events = []
    bundle = _BOOT_BUNDLE
    _BOOT_BUNDLE = None
    self.bundle = bundle
    self.guard = runtime.LightStateGuard(
        bundle["states"], bundle["stable_ms"], bundle["hysteresis"],
        bundle["sensor_timeout_ms"])
    self.guard.bundle = bundle
    gc.collect()

    self.arm = runtime.Arm()
    self.keyboard = runtime.Keyboard(runtime.usb_hid.devices)
    self.controls = runtime.Controls(self.arm, self.keyboard)
    self.sensor = runtime.BH1750()
    self.routes = {}
    self.blue = runtime.Button(runtime.board.GP4)
    self.yellow = runtime.Button(runtime.board.GP3)
    self.usb = runtime.usb_cdc.data or runtime.usb_cdc.console
    self.host = bytearray()
    self.calibrating = False
    self.stage = 0
    self.samples = []
    self.sample_started = 0
    self.result = None
    self.saved = False
    self.saved_ids = set()
    self.blue_stop_consumed = False
    self.blue_start_consumed = False
    self.blue_start_pending = False
    self.last_cal_error = None
    self.debug_last_state = None
    self.restart_cycle_started = None
    self.restart_deadline = None
    self.restart_waiting = _restart_marker_exists()
    self.restart_down_seen = bool(self.restart_waiting)
    self.restart_up_since = None
    self.restart_route_pending = False
    _debug_event(self, "BOOT", "bundle=valid profiles=%d restart=%d" % (len(runtime.PROFILES), 1 if self.restart_waiting else 0), persist=True)

# The six calibration positions use distinct ascending notes: C4 through A4.
_CAL_NOTES = (262, 294, 330, 349, 392, 440)
# Board control cues are short rhythmic signatures instead of long continuous tones.
# Each pattern stays near one second and uses 3-6 notes so Start/Stop/Pause/Resume
# remain recognizable without sounding like a stuck alarm.
_GUARD_START_PATTERN = ((784, 160), (988, 160), (1175, 200), (0, 80), (1175, 280))
_GUARD_STOP_PATTERN = ((392, 180), (330, 160), (262, 260), (0, 60), (196, 260))
_GUARD_PAUSE_PATTERN = ((523, 180), (0, 100), (523, 180), (0, 100), (523, 340))
_GUARD_RESUME_PATTERN = ((659, 150), (784, 150), (988, 150), (784, 150), (988, 300))

def _cal_beep(self, frequency, duration_ms):
    tone = None
    try:
        tone = runtime.pwmio.PWMOut(runtime.board.GP6, duty_cycle=32768,
            frequency=int(frequency), variable_frequency=True)
        runtime.time.sleep(duration_ms / 1000)
    except Exception as exc:
        _debug_event(self, "CAL", "audio-failed=%s" % type(exc).__name__, persist=True)
        self.emit("ERR|CAL|AUDIO")
    finally:
        if tone is not None:
            try: tone.duty_cycle = 0; tone.deinit()
            except Exception: pass

def _cal_position_tone(self):
    self._cal_beep(_CAL_NOTES[self.stage], 220)

def _cal_record_start_tone(self):
    self._cal_beep(660, 65)

def _cal_stage_complete_tone(self):
    note = _CAL_NOTES[self.stage]
    self._cal_beep(note, 110)
    runtime.time.sleep(.06)
    self._cal_beep(note, 190)

def _cal_save_success_tone(self):
    self._cal_beep(880, 90)
    runtime.time.sleep(.05)
    self._cal_beep(1320, 180)

def _cal_complete_melody(self):
    for note in _CAL_NOTES:
        self._cal_beep(note, 90)
        runtime.time.sleep(.035)

def _guard_pattern(self, pattern):
    for frequency, duration_ms in pattern:
        if frequency <= 0:
            runtime.time.sleep(duration_ms / 1000)
        else:
            self._cal_beep(frequency, duration_ms)

def _guard_start_tone(self):
    _debug_event(self, "CAL", "start-cue", persist=True)
    self._guard_pattern(_GUARD_START_PATTERN)

def _guard_stop_tone(self):
    self._guard_pattern(_GUARD_STOP_PATTERN)

def _guard_pause_tone(self):
    self._guard_pattern(_GUARD_PAUSE_PATTERN)

def _guard_resume_tone(self):
    self._guard_pattern(_GUARD_RESUME_PATTERN)

def _immediate_audible_stop(self):
    # Make Stop audible immediately even when the optional Arduino arm is not
    # replying. Set the fail-safe state and release keyboard first, then play
    # the Stop cue before slower arm cleanup/timeout work.
    self.controls.running = False
    self.controls.paused = False
    self.controls.aborted = True
    try:
        self.keyboard.release_all()
    except Exception:
        pass
    self.guard_stop_tone()
    if getattr(self, "route_uses_mouse", False):
        self.arm.abort()

def _silent_shutdown(self):
    # Disconnect/shutdown must leave the board fail-safe without replaying the
    # user-facing Stop cue. Physical blue Stop and GUARD|OFF remain audible.
    self.controls.running = False
    self.controls.paused = False
    self.controls.aborted = True
    try:
        self.keyboard.release_all()
    except Exception:
        pass
    if getattr(self, "route_uses_mouse", False):
        try:
            self.arm.abort()
        except Exception:
            pass

def _immediate_audible_start(self):
    # Acknowledge Start on the physical press, not on release. Route execution
    # remains gated until the press resolves, so a held blue button can still
    # become the long-hold calibration gesture without executing a route.
    # Defer Start until release so a held press can become calibration.
    self.guard.reset()
    self.blue_start_pending = True
    self.blue_start_consumed = True

def _ensure_runtime_bundle(self):
    if self.bundle is None:
        self.bundle = runtime.load_guard_bundle("/")
        self.guard.bundle = self.bundle
        gc.collect()

def _enter_calibration_from_pending_start(self):
    _ensure_runtime_bundle(self)
    # The user held the same blue press that initially cued Start. Cancel the
    # not-yet-routable start state and enter calibration directly, without
    # playing a Stop cue or waiting on optional arm cleanup.
    self.blue_start_pending = False
    self.controls.running = False
    self.controls.paused = False
    self.controls.aborted = True
    try:
        self.keyboard.release_all()
    except Exception:
        pass
    self.calibrating = True
    self.stage = 0
    self.samples = []
    self.sample_started = 0
    self.result = None
    self.saved = False
    self.saved_ids = set()
    self.emit("EVT|CAL|mode=ready|stage=1|id=" + runtime.PROFILES[0] + "|seconds=5|saved=0")
    self.cal_position_tone()

_original_start_cal = runtime.Combined.start_cal
_original_next_cal = runtime.Combined.next_cal
_original_cal_tick = runtime.Combined.cal_tick
_original_save_cal = runtime.Combined.save_cal
_original_yellow_action = runtime.Combined.yellow_action

def _audible_start_cal(self):
    _ensure_runtime_bundle(self)
    _original_start_cal(self)
    if self.calibrating and self.stage == 0:
        self.cal_position_tone()

def _audible_next_cal(self):
    previous = self.stage
    can_wrap = (self.calibrating and
        self.stage == len(runtime.PROFILES) - 1 and
        self.result != "sampling" and
        not (self.result is not None and not self.saved))
    if can_wrap:
        self.stage = 0
        self.result = None
        self.saved = False
        self.emit("EVT|CAL|mode=ready|stage=1|id=" + runtime.PROFILES[0] +
            "|seconds=5|saved=%d" % len(self.saved_ids))
    else:
        _original_next_cal(self)
    if self.calibrating and self.stage != previous:
        self.cal_position_tone()

def _audible_cal_tick(self):
    was_sampling = self.result == "sampling"
    _original_cal_tick(self)
    if was_sampling and isinstance(self.result, dict):
        self.cal_stage_complete_tone()
        # Sampling completion is an implicit confirmation; save once.
        self.save_cal()

def _audible_save_cal(self):
    profile_was_saved = runtime.PROFILES[self.stage] in self.saved_ids
    had_pending_result = isinstance(self.result, dict) and not self.saved
    _original_save_cal(self)
    if had_pending_result and self.saved:
        if not profile_was_saved and len(self.saved_ids) == len(runtime.PROFILES):
            self.cal_complete_melody()
        else:
            self.cal_save_success_tone()

def _repeatable_yellow_action(self):
    # Handle both first samples and same-position retries explicitly. A saved
    # value remains active during a retry and is replaced only after the fresh
    # result completes and the user presses yellow again to save it.
    if not self.calibrating:
        was_paused = self.controls.paused
        _original_yellow_action(self)
        if self.controls.running and self.controls.paused != was_paused:
            self.guard_pause_tone() if self.controls.paused else self.guard_resume_tone()
        return
    if self.result == "sampling":
        _original_yellow_action(self)
        return
    if isinstance(self.result, dict) and not self.saved:
        self.save_cal()
        return
    retry = 1 if self.saved and isinstance(self.result, dict) else 0
    self.samples = []
    self.sample_started = runtime.time.monotonic()
    self.result = "sampling"
    self.emit("EVT|CAL|mode=started|stage=%d|id=%s|seconds=5|saved=%d|retry=%d" %
        (self.stage + 1, runtime.PROFILES[self.stage], len(self.saved_ids), retry))
    self.cal_record_start_tone()

def _audible_buttons(self):
    now = runtime.time.monotonic()
    blue = self.blue.poll(now)
    yellow = self.yellow.poll(now)
    if blue == "down":
        _debug_event(self, "GP4", "down running=%s calibrating=%s" % (self.controls.running, self.calibrating), persist=True)
    elif blue == "long":
        _debug_event(self, "GP4", "long calibrating=%s" % self.calibrating, persist=True)
    elif blue == "up":
        _debug_event(self, "GP4", "up", persist=True)
    if blue == "down" and not self.calibrating and self.controls.running:
        # Stop is fail-safe and should acknowledge immediately on press. This
        # consumes the blue press so the later release/long-hold path cannot
        # re-start the Guard or enter calibration accidentally.
        self.blue_stop_consumed = True
        self.immediate_audible_stop()
    elif blue == "down" and not self.calibrating and not self.controls.running:
        # Start must also acknowledge on the first physical press. Keep route
        # execution pending until release so the same press can still become
        # the long-hold calibration gesture safely.
        self.immediate_audible_start()
    elif blue == "long":
        if self.blue_stop_consumed:
            pass
        elif getattr(self, "blue_start_pending", False):
            self.blue_start_consumed = True
            self.enter_calibration_from_pending_start()
        else:
            self.end_cal() if self.calibrating else self.start_cal()
    elif blue == "up":
        stop_consumed = self.blue_stop_consumed
        start_consumed = getattr(self, "blue_start_consumed", False)
        self.blue_stop_consumed = False
        self.blue_start_consumed = False
        if getattr(self, "blue_start_pending", False):
            self.blue_start_pending = False
            # Short GP4 press: start and play the Start cue on release only.
            # Long GP4 press was consumed by the calibration branch above.
            if not stop_consumed and not self.blue.long:
                self.guard.reset()
                self.controls.start()
                self.guard_start_tone()
                self.bundle = None
                self.guard.bundle = None
                gc.collect()
                _debug_event(self, "GP4", "short-start running=1 free=%d" % gc.mem_free(), persist=True)
        elif not stop_consumed and not start_consumed and not self.blue.long:
            if self.calibrating:
                self.next_cal()
    if yellow == "up" and not self.yellow.long:
        self.yellow_action()
    self.cal_tick()


_original_plan_setres = runtime.PlanContext.setres
_original_plan_beep = runtime.PlanContext.beep

def _diagnostic_setres(ctx, w, h):
    _debug_event(ctx.r, "STEP", "SCREEN %dx%d -> SETRES" % (w, h), persist=True)
    return _original_plan_setres(ctx, w, h)

def _diagnostic_beep(ctx, frequency, duration):
    _debug_event(ctx.r, "STEP", "BEEP %d,%d" % (frequency, duration), persist=True)
    return _original_plan_beep(ctx, frequency, duration)

runtime.PlanContext.setres = _diagnostic_setres
runtime.PlanContext.beep = _diagnostic_beep

import random as _light_random

_LIGHT_ROUTE_COMMANDS = {"PLAN", "SCREEN", "SPEED", "BEEP", "DELAY", "LOOP", "LOOPTIME", "ENDLOOP", "KEY", "KDOWN", "KUP", "TYPE", "RMOUSE", "MOVETO"}
_VALID_ROUTE_NAMES = ("desktop_steps.txt", "restart_steps.txt", "login_or_dc_steps.txt", "character_dashboard_steps.txt", "entering_game_loading_steps.txt", "game_steps.txt", "targeted_steps.txt", "resumable_steps.txt")


def _light_gate(owner, expected_state):
    owner.host_poll(); owner.buttons()
    if getattr(owner, "route_uses_mouse", False): owner.arm.pump()
    while owner.controls.paused and owner.controls.running:
        owner.host_poll(); owner.buttons()
        if getattr(owner, "route_uses_mouse", False): owner.arm.pump()
        runtime.time.sleep(.01)
    if not owner.controls.running or owner.calibrating:
        return False
    now = runtime.time.monotonic()
    if now >= getattr(owner, "light_poll_due", 0):
        owner.light_poll_due = now + .25
        if owner.guard.update(owner.sensor.lux(), int(now * 1000)) != expected_state:
            return False
    return True


def _light_sleep(owner, milliseconds, expected_state):
    end = runtime.time.monotonic() + max(0, milliseconds) / 1000
    while runtime.time.monotonic() < end:
        if not _light_gate(owner, expected_state): return False
        runtime.time.sleep(.005)
    return True


def _light_beep(owner, frequency, duration, expected_state):
    tone = runtime.pwmio.PWMOut(runtime.board.GP6, duty_cycle=32768,
        frequency=int(frequency), variable_frequency=True)
    try:
        return _light_sleep(owner, duration, expected_state)
    finally:
        tone.duty_cycle = 0
        tone.deinit()


def _light_keycode(vk):
    # Windows virtual-key to USB boot-keyboard usage. Keep this branch-only so
    # route execution does not allocate a dictionary on CircuitPython's heap.
    if 65 <= vk <= 90: return vk - 61
    if 49 <= vk <= 57: return vk - 19
    if vk == 48: return 39
    if 112 <= vk <= 123: return vk - 54
    if vk == 13: return 40
    if vk == 27: return 41
    if vk == 8: return 42
    if vk == 9: return 43
    if vk == 32: return 44
    if vk == 189: return 45
    if vk == 187: return 46
    if vk == 219: return 47
    if vk == 221: return 48
    if vk == 220: return 49
    if vk == 186: return 51
    if vk == 222: return 52
    if vk == 192: return 53
    if vk == 188: return 54
    if vk == 190: return 55
    if vk == 191: return 56
    if vk == 20: return 57
    if vk == 44: return 70
    if vk == 145: return 71
    if vk == 19: return 72
    if vk == 45: return 73
    if vk == 36: return 74
    if vk == 33: return 75
    if vk == 46: return 76
    if vk == 35: return 77
    if vk == 34: return 78
    if vk == 39: return 79
    if vk == 37: return 80
    if vk == 40: return 81
    if vk == 38: return 82
    if vk == 144: return 83
    if vk == 111: return 84
    if vk == 106: return 85
    if vk == 109: return 86
    if vk == 107: return 87
    if vk == 108: return 88
    if 97 <= vk <= 105: return vk - 8
    if vk == 96: return 98
    if vk == 110: return 99
    if vk in (16, 160): return 225
    if vk == 161: return 229
    if vk in (17, 162): return 224
    if vk == 163: return 228
    if vk in (18, 164): return 226
    if vk == 165: return 230
    if vk == 91: return 227
    if vk == 92: return 231
    raise ValueError("unsupported virtual key: %d" % vk)


def _light_hold_ms(value):
    if not value: return 0
    pair = value.split(",")
    if len(pair) == 1:
        lo = hi = int(pair[0])
    elif len(pair) == 2:
        lo = int(pair[0]); hi = int(pair[1])
    else:
        raise ValueError("bad KEY hold")
    if lo < 0 or hi < 0: raise ValueError("negative KEY hold")
    if hi < lo: lo, hi = hi, lo
    return lo if hi == lo else _light_random.randint(lo, hi)


def _light_key(owner, args, expected_state):
    combo = None; hold = 0
    for field in args.split("|"):
        if field.startswith("combo="):
            if combo is not None: raise ValueError("duplicate KEY combo")
            combo = field[6:]
        elif field.startswith("hold="):
            hold = _light_hold_ms(field[5:])
        else:
            raise ValueError("bad KEY field")
    if not combo: raise ValueError("KEY needs combo")
    values = combo.split("+")
    if not values or len(values) > 10: raise ValueError("bad KEY combo")
    codes = []
    for value in values:
        codes.append(_light_keycode(int(value)))
    owner.emit("EVT|DEBUG|STEP/KEY keys=%d hold=%d" % (len(codes), hold))
    pressed = 0
    try:
        owner.emit("EVT|DEBUG|STEP/KEY press-start")
        for index in range(len(codes)):
            owner.emit("EVT|DEBUG|STEP/KEY press-index=%d code=%d" % (index, codes[index]))
            owner.keyboard.press(codes[index]); pressed += 1
            owner.emit("EVT|DEBUG|STEP/KEY press-ok=%d" % index)
        owner.emit("EVT|DEBUG|STEP/KEY pressed")
        if hold and not _light_sleep(owner, hold, expected_state): return False
        owner.emit("EVT|DEBUG|STEP/KEY release-start")
        while pressed:
            pressed -= 1
            owner.emit("EVT|DEBUG|STEP/KEY release-index=%d code=%d" % (pressed, codes[pressed]))
            owner.keyboard.release(codes[pressed])
        owner.emit("EVT|DEBUG|STEP/KEY released")
        return _light_gate(owner, expected_state)
    finally:
        while pressed:
            pressed -= 1
            try:
                owner.keyboard.release(codes[pressed])
            except Exception as cleanup:
                owner.emit("EVT|DEBUG|STEP/KEY cleanup-failed " + type(cleanup).__name__)


def _light_package_delay(owner, args, expected):
    parts = args.split(",")
    if len(parts) not in (1, 2): raise ValueError("bad DELAY range")
    lo = int(parts[0]); hi = int(parts[-1])
    if lo < 0 or hi < 0: raise ValueError("negative DELAY")
    if hi < lo: lo, hi = hi, lo
    value = lo if hi <= lo else _light_random.randint(lo, hi)
    return _light_sleep(owner, value, expected)


def _light_package_action(owner, op, args, expected):
    if op == "DELAY": return _light_package_delay(owner, args, expected)
    if op == "KEY": return _light_key(owner, args, expected)
    if op == "TYPE": return _light_type(owner, args, expected)
    if op in ("RMOUSE", "MOVETO"): return _light_mouse(owner, op, args, expected)
    if op in ("KDOWN", "KUP"):
        code = _light_keycode(int(args))
        if op == "KDOWN": owner.keyboard.press(code)
        else: owner.keyboard.release(code)
        owner.emit("EVT|DEBUG|STEP/%s vk=%s" % (op, args))
        return _light_gate(owner, expected)
    if op == "BEEP":
        fields = args.split(",")
        if len(fields) != 2: raise ValueError("bad BEEP")
        owner.emit("EVT|DEBUG|STEP/BEEP %s,%s" % (fields[0], fields[1]))
        return _light_beep(owner, int(fields[0]), int(float(fields[1])), expected)
    raise ValueError("unsupported package command: " + op)
def _type_pair(value, default_lo=0, default_hi=0):
    if value is None: return default_lo, default_hi
    a=value.split(",")
    if len(a)!=2: raise ValueError("bad TYPE range")
    lo=int(a[0]); hi=int(a[1])
    if lo<0 or hi<0: raise ValueError("negative TYPE range")
    if hi<lo: lo,hi=hi,lo
    return lo,hi


def _type_roll(pair):
    return pair[0] if pair[1]<=pair[0] else _light_random.randint(pair[0],pair[1])


def _type_decode(value):
    out=[]; i=0
    while i<len(value):
        if value[i]=="%" and i+2<len(value):
            try:
                out.append(chr(int(value[i+1:i+3],16))); i+=3; continue
            except Exception: pass
        out.append(value[i]); i+=1
    return "".join(out)


def _type_key(ch):
    o=ord(ch)
    if 97<=o<=122: return o-93,False
    if 65<=o<=90: return o-61,True
    if 49<=o<=57: return o-19,False
    if ch=="0": return 39,False
    if ch==" ": return 44,False
    if ch=="\t": return 43,False
    if ord(ch)==8: return 42,False
    if ch in "\r\n": return 40,False
    plain="-=[]\\;'`,./"
    shifted="_+{}|:\"~<>?"
    i=plain.find(ch)
    if i>=0:
        return (45,46,47,48,49,51,52,53,54,55,56)[i],False
    i=shifted.find(ch)
    if i>=0:
        return (45,46,47,48,49,51,52,53,54,55,56)[i],True
    symbols="!@#$%^&*()"
    i=symbols.find(ch)
    if i>=0: return (30,31,32,33,34,35,36,37,38,39)[i],True
    raise ValueError("TYPE supports exported ASCII text only")


def _type_char(owner,ch,hold,expected):
    code,shift=_type_key(ch); down=0
    try:
        if shift: owner.keyboard.press(225); down=1
        owner.keyboard.press(code); down=2
        if not _light_sleep(owner,_type_roll(hold),expected): return False
        owner.keyboard.release(code); down=1 if shift else 0
        if shift: owner.keyboard.release(225); down=0
        return _light_gate(owner,expected)
    finally:
        if down>=2:
            try: owner.keyboard.release(code)
            except Exception: pass
        if down>=1 and shift:
            try: owner.keyboard.release(225)
            except Exception: pass


def _type_neighbor(ch):
    low=ch.lower()
    for row in ("1234567890","qwertyuiop","asdfghjkl","zxcvbnm"):
        i=row.find(low)
        if i>=0:
            j=i+(-1 if _light_random.randrange(2)==0 else 1)
            if j<0 or j>=len(row): j=1 if i==0 else i-1
            n=row[j]
            return n.upper() if ch.isupper() else n
    return None


def _light_type(owner,args,expected):
    vals={}
    for field in args.split("|"):
        if "=" not in field: raise ValueError("bad TYPE field")
        k,v=field.split("=",1); vals[k]=v
    if "text" not in vals: raise ValueError("TYPE needs text")
    text=_type_decode(vals["text"])
    hold=_type_pair(vals.get("h"),80,220)
    word=_type_pair(vals.get("w"),0,0)
    punct=_type_pair(vals.get("p"),0,0)
    typo=_type_pair(vals.get("typo"),0,0)
    wp=max(0,min(100,int(vals.get("wp","100"))))
    think_chance=0; think=(800,2200)
    if "think" in vals:
        a=vals["think"].split(":",1)
        if len(a)!=2: raise ValueError("bad TYPE think")
        think_chance=max(0,min(100,int(a[0]))); think=_type_pair(a[1])
    typo_on=typo[1]>0
    next_typo=max(1,_type_roll(typo)) if typo_on else -1
    chars_since_typo=0; i=0
    owner.emit("EVT|DEBUG|STEP/TYPE chars=%d typo=%s" % (len(text), "on" if typo_on else "off"))
    while i<len(text):
        ch=text[i]
        if typo_on and not ch.isspace() and chars_since_typo>=next_typo:
            wrong=_type_neighbor(ch)
            if wrong is not None:
                if not _type_char(owner,wrong,hold,expected): return False
                if not _light_sleep(owner,_light_random.randint(max(hold[1],120),hold[1]*2+200),expected): return False
                if not _type_char(owner,chr(8),hold,expected): return False
                if not _light_sleep(owner,_type_roll(hold),expected): return False
                owner.emit("EVT|DEBUG|STEP/TYPE typo-correction char=%d" % i)
                chars_since_typo=0
                next_typo=max(1,_type_roll(typo))
        if not _type_char(owner,ch,hold,expected): return False
        chars_since_typo += 1
        if ch in ".,!?;:" and punct[1]>0:
            if not _light_sleep(owner,_type_roll(punct),expected): return False
        if ch.isspace():
            if word[1]>0 and _light_random.randrange(100)<wp:
                if not _light_sleep(owner,_type_roll(word),expected): return False
            if think_chance>0 and think[1]>0 and _light_random.randrange(100)<think_chance:
                if not _light_sleep(owner,_type_roll(think),expected): return False
        i+=1
    return True

def _mouse_pair(value, default):
    if value is None: return default
    a=value.split(",")
    if len(a)!=2: raise ValueError("bad mouse range")
    lo=int(a[0]); hi=int(a[1])
    if lo<0 or hi<0 or hi<lo: raise ValueError("invalid mouse range")
    return lo,hi


def _arm_send_cooperative(owner, line, timeout=5):
    # Mouse/HID calls must continue servicing GP4/GP3, host commands and UART
    # while the Pro Micro is acknowledging a human-mouse command.
    owner.arm.pump()
    owner.arm.write(line)
    head = line.split("|", 1)[0]
    # The ARM 2.7 firmware executes HRANDOM through hm3_move() and
    # acknowledges it as OK|HMOVE. Accept that canonical semantic reply.
    ack_head = "HMOVE" if head == "HRANDOM" else head
    end = runtime.time.monotonic() + timeout
    while runtime.time.monotonic() < end:
        owner.host_poll()
        owner.buttons()
        for reply in owner.arm.pump():
            if reply.startswith("OK|" + ack_head) or reply.startswith("ERR|"):
                return reply
        if not owner.controls.running and not owner.calibrating:
            return "ERR|STOPPED"
        runtime.time.sleep(.002)
    raise RuntimeError("arm acknowledgement timeout: " + head)


def _mouse_profile(owner, values):
    # Capability contract: owner.arm.send("HVER",3) is performed through
    # the cooperative ARM transport so Pico buttons remain responsive.
    cap=_arm_send_cooperative(owner,"HVER",3)
    if not cap.startswith("OK|HVER|2.7.0|HMOUSE=1"):
        raise RuntimeError("ARM 2.7 human mouse capability required")
    speed=getattr(owner,"route_speed",(150,500))
    mt=_mouse_pair(values.get("mt"),(0,0))
    curve=_mouse_pair(values.get("curve"),(15,45))
    before=_mouse_pair(values.get("before"),(120,450))
    after=_mouse_pair(values.get("after"),(150,600))
    mid=(12,100,400)
    if "mid" in values:
        a=values["mid"].split(":",1)
        if len(a)!=2: raise ValueError("bad mouse mid")
        r=_mouse_pair(a[1],(100,400)); mid=(int(a[0]),r[0],r[1])
    idle=(5,12,800,3000)
    if "idle" in values:
        a=values["idle"].split(":",1)
        if len(a)!=2: raise ValueError("bad mouse idle")
        every=_mouse_pair(a[0],(5,12)); pause=_mouse_pair(a[1],(800,3000))
        idle=(every[0],every[1],pause[0],pause[1])
    over=int(values.get("over","15"))
    if not 0<=mid[0]<=100 or not 0<=over<=100: raise ValueError("mouse chance out of range")
    cfg="HCFG|%d,%d,%d,%d,%d,%d,%d,%d,%d,%d"%(speed[0],speed[1],mt[0],mt[1],curve[0],curve[1],before[0],before[1],after[0],after[1])
    pauses="HPAUSE|%d,%d,%d,%d,%d,%d,%d,%d"%(mid[0],mid[1],mid[2],idle[0],idle[1],idle[2],idle[3],over)
    if not _arm_send_cooperative(owner,cfg,3).startswith("OK|"): raise RuntimeError("ARM HCFG rejected")
    if not _arm_send_cooperative(owner,pauses,3).startswith("OK|"): raise RuntimeError("ARM HPAUSE rejected")


def _mouse_values(args):
    values={}
    for field in args.split("|"):
        if "=" not in field: raise ValueError("bad mouse field")
        k,v=field.split("=",1)
        if k in values: raise ValueError("duplicate mouse field")
        values[k]=v
    return values


def _light_mouse(owner,op,args,expected):
    values=_mouse_values(args)
    owner.route_uses_mouse = True
    # Cursor origin and screen resolution belong to the Pro Micro only when
    # this route actually contains a mouse operation. Keyboard/light-only
    # routes must never wake or command the ARM.
    if not _apply_pending_cursor(owner, force=True):
        raise RuntimeError("ARM cursor origin not acknowledged")
    if not getattr(owner, "route_screen_applied", False):
        screen = getattr(owner, "route_screen", None)
        if screen is not None:
            if not owner.arm.send("SETRES|%d,%d" % (screen[0], screen[1]), 3).startswith("OK|"):
                raise RuntimeError("ARM SETRES rejected")
        owner.route_screen_applied = True
    if not _light_gate(owner,expected): return False
    if op=="MOVETO":
        if "x" not in values or "y" not in values: raise ValueError("MOVETO needs x/y")
        x=int(values["x"]); y=int(values["y"]); human=int(values.get("human","1"))
        if human==0:
            reply=_arm_send_cooperative(owner,"MMOVE|%d,%d,abs,0"%(x,y),8)
        else:
            _mouse_profile(owner,values); reply=_arm_send_cooperative(owner,"HMOVE|%d,%d"%(x,y),35)
    else:
        if "region" not in values: raise ValueError("RMOUSE needs region")
        a=values["region"].split(",")
        if len(a)!=4: raise ValueError("bad RMOUSE region")
        x,y,w,h=(int(v) for v in a)
        if w<1 or h<1: raise ValueError("bad RMOUSE region")
        _mouse_profile(owner,values); reply=_arm_send_cooperative(owner,"HRANDOM|%d,%d,%d,%d"%(x,y,w,h),35)
    if reply == "ERR|STOPPED": return False
    if not reply.startswith("OK|"):
        owner.emit("EVT|DEBUG|ARM/%s reply=%s"%(op,reply))
        raise RuntimeError("ARM human mouse rejected: " + reply)
    owner.emit("EVT|DEBUG|STEP/%s arm27=ok"%op)
    return _light_gate(owner,expected)

def _run_light_route(owner, name):
    gc.collect()
    owner.emit("EVT|DEBUG|MEM/route-enter free=%d" % gc.mem_free())
    expected = getattr(owner, "debug_last_state", None)
    owner.light_poll_due = 0
    owner.route_screen = None
    owner.route_screen_applied = False
    owner.route_uses_mouse = False
    frames = []
    with open("/" + name, "r") as fh:
        owner.emit("EVT|DEBUG|MEM/route-open free=%d" % gc.mem_free())
        while True:
            if not _light_gate(owner, expected): return False
            raw = fh.readline()
            if not raw:
                if frames: raise ValueError("LOOP without ENDLOOP")
                return True
            line = raw.strip()
            if not line or line.startswith("#"): continue
            split = line.find("|")
            if split < 0:
                op = line.upper(); args = ""
                if op != "ENDLOOP": raise ValueError("invalid route line")
            else:
                if split < 1: raise ValueError("invalid route line")
                op = line[:split].upper(); args = line[split + 1:]
            if op == "PLAN":
                if args != "2": raise ValueError("unsupported PLAN version")
            elif op == "SCREEN":
                a = args.split(",")
                if len(a) != 2: raise ValueError("bad SCREEN")
                w=int(a[0]); h=int(a[1])
                if w<1 or h<1: raise ValueError("bad SCREEN")
                owner.route_screen=(w,h)
            elif op == "SPEED":
                a = args.split(",")
                if len(a) != 2: raise ValueError("bad SPEED")
                lo=int(a[0]); hi=int(a[1])
                if lo<0 or hi<lo: raise ValueError("bad SPEED")
                owner.route_speed=(lo,hi)
            elif op == "DELAY":
                if not _light_package_delay(owner, args, expected): return False
            elif op == "RPKG":
                from random_package_runtime import run_file_package
                if not run_file_package(fh, args, owner, expected, _light_package_action): return False
            elif op == "BEEP":
                a = args.split(",")
                if len(a) != 2: raise ValueError("bad BEEP")
                owner.emit("EVT|DEBUG|STEP/BEEP %s,%s" % (a[0], a[1]))
                if not _light_beep(owner, int(a[0]), int(float(a[1])), expected): return False
            elif op == "KEY":
                if not _light_key(owner, args, expected): return
            elif op == "TYPE":
                if not _light_type(owner, args, expected): return
            elif op in ("RMOUSE", "MOVETO"):
                if not _light_mouse(owner, op, args, expected): return
            elif op in ("KDOWN", "KUP"):
                code = _light_keycode(int(args))
                if op == "KDOWN": owner.keyboard.press(code)
                else: owner.keyboard.release(code)
                owner.emit("EVT|DEBUG|STEP/%s vk=%s" % (op, args))
            elif op in ("LOOP", "LOOPTIME"):
                body = fh.tell()
                if op == "LOOP":
                    value = int(args or "1")
                    if value < 0: raise ValueError("negative LOOP")
                    frames.append([body, value, 0])
                else:
                    sec = float(args or "0")
                    if sec <= 0: raise ValueError("bad LOOPTIME")
                    frames.append([body, -1, runtime.time.monotonic() + sec])
            elif op == "ENDLOOP":
                if not frames: raise ValueError("ENDLOOP without LOOP")
                frame = frames[-1]
                if frame[1] < 0:
                    if runtime.time.monotonic() < frame[2]: fh.seek(frame[0])
                    else: frames.pop()
                elif frame[1] == 0:
                    fh.seek(frame[0])
                else:
                    frame[1] -= 1
                    if frame[1] > 0: fh.seek(frame[0])
                    else: frames.pop()
            else:
                raise ValueError("unsupported light command")


_RESTART_MARKER = "/.combined_restart_pending"

def _restart_marker_exists():
    try:
        with open(_RESTART_MARKER, "r") as fh: return fh.read().strip() == "1"
    except Exception: return False

def _restart_marker_set():
    try:
        try: runtime.storage.remount("/", readonly=False, disable_concurrent_write_protection=True)
        except Exception: pass
        with open(_RESTART_MARKER, "w") as fh: fh.write("1")
        return _restart_marker_exists()
    except Exception: return False

def _restart_marker_clear():
    try: runtime.os.remove(_RESTART_MARKER)
    except Exception: pass

def _restart_range():
    try:
        with open("/plan.txt", "r") as fh:
            for raw in fh:
                if raw.startswith("RUNFOR|"):
                    a=raw.strip().split("|",1)[1].split(",")
                    lo=int(a[0]); hi=int(a[1])
                    if hi<lo: lo,hi=hi,lo
                    if lo>0: return lo,hi
    except Exception: pass
    return 6600,7800

def _restart_tap(owner, code, lo, hi):
    owner.keyboard.press(code)
    try: owner.controls.sleep(_light_random.randint(lo,hi))
    finally: owner.keyboard.release(code)

def _restart_windows(owner):
    owner.keyboard.press(227); owner.controls.sleep(_light_random.randint(25,60))
    owner.keyboard.press(27); owner.controls.sleep(_light_random.randint(45,95)); owner.keyboard.release(27)
    owner.controls.sleep(_light_random.randint(25,60)); owner.keyboard.release(227)
    owner.controls.sleep(_light_random.randint(450,850))
    for code,hold,after in ((82,(45,100),(110,240)),(82,(45,100),(180,360)),(40,(55,120),(280,520)),(82,(45,100),(350,700)),(40,(55,120),None)):
        _restart_tap(owner,code,hold[0],hold[1])
        if after: owner.controls.sleep(_light_random.randint(after[0],after[1]))
    owner.controls.sleep(_light_random.randint(5200,6000))
    owner.keyboard.press(225); owner.controls.sleep(_light_random.randint(320,480))
    _restart_tap(owner,43,160,240); owner.controls.sleep(_light_random.randint(180,300)); owner.keyboard.release(225)
    owner.controls.sleep(_light_random.randint(300,650)); _restart_tap(owner,40,65,130)

def _restart_tick(owner):
    now=runtime.time.monotonic()
    if owner.restart_waiting:
        connected=bool(runtime.supervisor.runtime.usb_connected)
        if not connected:
            owner.restart_down_seen=True; owner.restart_up_since=None; return
        if not owner.restart_down_seen: return
        if owner.restart_up_since is None: owner.restart_up_since=now; return
        if now-owner.restart_up_since < 12: return
        owner.restart_waiting=False; owner.restart_route_pending=True
        owner.controls.start(); owner.guard.reset(); owner.debug_last_state=None
        _debug_event(owner,"RESTART","host-up route-pending=1",persist=True); return
    if not owner.controls.running or owner.restart_route_pending: return
    if owner.restart_cycle_started is None:
        lo,hi=_restart_range(); owner.restart_cycle_started=now
        owner.restart_deadline=now+(lo if hi<=lo else _light_random.randint(lo,hi))
        _debug_event(owner,"RESTART","deadline=%d"%int(owner.restart_deadline),persist=True); return
    if now < owner.restart_deadline: return
    if not _restart_marker_set(): raise RuntimeError("restart marker write failed")
    _debug_event(owner,"RESTART","armed",persist=True)
    owner.keyboard.release_all(); _restart_windows(owner)
    owner.controls.running=False; owner.restart_waiting=True
    owner.restart_down_seen=False; owner.restart_up_since=None

def _diagnostic_route(self, decision):
    if not decision.get("execute"):
        return False
    name = decision.get("route")
    if name not in _VALID_ROUTE_NAMES:
        raise runtime.GuardBundleError("unvalidated route")
    primary = None
    try:
        if name == "desktop_steps.txt" and self.restart_route_pending:
            _debug_event(self, "ROUTE", "restart-before-desktop", persist=True)
            if not _run_light_route(self, "restart_steps.txt"): return False
            self.restart_route_pending = False
            _restart_marker_clear()
            self.restart_cycle_started = runtime.time.monotonic()
            lo, hi = _restart_range()
            self.restart_deadline = self.restart_cycle_started + (lo if hi <= lo else _light_random.randint(lo, hi))
            _debug_event(self, "ROUTE", "restart-complete desktop-next", persist=True)
        return _run_light_route(self, name)
    except Exception as exc:
        primary = exc
        raise
    finally:
        try:
            self.keyboard.release_all()
        except Exception as cleanup:
            self.emit("EVT|DEBUG|CLEANUP/keyboard " + type(cleanup).__name__)
            if primary is None: raise
        try:
            self.arm.release(False)
        except Exception as cleanup:
            self.emit("EVT|DEBUG|CLEANUP/mouse " + type(cleanup).__name__)
        try:
            self.arm.release(False)
        except Exception as cleanup:
            self.emit("EVT|DEBUG|CLEANUP/mouse " + type(cleanup).__name__)
        try:
            self.arm.flush()
        except Exception as cleanup:
            self.emit("EVT|DEBUG|CLEANUP/arm " + type(cleanup).__name__)
            if primary is None: raise

runtime.Combined.route = _diagnostic_route




def _audible_loop(self):
    self.emit("combined-pico-guard-executor|GP4 start/stop hold3s=calibration|GP3 pause/resume|GP6 piezo")
    last = 0
    while True:
        self.host_poll()
        self.buttons()
        self.arm.pump()
        _restart_tick(self)
        if (self.controls.running and not self.calibrating and
                not getattr(self, "blue_start_pending", False) and
                runtime.time.monotonic() - last >= .25):
            last = runtime.time.monotonic()
            try:
                lux = self.sensor.lux()
                active = self.guard.update(lux, int(last * 1000))
                if active != self.debug_last_state:
                    _debug_event(self, "STATE", "%s lux=%.1f" % (active or "unknown", lux), persist=active is None)
                    self.debug_last_state = active
                decision = self.guard.last_decision
                # A decision is a one-shot transition. Clear it before running
                # the route so the same Desktop macro is not replayed every poll.
                self.guard.last_decision = None
                if decision is not None:
                    if decision.get("execute"):
                        route_name = decision.get("route")
                        _debug_event(self, "ROUTE", "start %s lux=%.1f" % (route_name, lux), persist=True)
                        # Cursor synchronization is lazy: keyboard/light-only routes
                        # must not command the Pro Micro. Mouse routes call it before
                        # their first mouse operation.
                        completed = self.route(decision)
                        if completed is False:
                            _debug_event(self, "ROUTE", "aborted %s" % route_name, persist=True)
                        else:
                            _debug_event(self, "ROUTE", "complete %s" % route_name, persist=True)
                    else:
                        _debug_event(self, "STATE", "denied reason=%s lux=%.1f" % (decision.get("reason"), lux), persist=True)
            except Exception as exc:
                failure = _debug_exception(exc)
                _debug_event(self, "FAIL", "guard=" + failure, persist=True)
                self.immediate_audible_stop()
                self.emit("ERR|GUARD|FAIL|" + failure[:100])
        runtime.time.sleep(.01)


# Live Classroom Studio commands that execute entirely on the Pico must not
# depend on an attached Pro Micro arm. SCREEN/SETRES is metadata; BEEP drives
# the passive piezo on GP6 directly.
def _live_host_beep(self, frequency, duration_ms):
    tone = None
    try:
        tone = runtime.pwmio.PWMOut(runtime.board.GP6, duty_cycle=32768,
            frequency=int(frequency), variable_frequency=True)
        runtime.time.sleep(duration_ms / 1000)
    finally:
        if tone is not None:
            try: tone.duty_cycle = 0; tone.deinit()
            except Exception: pass

_CURSOR_PENDING = None
_CURSOR_LAST_APPLIED = None
_CURSOR_SYNC_READY = False


def _apply_pending_cursor(self, force=False):
    """Synchronize the Pro Micro origin with an acknowledged Windows position.

    The Pro Micro owns the HID cursor state. A fire-and-forget HSETCUR can be
    lost or answered BUSY, after which the next HMOVE starts from stale/centre
    coordinates. Cursor sync is therefore a transaction: keep the pending
    value until OK|HSETCUR is received, and fail closed before a Route if no
    acknowledged origin exists.
    """
    global _CURSOR_PENDING, _CURSOR_LAST_APPLIED, _CURSOR_SYNC_READY
    if self.calibrating:
        return False
    if not getattr(self, "route_uses_mouse", False):
        return True
    if not getattr(self, "route_uses_mouse", False):
        return True
    if not getattr(self, "route_uses_mouse", False):
        return True
    if self.controls.running and not force:
        return True
    if _CURSOR_PENDING is None:
        # A host cursor sample is optional. Pico-only exports and Collector
        # sessions may start before the bridge sends CURSOR; preserve the
        # Build52 behaviour and let the ARM keep its existing origin. A pending
        # sample is still acknowledged transactionally below.
        return True
    if getattr(self.arm, "pending", 0):
        return False
    x, y = _CURSOR_PENDING
    try:
        reply = self.arm.send("HSETCUR|%d,%d" % (x, y), 2)
        if not reply.startswith("OK|HSETCUR"):
            return False
        _CURSOR_LAST_APPLIED = (x, y)
        _CURSOR_PENDING = None
        _CURSOR_SYNC_READY = True
        return True
    except Exception:
        # Keep the value pending. The next idle boundary retries it instead of
        # silently allowing a movement from a stale Arduino origin.
        return False


def _live_host_poll(self):
    if self.usb.in_waiting:
        self.host.extend(self.usb.read(self.usb.in_waiting))
    while b"\n" in self.host:
        raw, self.host = self.host.split(b"\n", 1)
        line = raw.decode("utf-8", "replace").strip()
        if not line:
            continue
        head = line.split("|", 1)[0]
        try:
            if line == "PING":
                reply = "OK|PONG|combined-pico-guard-executor|hid=on|uart=on|profiles=6|role=brain"
            elif line == "CALGET":
                reply = self.calget()
            elif line == "CALSTATUS":
                reply = self.calstatus()
            elif line.startswith("CALSET|"):
                reply = self.calset(line)
            elif line.startswith("CURSOR|"):
                # Coalesce cursor packets. The ARM must not receive HSETCUR while
                # a human mouse path is executing; the newest value is applied at
                # idle or immediately before the next route.
                global _CURSOR_PENDING
                fields = line.split("|", 1)[1].split(",")
                if len(fields) != 2:
                    raise ValueError("CURSOR needs x,y")
                x, y = int(fields[0]), int(fields[1])
                if x < 0 or y < 0:
                    raise ValueError("CURSOR range")
                _CURSOR_PENDING = (x, y)
                _apply_pending_cursor(self)
                reply = None
            elif line == "GUARD|ON":
                # A host Start is a new run request. Reset the one-shot light
                # transition gate so the same stable desktop state can execute
                # again without power-cycling the Pico.
                self.guard.reset()
                self.guard.last_decision = None
                self.debug_last_state = None
                self.controls.start()
                self.guard_start_tone()
                reply = "OK|GUARD|ON"
            elif line == "HALT|SILENT":
                self.silent_shutdown()
                reply = "OK|GUARD|OFF"
            elif line in ("GUARD|OFF", "HALT"):
                self.immediate_audible_stop()
                reply = "OK|GUARD|OFF"
            elif line == "LUX?":
                lux = self.sensor.lux()
                reply = "OK|LUX|lux=%.1f|sensor=ok" % lux
            elif line == "DEBUGGET":
                _debug_emit(self)
                reply = "OK|DEBUG|read"
            elif line == "DEBUGCLEAR":
                _debug_clear(self)
                reply = "OK|DEBUG|cleared"
            elif line.startswith("SETRES|"):
                fields = line.split("|", 1)[1].split(",")
                if len(fields) != 2:
                    raise ValueError("SETRES needs width,height")
                width, height = int(fields[0]), int(fields[1])
                if width < 1 or height < 1:
                    raise ValueError("SETRES dimensions")
                self.host_screen = (width, height)
                reply = "OK|SETRES"
            elif line.startswith("BEEP|"):
                fields = line.split("|", 1)[1].split(",")
                if len(fields) != 2:
                    raise ValueError("BEEP needs frequency,duration")
                frequency, duration_ms = int(fields[0]), int(fields[1])
                if not 30 <= frequency <= 20000 or not 0 <= duration_ms <= 60000:
                    raise ValueError("BEEP range")
                self._live_host_beep(frequency, duration_ms)
                reply = "OK|BEEP"
            else:
                reply = "ERR|UNKNOWN|" + head
        except Exception:
            reply = "ERR|EXEC|" + head
        if reply is not None:
            self.emit(reply)

runtime.Combined.__init__ = _memory_safe_init
runtime.Combined.debug_event = _debug_event
runtime.Combined.debug_get = _debug_get
runtime.Combined.debug_clear = _debug_clear
runtime.Combined.debug_emit = _debug_emit
runtime.Combined._live_host_beep = _live_host_beep
runtime.Combined.host_poll = _live_host_poll
runtime.Combined._cal_beep = _cal_beep
runtime.Combined.cal_position_tone = _cal_position_tone
runtime.Combined.cal_record_start_tone = _cal_record_start_tone
runtime.Combined.cal_stage_complete_tone = _cal_stage_complete_tone
runtime.Combined.cal_save_success_tone = _cal_save_success_tone
runtime.Combined.cal_complete_melody = _cal_complete_melody
runtime.Combined._guard_pattern = _guard_pattern
runtime.Combined.guard_start_tone = _guard_start_tone
runtime.Combined.guard_stop_tone = _guard_stop_tone
runtime.Combined.guard_pause_tone = _guard_pause_tone
runtime.Combined.guard_resume_tone = _guard_resume_tone
runtime.Combined.immediate_audible_stop = _immediate_audible_stop
runtime.Combined.silent_shutdown = _silent_shutdown
runtime.Combined.immediate_audible_start = _immediate_audible_start
runtime.Combined.enter_calibration_from_pending_start = _enter_calibration_from_pending_start
runtime.Combined.start_cal = _audible_start_cal
runtime.Combined.next_cal = _audible_next_cal
runtime.Combined.cal_tick = _audible_cal_tick
runtime.Combined.save_cal = _audible_save_cal
runtime.Combined.yellow_action = _repeatable_yellow_action
runtime.Combined.buttons = _audible_buttons
runtime.Combined.loop = _audible_loop
main()
