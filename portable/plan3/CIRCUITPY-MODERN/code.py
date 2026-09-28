# CI rerun marker: repository transfer validation
# Combined Phase 7 firmware entry point for the regular Raspberry Pi Pico.
# Parse the Guard bundle on a fresh heap, provide CircuitPython compatibility
# shims, and defer the 57 KB plan engine until route execution.
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

class _DeferredPlanEngine:
    def __init__(self):
        self.module = None

    def __getattr__(self, name):
        if self.module is None:
            # The deferred loader installs a proxy under this name during boot.
            # Removing it with del is not idempotent: a previous failed import
            # or CircuitPython module cleanup can leave the key absent, turning
            # the first route execution into KeyError('plan_engine').
            sys.modules.pop("plan_engine", None)
            gc.collect()
            try:
                self.module = __import__("plan_engine")
            except Exception:
                # Keep the proxy available for a controlled retry/diagnostic
                # instead of leaving a missing sys.modules entry.
                sys.modules["plan_engine"] = self
                raise
            sys.modules["plan_engine"] = self.module
        return getattr(self.module, name)

sys.modules["plan_engine"] = _DeferredPlanEngine()

# Load the hardware runtime first. Its verifier/protocol dependencies are
# bound below after the largest module has finished importing; importing the
# verifier before the executor leaves a fragmented heap on RP2040.
gc.collect()
import combined_guard_runtime as runtime
from combined_guard_runtime import main
# Keep the deferred plan proxy available to runtime.PlanContext and Route.
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

# Classroom Studio's complete hashed export hashes every payload except the
# hash manifest itself. Extend the verifier inventory before loading the bundle.
for _name in ("pico-calibration.json", "README-FLASH.md", "plan_engine_parse.py",
              "plan_engine_game.py", "plan_engine_human.py", "plan_engine_login.py",
              "plan_engine_exec.py", "plan_engine_parallel.py",
              "sound_step_calibration.py", "restart_cycle.py", "restart_windows.py",
              "startup_steps.txt", "whisper_steps.txt", "splash_steps.txt",
              "calibration_nvm.py"):
    if _name not in _guard_bundle.HASHED_BUNDLE_FILES:
        _guard_bundle.HASHED_BUNDLE_FILES += (_name,)
runtime.HASHED_BUNDLE_FILES = _guard_bundle.HASHED_BUNDLE_FILES
_BOOT_BUNDLE = _guard_bundle.load_guard_bundle("/")
import calibration_nvm as _calibration_nvm
_CALIBRATION_BASE_REVISION = _BOOT_BUNDLE["revision"]
_calibration_profiles = _calibration_nvm.load(
    getattr(_microcontroller, "nvm", None), _CALIBRATION_BASE_REVISION)
if _calibration_profiles is not None:
    _calibration_nvm.apply(_BOOT_BUNDLE, _calibration_profiles)
runtime.CALIBRATION_NVM_PROFILE_COUNT = len(_calibration_profiles or {})
runtime.calibration_nvm = _calibration_nvm
runtime.CALIBRATION_BASE_REVISION = _CALIBRATION_BASE_REVISION
del _calibration_profiles
del _name, _guard_bundle
# The runtime is already imported; only the verified boot bundle remains live.
gc.collect()

_DEBUG_MAX_LINES = 96
_DEBUG_NVM_BYTES = 1536
_DEBUG_MAX_BYTES = _DEBUG_NVM_BYTES - 5
_DEBUG_PERSIST_EVENTS = ("BOOT", "ROUTE", "FAIL", "STOP", "CAL")


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


def _debug_persist(self):
    try:
        # Never write diagnostics to the CIRCUITPY FAT volume while USB mass
        # storage is mounted. Host export and Pico file writes can otherwise
        # cross-link guard JSON sectors. NVM and live CDC events are sufficient.
        payload = _debug_trim(_debug_nvm_read() + "".join(self.debug_events))
        nvm_ok = _debug_nvm_write(payload)
        if nvm_ok:
            self.debug_events = []
        return nvm_ok
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
    if persist or any(kind.startswith(prefix) for prefix in _DEBUG_PERSIST_EVENTS):
        _debug_persist(self)


