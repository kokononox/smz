import unittest
from pathlib import Path


class AbvmBuzzerContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.pico = Path(__file__).resolve().parents[1] / "pico"
        cls.buzzer = (cls.pico / "buzzer.c").read_text(encoding="utf-8")
        cls.main = (cls.pico / "main.c").read_text(encoding="utf-8")
        cls.cmake = (cls.pico / "CMakeLists.txt").read_text(encoding="utf-8")

    def test_passive_buzzer_uses_gp6_hardware_pwm(self):
        self.assertIn("#define BUZZER_PIN 6u", self.buzzer)
        self.assertIn("GPIO_FUNC_PWM", self.buzzer)
        self.assertIn("hardware_pwm", self.cmake)
        self.assertIn("buzzer=legacy-presets-gp6", self.main)

    def test_pattern_engine_is_nonblocking_and_bounded(self):
        self.assertIn("static const BuzzerPattern patterns[]", self.buzzer)
        self.assertIn("next_priority < priority", self.buzzer)
        self.assertIn("void buzzer_service(uint32_t now)", self.buzzer)
        self.assertIn("{1000,180,0}", self.buzzer)
        self.assertIn("{1000,140,100},{1000,140,0}", self.buzzer)
        self.assertIn("{700,180,90},{700,180,90},{700,300,0}", self.buzzer)
        self.assertIn("{900,120,70},{1300,220,0}", self.buzzer)
        self.assertNotIn("sleep_ms", self.buzzer)
        self.assertNotIn("malloc", self.buzzer)

    def test_full_operational_cues_are_wired(self):
        for cue in ("BUZZER_CUE_START", "BUZZER_CUE_STOP",
                    "BUZZER_CUE_PAUSE", "BUZZER_CUE_RESUME",
                    "BUZZER_CUE_CATCH", "BUZZER_CUE_TIMEOUT",
                    "BUZZER_CUE_ERROR"):
            self.assertIn(cue, self.main)
        self.assertIn("buzzer_play_stage(guard_event.stage, now)", self.main)
        self.assertIn("buzzer_service(now)", self.main)


if __name__ == "__main__":
    unittest.main()
