import unittest
from pathlib import Path


class AbvmArmUartContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.pico = Path(__file__).resolve().parents[1] / "pico"
        cls.arm = (cls.pico / "arm_uart_mouse.c").read_text(encoding="utf-8")
        cls.main = (cls.pico / "main.c").read_text(encoding="utf-8")

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


if __name__ == "__main__":
    unittest.main()
