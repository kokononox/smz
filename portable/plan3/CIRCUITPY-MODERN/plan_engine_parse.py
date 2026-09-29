# plan_engine.py - Classroom Studio portable plan engine v2 (code65 line)
# Runs on the Pico (CircuitPython) and on CPython (the sim). Exact port of the app's
# humanization layers: WindMouse (HumanMouse.cs v0.9.24) and the typing planner
# (StepDefinitions.TypeTextCommands v0.9.15). Fresh randomness is rolled for every pass,
# so repeated loops never replay an identical path - exactly like the PC app.
#
# plan.txt - one op per line, pipe-separated, key=value args, %-encoding inside text:
#   PLAN|1                                header (format version, must be first)
#   SCREEN|1920,1080                      clamp bounds for all moves
#   SPEED|0,2000                          app mouse speed px/s (max 0 = shape-only pacing)
#   RMOUSE|region=x,y,w,h|mt=mn,mx|curve=mn,mx|before=mn,mx|after=mn,mx|
#         mid=chance:mn,mx|idle=everyMn,everyMx:pauseMn,pauseMx|over=pct
#   CLICK|btn=left|n=1|hold=mn,mx
#   TYPE|h=mn,mx|w=mn,mx|wp=pct|p=mn,mx|think=chance:mn,mx|typochars=mn,mx|text=<encoded>
#        typochars is the eligible-character interval between corrected slips.
#        Legacy typos=min,max count mode remains readable.
#        Legacy typo=mn,mx (one slip every N words) remains accepted.
#   DELAY|mn,mx
#   LOOP|n  ... ENDLOOP                   n = pass count, 0 = forever
#   LOOPTIME|sec ... ENDLOOP              repeats until the deadline passes
#   WLIGHT|lo,hi,stable_ms,timeout_ms,mode[|key=vk,hmn,hmx|react=mn,mx]
#
# plan v2 (PLAN|2) - Classroom Studio parity ops:
#   MOVETO|x=..,y=..[,human=0][,before=mn,mx][,after=mn,mx][,mid=c:mn,mx]
#         [,idle=eMn,eMx:pMn,pMx][,over=pct][,curve=mn,mx][,mt=mn,mx]
#   KEY|combo=vk+vk+...[,hold=mn,mx] · KDOWN|vk · KUP|vk · WHEEL|delta
#   WSND|thr,min_ms,timeout_ms          plain wait on the arm's sound sensor; a timeout
#                                       just continues (exactly the app's no-IfElse case)
#   TRGSND|thr,min_ms,timeout,act,rmin,rmax,hmin,hmax   armed: the ARM board clicks itself
#   IFSND|thr,min_ms,timeout_ms ... [ELSE] ... ENDIF    heard -> Then, timeout -> Else
#   IFLUX|lo,hi,stable,to,mode ... [ELSE] ... ENDIF     (light parity with the app)
#   LABEL|name · GOTO|name    a label is visible only on the goto's own block chain
#   INCLUDE|file=name.plan.txt           depth cap 4 + cycle guard (the app's playScript)
#   RAW|any|board|line                   escape hatch, routed like the host handle()
# Blank lines and lines starting with # are ignored. Unknown ops fail the whole file.

import time
import math
import random


class PlanAbort(Exception):
    """Raised inside a step when the host took over or the keypad stopped the run."""


def select_sound_profile(profiles, peak):
    # Keep the hot SoundWatch path allocation-free.  A list comprehension
    # creates both a temporary list and another Python frame on CircuitPython;
    # that is unsafe immediately after ASND completes inside a scheduler sleep.
    winner = None
    for item in profiles:
        if not (item["peak_min"] <= peak <= item["peak_max"]):
            continue
        if (winner is None or item["priority"] > winner["priority"]
                or (item["priority"] == winner["priority"]
                    and item["peak_min"] > winner["peak_min"])):
            winner = item
    return winner


