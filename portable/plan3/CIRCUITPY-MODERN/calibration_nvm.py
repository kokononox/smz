"""Compact, checksummed physical-calibration store outside the CIRCUITPY FAT."""
import json

MAGIC = b"CAL1"
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
        for index in range(BASE, end):
            nvm[index] = 0


def load(nvm, base_revision):
    end = _limit(nvm)
    if end < BASE + HEADER:
        return None
    try:
        if bytes(nvm[BASE:BASE + 4]) != MAGIC:
            return None
        size = int(nvm[BASE + 4]) | (int(nvm[BASE + 5]) << 8)
        expected = int(nvm[BASE + 6]) | (int(nvm[BASE + 7]) << 8)
        if size < 2 or BASE + HEADER + size > end:
            return None
        payload = bytes(nvm[BASE + HEADER:BASE + HEADER + size])
        if _sum16(payload) != expected:
            return None
        data = json.loads(payload.decode("utf-8"))
        if data.get("base") != base_revision or not isinstance(data.get("profiles"), dict):
            return None
        return data["profiles"]
    except Exception:
        return None


def save(nvm, base_revision, profiles):
    end = _limit(nvm)
    if end < BASE + HEADER:
        raise RuntimeError("calibration NVM unavailable")
    payload = json.dumps({"base": base_revision, "profiles": profiles}, separators=(",", ":")).encode("utf-8")
    if BASE + HEADER + len(payload) > end:
        raise RuntimeError("calibration NVM full")
    clear(nvm)
    checksum = _sum16(payload)
    nvm[BASE:BASE + 4] = MAGIC
    nvm[BASE + 4] = len(payload) & 0xFF
    nvm[BASE + 5] = (len(payload) >> 8) & 0xFF
    nvm[BASE + 6] = checksum & 0xFF
    nvm[BASE + 7] = (checksum >> 8) & 0xFF
    nvm[BASE + HEADER:BASE + HEADER + len(payload)] = payload
    if load(nvm, base_revision) is None:
        raise RuntimeError("calibration NVM verification failed")


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
