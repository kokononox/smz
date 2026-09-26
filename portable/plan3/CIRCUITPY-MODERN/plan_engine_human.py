import math
import random
from plan_engine_parse import _below, _clamp, _rf, rand_range, pct_dec

def tuned_wind(dist, curve):
    cv = 0.30 if curve < 0 else _clamp(curve, 0.0, 2.0)
    base = 0.3 + cv * 3.0 if cv < 1.0 else 3.3 + (cv - 1.0) * 4.7
    return base * _clamp(dist / 400.0, 0.35, 1.0)


def build_range_profile(mn, mx, knot_count, low_ends=False):
    if mx < mn:
        mn, mx = mx, mn
    knot_count = int(_clamp(knot_count, 2, 12))
    if abs(mx - mn) < 1e-9:
        return [float(mn)] * knot_count
    span = mx - mn
    knots = []
    upper = _below(2) == 1
    for i in range(knot_count):
        force_low = low_ends and (i == 0 or i == knot_count - 1)
        band = _rf() * 0.18 if force_low else (0.60 + _rf() * 0.40 if upper else _rf() * 0.40)
        knots.append(mn + span * band)
        upper = not upper
    return knots


def sample_profile(knots, t):
    if not knots:
        return 0.0
    if len(knots) == 1:
        return knots[0]
    p = _clamp(t, 0.0, 1.0) * (len(knots) - 1)
    i = min(len(knots) - 2, int(math.floor(p)))
    u = p - i
    smooth = u * u * (3.0 - 2.0 * u)          # C1 smoothstep
    return knots[i] + (knots[i + 1] - knots[i]) * smooth


def _poly_len(pts):
    total = 0.0
    for i in range(1, len(pts)):
        total += math.sqrt((pts[i][0] - pts[i - 1][0]) ** 2 + (pts[i][1] - pts[i - 1][1]) ** 2)
    return total


def windmouse(sx, sy, tx, ty, wind, gravity, curve, profile):
    sqrt3, sqrt5 = math.sqrt(3.0), math.sqrt(5.0)
    x, y = float(sx), float(sy)
    vx = vy = wx = wy = 0.0
    dist0 = max(1.0, math.sqrt((tx - sx) ** 2 + (ty - sy) ** 2))
    if dist0 < 3:
        return [[tx, ty, 0]]
    max_step = _clamp(dist0 / 15.0, 6.0, 30.0)
    stop_radius = min(8.0, max(2.0, dist0 * 0.02))
    pts = []
    guard = 0
    while guard < 2000:
        guard += 1
        dist = math.sqrt((tx - x) ** 2 + (ty - y) ** 2)
        if dist < stop_radius:
            break
        live_wind = wind
        if profile:
            progress = _clamp(1.0 - dist / dist0, 0.0, 1.0)
            live_wind = tuned_wind(dist0, sample_profile(profile, progress) / 100.0)
        wmag = min(live_wind, dist)
        if dist >= stop_radius * 4:             # far: wind roams; near: wind calms
            wx = wx / sqrt3 + (2 * _rf() - 1) * wmag / sqrt5
            wy = wy / sqrt3 + (2 * _rf() - 1) * wmag / sqrt5
        else:
            wx /= sqrt3
            wy /= sqrt3
            max_step = max(2.5, max_step / sqrt5)   # decelerate on approach
        vx += wx + gravity * (tx - x) / dist
        vy += wy + gravity * (ty - y) / dist
        vmag = math.sqrt(vx * vx + vy * vy)
        if vmag > max_step:
            vx = vx / vmag * max_step
            vy = vy / vmag * max_step
        x += vx
        y += vy
        pts.append([int(round(x)), int(round(y)), 0])
    pts.append([tx, ty, 0])                     # land exactly on the target
    return pts


