#!/usr/bin/env python3
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(root / "tools"))
import abvm


def route(*nodes):
    return list(nodes)


key = {
    "Type": "keystroke",
    "Props": {"key": "ENTER", "holdMin": 55, "holdMax": 120},
    "Children": [],
    "Delay": 0,
}
source = {
    "pipelines": {
        # Current AMSJ documents serialize the approved After tab as Launch;
        # the Native compiler must preserve its Restart route-ID alias.
        "Launch": route(key),
        "Startup": route({
            "Type": "delay",
            "Props": {"minMs": 30_000, "maxMs": 60_000},
            "Children": [],
            "Delay": 0,
        }),
    },
    "nativeCycle": {
        "enabled": True,
        "autoResume": True,
        "runMinSeconds": 110 * 60,
        "runMaxSeconds": 130 * 60,
        "maxRestarts": 5,
        "usbStableMs": 2000,
    },
}
program = abvm.Compiler().compile_amsj(source, ("Restart", "Startup"))
image = abvm.Verifier.verify(program.image)
cycles = [payload for kind, _, payload in image.constants
          if kind == abvm.CONST_CYCLE]
assert len(cycles) == 1
version, flags, limit, reserved, run_min, run_max, after, startup, stable = \
    abvm.CYCLE.unpack(cycles[0])
assert (version, flags, limit, reserved) == (1, 1, 5, 0)
assert (run_min, run_max) == (110 * 60_000, 130 * 60_000)
assert (after, startup, stable) == (2, 3, 2000)
assert {item.route_id for item in image.routes} == {2, 3}
assert program.flags & abvm.FLAG_HAS_CYCLE

pico = root / "firmware/abvm/pico"
cycle = (pico / "cycle_runtime.c").read_text(encoding="utf-8")
main = (pico / "main.c").read_text(encoding="utf-8")
arm = (pico / "arm_uart_mouse.c").read_text(encoding="utf-8")
store = (pico / "calibration_store.c").read_text(encoding="utf-8")
guard = (pico / "guard_runtime.c").read_text(encoding="utf-8")

assert "calibration_store_cycle_arm_next" in cycle
assert "CYCLE_ACTION_EXPIRE" in cycle and "CYCLE_ACTION_START_STARTUP" in cycle
assert "cycle.down_seen=true" in cycle
assert "EVT|CYCLE|deadline|action=after" in main
assert "startup-complete|next=login-or-dc|desktop=skip" in main
assert "EVT|HOSTUSB|DOWN" in arm and "EVT|HOSTUSB|SUSPEND" in arm
assert "EVT|HOSTUSB|UP" in arm
assert "cycle_armed" in store and "cycle_count" in store
assert "guard_runtime_start_after_restart" in guard and "guard.stage = 1u" in guard

print("native approved After/Startup cycle: 22 passed, 0 failed")