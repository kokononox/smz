import unittest
from pathlib import Path
import sys
import sys


class AbvmBuzzerContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root = Path(__file__).resolve().parents[3]
        cls.pico = cls.root / "firmware" / "abvm" / "pico"
        cls.buzzer = (cls.pico / "buzzer.c").read_text(encoding="utf-8")
        cls.compiler = (cls.root / "tools" / "abvm.py").read_text(encoding="utf-8")
        cls.main = (cls.pico / "main.c").read_text(encoding="utf-8")
        cls.cmake = (cls.pico / "CMakeLists.txt").read_text(encoding="utf-8")

    def test_passive_buzzer_uses_gp6_hardware_pwm(self):
        self.assertIn("#define BUZZER_PIN 6u", self.buzzer)
        self.assertIn("GPIO_FUNC_PWM", self.buzzer)
        self.assertIn("hardware_pwm", self.cmake)
        self.assertIn("buzzer=legacy-calibration-gp6", self.main)

    def test_native_buzzer_step_has_bounded_bytecode_and_actor(self):
        self.assertIn('"BEEP": OP_BEEP', self.compiler)
        self.assertIn('elif kind == "buzzer":', self.compiler)
        self.assertIn("self.emit(OP_BEEP, a=frequency, b=duration, c=tone_style)", self.compiler)
        self.assertIn("pwm_channels=1 if self.uses_pwm else 0", self.compiler)
        self.assertIn("event.opcode==ABVM_OP_BEEP", self.main)
        self.assertIn("service_buzzer_action(now)", self.main)
        self.assertIn("buzzer_play_tone", self.buzzer)

    def test_native_buzzer_presets_compile_and_verify(self):
        sys.path.insert(0, str(self.root / "tools"))
        import abvm
        source = {"pipelines": {"Game": [
            {"Type": "buzzer", "Props": {"preset": "success"},
             "Children": [], "Delay": 0},
            {"Type": "buzzer", "Props": {"preset": "warning"},
             "Children": [], "Delay": 0},
        ]}}
        program = abvm.Compiler().compile_amsj(source, ("Game",))
        image = abvm.Verifier.verify(program.image)
        beeps = [(ins.a, ins.b) for ins in image.instructions
                 if ins.op == abvm.OP_BEEP]
        self.assertEqual(
            beeps,
            [(900, 120), (1300, 220), (700, 180),
             (700, 180), (700, 300)])
        self.assertEqual(image.resources.pwm_channels, 1)

    def test_native_buzzer_volume_envelope_and_extra_presets(self):
        sys.path.insert(0, str(self.root / "tools"))
        import abvm
        source = {"pipelines": {"Game": [
            {"Type": "buzzer", "Props": {
                "preset": "notification", "volume": 42, "envelope": "smooth"},
             "Children": [], "Delay": 0},
            {"Type": "buzzer", "Props": {
                "preset": "falling", "volume": 80, "envelope": "fade-out"},
             "Children": [], "Delay": 0},
        ]}}
        program = abvm.Compiler().compile_amsj(source, ("Game",))
        image = abvm.Verifier.verify(program.image)
        beeps = [(ins.a, ins.b, ins.c) for ins in image.instructions
                 if ins.op == abvm.OP_BEEP]
        self.assertEqual(
            beeps,
            [(880, 110, 42 | (1 << 8)), (1175, 170, 42 | (1 << 8)),
             (784, 90, 80 | (3 << 8)), (659, 90, 80 | (3 << 8)),
             (523, 160, 80 | (3 << 8))])
        self.assertEqual(image.resources.pwm_channels, 1)

    def test_pattern_engine_is_nonblocking_and_bounded(self):
        self.assertIn("static const BuzzerPattern patterns[]", self.buzzer)
        self.assertIn("next_priority < priority", self.buzzer)
        self.assertIn("void buzzer_service(uint32_t now)", self.buzzer)
        self.assertIn("{784,160,0},{988,160,0},{1175,200,80},{1175,280,0}", self.buzzer)
        self.assertIn("{392,180,0},{330,160,0},{262,260,60},{196,260,0}", self.buzzer)
        self.assertIn("{523,180,100},{523,180,100},{523,340,0}", self.buzzer)
        self.assertIn("{659,150,0},{784,150,0},{988,150,0},{784,150,0},{988,300,0}", self.buzzer)
        self.assertIn("{262,294,330,349,392,440}", self.buzzer)
        self.assertIn("{880,160,60},{1175,220,60},{1568,360,0}", self.buzzer)
        self.assertIn("{220,140,80},{220,260,0}", self.buzzer)
        self.assertNotIn("sleep_ms", self.buzzer)
        self.assertNotIn("malloc", self.buzzer)

    def test_full_operational_cues_are_wired(self):
        for cue in ("BUZZER_CUE_START", "BUZZER_CUE_STOP",
                    "BUZZER_CUE_PAUSE", "BUZZER_CUE_RESUME",
                    "BUZZER_CUE_CATCH", "BUZZER_CUE_TIMEOUT",
                    "BUZZER_CUE_ERROR"):
            self.assertIn(cue, self.main)
        self.assertIn("service_calibration_cue(calibration_event, now)", self.main)
        self.assertIn("buzzer_guard_transition(guard_event.profile_id, now)", self.main)
        self.assertIn("start-at-current-state", self.main)
        self.assertIn("TRANSITION_UPDATE_MS 4u", self.buzzer)
        self.assertIn("ENVELOPE_UPDATE_MS 4u", self.buzzer)
        self.assertIn("buzzer_play_tone_ex", self.main)
        self.assertIn("volume=%u|envelope=%u", self.main)
        self.assertIn("profile_id*110u", self.buzzer)
        self.assertIn("sweep_start_hz+220u", self.buzzer)
        self.assertIn("sweep_duration_ms=150u", self.buzzer)
        self.assertNotIn("buzzer_play_stage(guard_event.stage, now)", self.main)
        self.assertIn("buzzer_service(now)", self.main)

    def test_route_release_preserves_system_cues_but_cancels_owned_beeps(self):
        release = self.main.split(
            "static void release_all_actors(uint32_t now) {", 1
        )[1].split("\n}", 1)[0]
        self.assertIn(
            "if (buzzer_action_pending || ui_buzzer_reply_pending) "
            "buzzer_silence();",
            release,
        )
        self.assertNotIn(
            "ui_buzzer_reply_pending=false;buzzer_silence()", release
        )
        self.assertIn(
            'printf("BUZZER|cue=transition|profile=%s|stage=%u\\n"',
            self.main,
        )
        self.assertIn(
            "release_all_actors(now_ms()); buzzer_silence();", self.main
        )


if __name__ == "__main__":
    unittest.main()
