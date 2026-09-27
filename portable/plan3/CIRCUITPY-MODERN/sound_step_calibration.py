# Portable two-slot sound-step calibration for the combined Pico Guard.
# Loaded lazily by code.py so normal boot and non-sound routes pay no heap cost.
import gc
import hashlib
import json
import os
import time

_PATH = "/sound-step-calibration.json"
_BACKUP = "/sound-step-calibration.bak"
_TEMP = "/sound-step-calibration.tmp"
_ROUTES = (
    "/plan.txt", "/desktop_steps.txt", "/login_or_dc_steps.txt",
    "/character_dashboard_steps.txt", "/entering_game_loading_steps.txt",
    "/game_steps.txt", "/restart_steps.txt", "/targeted_steps.txt",
    "/resumable_steps.txt", "/launch_recovery.txt", "/main_recovery.txt",
    "/dc_steps.txt",
)
_FORMAT = 1
_SILENCE_SECONDS = 3.0
_TARGET_SECONDS = 30.0
_SAMPLE_MS = 250
_MIN_SEPARATION = 12
_MIN_DURATION_MS = 20


def _emit(owner, text):
    owner.emit("EVT|SOUNDCAL|" + text)


def _error(owner, code, detail=""):
    owner.emit("ERR|SOUNDCAL|" + code + (("|" + detail) if detail else ""))
    try:
        owner.cal_save_error_tone()
    except Exception:
        pass


def _canonical(profiles):
    lines = ["format=1"]
    for key in ("1", "2"):
        item = profiles.get(key)
        if not item:
            continue
        lines.append("%s|%s|%d|%d|%d|%d" % (
            key, item["binding"], item["threshold"], item["minDurationMs"],
            item["silenceMax"], item["soundPeak"]))
    return "\n".join(lines) + "\n"


def _checksum(profiles):
    return hashlib.sha256(_canonical(profiles).encode("utf-8")).hexdigest()


def _validated(document):
    if not isinstance(document, dict) or document.get("format") != _FORMAT:
        raise ValueError("format")
    profiles = document.get("profiles")
    if not isinstance(profiles, dict) or document.get("checksum") != _checksum(profiles):
        raise ValueError("checksum")
    clean = {}
    for key, item in profiles.items():
        if key not in ("1", "2") or not isinstance(item, dict):
            raise ValueError("profile")
        binding = item.get("binding")
        threshold = item.get("threshold")
        minimum = item.get("minDurationMs")
        silence = item.get("silenceMax")
        peak = item.get("soundPeak")
        if (not isinstance(binding, str) or len(binding) != 12
                or any(ch not in "0123456789abcdefABCDEF" for ch in binding)
                or not isinstance(threshold, int) or not 1 <= threshold <= 511
                or not isinstance(minimum, int) or not 1 <= minimum <= 5000
                or not isinstance(silence, int) or not 0 <= silence <= 511
                or not isinstance(peak, int) or not 0 <= peak <= 511
                or peak <= silence):
            raise ValueError("profile")
        clean[key] = {
            "binding": binding.lower(), "threshold": threshold,
            "minDurationMs": minimum, "silenceMax": silence, "soundPeak": peak,
        }
    return clean


def _read(path):
    with open(path, "r") as handle:
        return _validated(json.loads(handle.read()))


def _load(owner=None):
    for path in (_PATH, _BACKUP):
        try:
            profiles = _read(path)
            if owner is not None and path == _BACKUP:
                _emit(owner, "mode=recovered-backup")
            return profiles
        except Exception:
            pass
    return {}


def _document(profiles):
    return json.dumps({"format": _FORMAT, "profiles": profiles,
                       "checksum": _checksum(profiles)})


def _remove(path):
    try:
        os.remove(path)
    except Exception:
        pass


def _write_verified(path, text):
    with open(path, "w") as handle:
        handle.write(text)
        try:
            handle.flush()
        except Exception:
            pass
    with open(path, "r") as handle:
        if handle.read() != text:
            raise RuntimeError("verify")


def _save(profiles):
    text = _document(profiles)
    _remove(_TEMP)
    _write_verified(_TEMP, text)
    old = None
    try:
        with open(_PATH, "r") as handle:
            old = handle.read()
    except Exception:
        pass
    if old is not None:
        _remove(_BACKUP)
        _write_verified(_BACKUP, old)
    try:
        _remove(_PATH)
        os.rename(_TEMP, _PATH)
        if _read(_PATH) != _validated(json.loads(text)):
            raise RuntimeError("readback")
    except Exception:
        _remove(_TEMP)
        if old is not None:
            try:
                _write_verified(_PATH, old)
            except Exception:
                pass
        raise


