#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""plan_export_twin.py - Python TWIN of ams-shell/src/Ams.UI/Services/PlanExporter.cs
(v0.9.65, phase 2 of the portable line).

Same input (an .amsj-shaped tree + AppSettings-shaped dict + target screen), same
output (a portable plan.txt), same blocking-error contract. The C# exporter is ported
from THIS file; sim/sim_plan_export.py feeds the twin's output to the REAL gen-1
engine (firmware/code64b/plan_engine.py) so the contract is proven before CI.

Target contract (decided 2026-09-10, project page): the gen-1 plan engine on the
user's drive (firmware pico-light 0.9.64b) accepts ONLY the header PLAN|1 and these
11 ops: PLAN, SCREEN, SPEED, RMOUSE, CLICK, TYPE, DELAY, LOOP, LOOPTIME, ENDLOOP,
WLIGHT. The gen-3 engine (PLAN|2, written by portable/plan3/tools/plan_gen.py)
belongs to the parked 0.9.66 line - every step that needs it is a BLOCKING ERROR
here, never silently skipped (project rule).

Compiled (8 of 23 actions): randomMousePosition, mouseMove (as RMOUSE region 1x1),
mouseClick, typeText, delay, forLoop, waitForLight (plain + armed), comment.
Blocked (15): findImage, waitForSound, keystroke, keyDown, keyUp, mouseScroll,
label, gotoLabel, rawCommand, randomPackage, parallelGroup, playAudio, playScript,
runExe, openFile (+ any unknown type). If/Else heads (insertIfElse) are blocked too:
IFLUX/ELSE/ENDIF are gen-2 ops.

Gen-1 compensations vs plan_gen (gen-3) - the gen-1 engine DEFAULTS mid-pauses and
idle breaks ON, so the twin emits them EXPLICITLY:
  * mid= is always emitted (mid=0:0,0 disables hesitations when the step has none);
  * idle= is always emitted (idle=1,1:0,0 disables the long distraction breaks);
  * mouseMove always carries idle=1,1:0,0 (a point-to-point move never idles).
