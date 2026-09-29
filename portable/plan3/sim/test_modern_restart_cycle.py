#!/usr/bin/env python3
"""Hardware-selected A resume plus deadline-only After regressions."""
import sys
from pathlib import Path
from types import SimpleNamespace
ROOT = Path(__file__).resolve().parents[3]
FW = ROOT / "portable/plan3/CIRCUITPY-MODERN"
sys.path.insert(0, str(FW))
from restart_cycle import Controller, Marker

class Clock:
    def __init__(self): self.value = 0
    def __call__(self): return self.value
class Controls:
    def __init__(self): self.running = False; self.aborted = False
    def start(self): self.running = True; self.aborted = False
    def stop(self): self.running = False; self.aborted = True
class Guard:
    def __init__(self):
        self.resets = 0; self.last_decision = None
        self.transition = SimpleNamespace(stage=None)
    def reset(self):
        self.resets += 1; self.last_decision = None; self.transition.stage = None
class Owner:
    def __init__(self):
        self.events = []; self.routes = []; self.controls = Controls()
        self.arm = SimpleNamespace(host_usb_seen=True, host_usb_state="UP")
        self.guard = Guard(); self.debug_last_state = "game"; self.debug_last_denied = "old"
    def emit(self, line): self.events.append(line)
    def route(self, decision): self.routes.append(decision["route"]); return True

def controller(owner, nvm, clock, low=180, high=360):
    return Controller(owner, nvm, now=clock, run_for=(low, high),
                      choose=lambda minimum, maximum: minimum)

nvm = bytearray(4096); marker = Marker(nvm)
assert marker.available() and not marker.armed() and marker.count() == 0
assert marker.arm_next() and marker.armed() and marker.count() == 1
marker.clear_armed(); assert not marker.armed() and marker.count() == 1
marker.arm_next(); assert marker.count() == 2
marker.reset(); assert not marker.armed() and marker.count() == 0
assert bytes(nvm[:1536]) == b"\x00" * 1536

# Cast/Splash/Game completion re-enters Game and preserves the cycle deadline.
clock = Clock(); owner = Owner(); cycle = controller(owner, nvm, clock)
owner.controls.start(); cycle.tick(); deadline = cycle.deadline
cycle.route_complete("game_steps.txt")
assert cycle.phase == "run" and cycle.deadline == deadline
assert owner.routes == [] and not cycle.marker.armed()
assert owner.guard.resets == 1 and owner.debug_last_state == "__game-pass__"
assert any("game-complete|action=continue|after=deadline-only" in x for x in owner.events)

# Only RUNFOR expiry enters After.
clock.value = deadline; cycle.route_tick()
assert cycle.deadline_expired and not owner.controls.running
cycle.tick()
assert owner.routes == ["restart_steps.txt"]
assert cycle.phase == "wait-usb" and cycle.marker.armed()

# Variant A: marker + USB fused, including the no-DOWN case.
cycle.tick()
assert any("source=marker-no-down" in x for x in owner.events)
clock.value += 2; cycle.tick()
assert owner.routes == ["restart_steps.txt", "startup_steps.txt"]
assert cycle.phase == "run" and not cycle.marker.armed()
assert owner.guard.transition.stage == 1
assert any("desktop=skip" in x for x in owner.events)

# Armed marker at boot resumes through the same A path.
marker.reset(); marker.arm_next(); boot_owner = Owner(); boot = controller(boot_owner, nvm, clock)
assert boot.phase == "wait-usb" and boot.down_seen
boot.tick(); clock.value += 2; boot.tick()
assert boot_owner.routes == ["startup_steps.txt"] and boot.phase == "run"

# Stale Pro Micro DOWN is promoted by Pico UP when the marker exists.
marker.reset(); marker.arm_next()
sys.modules["supervisor"] = SimpleNamespace(runtime=SimpleNamespace(usb_connected=True))
stale_owner = Owner(); stale_owner.arm.host_usb_state = "DOWN"
stale = controller(stale_owner, nvm, clock)
stale.tick(); clock.value += 2; stale.tick()
assert stale_owner.routes == ["startup_steps.txt"]
sys.modules.pop("supervisor", None)

# DOWN/UP observed while After finishes remains valid.
marker.reset(); clock.value = 0; race_owner = Owner(); race = controller(race_owner, nvm, clock)
race_owner.controls.start(); race.tick(); race.marker.arm_next(); race.phase = "after"
race_owner.arm.host_usb_state = "DOWN"; race.route_tick(); clock.value += 1
race_owner.arm.host_usb_state = "UP"; race.route_tick()
assert race.down_seen and race.up_since == 1
race.phase = "wait-usb"; clock.value += 2; race.tick()
assert race_owner.routes == ["startup_steps.txt"]

source = (FW / "restart_cycle.py").read_text(encoding="utf-8")
assert "wait-host-cdc" not in source and "microcontroller.reset()" not in source
assert "RESET_DONE" not in source
assert 'self._emit("game-complete", "action=continue|after=deadline-only")' in source
print("modern restart cycle: A marker/USB resume and deadline-only After passed")
