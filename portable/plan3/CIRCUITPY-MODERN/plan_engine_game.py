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


def _response_commands(ctx, name):
    if not name.endswith(".txt") or "/" in name or "\\" in name:
        raise ValueError("unsafe sound response route")
    commands = []
    allowed = ("PLAN", "SCREEN", "SPEED", "DELAY", "KEY", "KDOWN", "KUP",
               "WHEEL", "RAW", "RMOUSE", "RPKG", "PKGITEM", "ENDPKG",
               "LOOP", "LOOPTIME", "ENDLOOP", "BEEP", "LABEL", "GOTO")
    for raw in ctx.read_plan_file(name).splitlines():
        line = raw.strip()
        if not line or line.startswith("#"): continue
        parts = line.split("|", 1)
        op = parts[0].upper(); args = parts[1] if len(parts) == 2 else ""
        if op not in allowed: raise ValueError("unsupported sound response " + op)
        commands.append((op, args))
    return commands


def _run_response(ctx, name, state):
    commands = _response_commands(ctx, name)
    labels = {}
    for index, item in enumerate(commands):
        if item[0] == "LABEL": labels[item[1]] = index
    ctx.log("sound response start " + name)
    _run(commands, 0, len(commands), ctx, state, labels)
    ctx.log("sound response done " + name)


def _parallel(commands, start, end, ctx, state):
    branches = _items(commands, start, end, "PARITEM", "PGROUP", "ENDPAR")
    now = int(ctx.now() * 1000)
    tasks = [{"it": _events(commands, a, b, ctx, state), "due": now,
              "pending": None, "moving": False, "sound": False,
              "sound_spec": None, "poll": now} for a, b in branches]
    sound_owner = None; sound_config = None
    try:
        while tasks:
            if not ctx.gate(): _abort()
            now = int(ctx.now() * 1000); progressed = False
            for task in tuple(tasks):
                if task not in tasks: continue
                if task["sound"]:
                    if task is not sound_owner or now < task["poll"]: continue
                    concurrent = getattr(ctx, "sound_parallel_safe", None)
                    if (not (concurrent and concurrent()) and
                            any(other is not task and other["moving"] for other in tasks)):
                        task["poll"] = now + 10; continue
                    result = ctx.sound_poll(); task["poll"] = now + 10
                    if result is None: continue
                    waiters = [item for item in tasks if item["sound"]]
                    for item in waiters: item["sound"] = False
                    sound_owner = None; sound_config = None
                    persistent = any(item["sound_spec"][6] for item in waiters)
                    winner = None; peak = None
                    if result:
                        peak_reader = getattr(ctx, "sound_peak", None)
                        peak = peak_reader() if peak_reader is not None else None
                        if len(waiters) > 1 and peak is None:
                            raise ValueError("parallel sound profiles require peak telemetry")
                        eligible = waiters if peak is None else [item for item in waiters
                            if item["sound_spec"][3] <= peak <= item["sound_spec"][4]]
                        if eligible:
                            winner = eligible[0]
                            for item in eligible[1:]:
                                a, b = item["sound_spec"], winner["sound_spec"]
                                if a[5] > b[5] or (a[5] == b[5] and a[0] > b[0]): winner = item
                    if persistent:
                        if winner is not None and winner["sound_spec"][6]:
                            _run_response(ctx, winner["sound_spec"][6], state)
                        resumed = int(ctx.now() * 1000)
                        cooldown = winner["sound_spec"][7] if winner is not None else 0
                        for item in waiters: item["due"] = max(item["due"], resumed + cooldown)
                        ctx.log("sound interrupt resume peak=%s" % str(peak))
                    elif result and winner is not None:
                        tasks[:] = [winner]
                        ctx.log("parallel wsnd heard - cancel siblings")
                    else:
                        tasks[:] = []
                        ctx.log("parallel wsnd timeout - cancel group")
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
                    task["sound"] = True; task["sound_spec"] = tuple(event[1:])
                    waiters = [item for item in tasks if item["sound"]]
                    threshold = min(item["sound_spec"][0] for item in waiters)
                    minimum = min(item["sound_spec"][1] for item in waiters)
                    timeout = min(item["sound_spec"][2] for item in waiters)
                    config = (threshold, minimum, timeout)
                    if sound_owner is not None and config != sound_config:
                        ctx.sound_cancel(); sound_owner = None
                    if sound_owner is None:
                        ctx.sound_start(threshold, minimum, timeout)
                        sound_owner = waiters[0]; sound_config = config
                    for item in waiters: item["poll"] = now
                progressed = True
            if not tasks: break
            if progressed: continue
            wake = min(task["poll"] if task["sound"] else task["due"] for task in tasks)
            if not ctx.sleep_ms(max(1, wake - int(ctx.now() * 1000))): _abort()
    finally:
        if sound_owner is not None: ctx.sound_cancel()


