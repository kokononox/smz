import copy
import pathlib
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools"))
import abvm


def node(kind, props=None, children=None, **extra):
    return {"Type": kind, "Props": props or {}, "Children": children or [], **extra}


def project():
    buff = node("keystroke", {
        "key": "8", "keyboardBoard": "pico", "holdMin": 10, "holdMax": 10,
        "renewMinMinutes": 0.01, "renewMaxMinutes": 0.02,
        "beforeMinMs": 50, "beforeMaxMs": 50,
    }, Delay=100, DelayMax=100)
    return {
        "gameBuffs": [buff],
        "pipelines": {
            "Game": [node("forLoop", {"mode": "infinite"}, [
                node("buffCheckpoint"),
                node("keystroke", {"key": "7", "holdMin": 10, "holdMax": 10}),
                node("waitForSound", {
                    "calibrationId": 2, "threshold": 40, "minDurationMs": 20,
                    "timeoutMinSec": 1, "timeoutMaxSec": 1, "armCuePreset": "off",
                }, [node("keystroke", {"key": "F", "holdMin": 10, "holdMax": 10})]),
            ])],
            "Whisper": [node("delay", {"minMs": 20, "maxMs": 20})],
        },
    }


class GameBuffCompilerTests(unittest.TestCase):
    def test_binary_descriptor_and_initial_checkpoint(self):
        result = abvm.Compiler().compile_amsj(project())
        image = abvm.Verifier.verify(result.image)
        route = image.route("Game")
        first = image.instructions[route.pc]
        self.assertEqual((first.op, first.flags), (abvm.OP_BUFF, 1))
        payload = image.const(first.a, abvm.CONST_BUFF)
        self.assertEqual(len(payload), 48)
        self.assertEqual(abvm.BUFF_HEADER.unpack_from(payload), (b"GBF1", 1, 1, 0))
        self.assertEqual(abvm.BUFF_ROW.unpack_from(payload, 8),
                         (56, 1, 600, 1200, 50, 50, 10, 10, 100, 100))
        codes = image.instructions[route.pc:route.pc + route.length]
        watch = next(n for n in codes if n.op == abvm.OP_WATCH)
        self.assertEqual(image.instructions[watch.d].op, abvm.OP_LOOP_NEXT)
        self.assertEqual(codes[2].op, abvm.OP_BUFF)

    def test_legacy_project_remains_opt_out(self):
        data = project(); data.pop("gameBuffs")
        image = abvm.Verifier.verify(abvm.Compiler().compile_amsj(data).image)
        self.assertFalse(any(i.op == abvm.OP_BUFF for i in image.instructions))

    def test_missing_safe_boundary_rejected(self):
        data = project(); data["pipelines"]["Game"][0]["Children"].pop(0)
        with self.assertRaisesRegex(abvm.AbvmError, "safe buffCheckpoint"):
            abvm.Compiler().compile_amsj(data)

    def test_invalid_interval_combo_and_delay_rejected(self):
        for key, value in [("renewMinMinutes", -1), ("holdMin", 0),
                           ("beforeMinMs", 60), ("renewMaxMinutes", 0)]:
            data = project(); data["gameBuffs"][0]["Props"][key] = value
            with self.assertRaises(abvm.AbvmError):
                abvm.Compiler().compile_amsj(data)
        data = project(); data["gameBuffs"] *= 17
        with self.assertRaises(abvm.AbvmError):
            abvm.Compiler().compile_amsj(data)

    def test_old_template_cannot_silently_drop_buffs(self):
        sys.path.insert(0,str(ROOT / "firmware/abvm/tests"))
        from test_abvm_uf2 import make_uf2
        from abvm_uf2 import inject, Uf2Error
        result=abvm.Compiler().compile_amsj(project())
        template=make_uf2(b"ABP1old",capacity=16384)
        with self.assertRaisesRegex(Uf2Error,"lacks the Pico buff runtime"):
            inject(template,result.image)
        marked=bytearray(template)
        marker=b"EVT|BUFF|state=new-game|count=%u"
        marked[32:32+len(marker)]=marker
        output,info=inject(bytes(marked),result.image)
        self.assertEqual(info["program_size"],len(result.image))

    def test_pico_vm_keyboard_adapter(self):
        with tempfile.TemporaryDirectory() as directory:
            directory = pathlib.Path(directory)
            program = directory / "buff.abp"
            program.write_bytes(abvm.Compiler().compile_amsj(project()).image)
            # Exercise the production main.c adapter, not a reimplemented timer.
            main = (ROOT / "firmware/abvm/pico/main.c").read_text()
            adapter = main[main.index("static void service_game_buffs("):
                           main.index("static void service_vm(")]
            harness = (ROOT / "firmware/abvm/tests/game_buff_adapter_smoke.c").read_text()
            source = directory / "adapter.c"
            source.write_text(harness.replace("/* PRODUCTION_ADAPTER */", adapter))
            executable = directory / "adapter"
            subprocess.run([
                "cc", "-std=c11", "-Wall", "-Wextra", "-Werror",
                "-I" + str(ROOT / "firmware/abvm/tests/pico_stub"),
                "-I" + str(ROOT / "firmware/abvm/include"),
                "-I" + str(ROOT / "firmware/abvm/pico"),
                str(source), str(ROOT / "firmware/abvm/src/abvm_vm.c"),
                str(ROOT / "firmware/abvm/pico/game_buff_runtime.c"),
                str(ROOT / "firmware/abvm/pico/hid_keyboard.c"),
                "-o", str(executable),
            ], check=True)
            subprocess.run([str(executable), str(program)], check=True,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE)


if __name__ == "__main__":
    unittest.main()