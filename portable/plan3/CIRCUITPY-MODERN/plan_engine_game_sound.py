"""Small SoundWatch resolver loaded before the deep Game scheduler stack."""
import gc
import time

_core = None


def bind(core):
    global _core
    _core = core


def _drain_cancelled_sound(ctx):
    """Consume late ASND frames before the next cast can install a listener."""
    arm = ctx.r.arm
    arm.sound_result = None
    arm.sound_detail = None
    deadline = time.monotonic() + .06
    while time.monotonic() < deadline:
        arm.pump()
        time.sleep(.002)
    arm.sound_result = None
    arm.sound_detail = None


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
    if not isinstance(signal, dict) or "profiles" not in signal:
        return False
    ctx._sound_watch = signal
    winner = resolve_sound_watch(ctx)
    if winner is None:
        winner = signal.get("scope_result")
    state = signal.get("game_state")
    if winner is not None and state is not None:
        ctx.r.arm.send("ASNDCANCEL", 2)
        _drain_cancelled_sound(ctx)
        _core._emit_heap(ctx, "before-response-callback")
        ctx.r.emit("EVT|SOUNDWATCH|response-start|file=%s" % winner["file"])
        ctx.suspend_sound_watch()
        try:
            run_response(ctx, winner["file"], state)
        finally:
            ctx.resume_sound_watch(winner["cooldown"])
        _drain_cancelled_sound(ctx)
        ctx.r.emit("EVT|SOUNDWATCH|response-done|file=%s" % winner["file"])
        _core._emit_heap(ctx, "after-response-callback")
    # Keep the watch, VM state and explicit Cursor alive.  The caller reopens
    # the Flash command file and resumes immediately after the interrupted
    # PGROUP; closing/clearing here used to restart Game from its first command
    # and silently reset the ten-minute LOOPTIME deadline.
    gc.collect()
    return winner is not None