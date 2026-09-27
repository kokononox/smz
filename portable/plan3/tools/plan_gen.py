#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""plan_gen.py - compile a Classroom Studio .amsj document (+ the app's
ams-settings.json Play Options) into a portable plan.txt for the Pico
(plan engine v3, firmware pico-light 0.9.66+ / plan_api 3).

Hard rules (project page, 2026-09-09):
  * Play Options: once = no wrapper, times N = LOOP|N wrapper,
    timed = LOOPTIME|seconds wrapper around the WHOLE plan.
  * Step order and the per-step delay-after (Delay / DelayMax) match the app:
    for a container the delay lands AFTER the whole block, exactly where the
    app's runner applies it.
  * Disabled steps are counted and skipped - never silently dropped.
  * NO unsupported step is silently skipped. Blocking errors stop the build:
    findImage (needs machine vision), clipboard/secret typing (no OS clipboard
    on a bare Pico), non-ASCII text/paths, non-parallel steps inside a
    parallelGroup, If/Else inside a Random Package, missing/cyclic playScript
    children, broken If/Else markers, duplicate labels.
  * Anything compiled with a caveat gets a '# FLAG' line in the plan AND a line
    in the report (plan_check.py surfaces them).

Exit codes: 0 = plan written (flags may exist), 1 = blocked (nothing written),
2 = usage / IO error.
"""
import argparse
import hashlib
import json
import os
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")   # cp1252-safe console
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

_HERE = os.path.dirname(os.path.abspath(__file__))
for _cand in (_HERE, os.path.dirname(_HERE), os.path.join(os.path.dirname(_HERE), "CIRCUITPY"),
              os.path.join(_HERE, "CIRCUITPY"), os.getcwd()):
    if os.path.exists(os.path.join(_cand, "plan_engine.py")):
        sys.path.insert(0, _cand)
for _cand in (_HERE, os.path.dirname(_HERE), os.path.join(os.path.dirname(_HERE), "tools"), os.getcwd()):
    if os.path.exists(os.path.join(_cand, "winr.py")):
        sys.path.insert(0, _cand)
        break

import winr                                                     # noqa: E402
import plan_engine as pe                                        # noqa: E402  (self-check with the real engine)

APP_ID = "AMS"
MAX_INCLUDE_DEPTH = 4   # the engine logs-and-skips includes past depth 4 - plan_gen blocks instead

# ── KeyMap.cs (ams-shell main) — decimal VK codes the firmware expects (vk_to_hid) ──
MOD_VK = {"Ctrl": 162, "Shift": 160, "Alt": 164, "Win": 91}
VK = {}
for _c in range(ord("A"), ord("Z") + 1):
    VK[chr(_c)] = _c                                            # A-Z = 65-90
for _i in range(10):
    VK[str(_i)] = 48 + _i                                       # 0-9
for _f in range(1, 13):
    VK["F%d" % _f] = 111 + _f                                   # F1-F12 = 112-123
VK.update({"ENTER": 13, "ESC": 27, "SPACE": 32, "TAB": 9, "BACKSPACE": 8,
           "DELETE": 46, "INSERT": 45, "HOME": 36, "END": 35, "PGUP": 33, "PGDN": 34,
           "LEFT": 37, "UP": 38, "RIGHT": 39, "DOWN": 40,
           "CAPS LOCK": 20, "NUM LOCK": 144, "SCROLL LOCK": 145,
           "PRINT SCREEN": 44, "BREAK": 19})
for _i in range(10):
    VK["NUMPAD%d" % _i] = 96 + _i                               # 96-105
VK.update({",": 188, "-": 189, ".": 190, "/": 191, ";": 186, "=": 187,
           "[": 219, "]": 221, "\\": 220, "'": 222, "`": 192})
# Bare modifier names for keyDown/keyUp: the app's dialog DEFAULTS keyDown to "SHIFT"
# (StepDefinitions.cs) even though KeyMap.VK only carries the left-modifier VKs under
# Modifiers. The firmware's vk_to_hid accepts those VKs, so the aliases are mapped here.
VK.update({"SHIFT": 160, "CTRL": 162, "CONTROL": 162, "ALT": 164, "WIN": 91, "LWIN": 91})

TIME_UNIT_SEC = {"second": 1, "minute": 60, "hour": 3600}
CONDITIONAL = ("findImage", "waitForSound", "waitForLight")
LOOP_OWNERS = ("forLoop", "randomPackage", "parallelGroup")    # v0.9.48 - they own the Next marker
# the only step types the portable engine may put inside a parallelGroup branch
# (engine _PAR_OK = MOVETO, CLICK, KEY, KDOWN, KUP, WHEEL, TYPE, DELAY, RAW, BEEP)
PGROUP_OK = {"mouseMove", "mouseClick", "mouseScroll", "keystroke", "keyDown", "keyUp",
             "typeText", "delay", "rawCommand", "comment"}

# ── prop helpers (mirror PropEx: missing key -> StepDefinitions default) ─────
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
    """Engine/app ranges are swap-tolerant."""
    return (mx, mn) if mx < mn else (mn, mx)

def pct_type(text):
    """TYPE text encoder: '|', '%' and newline are structural in plan.txt.
    The board types ASCII 0x20-0x7E only; anything else is a blocking error
    (the 2026-09-04 decision: non-Latin typing is out of scope for the port)."""
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

def safe_plan_filename(base):
    """INCLUDE file names live on the Pico drive and inside a key=value line."""
    if not base:
        return None
    for ch in base:
        o = ord(ch)
        if o < 32 or o > 126 or ch in "/\\:|%":
            return None
    return base + ".txt"

def is_marker(node, kind=None):
    """The app's structural markers are comment steps: 'Next' / 'Else...' / 'End If'."""
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

