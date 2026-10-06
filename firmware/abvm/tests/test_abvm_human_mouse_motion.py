#!/usr/bin/env python3
"""v3.3 behavioural contract for the portable human-mouse engine.

Builds the real firmware translation unit with the host stubs, loops the ARM
side of the UART in software, then measures the emitted MMOVE stream:
- per-tick progress must jitter (the recorded hand never steps uniformly)
- flicks stay inside the recorded hand's reach (<= 80 px per tick)
- circular-authored moves keep their deep arc without a waypoint kink
- ordinary moves stay efficient (no drunken wandering)
- the first move after a long idle visibly warms up
- tiny twitches never burst
"""
import math
import statistics
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
FIXTURE = ROOT / "firmware/abvm/tests/abvm_human_mouse_smoke.amsj"


def run(cmd):
    subprocess.run(cmd, check=True)


def main():
    with tempfile.TemporaryDirectory() as tmp:
        abp = Path(tmp) / "hm.abp"
        log = Path(tmp) / "hm.log"
        exe = Path(tmp) / "hm-smoke"
        run([sys.executable, str(ROOT / "tools/abvm.py"), "compile",
             str(FIXTURE), str(abp), "--routes", "Game"])
        run(["cc", "-std=c11", "-Wall", "-Wextra", "-Werror",
             "-Ifirmware/abvm/tests/pico_stub",
             "-Ifirmware/abvm/include", "-Ifirmware/abvm/pico",
             "firmware/abvm/src/abvm_vm.c",
             "firmware/abvm/pico/arm_uart_mouse.c",
             "firmware/abvm/tests/abvm_human_mouse_smoke.c",
             "-o", str(exe)])
        run([str(exe), str(abp), str(log)])
        lines = log.read_text().splitlines()

    moves, cur = [], None
    for line in lines:
        if line.startswith("BEGIN|"):
            cur = {"start": int(line.split("|")[2]), "steps": []}
            moves.append(cur)
        elif line.startswith("END|"):
            cur["end"] = int(line.split("|")[1])
            cur = None
        elif line.startswith("M|") and cur is not None:
            _, t, dx, dy = line.split("|")
            cur["steps"].append((int(t), int(dx), int(dy)))

    assert len(moves) == 5, f"expected 5 fixture moves, got {len(moves)}"
    for move in moves:
        assert move["steps"], "every move must emit at least one tick"

    def metrics(move):
        steps = move["steps"]
        x = y = 0
        sizes, turns = [], []
        px = py = 0.0
        prev = None
        path = 0.0
        for _, dx, dy in steps:
            x += dx
            y += dy
            size = math.hypot(dx, dy)
            path += size
            sizes.append(size)
            if prev is not None and (dx or dy) and (prev[0] or prev[1]):
                cosine = ((prev[0] * dx + prev[1] * dy) /
                          (math.hypot(*prev) * size))
                turns.append(math.degrees(math.acos(max(-1.0, min(1.0, cosine)))))
            prev = (dx, dy)
        dist = math.hypot(x, y)
        efficiency = dist / path if path else 1.0
        gaps = [steps[i + 1][0] - steps[i][0] for i in range(len(steps) - 1)]
        return sizes, turns, dist, efficiency, gaps

    # Move 0: long ordinary move — jittered ticks, bounded flicks, no kink.
    sizes, turns, dist, eff, _ = metrics(moves[0])
    assert dist > 400, dist
    median = statistics.median(sizes)
    assert statistics.pstdev(sizes) > median * 0.3, "ticks must jitter"
    assert max(sizes) <= 80, f"flick exceeds hand reach: {max(sizes)}"
    assert max(turns) <= 90, f"ordinary join kink: {max(turns)}"
    assert eff >= 0.90, f"ordinary move wanders too much: {eff}"

    # Move 1: medium twitch stays small and calm.
    sizes, _, dist, _, _ = metrics(moves[1])
    assert dist < 100 and max(sizes) <= 12, (dist, max(sizes))

    # Move 2: micro twitch never bursts.
    sizes, _, dist, _, _ = metrics(moves[2])
    assert max(sizes) <= 8, max(sizes)

    # Move 3 follows the 4 s idle: opening ticks run visibly slower.
    _, _, dist, _, gaps = metrics(moves[3])
    assert dist > 600, dist
    head = sum(gaps[:6]) / 6
    tail = sum(gaps[-20:]) / 20
    assert head >= tail * 1.25, f"no warm-up after idle: {head} vs {tail}"

    # Move 4: authored 190..200 circular keeps a deep arc without a kink.
    sizes, turns, dist, eff, _ = metrics(moves[4])
    assert dist > 600, dist
    assert eff <= 0.90, f"circular arc flattened: {eff}"
    assert max(turns) <= 90, f"circular join kink: {max(turns)}"
    assert max(sizes) <= 80, max(sizes)

    print("v3.3 human-mouse motion contract passed")


if __name__ == "__main__":
    main()
