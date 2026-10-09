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
