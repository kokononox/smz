"""Small Login/DC runner helpers; intentionally independent of plan_engine_parse."""
import gc
import math
import random


def _below(n):
    return random.randrange(n) if n > 0 else 0


def _clamp(v, lo, hi):
    return lo if v < lo else hi if v > hi else v


def rand_range(mn, mx):
    if mx < mn:
        mn, mx = mx, mn
    if mx <= 0:
        return 0
    return mn if mx <= mn else random.randint(mn, mx)


def _pair(value):
    bits = value.split(",", 1)
    if len(bits) != 2:
        raise ValueError("range needs min,max")
    a, b = int(bits[0]), int(bits[1])
    return (a, b) if b >= a else (b, a)


def _fields(args):
    out = {}
    for field in args.split("|"):
        if "=" not in field:
            raise ValueError("Login command needs key=value")
        key, value = field.split("=", 1)
        out[key] = value
    return out


def pct_dec(value):
    out = []
    i = 0
    while i < len(value):
        if value[i] == "%" and i + 2 < len(value):
            try:
                out.append(chr(int(value[i + 1:i + 3], 16)))
                i += 3
                continue
            except Exception:
                pass
        out.append(value[i])
        i += 1
    return "".join(out)


class PausePlanner:
    def __init__(self):
        self.moves_since = 0
        self.next_idle_at = -1

    def mid_pause(self, cfg):
        if cfg["mid_chance"] > 0 and _below(100) < cfg["mid_chance"]:
            return rand_range(cfg["mid_min"], cfg["mid_max"])
        return 0

    def roll_long(self, cfg):
        if cfg["idle_pause_max"] <= 0 or cfg["idle_every_max"] <= 0:
            return 0
        self.moves_since += 1
        if self.next_idle_at < 0:
            self.next_idle_at = max(1, rand_range(cfg["idle_every_min"], cfg["idle_every_max"]))
        if self.moves_since < self.next_idle_at:
            return 0
        self.moves_since = 0
        self.next_idle_at = max(1, rand_range(cfg["idle_every_min"], cfg["idle_every_max"]))
        return rand_range(cfg["idle_pause_min"], cfg["idle_pause_max"])


_DEFAULT_CFG = dict(before_min=120, before_max=450, after_min=150, after_max=600,
                    mid_chance=12, mid_min=100, mid_max=400,
                    idle_every_min=5, idle_every_max=12,
                    idle_pause_min=1000, idle_pause_max=5000,
                    over_chance=15, curve_min=15, curve_max=45,
                    speed_min=0, speed_max=2000, mt_min=0, mt_max=0)


def distance_scaled_move_ms(mn, mx, distance):
    if mx < mn:
        mn, mx = mx, mn
    if mx <= 0:
        return 0
    sampled = rand_range(max(1, mn), max(1, mx))
    scale = 0.72 + 0.63 * _clamp(distance / 650.0, 0.0, 1.0)
    return int(_clamp(round(sampled * scale), 120, 30000))


