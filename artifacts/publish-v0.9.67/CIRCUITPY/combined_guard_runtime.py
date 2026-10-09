# Combined Phase 7 board-owned runtime. It validates the exported bundle before routing.
import gc
import json
import math
import os
import random
import time
import board
import busio
import digitalio
import pwmio
import usb_cdc
import usb_hid
import storage

class Keyboard:
    """Small boot-keyboard driver; avoids an undeclared adafruit_hid dependency."""
    def __init__(self, devices):
        self.device = None
        for device in devices:
            if getattr(device, "usage_page", None) == 0x01 and getattr(device, "usage", None) == 0x06:
                self.device = device
                break
        if self.device is None:
            raise RuntimeError("USB HID keyboard device is unavailable")
        self.report = bytearray(8)
        # Clear any key state the host may retain across a firmware reload.
        try:
            self._send()
        except Exception:
            pass
        # Clear any key state the host may retain across a firmware reload.
        try:
            self._send()
        except Exception:
            pass
        # Clear any key state the host may retain across a firmware reload.
        try:
            self._send()
        except Exception:
            pass

    @staticmethod
    def _modifier(code):
        return code - 224 if 224 <= code <= 231 else None

    def _send(self):
        try:
            # Standard descriptors use report ID 1, but some board builds expose
            # the optional argument while raising NotImplementedError at runtime.
            self.device.send_report(self.report, 1)
        except (TypeError, NotImplementedError):
            # A single-report keyboard can infer its only report ID.
            self.device.send_report(self.report)

    def press(self, *codes):
        for code in codes:
            modifier = self._modifier(code)
            if modifier is not None:
                self.report[0] |= 1 << modifier
                continue
            # Do not use ``code in self.report[2:]`` here. On the target
            # CircuitPython build bytearray containment raises NotImplementedError.
            duplicate = False
            for index in range(2, 8):
                if self.report[index] == code:
                    duplicate = True
                    break
            if duplicate:
                continue
            for index in range(2, 8):
                if self.report[index] == 0:
                    self.report[index] = code
                    break
            else:
                raise ValueError("USB HID keyboard supports at most six simultaneous keys")
        self._send()

    def release(self, *codes):
        for code in codes:
            modifier = self._modifier(code)
            if modifier is not None:
                self.report[0] &= ~(1 << modifier)
                continue
            for index in range(2, 8):
                if self.report[index] == code:
                    self.report[index] = 0
        self._send()

    def release_all(self):
        for index in range(8):
            self.report[index] = 0
        # Repeat the zero report. This is deliberately fail-safe: a cleanup
        # failure must not abort the route before the host sees key-up.
        for _ in range(2):
            try:
                self._send()
            except Exception:
                pass

# Combined Guard routes are executed only by the bounded streaming runner.
# The verifier/protocol dependencies are bound by code.py after this module
# imports. This avoids a nested allocation peak on the RP2040.
# Contract markers for the deferred bindings:
# from guard_calibration_protocol import build_calibration_get, parse_calibration_set
# from live_light_guard import HASHED_BUNDLE_FILES, GuardBundleError, LightStateGuard, _file_sha256, load_guard_bundle

PROFILES = ("desktop", "login-or-dc", "character-dashboard", "entering-game-loading", "game", "targeted")
PENDING_REVISION = "pending"
DC_ESC_DELAY_MIN_MS = 88
DC_ESC_DELAY_MAX_MS = 188

class BH1750:
    def __init__(self):
        self.i2c = busio.I2C(board.GP21, board.GP20); self.buf = bytearray(2)
        for command in (1, 7, 0x10): self._write(command)
        time.sleep(.2)
    def _write(self, command):
        while not self.i2c.try_lock(): pass
        try: self.i2c.writeto(0x23, bytes((command,)))
        finally: self.i2c.unlock()
    def lux(self):
        while not self.i2c.try_lock(): pass
        try: self.i2c.readfrom_into(0x23, self.buf)
        finally: self.i2c.unlock()
        value = ((self.buf[0] << 8) | self.buf[1]) / 1.2
        if not math.isfinite(value) or value < 0: raise RuntimeError("invalid lux")
        return value

