import json
from pathlib import Path
import sys
import unittest


class AbvmMouseMoveRegionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root = Path(__file__).resolve().parents[3]
        sys.path.insert(0, str(cls.root / "tools"))
        import abvm
        cls.abvm = abvm

    def source(self, props):
        return {"pipelines": {"Game": [
            {"Type": "mouseMove", "Props": props, "Children": [], "Delay": 0},
        ]}}

    def test_fixed_mouse_move_compiles_the_selected_random_rectangle(self):
        program = self.abvm.Compiler().compile_amsj(self.source({
            "x": 120, "y": 240, "w": 360, "h": 180, "human": True,
        }), ("Game",))
        image = self.abvm.Verifier.verify(program.image)
        instruction = next(ins for ins in image.instructions if ins.op == self.abvm.OP_RMOUSE)
        spec = json.loads(image.const(instruction.a, self.abvm.CONST_MOUSE))
        self.assertEqual((spec["x"], spec["y"], spec["w"], spec["h"]),
                         (120, 240, 360, 180))
        self.assertEqual(spec["motionIntent"], "targetRegion")

    def test_legacy_missing_dimensions_preserve_exact_point(self):
        program = self.abvm.Compiler().compile_amsj(self.source({
            "x": 700, "y": 400, "human": True,
        }), ("Game",))
        image = self.abvm.Verifier.verify(program.image)
        instruction = next(ins for ins in image.instructions if ins.op == self.abvm.OP_RMOUSE)
        spec = json.loads(image.const(instruction.a, self.abvm.CONST_MOUSE))
        self.assertEqual((spec["x"], spec["y"], spec["w"], spec["h"]),
                         (700, 400, 1, 1))

    def test_rejects_nonhuman_or_out_of_display_regions(self):
        with self.assertRaisesRegex(self.abvm.AbvmError, "requires humanized movement"):
            self.abvm.Compiler().compile_amsj(
                self.source({"x": 10, "y": 10, "w": 50, "h": 50, "human": False}),
                ("Game",))
        with self.assertRaisesRegex(self.abvm.AbvmError, "outside selected display"):
            self.abvm.Compiler().compile_amsj(
                self.source({"x": 1900, "y": 1000, "w": 50, "h": 50, "human": True}),
                ("Game",))


if __name__ == "__main__":
    unittest.main()