def _parse_route_line(line):
    line = line.strip()
    if not line.startswith("WSNDP|"):
        return None
    values = line[6:].split(",")
    if len(values) != 5:
        return None
    try:
        profile_id = int(values[0])
        threshold = int(values[2])
        minimum = int(values[3])
    except Exception:
        return None
    binding = values[1].strip().lower()
    if profile_id not in (1, 2) or len(binding) != 12:
        return None
    return profile_id, binding, threshold, minimum


def _discover(owner):
    found = {}
    conflicts = set()
    for path in _ROUTES:
        try:
            with open(path, "r") as handle:
                for line in handle:
                    row = _parse_route_line(line)
                    if row is None:
                        continue
                    profile_id, binding, threshold, minimum = row
                    old = found.get(profile_id)
                    if old is not None and old[0] != binding:
                        conflicts.add(profile_id)
                    else:
                        found[profile_id] = (binding, threshold, minimum)
        except Exception:
            pass
    for profile_id in conflicts:
        found.pop(profile_id, None)
        _error(owner, "DUPLICATE-ID", "id=%d" % profile_id)
    owner.sound_bindings = found
    return found


def _sample(owner):
    reply = owner.arm.send("SCAL|%d" % _SAMPLE_MS, 2.0)
    average = None
    peak = None
    for part in reply.split("|"):
        if part.startswith("avg="):
            average = int(part[4:])
        elif part.startswith("max="):
            peak = int(part[4:])
    if average is None or peak is None or not 0 <= average <= 511 or not 0 <= peak <= 511:
        raise ValueError("bad SCAL reply")
    return average, peak


def run_wait(ctx, command, args):
    fields = args.replace(",", " ").split()
    need = 3 if command == "WSND" else 5
    if len(fields) != need:
        raise ValueError(command + " has bad fields")
    profile_id = 0
    source = "default"
    if command == "WSND":
        threshold, minimum = int(fields[0]), int(fields[1])
    else:
        profile_id = int(fields[0])
        threshold, minimum = ctx.sound_profile(
            profile_id, fields[1], int(fields[2]), int(fields[3]))
        source = getattr(ctx.r, "sound_profile_source", "default")
    timeout = int(fields[-1])
    ctx.r.emit("EVT|SOUND|listen|source=%s|id=%d|threshold=%d|min=%d|timeout=%d" %
               (source, profile_id, threshold, minimum, timeout))
    heard = ctx.wait_sound(threshold, minimum, timeout)
    if heard is None:
        raise RuntimeError("route aborted")
    ctx.log(command.lower() + (" heard" if heard else " timeout - continue"))

def handle_buttons(owner, blue, yellow):
    if yellow == "long":
        finish(owner)
    elif blue == "up" and not owner.blue.long:
        select_next(owner)
    elif yellow == "up" and not owner.yellow.long:
        begin_sample(owner)
    tick(owner)


def resolve(owner, profile_id, binding, threshold, minimum):
    profile_id = int(profile_id)
    binding = str(binding).lower()
    owner.sound_bindings[profile_id] = (binding, int(threshold), int(minimum))
    if not owner.sound_profiles:
        owner.sound_profiles = _load(owner)
    item = owner.sound_profiles.get(str(profile_id))
    if item and item.get("binding") == binding:
        owner.sound_profile_source = "calibrated"
        return item["threshold"], item["minDurationMs"]
    if item and item.get("binding") != binding:
        _error(owner, "BINDING", "id=%d" % profile_id)
    owner.sound_profile_source = "default"
    return int(threshold), int(minimum)


def start(owner):
    if owner.controls.running or owner.calibrating or owner.sound_calibrating:
        _error(owner, "BUSY")
        return False
    owner.prepare_calibration_heap()
    bindings = _discover(owner)
    if not bindings:
        _error(owner, "NO-BINDING")
        return False
    owner.sound_profiles = _load(owner)
    owner.sound_calibration_id = 1 if 1 in bindings else min(bindings)
    owner.sound_calibration_phase = "ready"
    owner.sound_calibration_started = 0
    owner.sound_calibration_silence = 0
    owner.sound_calibration_peak = 0
    owner.sound_calibration_pending = {}
    owner.sound_calibrating = True
    _emit(owner, "mode=ready|id=%d|silence=3|sound=30" % owner.sound_calibration_id)
    try:
        owner.calibration_enter_tone()
        owner._cal_beep(660 if owner.sound_calibration_id == 1 else 880, 180)
    except Exception:
        pass
    return True