class _Blocked(Exception):
    def __init__(self, errors):
        super().__init__("blocked")
        self.errors = errors

class Gen:
    """One compilation: source -> plan lines (+ child include descriptors)."""

    def __init__(self, opts, source_path, depth=0, include_stack=()):
        self.opts = opts
        self.source_path = source_path
        self.depth = depth
        self.include_stack = include_stack
        self.lines = []
        self.errors = []
        self.flags = []
        self.disabled = []        # names of skipped disabled steps
        self.markers = 0          # structural markers consumed
        self.counts = {}          # per-type emission counts
        self.children_files = []  # (file name, abs path, chain, depth) of playScript children
        self.labels = {}          # label name -> node number (duplicate detection)
        self._numbering = {}

    # ── reporting ─────────────────────────────────────────────────────────────
    def _num(self, node):
        return node.get("_num", self._numbering.get(id(node), "?"))

    def _title(self, node):
        name = (node.get("Name") or "").strip()
        return "step %s (%s)%s" % (self._num(node), node.get("Type"),
                                   " — «%s»" % name if name else "")

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
        """Per-step 'delay after' - exactly the app's Delay / DelayMax semantics."""
        d = _pi(node, "Delay", 0)
        dm = node.get("DelayMax")
        dm = _pi(node, "DelayMax", 0) if dm is not None else 0
        if dm > 0:
            lo, hi = _pair(d, dm)
            if hi > 0:
                self.lines.append("DELAY|%d,%d" % (lo, hi))
        elif d > 0:
            self.lines.append("DELAY|%d" % d)

    def count(self, node, key):
        self.counts[key] = self.counts.get(key, 0) + 1

    # ── main walk ─────────────────────────────────────────────────────────────
    def walk(self, nodes, in_pgroup=False):
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
            if in_pgroup and t not in PGROUP_OK:
                self.error(n, "'%s' cannot live inside a Parallel Group on the Pico - only short, "
                              "immediate steps can (%s). Move it out of the group"
                              % (t, ", ".join(sorted(PGROUP_OK - {"comment"}))))
                i = self._consume_next(nodes, i) if t in LOOP_OWNERS else i + 1
                continue
            if t in CONDITIONAL and _pb(n.get("Props") or {}, "insertIfElse"):
                i = self._emit_if(nodes, i)
                continue
            self._emit_step(nodes, i, in_pgroup)
            i = self._consume_next(nodes, i) if t in LOOP_OWNERS else i + 1

    def _consume_next(self, nodes, i):
        """A loop/package/group head owns the following 'Next' marker (v0.9.48)."""
        if i + 1 < len(nodes) and is_marker(nodes[i + 1], "next"):
            self.markers += 1
            return i + 2
        return i + 1

    def _emit_if(self, nodes, i):
        """insertIfElse head: children = Then, sibling comment 'Else' = Else, 'End If' closes."""
        n = nodes[i]
        p = n.get("Props") or {}
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
        if endif_node is None:
            self.error(n, "insertIfElse is on but the matching 'End If' marker is missing - open "
                          "and re-save the plan in the app (it self-heals), then export again")
        if t == "findImage":
            self.error(n, "findImage needs machine vision - it cannot run on the Pico. Convert "
                          "this step to Wait For Light (BH1750) or Wait For Sound in the app first")
            self._collect_blockers(n)                  # nested blockers must be LISTED, not hidden
            for sub in (else_node, endif_node):
                if sub is not None:
                    self._collect_blockers(sub)
            return j                                   # markers consumed, no cascade errors
        if t == "waitForSound":
            self.lines.append("IFSND|%d,%d,%d" % (_pi(p, "threshold", 90), _pi(p, "minDurationMs", 60),
                                                  _pi(p, "timeoutMs", 20000)))
            self.count(n, "IFSND")
        else:
            lo, hi, stable, to, mode = self._lux_args(p)
            self.lines.append("IFLUX|%d,%d,%d,%d,%d" % (lo, hi, stable, to, mode))
            self.count(n, "IFLUX")
        self.walk(n.get("Children") or [])
        if else_node is not None:
            self.lines.append("ELSE")
            self.markers += 1
            self.walk(else_node.get("Children") or [])
        if endif_node is not None:
            self.lines.append("ENDIF")
            self.markers += 1
        self.emit_delay(n)                             # the app's delay lands after the whole If block
        return j

    @staticmethod
    def _lux_args(p):
        center = _pi(p, "luxCenter", 1250)
        tol = max(1, _pi(p, "luxTolerance", 50))
        stable = max(0, int(round(_pd(p, "stableSec", 2) * 1000)))
        to = _pi(p, "timeoutMs", 20000)
        mode = 1 if _ps(p, "sampleMode", "hires") == "lowres" else 0
        return max(0, center - tol), center + tol, stable, to, mode

    # ── per-step emitters ─────────────────────────────────────────────────────
    def _emit_step(self, nodes, i, in_pgroup):
        n = nodes[i]
        p = n.get("Props") or {}
        t = n.get("Type")
        emit = getattr(self, "_st_" + t, None) if t else None
        if emit is None:
            self.error(n, "unknown step type '%s' - this plan_gen does not know it "
                          "(supported: the 23 app actions)" % t)
            return
        emit(n, p)

    def _st_comment(self, n, p):
        self.lines.append("# " + _ps(p, "text").replace("\n", " "))
        self.count(n, "comment")

    def _st_delay(self, n, p):
        lo, hi = _pair(_pi(p, "minMs", 0), _pi(p, "maxMs", 333))
        self.emit(n, ["DELAY|%d,%d" % (lo, hi)] if hi > lo else ["DELAY|%d" % lo], "DELAY")

    def _st_label(self, n, p):
        name = _ps(p, "label", "label1").strip()
        if not name or "=" in name or "|" in name:
            self.error(n, "bad label name '%s'" % name)
            return
        if name in self.labels:
            self.error(n, "duplicate label '%s' (first at step %s) - the engine rejects duplicates"
                          % (name, self.labels[name]))
            return
        self.labels[name] = self._num(n)
        self.emit(n, ["LABEL|%s" % name], "LABEL")

    def _st_gotoLabel(self, n, p):
        name = _ps(p, "label", "").strip()
        if not name:
            self.error(n, "Go To Label with no label chosen")
            return
        self.emit(n, ["GOTO|%s" % name], "GOTO")

    def _st_rawCommand(self, n, p):
        cmd = _ps(p, "cmd", "PING").strip()
        if not cmd:
            self.error(n, "empty raw command")
            return
        if "\n" in cmd or "\r" in cmd:
            self.error(n, "raw command must be a single line")
            return
        self.emit(n, ["RAW|" + cmd], "RAW")

    def _st_mouseMove(self, n, p):
        x, y = _pi(p, "x", 600), _pi(p, "y", 497)
        if not _pb(p, "human", True):
            self.emit(n, ["MOVETO|x=%d|y=%d|human=0" % (x, y)], "MOVETO")
            return
        self.emit(n, ["MOVETO|x=%d|y=%d%s" % (x, y, self._tuning(p, idle=False))], "MOVETO")

    def _st_randomMousePosition(self, n, p):
        x, y = _pi(p, "x", 1301), _pi(p, "y", 0)
        w, h = _pi(p, "w", 378), _pi(p, "h", 1049)
        if w <= 0 or h <= 0:
            self.error(n, "region width/height must be positive (got %dx%d)" % (w, h))
            return
        self.emit(n, ["RMOUSE|region=%d,%d,%d,%d%s" % (x, y, w, h, self._tuning(p, idle=True))], "RMOUSE")

    def _tuning(self, p, idle):
        """The humanized-move layers, exactly the app's field names (StepDefinitions.cs)."""
        b0, b1 = _pair(_pi(p, "pauseBeforeMin", 60), _pi(p, "pauseBeforeMax", 220))
        a0, a1 = _pair(_pi(p, "pauseAfterMin", 80), _pi(p, "pauseAfterMax", 280))
        c0, c1 = _pair(_pi(p, "curveMinPct", 20), _pi(p, "curveMaxPct", 40))
        parts = ["before=%d,%d" % (b0, b1), "after=%d,%d" % (a0, a1), "curve=%d,%d" % (c0, c1)]
        mch = _pi(p, "midPauseChance", 6)
        if mch > 0:
            m0, m1 = _pair(_pi(p, "midPauseMin", 80), _pi(p, "midPauseMax", 250))
            parts.append("mid=%d:%d,%d" % (min(100, mch), m0, m1))
        parts.append("over=%d" % max(0, min(100, _pi(p, "overshootChance", 12))))
        t0, t1 = _pair(_pi(p, "moveTimeMin", 0), _pi(p, "moveTimeMax", 0))
        if t1 > 0:
            parts.append("mt=%d,%d" % (t0, t1))
        if idle:
            i0, i1 = _pair(_pi(p, "idleEveryMin", 5), _pi(p, "idleEveryMax", 12))
            p0, p1 = _pair(_pi(p, "idlePauseMin", 800), _pi(p, "idlePauseMax", 3000))
            if p1 > 0:
                parts.append("idle=%d,%d:%d,%d" % (i0, i1, p0, p1))
        return "|" + "|".join(parts)

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

    def _st_mouseScroll(self, n, p):
        self.emit(n, ["WHEEL|%d" % _pi(p, "delta", -1)], "WHEEL")

    def _st_keystroke(self, n, p):
        vks = []
        for label, key in (("Ctrl", "modCtrl"), ("Shift", "modShift"), ("Alt", "modAlt"), ("Win", "modWin")):
            if _pb(p, key):
                vks.append(MOD_VK[label])
        vk = self._vk(_ps(p, "key", "F4"), "keystroke", n)
        if not vk:
            return
        vks.append(vk)
        hmin, hmax = _pair(_pi(p, "holdMin", 0), _pi(p, "holdMax", 0))
        line = "KEY|combo=%s" % "+".join(str(v) for v in vks)
        if hmax > 0:
            line += "|hold=%d,%d" % (hmin, hmax)
        self._keyboard_board_flag(n, p)
        self.emit(n, [line], "KEY")

    def _st_keyDown(self, n, p):
        vk = self._vk(_ps(p, "key", "SHIFT"), "keyDown", n)
        if vk:
            self._keyboard_board_flag(n, p)
            self.emit(n, ["KDOWN|%d" % vk], "KDOWN")

    def _st_keyUp(self, n, p):
        vk = self._vk(_ps(p, "key", "SHIFT"), "keyUp", n)
        if vk:
            self._keyboard_board_flag(n, p)
            self.emit(n, ["KUP|%d" % vk], "KUP")

    def _vk(self, name, what, node):
        vk = VK.get(name)
        if vk is None:
            self.error(node, "unknown key name '%s' for %s" % (name, what))
            return 0
        return vk

    def _keyboard_board_flag(self, n, p):
        """Portable contract (2026-09-08): the Pico ALWAYS executes keyboard steps;
        a per-step Pro Micro override is a bridge-mode feature, never silently honored."""
        if _ps(p, "keyboardBoard", "default") == "promicro":
            self.flag(n, "keyboard executor 'promicro' is bridge-mode only; on the portable plan "
                         "the Pico types it (keyboard=Pico contract)")

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
            parts.append("typochars=%d,%d" % (y0, y1))
        elif _pi(p, "typoChance", 0) > 0:
            self.flag(n, "legacy typoChance %% is not portable - use the character interval "
                         "(typoEveryMin/Max); the step types WITHOUT typos in this plan")
        self._keyboard_board_flag(n, p)
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
        self.emit_delay(n)                             # the app's delay lands after the whole loop

    def _st_randomPackage(self, n, p):
        mode = _ps(p, "mode", "shuffleAll")
        if mode not in ("shuffleAll", "randomSubset"):
            self.error(n, "unknown random package mode '%s'" % mode)
            return
        # group children into drawable items: Next markers after a container are
        # consumed; an If/Else structure cannot be shuffled coherently -> blocked.
        kids = n.get("Children") or []
        items = []
        k = 0
        while k < len(kids):
            c = kids[k]
            ct = c.get("Type")
            c["_num"] = self._numbering.get(id(c), "?")
            if c.get("IsDisabled"):
                self.disabled.append(self._title(c))
                k += 1
                continue
            if is_marker(c, "next") and items and items[-1].get("Type") in LOOP_OWNERS:
                self.markers += 1
                k += 1
                continue
            if ct in CONDITIONAL and _pb(c.get("Props") or {}, "insertIfElse"):
                self.error(c, "an If/Else structure cannot live inside a Random Package on the "
                              "Pico - the package draws items independently, so the branches would "
                              "scatter. Move the condition outside the package")
                k += 1
                if k < len(kids) and is_marker(kids[k], "else"):
                    k += 1
                if k < len(kids) and is_marker(kids[k], "endif"):
                    k += 1
                continue
            if is_marker(c):
                self.error(c, "structural marker '%s' has no matching block head"
                              % _ps(c.get("Props") or {}, "text").strip())
                k += 1
                continue
            items.append(c)
            k += 1
        if not items:
            self.error(n, "random package has no enabled children - the engine would have nothing "
                          "to draw")
            return
        if mode == "shuffleAll":
            mn, mx, em = 1, len(items), "all"
        else:
            mn = _pi(p, "minCount", 1)
            mx = _pi(p, "maxCount", 10)
            if mx > len(items):
                self.flag(n, "package max count %d clamped to the %d enabled item(s)" % (mx, len(items)))
                mx = len(items)
            if mn < 0:
                mn = 0
            if mn > mx:
                mn, mx = mx, mn
            em = "pick"
        self.lines.append("RPKG|%s,%d,%d" % (em, mn, mx))
        self.count(n, "RPKG")
        for idx, c in enumerate(items):
            if idx > 0:
                self.lines.append("PKGITEM")
            before = len(self.lines)
            self.walk([c])
            if len(self.lines) == before:
                self.lines.append("# empty package item")
        self.lines.append("ENDPKG")
        self.emit_delay(n)

    def _st_parallelGroup(self, n, p):
        kids = n.get("Children") or []
        branches = []
        for c in kids:
            c["_num"] = self._numbering.get(id(c), "?")
            if c.get("IsDisabled"):
                self.disabled.append(self._title(c))
                continue
            if is_marker(c, "next"):
                self.markers += 1               # a loop-owner branch already errored; eat its Next
                continue
            branches.append(c)
        if len(branches) < 2:
            self.error(n, "a Parallel Group needs at least two enabled branches (got %d)" % len(branches))
            return
        self.lines.append("PGROUP")
        self.count(n, "PGROUP")
        for idx, c in enumerate(branches):
            if idx > 0:
                self.lines.append("PARITEM")
            self.walk([c], in_pgroup=True)
        self.lines.append("ENDPAR")
        self.emit_delay(n)

    def _st_waitForSound(self, n, p):
        thr, min_ms, to = _pi(p, "threshold", 90), _pi(p, "minDurationMs", 60), _pi(p, "timeoutMs", 20000)
        if _pb(p, "armed"):
            act = {"left": 1, "right": 2, "middle": 3}.get(_ps(p, "act", "left"), 1)
            rmin, rmax = _pair(_pi(p, "reactMin", 80), _pi(p, "reactMax", 180))
            hmin, hmax = _pair(_pi(p, "holdMin", 30), _pi(p, "holdMax", 90))
            self.emit(n, ["TRGSND|%d,%d,%d,%d,%d,%d,%d,%d" % (
                thr, min_ms, to, act, rmin, rmax, hmin, hmax)], "TRGSND")
        else:
            self.emit(n, ["WSND|%d,%d,%d" % (thr, min_ms, to)], "WSND")

    def _st_waitForLight(self, n, p):
        lo, hi, stable, to, mode = self._lux_args(p)
        if _pb(p, "armed"):
            vk = self._vk(_ps(p, "key", "E"), "waitForLight armed key", n)
            if not vk:
                return
            hmin, hmax = _pair(_pi(p, "holdMin", 30), _pi(p, "holdMax", 90))
            rmin, rmax = _pair(_pi(p, "reactMin", 80), _pi(p, "reactMax", 180))
            self.emit(n, ["WLIGHT|%d,%d,%d,%d,%d|key=%d,%d,%d|react=%d,%d" % (
                lo, hi, stable, to, mode, vk, hmin, hmax, rmin, rmax)], "WLIGHT")
        else:
            self.emit(n, ["WLIGHT|%d,%d,%d,%d,%d" % (lo, hi, stable, to, mode)], "WLIGHT")

    def _st_findImage(self, n, p):
        self.error(n, "findImage needs machine vision - it cannot run on the Pico. Convert "
                      "this step to Wait For Light (BH1750) or Wait For Sound in the app first")
        self._collect_blockers(n)                      # nested blockers must be LISTED, not hidden

    def _collect_blockers(self, node):
        """A blocked container hides its whole subtree from the walk; sweep it so every
        nested findImage/secret/clipboard step is still NAMED in the blocking report."""
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

    def _st_runExe(self, n, p):
        self._launch(n, p, shell_open=False)

    def _st_openFile(self, n, p):
        self._launch(n, p, shell_open=True)

    def _launch(self, n, p, shell_open):
        path = _ps(p, "path", "").strip()
        if not path:
            self.error(n, "no path set - the Win+R macro would type nothing")
            return
        ws = _ps(p, "windowState", "normal")
        mode = {"normal": "visible", "minimized": "min"}.get(ws)
        if mode is None:
            mode = "visible"
            if ws == "maximized":
                self.flag(n, "'maximized' cannot be expressed through the Run box - launching visible/normal")
            else:
                self.flag(n, "unknown window state '%s' - launching visible" % ws)
        if mode in ("visible", "min"):
            self.flag(n, "mode=%s types into the visible Run box and leaves RunMRU history; "
                         "use a hidden-capable state or a pinned taskbar icon for zero footprint" % mode)
        try:
            lines = winr.launch(path, _ps(p, "args", ""), mode=mode, shell_open=shell_open)
        except ValueError as exc:
            self.error(n, str(exc))
            return
        self.emit(n, lines, "openFile" if shell_open else "runExe")

    def _st_playAudio(self, n, p):
        path = _ps(p, "path", "").strip()
        if not path:
            self.error(n, "no audio path set")
            return
        mode = _ps(p, "mode", "playerMacro")
        if mode == "pcSpeaker":
            self.error(n, "pcSpeaker mode plays through the PC's own audio stack (NAudio) - "
                          "it only exists in PC mode; on the Pico use playerMacro (or the buzzer, phase 3)")
            return
        if mode == "deviceBuzzer":
            self.error(n, "deviceBuzzer needs the phase-2 app fields (freq/ms) which this app version "
                          "does not write yet - rebuild the step as playerMacro or wait for the exporter update")
            return
        if mode != "playerMacro":
            self.error(n, "unknown playAudio mode '%s'" % mode)
            return
        if _pb(p, "loop"):
            self.flag(n, "'loop until stop' is not portable - the hidden player plays the file once")
        dev = _pi(p, "outputDevice", -1)
        if dev >= 0:
            self.flag(n, "output device selection is PC-only (NAudio); the macro plays through the "
                         "system default device")
        if not path.lower().endswith((".wav", ".mp3")):
            self.flag(n, "unusual audio extension - the macro supports .wav (SoundPlayer) and "
                         ".mp3 (MediaPlayer); other formats may fail silently")
        elif path.lower().endswith(".mp3"):
            self.flag(n, "mp3 plays via a hidden MediaPlayer with no duration hold - it may stop when "
                         "the hidden host exits; prefer .wav, or a duration field (phase 3)")
        try:
            lines = winr.audio(path, seconds=0)
        except ValueError as exc:
            self.error(n, str(exc))
            return
        self.emit(n, lines, "playAudio")

    def _st_playScript(self, n, p):
        raw = _ps(p, "path", "").strip()
        if not raw:
            self.error(n, "no .amsj path set")
            return
        child_path = raw if os.path.isabs(raw) else os.path.join(os.path.dirname(self.source_path), raw)
        child_path = os.path.normpath(child_path)
        if not os.path.exists(child_path):
            self.error(n, "playScript child not found: %s - every included plan must be exported "
                          "with the bundle" % raw)
            return
        base = os.path.splitext(os.path.basename(child_path))[0]
        fname = safe_plan_filename(base)
        if fname is None:
            self.error(n, "playScript file name '%s' cannot live on the Pico drive "
                          "(ASCII, no / \\ : | %%) - rename it" % base)
            return
        canon = os.path.abspath(child_path)
        chain = self.include_stack + (os.path.abspath(self.source_path),)
        if canon in chain:
            self.error(n, "include cycle: %s eventually includes itself - break the cycle" % base)
            return
        if self.depth + 1 >= MAX_INCLUDE_DEPTH:
            self.error(n, "include depth would exceed %d at %s - the engine caps nesting at %d"
                          % (MAX_INCLUDE_DEPTH, base, MAX_INCLUDE_DEPTH))
            return
        self.children_files.append((fname, canon, chain, self.depth + 1))
        self.emit(n, ["INCLUDE|file=%s" % fname], "INCLUDE")

