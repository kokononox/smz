"""Game scheduler/interpreter; core is injected after sequential imports."""
import gc
import random

_core = None

def _service_sound_watch(ctx, state):
    winner = ctx.poll_sound_watch()
    if winner is None:
        return
    ctx.suspend_sound_watch()
    try:
        _run_response(ctx, winner["file"], state)
    finally:
        ctx.resume_sound_watch(winner["cooldown"])

def _event_module():
    import plan_engine_game_events as module
    module.bind(_core)
    return module


def _events(commands, start, end, ctx, state):
    return _event_module().events(commands, start, end, ctx, state)

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
    gc.collect(); _core._emit_heap(ctx, "before-parallel-import"); gc.collect()
    try:
        import plan_engine_game_parallel as parallel
    except MemoryError:
        _core._emit_heap(ctx, "parallel-import-memoryerror")
        raise
    gc.collect(); _core._emit_heap(ctx, "after-parallel-import")
    return parallel.run(commands, start, end, ctx, state, _core,
                        _events, _run_response)


def _profile(args, ctx, state):
    values = args.replace(" ", "").split(",")
    lo, hi = int(values[1]), int(values[2])
    deadline = ctx.now() + random.randint(min(lo, hi), max(lo, hi)) / 1000
    ctx.begin_profile_wait(values[0])
    try:
        while ctx.now() < deadline:
            winner = ctx.poll_profile_wait(values[0])
            if winner is not None:
                ctx.suspend_sound_watch()
                try: _run_response(ctx, winner["file"], state)
                finally: ctx.resume_sound_watch(winner["cooldown"])
                break
            if not ctx.sleep_ms(10): _core._abort()
    finally:
        ctx.end_profile_wait()


def _wait_sound(op, args, ctx):
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
    if heard is None: _core._abort()
    ctx.log(("wsndp" if op == "WSNDP" else "wsnd") +
            (" heard" if heard else " timeout - continue"))


def _sound(op, args, ctx, state):
    if op == "SOUNDWATCH":
        ctx.install_sound_watch(_core._watch_profiles(args),
            lambda: _service_sound_watch(ctx, state))
        ctx.log("soundwatch active")
    elif op == "WPROFILE":
        _profile(args, ctx, state)
    else:
        _wait_sound(op, args, ctx)


def _beep(args, ctx):
    values = [int(v) for v in args.replace(",", " ").split()]
    if len(values) != 2: raise ValueError("BEEP needs frequency,duration")
    ctx.beep(values[0], values[1])


def _basic(op, args, ctx, state):
    if op == "PLAN": pass
    elif op == "SCREEN":
        values = args.replace(",", " ").split()
        if len(values) != 2: raise ValueError("SCREEN needs width,height")
        ctx.screen_w, ctx.screen_h = int(values[0]), int(values[1])
    elif op == "SPEED": state["speed"][:] = _core._range(args)
    elif op == "DELAY":
        lo, hi = _core._range(args)
        if not ctx.sleep_ms(random.randint(lo, hi)): _core._abort()
    elif op == "KEY":
        combo, hold = _core._key(args); ctx.key_combo(combo, hold[0], hold[1])
    elif op == "KDOWN": ctx.kdown(int(args))
    elif op == "KUP": ctx.kup(int(args))
    elif op == "WHEEL": ctx.wheel(int(args))
    elif op == "RAW": ctx.raw(args)
    elif op == "BEEP": _beep(args, ctx)
    else:
        return False
    return True


def _leaf(op, args, ctx, state):
    if _basic(op, args, ctx, state):
        return True
    if op == "RMOUSE":
        helper = _core._mouse(ctx, state)
        helper.run_rmouse(args, ctx, state["pauses"], state["pos"], state["speed"])
        return True
    if op in ("SOUNDWATCH", "WPROFILE", "WSND", "WSNDP"):
        _sound(op, args, ctx, state)
        return True
    return False

def _run(commands, start, end, ctx, state, labels):
    i = start
    while i < end:
        op, args = commands[i]
        if _leaf(op, args, ctx, state):
            pass
        elif op == "RPKG":
            finish, parts, order = _core._package(commands, i)
            for selected in order:
                _run(commands, parts[selected][0], parts[selected][1], ctx, state, labels)
            i = finish
        elif op in ("LOOP", "LOOPTIME"):
            finish = _core._end(commands, i, op, "ENDLOOP")
            if op == "LOOP":
                remaining = int(args)
                while remaining == 0 or remaining > 0:
                    _run(commands, i + 1, finish, ctx, state, labels)
                    if remaining > 0: remaining -= 1
            else:
                deadline = ctx.now() + float(args)
                while ctx.now() < deadline:
                    _run(commands, i + 1, finish, ctx, state, labels)
            i = finish
        elif op == "PGROUP":
            finish = _core._end(commands, i, "PGROUP", "ENDPAR")
            _parallel(commands, i + 1, finish, ctx, state); i = finish
        elif op == "LABEL":
            pass
        elif op == "GOTO":
            target = labels.get(args)
            if target is None: raise ValueError("GOTO label not found")
            i = target; continue
        else:
            raise ValueError("unsupported Game command " + op)
        i += 1

def run_game(commands, ctx, core):
    global _core
    _core = core
    labels = {}
    for label_index, item in enumerate(commands):
        if item[0] == "LABEL":
            if not item[1] or item[1] in labels:
                raise ValueError("LABEL needs a unique name")
            labels[item[1]] = label_index
    gc.collect()
    state = {"speed": [0, 2000], "pos": [ctx.screen_w // 2, ctx.screen_h // 2],
             "pauses": None}
    try: _run(commands, 0, len(commands), ctx, state, labels)
    except RuntimeError as exc:
        if str(exc) == "route aborted": _core._abort()
        raise
    finally:
        state.clear(); gc.collect()