def select_next(owner):
    if not owner.sound_calibrating:
        return False
    if owner.sound_calibration_phase in ("silence", "sound"):
        _error(owner, "BUSY", "id=%d" % owner.sound_calibration_id)
        return False
    target = 2 if owner.sound_calibration_id == 1 else 1
    if target not in owner.sound_bindings:
        _error(owner, "NO-BINDING", "id=%d" % target)
        return False
    owner.sound_calibration_id = target
    owner.sound_calibration_phase = "ready"
    _emit(owner, "mode=ready|id=%d|pending=%d" %
          (target, len(owner.sound_calibration_pending or {})))
    try:
        owner._cal_beep(660 if target == 1 else 880, 180)
    except Exception:
        pass
    return True


def begin_sample(owner):
    if not owner.sound_calibrating:
        return False
    if owner.sound_calibration_phase in ("silence", "sound"):
        _error(owner, "BUSY", "id=%d" % owner.sound_calibration_id)
        return False
    if owner.sound_calibration_id not in owner.sound_bindings:
        _error(owner, "NO-BINDING", "id=%d" % owner.sound_calibration_id)
        return False
    owner.sound_calibration_silence = 0
    owner.sound_calibration_peak = 0
    owner.sound_calibration_started = time.monotonic()
    owner.sound_calibration_phase = "silence"
    _emit(owner, "mode=silence|id=%d|seconds=3" % owner.sound_calibration_id)
    try:
        owner._cal_beep(523, 90)
    except Exception:
        pass
    return True


def _complete(owner):
    profile_id = owner.sound_calibration_id
    silence = owner.sound_calibration_silence
    peak = owner.sound_calibration_peak
    separation = peak - silence
    if separation < _MIN_SEPARATION:
        owner.sound_calibration_phase = "ready"
        _error(owner, "NO-SEPARATION", "id=%d|silence=%d|peak=%d" %
               (profile_id, silence, peak))
        return False
    binding = owner.sound_bindings[profile_id][0]
    threshold = max(1, min(511, (silence + peak) // 2))
    pending = owner.sound_calibration_pending or {}
    pending[str(profile_id)] = {
        "binding": binding, "threshold": threshold,
        "minDurationMs": _MIN_DURATION_MS, "silenceMax": silence,
        "soundPeak": peak,
    }
    owner.sound_calibration_pending = pending
    owner.sound_calibration_phase = "complete"
    _emit(owner, "mode=complete|id=%d|threshold=%d|min=%d|silence=%d|peak=%d|pending=%d" %
          (profile_id, threshold, _MIN_DURATION_MS, silence, peak, len(pending)))
    try:
        owner.cal_stage_complete_tone()
    except Exception:
        pass
    return True


def tick(owner):
    if not owner.sound_calibrating:
        return
    phase = owner.sound_calibration_phase
    if phase not in ("silence", "sound"):
        return
    try:
        average, peak = _sample(owner)
    except Exception as exc:
        owner.sound_calibration_phase = "ready"
        _error(owner, "SENSOR", type(exc).__name__ + ":" + str(exc)[:48])
        return
    now = time.monotonic()
    if phase == "silence":
        owner.sound_calibration_silence = max(owner.sound_calibration_silence, peak, average)
        if now - owner.sound_calibration_started >= _SILENCE_SECONDS:
            owner.sound_calibration_phase = "sound"
            owner.sound_calibration_started = now
            _emit(owner, "mode=sound|id=%d|seconds=30|silence=%d" %
                  (owner.sound_calibration_id, owner.sound_calibration_silence))
            try:
                owner._cal_beep(988, 120)
            except Exception:
                pass
    else:
        owner.sound_calibration_peak = max(owner.sound_calibration_peak, peak, average)
        if now - owner.sound_calibration_started >= _TARGET_SECONDS:
            _complete(owner)


def finish(owner):
    if not owner.sound_calibrating:
        return False
    sampling = owner.sound_calibration_phase in ("silence", "sound")
    pending = owner.sound_calibration_pending or {}
    saved = 0
    if not sampling and pending:
        try:
            profiles = _load(owner)
            profiles.update(pending)
            _save(profiles)
            owner.sound_profiles = profiles
            saved = len(pending)
            _emit(owner, "mode=saved|count=%d" % saved)
            try:
                owner.cal_save_success_tone()
            except Exception:
                pass
        except Exception as exc:
            _error(owner, "SAVE", type(exc).__name__ + ":" + str(exc)[:48])
            return False
    owner.sound_calibrating = False
    owner.sound_calibration_phase = None
    owner.sound_calibration_pending = None
    _emit(owner, "mode=exited|saved=%d|discarded=%d" % (saved, 1 if sampling else 0))
    try:
        owner.calibration_exit_tone()
    except Exception:
        pass
    gc.collect()
    return True
