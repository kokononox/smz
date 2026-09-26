import gc
import math
import random

from plan_engine_parse import (PlanAbort, _V2_OPS, _clamp, _load_mouse_pos, _save_mouse_pos,
                               handpath_events, parse_plan, rand_range)
from plan_engine_human import (PausePlanner, _DEFAULT_CFG, plan_move, plan_typing,
                               relative_mouse_events)

class _LightStateChanged(Exception):
    def __init__(self, state_id):
        self.state_id = state_id


class _LiveLightSession:
    def __init__(self, prm, ctx):
        try:
            from live_light_guard import LightStateGuard, state_spec
        except ImportError as exc:
            raise ValueError("STATELOOP needs live_light_guard.py in the portable bundle") from exc
        self.ctx = ctx
        self.prm = prm
        self.routes = {r["id"]: r for r in prm["routes"]}
        specs = [state_spec(r["id"], r["lo"], r["hi"], r["file"]) for r in prm["routes"]]
        self.guard = LightStateGuard(specs, prm["stable"], prm["hysteresis"], prm["timeout"])
        self.current = None
        self.next_poll = -1

    def _lux(self):
        reader = getattr(self.ctx, "read_lux", None)
        if reader is None:
            reader = getattr(self.ctx, "light_lux", None)
        if reader is not None:
            try:
                value = reader()
                return None if value is None else float(value)
            except Exception:
                return None
        # Compatibility fallback for the current firmware context. It probes
        # each range with the existing one-shot wait API; new firmware exposes
        # read_lux() so all ranges share one BH1750 sample.
        waiter = getattr(self.ctx, "wait_light", None)
        if waiter is None:
            return None
        for route in self.prm["routes"]:
            try:
                if waiter(route["lo"], route["hi"], 0, self.prm["poll"], 0):
                    return (route["lo"] + route["hi"]) / 2.0
            except Exception:
                return None
        return None

    def poll(self, force=False):
        now = int(self.ctx.now() * 1000)
        if not force and self.next_poll > now:
            return
        self.next_poll = now + self.prm["poll"]
        state_id = self.guard.update(self._lux(), now)
        if state_id is None:
            if self.current is not None:
                self.ctx.log("light guard unsafe - stopping")
            raise PlanAbort()
        if self.current is None:
            self.current = state_id
            self.ctx.log("light state -> " + state_id)
        elif state_id != self.current:
            old = self.current
            self.current = state_id
            self.ctx.log("light state %s -> %s" % (old, state_id))
            raise _LightStateChanged(state_id)


def _run_state_loop(prm, ctx, pos, pauses, inc):
    """Run one exported route at a time and switch only at safe step boundaries."""
    session = _LiveLightSession(prm, ctx)
    session.poll(True)
    while True:
        route = session.routes.get(session.current)
        if route is None:
            if prm["fallback"] == "FIRST":
                route = prm["routes"][0]
                session.current = route["id"]
            else:
                raise PlanAbort()
        try:
            sub = parse_plan(ctx.read_plan_file(route["file"]))
        except Exception as exc:
            ctx.log("light route failed: " + str(exc))
            raise PlanAbort()
        setattr(ctx, "_live_light_guard", session)
        try:
            run_plan(sub, ctx, _pos=pos, _pauses=pauses, _inc=inc + (route["file"],))
        except _LightStateChanged:
            # The current route was not allowed to finish another action. The
            # loop immediately loads the newly selected route.
            continue
        finally:
            if getattr(ctx, "_live_light_guard", None) is session:
                delattr(ctx, "_live_light_guard")
        session.poll(True)


# ── executor ─────────────────────────────────────────────────────────────────────────────

