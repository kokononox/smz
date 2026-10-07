#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""sim_plan_export.py - sandbox proof for the v0.9.65 PlanExporter (phase 2).

For every fixture tree, the twin (tools/plan_export_twin.py - the Python twin of
Services/PlanExporter.cs) compiles a plan, and then the REAL gen-1 engine that lives
on the user's drive (firmware/code64b/plan_engine.py) must both PARSE it and EXECUTE
it correctly against a recording fake ctx. Blocking rules are asserted by name.

Run:  python sim/sim_plan_export.py        (from the repo root - paths resolve by __file__)
Exit: 0 = all green, 1 = failures listed.
"""
import os
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))
for _cand in (_HERE, os.path.join(_HERE, "tools"), os.path.dirname(_HERE),
              os.path.join(os.path.dirname(_HERE), "tools"),
              os.path.join(os.path.dirname(_HERE), "firmware", "code64b"),
              os.path.join(os.path.dirname(os.path.dirname(_HERE)), "firmware", "code64b"),
              os.getcwd()):
    for _mod in ("plan_engine.py", "plan_export_twin.py"):
        if os.path.exists(os.path.join(_cand, _mod)):
            sys.path.insert(0, _cand)

import plan_engine as pe          # noqa: E402  the REAL gen-1 engine (the one on the drive)
import plan_export_twin as twin   # noqa: E402  the Python twin of PlanExporter.cs

PASS = 0
FAIL = 0


def check(cond, msg):
    global PASS, FAIL
    if cond:
        PASS += 1
        print("PASS: " + msg)
    else:
        FAIL += 1
        print("FAIL: " + msg)


def step(t, props=None, name="", delay=0, delaymax=0, disabled=False, children=None):
    return {"Type": t, "Props": props or {}, "Name": name, "Delay": delay,
            "DelayMax": delaymax, "IsDisabled": disabled, "Children": children or []}


def build(steps, settings=None, w=1920, h=1080):
    s = dict(twin.DEFAULT_SETTINGS)
    if settings:
        s.update(settings)
    return twin.build(steps, s, w, h, "fixture.amsj", "TESTPC", "2026-09-10 03:00")


class Ctx:
    """Recording ctx for the real engine - the _PlanCtx contract of code64b."""
    def __init__(self):
        self.screen_w, self.screen_h = 1920, 1080
        self.speed_min, self.speed_max = 0, 2000
        self.moves = []
        self.clicks = []
        self.texts = []
        self.combos = []
        self.keys = []
        self.sleeps = []
        self.light_calls = []
        self.light_result = True
        self.logs = []
        self._pos = None
        self.t = 0.0

    def get_mouse_pos(self):
        return self._pos

    def set_mouse_pos(self, x, y):
        self._pos = (x, y)

    def now(self):
        return self.t

    def sleep_ms(self, ms):
        self.sleeps.append(ms)
        self.t += ms / 1000.0
        return True

    def mmove(self, x, y):
        self.moves.append((x, y))

    def mclick(self, btn, n, hmin, hmax):
        self.clicks.append((btn, n, hmin, hmax))

    def ktext(self, hmin, hmax, text):
        self.texts.append((hmin, hmax, text))

    def kcombo(self, vk):
        self.combos.append(vk)

    def wait_light(self, lo, hi, stable, to, mode):
        self.light_calls.append((lo, hi, stable, to, mode))
        return self.light_result

    def key(self, vk, hold_ms):
        self.keys.append((vk, hold_ms))

    def log(self, msg):
        self.logs.append(msg)


def run(text, ctx=None):
    ops = pe.parse_plan(text)          # the engine's OWN parser - the dogfood gate
    ctx = ctx or Ctx()
    pe.run_plan(ops, ctx)
    return ctx


# ── 1) header + empty body ─────────────────────────────────────────────────────
text, gen = build([])
check(text.startswith("PLAN|1\n"), "empty plan starts with PLAN|1")
check("SCREEN|1920,1080\n" in text and "SPEED|300,2000\n" in text,
      "header carries SCREEN + SPEED from settings")
check(pe.parse_plan(text)[0] == ("PLAN", {"v": 1}), "empty plan parses on the real engine")

# ── 2) randomMousePosition: exact golden line + real execution ─────────────────
text, gen = build([step("randomMousePosition",
                        {"x": 1301, "y": 0, "w": 378, "h": 1049,
                         "pauseBeforeMin": 120, "pauseBeforeMax": 450,
                         "pauseAfterMin": 150, "pauseAfterMax": 600,
                         "midPauseChance": 12, "midPauseMin": 100, "midPauseMax": 500,
                         "idleEveryMin": 5, "idleEveryMax": 12, "idlePauseMin": 800, "idlePauseMax": 3000,
                         "overshootChance": 25, "curveMinPct": 22, "curveMaxPct": 155,
                         "moveTimeMin": 0, "moveTimeMax": 333}, delay=55, delaymax=55)])
line = [l for l in text.split("\n") if l.startswith("RMOUSE|")][0]
check(line == "RMOUSE|region=1301,0,378,1049|before=120,450|after=150,600|curve=22,155|"
              "mid=12:100,500|over=25|mt=0,333|idle=5,12:800,3000",
      "randomMousePosition emits the exact golden RMOUSE line (got: " + line + ")")
check(text.rstrip().endswith("DELAY|55,55"), "per-step delay-after lands after the op")
ctx = run(text)
check(len(ctx.moves) > 30, "real engine streams a dense path (%d pts)" % len(ctx.moves))
check(ctx.moves[-1][0] in range(1301, 1301 + 378) and ctx.moves[-1][1] in range(0, 1049),
      "path ends inside the region (got %d,%d)" % ctx.moves[-1])
gaps = [abs(ctx.moves[i][0] - ctx.moves[i - 1][0]) + abs(ctx.moves[i][1] - ctx.moves[i - 1][1])
        for i in range(1, len(ctx.moves))]
check(max(gaps) <= 6, "gliding contract: max micro-step %d px (<= 6)" % max(gaps))

# ── 3) mouseMove -> random point in the selected region ────────────────────────
text, gen = build([step("mouseMove", {"x": 700, "y": 400, "w": 180, "h": 120, "human": True})])
line = [l for l in text.split("\n") if l.startswith("RMOUSE|")][0]
check(line == "RMOUSE|region=700,400,180,120|before=60,220|after=80,280|curve=20,40|"
              "mid=6:80,250|over=12|idle=1,1:0,0",
      "mouseMove compiles to the selected region with idle explicitly off (got: " + line + ")")
ctx = run(text)
check(ctx.moves[-1][0] in range(700, 880) and ctx.moves[-1][1] in range(400, 520),
      "mouseMove lands at a fresh point inside the selected region on the real engine")

text, gen = build([step("mouseMove", {"x": 5, "y": 6, "human": False})])
check(any("human=false cannot be preserved" in f for f in gen.flags), "human=false is flagged, not silent")

# idle breaks OFF when the raw idlePauseMax is 0 (read BEFORE the min/max swap - the
# off-intent must survive even when only the max is zeroed)
text, gen = build([step("randomMousePosition",
                        {"x": 10, "y": 10, "w": 50, "h": 50, "midPauseChance": 0, "idlePauseMax": 0})])
line = [l for l in text.split("\n") if l.startswith("RMOUSE|")][0]
check("mid=0:80,250" in line and "idle=1,1:0,0" in line,
      "explicit-off beats the engine's built-in 12%/1000-5000 defaults (got: " + line + ")")

# ── 4) CLICK ───────────────────────────────────────────────────────────────────
text, gen = build([step("mouseClick", {"button": "right", "action": "double",
                                       "holdMin": 90, "holdMax": 30})])   # swapped on purpose
line = [l for l in text.split("\n") if l.startswith("CLICK|")][0]
check(line == "CLICK|btn=right|n=2|hold=30,90", "click line with swap-normalized hold (got: " + line + ")")
ctx = run(text)
check(ctx.clicks == [("right", 2, 30, 90)], "real engine replays the click")

# ── 5) TYPE ────────────────────────────────────────────────────────────────────
text, gen = build([step("typeText", {"text": "Hello 50%|world\nline2", "hmin": 90, "hmax": 180})])
line = [l for l in text.split("\n") if l.startswith("TYPE|")][0]
check(line == "TYPE|text=Hello 50%25%7Cworld%0Aline2|h=90,180",
      "TYPE %-encoding (got: " + line + ")")
ctx = run(text)
typed = "".join(t[2] for t in ctx.texts)
check("Hello 50%|world" in typed and ctx.combos == [13],
      f"engine decodes %25/%7C and presses Enter between lines (typed {typed!r}, combos {ctx.combos!r})")
check(all(t[0] == 90 and t[1] == 180 for t in ctx.texts), "typing cadence range reaches the engine")

text, gen = build([step("typeText", {"text": "hi there you", "wmin": 100, "wmax": 180, "wordPauseChance": 100})])
line = [l for l in text.split("\n") if l.startswith("TYPE|")][0]
check(line == "TYPE|text=hi there you|h=80,220|w=100,180|wp=100",
      "word pauses + settings cadence defaults (got: " + line + ")")

# ── 6) blocking rules (the 23 x port matrix) ──────────────────────────────────
BLOCKED = {"findImage": "machine vision", "waitForSound": "no WSND op",
           "keystroke": "no KEY op", "keyDown": "no KDOWN op", "keyUp": "no KUP op",
           "mouseScroll": "no WHEEL op", "label": "LABEL/GOTO", "gotoLabel": "LABEL/GOTO",
           "rawCommand": "gen-2", "randomPackage": "RPKG", "parallelGroup": "PGROUP",
           "playAudio": "no op", "playScript": "INCLUDE", "runExe": "Win+R", "openFile": "Win+R"}
for btype, needle in BLOCKED.items():
    try:
        build([step(btype, {"text": "x"} if btype == "typeText" else {})])
        check(False, "%s must be blocked on PLAN|1" % btype)
    except twin.PlanBlocked as b:
        joined = " ".join(b.errors)
        check(btype in joined and needle in joined,
              "%s blocked with a named reason (%r contains %r)" % (btype, needle, needle))

try:
    build([step("typeText", {"text": "سلام"})])
    check(False, "non-ASCII typeText must be blocked")
except twin.PlanBlocked as b:
    check("non-ASCII" in " ".join(b.errors), "non-ASCII typing blocked with the ASCII rule")
try:
    build([step("typeText", {"text": "x", "secret": True})])
    check(False, "secret typeText must be blocked")
except twin.PlanBlocked as b:
    check("clipboard" in " ".join(b.errors), "secret typing blocked (no PC clipboard on the Pico)")
try:
    build([step("typeText", {"text": "x", "mode": "clipboard"})])
    check(False, "clipboard typeText must be blocked")
except twin.PlanBlocked as b:
    check("clipboard mode" in " ".join(b.errors), "clipboard mode blocked")

# If/Else heads: blocked, markers consumed without stray-marker cascade
loop_if = step("waitForLight", {"insertIfElse": True}, children=[step("delay", {"minMs": 5, "maxMs": 5})])
els = step("comment", {"text": "Else"}, children=[step("delay", {"minMs": 5, "maxMs": 5})])
endif = step("comment", {"text": "End If"})
try:
    build([loop_if, els, endif])
    check(False, "insertIfElse must be blocked on PLAN|1")
except twin.PlanBlocked as b:
    check(len(b.errors) == 1 and "If/Else needs the gen-2 plan engine" in b.errors[0],
          "If/Else blocked once; Else/End If markers consumed without cascade (got %d errors)" % len(b.errors))

# findImage nested blockers are still NAMED
fi = step("findImage", {"insertIfElse": True},
          children=[step("findImage"), step("typeText", {"text": "x", "secret": True})])
try:
    build([fi, step("comment", {"text": "Else"}), step("comment", {"text": "End If"})])
    check(False, "findImage must be blocked")
except twin.PlanBlocked as b:
    check(len(b.errors) == 3, "nested findImage + secret typing are NAMED inside a blocked head (%d errors)" % len(b.errors))

# stray marker
stray = step("comment", {"text": "Next"})
try:
    build([stray])
    check(False, "a stray Next marker must be an error")
except twin.PlanBlocked as b:
    check("no matching block head" in " ".join(b.errors), "stray marker error")

# ── 7) forLoop + Play Options wrapper ─────────────────────────────────────────
text, gen = build([step("forLoop", {"mode": "count", "count": 2},
                        children=[step("mouseClick", {"button": "left", "action": "single"})]),
                   step("comment", {"text": "Next"})])
check("LOOP|2\n" in text and text.count("ENDLOOP") == 1, "forLoop emits LOOP|2 ... ENDLOOP")
ctx = run(text)
check(ctx.clicks == [("left", 1, 0, 0), ("left", 1, 0, 0)], "real engine runs the loop body twice")

text, gen = build([step("forLoop", {"mode": "time", "timeValue": 5, "timeUnit": "minute"},
                        children=[step("delay", {"minMs": 10, "maxMs": 10})])])
check("LOOPTIME|300" in text, "forLoop time 5 minute -> LOOPTIME|300")

text, gen = build([step("forLoop", {"mode": "infinite"}, children=[step("delay", {"minMs": 1, "maxMs": 1})])])
check("LOOP|0" in text, "infinite loop -> LOOP|0")

text, gen = build([step("mouseClick", {})], settings={"PlayRepeatMode": "times", "PlayRepeatTimes": 3})
lines = [l for l in text.split("\n") if l and not l.startswith("#")]
check(lines[3] == "LOOP|3" and lines[-1] == "ENDLOOP", "Play Options 'times 3' wraps the whole body")
ctx = run(text)
check(len(ctx.clicks) == 3, "the wrapper really runs 3 passes on the real engine")

text, gen = build([step("mouseClick", {})], settings={"PlayRepeatMode": "timed", "PlayRepeatValue": 1,
                                                      "PlayRepeatUnit": "minute"})
check("LOOPTIME|60" in text, "Play Options 'timed 1 minute' -> LOOPTIME|60")

# ── 8) waitForLight plain + armed, executed on the real engine ────────────────
text, gen = build([step("waitForLight", {"luxCenter": 1250, "luxTolerance": 50, "stableSec": 0.5,
                                         "timeoutMs": 9000, "sampleMode": "lowres"})])
line = [l for l in text.split("\n") if l.startswith("WLIGHT|")][0]
check(line == "WLIGHT|1200,1300,500,9000,1", "plain WLIGHT (got: " + line + ")")
ctx = run(text)
check(ctx.light_calls == [(1200, 1300, 500, 9000, 1)] and not ctx.keys,
      "plain WLIGHT waits and presses nothing")

text, gen = build([step("waitForLight", {"luxCenter": 1000, "luxTolerance": 100, "stableSec": 2,
                                         "timeoutMs": 20000, "sampleMode": "hires", "armed": True,
                                         "key": "F4", "holdMin": 30, "holdMax": 90,
                                         "reactMin": 80, "reactMax": 180})])
line = [l for l in text.split("\n") if l.startswith("WLIGHT|")][0]
check(line == "WLIGHT|900,1100,2000,20000,0|key=115,30,90|react=80,180",
      "armed WLIGHT with key + react (got: " + line + ")")
ctx = run(text)
check(len(ctx.keys) == 1 and ctx.keys[0][0] == 115,
      "armed WLIGHT presses the key on match (vk 115 = F4)")

# ── 9) disabled steps, comments, keyboard-board flag ──────────────────────────
text, gen = build([step("mouseClick", {}, disabled=True), step("comment", {"text": "hello note"})])
check("CLICK|" not in text and "# hello note" in text and len(gen.disabled) == 1,
      "disabled steps are counted and skipped; comments become # lines")

text, gen = build([step("typeText", {"text": "abc"})], settings={"KeyboardBoard": "promicro"})
check(any("KeyboardBoard=promicro" in f for f in gen.flags) and "# FLAG" in text,
      "KeyboardBoard=promicro is a documented FLAG, not silent")

# ── 10) delay step + delay-after on a container ───────────────────────────────
text, gen = build([step("delay", {"minMs": 500, "maxMs": 100})])   # swapped on purpose
lines = [l for l in text.split("\n") if l.startswith("DELAY|")]
check(lines == ["DELAY|100,500"], "delay step is swap-normalized (got %r)" % lines)

text, gen = build([step("forLoop", {"mode": "count", "count": 1},
                        children=[step("delay", {"minMs": 5, "maxMs": 5})], delay=250)],
                  )
body = [l for l in text.split("\n") if l and not l.startswith("#")]
check(body[-1] == "DELAY|250" and body[-2] == "ENDLOOP",
      "a container's delay-after lands AFTER its ENDLOOP (app semantics)")

print()
print("=== sim_plan_export: %d passed, %d failed ===" % (PASS, FAIL))
sys.exit(1 if FAIL else 0)
