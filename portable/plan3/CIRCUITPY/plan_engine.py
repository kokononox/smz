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
#   TYPE|h=mn,mx|w=mn,mx|wp=pct|p=mn,mx|think=chance:mn,mx|typo=mn,mx|text=<encoded>
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


# code64: optional persistent cursor contract. The Pico context owns the saved
# coordinate so Start/Stop does not reset every new run_plan() call to screen centre.
def _load_mouse_pos(ctx, ops):
    sw, sh = ctx.screen_w, ctx.screen_h
    for op, prm in ops:
        if op == "SCREEN":
            sw, sh = prm["v"]
            break
    saved = None
    getter = getattr(ctx, "get_mouse_pos", None)
    if getter is not None:
        try:
            saved = getter()
        except Exception:
            saved = None
    if saved is None or len(saved) < 2:
        return [sw // 2, sh // 2]
    return [_clamp(int(saved[0]), 0, max(0, sw - 1)),
            _clamp(int(saved[1]), 0, max(0, sh - 1))]


def _save_mouse_pos(ctx, pos):
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
        "WSND", "TRGSND", "IFSND", "IFLUX", "ELSE", "ENDIF",
        "LABEL", "GOTO", "INCLUDE",
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

# Inside a parallel group only short, immediate commands keep the "parallel" meaning:
# a long board-side hold would own the serial channel and serialize the whole group.
_PAR_OK = ("MOVETO", "CLICK", "KEY", "KDOWN", "KUP", "WHEEL", "TYPE", "DELAY", "RAW", "BEEP")
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
        if op in ("RPKG", "PGROUP", "RETRY", "BEEP", "PKGITEM", "ENDPKG", "PARITEM", "ENDPAR", "ENDRETRY"):
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
                for k in ("mt", "curve", "before", "after"):
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
                for k in ("h", "w", "p", "typo"):
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


# ── HumanMouse port (C# v0.9.24 → Python; banker-rounding matches Math.Round) ───────────

def tuned_wind(dist, curve):
    cv = 0.30 if curve < 0 else _clamp(curve, 0.0, 2.0)
    base = 0.3 + cv * 3.0 if cv < 1.0 else 3.3 + (cv - 1.0) * 4.7
    return base * _clamp(dist / 400.0, 0.35, 1.0)


def build_range_profile(mn, mx, knot_count, low_ends=False):
    if mx < mn:
        mn, mx = mx, mn
    knot_count = int(_clamp(knot_count, 2, 12))
    if abs(mx - mn) < 1e-9:
        return [float(mn)] * knot_count
    span = mx - mn
    knots = []
    upper = _below(2) == 1
    for i in range(knot_count):
        force_low = low_ends and (i == 0 or i == knot_count - 1)
        band = _rf() * 0.18 if force_low else (0.60 + _rf() * 0.40 if upper else _rf() * 0.40)
        knots.append(mn + span * band)
        upper = not upper
    return knots


def sample_profile(knots, t):
    if not knots:
        return 0.0
    if len(knots) == 1:
        return knots[0]
    p = _clamp(t, 0.0, 1.0) * (len(knots) - 1)
    i = min(len(knots) - 2, int(math.floor(p)))
    u = p - i
    smooth = u * u * (3.0 - 2.0 * u)          # C1 smoothstep
    return knots[i] + (knots[i + 1] - knots[i]) * smooth


def _poly_len(pts):
    total = 0.0
    for i in range(1, len(pts)):
        total += math.sqrt((pts[i][0] - pts[i - 1][0]) ** 2 + (pts[i][1] - pts[i - 1][1]) ** 2)
    return total


def windmouse(sx, sy, tx, ty, wind, gravity, curve, profile):
    sqrt3, sqrt5 = math.sqrt(3.0), math.sqrt(5.0)
    x, y = float(sx), float(sy)
    vx = vy = wx = wy = 0.0
    dist0 = max(1.0, math.sqrt((tx - sx) ** 2 + (ty - sy) ** 2))
    if dist0 < 3:
        return [[tx, ty, 0]]
    max_step = _clamp(dist0 / 15.0, 6.0, 30.0)
    stop_radius = min(8.0, max(2.0, dist0 * 0.02))
    pts = []
    guard = 0
    while guard < 2000:
        guard += 1
        dist = math.sqrt((tx - x) ** 2 + (ty - y) ** 2)
        if dist < stop_radius:
            break
        live_wind = wind
        if profile:
            progress = _clamp(1.0 - dist / dist0, 0.0, 1.0)
            live_wind = tuned_wind(dist0, sample_profile(profile, progress) / 100.0)
        wmag = min(live_wind, dist)
        if dist >= stop_radius * 4:             # far: wind roams; near: wind calms
            wx = wx / sqrt3 + (2 * _rf() - 1) * wmag / sqrt5
            wy = wy / sqrt3 + (2 * _rf() - 1) * wmag / sqrt5
        else:
            wx /= sqrt3
            wy /= sqrt3
            max_step = max(2.5, max_step / sqrt5)   # decelerate on approach
        vx += wx + gravity * (tx - x) / dist
        vy += wy + gravity * (ty - y) / dist
        vmag = math.sqrt(vx * vx + vy * vy)
        if vmag > max_step:
            vx = vx / vmag * max_step
            vy = vy / vmag * max_step
        x += vx
        y += vy
        pts.append([int(round(x)), int(round(y)), 0])
    pts.append([tx, ty, 0])                     # land exactly on the target
    return pts


def resample_variable(spine, mn_sp, mx_sp):
    if not spine:
        return []
    if len(spine) == 1:
        return [list(spine[0])]
    if mx_sp < mn_sp:
        mn_sp, mx_sp = mx_sp, mn_sp
    mn_sp = max(0.5, mn_sp)
    mx_sp = max(mn_sp, mx_sp)
    total = _poly_len(spine)
    if total < 1e-6:
        return [list(spine[-1])]
    profile = build_range_profile(mn_sp, mx_sp, 5)
    outp = []
    next_at = sample_profile(profile, 0.0)
    acc = 0.0
    for i in range(1, len(spine)):
        x0, y0 = spine[i - 1][0], spine[i - 1][1]
        seg = math.sqrt((spine[i][0] - x0) ** 2 + (spine[i][1] - y0) ** 2)
        if seg < 1e-6:
            continue
        while acc + seg >= next_at:
            u = (next_at - acc) / seg
            outp.append([int(round(x0 + (spine[i][0] - x0) * u)),
                         int(round(y0 + (spine[i][1] - y0) * u)), 0])
            phase = _clamp(next_at / total, 0.0, 1.0)
            next_at += max(0.5, sample_profile(profile, phase))
        acc += seg
    last = spine[-1]
    if not outp or outp[-1][0] != last[0] or outp[-1][1] != last[1]:
        outp.append([last[0], last[1], 0])
    return outp


def assign_dynamic_delays(pts, sx, sy, smin, smax, target_ms=0):
    """v0.9.8/10 port: per-point delay from a smooth speed profile (low at both ends,
    alternating interior knots), normalized to target_ms when the step set a duration."""
    for p in pts:
        p[2] = 0
    if not pts or (smax <= 0 and target_ms <= 0):
        return
    shape_only = smax <= 0
    lo = 150 if shape_only else max(150, min(smin, smax))
    hi = 2000 if shape_only else max(lo, max(smin, smax))
    knot_count = int(_clamp(4 + len(pts) // 120, 4, 9))
    prof = build_range_profile(float(lo), float(hi), knot_count, low_ends=True)
    segs = []
    path = 0.0
    px, py = sx, sy
    for p in pts:
        s = math.sqrt((p[0] - px) ** 2 + (p[1] - py) ** 2)
        segs.append(s)
        path += s
        px, py = p[0], p[1]
    if path < 1e-6:
        return
    weights = []
    wsum = 0.0
    travelled = 0.0
    for i in range(len(pts)):
        phase = _clamp((travelled + segs[i] * 0.5) / path, 0.0, 1.0)
        speed = _clamp(sample_profile(prof, phase), lo, hi)
        w = segs[i] * 1000.0 / max(1.0, speed)
        weights.append(w)
        wsum += w
        travelled += segs[i]
    if wsum <= 0:
        return
    scale = target_ms / wsum if target_ms > 0 else 1.0
    for i in range(len(pts)):
        pts[i][2] = max(1, int(round(weights[i] * scale)))


def curve_height_ratio(pct):
    p = _clamp(float(pct), 0.0, 200.0)
    if p <= 100.0:
        return 0.08 * ((p / 100.0) ** 1.15)
    return 0.08 + 0.42 * math.sqrt((p - 100.0) / 100.0)


def _ray_to_edge(x, y, dx, dy, w, h, margin):
    max_x = max(margin, w - 1.0 - margin)
    max_y = max(margin, h - 1.0 - margin)
    limit = float("inf")
    if abs(dx) > 1e-9:
        limit = min(limit, (max_x - x) / dx if dx > 0 else (margin - x) / dx)
    if abs(dy) > 1e-9:
        limit = min(limit, (max_y - y) / dy if dy > 0 else (margin - y) / dy)
    return max(0.0, limit) if limit != float("inf") else 0.0


def build_arc(sx, sy, tx, ty, total_ms, cmin, cmax, w, h):
    """v0.9.7/8 port: one continuous half-ellipse; curvature glides through the range;
    the side with screen room is chosen and pre-scaled so no flat clamped sections."""
    dx, dy = tx - sx, ty - sy
    dist = math.sqrt(dx * dx + dy * dy)
    if dist < 3:
        return [[tx, ty, max(0, total_ms)]], 0.0, total_ms
    cmin = _clamp(cmin, 0, 200)
    cmax = _clamp(cmax, 0, 200)
    if cmax < cmin:
        cmin, cmax = cmax, cmin
    ux, uy = dx / dist, dy / dist
    nx, ny = -uy, ux
    skew = _rf() * 0.14 - 0.07
    wobble = 0.025 + _rf() * 0.045
    phase0 = _rf() * math.pi * 2.0
    samples = int(_clamp(math.ceil(dist / 12.0), 32, 240))
    knot_count = int(_clamp(4 + int(dist / 320.0), 4, 8))
    knots = build_range_profile(cmin, cmax, knot_count)

    def fit_for_side(side):
        fit = 1.0
        for i in range(1, samples):
            t = i / samples
            q = _clamp(t + skew * math.sin(math.pi * t), 0.0, 1.0)
            along = 0.5 - 0.5 * math.cos(math.pi * q)
            shape = math.sin(math.pi * q) * (1.0 + wobble * math.sin(2.0 * math.pi * t + phase0))
            bx = sx + ux * dist * along
            by = sy + uy * dist * along
            required = dist * curve_height_ratio(sample_profile(knots, t)) * max(0.0, shape)
            if required < 1e-6:
                continue
            room = _ray_to_edge(bx, by, nx * side, ny * side, w, h, 2.0)
            fit = min(fit, room / required)
        return _clamp(fit * 0.94, 0.0, 1.0)

    fit_pos, fit_neg = fit_for_side(+1), fit_for_side(-1)
    if fit_pos >= 0.90 and fit_neg >= 0.90:
        side = -1 if _below(2) == 0 else +1
        fit = fit_pos if side > 0 else fit_neg
    elif fit_pos >= fit_neg:
        side, fit = +1, fit_pos
    else:
        side, fit = -1, fit_neg

    spine = [[sx, sy, 0]]
    actual_height = 0.0
    for i in range(1, samples):
        t = i / samples
        q = _clamp(t + skew * math.sin(math.pi * t), 0.0, 1.0)
        along = 0.5 - 0.5 * math.cos(math.pi * q)
        shape = math.sin(math.pi * q) * (1.0 + wobble * math.sin(2.0 * math.pi * t + phase0))
        local_h = dist * curve_height_ratio(sample_profile(knots, t)) * fit
        normal = side * local_h * max(0.0, shape)
        actual_height = max(actual_height, abs(normal))
        spine.append([int(round(sx + ux * dist * along + nx * normal)),
                      int(round(sy + uy * dist * along + ny * normal)), 0])
    spine.append([tx, ty, 0])
    path_len = _poly_len(spine)
    arc_ms = 0 if total_ms <= 0 else int(_clamp(round(total_ms * max(1.0, path_len / dist)), 60, 30000))
    # memory guard: Pico RAM - only >2500px moves (>4K diagonals) get wider spacing;
    # every screen up to 1440p keeps the hand-matched 2.0-3.2 px gliding steps.
    scale = max(1.0, dist / 2500.0)
    return resample_variable(spine, 2.0 * scale, 3.2 * scale), actual_height, arc_ms


class PausePlanner:
    """Run-scoped pause manager: long distraction breaks once per fresh random cadence."""

    def __init__(self):
        self.moves_since = 0
        self.next_idle_at = -1

    def mid_pause(self, c):
        if c["mid_chance"] > 0 and _below(100) < c["mid_chance"]:
            return rand_range(c["mid_min"], c["mid_max"])
        return 0

    def roll_long(self, c):
        if c["idle_pause_max"] <= 0 or c["idle_every_max"] <= 0:
            return 0
        self.moves_since += 1
        if self.next_idle_at < 0:
            self.next_idle_at = max(1, rand_range(c["idle_every_min"], c["idle_every_max"]))
        if self.moves_since < self.next_idle_at:
            return 0
        self.moves_since = 0
        self.next_idle_at = max(1, rand_range(c["idle_every_min"], c["idle_every_max"]))
        return rand_range(c["idle_pause_min"], c["idle_pause_max"])


_DEFAULT_CFG = dict(before_min=120, before_max=450, after_min=150, after_max=600,
                    mid_chance=12, mid_min=100, mid_max=400,
                    idle_every_min=5, idle_every_max=12, idle_pause_min=1000, idle_pause_max=5000,
                    over_chance=15, curve_min=15, curve_max=45,
                    speed_min=0, speed_max=2000, mt_min=0, mt_max=0)


def plan_move(sx, sy, tx, ty, c, pauses, w, h):
    """Full PlanMove port. Returns dict(before, after, long, pts=[[x,y,delayMs]...])."""
    tx = int(_clamp(tx, 0, max(0, w - 1)))
    ty = int(_clamp(ty, 0, max(0, h - 1)))
    sx = int(_clamp(sx, 0, max(0, w - 1)))
    sy = int(_clamp(sy, 0, max(0, h - 1)))
    dist = math.sqrt((tx - sx) ** 2 + (ty - sy) ** 2)
    speed_lo = max(150, c["speed_min"])
    speed_hi = max(speed_lo, c["speed_max"])
    total_ms = 0 if c["speed_max"] <= 0 else int(_clamp(
        dist * 1000.0 / max(1.0, (speed_lo + speed_hi) / 2.0), 60, 30000))
    curve_min = int(_clamp(c["curve_min"], 0, 200))
    curve_max = int(_clamp(c["curve_max"], 0, 200))
    if curve_max < curve_min:
        curve_min, curve_max = curve_max, curve_min
    sampled = curve_min if curve_max == curve_min else random.randint(curve_min, curve_max)
    curve = _clamp(sampled / 100.0, 0.0, 2.0)
    mt_min, mt_max = max(0, c["mt_min"]), max(0, c["mt_max"])
    if mt_max < mt_min:
        mt_min, mt_max = mt_max, mt_min
    target_ms = rand_range(max(1, mt_min), max(1, mt_max)) if mt_max > 0 else 0

    dense = []
    overshoot_idx = -1
    if curve_max > 100 and dist >= 80:                          # true arc mode
        dense, _height, _arc_ms = build_arc(sx, sy, tx, ty, total_ms, curve_min, curve_max, w, h)
    elif dist >= 60 and c["over_chance"] > 0 and _below(100) < c["over_chance"]:
        ux, uy = (tx - sx) / dist, (ty - sy) / dist
        cscale = min(1.0, curve)
        over = int(_clamp(dist * (0.03 + _rf() * 0.05) * cscale, 2, 20))
        perp = int(_rf() * (2.0 + cscale * 5.0) - (1.0 + cscale * 2.5))
        ox = int(_clamp(tx + int(ux * over - uy * perp), 0, max(0, w - 1)))
        oy = int(_clamp(ty + int(uy * over + ux * perp), 0, max(0, h - 1)))
        leg1_ms = max(40, int(total_ms * 0.8))
        leg2_ms = max(30, total_ms - leg1_ms)
        dense += _build_leg(sx, sy, ox, oy, leg1_ms, curve, curve_min, curve_max)
        overshoot_idx = len(dense) - 1
        dense += _build_leg(ox, oy, tx, ty, leg2_ms, curve, curve_min, curve_max)
    else:
        dense = _build_leg(sx, sy, tx, ty, total_ms, curve, curve_min, curve_max)

    for p in dense:                                             # keep on-screen
        p[0] = int(_clamp(p[0], 0, max(0, w - 1)))
        p[1] = int(_clamp(p[1], 0, max(0, h - 1)))

    assign_dynamic_delays(dense, sx, sy, c["speed_min"], c["speed_max"], target_ms)

    if overshoot_idx >= 0:                                      # re-aim pause
        dense[overshoot_idx][2] += rand_range(60, 180)
    mid = pauses.mid_pause(c)                                   # one hesitation per move
    if mid > 0 and len(dense) >= 8:
        dense[2 + _below(len(dense) - 4)][2] += mid

    return {"before": rand_range(c["before_min"], c["before_max"]),
            "after": rand_range(c["after_min"], c["after_max"]),
            "long": pauses.roll_long(c),
            "target": (tx, ty),
            "pts": dense}


def _build_leg(sx, sy, tx, ty, total_ms, curve, cmin, cmax):
    dist0 = max(1.0, math.sqrt((tx - sx) ** 2 + (ty - sy) ** 2))
    profile = None
    if cmin >= 0 and cmax >= 0:
        profile = build_range_profile(cmin, cmax, int(_clamp(4 + int(dist0 / 320.0), 4, 8)))
    spine = windmouse(sx, sy, tx, ty, tuned_wind(dist0, curve), 14.0, curve, profile)
    with_start = [[sx, sy, 0]] + spine
    scale = max(1.0, dist0 / 2500.0)                           # Pico RAM guard (>4K only)
    return resample_variable(with_start, 2.0 * scale, 3.2 * scale)


# ── typing port (C# TypeTextCommands v0.9.15 → Python) ──────────────────────────────────

_QWERTY_ROWS = ("1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm")
_PUNCT = ".,!?;:"


def _qwerty_neighbor(ch):
    lower = ch.lower()
    for row in _QWERTY_ROWS:
        i = row.find(lower)
        if i < 0:
            continue
        j = i + (-1 if _below(2) == 0 else 1)
        if j < 0 or j >= len(row):
            j = 1 if i == 0 else i - 1
        n = row[j]
        return n.upper() if ch.isupper() else n
    return None


def _split_punct(s):
    outp = []
    start = 0
    for i in range(len(s) - 1):
        if s[i] in _PUNCT:
            outp.append(s[start:i + 1])
            start = i + 1
    if start < len(s):
        outp.append(s[start:])
    if not outp and s:
        outp.append(s)
    return outp


def plan_typing(text, p):
    """Returns [("KTEXT",hmin,hmax,chunk) | ("DLY",ms) | ("KCOMBO",vk), ...].
    p keys: h,w (ms ranges), wp (word pause chance %), p (punct pause range),
    think ((chance,(mn,mx))), typo ((mn,mx) cadence, 0/0=off)."""
    hmin, hmax = p.get("h", (80, 220))
    wmin, wmax = p.get("w", (0, 0))
    wp = _clamp(p.get("wp", 100), 0, 100)
    pmin, pmax = p.get("p", (0, 0))
    think_chance, think_range = p.get("think", (0, (800, 2200)))
    think_chance = _clamp(think_chance, 0, 100)
    think_min, think_max = think_range
    typo_min, typo_max = p.get("typo", (0, 0))
    typo_cadence = typo_max > 0
    next_typo_at = max(1, rand_range(typo_min, typo_max)) if typo_cadence else -1
    words_since_typo = 0
    word_mode = wmax > 0 or pmax > 0 or think_chance > 0 or typo_cadence

    cmds = []
    lines = text.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    pending = []

    def flush():
        s = "".join(pending)
        pending.clear()
        for i in range(0, len(s), 60):
            cmds.append(("KTEXT", hmin, hmax, s[i:i + 60]))

    for li, line in enumerate(lines):
        if word_mode:
            words = [wd for wd in line.split() if wd]
            for wi, word in enumerate(words):
                tail = " " if wi < len(words) - 1 else ""
                typed = word + tail
                typo_due = typo_cadence and (words_since_typo := words_since_typo + 1) >= next_typo_at
                if typo_due and 2 <= len(word) <= 60:
                    pos = 1 + _below(len(word) - 1)      # never the first char
                    wrong = _qwerty_neighbor(word[pos])
                    if wrong is not None:
                        flush()
                        cmds.append(("KTEXT", hmin, hmax, word[:pos] + wrong))
                        cmds.append(("DLY", rand_range(max(hmax, 120), hmax * 2 + 200)))
                        cmds.append(("KCOMBO", 8))       # Backspace
                        cmds.append(("DLY", rand_range(hmin, hmax)))
                        typed = word[pos:] + tail
                        words_since_typo = 0
                        next_typo_at = max(1, rand_range(typo_min, typo_max))
                segs = _split_punct(typed) if pmax > 0 else [typed]
                for si, seg in enumerate(segs):
                    pending.append(seg)
                    if si < len(segs) - 1:
                        flush()
                        cmds.append(("DLY", rand_range(pmin, pmax)))
                if wi < len(words) - 1:
                    if wmax > 0 and _below(100) < wp:
                        flush()
                        cmds.append(("DLY", rand_range(wmin, wmax)))
                    if think_chance > 0 and think_max > 0 and _below(100) < think_chance:
                        flush()
                        cmds.append(("DLY", rand_range(think_min, think_max)))
            flush()
        else:
            for i in range(0, len(line), 60):
                cmds.append(("KTEXT", hmin, hmax, line[i:i + 60]))
        if li < len(lines) - 1:
            cmds.append(("KCOMBO", 13))                  # Enter between lines
    return cmds


# live-light-guard-v1

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
            # parallelGroup: cooperative round-robin, one command per branch per turn
            _rr = [[o for o in p if o[0] != "PLAN"] for p in prm["progs"]]
            while True:
                _live = False
                for _b in _rr:
                    if _b:
                        _live = True
                        run_plan([_b.pop(0)], ctx, _pos=pos, _pauses=pauses, _inc=inc)
                if not _live:
                    break
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
    c = dict(_DEFAULT_CFG)
    c["speed_min"], c["speed_max"] = ctx.speed_min, ctx.speed_max
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
    if target is None:
        tx = random.randint(rx, rx + max(0, rw - 1))
        ty = random.randint(ry, ry + max(0, rh - 1))
    else:
        tx, ty = target                      # v2 MOVETO: the app's fixed human move target
    if prm.get("human", 1) == 0:
        # v2 MOVETO human=0 - the app's human=off instant firmware move (single jump)
        ctx.mmove(tx, ty)
        pos[0], pos[1] = tx, ty
        _save_mouse_pos(ctx, pos)
        return
    plan = plan_move(pos[0], pos[1], tx, ty, c, pauses, ctx.screen_w, ctx.screen_h)
    ctx.log("rmouse -> (%d,%d) %d pts" % (tx, ty, len(plan["pts"])))
    if not ctx.sleep_ms(plan["before"]):
        raise PlanAbort()
    for pt in plan["pts"]:
        ctx.mmove(pt[0], pt[1])
        # Persist immediately, before the abortable delay: Stop in the middle of
        # a path resumes from the last point that was actually sent to the arm.
        pos[0], pos[1] = pt[0], pt[1]
        _save_mouse_pos(ctx, pos)
        if not ctx.sleep_ms(pt[2]):
            raise PlanAbort()
    pos[0], pos[1] = plan["target"]
    _save_mouse_pos(ctx, pos)
    if not ctx.sleep_ms(plan["after"]):
        raise PlanAbort()
    if plan["long"] > 0:
        ctx.log("idle break %d ms" % plan["long"])
        if not ctx.sleep_ms(plan["long"]):
            raise PlanAbort()
