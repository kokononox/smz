#!/usr/bin/env python3
"""Route-driven After/Startup cycle regressions."""
import sys
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[3]
FW = ROOT / "portable/plan3/CIRCUITPY-MODERN"
sys.path.insert(0, str(FW))
from restart_cycle import Controller, Marker

nvm = bytearray(4096)
marker = Marker(nvm)
assert marker.available() and not marker.armed() and marker.count() == 0
assert marker.arm_next() and marker.armed() and marker.count() == 1
marker.clear_armed(); assert not marker.armed() and marker.count() == 1
marker.arm_next(); assert marker.count() == 2
marker.reset(); assert not marker.armed() and marker.count() == 0
assert bytes(nvm[:1536]) == b"\x00" * 1536

class Clock:
    def __init__(self): self.value = 0
    def __call__(self): return self.value

class Controls:
    def __init__(self): self.running=False; self.aborted=False
    def start(self): self.running=True; self.aborted=False
    def stop(self): self.running=False; self.aborted=True

class Owner:
    def __init__(self):
        self.events=[]; self.routes=[]; self.controls=Controls()
        self.arm=SimpleNamespace(host_usb_seen=True, host_usb_state="UP")
        self.guard=SimpleNamespace(reset=lambda: None, last_decision=None,
            transition=SimpleNamespace(stage=None))
        self.debug_last_state="game"; self.debug_last_denied=None
    def emit(self,line): self.events.append(line)
    def route(self,decision): self.routes.append(decision["route"]); return True

clock=Clock(); owner=Owner(); cycle=Controller(
    owner,nvm,now=clock,run_for=(110 * 60,130 * 60),
    choose=lambda minimum, maximum: minimum)
owner.controls.start(); cycle.tick()
assert cycle.phase == "run"
assert cycle.deadline == 110 * 60
cycle.route_tick(); assert owner.controls.running
clock.value = 110 * 60
cycle.route_tick()
assert cycle.deadline_expired and not owner.controls.running
# After is deferred until the route stack has unwound.
cycle.tick()
assert owner.routes == ["restart_steps.txt"]
assert cycle.phase == "wait-usb" and cycle.marker.armed()

owner.arm.host_usb_state="DOWN"; cycle.tick()
owner.arm.host_usb_state="UP"; cycle.tick()
assert any("startup-in=2" in event for event in owner.events)
clock.value += 2; cycle.tick()
assert owner.routes == ["restart_steps.txt", "startup_steps.txt"]
assert cycle.phase == "run" and not cycle.marker.armed()
assert owner.guard.transition.stage == 1
assert any("desktop=skip" in event for event in owner.events)

# Natural Game completion still starts After before the selected deadline.
marker.reset(); clock.value = 0
natural_owner=Owner()
natural=Controller(natural_owner,nvm,now=clock,run_for=(6600,7800),
    choose=lambda minimum, maximum: maximum)
natural_owner.controls.start(); natural.tick()
natural.route_complete("game_steps.txt")
assert natural_owner.routes == ["restart_steps.txt"]
assert natural.phase == "wait-usb"

# On affected Windows hardware a warm restart keeps USB configured/UP for the
# whole reboot, so there is no DOWN edge to observe. The armed marker must
# still start the Startup route instead of deadlocking until a manual Start.
natural.tick()
assert any("source=marker-no-down" in event for event in natural_owner.events)
clock.value += 2; natural.tick()
assert natural_owner.routes == ["restart_steps.txt", "startup_steps.txt"]
assert natural.phase == "run" and not natural.marker.armed()

# USB may go DOWN and back UP while the After route is still finishing. That
# evidence must survive route completion so Startup cannot remain stuck.
marker.reset(); clock.value = 0
race_owner=Owner()
race=Controller(race_owner,nvm,now=clock,run_for=(6600,7800),
    choose=lambda minimum, maximum: minimum)
race_owner.controls.start(); race.tick()
race.marker.arm_next(); race.phase = "after"
race_owner.arm.host_usb_state = "DOWN"; race.route_tick()
clock.value += 1
race_owner.arm.host_usb_state = "UP"; race.route_tick()
assert race.down_seen and race.up_since == 1
race.phase = "wait-usb"
clock.value += 2; race.tick()
assert race_owner.routes == ["startup_steps.txt"]
assert race.phase == "run"

# NVM marker survives a Pico reboot and authorizes Startup once.
marker.reset(); marker.arm_next(); boot_owner=Owner()
boot=Controller(boot_owner,nvm,now=clock,run_for=(6600,7800),
    choose=lambda minimum, maximum: minimum)
assert boot.phase == "wait-usb" and boot.down_seen
boot.tick(); clock.value += 2; boot.tick()
assert boot_owner.routes == ["startup_steps.txt"]

print("modern restart cycle: 110-130 deadline, persistent Startup and Desktop skip passed")
