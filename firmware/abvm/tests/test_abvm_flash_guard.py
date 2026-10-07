import sys
import unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[3]
sys.path.insert(0,str(ROOT));sys.path.insert(0,str(Path(__file__).parent))
from tools.abvm_flash_guard import verify
from test_abvm_uf2 import make_uf2

class FlashGuardTests(unittest.TestCase):
    def test_1024_kib_slot_fits_with_conservative_pico_margin(self):
        result=verify(make_uf2(b"ABP1old",1024*1024))
        self.assertGreater(result["free_margin_bytes"],0)
    def test_reserved_calibration_sectors_are_rejected(self):
        with self.assertRaisesRegex(ValueError,"calibration"):
            verify(make_uf2(b"ABP1old",1024,base=0x101fe000))
    def test_invalid_flash_budget_is_rejected(self):
        with self.assertRaisesRegex(ValueError,"budget"):
            verify(make_uf2(b"ABP1old"),8192)
if __name__=="__main__":unittest.main()
