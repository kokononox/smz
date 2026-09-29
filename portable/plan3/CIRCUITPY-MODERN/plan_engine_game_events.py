"""Iterative Game flow and Parallel events with an explicit container stack."""
import gc
import random

_core = None

def bind(core):
    global _core
    _core = core


class Cursor:
    """Resumable sequential command cursor; never recurses for containers."""
    def __init__(self, commands, start, end, ctx):
        self.commands = commands
        self.ctx = ctx
        self.root_end = end
        self.i = start
        self.end = end
        self.frames = []

    def jump(self, index):
        self.frames[:] = []
        self.i = index
        self.end = self.root_end

    def _resume(self):
        while self.frames:
            frame = self.frames[-1]
            if frame[0] == "P" and frame[5] < len(frame[4]):
                selected = frame[4][frame[5]]
                frame[5] += 1
                self.i, self.end = frame[3][selected]
                return True
            if frame[0] == "L":
                again = (self.ctx.now() < frame[6]) if frame[6] is not None else (
                    frame[5] is None or frame[5] > 1)
                if again:
                    if frame[5] is not None:
                        frame[5] -= 1
                    self.i, self.end = frame[3], frame[4]
                    return True
            self.frames.pop()
            self.i, self.end = frame[2], frame[1]
            if self.i < self.end:
                return True
        return False

    def next(self):
        while True:
            if self.i >= self.end:
                if not self._resume():
                    return None
                continue
            i = self.i
            op, args = self.commands[i]
            if op == "RPKG":
                finish, parts, order = _core._package(self.commands, i)
                if order:
                    selected = order[0]
                    self.frames.append(["P", self.end, finish + 1,
                                        parts, order, 1])
                    self.i, self.end = parts[selected]
                    continue
                self.i = finish + 1
                continue
            if op in ("LOOP", "LOOPTIME"):
                finish = _core._end(self.commands, i, op, "ENDLOOP")
                if op == "LOOP":
                    count = int(args)
                    left = None if count == 0 else count
                    deadline = None
                else:
                    left = None
                    deadline = self.ctx.now() + float(args)
                self.frames.append(["L", self.end, finish + 1, i + 1,
                                    finish, left, deadline])
                self.i, self.end = i + 1, finish
                continue
            if op == "PGROUP":
                finish = _core._end(self.commands, i, "PGROUP", "ENDPAR")
                self.i = finish + 1
                return ("PGROUP", args, i + 1, finish)
            self.i = i + 1
            if op not in ("PKGITEM", "ENDPKG", "ENDLOOP", "PARITEM", "ENDPAR"):
                return (op, args, -1, -1)


def session(commands, ctx, resume):
    if resume is not None:
        cursor = resume["_game_cursor"]
        cursor.commands = commands; cursor.ctx = ctx
        return resume["_game_labels"], cursor, resume["game_state"]
    labels = {}
    for index, item in enumerate(commands):
        if item[0] == "LABEL":
            if not item[1] or item[1] in labels:
                raise ValueError("LABEL needs a unique name")
            labels[item[1]] = index
    state = {"speed": [0, 2000], "pos": [ctx.screen_w // 2, ctx.screen_h // 2],
             "pauses": None, "watch": False}
    return labels, None, state


def events(commands, start, end, ctx, state):
    i = start
    # Explicit container frames keep CircuitPython's bounded pystack flat.
    # P = package: [P,parent_end,after,parts,order,next]
    # L = loop:    [L,parent_end,after,body_start,body_end,left,deadline]
    frames = []
    while True:
        if i >= end:
            resumed = False
            while frames:
                frame = frames[-1]
                if frame[0] == "P" and frame[5] < len(frame[4]):
                    selected = frame[4][frame[5]]; frame[5] += 1
                    i, end = frame[3][selected]; resumed = True; break
                if frame[0] == "L":
                    again = (ctx.now() < frame[6]) if frame[6] is not None else (
                        frame[5] is None or frame[5] > 1)
                    if again:
                        if frame[5] is not None: frame[5] -= 1
                        i, end = frame[3], frame[4]; resumed = True; break
                frames.pop(); i, end = frame[2], frame[1]
                if i < end: resumed = True; break
            if not resumed: return
            continue
        op, args = commands[i]
        if op in ("PLAN", "SCREEN", "SPEED", "PKGITEM", "PARITEM", "SOUNDWATCH"):
            pass
        elif op == "DELAY":
            lo, hi = _core._range(args); yield ("wait", random.randint(lo, hi))
        elif op == "KEY":
            combo, hold = _core._key(args); yield ("key", combo, hold)
        elif op == "RMOUSE":
            gc.collect()
            for event in _core._mouse_events(args, ctx, state): yield event
        elif op == "WSND":
            values = [int(v) for v in args.replace(",", " ").split()]
            if len(values) != 3: raise ValueError("WSND needs threshold,min,timeout")
            yield ("sound", values[0], values[1], values[2], values[0], 65535, 0, "", 0)
        elif op == "WSNDP":
            values = args.replace(" ", "").split(",")
            if len(values) not in (5, 8, 10): raise ValueError("WSNDP needs 5, 8, or 10 fields")
            profile_id = int(values[0])
            threshold, minimum = ctx.sound_profile(
                profile_id, values[1], int(values[2]), int(values[3]))
            peak_min = int(values[5]) if len(values) >= 8 else threshold
            if peak_min == 0: peak_min = threshold
            peak_max = int(values[6]) if len(values) >= 8 else 65535
            priority = int(values[7]) if len(values) >= 8 else 0
            response = values[8] if len(values) == 10 else ""
            cooldown = int(values[9]) if len(values) == 10 else 0
            yield ("sound", threshold, minimum, int(values[4]), peak_min, peak_max,
                   priority, response, cooldown)
        elif op == "WPROFILE":
            values = args.replace(" ", "").split(",")
            if len(values) != 3 or values[0] != "splash":
                raise ValueError("WPROFILE needs splash,min,max")
            lo, hi = int(values[1]), int(values[2])
            yield ("profile", "splash", random.randint(min(lo, hi), max(lo, hi)))
        elif op == "RPKG":
            finish, parts, order = _core._package(commands, i)
            if order:
                selected = order[0]
                frames.append(["P", end, finish + 1, parts, order, 1])
                i, end = parts[selected]
                continue
            i = finish
        elif op in ("LOOP", "LOOPTIME"):
            finish = _core._end(commands, i, op, "ENDLOOP")
            if op == "LOOP":
                count = int(args)
                left = None if count == 0 else count
                deadline = None
            else:
                left = None; deadline = ctx.now() + float(args)
            frames.append(["L", end, finish + 1, i + 1, finish, left, deadline])
            i, end = i + 1, finish
            continue
        else:
            raise ValueError("unsupported Game event " + op)
        i += 1