def _run(commands, start, end, ctx, state, labels):
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
        elif op == "KDOWN": ctx.kdown(int(args))
        elif op == "KUP": ctx.kup(int(args))
        elif op == "WHEEL": ctx.wheel(int(args))
        elif op == "RAW": ctx.raw(args)
        elif op == "BEEP":
            values = [int(v) for v in args.replace(",", " ").split()]
            if len(values) != 2: raise ValueError("BEEP needs frequency,duration")
            ctx.beep(values[0], values[1])
        elif op == "RMOUSE":
            mouse.run_rmouse(args, ctx, state["pauses"], state["pos"], state["speed"])
        elif op in ("WSND", "WSNDP"):
            values = args.replace(" ", "").split(",")
            if op == "WSND":
                if len(values) != 3: raise ValueError("WSND needs threshold,min,timeout")
                threshold, minimum, timeout = int(values[0]), int(values[1]), int(values[2])
            else:
                if len(values) != 5: raise ValueError("WSNDP needs id,binding,threshold,min,timeout")
                threshold, minimum = ctx.sound_profile(
                    int(values[0]), values[1], int(values[2]), int(values[3]))
                timeout = int(values[4])
            heard = ctx.wait_sound(threshold, minimum, timeout)
            if heard is None: _abort()
            ctx.log(("wsndp" if op == "WSNDP" else "wsnd") +
                    (" heard" if heard else " timeout - continue"))
        elif op == "RPKG":
            finish, parts, order = _package(commands, i)
            for selected in order: _run(commands, parts[selected][0], parts[selected][1], ctx, state, labels)
            i = finish
        elif op in ("LOOP", "LOOPTIME"):
            finish = _end(commands, i, op, "ENDLOOP")
            if op == "LOOP":
                remaining = int(args)
                while remaining == 0 or remaining > 0:
                    _run(commands, i + 1, finish, ctx, state, labels)
                    if remaining > 0: remaining -= 1
            else:
                deadline = ctx.now() + float(args)
                while ctx.now() < deadline: _run(commands, i + 1, finish, ctx, state, labels)
            i = finish
        elif op == "PGROUP":
            finish = _end(commands, i, "PGROUP", "ENDPAR")
            _parallel(commands, i + 1, finish, ctx, state); i = finish
        elif op == "LABEL":
            pass
        elif op == "GOTO":
            target = labels.get(args)
            if target is None:
                raise ValueError("GOTO label not found")
            i = target
            continue
        else: raise ValueError("unsupported Game command " + op)
        i += 1


def run_game(commands, ctx):
    labels = {}
    for label_index, item in enumerate(commands):
        if item[0] == "LABEL":
            if not item[1] or item[1] in labels:
                raise ValueError("LABEL needs a unique name")
            labels[item[1]] = label_index
    state = {"speed": [0, 2000], "pos": [ctx.screen_w // 2, ctx.screen_h // 2],
             "pauses": mouse.PausePlanner()}
    try: _run(commands, 0, len(commands), ctx, state, labels)
    except RuntimeError as exc:
        if str(exc) == "route aborted": _abort()
        raise
    finally:
        state.clear(); gc.collect()