"""
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

PLAN_VERSION = 1
ENGINE_VERSION = "0.9.64b"
TIME_UNIT_SEC = {"second": 1, "minute": 60, "hour": 3600}
CONDITIONAL = ("findImage", "waitForSound", "waitForLight")
LOOP_OWNERS = ("forLoop", "randomPackage", "parallelGroup")

MOD_VK = {"Ctrl": 162, "Shift": 160, "Alt": 164, "Win": 91}
VK = {}
for _c in range(ord("A"), ord("Z") + 1):
    VK[chr(_c)] = _c
for _i in range(10):
    VK[str(_i)] = 48 + _i
for _f in range(1, 13):
    VK["F%d" % _f] = 111 + _f
VK.update({"ENTER": 13, "ESC": 27, "SPACE": 32, "TAB": 9, "BACKSPACE": 8,
           "DELETE": 46, "INSERT": 45, "HOME": 36, "END": 35, "PGUP": 33, "PGDN": 34,
           "LEFT": 37, "UP": 38, "RIGHT": 39, "DOWN": 40,
           "CAPS LOCK": 20, "NUM LOCK": 144, "SCROLL LOCK": 145,
           "PRINT SCREEN": 44, "BREAK": 19})
for _i in range(10):
    VK["NUMPAD%d" % _i] = 96 + _i
VK.update({",": 188, "-": 189, ".": 190, "/": 191, ";": 186, "=": 187,
           "[": 219, "]": 221, "\\": 220, "'": 222, "`": 192})
VK.update({"SHIFT": 160, "CTRL": 162, "CONTROL": 162, "ALT": 164, "WIN": 91, "LWIN": 91})


# -- prop helpers (mirror PropEx: missing key -> default) -----------------------------
def _pi(p, key, default=0):
    v = p.get(key, default)
    if isinstance(v, bool):
        return 1 if v else 0
    try:
        return int(v)
    except Exception:
        try:
            return int(float(v))
        except Exception:
            return default


def _pd(p, key, default=0.0):
    v = p.get(key, default)
    try:
        return float(v)
    except Exception:
        return default


def _ps(p, key, default=""):
    v = p.get(key, default)
    return default if v is None else str(v)


def _pb(p, key, default=False):
    v = p.get(key, default)
    if isinstance(v, str):
        return v.strip().lower() in ("true", "1", "yes")
    return bool(v)


def _pair(mn, mx):
    return (mx, mn) if mx < mn else (mn, mx)


def pct_type(text):
    """|, % and newline are structural in plan.txt; the board types ASCII 0x20-0x7E only."""
    out = []
    for ch in text.replace("\r\n", "\n").replace("\r", "\n"):
        o = ord(ch)
        if ch == "\n":
            out.append("%0A")
        elif ch == "%":
            out.append("%25")
        elif ch == "|":
            out.append("%7C")
        elif 32 <= o <= 126:
            out.append(ch)
        else:
            raise ValueError(
                "non-ASCII/control character %r - the board types ASCII only. "
                "Use keystrokes with named keys, or keep the text Latin." % ch)
    return "".join(out)


def is_marker(node, kind=None):
    if node.get("Type") != "comment":
        return False
    t = _ps(node.get("Props") or {}, "text").strip().lower()
    if kind == "next":
        return t == "next"
    if kind == "else":
        return t.startswith("else")
    if kind == "endif":
        return t.startswith("end if")
    return t == "next" or t.startswith("else") or t.startswith("end if")


class PlanBlocked(Exception):
    def __init__(self, errors):
        super().__init__("blocked")
        self.errors = errors


class Gen:
    def __init__(self, opts):
        self.opts = opts
        self.lines = []
        self.errors = []
        self.flags = []
        self.disabled = []
        self.markers = 0
        self.counts = {}
        self._numbering = {}

    def _num(self, node):
        return node.get("_num", self._numbering.get(id(node), "?"))

    def _title(self, node):
        name = (node.get("Name") or "").strip()
        return "step %s (%s)%s" % (self._num(node), node.get("Type"),
                                   " - «%s»" % name if name else "")

    def error(self, node, msg):
        self.errors.append("%s: %s" % (self._title(node), msg))

    def flag(self, node, msg):
        self.flags.append("%s: %s" % (self._title(node), msg))
        self.lines.append("# FLAG %s" % self.flags[-1])

    def emit(self, node, ops, count_type=None):
        self.lines.extend(ops)
        key = count_type or node.get("Type")
        self.counts[key] = self.counts.get(key, 0) + 1
        self.emit_delay(node)

    def emit_delay(self, node):
        d = int(node.get("Delay") or 0)
        dm = int(node.get("DelayMax") or 0)
        if dm > 0:
            lo, hi = _pair(d, dm)
            if hi > 0:
                self.lines.append("DELAY|%d,%d" % (lo, hi))
        elif d > 0:
            self.lines.append("DELAY|%d" % d)

    def count(self, node, key):
        self.counts[key] = self.counts.get(key, 0) + 1

    # -- main walk -----------------------------------------------------------
    def walk(self, nodes):
        i = 0
        while i < len(nodes):
            n = nodes[i]
            t = n.get("Type")
            n["_num"] = self._numbering.get(id(n), "?")
            if n.get("IsDisabled"):
                self.disabled.append(self._title(n))
                i += 1
                continue
            if is_marker(n):
                self.error(n, "structural marker '%s' has no matching block head - open and "
                              "re-save the plan in the app (it self-heals), then export again"
                              % _ps(n.get("Props") or {}, "text").strip())
                i += 1
                continue
            if t in CONDITIONAL and _pb(n.get("Props") or {}, "insertIfElse"):
                i = self._emit_if(nodes, i)
                continue
            self._emit_step(nodes, i)
            i = self._consume_next(nodes, i) if t in LOOP_OWNERS else i + 1

    def _consume_next(self, nodes, i):
        if i + 1 < len(nodes) and is_marker(nodes[i + 1], "next"):
            self.markers += 1
            return i + 2
        return i + 1

    def _emit_if(self, nodes, i):
        """An insertIfElse head is a BLOCKING error on PLAN|1 (IFLUX/ELSE/ENDIF are gen-2);
        its Else/End If markers are still consumed so they do not cascade as stray-marker
        errors, and nested blockers are still NAMED."""
        n = nodes[i]
        t = n["Type"]
        else_node = None
        endif_node = None
        j = i + 1
        if j < len(nodes) and is_marker(nodes[j], "else"):
            else_node = nodes[j]
            j += 1
        if j < len(nodes) and is_marker(nodes[j], "endif"):
            endif_node = nodes[j]
            j += 1
        if t == "findImage":
            self.error(n, "findImage needs machine vision - it cannot run on the Pico. Convert "
                          "this step to Wait For Light (BH1750) or Wait For Sound in the app first")
            self._collect_blockers(n)
        elif t == "waitForSound":
            self.error(n, "waitForSound has no WSND op in the PLAN|1 contract of firmware "
                          + ENGINE_VERSION + " (the sound sensor needs the gen-2 line) - convert "
                          "the trigger to Wait For Light, or keep this plan in PC mode")
        else:
            self.error(n, "If/Else needs the gen-2 plan engine (IFLUX/ELSE/ENDIF are not in the "
                          "PLAN|1 contract of firmware " + ENGINE_VERSION + ") - uncheck "
                          "'Insert If-Else' so the step runs as a plain wait, or keep this plan "
                          "in PC mode")
        for sub in (else_node, endif_node):
            if sub is not None:
                self.markers += 1
                self._collect_blockers(sub)
        return j

    def _collect_blockers(self, node):
        for c in node.get("Children") or []:
            ct = c.get("Type")
            c["_num"] = self._numbering.get(id(c), "?")
            if c.get("IsDisabled"):
                continue
            if ct == "findImage":
                self.error(c, "findImage needs machine vision - it cannot run on the Pico (nested "
                              "inside an already-blocked step)")
            elif ct == "typeText":
                cp = c.get("Props") or {}
                if _pb(cp, "secret") or _ps(cp, "mode", "keystrokes") == "clipboard":
                    self.error(c, "secret/clipboard typing needs a PC clipboard (nested inside an "
                                  "already-blocked step)")
            self._collect_blockers(c)

    # -- per-step emitters ---------------------------------------------------
    def _emit_step(self, nodes, i):
        n = nodes[i]
        p = n.get("Props") or {}
        t = n.get("Type")
        emit = getattr(self, "_st_" + t, None) if t else None
        if emit is None:
            self.error(n, "unknown step type '%s' - this exporter does not know it "
                          "(supported: the 23 app actions)" % t)
            return
        emit(n, p)

    def _blocked(self, n, msg):
        self.error(n, msg)

    def _st_comment(self, n, p):
        self.lines.append("# " + _ps(p, "text").replace("\n", " "))
        self.count(n, "comment")

    def _st_delay(self, n, p):
        lo, hi = _pair(_pi(p, "minMs", 0), _pi(p, "maxMs", 333))
        self.emit(n, ["DELAY|%d,%d" % (lo, hi)] if hi > lo else ["DELAY|%d" % lo], "DELAY")

    def _tuning(self, p, idle_values):
        """The humanized-move layers; gen-1 forces EVERY key explicit (its built-in
        defaults enable mid-pauses at 12%% and idle breaks at 1000-5000 ms - the app
        defaults differ, so an omitted key would change behavior on the Pico)."""
        b0, b1 = _pair(_pi(p, "pauseBeforeMin", 60), _pi(p, "pauseBeforeMax", 220))
        a0, a1 = _pair(_pi(p, "pauseAfterMin", 80), _pi(p, "pauseAfterMax", 280))
        c0, c1 = _pair(_pi(p, "curveMinPct", 20), _pi(p, "curveMaxPct", 40))
        parts = ["before=%d,%d" % (b0, b1), "after=%d,%d" % (a0, a1), "curve=%d,%d" % (c0, c1)]
        mch = max(0, min(100, _pi(p, "midPauseChance", 6)))
        m0, m1 = _pair(_pi(p, "midPauseMin", 80), _pi(p, "midPauseMax", 250))
        parts.append("mid=%d:%d,%d" % (mch, m0, m1))
        parts.append("over=%d" % max(0, min(100, _pi(p, "overshootChance", 12))))
        t0, t1 = _pair(_pi(p, "moveTimeMin", 0), _pi(p, "moveTimeMax", 0))
        if t1 > 0:
            parts.append("mt=%d,%d" % (t0, t1))
        i0, i1, p0, p1 = idle_values
        parts.append("idle=%d,%d:%d,%d" % (i0, i1, p0, p1))
        return "|" + "|".join(parts)

    def _st_randomMousePosition(self, n, p):
        x, y = _pi(p, "x", 1301), _pi(p, "y", 0)
        w, h = _pi(p, "w", 378), _pi(p, "h", 1049)
        if w <= 0 or h <= 0:
            self.error(n, "region width/height must be positive (got %dx%d)" % (w, h))
            return
        i0, i1 = _pair(_pi(p, "idleEveryMin", 5), _pi(p, "idleEveryMax", 12))
        p0, p1 = _pair(_pi(p, "idlePauseMin", 800), _pi(p, "idlePauseMax", 3000))
        # explicit-off is read from the RAW idlePauseMax (max pause = 0 => no idle breaks),
        # BEFORE _pair swaps the bounds - otherwise (min=800, max=0) would swap to (0,800) and
        # the off-intent would be lost (CI run #2 caught exactly this).
        idle = (i0, i1, p0, p1) if _pi(p, "idlePauseMax", 3000) > 0 else (1, 1, 0, 0)
        self.emit(n, ["RMOUSE|region=%d,%d,%d,%d%s" % (x, y, w, h, self._tuning(p, idle))], "RMOUSE")

    def _st_mouseMove(self, n, p):
        """PLAN|2 samples a fresh destination from the fixed step's rectangle."""
        x, y = _pi(p, "x", 600), _pi(p, "y", 497)
        w, h = _pi(p, "w", 1), _pi(p, "h", 1)
        if w <= 0 or h <= 0:
            self.error(n, "region width/height must be positive (got %dx%d)" % (w, h))
            return
        if not _pb(p, "human", True):
            self.flag(n, "PLAN|2 RMOUSE always uses a humanized path; human=false cannot be preserved in portable export")
        self.emit(n, ["RMOUSE|region=%d,%d,%d,%d%s" % (x, y, w, h, self._tuning(p, (1, 1, 0, 0)))], "MOVETO")

    def _st_mouseClick(self, n, p):
        btn = _ps(p, "button", "left")
        if btn not in ("left", "middle", "right"):
            self.error(n, "unknown mouse button '%s'" % btn)
            return
        count = 2 if _ps(p, "action", "single") == "double" else 1
        hmin, hmax = _pair(_pi(p, "holdMin", 0), _pi(p, "holdMax", 0))
        line = "CLICK|btn=%s|n=%d" % (btn, count)
        if hmax > 0:
            line += "|hold=%d,%d" % (hmin, hmax)
        self.emit(n, [line], "CLICK")

    def _st_typeText(self, n, p):
        if _pb(p, "secret"):
            self.error(n, "secret steps type through the Windows clipboard, which does not exist "
                          "on a bare Pico - re-enter the text as plain keystrokes (or keep this "
                          "plan in PC mode)")
            return
        if _ps(p, "mode", "keystrokes") == "clipboard":
            self.error(n, "clipboard mode needs a PC clipboard - the portable plan can only type "
                          "keystrokes. Switch the step to keystrokes mode (text must be ASCII)")
            return
        try:
            text = pct_type(_ps(p, "text"))
        except ValueError as exc:
            self.error(n, str(exc))
            return
        if not text:
            self.error(n, "empty text - nothing to type")
            return
        hmin, hmax = _pair(_pi(p, "hmin", self.opts["type_key_min"]), _pi(p, "hmax", self.opts["type_key_max"]))
        parts = ["h=%d,%d" % (hmin, hmax)]
        wmin, wmax = _pair(_pi(p, "wmin", 0), _pi(p, "wmax", 0))
        if wmax > 0:
            parts.append("w=%d,%d" % (wmin, wmax))
            if "wordPauseChance" in p:
                parts.append("wp=%d" % max(0, min(100, _pi(p, "wordPauseChance", 100))))
        pmin, pmax = _pair(_pi(p, "pmin", 0), _pi(p, "pmax", 0))
        if pmax > 0:
            parts.append("p=%d,%d" % (pmin, pmax))
        tch = max(0, min(100, _pi(p, "thinkChance", 0)))
        if tch > 0:
            t0, t1 = _pair(_pi(p, "thinkMin", 800), _pi(p, "thinkMax", 2200))
            if t1 > 0:
                parts.append("think=%d:%d,%d" % (tch, t0, t1))
        y0, y1 = _pair(_pi(p, "typoEveryMin", 0), _pi(p, "typoEveryMax", 0))
        if y1 > 0:
            parts.append("typo=%d,%d" % (y0, y1))
        elif _pi(p, "typoChance", 0) > 0:
            self.flag(n, "legacy typoChance %% is not portable - use 'typo every N words' "
                         "(typoEveryMin/Max); the step types WITHOUT typos in this plan")
        self.emit(n, ["TYPE|text=%s|%s" % (text, "|".join(parts))], "TYPE")

    def _st_forLoop(self, n, p):
        mode = _ps(p, "mode", "count")
        if mode == "time":
            unit = _ps(p, "timeUnit", "minute")
            if unit not in TIME_UNIT_SEC:
                self.error(n, "unknown time unit '%s'" % unit)
                return
            sec = _pi(p, "timeValue", 10) * TIME_UNIT_SEC[unit]
            if sec <= 0:
                self.error(n, "loop time must be positive (got %d %s)" % (_pi(p, "timeValue", 10), unit))
                return
            head = "LOOPTIME|%d" % sec
        elif mode == "infinite":
            head = "LOOP|0"
        elif mode == "count":
            cnt = _pi(p, "count", 10)
            if cnt <= 0:
                self.error(n, "loop count must be positive (got %d) - use 'infinite' for forever" % cnt)
                return
            head = "LOOP|%d" % cnt
        else:
            self.error(n, "unknown loop mode '%s'" % mode)
            return
        self.lines.append(head)
        self.count(n, "LOOP")
        self.walk(n.get("Children") or [])
        self.lines.append("ENDLOOP")
        self.emit_delay(n)

    def _st_waitForLight(self, n, p):
        lo, hi, stable, to, mode = lux_args(p)
        if _pb(p, "armed"):
            vk = VK.get(_ps(p, "key", "E"))
            if vk is None:
                self.error(n, "unknown key name '%s' for waitForLight armed key" % _ps(p, "key", "E"))
                return
            hmin, hmax = _pair(_pi(p, "holdMin", 30), _pi(p, "holdMax", 90))
            rmin, rmax = _pair(_pi(p, "reactMin", 80), _pi(p, "reactMax", 180))
            self.emit(n, ["WLIGHT|%d,%d,%d,%d,%d|key=%d,%d,%d|react=%d,%d" % (
                lo, hi, stable, to, mode, vk, hmin, hmax, rmin, rmax)], "WLIGHT")
        else:
            self.emit(n, ["WLIGHT|%d,%d,%d,%d,%d" % (lo, hi, stable, to, mode)], "WLIGHT")

    # -- gen-1 blocking errors (never silent) --------------------------------
    def _st_findImage(self, n, p):
        self.error(n, "findImage needs machine vision - it cannot run on the Pico. Convert "
                      "this step to Wait For Light (BH1750) or Wait For Sound in the app first")
        self._collect_blockers(n)

    def _st_waitForSound(self, n, p):
        self._blocked(n, "waitForSound has no WSND op in the PLAN|1 contract of firmware "
                         + ENGINE_VERSION + " (the sound sensor needs the gen-2 line) - convert "
                         "the trigger to Wait For Light, or keep this plan in PC mode")

    def _st_keystroke(self, n, p):
        self._blocked(n, "the PLAN|1 engine of firmware " + ENGINE_VERSION + " has no KEY op "
                         "(it types TEXT only) - a Keystroke cannot be expressed; use Type Text "
                         "for ASCII text or keep this plan in PC mode until the gen-2 line ships")

    def _st_keyDown(self, n, p):
        self._blocked(n, "the PLAN|1 engine of firmware " + ENGINE_VERSION + " has no KDOWN op "
                         "- keep this plan in PC mode until the gen-2 line ships")

    def _st_keyUp(self, n, p):
        self._blocked(n, "the PLAN|1 engine of firmware " + ENGINE_VERSION + " has no KUP op "
                         "- keep this plan in PC mode until the gen-2 line ships")

    def _st_mouseScroll(self, n, p):
        self._blocked(n, "the PLAN|1 engine of firmware " + ENGINE_VERSION + " has no WHEEL op "
                         "- keep this plan in PC mode until the gen-2 line ships")

    def _st_label(self, n, p):
        self._blocked(n, "labels need the gen-2 engine (LABEL/GOTO are not in PLAN|1) - keep "
                         "this plan in PC mode until the gen-2 line ships")

    def _st_gotoLabel(self, n, p):
        self._st_label(n, p)

    def _st_rawCommand(self, n, p):
        self._blocked(n, "RAW passthrough is a gen-2 op - on PLAN|1 the engine would reject the "
                         "line; keep this plan in PC mode")

    def _st_randomPackage(self, n, p):
        self._blocked(n, "random packages need the gen-2 engine (RPKG is not in PLAN|1) - keep "
                         "this plan in PC mode until the gen-2 line ships")

    def _st_parallelGroup(self, n, p):
        self._blocked(n, "parallel groups need the gen-2 engine (PGROUP is not in PLAN|1) - keep "
                         "this plan in PC mode until the gen-2 line ships")

    def _st_playAudio(self, n, p):
        self._blocked(n, "playAudio has no op in the PLAN|1 contract (the buzzer/BEEP belongs to "
                         "the gen-2 line) - keep this plan in PC mode")

    def _st_playScript(self, n, p):
        self._blocked(n, "playScript needs the gen-2 engine (INCLUDE is not in PLAN|1) - inline "
                         "the child steps, or keep this plan in PC mode")

    def _st_runExe(self, n, p):
        self._blocked(n, "runExe compiles to a Win+R macro on the gen-2 line only - PLAN|1 has no "
                         "launch ops; keep this plan in PC mode")

    def _st_openFile(self, n, p):
        self._blocked(n, "openFile compiles to a Win+R macro on the gen-2 line only - PLAN|1 has "
                         "no launch ops; keep this plan in PC mode")


