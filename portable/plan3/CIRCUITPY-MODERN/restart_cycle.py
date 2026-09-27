"""Low-memory RUNFOR/AUTORESUME controller for the modern Guard runtime."""
import random
import time

MAGIC = b"RC83"
MAX_RESTARTS = 5


def _pair(value, label, allow_zero=False):
    fields = value.split(",")
    if len(fields) != 2:
        raise ValueError(label + " needs min,max")
    low, high = int(fields[0]), int(fields[1])
    if high < low:
        low, high = high, low
    if low < (0 if allow_zero else 1):
        raise ValueError(label + " range")
    return low, high


def parse_policy(text):
    """Parse only root cycle headers; never import the full plan parser."""
    run = auto = launch = None
    seen_plan = False
    seen_work = False
    for line_no, raw in enumerate(text.split("\n"), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        op, sep, body = line.partition("|")
        op = op.upper()
        if op == "PLAN":
            if seen_plan or not sep:
                raise ValueError("line %d: PLAN" % line_no)
            seen_plan = True
            continue
        if op not in ("RUNFOR", "AUTORESUME", "POSTLAUNCH", "LAUNCH"):
            seen_work = True
            continue
        if not seen_plan or seen_work:
            raise ValueError("line %d: cycle header order" % line_no)
        if op == "RUNFOR":
            if run is not None:
                raise ValueError("duplicate RUNFOR")
            run = _pair(body, op)
        elif op == "AUTORESUME":
            fields = body.split(",")
            if auto is not None or len(fields) != 3 or fields[0] not in ("0", "1"):
                raise ValueError("AUTORESUME format")
            auto = (fields[0] == "1",) + _pair(",".join(fields[1:]), op)
        else:
            fields = body.split(",")
            if launch is not None or len(fields) != 6 or fields[0] not in ("0", "1"):
                raise ValueError("POSTLAUNCH format")
            slot = int(fields[1])
            if not 1 <= slot <= 9:
                raise ValueError("POSTLAUNCH slot")
            launch = (
                fields[0] == "1", slot,
                _pair(",".join(fields[2:4]), "POSTLAUNCH before", True),
                _pair(",".join(fields[4:6]), "POSTLAUNCH after", True),
            )
    if run is None and auto is None and launch is None:
        return None
    if run is None or auto is None:
        raise ValueError("RUNFOR and AUTORESUME must be paired")
    return {"run": run, "auto": auto,
            "launch": launch or (False, 1, (0, 0), (0, 0))}


class Marker:
    """NVM-only marker. The debug journal owns bytes 0..1535."""
    def __init__(self, nvm):
        self.nvm = nvm
        self.base = (len(nvm) - 16) if nvm is not None and len(nvm) >= 1552 else -1

    def available(self):
        return self.base >= 1536

    def armed(self):
        try:
            return self.available() and bytes(self.nvm[self.base:self.base + 4]) == MAGIC \
                and self.nvm[self.base + 4] == 0xA5
        except Exception:
            return False

    def count(self):
        try:
            return int(self.nvm[self.base + 5]) if self.available() else 0
        except Exception:
            return 0

    def arm_next(self):
        if not self.available():
            return False
        count = self.count()
        if count >= MAX_RESTARTS:
            self.clear_armed()
            return False
        self.nvm[self.base:self.base + 4] = MAGIC
        self.nvm[self.base + 4] = 0xA5
        self.nvm[self.base + 5] = count + 1
        return self.armed()

    def clear_armed(self):
        if self.available():
            for index in range(5):
                self.nvm[self.base + index] = 0

    def reset(self):
        if self.available():
            for index in range(16):
                self.nvm[self.base + index] = 0


class Controller:
    def __init__(self, owner, nvm, policy, now=None, rng=None):
        self.owner = owner
        self.policy = policy
        self.now = now or time.monotonic
        self.rng = rng or random
        self.marker = Marker(nvm)
        self.phase = "idle"
        self.deadline = None
        self.due = False
        self.down_seen = False
        self.up_since = None
        self.resume_at = None
        self.previous_running = False
        if policy is not None and self.marker.armed():
            self.phase = "wait-usb"
            self.down_seen = True
            self._emit("armed-at-boot", "count=%d" % self.marker.count())

    @classmethod
    def from_root(cls, owner, nvm, path="/plan.txt", **kwargs):
        try:
            with open(path, "r") as handle:
                policy = parse_policy(handle.read())
        except Exception as exc:
            owner.emit("EVT|CYCLE|disabled|reason=parse-%s" % type(exc).__name__)
            policy = None
        return cls(owner, nvm, policy, **kwargs)

    def _emit(self, event, detail=""):
        self.owner.emit("EVT|CYCLE|" + event + (("|" + detail) if detail else ""))

    def _draw(self, pair):
        return pair[0] if pair[1] <= pair[0] else self.rng.randint(pair[0], pair[1])

    def _begin_run(self, resumed=False):
        seconds = self._draw(self.policy["run"])
        self.deadline = self.now() + seconds
        self.phase = "run"
        self.due = False
        self.previous_running = True
        self._emit("resumed" if resumed else "armed",
                   "run=%d|count=%d" % (seconds, self.marker.count()))

    def _manual_stop(self):
        self.deadline = None
        self.due = False
        self.phase = "idle"
        self.marker.reset()
        self._emit("cancelled", "reason=manual-stop")

    def route_tick(self):
        if self.phase == "run" and self.deadline is not None and self.now() >= self.deadline:
            self.due = True
            self.owner.controls.running = False
            self.owner.controls.aborted = True
            try:
                self.owner.keyboard.release_all()
            except Exception:
                pass

    def _host_state(self):
        if getattr(self.owner.arm, "host_usb_seen", False):
            return getattr(self.owner.arm, "host_usb_state", None)
        try:
            import supervisor
            return "UP" if supervisor.runtime.usb_connected else "DOWN"
        except Exception:
            return None

    def _perform_restart(self):
        auto_enabled = self.policy["auto"][0]
        if auto_enabled and not self.marker.arm_next():
            self._emit("blocked", "reason=marker-or-limit")
            self.phase = "idle"
            self.deadline = None
            return
        self.phase = "restarting"
        self._emit("deadline", "restart=begin|count=%d" % self.marker.count())
        self.owner.controls.start()
        try:
            import restart_windows
            restart_windows.perform(self.owner.plan_context(), self.rng)
        except Exception as exc:
            self.marker.clear_armed()
            self.phase = "idle"
            self.deadline = None
            self.owner.controls.running = False
            self.owner.controls.aborted = True
            self._emit("failed", "stage=restart|error=%s" % type(exc).__name__)
            return
        self.owner.controls.running = False
        self.owner.controls.aborted = True
        self.deadline = None
        self.down_seen = False
        self.up_since = None
        self.resume_at = None
        self.phase = "wait-usb" if auto_enabled else "idle"
        self._emit("restart-sent", "auto=%d" % (1 if auto_enabled else 0))

    def _launch(self):
        enabled, slot, before, after = self.policy["launch"]
        if not enabled:
            return
        ctx = self.owner.plan_context()
        self._emit("postlaunch", "phase=before|slot=%d" % slot)
        if not ctx.sleep_ms(self._draw(before) * 1000):
            raise RuntimeError("postlaunch cancelled")
        ctx.kdown(91)
        try:
            if not ctx.sleep_ms(self.rng.randint(35, 75)):
                raise RuntimeError("postlaunch cancelled")
            ctx.key(48 + slot, self.rng.randint(55, 110))
        finally:
            ctx.kup(91)
        self._emit("postlaunch", "phase=sent|slot=%d" % slot)
        if not ctx.sleep_ms(self._draw(after) * 1000):
            raise RuntimeError("postlaunch cancelled")

    def _resume(self):
        self.phase = "launching"
        self.owner.controls.start()
        try:
            self._launch()
        except Exception as exc:
            self.owner.controls.running = False
            self.owner.controls.aborted = True
            self._emit("failed", "stage=postlaunch|error=%s" % type(exc).__name__)
            self.phase = "wait-usb"
            return
        self.marker.clear_armed()
        self.owner.guard.reset()
        self.owner.guard.last_decision = None
        self.owner.debug_last_state = None
        self.owner.debug_last_denied = None
        self._begin_run(True)

    def tick(self):
        if self.policy is None:
            return
        running = bool(self.owner.controls.running)
        if self.phase == "idle" and running and not self.previous_running:
            self.marker.reset()
            self._begin_run(False)
        elif self.phase == "run":
            if self.due:
                self._perform_restart()
            elif not running and self.previous_running:
                self._manual_stop()
        elif self.phase == "wait-usb":
            # A physical/host Start while waiting is an explicit manual
            # override. Cancel the stale resume marker and start a fresh cycle.
            if running and not self.previous_running:
                self.marker.reset()
                self._emit("cancelled", "reason=manual-override")
                self._begin_run(False)
                self.previous_running = True
                return
            state = self._host_state()
            if state in ("DOWN", "SUSPEND"):
                if not self.down_seen:
                    self._emit("usb", "state=" + state)
                self.down_seen = True
                self.up_since = None
                self.resume_at = None
            elif state == "UP" and self.down_seen:
                if self.up_since is None:
                    self.up_since = self.now()
                    delay = self._draw(self.policy["auto"][1:])
                    self.resume_at = self.up_since + 2 + delay
                    self._emit("usb", "state=UP|resume-in=%d" % (delay + 2))
                elif self.now() >= self.resume_at:
                    self._resume()
        self.previous_running = bool(self.owner.controls.running)