def _debug_get(self):
    _debug_persist(self)
    return _debug_nvm_read()


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
    # Route waits must continue polling GP3/GP4; otherwise Pause/Resume only
    # works between steps and feels unresponsive during long delays or TYPE.
    self.controls.tick = self.controls_tick
    self.route_active_profile = None
    self.route_light_last = 0
    self.mouse_pos = None
    self.sensor = runtime.BH1750()
    self.routes = {}
    self.blue = runtime.Button(runtime.board.GP4)
    self.yellow = runtime.Button(runtime.board.GP3)
    self.usb = runtime.usb_cdc.data or runtime.usb_cdc.console
    self.host = bytearray()
    self.calibrating = False
    self.sound_calibrating, self.sound_calibration_id = False, 1
    self.sound_calibration_phase = self.sound_calibration_pending = None
    self.sound_calibration_started = self.sound_calibration_silence = self.sound_calibration_peak = 0
    self.sound_bindings, self.sound_profiles = {}, {}
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
    # Force the first sensor sample to emit STATE/unknown + lux instead of
    # silently comparing None == None.
    self.debug_last_state = "__boot__"
    self.debug_last_denied = None
    self.calibration_source = (
        "nvm" if getattr(runtime, "CALIBRATION_NVM_PROFILE_COUNT", 0) else "file")
    import restart_cycle
    self.cycle = restart_cycle.Controller.from_root(
        self, getattr(_microcontroller, "nvm", None))
    _debug_event(self, "BOOT", "bundle=valid profiles=%d cal=%s" %
                 (len(runtime.PROFILES), self.calibration_source), persist=True)
    self.emit("EVT|CALSTATUS|source=%s|count=%d" %
              (self.calibration_source, len(runtime.PROFILES)))
    if self.calibration_source == "nvm":
        self.emit("EVT|CAL|storage=nvm|loaded=%d" %
                  runtime.CALIBRATION_NVM_PROFILE_COUNT)

_CAL_NOTES = (262, 294, 330, 349, 392, 440)
_GUARD_START_PATTERN = ((784, 160), (988, 160), (1175, 200), (0, 80), (1175, 280))
_GUARD_STOP_PATTERN = ((392, 180), (330, 160), (262, 260), (0, 60), (196, 260))
_GUARD_PAUSE_PATTERN = ((523, 180), (0, 100), (523, 180), (0, 100), (523, 340))
_GUARD_RESUME_PATTERN = ((659, 150), (784, 150), (988, 150), (784, 150), (988, 300))
_CAL_ENTER_PATTERN = ((523, 100), (659, 120), (784, 180))
_CAL_EXIT_PATTERN = ((784, 100), (659, 120), (523, 220))
_CAL_SAVE_ERROR_PATTERN = ((220, 140), (0, 80), (220, 260))
def _sound_module():
    gc.collect(); return sys.modules.get("sound_step_calibration") or __import__("sound_step_calibration")

def _drop_sound_module(self):
    if not self.sound_calibrating: sys.modules.pop("sound_step_calibration", None); gc.collect()

def _sound_profile(self, profile_id, binding, threshold, minimum):
    try: return _sound_module().resolve(self, profile_id, binding, threshold, minimum)
    finally: _drop_sound_module(self)

def _sound_start_calibration(self): return _sound_module().start(self)
def _sound_select_next(self): return _sound_module().select_next(self)
def _sound_begin_sample(self): return _sound_module().begin_sample(self)
def _sound_calibration_tick(self): return _sound_module().tick(self)
def _sound_end_calibration(self):
    result = _sound_module().finish(self); _drop_sound_module(self); return result

def _cal_beep(self, frequency, duration_ms):
    tone = None
    try:
        tone = runtime.pwmio.PWMOut(runtime.board.GP6, duty_cycle=32768,
            frequency=int(frequency), variable_frequency=True)
        runtime.time.sleep(duration_ms / 1000)
    except Exception:
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
    # A longer, unmistakable rising confirmation. The previous 270 ms pair
    # was easy to miss next to the stage-position cue on the passive piezo.
    self._cal_beep(880, 160)
    runtime.time.sleep(.06)
    self._cal_beep(1175, 220)
    runtime.time.sleep(.06)
    self._cal_beep(1568, 360)

def _cal_save_error_tone(self):
    self._guard_pattern(_CAL_SAVE_ERROR_PATTERN)

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
    self._guard_pattern(_GUARD_START_PATTERN)

def _guard_stop_tone(self):
    self._guard_pattern(_GUARD_STOP_PATTERN)

def _guard_pause_tone(self):
    self._guard_pattern(_GUARD_PAUSE_PATTERN)

def _guard_resume_tone(self):
    self._guard_pattern(_GUARD_RESUME_PATTERN)