# ── settings / wrapper ────────────────────────────────────────────────────────
def load_settings(path):
    """AppSettings (ams-settings.json). Missing file = app defaults."""
    s = {"PlayRepeatMode": "once", "PlayRepeatTimes": 10, "PlayRepeatValue": 1,
         "PlayRepeatUnit": "minute", "MouseMoveSpeedMin": 300, "MouseMoveSpeedMax": 2000,
         "TypeKeyMinMs": 80, "TypeKeyMaxMs": 220, "KeyboardBoard": "pico"}
    if path:
        try:
            with open(path, "r", encoding="utf-8") as fh:
                data = json.load(fh)
            for k in s:
                if k in data:
                    s[k] = data[k]
        except Exception as exc:
            raise ValueError("cannot read settings '%s': %s" % (path, exc))
    # v0.9.23/24 migration, same rule as AppSettings.NormalizeSpeedDefaults
    if s["MouseMoveSpeedMin"] == 0 and s["MouseMoveSpeedMax"] == 2300:
        s["MouseMoveSpeedMin"], s["MouseMoveSpeedMax"] = 300, 2000
    return s

def number_tree(nodes, prefix=""):
    """The app's hierarchical numbering (1, 1.1, 1.1.2 ...) for error messages."""
    out = {}
    for i, n in enumerate(nodes):
        num = "%s%d" % (prefix + "." if prefix else "", i + 1)
        n["_num"] = num
        out[id(n)] = num
        out.update(number_tree(n.get("Children") or [], num))
    return out

