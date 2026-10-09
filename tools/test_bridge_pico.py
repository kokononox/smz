#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""tools/test_bridge_pico.py — v0.9.59 behavior test for the Pico brain transport.

Runs WITHOUT hardware and WITHOUT pyserial: a fake `serial` module (and a stub
`ams_serial`) is injected into sys.modules before importing bridge.py, so
PicoLink talks to a scripted fake Pico data port. CI runs this on every push.

Scenarios:
 1. brain-first connect: PING->role=brain picks PicoLink, BoardLink never built
 2. keyboard command executes on the Pico (KTEXT -> OK|KTEXT)
 3. arm command flows through the brain (MMOVE -> forwarded reply OK|MMOVE)
 4. an EVT line mid-command is queued to link.events, NOT mispaired as reply
 5. a reply split into tiny reads still parses (partial-line buffering)
 6. silence raises a timeout naming the command head (ERR|TIMEOUT|…)
 7. a non-brain port falls back to the encrypted BoardLink path
 8. HALT goes out as a bare write (abort path), close() sends HALT+BYE
 9. a busy Pico that misses the first PING reconnects without a cable cycle
"""
import os
import sys
import time
import types

HERE = os.path.dirname(os.path.abspath(__file__))
BRIDGE_DIR = os.path.normpath(os.path.join(HERE, "..", "ams-shell", "bridge"))
sys.path.insert(0, BRIDGE_DIR)

PASSED = 0


def check(label, cond):
    global PASSED
    if not cond:
        print("FAIL: " + label)
        sys.exit(1)
    PASSED += 1
    print("PASS: " + label)


class FakePort:
    """Scripted stand-in for the Pico's USB data port (or any serial device)."""

    def __init__(self, script, name="COM5"):
        self.name = name
        self.script = script          # list of (match_substr, [reply lines])
        self.written = []
        self._rx = bytearray()
        self.closed = False
        self.chunk = 10 ** 9          # lowered in scenario 5 to force split reads

    # --- pyserial surface used by PicoLink ---
    def reset_input_buffer(self):
        pass

    def reset_output_buffer(self):
        pass

    def write(self, data):
        data = bytes(data)
        self.written.append(data)
        line = data.decode("utf-8", "replace").strip()
        for want, replies in self.script:
            if want in line:
                for r in replies:
                    self._rx += r.encode("utf-8") + b"\n"
        return len(data)

    def flush(self):
        pass

    def read(self, n=64):
        if not self._rx:
            time.sleep(0.005)
            return b""
        n = min(n, self.chunk, len(self._rx))
        out = bytes(self._rx[:n])
        del self._rx[:n]
        return out

    def close(self):
        self.closed = True


class RetryPingPort(FakePort):
    """Ignore the first PING, then identify normally on the retry."""
    def __init__(self):
        super().__init__([])
        self.pings = 0

    def write(self, data):
        data = bytes(data)
        self.written.append(data)
        if data.strip() == b"PING":
            self.pings += 1
            if self.pings >= 2:
                self._rx += BRAIN_PONG.encode("utf-8") + b"\n"
        return len(data)


def fake_serial_module(port):
    mod = types.ModuleType("serial")
    mod.Serial = lambda device, baud, **kw: port
    return mod


# stub ams_serial: records every BoardLink construction/connection attempt
BOARDLINK_CALLS = {"built": 0, "connected": 0}


def fake_ams_serial_module():
    mod = types.ModuleType("ams_serial")

    class BoardError(Exception):
        pass

    class BoardLink:
        def __init__(self, port="AUTO", **kw):
            BOARDLINK_CALLS["built"] += 1
            self.port = port
            self.fw_ver = "1.6"
            self.events = []

        def connect(self):
            BOARDLINK_CALLS["connected"] += 1
            return self.port

        def close(self):
            pass

    mod.BoardError = BoardError
    mod.BoardLink = BoardLink
    return mod


