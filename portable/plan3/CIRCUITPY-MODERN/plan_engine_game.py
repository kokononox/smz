"""Low-memory Game/fishing runner; no full parser or executor imports."""
import gc
import random
import plan_engine_login as mouse


class GameAbort(RuntimeError):
    pass


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


def _package(commands, index):
    args = commands[index][1].replace(",", " ").split()
    if len(args) != 3:
        raise ValueError("RPKG needs mode,min,max")
    mode = args[0].lower()
    if mode in ("randomsubset", "pick"): mode = "pick"
    elif mode in ("shuffleall", "all"): mode = "all"
    elif mode != "seq": raise ValueError("bad RPKG mode")
    finish = _end(commands, index, "RPKG", "ENDPKG")
    parts = _items(commands, index + 1, finish, "PKGITEM", "RPKG", "ENDPKG")
    order = list(range(len(parts)))
    if mode != "seq":
        for k in range(len(order) - 1, 0, -1):
            j = random.randrange(k + 1)
            order[k], order[j] = order[j], order[k]
        if mode == "pick":
            lo, hi = int(args[1]), int(args[2])
            lo, hi = max(0, min(lo, hi)), min(len(order), max(lo, hi))
            order = order[:random.randint(lo, hi)]
    return finish, parts, order


def _mouse_events(args, ctx, state):
    for event in mouse.mouse_events(args, ctx, state["pauses"], state["pos"], state["speed"]):
        if event[0] == "wait": yield ("wait", event[1])
        else: yield ("move", event[1], event[2], event[3])


def _events(commands, start, end, ctx, state):
    i = start
    while i < end:
        op, args = commands[i]
        if op in ("PLAN", "SCREEN", "SPEED", "PKGITEM", "PARITEM"):
            pass
        elif op == "DELAY":
            lo, hi = _range(args); yield ("wait", random.randint(lo, hi))
        elif op == "KEY":
            combo, hold = _key(args); yield ("key", combo, hold)
        elif op == "RMOUSE":
            gc.collect()
            for event in _mouse_events(args, ctx, state): yield event
        elif op == "WSND":
            values = [int(v) for v in args.replace(",", " ").split()]
            if len(values) != 3: raise ValueError("WSND needs threshold,min,timeout")
            yield ("sound", values[0], values[1], values[2])
        elif op == "RPKG":
            finish, parts, order = _package(commands, i)
            for selected in order:
                a, b = parts[selected]
                for event in _events(commands, a, b, ctx, state): yield event
            i = finish
        elif op in ("LOOP", "LOOPTIME"):
            finish = _end(commands, i, op, "ENDLOOP")
            if op == "LOOP":
                count = int(args)
                remaining = None if count == 0 else count
                while remaining is None or remaining > 0:
                    for event in _events(commands, i + 1, finish, ctx, state): yield event
                    if remaining is not None: remaining -= 1
            else:
                deadline = ctx.now() + float(args)
                while ctx.now() < deadline:
                    for event in _events(commands, i + 1, finish, ctx, state): yield event
            i = finish
        else:
            raise ValueError("unsupported Game event " + op)
        i += 1


def _parallel(commands, start, end, ctx, state):
    branches = _items(commands, start, end, "PARITEM", "PGROUP", "ENDPAR")
    now = int(ctx.now() * 1000)
    tasks = [{"it": _events(commands, a, b, ctx, state), "due": now,
              "pending": None, "moving": False, "sound": False, "poll": now}
             for a, b in branches]
    sound_owner = None
    try:
        while tasks:
            if not ctx.gate(): _abort()
            now = int(ctx.now() * 1000); progressed = False
            for task in tuple(tasks):
                if task not in tasks: continue
                if task["sound"]:
                    if now < task["poll"]: continue
                    concurrent = getattr(ctx, "sound_parallel_safe", None)
                    if (not (concurrent and concurrent()) and
                            any(other is not task and other["moving"] for other in tasks)):
                        task["poll"] = now + 10; continue
                    result = ctx.sound_poll(); task["poll"] = now + 10
                    if result is None: continue
                    task["sound"] = False; sound_owner = None
                    tasks[:] = [task] if result else []
                    ctx.log("parallel wsnd " + ("heard - cancel siblings" if result else "timeout - cancel group"))
                    progressed = True; continue
                if now < task["due"]: continue
                pending = task["pending"]
                if pending is not None:
                    task["pending"] = None
                    if pending[2] or pending[3]: ctx.mmove_relative(pending[2], pending[3])
                    progressed = True; continue
                try: event = next(task["it"])
                except StopIteration:
                    tasks.remove(task); progressed = True; continue
                kind = event[0]
                if kind == "wait":
                    task["moving"] = False; task["due"] = now + max(0, event[1])
                elif kind == "move":
                    task["moving"] = True; task["due"] = now + max(0, event[1]); task["pending"] = event
                elif kind == "key":
                    task["moving"] = False; ctx.key_combo(event[1], event[2][0], event[2][1])
                elif kind == "sound":
                    if sound_owner is not None: raise ValueError("only one WSND listener is allowed")
                    ctx.sound_start(event[1], event[2], event[3])
                    task["sound"] = True; task["poll"] = now; sound_owner = task
                progressed = True
            if not tasks: break
            if progressed: continue
            wake = min(task["poll"] if task["sound"] else task["due"] for task in tasks)
            if not ctx.sleep_ms(max(1, wake - int(ctx.now() * 1000))): _abort()
    finally:
        if sound_owner is not None: ctx.sound_cancel()


def _run(commands, start, end, ctx, state):
    i = start
    while i < end:
        op, args = commands[i]
        if op == "PLAN": pass
        elif op == "SCREEN":
            values = args.replace(",", " ").split()
            if len(values) != 2: raise ValueError("SCREEN needs width,height")
            ctx.screen_w, ctx.screen_h = int(values[0]), int(values[1])
        elif op == "SPEED": state["speed"][:] = _range(args)
        elif op == "DELAY":
            lo, hi = _range(args)
            if not ctx.sleep_ms(random.randint(lo, hi)): _abort()
        elif op == "KEY":
            combo, hold = _key(args); ctx.key_combo(combo, hold[0], hold[1])
        elif op == "RMOUSE":
            mouse.run_rmouse(args, ctx, state["pauses"], state["pos"], state["speed"])
        elif op == "RPKG":
            finish, parts, order = _package(commands, i)
            for selected in order: _run(commands, parts[selected][0], parts[selected][1], ctx, state)
            i = finish
        elif op in ("LOOP", "LOOPTIME"):
            finish = _end(commands, i, op, "ENDLOOP")
            if op == "LOOP":
                remaining = int(args)
                while remaining == 0 or remaining > 0:
                    _run(commands, i + 1, finish, ctx, state)
                    if remaining > 0: remaining -= 1
            else:
                deadline = ctx.now() + float(args)
                while ctx.now() < deadline: _run(commands, i + 1, finish, ctx, state)
            i = finish
        elif op == "PGROUP":
            finish = _end(commands, i, "PGROUP", "ENDPAR")
            _parallel(commands, i + 1, finish, ctx, state); i = finish
        else: raise ValueError("unsupported Game command " + op)
        i += 1


def run_game(commands, ctx):
    state = {"speed": [0, 2000], "pos": [ctx.screen_w // 2, ctx.screen_h // 2],
             "pauses": mouse.PausePlanner()}
    try: _run(commands, 0, len(commands), ctx, state)
    except RuntimeError as exc:
        if str(exc) == "route aborted": _abort()
        raise
    finally:
        state.clear(); gc.collect()