class Arm:
    """UART0 GP16/GP17 transport: checksum frames, short-write rejection, two-move back-pressure."""
    def __init__(self):
        self.uart = busio.UART(board.GP16, board.GP17, baudrate=57600, timeout=.05)
        self.buf = bytearray(); self.pending = 0; self.held = set()
    def frame(self, line): return ("#%02X|%s\n" % (sum(line.encode()) & 255, line)).encode()
    def write(self, line):
        data = self.frame(line); count = self.uart.write(data)
        if count is not None and count != len(data): raise RuntimeError("short UART write")
    def pump(self):
        if self.uart.in_waiting: self.buf.extend(self.uart.read(self.uart.in_waiting))
        replies = []
        while b"\n" in self.buf:
            raw, self.buf = self.buf.split(b"\n", 1); line = raw.decode("utf-8", "replace").strip()
            if line.startswith("OK|MMOVE"): self.pending = max(0, self.pending - 1)
            elif line.startswith("EVT|"): print(line)
            elif line: replies.append(line)
        return replies
    def move(self, x, y):
        end = time.monotonic() + 2
        while self.pending >= 2:
            self.pump()
            if time.monotonic() > end: raise RuntimeError("arm back-pressure timeout")
            time.sleep(.001)
        self.write("MMOVE|%d,%d,abs,2" % (x, y)); self.pending += 1
    def _track_button_command(self, line):
        head, sep, payload = line.partition("|")
        button = payload.split(",", 1)[0].strip().lower() if sep else ""
        if head == "MDOWN" and button in ("left", "right", "middle"):
            self.held.add(button)
        elif head == "MUP" and button in ("left", "right", "middle"):
            self.held.discard(button)

    def send(self, line, timeout=5):
        head = line.split("|", 1)[0]
        # SETRES is idempotent. A stale ERR/partial UART line must not make a
        # whole Route fail; retry it once after the receive queue is pumped.
        attempts = 2 if head == "SETRES" else 1
        last_error = None
        for attempt in range(attempts):
            self.pump()
            self.write(line)
            end = time.monotonic() + timeout
            while time.monotonic() < end:
                for reply in self.pump():
                    if reply.startswith("OK|" + head):
                        self._track_button_command(line)
                        return reply
                    if reply.startswith("ERR|"):
                        last_error = reply
                        break
                if last_error is not None: break
                time.sleep(.002)
            if attempt + 1 < attempts:
                self.pump()
                time.sleep(.03)
                last_error = None
                continue
            if last_error is not None:
                raise RuntimeError("ARM %s rejected: %s" % (head, last_error))
            raise RuntimeError("ARM %s acknowledgement timeout" % head)
    def flush(self):
        end = time.monotonic() + 3
        while self.pending and time.monotonic() < end: self.pump(); time.sleep(.002)
        if self.pending: raise RuntimeError("arm move acknowledgement timeout")
    def release(self, force=True):
        # Never emit MUP for a button that this runtime did not observe going
        # down. Unconditional MUP frames were interpreted by the Pro Micro as
        # a stray click/hold on otherwise mouse-only routes.
        for button in tuple(self.held):
            try: self.send("MUP|" + button, 1)
            except Exception: pass
        self.held.clear(); self.pending = 0
    def abort(self):
        # Stop the active human-mouse operation first. Do not send synthetic
        # MUP frames for left/right/middle when no MDOWN was acknowledged.
        try:
            self.write("HALT")
        except Exception:
            pass
        deadline = time.monotonic() + .35
        while time.monotonic() < deadline:
            halted = False
            for reply in self.pump():
                if reply.startswith("OK|HALT") or reply.startswith("ERR|ABORTED"):
                    halted = True
                    break
            if halted:
                break
            time.sleep(.002)
        for button in tuple(self.held):
            try:
                self.write("MUP|" + button)
                time.sleep(.008)
            except Exception:
                pass
        deadline = time.monotonic() + .35
        while time.monotonic() < deadline:
            self.pump()
            time.sleep(.002)
        self.held.clear(); self.pending = 0
    def prepare_route(self):
        # Software equivalent of the power-cycle workaround: cancel a stale
        # route, neutralize all buttons, and discard completed move debt before
        # SETRES or the first RMOUSE command of the next route.
        self.abort()
        self.pending = 0

