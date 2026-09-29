"""Small sequential Game actions shard; kept separate to cap compiler peaks."""
import random

_core = None
_run_response = None
_service_pending_response = None


def bind(core, run_response, service_pending_response):
    global _core, _run_response, _service_pending_response
    _core = core
    _run_response = run_response
    _service_pending_response = service_pending_response


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
                try:
                    _run_response(ctx, winner["file"], state)
                finally:
                    ctx.resume_sound_watch(winner["cooldown"])
                break
            if not ctx.sleep_ms(10):
                _core._abort()
            _service_pending_response(ctx, state)
    finally:
        ctx.end_profile_wait()


def _wait_sound(op, args, ctx):
    values = args.replace(" ", "").split(",")
    if op == "WSND":
        if len(values) != 3:
            raise ValueError("WSND needs threshold,min,timeout")
        threshold, minimum, timeout = int(values[0]), int(values[1]), int(values[2])
    else:
        if len(values) != 5:
            raise ValueError("WSNDP needs id,binding,threshold,min,timeout")
        threshold, minimum = ctx.sound_profile(
            int(values[0]), values[1], int(values[2]), int(values[3]))
        timeout = int(values[4])
    heard = ctx.wait_sound(threshold, minimum, timeout)
    if heard is None:
        _core._abort()
    ctx.log(("wsndp" if op == "WSNDP" else "wsnd") +
            (" heard" if heard else " timeout - continue"))


def _sound(op, args, ctx, state):
    if op == "SOUNDWATCH":
        ctx.install_sound_watch(_core._watch_profiles(args))
        ctx._sound_watch["game_state"] = state
        state["watch"] = True
        ctx.log("soundwatch active")
    elif op == "WPROFILE":
        _profile(args, ctx, state)
    else:
        _wait_sound(op, args, ctx)


def _beep(args, ctx):
    values = [int(v) for v in args.replace(",", " ").split()]
    if len(values) != 2:
        raise ValueError("BEEP needs frequency,duration")
    ctx.beep(values[0], values[1])

def _type(args, ctx):
    import plan_engine_login as helper
    helper.run_type(args, ctx)


def _basic(op, args, ctx, state):
    if op == "PLAN":
        pass
    elif op == "SCREEN":
        values = args.replace(",", " ").split()
        if len(values) != 2:
            raise ValueError("SCREEN needs width,height")
        ctx.screen_w, ctx.screen_h = int(values[0]), int(values[1])
    elif op == "SPEED":
        state["speed"][:] = _core._range(args)
    elif op == "DELAY":
        lo, hi = _core._range(args)
        if not ctx.sleep_ms(random.randint(lo, hi)):
            _core._abort()
    elif op == "KEY":
        combo, hold = _core._key(args)
        ctx.key_combo(combo, hold[0], hold[1])
    elif op == "KDOWN":
        ctx.kdown(int(args))
    elif op == "KUP":
        ctx.kup(int(args))
    elif op == "WHEEL":
        ctx.wheel(int(args))
    elif op == "RAW":
        ctx.raw(args)
    elif op == "BEEP":
        _beep(args, ctx)
    elif op == "TYPE":
        _type(args, ctx)
    else:
        return False
    return True


def leaf(op, args, ctx, state):
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