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
assert not marker.reset_done()
marker.mark_reset_done(); assert marker.reset_done()
marker.clear_armed(); assert not marker.armed() and marker.count() == 1
assert not marker.reset_done()
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
        self.events=[]; self.routes=[]; self.tones=[]; self.controls=Controls()
        self.arm=SimpleNamespace(host_usb_seen=True, host_usb_state="UP")
        self.usb=SimpleNamespace(connected=True)
        self.guard=SimpleNamespace(reset=lambda: None, last_decision=None,
            transition=SimpleNamespace(stage=None))
        self.debug_last_state="game"; self.debug_last_denied=None
    def emit(self,line): self.events.append(line)
    def route(self,decision): self.routes.append(decision["route"]); return True
    def _cal_beep(self,frequency,duration): self.tones.append((frequency,duration))

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
assert cycle.phase == "wait-host-cdc" and cycle.marker.armed()
assert owner.tones[-1] == (330, 220)

# Windows CDC disconnect/reconnect authorizes one Pico reset. Resetting after
# the host returns makes CIRCUITPY enumerate read/write again.
owner.usb.connected = False; cycle.tick()
owner.usb.connected = True; cycle.tick()
clock.value += 5
class ResetNow:
    @staticmethod
    def reset(): raise SystemExit("simulated Pico reset")
sys.modules["microcontroller"] = ResetNow
try:
    cycle.tick()
except SystemExit:
    pass
assert cycle.marker.armed() and cycle.marker.reset_done()
assert any("source=cdc-reconnected" in event for event in owner.events)
assert owner.tones[-2:] == [(659, 160), (988, 260)]

# Fresh Pico boot consumes the reset-done marker, runs Startup once, and then
# returns to the normal light-driven cycle.
resumed_owner=Owner()
resumed=Controller(resumed_owner,nvm,now=clock,run_for=(6600,7800),
    choose=lambda minimum, maximum: minimum)
assert resumed.phase == "wait-usb" and resumed.down_seen
assert resumed_owner.tones == [(1175, 100), (1175, 100), (1568, 220)]
resumed.tick(); clock.value += 2; resumed.tick()
assert resumed_owner.routes == ["startup_steps.txt"]
assert resumed.phase == "run" and not resumed.marker.armed()
assert resumed_owner.guard.transition.stage == 1
assert any("desktop=skip" in event for event in resumed_owner.events)
assert resumed_owner.tones[-3:] == [(880, 160), (1175, 220), (1568, 360)]
sys.modules.pop("microcontroller", None)

# Natural Game completion still starts After before the selected deadline.
marker.reset(); clock.value = 0
natural_owner=Owner()
natural=Controller(natural_owner,nvm,now=clock,run_for=(6600,7800),
    choose=lambda minimum, maximum: maximum)
natural_owner.controls.start(); natural.tick()
natural.route_complete("game_steps.txt")
assert natural_owner.routes == ["restart_steps.txt"]
assert natural.phase == "wait-host-cdc"

# After the intentional Pico reset, a stale Pro Micro DOWN must not mask Pico
# UP or prevent the marker-authorized Startup.
marker.reset(); clock.value = 0
marker.arm_next(); marker.mark_reset_done()
sys.modules["supervisor"] = SimpleNamespace(
    runtime=SimpleNamespace(usb_connected=True))
stale_owner=Owner(); stale_owner.arm.host_usb_state = "DOWN"
stale=Controller(stale_owner,nvm,now=clock,run_for=(6600,7800),
    choose=lambda minimum, maximum: minimum)
stale.tick()
assert any("startup-in=2" in event for event in stale_owner.events)
clock.value += 2; stale.tick()
assert stale_owner.routes == ["startup_steps.txt"]
assert stale.phase == "run"
sys.modules.pop("supervisor", None)

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

# An armed marker without reset-done waits for CDC; the 120-second fallback
# still resets the Pico if DTR reconnect is missed.
marker.reset(); marker.arm_next(); boot_owner=Owner()
boot=Controller(boot_owner,nvm,now=clock,run_for=(6600,7800),
    choose=lambda minimum, maximum: minimum)
assert boot.phase == "wait-host-cdc"
clock.value += 120
sys.modules["microcontroller"] = ResetNow
try:
    boot.tick()
except SystemExit:
    pass
assert boot.marker.reset_done()
assert any("source=timeout-fallback" in event for event in boot_owner.events)
sys.modules.pop("microcontroller", None)

print("modern restart cycle: CDC remount reset, persistent Startup and Desktop skip passed")