def _calibration_enter_tone(self):
    self._guard_pattern(_CAL_ENTER_PATTERN)

def _calibration_exit_tone(self):
    self._guard_pattern(_CAL_EXIT_PATTERN)

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

def _enter_calibration_from_pending_start(self):
    _prepare_calibration_heap(self)
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
    self.calibration_enter_tone()
    self.cal_position_tone()

_original_start_cal = runtime.Combined.start_cal
_original_end_cal = runtime.Combined.end_cal
_original_next_cal = runtime.Combined.next_cal
_original_cal_tick = runtime.Combined.cal_tick
_original_save_cal = runtime.Combined.save_cal
_original_yellow_action = runtime.Combined.yellow_action

_PLAN_MODULES = ("plan_engine_exec", "plan_engine_game", "plan_engine_human",
                 "plan_engine_login", "plan_engine_parallel", "plan_engine_parse")

def _release_plan_heap(self, emit_cal=False):
    # Complex routes lazily import the split plan engine. CircuitPython keeps
    # those modules cached after Stop/PlanAbort. Return the deferred proxy to
    # its cold state after every complex route so a second Start cannot retain
    # the prior parsed Game tree and enter the next parse with ~1 KB free.
    proxy = runtime.plan_engine
    try:
        if hasattr(proxy, "module"):
            proxy.module = None
    except Exception:
        pass
    for name in _PLAN_MODULES:
        sys.modules.pop(name, None)
    sys.modules["plan_engine"] = proxy
    gc.collect()
    if emit_cal:
        try:
            self.emit("EVT|DEBUG|CAL|heap-ready|free=%d" % gc.mem_free())
        except Exception:
            pass

def _prepare_calibration_heap(self):
    # Calibration also needs a contiguous block for its atomic JSON write.
    _release_plan_heap(self, True)

def _audible_start_cal(self):
    _prepare_calibration_heap(self)
    _original_start_cal(self)
    if self.calibrating:
        self.calibration_enter_tone()
        if self.stage == 0:
            self.cal_position_tone()

def _audible_end_cal(self):
    was_calibrating = self.calibrating
    _original_end_cal(self)
    if was_calibrating and not self.calibrating:
        self.calibration_exit_tone()

def _audible_next_cal(self):
    previous = self.stage
    blocked_unsaved = (self.calibrating and isinstance(self.result, dict) and not self.saved)
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
    elif blocked_unsaved and self.stage == previous:
        # Short blue cannot advance past a rejected/unsaved profile. Make that
        # guard audible instead of looking like a dead button.
        self.cal_save_error_tone()

def _audible_cal_tick(self):
    was_sampling = self.result == "sampling"
    _original_cal_tick(self)
    if was_sampling and isinstance(self.result, dict):
        self.samples = []
        _prepare_calibration_heap(self)
        self.save_cal()
    elif was_sampling and self.result is None:
        # UNSTABLE used to fail silently on the physical interface: the event
        # was emitted over CDC, but the user heard neither save nor error.
        self.cal_save_error_tone()
        _debug_event(self, "CAL", "sample-failed stage=%d id=%s" %
            (self.stage + 1, runtime.PROFILES[self.stage]), persist=True)

def _audible_save_cal(self):
    profile_was_saved = runtime.PROFILES[self.stage] in self.saved_ids
    had_pending_result = isinstance(self.result, dict) and not self.saved
    _original_save_cal(self)
    if had_pending_result and self.saved:
        _debug_event(self, "CAL", "save-ok stage=%d id=%s source=nvm" %
            (self.stage + 1, runtime.PROFILES[self.stage]), persist=True)
        if not profile_was_saved and len(self.saved_ids) == len(runtime.PROFILES):
            self.cal_complete_melody()
        else:
            self.cal_save_success_tone()
    elif had_pending_result and not self.saved and self.last_cal_error:
        _debug_event(self, "CAL", "save-failed stage=%d id=%s error=%s" %
            (self.stage + 1, runtime.PROFILES[self.stage],
             self.last_cal_error), persist=True)
        self.cal_save_error_tone()