def resample_variable(spine, mn_sp, mx_sp):
    if not spine:
        return []
    if len(spine) == 1:
        return [list(spine[0])]
    if mx_sp < mn_sp:
        mn_sp, mx_sp = mx_sp, mn_sp
    mn_sp = max(0.5, mn_sp)
    mx_sp = max(mn_sp, mx_sp)
    total = _poly_len(spine)
    if total < 1e-6:
        return [list(spine[-1])]
    profile = build_range_profile(mn_sp, mx_sp, 5)
    outp = []
    next_at = sample_profile(profile, 0.0)
    acc = 0.0
    for i in range(1, len(spine)):
        x0, y0 = spine[i - 1][0], spine[i - 1][1]
        seg = math.sqrt((spine[i][0] - x0) ** 2 + (spine[i][1] - y0) ** 2)
        if seg < 1e-6:
            continue
        while acc + seg >= next_at:
            u = (next_at - acc) / seg
            outp.append([int(round(x0 + (spine[i][0] - x0) * u)),
                         int(round(y0 + (spine[i][1] - y0) * u)), 0])
            phase = _clamp(next_at / total, 0.0, 1.0)
            next_at += max(0.5, sample_profile(profile, phase))
        acc += seg
    last = spine[-1]
    if not outp or outp[-1][0] != last[0] or outp[-1][1] != last[1]:
        outp.append([last[0], last[1], 0])
    return outp


