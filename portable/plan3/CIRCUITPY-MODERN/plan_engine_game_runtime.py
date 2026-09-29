"""Game scheduler/interpreter; core is injected after sequential imports."""
import gc
import random

_core = None
_response_run = None

def _queue_sound_watch(ctx, state):
    winner = ctx.poll_sound_watch()
    if winner is None:
        return
    ctx.suspend_sound_watch()
    state["_response"] = winner

def _event_module():
    import plan_engine_game_events as module
    module.bind(_core)
    return module


def _events(commands, start, end, ctx, state):
    return _event_module().events(commands, start, end, ctx, state)

def _prepare_response(ctx):
    global _response_run
    if _response_run is not None:
        return
    _core._emit_heap(ctx, "before-response-bind")
    import plan_engine_game_response as module
    module.bind(_core)
    _response_run = module.run
    _core._emit_heap(ctx, "after-response-bind")


def _run_response(ctx, name, state):
    if _response_run is None:
        _prepare_response(ctx)
    return _response_run(ctx, name, state, _run)


def _service_pending_response(ctx, state):
    winner = state.get("_response")
    if winner is None:
        return False
    state["_response"] = None
    _core._emit_heap(ctx, "before-response-callback")
    try:
        _run_response(ctx, winner["file"], state)
    finally:
        ctx.resume_sound_watch(winner["cooldown"])
    _core._emit_heap(ctx, "after-response-callback")
    return True

def _parallel(commands, start, end, ctx, state):
    gc.collect(); _core._emit_heap(ctx, "before-parallel-import"); gc.collect()
    try:
        import plan_engine_game_parallel as parallel
    except MemoryError:
        _core._emit_heap(ctx, "parallel-import-memoryerror")
        raise
    gc.collect(); _core._emit_heap(ctx, "after-parallel-import")
    return parallel.run(commands, start, end, ctx, state, _core,
                        _events, _run_response, _service_pending_response)


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
            lambda: _queue_sound_watch(ctx, state))
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
        _service_pending_response(ctx, state)
        i += 1

def run_game(commands, ctx, core):
    global _core
    _core = core
    _prepare_response(ctx)
    labels = {}
    for label_index, item in enumerate(commands):
        if item[0] == "LABEL":
            if not item[1] or item[1] in labels:
                raise ValueError("LABEL needs a unique name")
            labels[item[1]] = label_index
    gc.collect()
    state = {"speed": [0, 2000], "pos": [ctx.screen_w // 2, ctx.screen_h // 2],
             "pauses": None, "_response": None}
    try: _run(commands, 0, len(commands), ctx, state, labels)
    except RuntimeError as exc:
        if str(exc) == "route aborted": _core._abort()
        raise
    finally:
        state.clear(); gc.collect()
