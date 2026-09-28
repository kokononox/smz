# Cooperative PGROUP scheduler. Imported only when a route reaches PGROUP so
# ordinary Desktop/Login routes keep the low-memory split executor footprint.
import gc
import random
from plan_engine_parse import PlanAbort, _below, rand_range
from plan_engine_human import (_DEFAULT_CFG, plan_move, plan_typing,
                               relative_mouse_events)

def _parallel_relative_mouse_events(prm, ctx, pauses, pos, c, tx, ty):
    """Adapt the shared bounded relative stream to scheduler move events."""
    for event in relative_mouse_events(pos, tx, ty, c, pauses):
        if event[0] == "wait":
            yield event
        else:
            yield ("move", event[1], event[2], event[3], True)


def _parallel_mouse_events(prm, ctx, pauses, pos, target=None):
    """Yield one mouse report at a time instead of owning the executor."""
    rx, ry, rw, rh = prm.get("region", (0, 0, ctx.screen_w, ctx.screen_h))
    relative = target is None and getattr(ctx, "mouse_mode", "") == "relative"
    if target is not None and getattr(ctx, "mouse_mode", "") == "relative":
        raise ValueError("MOVETO needs an absolute cursor origin")
    c = dict(_DEFAULT_CFG)
    c["speed_min"], c["speed_max"] = ctx.speed_min, ctx.speed_max
    for key, names in (
        ("speed", ("speed_min", "speed_max")), ("mt", ("mt_min", "mt_max")),
        ("curve", ("curve_min", "curve_max")), ("before", ("before_min", "before_max")),
        ("after", ("after_min", "after_max"))):
        if key in prm:
            c[names[0]], c[names[1]] = prm[key]
    if "mid" in prm:
        c["mid_chance"], (c["mid_min"], c["mid_max"]) = prm["mid"]
    if "idle" in prm:
        (c["idle_every_min"], c["idle_every_max"]), (
            c["idle_pause_min"], c["idle_pause_max"]) = prm["idle"]
    if "over" in prm:
        c["over_chance"] = prm["over"]
    if relative:
        sx, sy = ctx.screen_w // 2, ctx.screen_h // 2
        xlim = max(1, min(max(1, rw - 1), max(32, ctx.screen_w // 4)))
        ylim = max(1, min(max(1, rh - 1), max(32, ctx.screen_h // 4)))
        xmag = random.randint(max(1, xlim // 3), xlim)
        ymag = random.randint(max(1, ylim // 3), ylim)
        tx = sx + (xmag if random.randint(0, 1) else -xmag)
        ty = sy + (ymag if random.randint(0, 1) else -ymag)
        pos[0], pos[1] = sx, sy
    elif target is None:
        tx = random.randint(rx, rx + max(0, rw - 1))
        ty = random.randint(ry, ry + max(0, rh - 1))
    else:
        tx, ty = target
    if prm.get("human", 1) == 0:
        yield ("move", 0, tx - pos[0], ty - pos[1], relative)
        pos[0], pos[1] = tx, ty
        return
    if relative:
        for event in _parallel_relative_mouse_events(prm, ctx, pauses, pos, c, tx, ty):
            yield event
        return
    plan = plan_move(pos[0], pos[1], tx, ty, c, pauses, ctx.screen_w, ctx.screen_h)
    if plan["before"]:
        yield ("wait", plan["before"])
    px, py = pos[0], pos[1]
    for pt in plan["pts"]:
        yield ("move", pt[2], pt[0] - px if relative else pt[0],
               pt[1] - py if relative else pt[1], relative)
        px, py = pt[0], pt[1]
        pos[0], pos[1] = px, py
    pos[0], pos[1] = plan["target"]
    if plan["after"]:
        yield ("wait", plan["after"])
    if plan["long"]:
        yield ("wait", plan["long"])


def _parallel_events(ops, ctx, pos, pauses, inc):
    """Flatten a branch lazily; loops/packages are expanded only when reached."""
    i = 0
    stack = []
    while i < len(ops):
        op, prm = ops[i]
        if op == "PLAN":
            pass
        elif op == "DELAY":
            yield ("wait", rand_range(*prm["v"]))
        elif op == "LOOP":
            stack.append([i, prm["n"], None])
        elif op == "LOOPTIME":
            stack.append([i, 0, ctx.now() + prm["sec"]])
        elif op == "ENDLOOP":
            top = stack[-1]
            if top[2] is not None:
                if ctx.now() < top[2]:
                    i = top[0]
                else:
                    stack.pop()
            elif top[1] == 0:
                i = top[0]
            else:
                top[1] -= 1
                if top[1] > 0:
                    i = top[0]
                else:
                    stack.pop()
        elif op == "RPKG":
            progs = prm["progs"]
            order = list(range(len(progs)))
            if prm["mode"] != "seq":
                for k in range(len(order) - 1, 0, -1):
                    j = _below(k + 1)
                    order[k], order[j] = order[j], order[k]
                if prm["mode"] == "pick":
                    order = order[:rand_range(prm["mn"], prm["mx"])]
            ctx.log("package: %d of %d" % (len(order), len(progs)))
            for item in order:
                for event in _parallel_events(progs[item], ctx, pos, pauses, inc):
                    yield event
        elif op == "HANDPATH":
            raw = prm["path"]
            start = 0
            while start < len(raw):
                end = raw.find(";", start)
                if end < 0:
                    end = len(raw)
                delay, dx, dy = (int(v) for v in raw[start:end].split(",", 2))
                yield ("move", delay, dx, dy, True)
                start = end + 1
        elif op == "TYPE":
            for cmd in plan_typing(prm["text"], prm):
                if cmd[0] == "KTEXT":
                    for ch in cmd[3]:
                        yield ("char", rand_range(cmd[1], cmd[2]), ch)
                elif cmd[0] == "DLY":
                    yield ("wait", cmd[1])
                else:
                    yield ("combo", cmd[1])
        elif op in ("RMOUSE", "MOVETO"):
            # Reclaim the prior streamed move and SCAL reply garbage before
            # planning the next random pass in an unbounded fishing loop.
            gc.collect()
            target = (prm["x"], prm["y"]) if op == "MOVETO" else None
            for event in _parallel_mouse_events(prm, ctx, pauses, pos, target):
                yield event
        elif op == "WSND":
            yield ("sound", prm["a"][0], prm["a"][1], prm["a"][2])
        elif op == "WSNDP":
            threshold, minimum = ctx.sound_profile(
                prm["profile_id"], prm["binding"], prm["threshold"], prm["minimum"])
            yield ("sound", threshold, minimum, prm["timeout"])
        else:
            yield ("op", op, prm)
        i += 1


def run_parallel(prm, ctx, pos, pauses, inc):
    """Deadline scheduler for Pico keyboard plus Pro Micro mouse/sound."""
    now = int(ctx.now() * 1000)
    tasks = [{"it": _parallel_events(prog, ctx, pos, pauses, inc),
              "due": now, "pending": None, "moving": False,
              "sound": False, "sound_spec": None, "poll": now}
             for prog in prm["progs"]]
    sound_owner = None
    sound_config = None
    start_sound = getattr(ctx, "sound_start", None)
    poll_sound = getattr(ctx, "sound_poll", None)
    cancel_sound = getattr(ctx, "sound_cancel", None)
    try:
        while tasks:
            if not ctx.gate():
                raise PlanAbort()
            now = int(ctx.now() * 1000)
            progressed = False
            for task in tuple(tasks):
                if task not in tasks:
                    continue
                if task["sound"]:
                    # Multiple logical listeners share one physical ADC. Only
                    # the owner polls it; sibling listeners remain registered
                    # for peak-based classification.
                    if task is not sound_owner:
                        continue
                    if now < task["poll"]:
                        continue
                    # SCAL is a synchronous ARM transaction and its UART
                    # round-trip is ~60 ms on hardware. Never insert that gap
                    # between streamed mouse segments: sample during the
                    # before/after/package waits instead, when the cursor is
                    # intentionally stationary.
                    if any(other is not task and other["moving"] for other in tasks):
                        task["poll"] = now + 10
                        continue
                    result = poll_sound()
                    task["poll"] = now + 10
                    if result is None:
                        continue
                    waiters = [item for item in tasks if item["sound"]]
                    for item in waiters:
                        item["sound"] = False
                    sound_owner = None
                    sound_config = None
                    if result:
                        # One ARM listener runs at the lowest threshold. The
                        # reported peak selects the highest matching logical
                        # profile, so low/high calibrated waits can race on the
                        # same physical sensor without opening two ADC jobs.
                        peak_reader = getattr(ctx, "sound_peak", None)
                        peak = peak_reader() if peak_reader is not None else None
                        if len(waiters) > 1 and peak is None:
                            raise ValueError(
                                "parallel sound profiles require peak telemetry")
                        eligible = waiters if peak is None else [
                            item for item in waiters
                            if peak >= item["sound_spec"][0]]
                        if not eligible:
                            tasks[:] = []
                        else:
                            winner = eligible[0]
                            for item in eligible[1:]:
                                if item["sound_spec"][0] > winner["sound_spec"][0]:
                                    winner = item
                            tasks[:] = [winner]
                            ctx.log("parallel wsnd profile threshold=%d peak=%s" %
                                    (winner["sound_spec"][0], str(peak)))
                    else:
                        # A timed-out fishing race must also stop its infinite
                        # mouse sibling. End PGROUP without executing the
                        # reaction that follows WSND; the outer loop can cast
                        # again instead of remaining trapped in mouse motion.
                        tasks[:] = []
                    ctx.log("parallel wsnd " + (
                        "heard - cancel siblings" if result
                        else "timeout - cancel group"))
                    progressed = True
                    continue
                if now < task["due"]:
                    continue
                pending = task["pending"]
                if pending is not None:
                    task["pending"] = None
                    if pending[4]:
                        if pending[2] or pending[3]:
                            ctx.mmove_relative(pending[2], pending[3])
                    else:
                        ctx.mmove(pending[2], pending[3])
                    progressed = True
                    continue
                try:
                    event = next(task["it"])
                except StopIteration:
                    tasks.remove(task)
                    progressed = True
                    continue
                kind = event[0]
                if kind == "wait":
                    task["moving"] = False
                    task["due"] = now + max(0, event[1])
                elif kind == "move":
                    task["moving"] = True
                    task["due"] = now + max(0, event[1])
                    task["pending"] = event
                elif kind == "char":
                    writer = getattr(ctx, "type_char", None)
                    if writer is None:
                        ctx.ktext(0, 0, event[2])
                    else:
                        writer(event[2])
                    task["due"] = now + max(0, event[1])
                elif kind == "combo":
                    ctx.kcombo(event[1])
                elif kind == "sound":
                    task["sound"] = True
                    task["sound_spec"] = (event[1], event[2], event[3])
                    waiters = [item for item in tasks if item["sound"]]
                    if start_sound is None or poll_sound is None:
                        if len(waiters) > 1:
                            raise ValueError(
                                "parallel sound profiles require async ARM sound")
                        result = ctx.wait_sound(event[1], event[2], event[3])
                        if result:
                            tasks[:] = [task]
                        else:
                            tasks[:] = []
                        ctx.log("parallel wsnd " + (
                            "heard - cancel siblings" if result
                            else "timeout - cancel group"))
                    else:
                        threshold = min(item["sound_spec"][0] for item in waiters)
                        minimum = min(item["sound_spec"][1] for item in waiters)
                        timeout = min(item["sound_spec"][2] for item in waiters)
                        config = (threshold, minimum, timeout)
                        if sound_owner is not None and config != sound_config:
                            cancel_sound()
                            sound_owner = None
                        if sound_owner is None:
                            start_sound(threshold, minimum, timeout)
                            sound_owner = waiters[0]
                            sound_config = config
                        for item in waiters:
                            item["poll"] = now
                else:
                    from plan_engine_exec import run_plan
                    run_plan([(event[1], event[2])], ctx, _pos=pos,
                             _pauses=pauses, _inc=inc)
                progressed = True
            if not tasks:
                break
            if progressed:
                continue
            wake = min(task["poll"] if task["sound"] else task["due"] for task in tasks)
            if not ctx.sleep_ms(max(1, wake - int(ctx.now() * 1000))):
                raise PlanAbort()
    finally:
        if sound_owner is not None and cancel_sound is not None:
            cancel_sound()


