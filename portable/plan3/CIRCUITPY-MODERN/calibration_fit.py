# Lazy adaptive light-profile fitting. Imported only while publishing calibration.
PROFILE_IDS = (
    "desktop", "login-or-dc", "character-dashboard",
    "entering-game-loading", "game", "targeted",
)
SAFETY_GAP = 0.25
MINIMUM = 0.5


def _profile(value):
    return {
        "center": float(value["center"]),
        "tolerance": float(value["tolerance"]),
        "stable_ms": int(value.get("stable_ms", 750)),
    }


def prepare(profiles, profile_id, candidate):
    """Return (fitted_profiles, telemetry_events, blocked_profile)."""
    if profile_id not in PROFILE_IDS or not isinstance(profiles, dict):
        raise ValueError("profile")
    center = float(candidate["center"])
    requested = float(candidate["tolerance"])
    if requested < MINIMUM:
        event = ("ERR|CAL|FIT|id=%s|with=%s|reason=candidate-minimum|gap=%.3f" %
                 (profile_id, profile_id, SAFETY_GAP))
        return None, (event,), profile_id

    fitted = {}
    for pid, value in profiles.items():
        if isinstance(value, dict):
            fitted[pid] = _profile(value)
    fitted[profile_id] = {
        "center": center, "tolerance": requested,
        "stable_ms": int(candidate.get("stable_ms", 750)),
    }
    changes = []

    # Centres remain fixed. Shrink the candidate first, then the neighbour.
    # Shrinking cannot create an overlap already processed in this pass.
    for other_id in PROFILE_IDS:
        if other_id == profile_id or other_id not in fitted:
            continue
        new = fitted[profile_id]
        other = fitted[other_id]
        available = abs(new["center"] - other["center"]) - SAFETY_GAP
        excess = new["tolerance"] + other["tolerance"] - available
        if excess <= 0.000001:
            continue

        old_new = new["tolerance"]
        reduction = min(excess, old_new - MINIMUM)
        if reduction > 0:
            new["tolerance"] = old_new - reduction
            excess -= reduction

        old_other = other["tolerance"]
        # Never widen an old profile that is already below the modern minimum.
        other_floor = min(old_other, MINIMUM)
        reduction = min(excess, old_other - other_floor)
        if reduction > 0:
            other["tolerance"] = old_other - reduction
            excess -= reduction

        if excess > 0.000001:
            event = ("ERR|CAL|FIT|id=%s|with=%s|reason=centers-too-close|gap=%.3f" %
                     (profile_id, other_id, SAFETY_GAP))
            return None, (event,), other_id
        if abs(old_new - new["tolerance"]) > 0.000001:
            changes.append((profile_id, old_new, new["tolerance"], other_id))
        if abs(old_other - other["tolerance"]) > 0.000001:
            changes.append((other_id, old_other, other["tolerance"], profile_id))

    if not changes:
        return fitted, (), None
    neighbour = None
    for change in changes:
        if change[0] != profile_id:
            neighbour = change
            break
    applied = fitted[profile_id]["tolerance"]
    if neighbour is not None:
        event = ("EVT|CAL|FIT-PAIR|new=%s:%.3f->%.3f|adjusted=%s:%.3f->%.3f|gap=%.3f" %
                 (profile_id, requested, applied, neighbour[0],
                  neighbour[1], neighbour[2], SAFETY_GAP))
    else:
        limiter = changes[0][3]
        event = ("EVT|CAL|FIT|id=%s|requested=%.3f|applied=%.3f|limited-by=%s|gap=%.3f" %
                 (profile_id, requested, applied, limiter, SAFETY_GAP))
    return fitted, (event,), None
