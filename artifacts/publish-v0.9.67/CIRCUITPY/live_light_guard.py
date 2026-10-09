# live_light_guard.py - portable BH1750 state guard (runtime v3)
# Debounces light samples and applies the canonical Phase 7 GuardTransition policy.
# The bundle loader below is deliberately independent from Classroom Studio and RunEngine.

import gc
import hashlib
import json
import math
import os

try:
    from guard_transition import GuardTransition, PROFILE_TO_ROUTE
except ImportError:  # keep the generic legacy light guard usable by older bundles
    GuardTransition = None
    PROFILE_TO_ROUTE = {}


PROFILE_IDS = (
    "desktop",
    "login-or-dc",
    "character-dashboard",
    "entering-game-loading",
    "game",
    "targeted",
)
ROUTE_FILES = {
    "Desktop": "desktop_steps.txt",
    "Restart": "restart_steps.txt",
    "LoginOrDc": "login_or_dc_steps.txt",
    "Dc": "dc_steps.txt",
    "CharacterDashboard": "character_dashboard_steps.txt",
    "EnteringGameLoading": "entering_game_loading_steps.txt",
    "Game": "game_steps.txt",
    "Targeted": "targeted_steps.txt",
    "Resumable": "resumable_steps.txt",
}
REQUIRED_BUNDLE_FILES = (
    "code.py",
    "boot.py",
    "plan.txt",
    "plan_engine.py",
    "live_light_guard.py",
    "guard_transition.py",
    "guard_calibration_protocol.py",
    "error_policy.py",
    "combined_guard_runtime.py",
    "SHA256SUMS.txt",
)
HASHED_BUNDLE_FILES = tuple(
    filename for filename in REQUIRED_BUNDLE_FILES if filename != "SHA256SUMS.txt"
) + tuple(ROUTE_FILES.values()) + ("guard-transition.json", "guard-calibration.json")


class GuardBundleError(ValueError):
    pass


def _read_json(root, name):
    try:
        with open(os.path.join(root, name), "r") as fh:
            return json.load(fh)
    except Exception as exc:
        raise GuardBundleError("cannot read " + name) from exc


def _finite_nonnegative(value, label):
    if not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
        raise GuardBundleError("invalid " + label)
    return float(value)


def _file_sha256(root, name):
    # RP2040/CircuitPython can have only a few KB of contiguous heap left
    # after the runtime and plan engine are loaded. Keep the read buffer small
    # and collect before each file so bundle verification cannot fail merely
    # because a previous file left a fragmented temporary allocation.
    gc.collect()
    digest = hashlib.sha256()
    try:
        with open(os.path.join(root, name), "rb") as fh:
            while True:
                chunk = fh.read(256)
                if not chunk:
                    break
                digest.update(chunk)
    except Exception as exc:
        raise GuardBundleError("cannot hash Guard file: " + name) from exc
    finally:
        gc.collect()
    return digest.hexdigest().lower()


def _verify_hash_manifest(root):
    expected = set(HASHED_BUNDLE_FILES)
    seen = {}
    try:
        with open(os.path.join(root, "SHA256SUMS.txt"), "r") as fh:
            for raw in fh:
                fields = raw.strip().split()
                if not fields:
                    continue
                if len(fields) != 2 or len(fields[0]) != 64 \
                        or any(character not in "0123456789abcdefABCDEF" for character in fields[0]):
                    raise GuardBundleError("invalid SHA256SUMS entry")
                digest, name = fields[0].lower(), fields[1]
                if name not in expected or name in seen:
                    raise GuardBundleError("unexpected or duplicate SHA256SUMS file: " + name)
                seen[name] = digest
    except GuardBundleError:
        raise
    except Exception as exc:
        raise GuardBundleError("cannot read SHA256SUMS.txt") from exc

    if set(seen) != expected:
        missing = sorted(expected - set(seen))
        extra = sorted(set(seen) - expected)
        detail = "missing=" + ",".join(missing)
        if extra:
            detail += "; extra=" + ",".join(extra)
        raise GuardBundleError("SHA256SUMS file set mismatch: " + detail)
    for name, expected_digest in seen.items():
        if _file_sha256(root, name) != expected_digest:
            raise GuardBundleError("SHA256SUMS hash mismatch: " + name)


