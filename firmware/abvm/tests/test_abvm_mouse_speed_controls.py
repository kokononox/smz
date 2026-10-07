import copy
import json
import math
import statistics
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools"))
import abvm

class MouseSpeedControlsTests(unittest.TestCase):
    def source(self, **kwargs):
        profile = {"DurationMs": 30000, "Version": 2,
                   "EncodedSample": "v1|30000|0,0|15000,0|" + ";".join(["8,4,0"] * 3750)}
        props = {"motionIntent": "mediumTwitch", "twitchMinPx": 75, "twitchMaxPx": 89,
                 "speedMode": "fast", "handProfileSource": "global", "speedCapPxPerSec": 500,
                 "pauseBeforeMin": 0, "pauseBeforeMax": 0, "pauseAfterMin": 0, "pauseAfterMax": 0,
                 "midPauseChance": 0, "idlePauseMax": 0, "overshootChance": 0,
                 "curveMinPct": 0, "curveMaxPct": 0}
        props.update(kwargs)
        return {"humanMouseProfile": profile, "pipelines": {"Game": [
            {"Type": "randomMousePosition", "Props": props, "Children": [], "Delay": 0}]}}

    def descriptor(self, source):
        image = abvm.Verifier.verify(abvm.Compiler().compile_amsj(source, ("Game",)).image)
        ins = next(i for i in image.instructions if i.op == abvm.OP_RMOUSE)
        return json.loads(image.const(ins.a, abvm.CONST_MOUSE))

    def test_missing_keys_keep_legacy(self):
        source = self.source()
        p = source["pipelines"]["Game"][0]["Props"]
        for k in ("speedMode", "handProfileSource", "speedCapPxPerSec"): p.pop(k)
        spec = self.descriptor(source)
        self.assertEqual(spec["speedControl"], 0)
        self.assertNotIn("speedCap", spec)

    def test_global_clears_stale_local_sample(self):
        spec = self.descriptor(self.source(handSample="stale"))
        self.assertEqual(spec["handSample"], "")
        self.assertEqual(spec["handProfileSourceId"], 0)
        self.assertEqual(spec["speedCap"], 500)

    def test_local_10_second_profile_is_resolved(self):
        sample = "v1|10000|0,0|5000,0|" + ";".join(["8,4,0"] * 1250)
        spec = self.descriptor(self.source(handProfileSource="local", handSample=sample))
        self.assertEqual(spec["handProfileSourceId"], 1)
        self.assertEqual(spec["handSpeedMax"], 500)

    def test_rejects_invalid_cap_weights_custom_or_mode(self):
        for patch in ({"speedCapPxPerSec": 501}, {"speedSlowWeight": -1},
                      {"speedSlowWeight": 0, "speedNormalWeight": 0, "speedFastWeight": 0},
                      {"speedMode": "custom", "speedCustomMin": 450, "speedCustomMax": 600},
                      {"speedMode": "unknown"}, {"handProfileSource": "legacy"}, {"handProfileSource": "local", "handSample": "invalid"}):
            with self.subTest(patch=patch), self.assertRaises(abvm.AbvmError):
                self.descriptor(self.source(**patch))

    def test_real_native_report_speed_cap_and_fast_selection(self):
        source = self.source()
        base = source["pipelines"]["Game"][0]
        nodes = []
        for mode in ("slow", "fast", "mixed", "custom"):
            for i in range(3):
                n = copy.deepcopy(base)
                n["Props"].update(speedMode=mode, speedTestId=len(nodes),
                                  speedSlowWeight=0, speedNormalWeight=0, speedFastWeight=100,
                                  speedCustomMin=450, speedCustomMax=500)
                nodes.append(n)
        source["pipelines"]["Game"] = nodes
        with tempfile.TemporaryDirectory() as directory:
            directory = Path(directory)
            src, program, log, exe = [directory / n for n in ("source.amsj", "program.abp", "motion.log", "motion-test")]
            src.write_text(json.dumps(source))
            subprocess.run([sys.executable, str(ROOT/"tools/abvm.py"), "compile", str(src), str(program), "--routes", "Game"], check=True, capture_output=True)
            subprocess.run(["cc", "-std=c11", "-Wall", "-Wextra", "-Werror", "-Ifirmware/abvm/tests/pico_stub", "-Ifirmware/abvm/include", "-Ifirmware/abvm/pico", "firmware/abvm/src/abvm_vm.c", "firmware/abvm/pico/arm_uart_mouse.c", "firmware/abvm/tests/abvm_human_mouse_smoke.c", "-o", str(exe)], cwd=ROOT, check=True, capture_output=True)
            output = subprocess.run([str(exe), str(program), str(log)], check=True, capture_output=True, text=True).stdout
            moves, current = [], None
            for line in log.read_text().splitlines():
                fields = line.split("|")
                if fields[0] == "BEGIN":
                    current = {"start": int(fields[2]), "points": []}; moves.append(current)
                elif fields[0] == "M": current["points"].append(tuple(map(int, fields[1:])))
                elif fields[0] == "END": current["end"] = int(fields[1])
        self.assertEqual(len(moves), 12)
        measured = []
        for move in moves:
            previous = move["start"]; distance = 0
            for time, dx, dy in move["points"]:
                length = math.hypot(dx, dy); gap = time-previous
                self.assertLessEqual(dx*dx+dy*dy, 9)
                self.assertGreater(gap, 0)
                self.assertLessEqual(length*1000/gap, 500.001)
                previous = time; distance += length
            measured.append(distance*1000/(move["end"]-move["start"]))
        self.assertGreater(statistics.mean(measured[3:6]), statistics.mean(measured[:3]))
        self.assertEqual(output.count("EVT|MOUSE|"), 12)
        # Both explicit fast and 100%-fast weighted mode resolve to native mode 4.
        self.assertEqual(output.count("mode=4|"), 6)

if __name__ == "__main__": unittest.main()