def _repeatable_yellow_action(self):
    if not self.calibrating:
        was_paused = self.controls.paused
        _original_yellow_action(self)
        if self.controls.running and self.controls.paused != was_paused:
            if self.controls.paused:
                # Pause can be requested from inside Controls.sleep() while a
                # KEY/COMBO hold is active. Release immediately instead of
                # leaving Windows to auto-repeat the held key until Resume.
                try:
                    self.keyboard.release_all()
                except Exception:
                    pass
            self.guard_pause_tone() if self.controls.paused else self.guard_resume_tone()
            _debug_event(self, "GP3", "pause" if self.controls.paused else "resume", persist=True)
        return
    if self.result == "sampling":
        _original_yellow_action(self)
        self.cal_save_error_tone()
        _debug_event(self, "GP3", "calibration-busy stage=%d" %
            (self.stage + 1), persist=True)
        return
    if isinstance(self.result, dict) and not self.saved:
        if (self.last_cal_error or "").startswith("OVERLAP:"):
            self.samples = []
            self.sample_started = runtime.time.monotonic()
            self.result = "sampling"
            self.last_cal_error = None
            self.emit("EVT|CAL|mode=started|stage=%d|id=%s|seconds=5|saved=%d|retry=1" %
                (self.stage + 1, runtime.PROFILES[self.stage], len(self.saved_ids)))
            self.cal_record_start_tone()
            return
        self.save_cal()
        return
    retry = 1 if self.saved and isinstance(self.result, dict) else 0
    self.samples = []
    self.sample_started = runtime.time.monotonic()
    self.result = "sampling"
    self.emit("EVT|CAL|mode=started|stage=%d|id=%s|seconds=5|saved=%d|retry=%d" %
        (self.stage + 1, runtime.PROFILES[self.stage], len(self.saved_ids), retry))
    _debug_event(self, "GP3", "calibration-start stage=%d id=%s wait=5s" %
        (self.stage + 1, runtime.PROFILES[self.stage]), persist=True)
    self.cal_record_start_tone()

def _audible_buttons(self):
    now = runtime.time.monotonic()
    blue = self.blue.poll(now)
    yellow = self.yellow.poll(now)
    if blue == "down":
        _debug_event(self, "GP4", "down running=%s calibrating=%s" % (self.controls.running, self.calibrating))
    elif blue == "long":
        _debug_event(self, "GP4", "long calibrating=%s" % self.calibrating)
    elif blue == "up":
        _debug_event(self, "GP4", "up")
    if yellow == "long":
        _debug_event(self, "GP3", "long sound-calibrating=%s" % self.sound_calibrating)
    if self.sound_calibrating:
        _sound_module().handle_buttons(self, blue, yellow); return
    if yellow == "long" and not self.calibrating and not self.controls.running:
        self.sound_start_calibration()
        return
    if blue == "down" and not self.calibrating and self.controls.running:
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
                self.guard.last_decision = None
                # A physical Start is a fresh observation boundary, exactly
                # like GUARD|ON. Without this reset, unknown -> unknown was
                # measured but suppressed until the Pico reconnected.
                self.debug_last_state = "__start__"
                self.debug_last_denied = None
                profiles = self.bundle.get("calibration", {}).get("profiles", {})
                dashboard = profiles.get("character-dashboard", {})
                center = float(dashboard.get("center", 0))
                tolerance = float(dashboard.get("tolerance", 0))
                self.emit(
                    "EVT|CALSTATUS|source=%s|id=character-dashboard|"
                    "center=%.1f|tolerance=%.1f|range=%.1f,%.1f" %
                    (getattr(self, "calibration_source", "file"),
                     center, tolerance, center - tolerance, center + tolerance))
                self.controls.start()
                self.guard_start_tone()
                _debug_event(self, "GP4", "short-start running=1", persist=True)
        elif not stop_consumed and not start_consumed and not self.blue.long:
            if self.calibrating:
                self.next_cal()
    if yellow == "up" and not self.yellow.long:
        self.yellow_action()
    self.cal_tick()


def _cycle_controls_tick(self):
    # Route waits cooperatively poll controls, the cycle deadline, and Guard.
    # A newly stable optical state aborts the old route without stopping the
    # overall run; the outer loop consumes Guard.last_decision next.
    self.buttons()
    self.cycle.route_tick()
    profile = getattr(self, "route_active_profile", None)
    now = runtime.time.monotonic()
    if (not profile or self.controls.aborted or
            now - getattr(self, "route_light_last", 0) < .10):
        return
    self.route_light_last = now
    lux = self.sensor.lux()
    self.guard.update(lux, int(now * 1000))
    decision = self.guard.last_decision
    stable = getattr(getattr(self.guard, "transition", None), "last_stable", None)
    if decision is None or stable is None or stable == profile:
        return
    _debug_event(self, "STATE", "preempt from=%s to=%s lux=%.1f" %
                 (profile, stable, lux), persist=True)
    self.emit("EVT|GUARD|PREEMPT|from=%s|to=%s|lux=%.1f" %
              (profile, stable, lux))
    self.controls.aborted = True
    try: self.keyboard.release_all()
    except Exception: pass
    try: self.arm.abort()
    except Exception: pass


