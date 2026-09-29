"""Iterative Game event generator, split to bound CircuitPython compile peaks."""
import gc
import random

_core = None

def bind(core):
    global _core
    _core = core


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