def load_guard_bundle(root="/"):
    """Load and fail closed on the exported transition/calibration contract.

    The manifest and calibration revision must agree, every exported runtime and
    route file must be present and hash-verified, and the six optical profiles must
    be unique and numerically valid. This pure filesystem check lets the Pico reject
    a stale or partial CIRCUITPY copy before STATELOOP executes a route.
    """
    manifest = _read_json(root, "guard-transition.json")
    calibration = _read_json(root, "guard-calibration.json")
    if manifest.get("format") != 1 or manifest.get("runtime") != "combined-pico-guard-executor":
        raise GuardBundleError("unsupported Guard manifest")
    if calibration.get("format") != 1:
        raise GuardBundleError("unsupported Guard calibration")

    for filename in REQUIRED_BUNDLE_FILES:
        if not os.path.isfile(os.path.join(root, filename)):
            raise GuardBundleError("missing Guard runtime file: " + filename)
    _verify_hash_manifest(root)

    routes = manifest.get("routes")
    if routes != ROUTE_FILES:
        raise GuardBundleError("Guard route map is incomplete or changed")
    manifest_revision = manifest.get("calibrationRevision")
    calibration_revision = calibration.get("revision")
    if not isinstance(manifest_revision, str) or not manifest_revision:
        raise GuardBundleError("Guard manifest has no calibration revision")
    if manifest_revision != calibration_revision:
        raise GuardBundleError("Guard calibration revision mismatch")

    raw_profiles = manifest.get("profiles")
    if (not isinstance(raw_profiles, list)
            or len(raw_profiles) != len(PROFILE_IDS)
            or any(not isinstance(item, dict) for item in raw_profiles)
            or {item.get("id") for item in raw_profiles} != set(PROFILE_IDS)):
        raise GuardBundleError("Guard manifest needs exactly six unique optical profiles")
    calibration_profiles = calibration.get("profiles")
    if not isinstance(calibration_profiles, dict) or set(calibration_profiles) != set(PROFILE_IDS):
        raise GuardBundleError("Guard calibration needs exactly six optical profiles")

    states = []
    for item in raw_profiles:
        pid = item.get("id")
        center = _finite_nonnegative(item.get("center"), pid + " center")
        tolerance = _finite_nonnegative(item.get("tolerance"), pid + " tolerance")
        stable_ms = int(_finite_nonnegative(item.get("stableMs"), pid + " stableMs"))
        cal = calibration_profiles[pid]
        if abs(center - _finite_nonnegative(cal.get("center"), pid + " calibration center")) > 1e-9 \
                or abs(tolerance - _finite_nonnegative(cal.get("tolerance"), pid + " calibration tolerance")) > 1e-9 \
                or stable_ms != int(_finite_nonnegative(cal.get("stable_ms"), pid + " calibration stable_ms")):
            raise GuardBundleError("Guard manifest/calibration profile mismatch: " + pid)
        route = PROFILE_TO_ROUTE.get(pid)
        if route is None:
            raise GuardBundleError("Guard profile has no runtime route: " + pid)
        route_path = os.path.join(root, route)
        if not os.path.isfile(route_path):
            raise GuardBundleError("missing Guard route file: " + route)
        states.append(state_spec(pid, int(math.floor(max(0, center - tolerance))),
                                 int(math.ceil(center + tolerance)), route))

    # Resumable is not an optical profile but remains a required exported route.
    if not os.path.isfile(os.path.join(root, ROUTE_FILES["Resumable"])):
        raise GuardBundleError("missing Guard route file: " + ROUTE_FILES["Resumable"])
    return {
        "manifest": manifest,
        "calibration": calibration,
        "revision": manifest_revision,
        "states": states,
        "stable_ms": max((int(s.get("stableMs", 0)) for s in raw_profiles), default=750),
        "hysteresis": 1,
        "sensor_timeout_ms": 1500,
    }


