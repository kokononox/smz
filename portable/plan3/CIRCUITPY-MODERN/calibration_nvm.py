"""Compact, checksummed physical-calibration store outside the CIRCUITPY FAT."""
import json

MAGIC = b"CAL2"
LEGACY_MAGIC = b"CAL1"
BASE = 1536
HEADER = 8


def _sum16(data):
    value = 0
    for byte in data:
        value = (value + byte) & 0xFFFF
    return value


def _limit(nvm):
    return len(nvm) - 16 if nvm is not None else 0


def clear(nvm):
    end = _limit(nvm)
    if end >= BASE + HEADER:
        # Flash-backed NVM must be cleared in one transaction, not per byte.
        nvm[BASE:end] = b"\x00" * (end - BASE)


def load(nvm, base_revision):
    end = _limit(nvm)
    if end < BASE + HEADER:
        return None
    try:
        magic = bytes(nvm[BASE:BASE + 4])
        if magic not in (MAGIC, LEGACY_MAGIC):
            return None
        size = int(nvm[BASE + 4]) | (int(nvm[BASE + 5]) << 8)
        expected = int(nvm[BASE + 6]) | (int(nvm[BASE + 7]) << 8)
        if size < 2 or BASE + HEADER + size > end:
            return None
        payload = bytes(nvm[BASE + HEADER:BASE + HEADER + size])
        if _sum16(payload) != expected:
            return None
        data = json.loads(payload.decode("utf-8"))
        if not isinstance(data.get("profiles"), dict):
            return None
        # Route-only exports keep the same calibration revision and therefore
        # preserve physical calibration. Editing Classroom light profiles makes
        # a new revision authoritative and retires the old NVM override.
        stored_revision = (data.get("base_revision") if magic == MAGIC
                           else data.get("base"))
        if stored_revision != base_revision:
            return None
        return data["profiles"]
    except Exception:
        return None


def save(nvm, base_revision, profiles):
    end = _limit(nvm)
    if end < BASE + HEADER:
        raise RuntimeError("calibration NVM unavailable")
    payload = json.dumps({"schema": 2, "base_revision": base_revision, "profiles": profiles}, separators=(",", ":")).encode("utf-8")
    if BASE + HEADER + len(payload) > end:
        raise RuntimeError("calibration NVM full")
    checksum = _sum16(payload)
    size = len(payload)
    body = bytes((size & 0xFF, (size >> 8) & 0xFF,
                  checksum & 0xFF, (checksum >> 8) & 0xFF)) + payload
    # Invalidate, write body, then publish MAGIC: three flash transactions.
    nvm[BASE:BASE + 4] = b"\x00\x00\x00\x00"
    nvm[BASE + 4:BASE + 4 + len(body)] = body
    nvm[BASE:BASE + 4] = MAGIC
    if load(nvm, base_revision) is None:
        raise RuntimeError("calibration NVM verification failed")


FIT_GAP = 0.25
FIT_MIN = 0.5
FIT_IDS = ("desktop", "login-or-dc", "character-dashboard",
           "entering-game-loading", "game", "targeted")

def fit_profiles(src, pid, candidate):
    """Fixed centres: shrink candidate first, then neighbour; return events."""
    requested = float(candidate["tolerance"]); center = float(candidate["center"])
    if pid not in FIT_IDS or requested < FIT_MIN:
        event = "ERR|CAL|FIT|id=%s|with=%s|reason=candidate-minimum|gap=%.3f" % (pid, pid, FIT_GAP)
        return None, (event,), pid
    out = {}
    for key, value in src.items():
        if isinstance(value, dict):
            out[key] = {"center": float(value["center"]),
                        "tolerance": float(value["tolerance"]),
                        "stable_ms": int(value.get("stable_ms", 750))}
    out[pid] = {"center": center, "tolerance": requested,
                "stable_ms": int(candidate.get("stable_ms", 750))}
    changes = []
    for other_id in FIT_IDS:
        if other_id == pid or other_id not in out: continue
        new = out[pid]; other = out[other_id]
        apart = abs(center - other["center"])
        if apart <= 0.000001:
            event = "ERR|CAL|FIT|id=%s|with=%s|reason=centers-identical|gap=%.3f" % (pid, other_id, FIT_GAP)
            return None, (event,), other_id
        excess = new["tolerance"] + other["tolerance"] - (apart - FIT_GAP)
        if excess <= 0.000001: continue
        old_new = new["tolerance"]; take = min(excess, old_new - FIT_MIN)
        if take > 0: new["tolerance"] -= take; excess -= take
        old_other = other["tolerance"]; floor = min(old_other, FIT_MIN)
        take = min(excess, old_other - floor)
        if take > 0: other["tolerance"] -= take; excess -= take
        if excess > 0.000001:
            event = "ERR|CAL|FIT|id=%s|with=%s|reason=centers-too-close|gap=%.3f" % (pid, other_id, FIT_GAP)
            return None, (event,), other_id
        if old_new != new["tolerance"]: changes.append((pid, old_new, new["tolerance"], other_id))
        if old_other != other["tolerance"]: changes.append((other_id, old_other, other["tolerance"], pid))
    if not changes: return out, (), None
    neighbour = next((x for x in changes if x[0] != pid), None)
    applied = out[pid]["tolerance"]
    if neighbour:
        event = "EVT|CAL|FIT-PAIR|new=%s:%.3f->%.3f|adjusted=%s:%.3f->%.3f|gap=%.3f" % (pid, requested, applied, neighbour[0], neighbour[1], neighbour[2], FIT_GAP)
    else:
        event = "EVT|CAL|FIT|id=%s|requested=%.3f|applied=%.3f|limited-by=%s|gap=%.3f" % (pid, requested, applied, changes[0][3], FIT_GAP)
    return out, (event,), None


def apply(bundle, profiles):
    if not isinstance(profiles, dict):
        return bundle
    calibration = bundle.get("calibration", {}).get("profiles", {})
    manifest_profiles = bundle.get("manifest", {}).get("profiles", [])
    states = bundle.get("states", [])
    for profile_id, value in profiles.items():
        if profile_id not in calibration or not isinstance(value, dict):
            continue
        center = float(value["center"])
        tolerance = float(value["tolerance"])
        stable_ms = int(value["stable_ms"])
        calibration[profile_id] = {
            "center": center, "tolerance": tolerance, "stable_ms": stable_ms}
        for item in manifest_profiles:
            if item.get("id") == profile_id:
                item["center"] = center; item["tolerance"] = tolerance; item["stableMs"] = stable_ms
        for state in states:
            if state.get("id") == profile_id:
                state["lo"] = int(max(0, center - tolerance))
                high = center + tolerance
                state["hi"] = int(high) if high == int(high) else int(high) + 1
    bundle["stable_ms"] = max((int(item.get("stableMs", 0)) for item in manifest_profiles), default=750)
    return bundle