def compile_doc(source_path, opts, depth=0, include_stack=()):
    """Compile one .amsj into a Gen (lines + errors + flags)."""
    try:
        with open(source_path, "r", encoding="utf-8") as fh:
            doc = json.load(fh)
    except Exception as exc:
        raise ValueError("cannot read '%s': %s" % (source_path, exc))
    if not isinstance(doc, dict) or doc.get("app") != APP_ID or not isinstance(doc.get("steps"), list):
        raise ValueError("'%s' is not an AMS document (expected {app: 'AMS', steps: [...]})" % source_path)
    gen = Gen(opts, source_path, depth, include_stack)
    gen._numbering = number_tree(doc["steps"])
    gen.walk(doc["steps"])
    return gen

def build(source_path, opts):
    """Full build: main plan + every playScript child (recursive). Returns
    (files: {name: text}, gen, child_gens) or raises _Blocked with all errors."""
    gen = compile_doc(source_path, opts, 0, ())
    settings = opts["settings"]
    try:
        sha = hashlib.sha256(open(source_path, "rb").read()).hexdigest()[:12]
    except Exception:
        sha = "?"
    body = list(gen.lines)
    # Play Options wrapper: once = bare, times = LOOP|N, timed = LOOPTIME|seconds
    mode = settings["PlayRepeatMode"]
    if mode == "times":
        times = settings["PlayRepeatTimes"]
        if not isinstance(times, int) or times <= 0:
            gen.errors.append("settings: PlayRepeatTimes must be a positive integer (got %r)" % (times,))
        else:
            body = ["LOOP|%d" % times] + body + ["ENDLOOP"]
    elif mode == "timed":
        unit = settings["PlayRepeatUnit"]
        sec = settings["PlayRepeatValue"] * TIME_UNIT_SEC.get(unit, 0)
        if unit not in TIME_UNIT_SEC or not isinstance(sec, int) or sec <= 0:
            gen.errors.append("settings: bad timed repeat (%r %r)" % (settings["PlayRepeatValue"], unit))
        else:
            body = ["LOOPTIME|%d" % sec] + body + ["ENDLOOP"]
    elif mode != "once":
        gen.errors.append("settings: unknown PlayRepeatMode '%s' (once|times|timed)" % mode)
    if settings.get("KeyboardBoard", "pico") == "promicro":
        gen.flags.append("settings: KeyboardBoard=promicro is bridge-mode only; on the portable plan "
                         "the Pico types every keyboard step (keyboard=Pico contract)")
        body.insert(0, "# FLAG " + gen.flags[-1])
    text = "\n".join(["PLAN|2",
                      "# generated by tools/plan_gen.py from %s" % os.path.basename(source_path),
                      "# source sha256: %s" % sha,
                      "SCREEN|%d,%d" % (opts["screen_w"], opts["screen_h"]),
                      "SPEED|%d,%d" % (settings["MouseMoveSpeedMin"], settings["MouseMoveSpeedMax"]),
                      ""] + body) + "\n"
    files = {opts["out_name"]: text}
    # children (playScript) compile after the parent; their own errors block the
    # whole bundle - a half-written set must never reach a drive.
    pending = list(gen.children_files)          # (fname, canon, chain, depth)
    seen = {os.path.abspath(source_path)}
    child_gens = []
    while pending:
        fname, canon, chain, depth = pending.pop(0)
        if canon in seen:
            continue                                        # same child referenced twice = compiled once
        seen.add(canon)
        child = compile_doc(canon, opts, depth, chain)
        child_gens.append((fname, canon, child))
        pending.extend(child.children_files)
    all_errors = list(gen.errors)
    for fname, canon, child in child_gens:
        for e in child.errors:
            all_errors.append("[%s] %s" % (fname, e))
    if all_errors:
        raise _Blocked(all_errors)
    for fname, canon, child in child_gens:
        files[fname] = "\n".join(["PLAN|2",
                                  "# include plan for %s (generated)" % os.path.basename(canon),
                                  "SCREEN|%d,%d" % (opts["screen_w"], opts["screen_h"]),
                                  "SPEED|%d,%d" % (settings["MouseMoveSpeedMin"], settings["MouseMoveSpeedMax"]),
                                  ""] + child.lines) + "\n"
    # dogfood: validate every emitted file with the REAL engine before anything ships
    for name, ftext in files.items():
        try:
            pe.parse_plan(ftext)
        except Exception as exc:
            raise _Blocked(["plan_gen BUG: %s failed the engine's own parser: %s "
                            "(please report with the source .amsj)" % (name, exc)])
    return files, gen, child_gens

