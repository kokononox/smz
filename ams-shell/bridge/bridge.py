#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""
bridge.py v2 — پل stdio بین پوسته WPF و استک پایتون AMS (مسیر B — بخش ۱۳.۱۰ سند طراحی)

تفاوت v2 (۰.۶.۲): نخ کارگر جداگانه برای اجرای فرمان‌ها + عملیات فوری «abort».
پیش از این، حلقهٔ stdin هنگام فرمان‌های طولانی (مثل WSND با پنجرهٔ ۲۰ ثانیه‌ای)
قفل می‌شد و HALTِ دکمهٔ Stop نمی‌توانست به برد برسد ← اسکریپت می‌ایستاد ولی
dokme Run دوباره فعال نمی‌شد.

پروتکل: JSON خط‌به‌خط روی stdin/stdout
  → {"op":"connect","port":"COM5"}      ← {"event":"connected","port":"COM5","fw":"1.6"}
  → {"op":"send","cmd":"MCLICK|left,1"} ← {"event":"reply","reply":"OK|MCLICK"}
  → {"op":"abort"}                      ← {"event":"abort_sent"}   (فوری — صف را دور می‌زند)
  → {"op":"send_path","pts":"x,y;x,y;…","dlys":"d;d;…"}  ← {"event":"reply","reply":"OK|PATH,n"}
      v0.9.2 — مسیر متراکم موس در یک عملیات واحد: نوشتن بدون انتظارِ پاسخ هر نقطه
      (تخلیهٔ پاسخ‌ها با PING هر ۱۲ نقطه) تا حرکت با کادانس واقعی دست (~۵ms / ~۳px) نرم باشد.
      فرمانِ در حال اجرا سپس با           ← {"event":"aborted","reply":"ERR|WSND|aborted"}
      رویداد aborted (نه reply) تمام می‌شود تا جفت‌کردن پاسخ‌ها سمت WPF به‌هم نریزد.
  → {"op":"disconnect"}                  ← {"event":"disconnected"}  (HALT + BYE — §۱۱.۳)
  → {"op":"ping_bridge"}                 ← {"event":"pong"}
رویدادهای EVT برد به‌صورت خودکار:          ← {"event":"evt","line":"EVT|..."}
خطاها:                                     ← {"event":"error","op":"...","message":"..."}

نکتهٔ هم‌زمانی: abort فقط روی پورت «می‌نویسد» (link._send) و هرگز نمی‌خواند —
خواندن فقط در نخ کارگر است (ams_serial قفل داخلی ندارد؛ دو خوانندهٔ هم‌زمان
روی یک پورت = دزدیده‌شدن پاسخ‌ها). نوشتن هم‌زمان حین خواندنِ مسدود، مسیر امن
pyserial است.

v0.9.59 — انتخاب خودکار لینک در connect: اگر پورت به PING با role=brain/pico-light
  جواب داد (پیکو)، لینک متن‌باز PicoLink بدون نیاز به ams_key.json — کیبورد و نور
  همان‌جا روی پیکو و فرمان‌های بازو روی UART به پرو میکرو پاس داده می‌شوند؛
  وگرنه همان BoardLink رمزشده‌ی همیشگی برای اتصال مستقیم به پرو میکرو.
اجرا:
  python bridge.py --pydir "C:\Users\wasteland\Documents\ams\pc"
  (--pydir = پوشه‌ای که ams_serial.py و ams_crypto.py در آن است)
