import pathlib
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools"))
import abvm


def source(alarm_after=2):
    return {
        "pipelines": {
            "Game": [{
                "Type": "forLoop",
                "Props": {"mode": "infinite"},
                "Children": [{
                    "Type": "waitForSound",
                    "Props": {
                        "calibrationId": 2,
                        "threshold": 40,
                        "minDurationMs": 20,
                        "timeoutMinSec": 1,
                        "timeoutMaxSec": 1,
                        "timeoutAlarmAfter": alarm_after,
                    },
                    "Children": [{
                        "Type": "keystroke",
                        "Props": {"key": "F", "holdMin": 10, "holdMax": 10},
                        "Children": [],
                        "Delay": 0,
                    }],
                    "Delay": 0,
                }],
                "Delay": 0,
            }],
        },
    }


class SoundTimeoutAlarmTests(unittest.TestCase):
    def test_extended_descriptor_is_opt_in_and_validated(self):
        default = abvm.Verifier.verify(
            abvm.Compiler().compile_amsj(source(1), ("Game",)).image)
        default_watch = next(i for i in default.instructions
                             if i.op == abvm.OP_WATCH)
        self.assertEqual(len(default.const(default_watch.a, abvm.CONST_SOUND)), 8)

        image = abvm.Verifier.verify(
            abvm.Compiler().compile_amsj(source(2), ("Game",)).image)
        watch = next(i for i in image.instructions if i.op == abvm.OP_WATCH)
        descriptor = image.const(watch.a, abvm.CONST_SOUND)
        self.assertEqual(abvm.SOUND_ALARM.unpack(descriptor), (2, 40, 20, 2))

        for value in (0, 256):
            with self.assertRaises(abvm.AbvmError):
                abvm.Compiler().compile_amsj(source(value), ("Game",))

    def test_native_vm_alarms_on_second_miss_and_catch_resets(self):
        program = abvm.Compiler().compile_amsj(
            source(2), ("Game",)).image
        with tempfile.TemporaryDirectory() as temp:
            temp_path = pathlib.Path(temp)
            image_path = temp_path / "watch-alarm.abp"
            image_path.write_bytes(program)
            executable = temp_path / "watch-alarm-smoke"
            subprocess.run([
                "cc", "-std=c11", "-Wall", "-Wextra", "-Werror",
                "-I" + str(ROOT / "firmware/abvm/include"),
                str(ROOT / "firmware/abvm/tests/watch_alarm_smoke.c"),
                str(ROOT / "firmware/abvm/src/abvm_vm.c"),
                "-o", str(executable),
            ], check=True)
            subprocess.run([str(executable), str(image_path)], check=True)


if __name__ == "__main__":
    unittest.main()