def report(gen, child_gens, files, out_dir):
    print("compile ok: %s" % gen.source_path)
    for name in sorted(files):
        print("  wrote %s (%d bytes)" % (os.path.join(out_dir, name), len(files[name].encode("utf-8"))))
    total = sum(gen.counts.values())
    print("  %d emission(s): %s" % (
        total, ", ".join("%s x%d" % (k, gen.counts[k]) for k in sorted(gen.counts))))
    if gen.disabled:
        print("  %d disabled step(s) counted and skipped:" % len(gen.disabled))
        for d in gen.disabled:
            print("    - " + d)
    if gen.markers:
        print("  %d structural marker(s) consumed (Else / End If / Next)" % gen.markers)
    flags = list(gen.flags)
    for _fname, _canon, child in child_gens:
        flags.extend(child.flags)
    if flags:
        print("  %d flag(s) - honored with a caveat (never silently):" % len(flags))
        for f in flags:
            print("    # FLAG " + f)

def main(argv=None):
    ap = argparse.ArgumentParser(
        description="compile a Classroom Studio .amsj into a portable plan.txt for the Pico")
    ap.add_argument("input", help="the .amsj document")
    ap.add_argument("-o", "--out", default="plan.txt",
                    help="output plan file name (default: plan.txt in --out-dir)")
    ap.add_argument("--out-dir", default=".", help="where plan.txt and child includes are written")
    ap.add_argument("--settings", default=None,
                    help="ams-settings.json (Play Options + mouse speed + typing cadence); "
                         "defaults to the app's factory values")
    ap.add_argument("--screen", default="1920x1080",
                    help="TARGET machine resolution for SCREEN (the arm's absolute mapping); "
                         "default 1920x1080")
    ap.add_argument("--speed", default=None, help="override mouse speed range: min,max (px/s)")
    args = ap.parse_args(argv)

    try:
        sw, sh = args.screen.lower().split("x")
        screen = (int(sw), int(sh))
        assert screen[0] > 0 and screen[1] > 0
    except Exception:
        print("error: --screen must look like 1920x1080")
        return 2
    if not os.path.exists(args.input):
        print("error: input not found: %s" % args.input)
        return 2
    try:
        settings = load_settings(args.settings)
    except ValueError as exc:
        print("error: %s" % exc)
        return 2
    if args.speed:
        try:
            s0, s1 = args.speed.split(",")
            settings["MouseMoveSpeedMin"], settings["MouseMoveSpeedMax"] = int(s0), int(s1)
        except Exception:
            print("error: --speed must look like 300,2000")
            return 2

    opts = {"screen_w": screen[0], "screen_h": screen[1], "settings": settings,
            "type_key_min": settings["TypeKeyMinMs"], "type_key_max": settings["TypeKeyMaxMs"],
            "out_name": os.path.basename(args.out)}
    try:
        files, gen, child_gens = build(os.path.abspath(args.input), opts)
    except _Blocked as b:
        print("BLOCKED - the plan was NOT written. %d problem(s):" % len(b.errors))
        for e in b.errors:
            print("  x " + e)
        return 1
    except ValueError as exc:
        print("error: %s" % exc)
        return 2
    os.makedirs(args.out_dir, exist_ok=True)
    for name, text in files.items():
        with open(os.path.join(args.out_dir, name), "w", encoding="utf-8", newline="\n") as fh:
            fh.write(text)
    report(gen, child_gens, files, args.out_dir)
    return 0

if __name__ == "__main__":
    sys.exit(main())
