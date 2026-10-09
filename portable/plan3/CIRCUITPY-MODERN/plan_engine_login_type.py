"""Human typing implementation loaded only at the first TYPE."""
import gc

_core = None

def bind(core):
    global _core
    _core = core

_PUNCT = ".,!?;:"
_QWERTY_ROWS = ("1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm")


def _qwerty_neighbor(ch):
    lower = ch.lower()
    for row in _QWERTY_ROWS:
        i = row.find(lower)
        if i >= 0:
            j = i + (-1 if _core._below(2) == 0 else 1)
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
    wp = _core._clamp(prm.get("wp", 100), 0, 100)
    pmin, pmax = prm.get("p", (0, 0))
    think_chance, think_range = prm.get("think", (0, (800, 2200)))
    think_min, think_max = think_range
    tmin, tmax = prm.get("typos", (0, 0))
    if tmax < tmin: tmin, tmax = tmax, tmin
    tmin, tmax = max(0, tmin), max(0, tmax)
    cmin, cmax = prm.get("typochars", (0, 0))
    if cmax < cmin: cmin, cmax = cmax, cmin
    cmin, cmax = max(1, cmin), max(0, cmax)
    word_mode = wmax > 0 or pmax > 0 or think_chance > 0 or tmax > 0 or cmax > 0
    commands = []
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    words_by_line = [[word for word in line.split() if word] for line in lines]
    typo_positions = {}
    if cmax > 0:
        candidates = []
        for li, words in enumerate(words_by_line):
            for wi, word in enumerate(words):
                for pi, ch in enumerate(word):
                    if ch.lower() in "1234567890qwertyuiopasdfghjklzxcvbnm":
                        candidates.append((li, wi, pi))
        cursor = _core.rand_range(cmin, cmax) - 1
        while cursor < len(candidates):
            li, wi, pi = candidates[cursor]
            typo_positions.setdefault((li, wi), []).append(pi)
            cursor += _core.rand_range(cmin, cmax)
    elif tmax > 0:
        candidates = []
        for li, words in enumerate(words_by_line):
            for wi, word in enumerate(words):
                for pi, ch in enumerate(word):
                    if ch.lower() in "1234567890qwertyuiopasdfghjklzxcvbnm":
                        candidates.append((li, wi, pi))
        wanted = min(len(candidates), _core.rand_range(tmin, tmax))
        for i in range(wanted):
            j = i + _core._below(len(candidates) - i)
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
                    commands.append(("delay", _core.rand_range(max(hmax, 120), hmax * 2 + 200)))
                    commands.append(("combo", 8))
                    commands.append(("delay", _core.rand_range(hmin, hmax)))
                    start = pi
                typed = word[start:] + tail
                segments = _split_punct(typed) if pmax > 0 else (typed,)
                for si, segment in enumerate(segments):
                    for ci in range(0, len(segment), 60): commands.append(("text", hmin, hmax, segment[ci:ci + 60]))
                    if si < len(segments) - 1: commands.append(("delay", _core.rand_range(pmin, pmax)))
                if wi < len(words) - 1:
                    if wmax > 0 and _core._below(100) < wp: commands.append(("delay", _core.rand_range(wmin, wmax)))
                    if think_chance > 0 and think_max > 0 and _core._below(100) < think_chance:
                        commands.append(("delay", _core.rand_range(think_min, think_max)))
        else:
            for ci in range(0, len(line), 60): commands.append(("text", hmin, hmax, line[ci:ci + 60]))
        if li < len(lines) - 1: commands.append(("combo", 13))
    return commands


def run_type(args, ctx):
    raw = _core._fields(args)
    if "text" not in raw:
        raise ValueError("TYPE needs text")
    prm = {}
    for key in ("h", "w", "p", "typos", "typochars"):
        if key in raw: prm[key] = _core._pair(raw[key])
    if "wp" in raw: prm["wp"] = int(raw["wp"])
    if "think" in raw:
        chance, ranges = raw["think"].split(":", 1)
        prm["think"] = (int(chance), _core._pair(ranges))
    commands = _typing_commands(_core.pct_dec(raw["text"]), prm)
    del raw, prm
    for command in commands:
        if command[0] == "text": ctx.ktext(command[1], command[2], command[3])
        elif command[0] == "delay":
            if not ctx.sleep_ms(command[1]): raise RuntimeError("route aborted")
        else: ctx.key_combo([command[1]], 0, 0)
    del commands
    gc.collect()