def lux_args(p):
    center = _pi(p, "luxCenter", 1250)
    tol = max(1, _pi(p, "luxTolerance", 50))
    stable = max(0, int(round(_pd(p, "stableSec", 2) * 1000)))
    to = _pi(p, "timeoutMs", 20000)
    mode = 1 if _ps(p, "sampleMode", "hires") == "lowres" else 0
    return max(0, center - tol), center + tol, stable, to, mode


DEFAULT_SETTINGS = {"PlayRepeatMode": "once", "PlayRepeatTimes": 10, "PlayRepeatValue": 1,
                    "PlayRepeatUnit": "minute", "MouseMoveSpeedMin": 300, "MouseMoveSpeedMax": 2000,
                    "TypeKeyMinMs": 80, "TypeKeyMaxMs": 220, "KeyboardBoard": "pico"}


def number_tree(nodes, prefix=""):
    out = {}
    for i, n in enumerate(nodes):
        num = "%s%d" % (prefix + "." if prefix else "", i + 1)
        n["_num"] = num
        out[id(n)] = num
        out.update(number_tree(n.get("Children") or [], num))
    return out


def compile_doc(steps, opts):
    gen = Gen(opts)
    gen._numbering = number_tree(steps)
    gen.walk(steps)
    return gen


def build(steps, settings, screen_w, screen_h, source_name, machine, generated):
    """Full build: Play Options wrapper + header. Returns (text, gen) or raises PlanBlocked."""
    opts = {"type_key_min": settings.get("TypeKeyMinMs", 80),
            "type_key_max": settings.get("TypeKeyMaxMs", 220)}
    gen = compile_doc(steps, opts)
    body = list(gen.lines)
    mode = settings.get("PlayRepeatMode", "once")
    if mode == "times":
        times = settings.get("PlayRepeatTimes", 10)
        if not isinstance(times, int) or times <= 0:
            gen.errors.append("settings: PlayRepeatTimes must be a positive integer (got %r)" % (times,))
        else:
            body = ["LOOP|%d" % times] + body + ["ENDLOOP"]
    elif mode == "timed":
        unit = settings.get("PlayRepeatUnit", "minute")
        sec = settings.get("PlayRepeatValue", 1) * TIME_UNIT_SEC.get(unit, 0)
        if unit not in TIME_UNIT_SEC or not isinstance(sec, int) or sec <= 0:
            gen.errors.append("settings: bad timed repeat (%r %r)" % (settings.get("PlayRepeatValue"), unit))
        else:
            body = ["LOOPTIME|%d" % sec] + body + ["ENDLOOP"]
    elif mode != "once":
        gen.errors.append("settings: unknown PlayRepeatMode '%s' (once|times|timed)" % mode)
    if settings.get("KeyboardBoard", "pico") == "promicro":
        gen.flags.append("settings: KeyboardBoard=promicro is bridge-mode only; on the portable plan "
                         "the Pico types every keyboard step (keyboard=Pico contract)")
        body.insert(0, "# FLAG " + gen.flags[-1])
    if gen.errors:
        raise PlanBlocked(gen.errors)
    text = "\n".join(["PLAN|1",
                      "# generated by Classroom Studio PlanExporter (engine " + ENGINE_VERSION + ") from %s" % source_name,
                      "# machine: %s · generated: %s" % (machine, generated),
                      "SCREEN|%d,%d" % (screen_w, screen_h),
                      "SPEED|%d,%d" % (settings.get("MouseMoveSpeedMin", 300),
                                       settings.get("MouseMoveSpeedMax", 2000)),
                      ""] + body) + "\n"
    return text, gen