def fresh_bridge(port, boardlink_stub=True):
    """Import bridge.py with fake serial + stub ams_serial around `port`."""
    sys.modules.pop("bridge", None)
    sys.modules["serial"] = fake_serial_module(port)
    sys.modules["serial.tools"] = types.ModuleType("serial.tools")
    if boardlink_stub:
        sys.modules["ams_serial"] = fake_ams_serial_module()
        sys.modules["ams_crypto"] = types.ModuleType("ams_crypto")
    import bridge
    return bridge


BRAIN_PONG = "OK|PONG|pico-light 0.9.58|role=brain+keyboard+light|arm=promicro"

# ── scenario 1-4: full brain session ─────────────────────────────────
pico = FakePort([
    ("PING", [BRAIN_PONG]),
    ("KTEXT", ["OK|KTEXT"]),
    ("MMOVE", ["OK|MMOVE"]),
    ("WSND", ["EVT|TRG|react=123", "OK|WSND|fired"]),   # EVT BEFORE the reply
])
br = fresh_bridge(pico)
link, dev = br.open_link("COM5")

check("1a: brain-first connect returns a PicoLink", type(link).__name__ == "PicoLink")
check("1b: connected device is the probed port", dev == "COM5")
check("1c: identity carries role=brain for the app LEDs",
      "role=brain" in (link.fw_ver or "") and "pico-light" in (link.fw_ver or ""))
check("1d: encrypted BoardLink was never constructed for a brain port",
      BOARDLINK_CALLS["built"] == 0)
check("1e: role metadata for the connected event", getattr(link, "role", None) == "brain")

reply = link.command("KTEXT|0,0,E", timeout=1.0)
check("2: keyboard step executes on the Pico itself", reply == "OK|KTEXT")

reply = link.command("MMOVE|100,200,abs,1", timeout=1.0)
check("3: arm command flows PC->Pico->arm and its reply returns", reply == "OK|MMOVE")

reply = link.command("WSND|500,100,2000", timeout=2.0)
check("4a: real reply wins over an interleaved EVT", reply == "OK|WSND|fired")
check("4b: the armed EVT was queued to events, not lost, not mispaired",
      link.events == ["EVT|TRG|react=123"])

# ── scenario 5: split reads ──────────────────────────────────────────
pico2 = FakePort([("PING", [BRAIN_PONG]), ("LCAL", ["OK|LCAL|min=55|max=59|avg=57"])])
pico2.chunk = 3            # every read() returns at most 3 bytes
br2 = fresh_bridge(pico2)
link2, _ = br2.open_link("COM5")
reply = link2.command("LCAL|3000", timeout=2.0)
check("5: reply split into 3-byte reads still parses", reply == "OK|LCAL|min=55|max=59|avg=57")

# ── scenario 6: silence -> clean timeout ──────────────────────────────
pico3 = FakePort([("PING", [BRAIN_PONG])])               # answers PING only
br3 = fresh_bridge(pico3)
link3, _ = br3.open_link("COM5")
try:
    link3.command("WLUX|100,200,1000,500,0", timeout=0.3)
    check("6: silent command must time out", False)
except Exception as e:
    check("6: silence raises a timeout naming the command head",
          "ERR|TIMEOUT|WLUX" in str(e))

# ── scenario 7: plain arm board -> encrypted fallback ─────────────────
arm = FakePort([("PING", ["OK|PONG|ams-board 1.6"])])    # no role=brain
br4 = fresh_bridge(arm)
test_key = os.path.join(BRIDGE_DIR, "ams_key.json")
with open(test_key, "w", encoding="utf-8") as fh:
    fh.write("{}")
try:
    link4, dev4 = br4.open_link("COM17")
finally:
    os.unlink(test_key)
check("7a: non-brain port falls back to BoardLink", type(link4).__name__ == "BoardLink")
check("7b: fallback connected to the same port", dev4 == "COM17")
check("7c: fallback really used the encrypted path",
      BOARDLINK_CALLS["built"] >= 1 and BOARDLINK_CALLS["connected"] >= 1)

# ── scenario 8: abort/close writes ───────────────────────────────────
link._send("HALT")
check("8a: abort path writes a bare HALT line", pico.written[-1] == b"HALT\n")
link.close()
check("8b: close sends HALT then BYE and closes the port",
      pico.written[-2] == b"HALT\n" and pico.written[-1] == b"BYE\n" and pico.closed)