def run_plan(ops, ctx, _pos=None, _pauses=None, _inc=()):
    """Executes parsed ops against ctx. ctx provides:
    now()->float seconds, sleep_ms(ms)->bool(False=abort), mmove(x,y),
    mclick(btn,n,hmin,hmax), ktext(hmin,hmax,text), kcombo(vk),
    wait_light(lo,hi,stable,to,mode)->bool, key(vk,hold_ms), log(msg),
    screen_w, screen_h, speed_min, speed_max.
    v2 ops also need a plan_api=2 ctx (code65+): wait_sound(thr,min_ms,to)->True/False
    (None = aborted), trg_sound(thr,min,to,act,rmin,rmax,hmin,hmax), key_combo(vks,hmin,hmax),
    kdown(vk), kup(vk), wheel(delta), raw(line)->reply, setres(w,h), read_plan_file(name)."""
    if any(o in _V2_OPS for o, _ in ops) and getattr(ctx, "plan_api", 1) < 2:
        raise ValueError("this plan uses v2 ops but the firmware ctx is plan_api 1 - flash code65+")
    pauses = _pauses if _pauses is not None else PausePlanner()
    pos = _pos if _pos is not None else _load_mouse_pos(ctx, ops)
    labels = {}
    for _li, (_o, _p) in enumerate(ops):
        if _o == "LABEL":
            labels[_p["name"]] = _li
    inc = tuple(_inc)
    i = 0
    stack = []          # [start_index, remaining(0=forever), deadline_or_None]
    while i < len(ops):
        op, prm = ops[i]
        _gate = getattr(ctx, "gate", None)   # v3: GP3 pause/resume + GP4 stop mid-plan
        if _gate is not None and not _gate():
            raise PlanAbort()
        _live_guard = getattr(ctx, "_live_light_guard", None)
        if _live_guard is not None:
            # Safe handoff point: polling is performed before every operation;
            # a changed state aborts the current route before the next action.
            _live_guard.poll()
        if op == "PLAN":
            pass
        elif op == "SCREEN":
            ctx.screen_w, ctx.screen_h = prm["v"]
            _sr = getattr(ctx, "setres", None)   # v2: forward SETRES once per run (code61 retry)
            if _sr is not None:
                _sr(prm["v"][0], prm["v"][1])
            pos[0] = _clamp(pos[0], 0, max(0, ctx.screen_w - 1))
            pos[1] = _clamp(pos[1], 0, max(0, ctx.screen_h - 1))
            _save_mouse_pos(ctx, pos)
        elif op == "SPEED":
            ctx.speed_min, ctx.speed_max = prm["v"]
        elif op == "DELAY":
            if not ctx.sleep_ms(rand_range(*prm["v"])):
                raise PlanAbort()
        elif op == "LOOP":
            stack.append([i, prm["n"], None])
        elif op == "LOOPTIME":
            stack.append([i, 0, ctx.now() + prm["sec"]])
        elif op == "ENDLOOP":
            top = stack[-1]
            if top[2] is not None:                       # LOOPTIME
                if ctx.now() < top[2]:
                    i = top[0]
                else:
                    stack.pop()
            elif top[1] == 0:                            # forever
                i = top[0]
            else:
                top[1] -= 1
                if top[1] > 0:
                    i = top[0]
                else:
                    stack.pop()
        elif op == "RMOUSE":
            _exec_rmouse(prm, ctx, pauses, pos)
        elif op == "CLICK":
            hold = prm.get("hold", (0, 0))
            ctx.mclick(prm["btn"], prm["n"], hold[0], hold[1])
        elif op == "TYPE":
            for cmd in plan_typing(prm["text"], prm):
                if cmd[0] == "KTEXT":
                    ctx.ktext(cmd[1], cmd[2], cmd[3])
                elif cmd[0] == "DLY":
                    if not ctx.sleep_ms(cmd[1]):
                        raise PlanAbort()
                else:
                    ctx.kcombo(cmd[1])
        elif op == "HANDPATH":
            replay_started_ms = int(ctx.now() * 1000)
            target_due = 0
            for delay, dx, dy in handpath_events(prm["path"], prm.get("mt")):
                target_due += delay
                remaining = max(0, target_due - (int(ctx.now() * 1000) - replay_started_ms))
                if remaining and not ctx.sleep_ms(remaining):
                    raise PlanAbort()
                if dx or dy:
                    ctx.mmove_relative(dx, dy)
        elif op == "STATELOOP":
            # v4: the portable live light guard owns routing and returns only
            # when the keypad stops the run or the sensor becomes unsafe.
            _run_state_loop(prm, ctx, pos, pauses, inc)
        elif op == "WLIGHT":
            ok = ctx.wait_light(prm["lo"], prm["hi"], prm["stable"], prm["to"], prm["mode"])
            if ok and "key" in prm:
                vk, hmn, hmx = prm["key"]
                rmn, rmx = prm.get("react", (80, 180))
                if not ctx.sleep_ms(rand_range(rmn, rmx)):
                    raise PlanAbort()
                ctx.key(vk, rand_range(hmn, hmx))
            ctx.log("wlight " + ("match" if ok else "timeout"))
        elif op == "WSND":
            r = ctx.wait_sound(prm["a"][0], prm["a"][1], prm["a"][2])
            if r is None:
                raise PlanAbort()
            ctx.log("wsnd " + ("heard" if r else "timeout - continue"))
        elif op == "IFSND":
            r = ctx.wait_sound(prm["a"][0], prm["a"][1], prm["a"][2])
            if r is None:
                raise PlanAbort()
            ctx.log("ifsnd " + ("heard -> then" if r else "timeout -> else"))
            if not r:
                i = prm["else_ip"]
                continue
        elif op == "TRGSND":
            a = prm["a"]
            r = ctx.trg_sound(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7])
            if r is None:
                raise PlanAbort()
            ctx.log("trgsnd " + ("fired" if r else "timeout - continue"))
        elif op == "IFLUX":
            ok2 = ctx.wait_light(prm["a"][0], prm["a"][1], prm["a"][2], prm["a"][3], prm["a"][4])
            ctx.log("iflux " + ("match -> then" if ok2 else "timeout -> else"))
            if not ok2:
                i = prm["else_ip"]
                continue
        elif op == "ELSE":
            i = prm["endif_ip"]              # the Then side finished - hop over the Else body
            continue
        elif op == "ENDIF":
            pass
        elif op == "MOVETO":
            _exec_rmouse(prm, ctx, pauses, pos, (prm["x"], prm["y"]))
        elif op == "KEY":
            hold = prm.get("hold", (0, 0))
            ctx.key_combo(prm["combo"], hold[0], hold[1])
        elif op == "KDOWN":
            ctx.kdown(prm["v"])
        elif op == "KUP":
            ctx.kup(prm["v"])
        elif op == "WHEEL":
            ctx.wheel(prm["v"])
        elif op == "RAW":
            if ctx.raw(prm["line"]) is None:
                raise PlanAbort()
        elif op == "LABEL":
            pass
        elif op == "GOTO":
            lip = labels.get(prm["name"])
            if lip is None:                  # parse-time validated; belt and braces
                ctx.log("goto: label not found: " + prm["name"] + " - plan stopped")
                return
            while stack and not (stack[-1][0] < lip < ops[stack[-1][0]][1].get("end_ip", 1 << 30)):
                stack.pop()                  # the app's GotoSignal unwinding, plan-style
            i = lip
            continue
        elif op == "RETRY":
            # retryAttempt: bounded login attempt, success light gate, Esc recovery,
            # then alarm + human review pause. Resume continues only after re-validation.
            success = False
            for attempt in range(1, prm["max_attempts"] + 1):
                ctx.log("retryAttempt attempt=%d/%d" % (attempt, prm["max_attempts"]))
                run_plan(prm["prog"], ctx, _pos=pos, _pauses=pauses, _inc=inc)
                wait_state = getattr(ctx, "retry_wait_state", None)
                if wait_state is None:
                    raise ValueError("RETRY needs ctx.retry_wait_state")
                if wait_state(prm["center"], prm["tolerance"], prm["stable"], prm["timeout"]):
                    ctx.log("retryAttempt success")
                    success = True
                    break
                ctx.log("retryAttempt timeout")
                if prm["timeout_action"] == "esc":
                    ctx.key_combo([27], 0, 0)
                if attempt < prm["max_attempts"] and not ctx.sleep_ms(1000):
                    raise PlanAbort()
            if not success:
                alarm = getattr(ctx, "retry_alarm", None)
                if alarm is not None:
                    alarm("retryAttempt exhausted")
                review = getattr(ctx, "pause_for_review", None)
                if review is None or not review():
                    raise PlanAbort()
                if not wait_state(prm["center"], prm["tolerance"], prm["stable"], prm["timeout"]):
                    raise PlanAbort()
                ctx.log("retryAttempt resumed after review")
        elif op == "RPKG":
            # randomPackage: a fresh draw on every pass, never a frozen playback
            progs = prm["progs"]
            order = list(range(len(progs)))
            if prm["mode"] != "seq":
                for _k in range(len(order) - 1, 0, -1):      # Fisher-Yates on the C# rand shim
                    _j = _below(_k + 1)
                    order[_k], order[_j] = order[_j], order[_k]
                if prm["mode"] == "pick":
                    order = order[:rand_range(prm["mn"], prm["mx"])]
            ctx.log("package: %d of %d" % (len(order), len(progs)))
            for _ix in order:
                run_plan(progs[_ix], ctx, _pos=pos, _pauses=pauses, _inc=inc)
        elif op == "PGROUP":
            # The scheduler is deliberately lazy: Login/DC routes must not pay
            # its ~9 KB source/bytecode cost before a PGROUP is actually reached.
            gc.collect()
            from plan_engine_parallel import run_parallel
            run_parallel(prm, ctx, pos, pauses, inc)
        elif op == "BEEP":
            _bp = getattr(ctx, "beep", None)
            if _bp is None:
                raise ValueError("BEEP needs a plan_api>=3 firmware (buzzer pin)")
            _bp(prm["v"][0], prm["v"][1])
        elif op == "INCLUDE":
            nm = prm["file"]
            if nm in inc:
                ctx.log("include cycle: " + nm + " - skipped")
            elif len(inc) >= 4:
                ctx.log("include depth cap (4): " + nm + " - skipped")
            else:
                try:
                    sub = parse_plan(ctx.read_plan_file(nm))
                except Exception as exc:
                    ctx.log("include failed: " + nm + ": " + str(exc))   # app's playScript is non-fatal too
                else:
                    run_plan(sub, ctx, _pos=pos, _pauses=pauses, _inc=inc + (nm,))
        i += 1