def _plan_context(self):
    return runtime.PlanContext(self)


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

_LIGHT_ROUTE_COMMANDS = {
    "PLAN", "SCREEN", "SPEED", "BEEP", "DELAY", "KEY", "KDOWN", "KUP", "RAW",
    "HANDPATH", "RMOUSE", "TYPE", "LABEL", "GOTO", "RPKG",
    "PKGITEM", "ENDPKG", "PGROUP", "PARITEM", "ENDPAR", "WSND", "WSNDP",
    "SOUNDWATCH", "WPROFILE",
    "LOOP", "LOOPTIME", "ENDLOOP",
}

def _light_route_rows(rows):
    commands = []; loop_depth = 0
    for raw in rows:
        line = raw.strip()
        if not line or line.startswith("#"): continue
        parts = line.split("|", 1); command = parts[0].upper()
        args = parts[1].strip() if len(parts) == 2 else ""
        if command not in _LIGHT_ROUTE_COMMANDS: return None
        if command in ("LOOP", "LOOPTIME"):
            if not args: return None
            loop_depth += 1
        elif command == "ENDLOOP":
            if args or loop_depth <= 0: return None
            loop_depth -= 1
        elif command in ("PKGITEM", "ENDPKG", "PGROUP", "PARITEM", "ENDPAR"):
            if args: return None
        elif len(parts) != 2: return None
        commands.append((command, args))
    return commands if loop_depth == 0 else None


def _light_route_lines(text):
    return _light_route_rows(text.splitlines())


def _light_route_file(name):
    # Stream lines: Game routes exceed 11 KB and fragmented RP2040 heaps cannot
    # guarantee one contiguous source-string allocation.
    with open("/" + name, "r") as fh:
        return _light_route_rows(fh)