class LightStateGuard:
    """Debounced, hysteretic light-state selector with optional ordered routing."""

    @classmethod
    def from_bundle(cls, root="/"):
        bundle = load_guard_bundle(root)
        guard = cls(bundle["states"], bundle["stable_ms"], bundle["hysteresis"], bundle["sensor_timeout_ms"])
        guard.bundle = bundle
        return guard

    def __init__(self, states, stable_ms=750, hysteresis=0, sensor_timeout_ms=1500):
        self.states = tuple(states or ())
        self.stable_ms = max(0, int(stable_ms))
        self.hysteresis = max(0, int(hysteresis))
        self.sensor_timeout_ms = max(0, int(sensor_timeout_ms))
        self.active = None
        self.candidate = None
        self.candidate_since = None
        self.last_sample_ms = None
        self.last_decision = None
        self.bundle = None
        ids = {str(state.get("id")) for state in self.states}
        self.transition = (
            GuardTransition()
            if GuardTransition is not None and set(PROFILE_TO_ROUTE).issubset(ids)
            else None
        )

    def _inside(self, state, lux, widened=False):
        low = int(state["lo"])
        high = int(state["hi"])
        if widened:
            low -= self.hysteresis
            high += self.hysteresis
        return low <= lux <= high

    def _eligible(self, lux):
        if lux is None:
            return []
        return [state for state in self.states if self._inside(state, lux, False)]

    def _active_still_valid(self, lux):
        if lux is None or self.active is None:
            return False
        return any(state["id"] == self.active and self._inside(state, lux, True)
                   for state in self.states)

    def _policy_state(self, state_id):
        if self.transition is None:
            self.last_decision = None
            return True
        decision = self.transition.observe(state_id)
        self.last_decision = decision
        # A denied transition is unsafe. Targeted -> Game is intentionally a
        # non-executing decision with a valid route, so it remains accepted.
        return bool(decision.get("execute") or decision.get("route"))

    def update(self, lux, now_ms):
        """Return active id, or None when the sensor/state is unsafe."""
        now_ms = int(now_ms)
        self.last_sample_ms = now_ms if lux is not None else self.last_sample_ms

        if lux is None:
            if self.last_sample_ms is None:
                return None
            if now_ms - self.last_sample_ms > self.sensor_timeout_ms:
                return None
            return self.active

        if self.active is not None and self._active_still_valid(lux):
            eligible = self._eligible(lux)
            if len(eligible) == 1 and eligible[0]["id"] == self.active:
                self.candidate = None
                self.candidate_since = None
                return self.active

        eligible = self._eligible(lux)
        if len(eligible) != 1:
            self.candidate = None
            self.candidate_since = None
            if self.active is not None and self._active_still_valid(lux):
                return self.active
            if self.transition is not None:
                self.transition.observe_unknown()
                self.last_decision = None
            self.active = None
            return None

        state_id = eligible[0]["id"]
        if state_id != self.candidate:
            self.candidate = state_id
            self.candidate_since = now_ms

        if self.candidate_since is not None and now_ms - self.candidate_since >= self.stable_ms:
            if not self._policy_state(state_id):
                self.active = None
                self.candidate = None
                self.candidate_since = None
                return None
            self.active = state_id
            self.candidate = None
            self.candidate_since = None
            return self.active

        # Keep the current route while a new candidate is being debounced.
        return self.active if self.active is not None else None

    def reset(self):
        self.active = None
        self.candidate = None
        self.candidate_since = None
        self.last_sample_ms = None
        self.last_decision = None
        self.bundle = None
        if self.transition is not None:
            self.transition.reset()


def state_spec(state_id, low, high, route):
    return {"id": str(state_id), "lo": int(low), "hi": int(high), "route": str(route)}