# ── scenario 9: missed first PING must not require cable replug ──────
busy_pico = RetryPingPort()
br5 = fresh_bridge(busy_pico)
link5, dev5 = br5.open_link("COM31")
check("9a: connect retries an idempotent PING on the same open port",
      type(link5).__name__ == "PicoLink" and busy_pico.pings == 2)
check("9b: retry keeps the same COM port and needs no close/reopen cycle",
      dev5 == "COM31" and not busy_pico.closed)
link5.close()

# ── scenario 10-13: the clock stamp after a power cut ────────────────
# A board that lost power keeps the schedule from the flashed program but owns no
# wall anchor, so no window can be armed and its own wake can never fire.  The app
# is what runs on the host afterwards, so the stamp has to come from here -- and
# only to a board that reports it has none.
NATIVE_PONG = ("OK|PONG|combined-pico-guard-executor|native=abvm|abi=3|format=13|"
               "hid=on|uart=on|arm-ready=1|arm-usb=3|arm-ver=2.8.3-S5|profiles=9|"
               "buzzer=legacy-calibration-gp6|role=brain")
WAKE_NO_CLOCK = ("OK|WAKE|armed=0|manual=0|dry=0|synced=0|schedule=1|target=18:30|"
                 "due-ms=0|attempts=0|phase=0|recovery=0|host=UNKNOWN|pico-usb=0|"
                 "pico-rw=0|recovery-reason=none")


def stamps(port):
    return [w.decode().strip() for w in port.written if w.startswith(b"TIME!")]


# ── scenario 10: a clockless native board gets its clock back ────────
cold = FakePort([("PING", [NATIVE_PONG]), ("WAKE?", [WAKE_NO_CLOCK])])
before = time.strftime("TIME!|%H:%M")
br6 = fresh_bridge(cold)
link6, _ = br6.open_link("COM7")
after = time.strftime("TIME!|%H:%M")
written = stamps(cold)
check("10a: a board with no wall clock is stamped exactly once",
      len(written) == 1 and written[0] in (before, after))
check("10b: the stamp follows the board's own verdict, not a guess",
      cold.written.index(b"WAKE?\n") < cold.written.index(written[0].encode() + b"\n"))
link6.close()

# ── scenario 11: a board that still has its anchor is left alone ─────
warm = FakePort([("PING", [NATIVE_PONG]),
                 ("WAKE?", [WAKE_NO_CLOCK.replace("synced=0", "synced=1")])])
br7 = fresh_bridge(warm)
link7, _ = br7.open_link("COM7")
# Re-arming a healthy board would move a real deadline, and a stamp taken inside
# the lead time falls back to "one minute from now".
check("11: a board that still holds its clock is never stamped", stamps(warm) == [])
link7.close()

# ── scenario 12: no native clock capability, no probe ────────────────
light = FakePort([("PING", [BRAIN_PONG])])
br8 = fresh_bridge(light)
link8, _ = br8.open_link("COM5")
check("12: a board without the native clock capability is never asked",
      not [w for w in light.written if w.startswith(b"WAKE?") or w.startswith(b"TIME!")])
link8.close()

# ── scenario 13: firmware that predates the probe ────────────────────
legacy = FakePort([("PING", [NATIVE_PONG]),
                   ("WAKE?", ["ERR|COMMAND|unknown=WAKE?"]),
                   ("STATUS", ["OK|STATUS|state=idle"])])
br9 = fresh_bridge(legacy)
link9, _ = br9.open_link("COM7")
check("13a: firmware that predates WAKE? is not stamped", stamps(legacy) == [])
# The refusal must be consumed by the probe: a stale ERR left in the buffer would
# be mispaired with the next command the app sends.
check("13b: the probe leaves no stale line behind",
      link9.command("STATUS", timeout=1.0) == "OK|STATUS|state=idle")
link9.close()

print("=== bridge pico transport: %d checks passed, 0 failed ===" % PASSED)
