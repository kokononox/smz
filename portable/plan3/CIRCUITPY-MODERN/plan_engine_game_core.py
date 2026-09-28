"""Low-memory Game primitives loaded before the execution runtime."""
import gc
import random

_mouse_module = None

class GameAbort(RuntimeError):
    pass


class _FileCommands:
    """Random-access command rows backed by Flash, not a heap-resident list."""
    def __init__(self, name):
        self.file = open("/" + name, "r")
        self.offsets = bytearray()
        while True:
            offset = self.file.tell()
            raw = self.file.readline()
            if not raw:
                break
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            for shift in (0, 8, 16, 24):
                self.offsets.append((offset >> shift) & 255)

    def __len__(self):
        return len(self.offsets) // 4

    def __getitem__(self, index):
        size = len(self)
        if index < 0:
            index += size
        if index < 0 or index >= size:
            raise IndexError(index)
        base = index * 4; data = self.offsets
        offset = (data[base] | data[base + 1] << 8 |
                  data[base + 2] << 16 | data[base + 3] << 24)
        self.file.seek(offset)
        parts = self.file.readline().strip().split("|", 1)
        return parts[0].upper(), parts[1].strip() if len(parts) == 2 else ""

    def __iter__(self):
        for index in range(len(self)):
            yield self[index]

    def close(self):
        self.file.close()
        self.offsets = None


def _abort():
    raise GameAbort("route aborted")


def _range(value):
    bits = value.replace(",", " ").split()
    if len(bits) == 1:
        value = int(float(bits[0])); return value, value
    if len(bits) != 2:
        raise ValueError("range needs min,max")
    a, b = int(float(bits[0])), int(float(bits[1]))
    return (a, b) if b >= a else (b, a)


def _key(args):
    combo = None; hold = (0, 0)
    for field in args.split("|"):
        if field.startswith("combo="):
            combo = [int(v) for v in field[6:].split("+") if v]
        elif field.startswith("hold="):
            hold = _range(field[5:])
        else:
            raise ValueError("bad KEY field")
    if not combo or len(combo) > 10:
        raise ValueError("KEY needs combo")
    return combo, hold


def _end(commands, start, opener, closer):
    depth = 0
    for i in range(start + 1, len(commands)):
        op = commands[i][0]
        if (op == opener or (opener in ("LOOP", "LOOPTIME") and op in ("LOOP", "LOOPTIME"))):
            depth += 1
        elif op == closer:
            if depth == 0: return i
            depth -= 1
    raise ValueError(opener + " without " + closer)


def _items(commands, start, end, separator, nested_open, nested_close):
    out = []; item = start; depth = 0
    for i in range(start, end):
        op = commands[i][0]
        if op == nested_open: depth += 1
        elif op == nested_close: depth -= 1
        elif op == separator and depth == 0:
            out.append((item, i)); item = i + 1
    out.append((item, end))
    return out


def _pick_items(commands, start, end, separator, nested_open, nested_close, take):
    """Reservoir-sample package ranges without allocating every item."""
    chosen = []; item = start; depth = 0; seen = 0
    for i in range(start, end + 1):
        op = commands[i][0] if i < end else separator
        if op == nested_open:
            depth += 1
        elif op == nested_close:
            depth -= 1
        elif op == separator and depth == 0:
            seen += 1; candidate = (item, i); item = i + 1
            if len(chosen) < take:
                chosen.append(candidate)
            else:
                slot = random.randrange(seen)
                if slot < take:
                    chosen[slot] = candidate
    for i in range(len(chosen) - 1, 0, -1):
        slot = random.randrange(i + 1)
        chosen[i], chosen[slot] = chosen[slot], chosen[i]
    return chosen


def _package(commands, index):
    args = commands[index][1].replace(",", " ").split()
    if len(args) != 3:
        raise ValueError("RPKG needs mode,min,max")
    mode = args[0].lower()
    if mode in ("randomsubset", "pick"): mode = "pick"
    elif mode in ("shuffleall", "all"): mode = "all"
    elif mode != "seq": raise ValueError("bad RPKG mode")
    finish = _end(commands, index, "RPKG", "ENDPKG")
    if mode == "pick":
        lo, hi = int(args[1]), int(args[2])
        lo, hi = max(0, min(lo, hi)), max(lo, hi)
        parts = _pick_items(commands, index + 1, finish, "PKGITEM",
                            "RPKG", "ENDPKG", random.randint(lo, hi))
        order = list(range(len(parts))); gc.collect()
        return finish, parts, order
    parts = _items(commands, index + 1, finish, "PKGITEM", "RPKG", "ENDPKG")
    order = list(range(len(parts)))
    if mode != "seq":
        for k in range(len(order) - 1, 0, -1):
            j = random.randrange(k + 1)
            order[k], order[j] = order[j], order[k]
    gc.collect()
    return finish, parts, order


def _watch_profiles(args):
    profiles = []
    for raw in args.split(";"):
        values = raw.split(",")
        if len(values) != 8:
            raise ValueError("SOUNDWATCH needs 8 fields per profile")
        profiles.append({"id": values[0], "peak_min": int(values[1]),
            "peak_max": int(values[2]), "minimum": int(values[3]),
            "priority": int(values[4]), "cooldown": int(values[5]),
            "file": values[6], "mode": values[7]})
    return profiles

def _emit_heap(ctx, stage):
    emit = getattr(getattr(ctx, "r", None), "emit", None)
    if emit is not None:
        emit("EVT|DEBUG|GAME|stage=%s|free=%d" %
             (stage, getattr(gc, "mem_free", lambda: -1)()))


def _mouse(ctx, state):
    # Importing login while this module itself is still compiling creates the
    # highest heap peak on RP2040. Wait until the first actual RMOUSE command,
    # after the Game module and file index are both stable and collectible.
    global _mouse_module
    if _mouse_module is None:
        gc.collect()
        _emit_heap(ctx, "before-mouse-import")
        gc.collect()
        try:
            _mouse_module = __import__("plan_engine_login")
        except MemoryError:
            _emit_heap(ctx, "mouse-import-memoryerror")
            raise
        gc.collect()
        _emit_heap(ctx, "after-mouse-import")
    if state["pauses"] is None:
        state["pauses"] = _mouse_module.PausePlanner(ctx)
    return _mouse_module


def _mouse_events(args, ctx, state):
    helper = _mouse(ctx, state)
    for event in helper.mouse_events(args, ctx, state["pauses"], state["pos"], state["speed"]):
        if event[0] == "wait": yield ("wait", event[1])
        else: yield ("move", event[1], event[2], event[3])
