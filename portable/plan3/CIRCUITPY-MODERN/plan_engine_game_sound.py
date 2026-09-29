"""Small SoundWatch resolver loaded before the deep Game scheduler stack."""
import gc

_core = None


def bind(core):
    global _core
    _core = core


def _select_profile(profiles, peak):
    # Keep the post-ASND hot path allocation-free.  Importing the 32 KB
    # plan parser here used to create a second compiler peak on RP2040.
    winner = None
    for item in profiles:
        if not (item["peak_min"] <= peak <= item["peak_max"]):
            continue
        if (winner is None or item["priority"] > winner["priority"]
                or (item["priority"] == winner["priority"]
                    and item["peak_min"] > winner["peak_min"])):
            winner = item
    return winner


def resolve_sound_watch(ctx):
    ctx.poll_sound_watch()
    pending = ctx.take_sound_watch()
    if not pending:
        return None
    if isinstance(pending, dict):
        return pending
    watch = ctx._sound_watch
    if watch is None:
        return None
    peak = ctx.sound_peak()
    if peak is None:
        ctx.r.emit("EVT|SOUNDWATCH|ignored|reason=no-peak")
        ctx._arm_sound_watch()
        return None
    profiles = watch["armed"]
    winner = _select_profile(profiles if profiles is not None else (), peak)
    if winner is None:
        ctx.r.emit("EVT|SOUNDWATCH|ignored|peak=%d" % peak)
        ctx._arm_sound_watch()
        return None
    ctx.r.emit("EVT|SOUNDWATCH|detected|profile=%s|peak=%d|priority=%d" %
               (winner["id"], peak, winner["priority"]))
    if winner["mode"] == "scoped":
        watch["scope_result"] = winner
        return None
    return winner


def service_pending_response(ctx, state, run_response):
    if not state["watch"]:
        return False
    winner = resolve_sound_watch(ctx)
    if winner is None:
        return False
    state["_response"] = None
    _core._emit_heap(ctx, "before-response-callback")
    ctx.suspend_sound_watch()
    try:
        run_response(ctx, winner["file"], state)
    finally:
        ctx.resume_sound_watch(winner["cooldown"])
    _core._emit_heap(ctx, "after-response-callback")
    return True


def service_sound_exit(ctx, signal, run_response):
    # Called only after plan_engine_game_parallel, runtime._run, run_game_file
    # and code._run_light_route have all returned.
    ctx._sound_watch = signal
    winner = resolve_sound_watch(ctx)
    if winner is None:
        winner = signal.get("scope_result")
    state = signal.get("game_state")
    if winner is not None and state is not None:
        ctx.r.arm.send("ASNDCANCEL", 2)
        ctx.r.arm.sound_result = None
        ctx.r.arm.sound_detail = None
        _core._emit_heap(ctx, "before-response-callback")
        ctx.suspend_sound_watch()
        try:
            run_response(ctx, winner["file"], state)
        finally:
            ctx.resume_sound_watch(winner["cooldown"])
        _core._emit_heap(ctx, "after-response-callback")
    ctx.close_sound_watch()
    if state is not None:
        state.clear()
    gc.collect()
    return winner is not None