def _run_light_route(ctx, commands):
    if any(item[0] in ("PGROUP", "SOUNDWATCH", "WPROFILE") for item in commands):
        gc.collect()
        import plan_engine_game
        plan_engine_game.run_game(commands, ctx)
        return
    index = 0
    loops = []  # [LOOP/LOOPTIME command index, remaining count, deadline]
    labels = {}
    for label_index, item in enumerate(commands):
        if item[0] == "LABEL":
            if not item[1] or item[1] in labels:
                raise ValueError("LABEL needs a unique name")
            labels[item[1]] = label_index
    route_speed = [0, 2000]
    mouse_pos = [ctx.screen_w // 2, ctx.screen_h // 2]
    login_helper = None
    login_pauses = None
    while index < len(commands):
        command, args = commands[index]
        if command == "PLAN":
            pass
        if command == "SCREEN":
            fields = args.replace(",", " ").split()
            if len(fields) != 2:
                raise ValueError("SCREEN needs width,height")
            ctx.screen_w, ctx.screen_h = int(fields[0]), int(fields[1])
            _debug_event(ctx.r, "STEP", "SCREEN metadata %dx%d" % (ctx.screen_w, ctx.screen_h), persist=True)
        elif command == "SPEED":
            fields = args.replace(",", " ").split()
            if len(fields) != 2:
                raise ValueError("SPEED needs min,max")
            route_speed[0], route_speed[1] = int(fields[0]), int(fields[1])
            if route_speed[1] < route_speed[0]:
                route_speed[0], route_speed[1] = route_speed[1], route_speed[0]
        elif command == "DELAY":
            fields = args.replace(",", " ").split()
            if len(fields) == 1:
                lo = hi = int(float(fields[0]))
            elif len(fields) == 2:
                lo, hi = int(float(fields[0])), int(float(fields[1]))
            else:
                raise ValueError("DELAY needs value or min,max")
            if hi < lo: lo, hi = hi, lo
            if not ctx.sleep_ms(runtime.random.randint(lo, hi)):
                raise RuntimeError("route aborted")
        elif command == "KEY":
            values = None; hold = (0, 0)
            for field in args.split("|"):
                if field.startswith("combo="):
                    values = [int(v) for v in field[6:].split("+") if v]
                elif field.startswith("hold="):
                    pair = [int(v) for v in field[5:].replace(",", " ").split()]
                    if len(pair) != 2: raise ValueError("KEY hold needs min,max")
                    hold = (min(pair), max(pair))
                else:
                    raise ValueError("bad KEY field")
            if not values or len(values) > 10:
                raise ValueError("KEY needs combo")
            ctx.key_combo(values, hold[0], hold[1])
        elif command == "KDOWN":
            ctx.kdown(int(args))
        elif command == "KUP":
            ctx.kup(int(args))
        elif command == "RAW":
            if args.startswith("MMOVE|"):
                fields = args[6:].split(",")
                if len(fields) != 4 or fields[2].strip().lower() != "rel":
                    raise ValueError("light RAW MMOVE needs dx,dy,rel,human")
                ctx.mmove_relative(int(fields[0]), int(fields[1]))
            elif not args or ctx.raw(args) is None:
                raise RuntimeError("RAW route command aborted")
        elif command == "HANDPATH":
            # Compact streaming replay for a ten-second hand sample.
            # Format: HANDPATH|delayMs,dx,dy;delayMs,dx,dy;...
            # Do not split the entire payload: hundreds of list/string objects
            # would recreate the RP2040 heap pressure this command avoids.
            start = 0
            count = 0
            size = len(args)
            while start < size:
                end = args.find(";", start)
                if end < 0:
                    end = size
                token = args[start:end]
                fields = token.split(",", 2)
                if len(fields) != 3:
                    raise ValueError("HANDPATH needs delay,dx,dy segments")
                delay_ms = int(fields[0])
                dx = int(fields[1])
                dy = int(fields[2])
                if delay_ms < 1 or delay_ms > 60000 or abs(dx) > 8192 or abs(dy) > 8192:
                    raise ValueError("HANDPATH segment out of range")
                if not ctx.sleep_ms(delay_ms):
                    raise RuntimeError("route aborted")
                if dx or dy:
                    ctx.mmove_relative(dx, dy)
                count += 1
                if count > 2000:
                    raise ValueError("HANDPATH has too many segments")
                if not (count & 31):
                    gc.collect()
                start = end + 1
            if not count:
                raise ValueError("HANDPATH is empty")
        elif command in ("RMOUSE", "TYPE"):
            if login_helper is None:
                gc.collect()
                import plan_engine_login as login_helper
                login_pauses = login_helper.PausePlanner()
            if command == "RMOUSE":
                login_helper.run_rmouse(args, ctx, login_pauses, mouse_pos, route_speed)
            else:
                login_helper.run_type(args, ctx)
        elif command == "LABEL":
            pass
        elif command == "GOTO":
            target = labels.get(args)
            if target is None:
                raise ValueError("GOTO label not found")
            loops[:] = []
            index = target
            continue
        elif command == "LOOP":
            count = int(args)
            if count < 0:
                raise ValueError("LOOP needs a nonnegative count")
            loops.append([index, count, None])
        elif command == "LOOPTIME":
            seconds = float(args)
            if seconds <= 0:
                raise ValueError("LOOPTIME needs positive seconds")
            loops.append([index, 0, ctx.now() + seconds])
        elif command == "ENDLOOP":
            if not loops:
                raise ValueError("ENDLOOP without LOOP")
            top = loops[-1]
            if top[2] is not None:
                if ctx.now() < top[2]:
                    index = top[0]
                else:
                    loops.pop()
            elif top[1] == 0:
                index = top[0]
            else:
                top[1] -= 1
                if top[1] > 0:
                    index = top[0]
                else:
                    loops.pop()
        elif command in ("WSND", "WSNDP"):
            _sound_module().run_wait(ctx, command, args); _drop_sound_module(ctx.r)
        elif command == "BEEP":
            fields = args.replace(",", " ").split()
            if len(fields) != 2:
                raise ValueError("BEEP needs frequency,duration")
            ctx.beep(int(fields[0]), int(float(fields[1])))
        index += 1

def _diagnostic_route(self, decision):
    if not decision.get("execute"):
        return False
    name = decision.get("route")
    if (name not in self.bundle["manifest"]["routes"].values()
            and name not in ("restart_steps.txt", "startup_steps.txt")):
        raise GuardBundleError("unvalidated route")
    # A parsed plan is consumable: loop/random bookkeeping and the step cursor
    # must never be reused by the next invocation of the same Route. Re-read
    # and parse the plan for every transition so repeated desktop runs really
    # emit their RMOUSE steps again.
    # Route-stage diagnostic only: do not change route semantics.
    self.emit("EVT|DEBUG|ROUTE|stage=before-route-read|free=%d" % gc.mem_free())
    commands = _light_route_file(name)
    text = None
    if commands is None:
        with open("/" + name, "r") as fh:
            text = fh.read()
    self.emit("EVT|DEBUG|ROUTE|stage=after-route-read|free=%d" % gc.mem_free())
    self.route_active_profile = decision.get("profile")
    self.route_light_last = 0
    try:
        if commands is not None:
            gc.collect()
            self.emit("EVT|DEBUG|ROUTE|stage=light-route|free=%d" % gc.mem_free())
            # Keep simple Pico-only routes off the large plan_engine import.
            route_ctx = runtime.PlanContext(self)
            try:
                _run_light_route(route_ctx, commands)
            except RuntimeError as exc:
                if str(exc) == "route aborted":
                    self.emit("EVT|DEBUG|ROUTE/aborted " + name)
                    return False
                raise
            finally:
                route_ctx.close()
        else:
            self.emit("EVT|DEBUG|ROUTE|stage=before-plan-engine-import|free=%d" % gc.mem_free())
            try:
                parser = runtime.plan_engine.parse_plan
            except MemoryError:
                self.emit("EVT|DEBUG|ROUTE|stage=plan-engine-import-memoryerror")
                raise
            self.emit("EVT|DEBUG|ROUTE|stage=after-plan-engine-import|free=%d" % gc.mem_free())
            try:
                route_plan = parser(text)
            except MemoryError:
                self.emit("EVT|DEBUG|ROUTE|stage=plan-parse-memoryerror")
                raise
            # The parser creates many short-lived strings and containers. The
            # source text is no longer needed once route_plan exists; reclaim
            # both before the first RMOUSE needs working heap.
            del text
            gc.collect()
            self.emit("EVT|DEBUG|ROUTE|stage=after-plan-parse|free=%d" % gc.mem_free())
            route_ctx = runtime.PlanContext(self)
            if getattr(route_ctx, "mouse_mode", "") == "relative":
                self.emit("EVT|DEBUG|CURSOR|plan-mode=relative-native")
            else:
                origin = route_ctx.get_mouse_pos()
                if origin is None:
                    self.emit("EVT|DEBUG|CURSOR|plan-origin=unknown")
                else:
                    self.emit("EVT|DEBUG|CURSOR|plan-origin=%d,%d" % (origin[0], origin[1]))
            aborted = False
            try:
                runtime.plan_engine.run_plan(route_plan, route_ctx)
            except runtime.plan_engine.PlanAbort:
                # GP4 Stop is normal control flow, not a Guard failure. Catch
                # it here so no traceback retains the full parsed Game tree.
                aborted = True
            finally:
                route_ctx.close()
                del route_plan
                del route_ctx
            if aborted:
                return False
        self.arm.flush()
        return True
    finally:
        self.route_active_profile = None
        # Cleanup must be idempotent and must not manufacture a click. Arm
        # releases only buttons explicitly tracked as held by MDOWN.
        try:
            self.arm.release(False)
        except Exception as cleanup:
            _debug_event(self, "CLEANUP", "mouse " + type(cleanup).__name__)
        _release_plan_heap(self)
        try:
            self.arm.flush()
        except Exception as cleanup:
            _debug_event(self, "CLEANUP", "arm " + type(cleanup).__name__)

runtime.Combined.route = _diagnostic_route

def _audible_loop(self):
    self.emit("combined-pico-guard-executor|GP4 start/stop hold3s=calibration|GP3 pause/resume|GP6 piezo")
    last = 0
    while True:
        self.host_poll()
        self.buttons()
        self.arm.pump()
        self.cycle.tick()
        if (self.controls.running and not self.calibrating and not self.sound_calibrating and
                not getattr(self, "blue_start_pending", False) and
                runtime.time.monotonic() - last >= .25):
            last = runtime.time.monotonic()
            try:
                lux = self.sensor.lux()
                active = self.guard.update(lux, int(last * 1000))
                if active != self.debug_last_state:
                    _debug_event(self, "STATE", "%s lux=%.1f" % (active or "unknown", lux), persist=active is None)
                    self.debug_last_state = active
                    self.debug_last_denied = None
                decision = self.guard.last_decision
                # A decision is a one-shot transition. Clear it before running
                # the route so the same Desktop macro is not replayed every poll.
                self.guard.last_decision = None
                if decision is not None:
                    # A Guard preemption aborts only the previous route. Manual
                    # Stop sets running=False and must never be revived here.
                    if self.controls.running:
                        self.controls.aborted = False
                    if decision.get("execute"):
                        route_name = decision.get("route")
                        _debug_event(self, "ROUTE", "start %s lux=%.1f" % (route_name, lux), persist=True)
                        if getattr(runtime.PlanContext, "mouse_mode", "") == "relative":
                            _debug_event(self, "CURSOR", "relative-native-before-route", persist=True)
                        else:
                            if not _apply_pending_cursor(self, force=True):
                                _debug_event(self, "CURSOR", "sync-failed-before-route", persist=True)
                                raise RuntimeError("ARM cursor origin not acknowledged")
                            _debug_event(self, "CURSOR", "sync-ok-before-route", persist=True)
                        completed = self.route(decision)
                        if completed is False:
                            _debug_event(self, "ROUTE", "aborted %s" % route_name, persist=True)
                        else:
                            _debug_event(self, "ROUTE", "complete %s" % route_name, persist=True)
                            self.cycle.route_complete(route_name)
                    else:
                        denied = (active, decision.get("reason"))
                        if denied != self.debug_last_denied:
                            _debug_event(self, "STATE", "denied reason=%s lux=%.1f" %
                                         (decision.get("reason"), lux), persist=True)
                            self.debug_last_denied = denied
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
    if self.calibrating or self.sound_calibrating:
        return False
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
        self.mouse_pos = (x, y)
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
                self.debug_last_state = "__start__"
                self.debug_last_denied = None
                self.emit("EVT|CALSTATUS|source=%s|count=%d" %
                          (getattr(self, "calibration_source", "file"),
                           len(self.bundle.get("calibration", {}).get("profiles", {}))))
                _apply_pending_cursor(self, force=True)
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
            elif line.startswith("SCAL|"):
                ms = int(line.split("|", 1)[1])
                if ms < 1 or ms > 10000:
                    raise ValueError("SCAL range")
                reply = self.arm.send(line, ms / 1000.0 + 3)
            elif line.startswith("WSND|"):
                fields = line.split("|", 1)[1].split(",")
                if len(fields) != 3:
                    raise ValueError("WSND needs threshold,min,timeout")
                threshold, minimum, timeout_ms = int(fields[0]), int(fields[1]), int(fields[2])
                if threshold < 1 or minimum < 1 or timeout_ms < 1 or timeout_ms > 600000:
                    raise ValueError("WSND range")
                reply = self.arm.send(line, timeout_ms / 1000.0 + 3)
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
runtime.Combined.cal_save_error_tone = _cal_save_error_tone
runtime.Combined.cal_complete_melody = _cal_complete_melody
runtime.Combined._guard_pattern = _guard_pattern
runtime.Combined.guard_start_tone = _guard_start_tone
runtime.Combined.guard_stop_tone = _guard_stop_tone
runtime.Combined.guard_pause_tone = _guard_pause_tone
runtime.Combined.guard_resume_tone = _guard_resume_tone
runtime.Combined.calibration_enter_tone = _calibration_enter_tone
runtime.Combined.calibration_exit_tone = _calibration_exit_tone
runtime.Combined.prepare_calibration_heap = _prepare_calibration_heap
runtime.Combined.sound_profile = _sound_profile
runtime.Combined.sound_start_calibration = _sound_start_calibration
runtime.Combined.sound_select_next = _sound_select_next
runtime.Combined.sound_begin_sample = _sound_begin_sample
runtime.Combined.sound_calibration_tick = _sound_calibration_tick
runtime.Combined.sound_end_calibration = _sound_end_calibration
runtime.Combined.immediate_audible_stop = _immediate_audible_stop
runtime.Combined.silent_shutdown = _silent_shutdown
runtime.Combined.immediate_audible_start = _immediate_audible_start
runtime.Combined.enter_calibration_from_pending_start = _enter_calibration_from_pending_start
runtime.Combined.start_cal = _audible_start_cal
runtime.Combined.end_cal = _audible_end_cal
runtime.Combined.next_cal = _audible_next_cal
runtime.Combined.cal_tick = _audible_cal_tick
runtime.Combined.save_cal = _audible_save_cal
runtime.Combined.yellow_action = _repeatable_yellow_action
runtime.Combined.buttons = _audible_buttons
runtime.Combined.controls_tick = _cycle_controls_tick
runtime.Combined.plan_context = _plan_context
runtime.Combined.loop = _audible_loop
main()
