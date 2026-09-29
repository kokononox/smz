"""Natural Mouse implementation loaded only at the first RMOUSE."""
import gc
import math
import random

_core = None

def bind(core):
    global _core
    _core = core

def _mouse_events(pos, tx, ty, cfg, pauses):
    sx, sy = pos[0], pos[1]
    dx, dy = tx - sx, ty - sy
    span = max(abs(dx), abs(dy))
    cmin, cmax = cfg["curve_min"], cfg["curve_max"]
    if cmax < cmin:
        cmin, cmax = cmax, cmin
    amp = min(span // 3, (span * _core.rand_range(int(cmin), int(cmax))) // 100)
    if _core._below(2) == 0:
        amp = -amp
    denom = max(1, span)
    pxoff, pyoff = (-dy * amp) // denom, (dx * amp) // denom
    path_dist = math.sqrt(dx * dx + dy * dy)
    if cfg["mt_max"] > 0:
        total = _core.distance_scaled_move_ms(cfg["mt_min"], cfg["mt_max"], path_dist)
    elif cfg["speed_max"] > 0:
        speed = _core.rand_range(max(1, cfg["speed_min"]),
                           max(max(1, cfg["speed_min"]), cfg["speed_max"]))
        path = max(abs(dx), abs(dy)) + min(abs(dx), abs(dy)) // 2
        total = max(0, (path * 1000) // speed - path)
    else:
        total = 0
    spatial = max(8, (span + 11) // 12)
    timed = (total + 7) // 8 if total > 0 else spatial
    segments = max(8, min(128, max(spatial, timed)))
    base, extra = total // segments, total % segments
    mid = pauses.mid_pause(cfg) if path_dist >= 300 else 0
    mid_at = 1 + _core._below(max(1, segments - 1))
    phase_skew = _core.rand_range(-220, 220)
    if cfg["before_max"] > 0:
        yield ("wait", _core.rand_range(cfg["before_min"], cfg["before_max"]), 0, 0)
    px, py = sx, sy
    for step in range(1, segments + 1):
        t = (step * 1024) // segments
        q = int(_core._clamp(t + (phase_skew * t * (1024 - t)) // 1024000, 0, 1024))
        ease = (q * q * (3072 - 2 * q)) // 1048576
        bow = (4 * t * (1024 - t)) // 1024
        nx = sx + (dx * ease + pxoff * bow) // 1024
        ny = sy + (dy * ease + pyoff * bow) // 1024
        if step == segments:
            nx, ny = tx, ty
        delay = base + (1 if step <= extra else 0)
        if mid and step == mid_at:
            delay += mid
        yield ("move", delay, nx - px, ny - py)
        px, py = nx, ny
        pos[0], pos[1] = px, py
    if cfg["after_max"] > 0:
        yield ("wait", _core.rand_range(cfg["after_min"], cfg["after_max"]), 0, 0)
    long_pause = pauses.roll_long(cfg)
    if long_pause:
        yield ("wait", long_pause, 0, 0)


def mouse_events(args, ctx, pauses, pos, route_speed):
    prm = _core._fields(args)
    if "region" not in prm:
        raise ValueError("RMOUSE needs region")
    region = tuple(int(v) for v in prm["region"].split(","))
    if len(region) != 4:
        raise ValueError("RMOUSE region needs x,y,w,h")
    cfg = dict(_core._DEFAULT_CFG)
    cfg["speed_min"], cfg["speed_max"] = route_speed
    for key in ("speed", "mt", "curve", "before", "after"):
        if key in prm:
            a, b = _core._pair(prm[key])
            if key == "speed": cfg["speed_min"], cfg["speed_max"] = a, b
            elif key == "mt": cfg["mt_min"], cfg["mt_max"] = a, b
            elif key == "curve": cfg["curve_min"], cfg["curve_max"] = a, b
            elif key == "before": cfg["before_min"], cfg["before_max"] = a, b
            else: cfg["after_min"], cfg["after_max"] = a, b
    if "mid" in prm:
        chance, ranges = prm["mid"].split(":", 1)
        cfg["mid_chance"] = int(chance)
        cfg["mid_min"], cfg["mid_max"] = _core._pair(ranges)
    if "idle" in prm:
        every, pause = prm["idle"].split(":", 1)
        cfg["idle_every_min"], cfg["idle_every_max"] = _core._pair(every)
        cfg["idle_pause_min"], cfg["idle_pause_max"] = _core._pair(pause)
    if "over" in prm:
        cfg["over_chance"] = int(prm["over"])
    rw, rh = region[2], region[3]
    sx, sy = ctx.screen_w // 2, ctx.screen_h // 2
    xlim = max(1, min(max(1, rw - 1), max(32, ctx.screen_w // 4)))
    ylim = max(1, min(max(1, rh - 1), max(32, ctx.screen_h // 4)))
    xmag = random.randint(max(1, xlim // 3), xlim)
    ymag = random.randint(max(1, ylim // 3), ylim)
    tx = sx + (xmag if random.randint(0, 1) else -xmag)
    ty = sy + (ymag if random.randint(0, 1) else -ymag)
    pos[0], pos[1] = sx, sy
    # Do not wrap the movement generator. Game's cooperative scheduler already
    # owns the outer generator; another forwarding frame can exhaust the Pico
    # pystack after Catch/Resume.
    return _mouse_events(pos, tx, ty, cfg, pauses)


def run_rmouse(args, ctx, pauses, pos, route_speed):
    for event, delay, dx, dy in mouse_events(args, ctx, pauses, pos, route_speed):
        if event == "move" and (dx or dy):
            ctx.mmove_relative(dx, dy)
        if delay and not ctx.sleep_ms(delay):
            raise RuntimeError("route aborted")
    gc.collect()