def _mouse_events(pos, tx, ty, cfg, pauses):
    sx, sy = pos[0], pos[1]
    dx, dy = tx - sx, ty - sy
    span = max(abs(dx), abs(dy))
    cmin, cmax = cfg["curve_min"], cfg["curve_max"]
    if cmax < cmin:
        cmin, cmax = cmax, cmin
    amp = min(span // 3, (span * rand_range(int(cmin), int(cmax))) // 100)
    if _below(2) == 0:
        amp = -amp
    denom = max(1, span)
    pxoff, pyoff = (-dy * amp) // denom, (dx * amp) // denom
    path_dist = math.sqrt(dx * dx + dy * dy)
    if cfg["mt_max"] > 0:
        total = distance_scaled_move_ms(cfg["mt_min"], cfg["mt_max"], path_dist)
    elif cfg["speed_max"] > 0:
        speed = rand_range(max(1, cfg["speed_min"]),
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
    mid_at = 1 + _below(max(1, segments - 1))
    phase_skew = rand_range(-220, 220)
    if cfg["before_max"] > 0:
        yield ("wait", rand_range(cfg["before_min"], cfg["before_max"]), 0, 0)
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
    if cfg["after_max"] > 0:
        yield ("wait", rand_range(cfg["after_min"], cfg["after_max"]), 0, 0)
    long_pause = pauses.roll_long(cfg)
    if long_pause:
        yield ("wait", long_pause, 0, 0)


def mouse_events(args, ctx, pauses, pos, route_speed):
    prm = _fields(args)
    if "region" not in prm:
        raise ValueError("RMOUSE needs region")
    region = tuple(int(v) for v in prm["region"].split(","))
    if len(region) != 4:
        raise ValueError("RMOUSE region needs x,y,w,h")
    cfg = dict(_DEFAULT_CFG)
    cfg["speed_min"], cfg["speed_max"] = route_speed
    for key in ("speed", "mt", "curve", "before", "after"):
        if key in prm:
            a, b = _pair(prm[key])
            if key == "speed": cfg["speed_min"], cfg["speed_max"] = a, b
            elif key == "mt": cfg["mt_min"], cfg["mt_max"] = a, b
            elif key == "curve": cfg["curve_min"], cfg["curve_max"] = a, b
            elif key == "before": cfg["before_min"], cfg["before_max"] = a, b
            else: cfg["after_min"], cfg["after_max"] = a, b
    if "mid" in prm:
        chance, ranges = prm["mid"].split(":", 1)
        cfg["mid_chance"] = int(chance)
        cfg["mid_min"], cfg["mid_max"] = _pair(ranges)
    if "idle" in prm:
        every, pause = prm["idle"].split(":", 1)
        cfg["idle_every_min"], cfg["idle_every_max"] = _pair(every)
        cfg["idle_pause_min"], cfg["idle_pause_max"] = _pair(pause)
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
    for event in _mouse_events(pos, tx, ty, cfg, pauses):
        yield event
    del cfg, prm


def run_rmouse(args, ctx, pauses, pos, route_speed):
    for event, delay, dx, dy in mouse_events(args, ctx, pauses, pos, route_speed):
        if event == "move" and (dx or dy):
            ctx.mmove_relative(dx, dy)
        if delay and not ctx.sleep_ms(delay):
            raise RuntimeError("route aborted")
    gc.collect()


_QWERTY_ROWS = ("1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm")
_PUNCT = ".,!?;:"


def _qwerty_neighbor(ch):
    lower = ch.lower()
    for row in _QWERTY_ROWS:
        i = row.find(lower)
        if i >= 0:
            j = i + (-1 if _below(2) == 0 else 1)
            if j < 0 or j >= len(row):
                j = 1 if i == 0 else i - 1
            value = row[j]
            return value.upper() if ch.isupper() else value
    return None


def _split_punct(value):
    out = []
    start = 0
    for i in range(len(value) - 1):
        if value[i] in _PUNCT:
            out.append(value[start:i + 1]); start = i + 1
    if start < len(value): out.append(value[start:])
    if not out and value: out.append(value)
    return out


def _typing_commands(text, prm):
    hmin, hmax = prm.get("h", (80, 220))
    wmin, wmax = prm.get("w", (0, 0))
    wp = _clamp(prm.get("wp", 100), 0, 100)
    pmin, pmax = prm.get("p", (0, 0))
    think_chance, think_range = prm.get("think", (0, (800, 2200)))
    think_min, think_max = think_range
    tmin, tmax = prm.get("typos", (0, 0))
    if tmax < tmin: tmin, tmax = tmax, tmin
    tmin, tmax = max(0, tmin), max(0, tmax)
    word_mode = wmax > 0 or pmax > 0 or think_chance > 0 or tmax > 0
    commands = []
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    words_by_line = [[word for word in line.split() if word] for line in lines]
    typo_positions = {}
    if tmax > 0:
        candidates = []
        for li, words in enumerate(words_by_line):
            for wi, word in enumerate(words):
                for pi, ch in enumerate(word):
                    if ch.lower() in "1234567890qwertyuiopasdfghjklzxcvbnm":
                        candidates.append((li, wi, pi))
        wanted = min(len(candidates), rand_range(tmin, tmax))
        for i in range(wanted):
            j = i + _below(len(candidates) - i)
            candidates[i], candidates[j] = candidates[j], candidates[i]
            li, wi, pi = candidates[i]
            typo_positions.setdefault((li, wi), []).append(pi)
        for values in typo_positions.values(): values.sort()
    for li, line in enumerate(lines):
        if word_mode:
            words = words_by_line[li]
            for wi, word in enumerate(words):
                tail = " " if wi < len(words) - 1 else ""
                selected = typo_positions.get((li, wi), ())
                start = 0
                for pi in selected:
                    wrong = _qwerty_neighbor(word[pi])
                    if wrong is None: continue
                    slip = word[start:pi] + wrong
                    for ci in range(0, len(slip), 60): commands.append(("text", hmin, hmax, slip[ci:ci + 60]))
                    commands.append(("delay", rand_range(max(hmax, 120), hmax * 2 + 200)))
                    commands.append(("combo", 8))
                    commands.append(("delay", rand_range(hmin, hmax)))
                    start = pi
                typed = word[start:] + tail
                segments = _split_punct(typed) if pmax > 0 else (typed,)
                for si, segment in enumerate(segments):
                    for ci in range(0, len(segment), 60): commands.append(("text", hmin, hmax, segment[ci:ci + 60]))
                    if si < len(segments) - 1: commands.append(("delay", rand_range(pmin, pmax)))
                if wi < len(words) - 1:
                    if wmax > 0 and _below(100) < wp: commands.append(("delay", rand_range(wmin, wmax)))
                    if think_chance > 0 and think_max > 0 and _below(100) < think_chance:
                        commands.append(("delay", rand_range(think_min, think_max)))
        else:
            for ci in range(0, len(line), 60): commands.append(("text", hmin, hmax, line[ci:ci + 60]))
        if li < len(lines) - 1: commands.append(("combo", 13))
    return commands


def run_type(args, ctx):
    raw = _fields(args)
    if "text" not in raw:
        raise ValueError("TYPE needs text")
    prm = {}
    for key in ("h", "w", "p", "typos"):
        if key in raw: prm[key] = _pair(raw[key])
    if "wp" in raw: prm["wp"] = int(raw["wp"])
    if "think" in raw:
        chance, ranges = raw["think"].split(":", 1)
        prm["think"] = (int(chance), _pair(ranges))
    commands = _typing_commands(pct_dec(raw["text"]), prm)
    del raw, prm
    for command in commands:
        if command[0] == "text": ctx.ktext(command[1], command[2], command[3])
        elif command[0] == "delay":
            if not ctx.sleep_ms(command[1]): raise RuntimeError("route aborted")
        else: ctx.key_combo([command[1]], 0, 0)
    del commands
    gc.collect()
