"""Login/Mouse primitives loaded independently of movement and typing."""
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
