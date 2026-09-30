import unittest
from pathlib import Path


class AbvmArmUartContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.pico = Path(__file__).resolve().parents[1] / "pico"
        cls.arm = (cls.pico / "arm_uart_mouse.c").read_text(encoding="utf-8")
        cls.main = (cls.pico / "main.c").read_text(encoding="utf-8")
        cls.calibration = (cls.pico / "calibration_runtime.c").read_text(encoding="utf-8")
        cls.storage = (cls.pico / "calibration_store.c").read_text(encoding="utf-8")

    def test_probe_requires_relative_mouse_and_async_sound(self):
        self.assertIn('queue_payload("HVER", now, ARM_PROBE)', self.arm)
        self.assertIn('strstr(rx,"|REL=1")', self.arm)
        self.assertIn('strstr(rx,"|ASND=1")', self.arm)
        self.assertIn("arm-ready=%u|arm-ver=%s", self.main)

    def test_transport_errors_are_bounded_and_observable(self):
        self.assertIn("#define ARM_RETRY_MAX 2u", self.arm)
        self.assertIn('!strcmp(rx,"ERR|CKSUM")', self.arm)
        self.assertIn('!strcmp(rx,"ERR|NOFRAME")', self.arm)
        self.assertIn('"reply=%.*s"', self.arm)
        self.assertIn("ERR|ARM|detail=%s|version=%s", self.main)

    def test_release_all_is_idempotent_while_halt_is_pending(self):
        self.assertIn("state==ARM_FAULT || state==ARM_HALT", self.arm)
        self.assertIn('state==ARM_IDLE&&!strcmp(rx,"OK|HALT")', self.arm)

    def test_parallel_sound_and_mouse_use_bounded_backpressure(self):
        self.assertIn("deferred_mouse_pending", self.arm)
        self.assertIn("queue_deferred_mouse", self.arm)
        self.assertIn("halt_pending=true", self.arm)
        self.assertIn("state==ARM_IDLE&&halt_pending", self.arm)
        self.assertNotIn('queue_payload(command,now,ARM_MOVE)) return ARM_MOUSE_INVALID;\n    lane=', self.arm)

    def test_physical_light_and_sound_calibration_are_persistent(self):
        self.assertIn("BUTTON_LONG_MS 3000u", self.main)
        self.assertIn("calibration_runtime_blue_long", self.main)
        self.assertIn("calibration_runtime_yellow_long", self.main)
        self.assertIn("SOUND_SILENCE_MS 3000u", self.calibration)
        self.assertIn("SOUND_TARGET_MS 30000u", self.calibration)
        self.assertIn("CAL_OFFSET_A", self.storage)
        self.assertIn("CAL_OFFSET_B", self.storage)
        self.assertIn("flash_range_erase", self.storage)
        self.assertIn("program_sha256", self.storage)


if __name__ == "__main__":
    unittest.main()
