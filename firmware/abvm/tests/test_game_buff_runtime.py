"""Compile and run the actual C scheduler; not a Python timing imitation."""
import pathlib
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[3]


class BuffRuntimeTests(unittest.TestCase):
    def test_actual_pico_scheduler(self):
        with tempfile.TemporaryDirectory() as directory:
            executable = pathlib.Path(directory) / "buff-test"
            subprocess.run([
                "cc", "-std=c11", "-Wall", "-Wextra", "-Werror",
                "-I" + str(ROOT / "firmware/abvm/pico"),
                str(ROOT / "firmware/abvm/pico/game_buff_runtime.c"),
                str(ROOT / "firmware/abvm/tests/game_buff_runtime_smoke.c"),
                "-o", str(executable),
            ], check=True)
            subprocess.run([str(executable)], check=True)


if __name__ == "__main__":
    unittest.main()