"""
import argparse
import json
import os
import queue
import sys
import threading
import time

PRINT_LOCK = threading.Lock()

# v0.9.60 — never-die emit: force UTF-8 on the stdio trio regardless of the Windows
# console code page. Before this, REPORTING an error that contained non-cp1252 text
# (Persian messages, Windows error strings) raised UnicodeEncodeError and killed the
# whole bridge, hiding the real error behind a generic "connect failed: timed out".
for _stream in (sys.stdout, sys.stderr, sys.stdin):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass


def emit(obj):
    try:
        line = json.dumps(obj, ensure_ascii=False)
    except Exception:
        line = json.dumps(obj, ensure_ascii=True)
    with PRINT_LOCK:
        try:
            sys.stdout.write(line + "\n")
        except UnicodeEncodeError:
            sys.stdout.write(json.dumps(obj, ensure_ascii=True) + "\n")
        except Exception:
            return
        try:
            sys.stdout.flush()
        except Exception:
            pass


def detect_board_port():
    """v0.9.57 — brain-first auto-detect: probes every port with PING, prefers
    the one answering role=brain/pico-light (the Pico DATA port), ignores console
    port (never says PONG), includes (0x2E8A, 0x0005), falls back to old behaviour."""
    try:
        from serial.tools import list_ports
        import serial
    except Exception:
        return None
    KNOWN = {(0x1A86, 0x7523), (0x1A86, 0x5523), (0x0403, 0x6001), (0x10C4, 0xEA60),
             (0x2341, 0x0043), (0x2341, 0x0001), (0x2A03, 0x0043), (0x2E8A, 0x0005)}
    KEYS = ("ch340", "ch341", "usb-serial", "usb serial", "arduino", "cp210", "ftdi", "uart")
    def score(p):
        s = 0
        if (p.vid, p.pid) in KNOWN: s += 100
        d = ((p.description or "") + " " + (p.manufacturer or "")).lower()
        if any(k in d for k in KEYS): s += 50
        return s
    cands = sorted(list_ports.comports(), key=score, reverse=True)
    fallback = None
    for p in cands:
        try:
            ser = serial.Serial(p.device, 115200, timeout=0.4, write_timeout=0.4)
            try:
                ser.reset_input_buffer(); ser.reset_output_buffer()
                ser.write(b"PING\n"); ser.flush()
                t0 = time.monotonic(); buf = b""
                while time.monotonic() - t0 < 0.8:
                    buf += ser.read(64)
                    if b"role=brain" in buf or b"pico-light" in buf:
                        return p.device      # Pico brain found — use it even if the arm is attached
                    if (b"PONG" in buf or b"HELLO" in buf or b"OK" in buf) and fallback is None:
                        # Remember a direct/legacy board, but keep scanning: the Pico brain
                        # may be on the next COM port and never needs ams_key.json.
                        fallback = p.device
                        break
                    if not buf or len(buf) < 1:
                        time.sleep(0.02)
            finally:
                ser.close()
        except Exception:
            continue
    return fallback or (cands[0].device if cands and score(cands[0]) >= 50 else None)


class PicoError(Exception):
    pass


class PicoLink:
    """v0.9.59 — لینک متن‌باز با مغز پیکو (فرم‌ور pico-light روی پورت data).

    پیکو همان‌جا کیبورد HID و سنسور نور را اجرا می‌کند و فرمان‌های بازو
    (MMOVE/WSND/…) را روی UART به پرو میکرو پاس می‌دهد؛ پس اپ دیگر مستقیم
    با برد حرف نمی‌زند. همان رابط BoardLink (connect/command/_send/close/events)
    را پیاده می‌کند تا حلقهٔ worker فرقی بین دو لینک نبیند. بدون رمزنگاری و
    بدون نیاز به ams_key.json — کانال PC↔Pico متن‌باز است؛ مسیر رمزشده فقط
    برای اتصال مستقیم به پرو میکرو (BoardLink) باقی می‌ماند.
    """

    def __init__(self, port="AUTO", baud=115200, settle_s=1.0):
        self.port = port
        self.baud = baud
        self.settle_s = settle_s
        self.ser = None
        self.fw_ver = None
        self.role = "brain"        # در رویداد connected به اپ می‌رسد
        self.events = []           # خطوط EVT که وسط پاسخ‌ها رسیدند
        self.tx = 0
        self.rx = 0
        self._rxbuf = bytearray()

    def connect(self):
        import serial
        self.ser = serial.Serial(self.port, self.baud, timeout=0.2, write_timeout=2)
        time.sleep(self.settle_s)   # پورت data پیکو با باز شدن، برد را ریست نمی‌کند
        self._rxbuf.clear()
        try:
            self.ser.reset_input_buffer()
        except Exception:
            pass
        # Build 119: opening the Pico data CDC while Guard is busy can miss the
        # first PING even though the port is healthy.  Retrying the idempotent
        # probe avoids the old "unplug/replug the cable" recovery.  Do not
        # reset the input queue between attempts: EVT traffic and a late PONG
        # are both valid evidence from the same live board.
        pong = None
        last_error = None
        for attempt in range(4):
            try:
                pong = self.command("PING", timeout=2.0)
                break
            except PicoError as exc:
                last_error = exc
                if "ERR|TIMEOUT|PING" not in str(exc) or attempt == 3:
                    break
                time.sleep(0.12)
        if pong is None:
            try:
                self.ser.close()
            except Exception:
                pass
            self.ser = None
            raise last_error or PicoError("ERR|TIMEOUT|PING")
        if "role=brain" not in pong and "pico-light" not in pong:
            try:
                self.ser.close()
            except Exception:
                pass
            self.ser = None
            raise PicoError("این پورت مغز پیکو نیست: " + pong[:60])
        # "OK|PONG|pico-light x|role=brain…|arm=promicro" ← هویت برای LEDهای اپ
        self.fw_ver = pong[len("OK|PONG|"):] if pong.startswith("OK|PONG|") else pong
        return self.port

    def command(self, cmd, timeout=5.0):
        if self.ser is None:
            raise PicoError("not connected")
        self._send(cmd)
        deadline = time.monotonic() + timeout
        # v0.9.60e - never mis-pair a stale reply with this command: a write-only
        # abort HALT answer or a late PONG used to be returned as the NEXT command's
        # reply (hardware log: SETRES <- OK|HALT / OK|PONG). Skip OK|X / ERR|?|X
        # whose X is not this command's head (PING's answer is PONG).
        expect = cmd.split("|")[0]
        if expect in ("KBDPICO", "KBDARM") and "|" in cmd:
            expect = cmd.split("|", 2)[1]
        want = "PONG" if expect == "PING" else expect
        retried = False                  # v0.9.60g - one retry on ERR|UNKNOWN (UART glitch)
        while True:
            line = self._read_line(max(0.05, deadline - time.monotonic()))
            if line is None:
                raise PicoError("ERR|TIMEOUT|" + cmd.split("|")[0])
            if line.startswith("EVT|"):
                self.events.append(line)   # رویداد مسلح، جایگزین پاسخ نمی‌شود
                continue
            # Native firmware reports an unsupported command with the complete
            # request after "unknown=".  Its third pipe field is therefore
            # "unknown=<head>", not <head>; return it to the caller instead of
            # mistaking it for a stale error and waiting until disconnect.
            if line == "ERR|COMMAND|unknown=" + cmd:
                return line
            parts = line.split("|")
            if len(parts) >= 2 and parts[0] == "OK" and parts[1] and parts[1] != want:
                continue                 # v0.9.60e - stale OK of an older command
            if len(parts) >= 3 and parts[0] == "ERR" and parts[2] and parts[2] != expect:
                continue                 # v0.9.60e - stale ERR of an older command
            if line == "ERR|UNKNOWN" and not retried:
                # v0.9.60g - UART noise garbled that command; the board answers
                # ERR|UNKNOWN only for lines it did NOT execute -> one resend is safe.
                retried = True
                self._send(cmd)
                deadline = time.monotonic() + timeout
                continue
            return line

    def _send(self, text):
        if self.ser is None:
            raise PicoError("not connected")
        self.ser.write(text.encode("ascii") + b"\n")
        self.ser.flush()
        self.tx += 1

    def _read_line(self, timeout=0.5):
        """یک خط کامل؛ بایت‌های نیمه‌تمام در بافر می‌مانند (همان قاعدهٔ ams_serial)."""
        end = time.monotonic() + timeout
        while True:
            nl = self._rxbuf.find(b"\n")
            if nl >= 0:
                raw = bytes(self._rxbuf[:nl])
                del self._rxbuf[:nl + 1]
                self.rx += 1
                return raw.decode("utf-8", "replace").strip()
            if time.monotonic() >= end:
                return None
            chunk = self.ser.read(64)
            if chunk:
                self._rxbuf += chunk
            else:
                time.sleep(0.02)

    def halt(self):
        try:
            self._send("HALT")
        except Exception:
            pass

    def close(self):
        try:
            if self.ser is not None:
                self._send("HALT")
                self._send("BYE")
        except Exception:
            pass
        try:
            if self.ser is not None:
                self.ser.close()
        except Exception:
            pass
        self.ser = None


def _drain_stale(link):
    """v0.9.60e - eat stale PicoLink lines (write-only abort HALT answers, late
    PONGs) before a new op pairs them with its own reply. Zero cost on a quiet pipe.
    BoardLink (encrypted, direct arm) already resyncs - left untouched."""
    if not isinstance(link, PicoLink):
        return
    try:
        ser = getattr(link, "ser", None)
        if ser is None or (not getattr(ser, "in_waiting", 0) and not getattr(link, "_rxbuf", None)):
            return
        while True:
            line = link._read_line(0.1)
            if line is None:
                return
            if line.startswith("EVT|"):
                link.events.append(line)
    except Exception:
        return


def _ktext_timeout(cmd, default):
    """v0.9.60e - humanized typing is deliberately slow: size the wait from the
    payload (len x hmax + margin) so a long Type Text never dies at 5 s."""
    inner = cmd
    for env in ("KBDPICO|", "KBDARM|"):
        if inner.startswith(env):
            inner = inner[len(env):]
    if not inner.startswith("KTEXT|"):
        return default
    try:
        p = inner[6:].split(",", 2)
        per_ms = max(int(p[0]), int(p[1]))
        return max(default, 5.0 + len(p[2]) * per_ms / 1000.0 + 5.0)
    except Exception:
        return default


def open_link(port):
    """v0.9.59 — مغز پیکو اول، بازوی رمزشده به‌عنوان fallback.

    روی پورت PING می‌فرستد: پاسخ با role=brain/pico-light ← PicoLink متن‌باز؛
    هر پاسخ/سکوت دیگر ← همان BoardLink رمزشده‌ی همیشگی (پرو میکرو مستقیم).
    """
    link = PicoLink(port=port)
    try:
        dev = link.connect()
        return link, dev
    except Exception:
        try:
            link.close()
        except Exception:
            pass
    # A saved manual COM can point at the Pro Micro after Windows renumbers USB
    # ports. Before requiring the private direct-link key, scan every port and
    # transparently prefer the Pico brain. This fixes portable Classroom ZIPs
    # that correctly omit ams_key.json.
    detected = detect_board_port()
    if detected and str(detected).upper() != str(port).upper():
        alternate = PicoLink(port=detected)
        try:
            dev = alternate.connect()
            emit({"event": "stage", "stage": "pico_fallback", "from": port, "port": detected})
            return alternate, dev
        except Exception:
            try:
                alternate.close()
            except Exception:
                pass
    key_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ams_key.json")
    if not os.path.isfile(key_path):
        raise PicoError(
            "Pico brain not found on any serial port; portable Classroom "
            "cannot use direct Pro Micro mode without ams_key.json"
        )
    from ams_serial import BoardLink   # import تنبل — مسیر پیکو به ams_key.json نیاز ندارد
    link = BoardLink(port=port)
    dev = link.connect()
    return link, dev


def main():
    emit({"event": "stage", "stage": "bridge_started"})
    ap = argparse.ArgumentParser()
    ap.add_argument("--pydir", default=".",
                    help="پوشه حاوی ams_serial.py و ams_crypto.py")
    args = ap.parse_args()
    # v0.9.57 — bundled stack wins over anything on PYTHONPATH
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    if args.pydir:
        sys.path.insert(0, args.pydir)

    from ams_serial import BoardLink, BoardError  # noqa: F401
    emit({"event": "stage", "stage": "stack_imported"})
    state = {"link": None}
    ops = queue.Queue()
    abort_flag = threading.Event()   # وقتی abort برای فرمانِ در حال اجرا فرستاده شد
    stop_evt = threading.Event()

    # ── نخ رویدادهای EVT برد ────────────────────────────────────────
    def event_pump():
        seen = 0
        while not stop_evt.is_set():
            link = state["link"]
            if link is not None:
                evs = link.events
                while seen < len(evs):
                    emit({"event": "evt", "line": evs[seen]})
                    seen += 1
            time.sleep(0.05)

    threading.Thread(target=event_pump, daemon=True).start()

    # ── نخ کارگر: اجرای ترتیبی همهٔ عملیات‌ها ─────────────────────────
    def worker():
        while True:
            req = ops.get()
            if req is None:                      # سم پایان
                return
            op = req.get("op")
            try:
                if op == "connect":
                    # A prior send fault may have left a live Serial object in
                    # this sidecar even though the UI already shows Disconnected.
                    # Always release it before probing/opening another port.
                    stale = state["link"]
                    state["link"] = None
                    if stale is not None:
                        try:
                            stale.close()
                        except Exception:
                            pass
                    port = req.get("port") or "AUTO"
                    if port.strip().upper() in ("AUTO", ""):
                        port = detect_board_port() or "AUTO"   # v0.9.5 — اسکن خودکار
                    emit({"event": "stage", "stage": "port_open", "port": port})
                    # v0.9.59 — مغز پیکو اول: لینک متن‌باز pico-light اگر PING با
                    # role=brain جواب داد؛ وگرنه همان BoardLink رمزشده برای پرو میکرو.
                    link, dev = open_link(port)
                    state["link"] = link
                    emit({"event": "stage", "stage": "hello_ok", "fw": dev})
                    emit({"event": "connected", "port": dev, "fw": link.fw_ver, "role": getattr(link, "role", None)})

                elif op == "list_ports":
                    # v0.9.43 — اتصال دستی: فهرست پورت‌های واقعی تا کاربر پورت پیکو را خودش انتخاب کند
                    try:
                        from serial.tools import list_ports as _lp
                        emit({"event": "ports", "ports": [
                            {"device": p.device, "label": p.description or ""}
                            for p in _lp.comports()
                        ]})
                    except Exception as ex:
                        emit({"event": "ports", "ports": [], "error": str(ex)})

                elif op == "send":
                    link = state["link"]
                    if link is None:
                        raise BoardError("not connected")
                    cmd = req["cmd"]
                    abort_flag.clear()
                    _drain_stale(link)         # v0.9.60e - eat leftovers of write-only aborts
                    cmd_head = cmd.split("|", 1)[0]
                    if cmd_head in ("MMOVE", "CURSOR"):
                        # v0.9.60e / cursor-origin-sync - these are write-only transport
                        # hints. MMOVE has no board reply, and the modern Pico accepts
                        # CURSOR as a coalesced host-origin sample with no reply. Return a
                        # local acknowledgement so the WPF side does not wait for a reply
                        # that the firmware intentionally does not emit.
                        link._send(cmd)
                        reply = f"OK|{cmd_head}"  # local ack, same contract as send_path
                    else:
                        reply = link.command(cmd, timeout=_ktext_timeout(cmd, req.get("timeout", 5.0)))
                    if abort_flag.is_set():
                        # پاسخِ فرمانِ متوقف‌شده reply نمی‌شود تا جفت‌کردن
                        # پاسخ‌ها در سمت WPF به‌هم نریزد (انتظار قبلی cancel شده).
                        emit({"event": "aborted", "reply": reply, "cmd": cmd})
                    else:
                        emit({"event": "reply", "reply": reply})

                elif op == "send_path":
                    # v0.9.2 — استریم مسیر متراکم موس: MMOVE برای هر میکرواستپ بدون انتظار
                    # پاسخ (write-only) + PING هر ۱۲ نقطه برای تخلیهٔ بافر پاسخ‌ها. یک reply
                    # در انتها جفت‌کردن سمت WPF را حفظ می‌کند. abort بین نقاط چک می‌شود.
                    link = state["link"]
                    if link is None:
                        raise BoardError("not connected")
                    abort_flag.clear()
                    pts = [p for p in req.get("pts", "").split(";") if p]
                    dlys = [int(d) for d in req.get("dlys", "").split(";") if d.strip()]
                    if not pts:
                        raise BoardError("empty path")
                    send = getattr(link, "_send", None)
                    if send is None:
                        raise BoardError("bridge: _send unavailable")
                    _drain_stale(link)         # v0.9.60e - clean pipe before streaming
                    # UART can be healthy while the Pro Micro USB HID side is
                    # disconnected or suspended. Never turn that into a false
                    # OK|PATH: Windows cannot receive mouse reports then.
                    presence = link.command("PING", timeout=2.0)
                    if ("role=brain" in presence and "arm-usb=" in presence
                            and "arm-usb=3" not in presence):
                        arm_usb_state = presence.split("arm-usb=", 1)[1].split("|", 1)[0]
                        raise BoardError(
                            "Pro Micro USB HID is not active (arm-usb="
                            + arm_usb_state + "; expected 3/UP)")
                    # v0.9.60f - hardware-cadence thinning (the choppy-mouse fix). The arm
                    # executes ~50 moves/sec (~20 ms each, measured 2026-09-08), but dense
                    # WindMouse trails arrive at ~4 ms/point: oversubscribed 4-5x, the
                    # firmware's coalescing dropped points and the cursor visibly jumped.
                    # Merge micro-steps so every emitted point gets >= MIN_STEP_MS: same
                    # total time, same curve shape, and every point actually executes.
                    MIN_STEP_MS = 25
                    if len(pts) > 1 and dlys:
                        tp, td = [pts[0]], []
                        acc = 0
                        for i in range(1, len(pts)):
                            acc += dlys[i - 1] if i - 1 < len(dlys) else 0
                            if acc >= MIN_STEP_MS:
                                tp.append(pts[i])
                                td.append(acc)
                                acc = 0
                        if tp[-1] != pts[-1]:
                            tp.append(pts[-1])     # the final target ALWAYS lands
                            td.append(acc)
                        pts, dlys = tp, td
                    aborted = False
                    t0 = time.monotonic()
                    budget = 0.0   # pacing تطبیقی: زمان هدف انباشته می‌شود، خواب فقط به اندازهٔ عقب‌ماندگی
                    for i, p in enumerate(pts):
                        if abort_flag.is_set():
                            aborted = True
                            break
                        # v0.9.60g - abs,2 = interpolated path point (arm fw 1.9 splits each
                        # segment into <=8 px native-paced micro-steps -> hand-smooth). An
                        # older arm reads hm==2 as non-human and jumps per point (the v3.1
                        # behaviour) - safe either way; flash fw 1.9 for the smoothness.
                        send("MMOVE|" + p + ",abs,2")
                        if i % 12 == 11:
                            try:
                                link.command("PING", timeout=2.0)   # تخلیهٔ OK|MMOVEهای انباشته
                            except Exception:
                                pass
                        if i < len(dlys):
                            budget += dlys[i] / 1000.0
                            wait = t0 + budget - time.monotonic()
                            if wait > 0:
                                time.sleep(wait)
                    try:
                        link.command("PING", timeout=3.0)   # sync/tخلیهٔ نهایی
                    except Exception:
                        pass
                    if aborted or abort_flag.is_set():
                        emit({"event": "aborted", "reply": "ERR|PATH|aborted", "cmd": "send_path"})
                    else:
                        emit({"event": "reply", "reply": f"OK|PATH,{len(pts)}"})

                elif op == "disconnect":
                    link = state["link"]
                    if link is not None:
                        link.close()             # HALT + BYE + close (§۱۱.۳)
                        state["link"] = None
                    emit({"event": "disconnected"})

                elif op == "ping_bridge":
                    emit({"event": "pong"})

                else:
                    emit({"event": "error", "op": op, "message": "unknown op"})
            except Exception as e:
                if op in ("send", "send_path") and abort_flag.is_set():
                    emit({"event": "aborted", "reply": "ERR|aborted", "cmd": req.get("cmd")})
                else:
                    emit({"event": "error", "op": op, "message": str(e)})
                    if op in ("connect", "send", "send_path"):
                        failed = state["link"]
                        state["link"] = None
                        if failed is not None:
                            try:
                                failed.close()
                            except Exception:
                                pass
                        emit({"event": "disconnected"})

    threading.Thread(target=worker, daemon=True).start()

    # ── حلقهٔ اصلی stdin — abort همین‌جا و فوری پاسخ داده می‌شود ─────
    for raw in sys.stdin:
        raw = raw.strip()
        if not raw:
            continue
        try:
            req = json.loads(raw)
        except json.JSONDecodeError:
            emit({"event": "error", "message": "bad json"})
            continue

        if req.get("op") == "abort":
            handle_abort(state, abort_flag)
        else:
            ops.put(req)

    stop_evt.set()
    ops.put(None)


def handle_abort(state, abort_flag):
    """HALT را فوری روی پورت «می‌نویسد» تا گوش‌به‌زنگ/فرمان طولانی برد بشکند.

    فقط نوشتن — خواندن پاسخ بر عهدهٔ نخ کارگر است (دو خوانندهٔ هم‌زمان ممنوع).
    """
    link = state["link"]
    abort_flag.set()
    if link is None:
        emit({"event": "abort_sent", "note": "not connected"})
        return

    def _write_halt():
        try:
            send = getattr(link, "_send", None)
            if send is not None:
                send("HALT")                 # فقط نوشتن — بدون خواندن
            else:
                link.halt()                  # fallback برای ams_serial قدیمی
        except Exception:
            pass

    threading.Thread(target=_write_halt, daemon=True).start()
    emit({"event": "abort_sent"})


if __name__ == "__main__":
    main()