class Button:
    def __init__(self, pin):
        self.io = digitalio.DigitalInOut(pin); self.io.direction = digitalio.Direction.INPUT; self.io.pull = digitalio.Pull.UP
        self.down = False; self.changed = 0; self.started = 0; self.long = False
    def poll(self, now):
        pressed = not self.io.value
        if pressed != self.down and now - self.changed >= .04:
            self.changed = now; self.down = pressed
            if pressed: self.started = now; self.long = False; return "down"
            return "up"
        if pressed and not self.long and now - self.started >= 3: self.long = True; return "long"
        return None

class Controls:
    def __init__(self, arm, keyboard): self.arm = arm; self.keyboard = keyboard; self.running = False; self.paused = False; self.aborted = False
    def start(self): self.running = True; self.paused = False; self.aborted = False
    def stop(self):
        self.running = False; self.paused = False; self.aborted = True
        try: self.keyboard.release_all()
        except Exception: pass
        self.arm.abort()
    def gate(self):
        while self.paused and self.running: self.arm.pump(); time.sleep(.01)
        return self.running and not self.aborted
    def sleep(self, milliseconds):
        end = time.monotonic() + max(0, milliseconds) / 1000
        while time.monotonic() < end:
            self.arm.pump()
            if not self.gate(): return False
            time.sleep(.005)
        return True

class PlanContext:
    plan_api = 3; screen_w = 1920; screen_h = 1080; speed_min = 0; speed_max = 2000
    def __init__(self, runtime): self.r = runtime
    def now(self): return time.monotonic()
    def gate(self): return self.r.controls.gate()
    def sleep_ms(self, ms): return self.r.controls.sleep(ms)
    def log(self, text): print("plan:", text)
    def mmove(self, x, y): self.r.arm.move(x, y)
    def mclick(self, button, count, hmin, hmax): self.r.arm.send("MCLICK|%s,%d,%d,%d" % (button, count, hmin, hmax), 8)
    def ktext(self, hmin, hmax, text): self.r.type_text(text, hmin, hmax, self)
    def kcombo(self, value): self.key_combo([value], 0, 0)
    def key(self, vk, hold): self.key_combo([vk], hold, hold)
    def key_combo(self, vks, hmin, hmax):
        codes = [self.r.key(v) for v in vks]
        for code in codes: self.r.keyboard.press(code)
        self.sleep_ms(max(hmin, hmax))
        for code in reversed(codes): self.r.keyboard.release(code)
    def kdown(self, vk): self.r.keyboard.press(self.r.key(vk))
    def kup(self, vk): self.r.keyboard.release(self.r.key(vk))
    def wheel(self, delta): self.r.arm.send("MWHEEL|%d" % delta, 3)
    def raw(self, line): return self.r.arm.send(str(line), 5)
    def setres(self, w, h): return self.r.arm.send("SETRES|%d,%d" % (w, h), 5)
    def wait_sound(self, threshold, minimum, timeout):
        reply = self.r.arm.send("WSND|%d,%d,%d" % (threshold, minimum, timeout), timeout / 1000 + 3)
        return True if "DETECTED" in reply else False if "TIMEOUT" in reply else None
    def trg_sound(self, threshold, minimum, timeout, action, rmin, rmax, hmin, hmax):
        return self.r.arm.send("TRGSND|%d,%d,%d,%d,%d,%d,%d,%d" % (threshold, minimum, timeout, action, rmin, rmax, hmin, hmax), timeout / 1000 + 3).startswith("OK|")
    def beep(self, frequency, duration):
        tone = pwmio.PWMOut(board.GP6, duty_cycle=32768, frequency=int(frequency), variable_frequency=True)
        try: self.sleep_ms(duration)
        finally: tone.duty_cycle = 0; tone.deinit()
    def read_plan_file(self, name):
        if not name or any(c in name for c in "/\\:"): raise ValueError("unsafe include")
        with open("/" + name, "r") as fh: return fh.read()
    def wait_light(self, lo, hi, stable, timeout, mode):
        start = None; end = time.monotonic() + timeout / 1000
        while time.monotonic() < end:
            value = self.r.sensor.lux()
            if lo <= value <= hi:
                if start is None: start = time.monotonic()
                if (time.monotonic() - start) * 1000 >= stable: return True
            else: start = None
            if not self.sleep_ms(20): return False
        return False

