"""Low-memory After/Startup controller for the modern Guard runtime.

Game owns its own duration (for example LOOPTIME). When game_steps.txt finishes,
After runs immediately. A persistent marker survives the Windows restart; after
USB returns and is stable, Startup runs once, then Login/DC continues while
Desktop is intentionally skipped.
"""
import time

MAGIC = b"RC84"
MAX_RESTARTS = 5
AFTER_ROUTE = "restart_steps.txt"
STARTUP_ROUTE = "startup_steps.txt"
USB_STABLE_SECONDS = 2


class Marker:
    """NVM-only restart marker. The debug journal owns bytes 0..1535."""
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
    def __init__(self, owner, nvm, now=None):
        self.owner = owner
        self.now = now or time.monotonic
        self.marker = Marker(nvm)
        self.phase = "idle"
        self.down_seen = False
        self.up_since = None
        self.previous_running = False
        if self.marker.armed():
            self.phase = "wait-usb"
            self.down_seen = True
            self._emit("armed-at-boot", "count=%d" % self.marker.count())

    @classmethod
    def from_root(cls, owner, nvm, **kwargs):
        # Stable construction API; timing no longer comes from plan.txt.
        return cls(owner, nvm, **kwargs)

    def _emit(self, event, detail=""):
        self.owner.emit("EVT|CYCLE|" + event + (("|" + detail) if detail else ""))

    def _begin_run(self, resumed=False):
        self.phase = "run"
        self.previous_running = True
        self._emit("resumed" if resumed else "armed",
                   "trigger=game-complete|count=%d" % self.marker.count())

    def _manual_stop(self):
        self.phase = "idle"
        self.marker.reset()
        self._emit("cancelled", "reason=manual-stop")

    def route_tick(self):
        # Long waits still call this hook, but there is no global deadline.
        return

    def _host_state(self):
        if getattr(self.owner.arm, "host_usb_seen", False):
            return getattr(self.owner.arm, "host_usb_state", None)
        try:
            import supervisor
            return "UP" if supervisor.runtime.usb_connected else "DOWN"
        except Exception:
            return None

    def _run_system_route(self, name):
        return self.owner.route({
            "execute": True,
            "route": name,
            "profile": None,
            "context": "after" if name == AFTER_ROUTE else "startup",
        })

    def _perform_after(self):
        if not self.marker.arm_next():
            self._emit("blocked", "reason=marker-or-limit")
            self.phase = "idle"
            self.owner.controls.running = False
            return
        self.phase = "after"
        self._emit("after-start", "route=%s|count=%d" %
                   (AFTER_ROUTE, self.marker.count()))
        self.owner.controls.start()
        try:
            completed = self._run_system_route(AFTER_ROUTE)
            if completed is False:
                raise RuntimeError("after aborted")
        except Exception as exc:
            self.marker.reset()
            self.phase = "idle"
            self.owner.controls.running = False
            self.owner.controls.aborted = True
            self._emit("failed", "stage=after|error=%s" % type(exc).__name__)
            return
        self.owner.controls.running = False
        self.owner.controls.aborted = True
        self.down_seen = False
        self.up_since = None
        self.phase = "wait-usb"
        self._emit("after-complete", "wait=usb-restart")

    def route_complete(self, name):
        if self.phase == "run" and name == "game_steps.txt":
            self._perform_after()

    def _startup(self):
        self.phase = "startup"
        self.owner.controls.start()
        self._emit("startup-start", "route=" + STARTUP_ROUTE)
        try:
            completed = self._run_system_route(STARTUP_ROUTE)
            if completed is False:
                raise RuntimeError("startup aborted")
        except Exception as exc:
            self.owner.controls.running = False
            self.owner.controls.aborted = True
            self.phase = "wait-usb"
            self.down_seen = False
            self.up_since = None
            self._emit("failed", "stage=startup|error=%s" % type(exc).__name__)
            return

        self.marker.clear_armed()
        self.owner.guard.reset()
        transition = getattr(self.owner.guard, "transition", None)
        if transition is not None:
            transition.stage = 1
        self.owner.guard.last_decision = None
        self.owner.debug_last_state = None
        self.owner.debug_last_denied = None
        self._emit("startup-complete", "next=login-or-dc|desktop=skip")
        self._begin_run(True)

    def tick(self):
        running = bool(self.owner.controls.running)
        if self.phase == "idle" and running and not self.previous_running:
            self.marker.reset()
            self._begin_run(False)
        elif self.phase == "run" and not running and self.previous_running:
            self._manual_stop()
        elif self.phase == "wait-usb":
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
            elif state == "UP" and self.down_seen:
                if self.up_since is None:
                    self.up_since = self.now()
                    self._emit("usb", "state=UP|startup-in=%d" % USB_STABLE_SECONDS)
                elif self.now() - self.up_since >= USB_STABLE_SECONDS:
                    self._startup()
        self.previous_running = bool(self.owner.controls.running)