def assign_dynamic_delays(pts, sx, sy, smin, smax, target_ms=0):
    """v0.9.8/10 port: per-point delay from a smooth speed profile (low at both ends,
    alternating interior knots), normalized to target_ms when the step set a duration."""
    for p in pts:
        p[2] = 0
    if not pts or (smax <= 0 and target_ms <= 0):
        return
    shape_only = smax <= 0
    lo = 150 if shape_only else max(150, min(smin, smax))
    hi = 2000 if shape_only else max(lo, max(smin, smax))
    knot_count = int(_clamp(4 + len(pts) // 120, 4, 9))
    prof = build_range_profile(float(lo), float(hi), knot_count, low_ends=True)
    phase_skew = _rf() * 0.44 - 0.22
    segs = []
    path = 0.0
    px, py = sx, sy
    for p in pts:
        s = math.sqrt((p[0] - px) ** 2 + (p[1] - py) ** 2)
        segs.append(s)
        path += s
        px, py = p[0], p[1]
    if path < 1e-6:
        return
    weights = []
    wsum = 0.0
    travelled = 0.0
    for i in range(len(pts)):
        phase = _clamp((travelled + segs[i] * 0.5) / path, 0.0, 1.0)
        phase = _clamp(phase + phase_skew * phase * (1.0 - phase), 0.0, 1.0)
        speed = _clamp(sample_profile(prof, phase), lo, hi)
        w = segs[i] * 1000.0 / max(1.0, speed)
        weights.append(w)
        wsum += w
        travelled += segs[i]
    if wsum <= 0:
        return
    scale = target_ms / wsum if target_ms > 0 else 1.0
    for i in range(len(pts)):
        pts[i][2] = max(1, int(round(weights[i] * scale)))


def curve_height_ratio(pct):
    p = _clamp(float(pct), 0.0, 200.0)
    if p <= 100.0:
        return 0.08 * ((p / 100.0) ** 1.15)
    return 0.08 + 0.42 * math.sqrt((p - 100.0) / 100.0)


def _ray_to_edge(x, y, dx, dy, w, h, margin):
    max_x = max(margin, w - 1.0 - margin)
    max_y = max(margin, h - 1.0 - margin)
    limit = float("inf")
    if abs(dx) > 1e-9:
        limit = min(limit, (max_x - x) / dx if dx > 0 else (margin - x) / dx)
    if abs(dy) > 1e-9:
        limit = min(limit, (max_y - y) / dy if dy > 0 else (margin - y) / dy)
    return max(0.0, limit) if limit != float("inf") else 0.0


def build_arc(sx, sy, tx, ty, total_ms, cmin, cmax, w, h):
    """v0.9.7/8 port: one continuous half-ellipse; curvature glides through the range;
    the side with screen room is chosen and pre-scaled so no flat clamped sections."""
    dx, dy = tx - sx, ty - sy
    dist = math.sqrt(dx * dx + dy * dy)
    if dist < 3:
        return [[tx, ty, max(0, total_ms)]], 0.0, total_ms
    cmin = _clamp(cmin, 0, 200)
    cmax = _clamp(cmax, 0, 200)
    if cmax < cmin:
        cmin, cmax = cmax, cmin
    ux, uy = dx / dist, dy / dist
    nx, ny = -uy, ux
    skew = _rf() * 0.14 - 0.07
    wobble = 0.025 + _rf() * 0.045
    phase0 = _rf() * math.pi * 2.0
    samples = int(_clamp(math.ceil(dist / 12.0), 32, 240))
    knot_count = int(_clamp(4 + int(dist / 320.0), 4, 8))
    knots = build_range_profile(cmin, cmax, knot_count)

    def fit_for_side(side):
        fit = 1.0
        for i in range(1, samples):
            t = i / samples
            q = _clamp(t + skew * math.sin(math.pi * t), 0.0, 1.0)
            along = 0.5 - 0.5 * math.cos(math.pi * q)
            shape = math.sin(math.pi * q) * (1.0 + wobble * math.sin(2.0 * math.pi * t + phase0))
            bx = sx + ux * dist * along
            by = sy + uy * dist * along
            required = dist * curve_height_ratio(sample_profile(knots, t)) * max(0.0, shape)
            if required < 1e-6:
                continue
            room = _ray_to_edge(bx, by, nx * side, ny * side, w, h, 2.0)
            fit = min(fit, room / required)
        return _clamp(fit * 0.94, 0.0, 1.0)

    fit_pos, fit_neg = fit_for_side(+1), fit_for_side(-1)
    if fit_pos >= 0.90 and fit_neg >= 0.90:
        side = -1 if _below(2) == 0 else +1
        fit = fit_pos if side > 0 else fit_neg
    elif fit_pos >= fit_neg:
        side, fit = +1, fit_pos
    else:
        side, fit = -1, fit_neg

    spine = [[sx, sy, 0]]
    actual_height = 0.0
    for i in range(1, samples):
        t = i / samples
        q = _clamp(t + skew * math.sin(math.pi * t), 0.0, 1.0)
        along = 0.5 - 0.5 * math.cos(math.pi * q)
        shape = math.sin(math.pi * q) * (1.0 + wobble * math.sin(2.0 * math.pi * t + phase0))
        local_h = dist * curve_height_ratio(sample_profile(knots, t)) * fit
        normal = side * local_h * max(0.0, shape)
        actual_height = max(actual_height, abs(normal))
        spine.append([int(round(sx + ux * dist * along + nx * normal)),
                      int(round(sy + uy * dist * along + ny * normal)), 0])
    spine.append([tx, ty, 0])
    path_len = _poly_len(spine)
    arc_ms = 0 if total_ms <= 0 else int(_clamp(round(total_ms * max(1.0, path_len / dist)), 60, 30000))
    # memory guard: Pico RAM - only >2500px moves (>4K diagonals) get wider spacing;
    # every screen up to 1440p keeps the hand-matched 2.0-3.2 px gliding steps.
    scale = max(1.0, dist / 2500.0)
    return resample_variable(spine, 2.0 * scale, 3.2 * scale), actual_height, arc_ms


class PausePlanner:
    """Run-scoped pause manager: long distraction breaks once per fresh random cadence."""

    def __init__(self):
        self.moves_since = 0
        self.next_idle_at = -1

    def mid_pause(self, c):
        if c["mid_chance"] > 0 and _below(100) < c["mid_chance"]:
            return rand_range(c["mid_min"], c["mid_max"])
        return 0

    def roll_long(self, c):
        if c["idle_pause_max"] <= 0 or c["idle_every_max"] <= 0:
            return 0
        self.moves_since += 1
        if self.next_idle_at < 0:
            self.next_idle_at = max(1, rand_range(c["idle_every_min"], c["idle_every_max"]))
        if self.moves_since < self.next_idle_at:
            return 0
        self.moves_since = 0
        self.next_idle_at = max(1, rand_range(c["idle_every_min"], c["idle_every_max"]))
        return rand_range(c["idle_pause_min"], c["idle_pause_max"])


_DEFAULT_CFG = dict(before_min=120, before_max=450, after_min=150, after_max=600,
                    mid_chance=12, mid_min=100, mid_max=400,
                    idle_every_min=5, idle_every_max=12, idle_pause_min=1000, idle_pause_max=5000,
                    over_chance=15, curve_min=15, curve_max=45,
                    speed_min=0, speed_max=2000, mt_min=0, mt_max=0)


def distance_scaled_move_ms(mn, mx, distance):
    """Natural-v1 duration: configured range is the medium-distance baseline."""
    if mx < mn:
        mn, mx = mx, mn
    if mx <= 0:
        return 0
    sampled = rand_range(max(1, mn), max(1, mx))
    scale = 0.72 + 0.63 * _clamp(distance / 650.0, 0.0, 1.0)
    return int(_clamp(round(sampled * scale), 120, 30000))


def relative_mouse_events(pos, tx, ty, c, pauses):
    """Yield a bounded curve; ARM 2.8.1 expands deltas to <=3 px reports."""
    sx, sy = pos[0], pos[1]
    dx, dy = tx - sx, ty - sy
    span = max(abs(dx), abs(dy))
    cmin, cmax = c["curve_min"], c["curve_max"]
    if cmax < cmin:
        cmin, cmax = cmax, cmin
    amp = min(span // 3, (span * rand_range(int(cmin), int(cmax))) // 100)
    if _below(2) == 0:
        amp = -amp
    denom = max(1, span)
    pxoff, pyoff = (-dy * amp) // denom, (dx * amp) // denom
    path_dist = math.sqrt(dx * dx + dy * dy)
    if c["mt_max"] > 0:
        total = distance_scaled_move_ms(c["mt_min"], c["mt_max"], path_dist)
    elif c["speed_max"] > 0:
        speed = rand_range(max(1, c["speed_min"]),
                           max(max(1, c["speed_min"]), c["speed_max"]))
        path = max(abs(dx), abs(dy)) + min(abs(dx), abs(dy)) // 2
        total = max(0, (path * 1000) // speed - path)
    else:
        total = 0
    # A 32-point path produced visible 40–55 ms burst gaps at slower sampled
    # speeds: ARM emitted its micro-steps, then waited for the next coarse Pico
    # point. Keep spatial control points, but add enough timing points to target
    # an ~8 ms cadence. This is a generator, so 128 points do not consume a
    # dense list in Pico RAM.
    spatial = max(8, (span + 11) // 12)
    timed = (total + 7) // 8 if total > 0 else spatial
    segments = max(8, min(128, max(spatial, timed)))
    base, extra = total // segments, total % segments
    mid = pauses.mid_pause(c) if path_dist >= 300 else 0
    mid_at = 1 + _below(max(1, segments - 1))
    phase_skew = rand_range(-220, 220)
    if c["before_max"] > 0:
        yield ("wait", rand_range(c["before_min"], c["before_max"]))
    px, py = sx, sy
    for step in range(1, segments + 1):
        t = (step * 1024) // segments
        q = int(_clamp(t + (phase_skew * t * (1024 - t)) // 1024000, 0, 1024))
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
    if c["after_max"] > 0:
        yield ("wait", rand_range(c["after_min"], c["after_max"]))
    long_pause = pauses.roll_long(c)
    if long_pause:
        yield ("wait", long_pause)


def plan_move(sx, sy, tx, ty, c, pauses, w, h):
    """Full PlanMove port. Returns dict(before, after, long, pts=[[x,y,delayMs]...])."""
    tx = int(_clamp(tx, 0, max(0, w - 1)))
    ty = int(_clamp(ty, 0, max(0, h - 1)))
    sx = int(_clamp(sx, 0, max(0, w - 1)))
    sy = int(_clamp(sy, 0, max(0, h - 1)))
    dist = math.sqrt((tx - sx) ** 2 + (ty - sy) ** 2)
    speed_lo = max(150, c["speed_min"])
    speed_hi = max(speed_lo, c["speed_max"])
    total_ms = 0 if c["speed_max"] <= 0 else int(_clamp(
        dist * 1000.0 / max(1.0, (speed_lo + speed_hi) / 2.0), 60, 30000))
    curve_min = int(_clamp(c["curve_min"], 0, 200))
    curve_max = int(_clamp(c["curve_max"], 0, 200))
    if curve_max < curve_min:
        curve_min, curve_max = curve_max, curve_min
    sampled = curve_min if curve_max == curve_min else random.randint(curve_min, curve_max)
    curve = _clamp(sampled / 100.0, 0.0, 2.0)
    mt_min, mt_max = max(0, c["mt_min"]), max(0, c["mt_max"])
    if mt_max < mt_min:
        mt_min, mt_max = mt_max, mt_min
    target_ms = distance_scaled_move_ms(mt_min, mt_max, dist) if mt_max > 0 else 0

    dense = []
    overshoot_idx = -1
    if curve_max > 100 and dist >= 80:                          # true arc mode
        dense, _height, _arc_ms = build_arc(sx, sy, tx, ty, total_ms, curve_min, curve_max, w, h)
    elif dist >= 300 and c["over_chance"] > 0 and _below(100) < c["over_chance"]:
        ux, uy = (tx - sx) / dist, (ty - sy) / dist
        over = rand_range(2, 6)
        perp = rand_range(-1, 1)
        ox = int(_clamp(tx + int(ux * over - uy * perp), 0, max(0, w - 1)))
        oy = int(_clamp(ty + int(uy * over + ux * perp), 0, max(0, h - 1)))
        leg1_ms = max(40, int(total_ms * 0.8))
        leg2_ms = max(30, total_ms - leg1_ms)
        dense += _build_leg(sx, sy, ox, oy, leg1_ms, curve, curve_min, curve_max)
        overshoot_idx = len(dense) - 1
        dense += _build_leg(ox, oy, tx, ty, leg2_ms, curve, curve_min, curve_max)
    else:
        dense = _build_leg(sx, sy, tx, ty, total_ms, curve, curve_min, curve_max)

    for p in dense:                                             # keep on-screen
        p[0] = int(_clamp(p[0], 0, max(0, w - 1)))
        p[1] = int(_clamp(p[1], 0, max(0, h - 1)))

    assign_dynamic_delays(dense, sx, sy, c["speed_min"], c["speed_max"], target_ms)

    if overshoot_idx >= 0:                                      # re-aim pause
        dense[overshoot_idx][2] += rand_range(70, 160)
    mid = pauses.mid_pause(c) if dist >= 300 else 0             # long-move hesitation only
    if mid > 0 and len(dense) >= 8:
        dense[2 + _below(len(dense) - 4)][2] += mid

    return {"before": rand_range(c["before_min"], c["before_max"]),
            "after": rand_range(c["after_min"], c["after_max"]),
            "long": pauses.roll_long(c),
            "target": (tx, ty),
            "pts": dense}


def _build_leg(sx, sy, tx, ty, total_ms, curve, cmin, cmax):
    dist0 = max(1.0, math.sqrt((tx - sx) ** 2 + (ty - sy) ** 2))
    profile = None
    if cmin >= 0 and cmax >= 0:
        profile = build_range_profile(cmin, cmax, int(_clamp(4 + int(dist0 / 320.0), 4, 8)))
    spine = windmouse(sx, sy, tx, ty, tuned_wind(dist0, curve), 14.0, curve, profile)
    with_start = [[sx, sy, 0]] + spine
    scale = max(1.0, dist0 / 2500.0)                           # Pico RAM guard (>4K only)
    return resample_variable(with_start, 2.0 * scale, 3.2 * scale)


# ── typing port (C# TypeTextCommands v0.9.15 → Python) ──────────────────────────────────

_QWERTY_ROWS = ("1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm")
_PUNCT = ".,!?;:"


def _qwerty_neighbor(ch):
    lower = ch.lower()
    for row in _QWERTY_ROWS:
        i = row.find(lower)
        if i < 0:
            continue
        j = i + (-1 if _below(2) == 0 else 1)
        if j < 0 or j >= len(row):
            j = 1 if i == 0 else i - 1
        n = row[j]
        return n.upper() if ch.isupper() else n
    return None


def _split_punct(s):
    outp = []
    start = 0
    for i in range(len(s) - 1):
        if s[i] in _PUNCT:
            outp.append(s[start:i + 1])
            start = i + 1
    if start < len(s):
        outp.append(s[start:])
    if not outp and s:
        outp.append(s)
    return outp


def plan_typing(text, p):
    """Returns [("KTEXT",hmin,hmax,chunk) | ("DLY",ms) | ("KCOMBO",vk), ...].
    p keys: h,w (ms ranges), wp (word pause chance %), p (punct pause range),
    think ((chance,(mn,mx))), typos ((mn,mx) corrected slips per TYPE).
    Legacy typo keeps its old every-N-words cadence."""
    hmin, hmax = p.get("h", (80, 220))
    wmin, wmax = p.get("w", (0, 0))
    wp = _clamp(p.get("wp", 100), 0, 100)
    pmin, pmax = p.get("p", (0, 0))
    think_chance, think_range = p.get("think", (0, (800, 2200)))
    think_chance = _clamp(think_chance, 0, 100)
    think_min, think_max = think_range
    typo_count_min, typo_count_max = p.get("typos", (0, 0))
    if typo_count_max < typo_count_min:
        typo_count_min, typo_count_max = typo_count_max, typo_count_min
    typo_count_min = max(0, typo_count_min)
    typo_count_max = max(0, typo_count_max)
    typo_count_mode = typo_count_max > 0
    typo_min, typo_max = p.get("typo", (0, 0))
    typo_cadence = not typo_count_mode and typo_max > 0
    next_typo_at = max(1, rand_range(typo_min, typo_max)) if typo_cadence else -1
    words_since_typo = 0
    word_mode = wmax > 0 or pmax > 0 or think_chance > 0 or typo_count_mode or typo_cadence

    cmds = []
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    line_words = [[wd for wd in line.split() if wd] for line in lines]
    pending = []
    typo_positions = {}

    # Pick the requested number once per TYPE execution. Positions are unique, so
    # a short one-word password can still receive several realistic corrected slips.
    if typo_count_mode:
        candidates = []
        for li, words in enumerate(line_words):
            for wi, word in enumerate(words):
                for pos, ch in enumerate(word):
                    if ch.lower() in "1234567890qwertyuiopasdfghjklzxcvbnm":
                        candidates.append((li, wi, pos))
        wanted = min(len(candidates), rand_range(typo_count_min, typo_count_max))
        for i in range(wanted):
            j = i + _below(len(candidates) - i)
            candidates[i], candidates[j] = candidates[j], candidates[i]
            li, wi, pos = candidates[i]
            typo_positions.setdefault((li, wi), []).append(pos)
        for positions in typo_positions.values():
            positions.sort()

    def flush():
        s = "".join(pending)
        pending.clear()
        for i in range(0, len(s), 60):
            cmds.append(("KTEXT", hmin, hmax, s[i:i + 60]))

    for li, line in enumerate(lines):
        if word_mode:
            words = line_words[li]
            for wi, word in enumerate(words):
                tail = " " if wi < len(words) - 1 else ""
                typed = word + tail
                selected = typo_positions.get((li, wi), ())
                if selected:
                    start = 0
                    for pos in selected:
                        wrong = _qwerty_neighbor(word[pos])
                        if wrong is None:
                            continue
                        flush()
                        slip = word[start:pos] + wrong
                        for ci in range(0, len(slip), 60):
                            cmds.append(("KTEXT", hmin, hmax, slip[ci:ci + 60]))
                        cmds.append(("DLY", rand_range(max(hmax, 120), hmax * 2 + 200)))
                        cmds.append(("KCOMBO", 8))       # Backspace
                        cmds.append(("DLY", rand_range(hmin, hmax)))
                        start = pos                      # retype the correct char next
                    typed = word[start:] + tail
                else:
                    typo_due = typo_cadence and (words_since_typo := words_since_typo + 1) >= next_typo_at
                    if typo_due and 2 <= len(word) <= 60:
                        pos = 1 + _below(len(word) - 1)  # legacy cadence never used first char
                        wrong = _qwerty_neighbor(word[pos])
                        if wrong is not None:
                            flush()
                            cmds.append(("KTEXT", hmin, hmax, word[:pos] + wrong))
                            cmds.append(("DLY", rand_range(max(hmax, 120), hmax * 2 + 200)))
                            cmds.append(("KCOMBO", 8))   # Backspace
                            cmds.append(("DLY", rand_range(hmin, hmax)))
                            typed = word[pos:] + tail
                            words_since_typo = 0
                            next_typo_at = max(1, rand_range(typo_min, typo_max))
                segs = _split_punct(typed) if pmax > 0 else [typed]
                for si, seg in enumerate(segs):
                    pending.append(seg)
                    if si < len(segs) - 1:
                        flush()
                        cmds.append(("DLY", rand_range(pmin, pmax)))
                if wi < len(words) - 1:
                    if wmax > 0 and _below(100) < wp:
                        flush()
                        cmds.append(("DLY", rand_range(wmin, wmax)))
                    if think_chance > 0 and think_max > 0 and _below(100) < think_chance:
                        flush()
                        cmds.append(("DLY", rand_range(think_min, think_max)))
            flush()
        else:
            for i in range(0, len(line), 60):
                cmds.append(("KTEXT", hmin, hmax, line[i:i + 60]))
        if li < len(lines) - 1:
            cmds.append(("KCOMBO", 13))                  # Enter between lines
    return cmds