class Combined:
    def __init__(self):
        self.arm = Arm(); self.keyboard = Keyboard(usb_hid.devices); self.controls = Controls(self.arm, self.keyboard); self.sensor = BH1750()
        self.bundle = load_guard_bundle("/"); self.guard = LightStateGuard.from_bundle("/"); self.routes = {}
        self.blue = Button(board.GP4); self.yellow = Button(board.GP3); self.usb = usb_cdc.data or usb_cdc.console; self.host = bytearray()
        self.calibrating = False; self.stage = 0; self.samples = []; self.sample_started = 0; self.result = None; self.saved = False; self.saved_ids = set(); self.last_cal_error = None
    def key(self, vk):
        # Convert Windows virtual-key values directly to USB HID usages.
        # Keep this branch-only mapping allocation-free on CircuitPython's small heap.
        if 65 <= vk <= 90: return vk - 61
        if 49 <= vk <= 57: return vk - 19
        if vk == 48: return 39
        if 112 <= vk <= 123: return vk - 54
        if vk == 13: return 40
        if vk == 27: return 41
        if vk == 8: return 42
        if vk == 9: return 43
        if vk == 32: return 44
        if vk == 37: return 80
        if vk == 38: return 82
        if vk == 39: return 79
        if vk == 40: return 81
        if vk == 160: return 225
        if vk == 162: return 224
        if vk == 164: return 226
        if vk == 91: return 227
        return 8
    def type_text(self, text, hmin, hmax, ctx):
        for ch in text:
            if not ctx.gate(): raise RuntimeError("route aborted")
            code = self.key(ord(ch.upper())) if ch.isalnum() else self.key(32 if ch == " " else 13)
            self.keyboard.press(code); self.keyboard.release(code)
            if not ctx.sleep_ms(hmax if hmax > hmin else hmin): raise RuntimeError("route aborted")
    def emit(self, line):
        try: self.usb.write((line + "\n").encode())
        except Exception: pass
    def _ensure_calibration_storage_writable(self):
        # Calibration is an approved runtime write path. Keep CIRCUITPY
        # writable so JSON persistence does not depend on a boot button state.
        try:
            remount = getattr(storage, "remount", None)
            if remount is None:
                raise RuntimeError("storage.remount unavailable")
            remount("/", readonly=False, disable_concurrent_write_protection=True)
        except Exception as exc:
            raise RuntimeError("calibration filesystem is not writable: " + str(exc)[:80])

    def _write_verified_text(self, path, text):
        # CircuitPython can acknowledge a filesystem write before the USB
        # volume has committed it. Flush, close, and read back the exact text
        # before allowing calibration to proceed.
        with open(path, "w") as fh:
            fh.write(text)
            try: fh.flush()
            except Exception: pass
        with open(path, "r") as fh:
            if fh.read() != text:
                raise RuntimeError("write verification mismatch: " + path)

    def _replace_text(self, path, text):
        temp = path + ".tmp"
        try:
            self._write_verified_text(temp, text)
            try: os.remove(path)
            except Exception: pass
            os.rename(temp, path)
            with open(path, "r") as fh:
                if fh.read() != text:
                    raise RuntimeError("rename verification mismatch: " + path)
        except Exception:
            try: os.remove(temp)
            except Exception: pass
            raise
    def _read_text(self, path):
        with open(path, "r") as fh: return fh.read()
    def _replace_json(self, path, text):
        self._replace_text(path, text)
    def _hash_manifest(self):
        lines = ["%s  %s" % (_file_sha256("/", name), name) for name in sorted(HASHED_BUNDLE_FILES)]
        return "\n".join(lines) + "\n"
    def _publish_calibration(self, revision, profile_id, profile):
        manifest = json.loads(json.dumps(self.bundle["manifest"]))
        calibration = json.loads(json.dumps(self.bundle["calibration"]))
        manifest["calibrationRevision"] = revision
        found = False
        for item in manifest.get("profiles", []):
            if item.get("id") == profile_id:
                item["center"] = profile["center"]; item["tolerance"] = profile["tolerance"]; item["stableMs"] = profile["stable_ms"]; found = True
        if not found: raise GuardBundleError("profile missing from Guard manifest")
        calibration["revision"] = revision
        calibration.setdefault("profiles", {})[profile_id] = {
            "center": profile["center"], "tolerance": profile["tolerance"], "stable_ms": profile["stable_ms"]}
        self._ensure_calibration_storage_writable()
        old_manifest = self._read_text("/guard-transition.json")
        old_calibration = self._read_text("/guard-calibration.json")
        old_hashes = self._read_text("/SHA256SUMS.txt")
        try:
            self._replace_json("/guard-transition.json", json.dumps(manifest))
            self._replace_json("/guard-calibration.json", json.dumps(calibration))
            self._replace_text("/SHA256SUMS.txt", self._hash_manifest())
            new_bundle = load_guard_bundle("/")
            new_guard = LightStateGuard.from_bundle("/")
        except Exception as exc:
            self.last_cal_error = type(exc).__name__ + ":" + str(exc)[:80]
            self.emit("ERR|CAL|SAVE|" + self.last_cal_error)
            rollback_error = None
            for path, text in (
                ("/guard-transition.json", old_manifest),
                ("/guard-calibration.json", old_calibration),
                ("/SHA256SUMS.txt", old_hashes),
            ):
                try:
                    self._replace_text(path, text)
                except Exception as exc:
                    if rollback_error is None: rollback_error = exc
            if rollback_error is not None: raise RuntimeError("calibration rollback failed") from rollback_error
            raise
        self.bundle = new_bundle; self.guard = new_guard; self.last_cal_error = None
    def calibration_count(self):
        return len(self.bundle.get("calibration", {}).get("profiles", {}))
    def calget(self):
        return build_calibration_get(self.bundle["revision"], self.calibration_count())
    def calstatus(self):
        profiles = self.bundle.get("calibration", {}).get("profiles", {})
        parts = []
        for pid in PROFILES:
            item = profiles.get(pid, {})
            parts.append("%s:%.3f:%.3f:%d" % (
                pid,
                float(item.get("center", 0)),
                float(item.get("tolerance", 0)),
                int(item.get("stable_ms", 750))))
        error = (self.last_cal_error or "none").replace("|", "/").replace("\n", " ")[:80]
        return "OK|CALSTATUS|revision=%s|count=%d|profiles=%s|last_error=%s" % (
            self.bundle.get("revision", "unknown"), len(profiles), ";".join(parts), error)
    def calset(self, line):
        if self.controls.running or self.calibrating: return "ERR|CALSET|BUSY"
        payload, error = parse_calibration_set(line)
        if error is not None: return "ERR|CALSET|" + error
        try:
            self._publish_calibration(payload["revision"], payload["id"], payload)
        except Exception:
            return "ERR|CALSET|SAVE"
        return "OK|CALSET|%s|revision=%s|count=%d" % (payload["id"], payload["revision"], self.calibration_count())
    def start_cal(self):
        self.controls.stop(); self.calibrating = True; self.stage = 0; self.samples = []; self.sample_started = 0; self.result = None; self.saved = False; self.saved_ids = set(); self.emit("EVT|CAL|mode=ready|stage=1|id=" + PROFILES[0] + "|seconds=5|saved=0")
    def end_cal(self):
        if self.result is not None and self.result != "sampling" and not self.saved: self.emit("ERR|CAL|UNSAVED|stage=%d" % (self.stage + 1)); return
        self.calibrating = False; self.result = None; self.emit("EVT|CAL|mode=exited|saved=%d" % len(self.saved_ids))
    def save_cal(self):
        if not isinstance(self.result, dict): self.emit("ERR|CAL|BUSY|stage=%d" % (self.stage + 1)); return
        try: self._publish_calibration(PENDING_REVISION, PROFILES[self.stage], self.result)
        except Exception as exc:
            self.last_cal_error = type(exc).__name__ + ":" + str(exc)[:80]
            self.emit("ERR|CAL|SAVE|stage=%d|detail=%s" % (self.stage + 1, self.last_cal_error))
            return
        self.saved = True; self.saved_ids.add(PROFILES[self.stage]); self.emit("EVT|CAL|mode=saved-stage|stage=%d|id=%s|saved=%d" % (self.stage+1, PROFILES[self.stage], len(self.saved_ids)))
    def yellow_action(self):
        if not self.calibrating: self.controls.paused = not self.controls.paused; return
        if self.result == "sampling": self.emit("ERR|CAL|BUSY|stage=%d" % (self.stage + 1)); return
        if self.result is not None: self.save_cal(); return
        self.samples = []; self.sample_started = time.monotonic(); self.result = "sampling"; self.emit("EVT|CAL|mode=started|stage=%d|id=%s|seconds=5|saved=%d" % (self.stage+1, PROFILES[self.stage], len(self.saved_ids)))
    def cal_tick(self):
        if not self.calibrating or self.result != "sampling": return
        self.samples.append(self.sensor.lux())
        if time.monotonic() - self.sample_started < 5: return
        values = sorted(self.samples); center = values[len(values)//2]; spread = max(values)-min(values)
        if len(values) < 5 or spread > 5: self.result = None; self.emit("ERR|CAL|UNSTABLE|stage=%d|spread=%.1f" % (self.stage+1, spread)); return
        self.result = {"center":center,"tolerance":max(2.0,spread*1.5),"stable_ms":750}; self.saved = False; self.emit("EVT|CAL|mode=complete-stage|stage=%d|id=%s|center=%.1f|spread=%.1f|tolerance=%.1f|saved=0" % (self.stage+1, PROFILES[self.stage], center, spread, self.result["tolerance"]))
    def next_cal(self):
        if not self.calibrating: return
        if self.result is not None and self.result != "sampling" and not self.saved: self.emit("ERR|CAL|UNSAVED|stage=%d" % (self.stage+1)); return
        if self.stage >= 5: self.emit("EVT|CAL|mode=last|stage=6|saved=%d" % len(self.saved_ids)); return
        self.stage += 1; self.result = None; self.saved = False; self.emit("EVT|CAL|mode=ready|stage=%d|id=%s|seconds=5|saved=%d" % (self.stage+1, PROFILES[self.stage], len(self.saved_ids)))
    def buttons(self):
        now = time.monotonic(); blue = self.blue.poll(now); yellow = self.yellow.poll(now)
        if blue == "long": self.end_cal() if self.calibrating else self.start_cal()
        elif blue == "up" and not self.blue.long: self.next_cal() if self.calibrating else (self.controls.stop() if self.controls.running else (self.guard.reset(), self.controls.start()))
        if yellow == "up" and not self.yellow.long: self.yellow_action()
        self.cal_tick()
    def _dismiss_dc_popup(self, decision):
        # Login and DC share the same optical profile. The transition policy
        # marks a stable observation from stages 3–5 as context=dc, so ESC is
        # never injected on the actual login path.
        if decision.get("context") != "dc":
            return True
        # New bundles carry the explicit DC tab as dc_steps.txt. Keep the inline
        # injector only for legacy bundles that do not publish that route.
        if decision.get("route") == "dc_steps.txt":
            return True
        delay_ms = random.randint(DC_ESC_DELAY_MIN_MS, DC_ESC_DELAY_MAX_MS)
        self.emit("EVT|DC|ESC|phase=delay|delay-ms=%d" % delay_ms)
        if not self.controls.sleep(delay_ms):
            return False
        esc = self.key(27)
        try:
            self.keyboard.press(esc)
            self.emit("EVT|DC|ESC|phase=sent|delay-ms=%d|key=ESC" % delay_ms)
        finally:
            # Always release the key even if the USB HID report fails.
            self.keyboard.release(esc)
        return True
    def route(self, decision):
        if not decision.get("execute"): return
        name = decision.get("route")
        if name not in self.bundle["manifest"]["routes"].values(): raise GuardBundleError("unvalidated route")
        # A parsed plan is consumable: loop/random bookkeeping must not be
        # reused by a later invocation of the same Route.
        with open("/" + name, "r") as fh:
            route_plan = plan_engine.parse_plan(fh.read())
        self.arm.prepare_route()
        try:
            if not self._dismiss_dc_popup(decision):
                return
            plan_engine.run_plan(route_plan, PlanContext(self))
            self.arm.flush()
        finally:
            # Always leave the Pro Micro neutral even when a plan step fails.
            self.arm.release(False)
            del route_plan
            gc.collect()
    def host_poll(self):
        if self.usb.in_waiting: self.host.extend(self.usb.read(self.usb.in_waiting))
        while b"\n" in self.host:
            raw, self.host = self.host.split(b"\n", 1); line = raw.decode("utf-8", "replace").strip()
            if not line: continue
            try:
                if line == "PING": reply = "OK|PONG|combined-pico-guard-executor|hid=on|uart=on|profiles=6"
                elif line == "CALGET": reply = self.calget()
                elif line == "CALSTATUS": reply = self.calstatus()
                elif line.startswith("CALSET|"): reply = self.calset(line)
                elif line == "GUARD|ON": self.controls.start(); reply = "OK|GUARD|ON"
                elif line in ("GUARD|OFF", "HALT"): self.controls.stop(); reply = "OK|GUARD|OFF"
                elif line == "LUX?": reply = "OK|LUX|lux=%.1f|sensor=ok" % self.sensor.lux()
                else: reply = "ERR|GUARD|UNKNOWN"
                self.emit(reply)
            except Exception: self.emit("ERR|EXC")
    def loop(self):
        self.emit("combined-pico-guard-executor|GP4 start/stop hold3s=calibration|GP3 pause/resume|GP6 piezo"); last = 0
        while True:
            self.host_poll(); self.buttons(); self.arm.pump()
            if self.controls.running and not self.calibrating and time.monotonic() - last >= .25:
                last = time.monotonic()
                try:
                    self.guard.update(self.sensor.lux(), int(last * 1000))
                    if self.guard.last_decision is not None: self.route(self.guard.last_decision)
                except Exception as exc: self.controls.stop(); self.emit("ERR|GUARD|FAIL|" + str(exc)[:60])
            time.sleep(.01)

def main():
    try: Combined().loop()
    except GuardBundleError as exc: print("combined Guard bundle rejected:", exc)
    except Exception as exc: print("combined Guard stopped:", exc)
