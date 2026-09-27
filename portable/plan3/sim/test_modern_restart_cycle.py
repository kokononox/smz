#!/usr/bin/env python3
"""Modern cycle parser, NVM marker, deadline and USB resume regressions."""
import sys
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[3]
FW = ROOT / "portable/plan3/CIRCUITPY-MODERN"
sys.path.insert(0, str(FW))

from restart_cycle import Controller, Marker, parse_policy


policy = parse_policy(
    "PLAN|2\n"
    "RUNFOR|300,600\n"
    "AUTORESUME|1,180,300\n"
    "POSTLAUNCH|0,1,1,3,20,40\n"
    "DELAY|1\n"
)
assert policy["run"] == (300, 600)
assert policy["auto"] == (True, 180, 300)
assert policy["launch"] == (False, 1, (1, 3), (20, 40))

for bad in (
    "PLAN|2\nRUNFOR|300,600\nDELAY|1\nAUTORESUME|1,180,300\n",
    "PLAN|2\nRUNFOR|0,600\nAUTORESUME|1,180,300\n",
    "PLAN|2\nAUTORESUME|1,180,300\n",
):
    try:
        parse_policy(bad)
        raise AssertionError("invalid cycle plan accepted")
    except ValueError:
        pass

nvm = bytearray(4096)
marker = Marker(nvm)
assert marker.available() and not marker.armed() and marker.count() == 0
assert marker.arm_next() and marker.armed() and marker.count() == 1
marker.clear_armed()
assert not marker.armed() and marker.count() == 1
assert marker.arm_next() and marker.count() == 2
marker.reset()
assert not marker.armed() and marker.count() == 0
assert bytes(nvm[:1536]) == b"\x00" * 1536


class Clock:
    def __init__(self):
        self.value = 0

    def __call__(self):
        return self.value


class Fixed:
    def randint(self, low, high):
        return low


class Owner:
    def __init__(self):
        self.events = []
        self.controls = SimpleNamespace(running=False, aborted=False)
        self.keyboard = SimpleNamespace(release_all=lambda: None)
        self.arm = SimpleNamespace(host_usb_seen=True, host_usb_state="UP")
        self.guard = SimpleNamespace(reset=lambda: None, last_decision=None)
        self.debug_last_state = "game"

    def emit(self, line):
        self.events.append(line)

    def plan_context(self):
        return SimpleNamespace()


clock = Clock()
owner = Owner()
controller = Controller(owner, nvm, policy, now=clock, rng=Fixed())
owner.controls.running = True
controller.tick()
assert controller.phase == "run" and controller.deadline == 300
clock.value = 300
controller.route_tick()
assert controller.due and not owner.controls.running and owner.controls.aborted

# A marker surviving reboot starts in wait-usb. It must observe/rely on a real
# disconnect before scheduling the configured 180-second resume delay.
marker.arm_next()
boot_owner = Owner()
boot = Controller(boot_owner, nvm, policy, now=clock, rng=Fixed())
assert boot.phase == "wait-usb" and boot.down_seen
boot_owner.arm.host_usb_state = "UP"
boot.tick()
assert boot.resume_at == clock.value + 182
assert any("resume-in=182" in event for event in boot_owner.events)

# A deliberate GP4/host Start while waiting cancels the stale marker and
# immediately creates a fresh RUNFOR deadline instead of running two sessions.
boot_owner.controls.running = True
boot.previous_running = False
boot.tick()
assert boot.phase == "run" and not boot.marker.armed()
assert any("manual-override" in event for event in boot_owner.events)

print("modern restart cycle: parser, NVM, deadline and USB resume passed")