def _exec_rmouse(prm, ctx, pauses, pos, target=None):
    rx, ry, rw, rh = prm.get("region", (0, 0, ctx.screen_w, ctx.screen_h))   # v2: MOVETO has no region
    relative = target is None and getattr(ctx, "mouse_mode", "") == "relative"
    if target is not None and getattr(ctx, "mouse_mode", "") == "relative":
        # MOVETO is an absolute contract. Pretending it is portable would
        # silently reintroduce the old centre/origin jump.
        raise ValueError("MOVETO needs an absolute cursor origin")
    c = dict(_DEFAULT_CFG)
    c["speed_min"], c["speed_max"] = ctx.speed_min, ctx.speed_max
    if "speed" in prm:
        c["speed_min"], c["speed_max"] = prm["speed"]
    if "mt" in prm:
        c["mt_min"], c["mt_max"] = prm["mt"]
    if "curve" in prm:
        c["curve_min"], c["curve_max"] = prm["curve"]
    if "before" in prm:
        c["before_min"], c["before_max"] = prm["before"]
    if "after" in prm:
        c["after_min"], c["after_max"] = prm["after"]
    if "mid" in prm:
        c["mid_chance"], (c["mid_min"], c["mid_max"]) = prm["mid"]
    if "idle" in prm:
        (c["idle_every_min"], c["idle_every_max"]), (c["idle_pause_min"], c["idle_pause_max"]) = prm["idle"]
    if "over" in prm:
        c["over_chance"] = prm["over"]
    if relative:
        # No HID device can query the Windows cursor position. Shape the move
        # around a virtual centre, then transmit only point-to-point deltas.
        # Region dimensions limit the wandering distance; its absolute x/y are
        # intentionally ignored because they cannot be honoured hostlessly.
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
        tx, ty = target                      # v2 MOVETO: the app's fixed human move target
    if prm.get("human", 1) == 0:
        # v2 MOVETO human=0 - the app's human=off instant firmware move (single jump)
        if relative:
            ctx.mmove_relative(tx - pos[0], ty - pos[1])
        else:
            ctx.mmove(tx, ty)
        pos[0], pos[1] = tx, ty
        if not relative:
            _save_mouse_pos(ctx, pos)
        return
    if relative:
        # Parsing leaves many short-lived strings behind. Collect before the
        # first move, then use the bounded streaming path instead of allocating
        # the normal dense point, segment and weight lists.
        gc.collect()
        ctx.log("rmouse-rel-stream -> (%+d,%+d) <=32 pts" %
                (tx - pos[0], ty - pos[1]))
        for event in relative_mouse_events(pos, tx, ty, c, pauses):
            if event[0] == "wait":
                if event[1] and not ctx.sleep_ms(event[1]):
                    raise PlanAbort()
            else:
                if event[2] or event[3]:
                    ctx.mmove_relative(event[2], event[3])
                if event[1] and not ctx.sleep_ms(event[1]):
                    raise PlanAbort()
        return
    plan = plan_move(pos[0], pos[1], tx, ty, c, pauses, ctx.screen_w, ctx.screen_h)
    if relative:
        ctx.log("rmouse-rel -> (%+d,%+d) %d pts" %
                (tx - pos[0], ty - pos[1], len(plan["pts"])))
    else:
        ctx.log("rmouse -> (%d,%d) %d pts" % (tx, ty, len(plan["pts"])))
    if not ctx.sleep_ms(plan["before"]):
        raise PlanAbort()
    previous_x, previous_y = pos[0], pos[1]
    for pt in plan["pts"]:
        if relative:
            dx, dy = pt[0] - previous_x, pt[1] - previous_y
            if dx or dy:
                ctx.mmove_relative(dx, dy)
            previous_x, previous_y = pt[0], pt[1]
        else:
            ctx.mmove(pt[0], pt[1])
        # Persist immediately, before the abortable delay: Stop in the middle of
        # a path resumes from the last point that was actually sent to the arm.
        pos[0], pos[1] = pt[0], pt[1]
        if not relative:
            _save_mouse_pos(ctx, pos)
        if not ctx.sleep_ms(pt[2]):
            raise PlanAbort()
    pos[0], pos[1] = plan["target"]
    if not relative:
        _save_mouse_pos(ctx, pos)
    if not ctx.sleep_ms(plan["after"]):
        raise PlanAbort()
    if plan["long"] > 0:
        ctx.log("idle break %d ms" % plan["long"])
        if not ctx.sleep_ms(plan["long"]):
            raise PlanAbort()
