# guard_calibration_protocol.py - pure CALGET/CALSET wire validation
# This module is CircuitPython-safe and intentionally has no board or filesystem dependency.
import math

PROFILE_IDS = (
    "desktop",
    "login-or-dc",
    "character-dashboard",
    "entering-game-loading",
    "game",
    "targeted",
)


def valid_revision(value):
    return (
        isinstance(value, str)
        and 0 < len(value) <= 80
        and "|" not in value
        and "\r" not in value
        and "\n" not in value
    )


def _number(value, label):
    try:
        parsed = float(value)
    except Exception:
        raise ValueError(label)
    if not math.isfinite(parsed) or parsed < 0:
        raise ValueError(label)
    return parsed


def _stable_ms(value):
    try:
        parsed = int(value, 10)
    except Exception:
        raise ValueError("stable_ms")
    if parsed < 0:
        raise ValueError("stable_ms")
    return parsed


def parse_calibration_set(line):
    """Return (payload, error) for the exact six-field CALSET command."""
    if not isinstance(line, str) or not line.startswith("CALSET|"):
        return None, "ARG"
    fields = line.split("|")
    if len(fields) != 6:
        return None, "ARG"
    revision, profile_id = fields[1], fields[2]
    if not valid_revision(revision):
        return None, "REVISION"
    if profile_id not in PROFILE_IDS:
        return None, "PROFILE"
    try:
        payload = {
            "revision": revision,
            "id": profile_id,
            "center": _number(fields[3], "center"),
            "tolerance": _number(fields[4], "tolerance"),
            "stable_ms": _stable_ms(fields[5]),
        }
    except ValueError:
        return None, "VALUE"
    return payload, None


def build_calibration_get(revision, count):
    if not valid_revision(revision):
        raise ValueError("revision")
    if not isinstance(count, int) or count < 0:
        raise ValueError("count")
    return "OK|CALGET|revision=%s|count=%d" % (revision, count)

# A candidate may touch another range at one boundary, but a positive-width
# intersection is unsafe. Existing overlaps are grandfathered only when the
# new sample does not increase them (the current targeted/login side-state
# layout predates this guard and must remain loadable).
CAL_OVERLAP_MARGIN_LUX = 0.0
CAL_FIT_SAFETY_GAP_LUX = 0.25
CAL_FIT_MIN_TOLERANCE_LUX = 0.5


def _overlap_width(left, right):
    lo = max(float(left["center"]) - float(left["tolerance"]),
             float(right["center"]) - float(right["tolerance"]))
    hi = min(float(left["center"]) + float(left["tolerance"]),
             float(right["center"]) + float(right["tolerance"]))
    return max(0.0, hi - lo)


def find_profile_overlap(profiles, profile_id, candidate, margin=CAL_OVERLAP_MARGIN_LUX):
    """Return {with,width} if candidate introduces/worsens an unsafe overlap."""
    if profile_id not in PROFILE_IDS or not isinstance(profiles, dict):
        raise ValueError("profile")
    current = profiles.get(profile_id)
    for other_id in PROFILE_IDS:
        if other_id == profile_id or other_id not in profiles:
            continue
        other = profiles[other_id]
        proposed = _overlap_width(candidate, other)
        previous = _overlap_width(current, other) if isinstance(current, dict) else 0.0
        if proposed > margin and proposed > previous + 0.000001:
            return {"with": other_id, "width": proposed}
    return None


