import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from tools.abvm_pico1 import render_header, write_native_assets


CONFIG = {
    "circuitpython_version": "10.3.0",
    "source_board": "raspberry_pi_pico",
    "target_board": "greenfield_uni_en_062",
    "usb_manufacturer": "Greenfield Uni Engineering",
    "usb_product": "GU Debug Port 062",
    "usb_vid": "0x3169",
    "usb_pid": "0xC198",
    "serial_prefix": "GUEN062-",
    "drive_label": "EDGUEN062",
    "maintenance_pin": "GP3",
    "drive_mode": "maintenance",
    "runtime_profile": "classroom_guard",
    "language": "en_US",
}


class NativePico1AdapterTests(unittest.TestCase):
    def test_header_uses_pico1_identity(self):
        header = render_header(CONFIG)
        self.assertIn("#define ABVM_USB_VID 0x3169u", header)
        self.assertIn("#define ABVM_USB_PID 0xC198u", header)
        self.assertIn('ABVM_USB_SERIAL_PREFIX "GUEN062-"', header)

    def test_manifest_is_auditable_and_keeps_storage_disabled(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            validated = root / "build-config.json"
            validated.write_text(json.dumps(CONFIG), encoding="utf-8")
            pico1_manifest = root / "build-manifest.json"
            pico1_manifest.write_text(json.dumps({"schema": 2}), encoding="utf-8")
            program = root / "program.abp"
            program.write_bytes(b"ABP1" + bytes(range(32)))
            revision = root / "PICO1_REVISION"
            revision.write_text("4b017dfa810ab8dd710d6952b61065b560a1e8ba\n", encoding="utf-8")
            out = root / "out"
            write_native_assets(validated, pico1_manifest, program, revision, out)
            manifest = json.loads((out / "abvm-firmware-manifest.json").read_text())
            self.assertEqual(manifest["backend"], "pico-sdk-native-abvm")
            self.assertFalse(manifest["mass_storage_enabled"])
            self.assertEqual(manifest["program_sha256"], hashlib.sha256(program.read_bytes()).hexdigest())
            self.assertIn("drive_mode", manifest["not_applicable_to_native_backend"])

    def test_experimental_batch_has_thirty_unique_identities(self):
        batch = Path(__file__).resolve().parents[1] / "pico" / "batch30.json"
        data = json.loads(batch.read_text())
        configs = [{**data["defaults"], **profile} for profile in data["profiles"]]
        self.assertEqual(len(configs), 30)
        fields = ("target_board", "usb_manufacturer", "usb_product", "usb_vid",
                  "usb_pid", "serial_prefix", "drive_label")
        for key in fields:
            self.assertEqual(len({cfg[key] for cfg in configs}), 30, key)
        for cfg in configs:
            render_header(cfg)

    def test_rejects_noncanonical_unvalidated_vid(self):
        with self.assertRaisesRegex(ValueError, "canonical pico1 hex"):
            render_header({**CONFIG, "usb_vid": "0x3169aa"})


if __name__ == "__main__":
    unittest.main()
