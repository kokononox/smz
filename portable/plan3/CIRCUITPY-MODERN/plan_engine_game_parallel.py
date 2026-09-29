"""Cooperative Game parallel scheduler, imported only at the first PGROUP."""

def run(commands, start, end, ctx, state, core, events, response_runner,
        execute):
    branches = core._items(commands, start, end, "PARITEM", "PGROUP", "ENDPAR")
    now = int(ctx.now() * 1000)
    tasks = [{"it": events(commands, a, b, ctx, state), "due": now,
              "pending": None, "moving": False, "sound": False,
              "sound_spec": None, "profile": None, "deadline": 0,
              "poll": now} for a, b in branches]
    sound_owner = None; sound_config = None
    try:
        while tasks:
            if not ctx.gate(): core._abort()
            winner = ctx.take_sound_watch() if state["watch"] else None
            if winner is not None:
                core._emit_heap(ctx, "before-response-callback")
                ctx.suspend_sound_watch()
                try:
                    response_runner(ctx, winner["file"], state, execute)
                finally:
                    ctx.resume_sound_watch(winner["cooldown"])
                core._emit_heap(ctx, "after-response-callback")
            now = int(ctx.now() * 1000); progressed = False
            for task in tuple(tasks):
                if task not in tasks: continue
                if task["profile"] is not None:
                    winner = ctx.poll_profile_wait(task["profile"])
                    if winner is not None:
                        ctx.suspend_sound_watch()
                        try:
                            response_runner(ctx, winner["file"], state, execute)
                        finally:
                            ctx.end_profile_wait()
                            ctx.resume_sound_watch(winner["cooldown"])
                        task["profile"] = None
                        tasks[:] = [task]
                        ctx.log("scoped splash heard -> response -> next cast")
                        progressed = True; continue
                    if now >= task["deadline"]:
                        ctx.end_profile_wait(); task["profile"] = None
                        tasks[:] = []
                        ctx.log("scoped splash timeout -> next cast")
                        progressed = True; continue
                    task["poll"] = now + 10
                    continue
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
                            response_runner(ctx, winner["sound_spec"][6], state, execute)
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
                elif kind == "profile":
                    task["moving"] = False; task["profile"] = event[1]
                    task["deadline"] = now + event[2]; task["poll"] = now
                    ctx.begin_profile_wait(event[1])
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
            wake = min(task["poll"] if task["sound"] or task["profile"] is not None
                       else task["due"] for task in tasks)
            if not ctx.sleep_ms(max(1, wake - int(ctx.now() * 1000))): core._abort()
    finally:
        if sound_owner is not None: ctx.sound_cancel()
        if any(task.get("profile") is not None for task in tasks):
            ctx.end_profile_wait()