# code64: optional persistent cursor contract. The Pico context owns the saved
# coordinate so Start/Stop does not reset every new run_plan() call to screen centre.
def _load_mouse_pos(ctx, ops):
    sw, sh = ctx.screen_w, ctx.screen_h
    for op, prm in ops:
        if op == "SCREEN":
            sw, sh = prm["v"]
            break
    # Fully portable mode uses genuine relative HID reports on the Pro Micro.
    # The coordinates below are only a virtual canvas for shaping a human path;
    # they are never sent as an absolute Windows cursor position.
    if getattr(ctx, "mouse_mode", "") == "relative":
        return [sw // 2, sh // 2]
    saved = None
    getter = getattr(ctx, "get_mouse_pos", None)
    if getter is not None:
        try:
            saved = getter()
        except Exception:
            saved = None
        if saved is None or len(saved) < 2:
            # A Pico runtime with cursor-state support must never silently
            # teleport the human mouse to screen centre. The host must send
            # CURSOR|x,y before a mouse Route; legacy contexts without this
            # API retain the historical centre fallback below.
            raise ValueError("cursor origin unavailable")
    if saved is None or len(saved) < 2:
        return [sw // 2, sh // 2]
    return [_clamp(int(saved[0]), 0, max(0, sw - 1)),
            _clamp(int(saved[1]), 0, max(0, sh - 1))]


def _save_mouse_pos(ctx, pos):
    if getattr(ctx, "mouse_mode", "") == "relative":
        return
    setter = getattr(ctx, "set_mouse_pos", None)
    if setter is not None:
        setter(int(pos[0]), int(pos[1]))


# ── rng helpers (mirror HumanMouse.Rand / RandRange: swap-tolerant, max<=0 → 0) ─────────

def _rf():
    return random.random()


def _below(n):            # C# Random.Next(n): 0..n-1
    return random.randrange(n) if n > 0 else 0


def rand_range(mn, mx):   # C# Rand: inclusive, swap-tolerant, 0 when max <= 0
    if mx < mn:
        mn, mx = mx, mn
    if mx <= 0:
        return 0
    return mn if mx <= mn else random.randint(mn, mx)


def _clamp(v, lo, hi):
    return lo if v < lo else hi if v > hi else v


# ── %-codec for TYPE text (|, %, newline are structural in plan.txt) ─────────────────────

def pct_dec(s):
    out = []
    i = 0
    while i < len(s):
        if s[i] == "%" and i + 2 < len(s) + 1 and i + 2 <= len(s) - 1:
            try:
                out.append(chr(int(s[i + 1:i + 3], 16)))
                i += 3
                continue
            except Exception:
                pass
        out.append(s[i])
        i += 1
    return "".join(out)


# ── parser ───────────────────────────────────────────────────────────────────────────────

_OPS = ("PLAN", "SCREEN", "SPEED", "RMOUSE", "CLICK", "TYPE",
        "DELAY", "LOOP", "LOOPTIME", "ENDLOOP", "WLIGHT", "STATELOOP",
        # v2 - Classroom Studio portable parity (sound, keys, flow, includes)
        "MOVETO", "KEY", "KDOWN", "KUP", "WHEEL", "RAW",
        "WSND", "WSNDP", "SOUNDWATCH", "WPROFILE", "TRGSND", "IFSND", "IFLUX", "ELSE", "ENDIF",
        "LABEL", "GOTO", "INCLUDE", "HANDPATH",
        # v3 - full Classroom Studio parity: packages, parallel groups, buzzer
        "RPKG", "PKGITEM", "ENDPKG", "PGROUP", "PARITEM", "ENDPAR", "RETRY", "ENDRETRY", "BEEP")

# ops that only a plan_api=2 ctx (code65+) can execute
_V2_OPS = frozenset(_OPS[11:])


def _pair(s, what, line_no):
    parts = s.split(",")
    try:
        a, b = int(parts[0]), int(parts[1])
    except Exception:
        raise ValueError("line %d: bad %s '%s'" % (line_no, what, s))
    return (a, b) if b >= a else (b, a)     # normalize like the app (swap-tolerant)


def _quad(s, what, line_no):
    parts = s.split(",")
    if len(parts) != 4:
        raise ValueError("line %d: bad %s '%s'" % (line_no, what, s))
    try:
        return tuple(int(p) for p in parts)
    except Exception:
        raise ValueError("line %d: bad %s '%s'" % (line_no, what, s))


def _link_blocks(ops):
    """v2 post-parse: match LOOP/ENDLOOP and IF/ELSE/ENDIF pairs (parse already enforced
    balance and kinds), record jump targets, and validate LABEL/GOTO visibility with the
    app's GotoSignal semantics: a label is visible only from its own block chain."""
    stack = []
    labels = {}
    for i, (op, prm) in enumerate(ops):
        if op in ("LOOP", "LOOPTIME", "IFSND", "IFLUX"):
            stack.append(i)
        elif op == "ENDLOOP":
            ops[stack.pop()][1]["end_ip"] = i
        elif op == "ELSE":
            ops[stack[-1]][1]["else_line"] = i
        elif op == "ENDIF":
            sp = ops[stack.pop()][1]
            sp["endif_ip"] = i + 1
            if "else_line" in sp:
                ops[sp["else_line"]][1]["endif_ip"] = i + 1
                sp["else_ip"] = sp["else_line"] + 1
            else:
                sp["else_ip"] = i + 1
        elif op == "LABEL":
            if prm["name"] in labels:
                raise ValueError("duplicate LABEL '%s'" % prm["name"])
            labels[prm["name"]] = i
    for op, prm in ops:
        if op != "GOTO":
            continue
        lip = labels.get(prm["name"])
        if lip is None:
            raise ValueError("GOTO '%s' has no matching LABEL" % prm["name"])
        gc = prm.get("_chain", ())
        lc = ops[lip][1].get("_chain", ())
        if lc != gc[:len(lc)]:
            raise ValueError("GOTO '%s': the label is not visible from this block" % prm["name"])


# -- v3 containers: randomPackage / parallelGroup -----------------------------

# Parallel branches are interpreted by the cooperative scheduler. LOOP/LOOPTIME
# and RPKG remain structural; HANDPATH, TYPE, RMOUSE and WSND yield between
# individual timed events. TRGSND deliberately stays out: the Arm-side click is
# an indivisible legacy transaction and cannot share the sound monitor.
_PAR_OK = ("RMOUSE", "MOVETO", "CLICK", "KEY", "KDOWN", "KUP", "WHEEL",
           "TYPE", "DELAY", "RAW", "BEEP", "HANDPATH", "WSND", "WSNDP", "WPROFILE",
           "LOOP", "LOOPTIME", "ENDLOOP", "RPKG")
_PKG_MODES = ("pick", "all", "seq")


def _extract_containers(text):
    """Lift v3 container bodies out of the flat plan text.

    RPKG/PGROUP have item separators; RETRY has one ordered attempt body.
    All three may contain nested containers and are parsed recursively.
    """
    lines = text.split("\n")
    out, table = [], []
    heads = ("RPKG", "PGROUP", "RETRY")
    i = 0
    while i < len(lines):
        head = lines[i].strip()
        kind = head.split("|", 1)[0].upper()
        if kind not in heads:
            if kind in ("PKGITEM", "ENDPKG", "PARITEM", "ENDPAR", "ENDRETRY"):
                raise ValueError("line %d: %s outside a package/parallel block" % (i + 1, kind))
            out.append(lines[i]); i += 1; continue
        if kind == "RETRY":
            end, items, depth, closed, j = "ENDRETRY", [[]], 0, False, i + 1
            while j < len(lines):
                k = lines[j].strip().split("|", 1)[0].upper()
                if k in heads:
                    depth += 1
                elif k in ("ENDRETRY", "ENDPKG", "ENDPAR"):
                    if depth == 0:
                        if k != end:
                            raise ValueError("line %d: %s closes a RETRY block" % (j + 1, k))
                        closed = True; break
                    depth -= 1
                items[0].append(lines[j]); j += 1
            if not closed:
                raise ValueError("line %d: RETRY is never closed with ENDRETRY" % (i + 1))
        else:
            sep = "PKGITEM" if kind == "RPKG" else "PARITEM"
            end = "ENDPKG" if kind == "RPKG" else "ENDPAR"
            items, depth, closed, j = [[]], 0, False, i + 1
            while j < len(lines):
                k = lines[j].strip().split("|", 1)[0].upper()
                if k in heads:
                    depth += 1
                elif k in ("ENDPKG", "ENDPAR", "ENDRETRY"):
                    if depth == 0:
                        if k != end:
                            raise ValueError("line %d: %s closes a %s block" % (j + 1, k, kind))
                        closed = True; break
                    depth -= 1
                elif k == sep and depth == 0:
                    items.append([]); j += 1; continue
                items[-1].append(lines[j]); j += 1
            if not closed:
                raise ValueError("line %d: %s is never closed with %s" % (i + 1, kind, end))
        table.append(["\n".join(b) for b in items])
        out.append(head + ("," if "|" in head else "|") + "#%d" % (len(table) - 1))
        i = j + 1
    return "\n".join(out), table

def _parse_v3(op, fields, prm, line_no, ctab):
    """Parse the v3 ops. Container bodies were replaced by a #index reference."""
    if op in ("PKGITEM", "ENDPKG", "PARITEM", "ENDPAR", "ENDRETRY"):
        raise ValueError("line %d: stray %s" % (line_no, op))
    body = fields[1] if len(fields) > 1 else ""
    if op == "BEEP":
        parts = body.split(",")
        if len(parts) != 2:
            raise ValueError("line %d: BEEP needs freq,ms" % line_no)
        try:
            prm["v"] = (int(parts[0]), int(parts[1]))
        except Exception:
            raise ValueError("line %d: bad BEEP '%s'" % (line_no, body))
        if not 30 <= prm["v"][0] <= 20000 or prm["v"][1] < 0:
            raise ValueError("line %d: BEEP out of range (30..20000 Hz)" % line_no)
        return
    if op == "HANDPATH":
        if not body:
            raise ValueError("line %d: HANDPATH needs samples" % line_no)
        count = 1
        start = 0
        size = len(body)
        while start < size:
            end = body.find(";", start)
            if end < 0:
                end = size
            token = body[start:end]
            values = token.split(",", 2)
            if len(values) != 3:
                raise ValueError("line %d: HANDPATH needs delay,dx,dy samples" % line_no)
            try:
                delay, dx, dy = int(values[0]), int(values[1]), int(values[2])
            except Exception:
                raise ValueError("line %d: bad HANDPATH sample" % line_no)
            if delay < 1 or delay > 60000 or abs(dx) > 8192 or abs(dy) > 8192:
                raise ValueError("line %d: HANDPATH sample out of range" % line_no)
            if end < size:
                count += 1
            if count > 2000:
                raise ValueError("line %d: HANDPATH has too many samples" % line_no)
            start = end + 1
        prm["path"] = body
        return
    parts = body.split(",")
    if not parts[-1].startswith("#"):
        raise ValueError("line %d: %s must open a block body" % (line_no, op))
    bodies = ctab[int(parts[-1][1:])]
    progs = [parse_plan("PLAN|2\n" + b) for b in bodies]
    if op == "RETRY":
        if len(parts) != 8:
            raise ValueError("line %d: RETRY needs max,timeout,lux,tolerance,stable,timeoutAction,exhaustedAction" % line_no)
        try:
            mx, timeout, center, tolerance, stable = (int(parts[i]) for i in range(5))
        except Exception:
            raise ValueError("line %d: bad RETRY numeric arguments" % line_no)
        if mx < 1 or timeout < 100 or center < 0 or tolerance < 1 or stable < 0:
            raise ValueError("line %d: RETRY arguments out of range" % line_no)
        if parts[5] not in ("esc", "none") or parts[6] != "alarmAndPauseForReview":
            raise ValueError("line %d: unsupported RETRY policy" % line_no)
        if len(progs) != 1:
            raise ValueError("line %d: RETRY must have one body" % line_no)
        prm.update(max_attempts=mx, timeout=timeout, center=center,
                   tolerance=tolerance, stable=stable, timeout_action=parts[5],
                   exhausted_action=parts[6], prog=progs[0])
        return
    if op == "RPKG":
        if len(parts) != 4:
            raise ValueError("line %d: RPKG needs mode,min,max" % line_no)
        mode = parts[0].strip().lower()
        if mode not in _PKG_MODES:
            raise ValueError("line %d: unknown RPKG mode '%s' (pick|all|seq)" % (line_no, parts[0]))
        try:
            mn, mx = int(parts[1]), int(parts[2])
        except Exception:
            raise ValueError("line %d: bad RPKG counts '%s'" % (line_no, body))
        if not progs:
            raise ValueError("line %d: RPKG has no items" % line_no)
        if mn < 0 or mx < mn or mx > len(progs):
            raise ValueError("line %d: RPKG counts out of range (%d items)" % (line_no, len(progs)))
        prm["mode"], prm["mn"], prm["mx"], prm["progs"] = mode, mn, mx, progs
        return
    if len(parts) != 1:
        raise ValueError("line %d: PGROUP takes no arguments" % line_no)
    if len(progs) < 2:
        raise ValueError("line %d: PGROUP needs at least two branches" % line_no)
    for b in progs:
        for o, _p in b:
            if o != "PLAN" and o not in _PAR_OK:
                raise ValueError("line %d: %s is not allowed inside PGROUP" % (line_no, o))
    prm["progs"] = progs


def parse_plan(text):
    """Returns [(op, params), ...]; params is a dict. Raises ValueError with line no."""
    text, _ctab = _extract_containers(text)          # v3: lift package/parallel bodies
    ops = []
    loop_stack = []      # v2: (op, line_no, op_index) for LOOP/LOOPTIME/IFSND/IFLUX blocks
    else_seen = set()    # v2: line numbers of IF blocks that already took an ELSE
    for line_no, raw in enumerate(text.split("\n"), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        fields = line.split("|")
        op = fields[0].upper()
        if op not in _OPS:
            raise ValueError("line %d: unknown op '%s'" % (line_no, fields[0]))
        prm = {}
        prm["_chain"] = tuple(b[2] for b in loop_stack)   # v2: enclosing blocks (GOTO visibility)
        if op in ("RPKG", "PGROUP", "RETRY", "BEEP", "HANDPATH", "PKGITEM", "ENDPKG", "PARITEM", "ENDPAR", "ENDRETRY"):
            _parse_v3(op, fields, prm, line_no, _ctab)    # v3
            ops.append((op, prm))
            continue
        if op in ("PLAN", "SCREEN", "SPEED", "DELAY", "LOOP", "LOOPTIME"):
            body = fields[1] if len(fields) > 1 else ""
            if op == "PLAN":
                try:
                    prm["v"] = int(body or "0")
                except Exception:
                    raise ValueError("line %d: bad PLAN version" % line_no)
                if prm["v"] not in (1, 2):
                    raise ValueError("line %d: unsupported PLAN version %d" % (line_no, prm["v"]))
            elif op == "SCREEN":
                parts = body.split(",")
                try:
                    # ordered pair: width,height are NOT a sortable range - _pair would
                    # swap 1920,1080 into 1080,1920 and clamp every x to the wrong axis
                    prm["v"] = (int(parts[0]), int(parts[1]))
                except Exception:
                    raise ValueError("line %d: bad SCREEN '%s'" % (line_no, body))
            elif op == "SPEED":
                prm["v"] = _pair(body, op, line_no)
            elif op == "DELAY":
                prm["v"] = _pair(body + ("," + body if "," not in body else ""), op, line_no)
            elif op == "LOOP":
                prm["n"] = int(body or "1")
                loop_stack.append((op, line_no, len(ops)))
            else:
                prm["sec"] = float(body or "0")
                loop_stack.append((op, line_no, len(ops)))
        elif op == "ENDLOOP":
            if not loop_stack or loop_stack[-1][0] in ("IFSND", "IFLUX"):
                raise ValueError("line %d: ENDLOOP without LOOP" % line_no)
            loop_stack.pop()
        elif op == "ENDIF":
            if not loop_stack or loop_stack[-1][0] not in ("IFSND", "IFLUX"):
                raise ValueError("line %d: ENDIF without IFSND/IFLUX" % line_no)
            loop_stack.pop()
        elif op == "ELSE":
            if not loop_stack or loop_stack[-1][0] not in ("IFSND", "IFLUX"):
                raise ValueError("line %d: ELSE outside an IF block" % line_no)
            if loop_stack[-1][1] in else_seen:
                raise ValueError("line %d: duplicate ELSE in one IF block" % line_no)
            else_seen.add(loop_stack[-1][1])
        elif op == "STATELOOP":
            # Syntax: STATELOOP|poll=250|stable=750|hysteresis=20|timeout=1500|
            #         routes=id:lo:hi:file.txt,id2:lo:hi:file.txt
            vals = {}
            for kv in fields[1:]:
                if "=" not in kv:
                    raise ValueError("line %d: STATELOOP wants key=value" % line_no)
                k, v = kv.split("=", 1)
                vals[k.strip().lower()] = v.strip()
            try:
                prm["poll"] = max(25, int(vals.get("poll", "250")))
                prm["stable"] = max(0, int(vals.get("stable", "750")))
                prm["hysteresis"] = max(0, int(vals.get("hysteresis", "0")))
                prm["timeout"] = max(prm["poll"], int(vals.get("timeout", "1500")))
            except Exception:
                raise ValueError("line %d: bad STATELOOP timing" % line_no)
            raw_routes = vals.get("routes", "")
            routes = []
            for raw_route in raw_routes.split(","):
                bits = raw_route.split(":")
                if len(bits) != 4 or not bits[0] or not bits[3].endswith(".txt"):
                    raise ValueError("line %d: bad STATELOOP route '%s'" % (line_no, raw_route))
                try:
                    lo, hi = int(bits[1]), int(bits[2])
                except Exception:
                    raise ValueError("line %d: bad STATELOOP lux range" % line_no)
                if hi < lo:
                    lo, hi = hi, lo
                routes.append({"id": bits[0], "lo": lo, "hi": hi, "file": bits[3]})
            if not routes:
                raise ValueError("line %d: STATELOOP needs routes=" % line_no)
            prm["routes"] = routes
            prm["fallback"] = vals.get("fallback", "STOP").upper()
            if prm["fallback"] not in ("STOP", "FIRST"):
                raise ValueError("line %d: STATELOOP fallback must be STOP or FIRST" % line_no)
        elif op == "WSNDP":
            pa = fields[1].split(",") if len(fields) > 1 else []
            if len(pa) not in (5, 8, 10):
                raise ValueError("line %d: WSNDP needs 5, 8, or 10 fields" % line_no)
            try:
                profile_id = int(pa[0])
                threshold, minimum, timeout = int(pa[2]), int(pa[3]), int(pa[4])
                peak_min = int(pa[5]) if len(pa) >= 8 else threshold
                peak_max = int(pa[6]) if len(pa) >= 8 else 65535
                priority = int(pa[7]) if len(pa) >= 8 else 0
                cooldown = int(pa[9]) if len(pa) == 10 else 0
            except Exception:
                raise ValueError("line %d: bad WSNDP numbers" % line_no)
            binding = pa[1].strip()
            response = pa[8].strip() if len(pa) == 10 else ""
            if (profile_id not in (1, 2) or not binding or len(binding) > 32
                    or peak_min < 0 or peak_max < max(1, peak_min)
                    or priority < -100 or priority > 100 or cooldown < 0
                    or (response and (not response.endswith(".txt") or "/" in response or "\\" in response))):
                raise ValueError("line %d: bad WSNDP profile" % line_no)
            prm.update({"profile_id": profile_id, "binding": binding,
                        "threshold": threshold, "minimum": minimum, "timeout": timeout,
                        "peak_min": peak_min, "peak_max": peak_max, "priority": priority,
                        "response": response, "cooldown": cooldown})
        elif op == "SOUNDWATCH":
            raw_profiles = fields[1].split(";") if len(fields) > 1 else []
            profiles = []
            seen = set()
            for raw_profile in raw_profiles:
                pa = raw_profile.split(",")
                if len(pa) != 8:
                    raise ValueError("line %d: SOUNDWATCH profile needs id,min,max,duration,priority,cooldown,file,mode" % line_no)
                profile_id, route_file, mode = pa[0].strip(), pa[6].strip(), pa[7].strip()
                try:
                    peak_min, peak_max, minimum, priority, cooldown = (
                        int(pa[1]), int(pa[2]), int(pa[3]), int(pa[4]), int(pa[5]))
                except Exception:
                    raise ValueError("line %d: bad SOUNDWATCH numbers" % line_no)
                if (profile_id not in ("splash", "whisper") or profile_id in seen
                        or mode not in ("global", "scoped")
                        or peak_min < 0 or peak_max < max(1, peak_min) or peak_max > 65535
                        or minimum < 10 or minimum > 10000
                        or priority < -1000 or priority > 1000
                        or cooldown < 0 or cooldown > 600000
                        or not route_file.endswith("_steps.txt")
                        or any(c in route_file for c in "/\\:|%")):
                    raise ValueError("line %d: bad SOUNDWATCH profile" % line_no)
                seen.add(profile_id)
                profiles.append({"id": profile_id, "peak_min": peak_min,
                                 "peak_max": peak_max, "minimum": minimum,
                                 "priority": priority, "cooldown": cooldown,
                                 "file": route_file, "mode": mode})
            if not profiles:
                raise ValueError("line %d: SOUNDWATCH needs profiles" % line_no)
            prm["profiles"] = profiles
        elif op == "WPROFILE":
            pa = fields[1].split(",") if len(fields) > 1 else []
            if len(pa) != 3 or pa[0] != "splash":
                raise ValueError("line %d: WPROFILE needs splash,min,max" % line_no)
            try:
                timeout_min, timeout_max = int(pa[1]), int(pa[2])
            except Exception:
                raise ValueError("line %d: bad WPROFILE timeout" % line_no)
            if timeout_min < 1000 or timeout_max < timeout_min or timeout_max > 300000:
                raise ValueError("line %d: bad WPROFILE timeout range" % line_no)
            prm.update({"profile_id": "splash", "timeout_min": timeout_min,
                        "timeout_max": timeout_max})
        elif op in ("WSND", "TRGSND", "IFSND", "IFLUX"):
            pa = fields[1].split(",") if len(fields) > 1 else []
            need = {"WSND": 3, "IFSND": 3, "IFLUX": 5, "TRGSND": 8}[op]
            if len(pa) < need:
                raise ValueError("line %d: %s needs %d comma numbers" % (line_no, op, need))
            try:
                prm["a"] = [int(x) for x in pa[:need]]
            except Exception:
                raise ValueError("line %d: bad %s numbers" % (line_no, op))
            if op in ("IFSND", "IFLUX"):
                loop_stack.append((op, line_no, len(ops)))
        elif op in ("KDOWN", "KUP", "WHEEL"):
            try:
                prm["v"] = int(fields[1]) if len(fields) > 1 else 0
            except Exception:
                raise ValueError("line %d: %s needs a number" % (line_no, op))
            if op != "WHEEL" and not 0 < prm["v"] < 256:
                raise ValueError("line %d: %s vk out of range" % (line_no, op))
        elif op in ("LABEL", "GOTO"):
            nm = fields[1].strip() if len(fields) > 1 else ""
            if not nm or "=" in nm:
                raise ValueError("line %d: %s needs a plain name" % (line_no, op))
            prm["name"] = nm
        elif op == "RAW":
            prm["line"] = "|".join(fields[1:]).strip()
            if not prm["line"]:
                raise ValueError("line %d: RAW needs a board line" % line_no)
        elif op == "WLIGHT":
            pos = fields[1].split(",") if len(fields) > 1 else []
            if len(pos) < 5:
                raise ValueError("line %d: WLIGHT needs lo,hi,stable,to,mode" % line_no)
            try:
                prm["lo"], prm["hi"] = int(pos[0]), int(pos[1])
                prm["stable"], prm["to"], prm["mode"] = int(pos[2]), int(pos[3]), int(pos[4])
            except Exception:
                raise ValueError("line %d: bad WLIGHT numbers" % line_no)
            for kv in fields[2:]:
                if "=" not in kv:
                    raise ValueError("line %d: bad arg '%s'" % (line_no, kv))
                k, v = kv.split("=", 1)
                if k == "key":
                    t = v.split(",")
                    prm["key"] = (int(t[0]), int(t[1]) if len(t) > 1 else 30,
                                  int(t[2]) if len(t) > 2 else 90)
                elif k == "react":
                    prm["react"] = _pair(v, k, line_no)
                else:
                    raise ValueError("line %d: unknown WLIGHT key '%s'" % (line_no, k))
        else:   # RMOUSE / CLICK / TYPE: key=value args
            for kv in fields[1:]:
                if "=" not in kv:
                    raise ValueError("line %d: bad arg '%s' (want key=value)" % (line_no, kv))
                k, v = kv.split("=", 1)
                prm[k] = v
            if op == "RMOUSE":
                if "region" not in prm:
                    raise ValueError("line %d: RMOUSE needs region=x,y,w,h" % line_no)
                prm["region"] = _quad(prm["region"], "region", line_no)
                for k in ("mt", "curve", "before", "after", "speed"):
                    if k in prm:
                        prm[k] = _pair(prm[k], k, line_no)
                if "mid" in prm:                       # chance:mn,mx
                    c, _, r = prm["mid"].partition(":")
                    prm["mid"] = (int(c), _pair(r, "mid", line_no))
                if "idle" in prm:                      # everyMn,everyMx:pauseMn,pauseMx
                    e, _, r = prm["idle"].partition(":")
                    prm["idle"] = (_pair(e, "idle-every", line_no), _pair(r, "idle-pause", line_no))
                if "over" in prm:
                    prm["over"] = int(prm["over"])
            elif op == "CLICK":
                prm["btn"] = prm.get("btn", "left")
                prm["n"] = int(prm.get("n", "1"))
                if "hold" in prm:
                    prm["hold"] = _pair(prm["hold"], "hold", line_no)
            elif op == "TYPE":
                if "text" not in prm:
                    raise ValueError("line %d: TYPE needs text=" % line_no)
                prm["text"] = pct_dec(prm["text"])
                for k in ("h", "w", "p", "typo", "typos", "typochars"):
                    if k in prm:
                        prm[k] = _pair(prm[k], k, line_no)
                if "think" in prm:
                    c, _, r = prm["think"].partition(":")
                    prm["think"] = (int(c), _pair(r, "think", line_no))
                if "wp" in prm:
                    prm["wp"] = int(prm["wp"])
            elif op == "MOVETO":
                if "x" not in prm or "y" not in prm:
                    raise ValueError("line %d: MOVETO needs x= and y=" % line_no)
                try:
                    prm["x"] = int(prm["x"])
                    prm["y"] = int(prm["y"])
                    prm["human"] = int(prm.get("human", "1"))
                except Exception:
                    raise ValueError("line %d: bad MOVETO numbers" % line_no)
                for k in ("mt", "curve", "before", "after"):
                    if k in prm:
                        prm[k] = _pair(prm[k], k, line_no)
                if "mid" in prm:
                    c, _, r = prm["mid"].partition(":")
                    prm["mid"] = (int(c), _pair(r, "mid", line_no))
                if "idle" in prm:
                    e, _, r = prm["idle"].partition(":")
                    prm["idle"] = (_pair(e, "idle-every", line_no), _pair(r, "idle-pause", line_no))
                if "over" in prm:
                    prm["over"] = int(prm["over"])
            elif op == "KEY":
                if "combo" not in prm:
                    raise ValueError("line %d: KEY needs combo=vk+vk" % line_no)
                try:
                    prm["combo"] = [int(v) for v in prm["combo"].split("+") if v != ""]
                except Exception:
                    raise ValueError("line %d: bad KEY combo" % line_no)
                if not prm["combo"] or any(v <= 0 or v > 255 for v in prm["combo"]):
                    raise ValueError("line %d: bad KEY combo" % line_no)
                if "hold" in prm:
                    prm["hold"] = _pair(prm["hold"], "hold", line_no)
            elif op == "INCLUDE":
                nm = prm.get("file", "")
                if not nm or any(ch in nm for ch in "/\\:") or not nm.lower().endswith(".txt"):
                    raise ValueError("line %d: INCLUDE needs a plain *.txt file name" % line_no)
        ops.append((op, prm))
    if loop_stack:
        _k, _ln, _ = loop_stack[-1]
        raise ValueError("line %d: %s without %s" % (_ln, _k, "ENDIF" if _k in ("IFSND", "IFLUX") else "ENDLOOP"))
    if not ops or ops[0][0] != "PLAN":
        raise ValueError("plan must start with PLAN|1 or PLAN|2")
    _link_blocks(ops)
    return ops