def fit_calibration_profiles(profiles, profile_id, candidate,
                             safety_gap=CAL_FIT_SAFETY_GAP_LUX,
                             minimum=CAL_FIT_MIN_TOLERANCE_LUX):
    """Fit a candidate and, when necessary, neighbouring tolerances.

    Centres never move. The new candidate shrinks first; if its minimum still
    intersects an existing range, that neighbour then shrinks just enough.
    Return (profiles, fit) or (None, blocked). Existing profiles are never
    modified in-place.
    """
    if profile_id not in PROFILE_IDS or not isinstance(profiles, dict):
        raise ValueError("profile")
    center = _number(candidate.get("center"), "center")
    requested = _number(candidate.get("tolerance"), "tolerance")
    if requested < float(minimum):
        return None, {
            "with": profile_id, "reason": "candidate-minimum",
            "requested": requested, "minimum": float(minimum),
            "gap": float(safety_gap),
        }

    fitted = {}
    for pid, value in profiles.items():
        if not isinstance(value, dict):
            continue
        fitted[pid] = {
            "center": float(value["center"]),
            "tolerance": float(value["tolerance"]),
            "stable_ms": int(value.get("stable_ms", 750)),
        }
    fitted[profile_id] = {
        "center": center, "tolerance": requested,
        "stable_ms": int(candidate.get("stable_ms", 750)),
    }
    changed = []

    # Shrinking only can never create a new overlap with an already-processed
    # neighbour, so one deterministic pass is sufficient.
    for other_id in PROFILE_IDS:
        if other_id == profile_id or other_id not in fitted:
            continue
        new = fitted[profile_id]
        other = fitted[other_id]
        available = (abs(float(new["center"]) - float(other["center"]))
                     - float(safety_gap))
        excess = (float(new["tolerance"]) + float(other["tolerance"])
                  - available)
        if excess <= 0.000001:
            continue

        old_new = float(new["tolerance"])
        reduce_new = min(excess, old_new - float(minimum))
        if reduce_new > 0:
            new["tolerance"] = old_new - reduce_new
            excess -= reduce_new

        old_other = float(other["tolerance"])
        # Never enlarge a legacy profile that is already narrower than the
        # modern minimum; its current width is its own lower bound.
        other_floor = min(old_other, float(minimum))
        reduce_other = min(excess, old_other - other_floor)
        if reduce_other > 0:
            other["tolerance"] = old_other - reduce_other
            excess -= reduce_other

        if excess > 0.000001:
            return None, {
                "with": other_id, "reason": "centers-too-close",
                "requested": requested, "minimum": float(minimum),
                "available": max(0.0, available),
                "gap": float(safety_gap),
            }
        if abs(old_new - float(new["tolerance"])) > 0.000001:
            changed.append({
                "id": profile_id, "old": old_new,
                "new": float(new["tolerance"]), "with": other_id,
            })
        if abs(old_other - float(other["tolerance"])) > 0.000001:
            changed.append({
                "id": other_id, "old": old_other,
                "new": float(other["tolerance"]), "with": profile_id,
            })

    return fitted, ({
        "requested": requested,
        "applied": float(fitted[profile_id]["tolerance"]),
        "gap": float(safety_gap),
        "changes": changed,
    } if changed else None)


def calibrated_profile(values, profiles, profile_id, stable_ms=750):
    """Build an asymmetric-safe requested profile.

    The publish boundary performs the atomic one/two-sided neighbour fit so
    physical calibration and CALSET share one policy and one telemetry path.
    """
    if profile_id not in PROFILE_IDS or not isinstance(profiles, dict):
        raise ValueError("profile")
    clean = sorted(_number(value, "sample") for value in values)
    if len(clean) < 5:
        raise ValueError("samples")
    count = len(clean)
    center = clean[count // 2]
    # Use a trimmed envelope so isolated sensor spikes cannot inflate the
    # profile, but measure each side from the median independently. The old
    # half-width formula assumed the median was exactly midway between P10 and
    # P90. A dashboard sample clustered at 13.3 with a repeated 15.8 upper
    # state therefore saved tolerance=1.0 and immediately became unknown.
    low = clean[int((count - 1) * 0.05)]
    high = clean[int((count - 1) * 0.95)]
    deviation = max(center - low, high - center)
    # Keep one lux of post-sample drift headroom. Hardware showed a Dashboard
    # envelope ending at 15.8 lux later settling at 16.7; the old 0.5 margin
    # missed that state by 0.4 lux even though adjacent profiles still left
    # ample room. The atomic publish fit remains authoritative.
    tolerance = max(1.0, deviation + 1.0)
    return {
        "center": center,
        "tolerance": max(0.25, tolerance),
        "stable_ms": int(stable_ms),
    }
