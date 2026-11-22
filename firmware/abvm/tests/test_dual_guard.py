import json
import pathlib
import sys
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools"))
import abvm

ROUTES = (
    "Desktop", "Restart", "Startup", "LoginOrDc", "Dc",
    "CharacterDashboard", "EnteringGameLoading", "Game", "Targeted",
    "TargetedRepeat", "Whisper", "WhisperRepeat", "Finish",
)


def dual_source():
    source = json.loads(
        (ROOT / "firmware/abvm/tests/abvm_guard_smoke.amsj").read_text())
    guard = source["nativeGuard"]
    guard["dualSensor"] = True
    for index, profile in enumerate(guard["profiles"]):
        # Deliberately overlap every legacy L1 range. The paired feature space
        # is distinct, so the two-sensor descriptor must still compile.
        profile.update({
            "luxCenter": 100,
            "luxTolerance": 5,
            "luxMin": 95,
            "luxMax": 105,
            "meanLuxCenter": 100 + index * 50,
            "meanLuxTolerance": 2,
            "differenceLuxCenter": 10 if index == 0 else 0,
            "differenceLuxTolerance": 2,
        })
    return source


class DualGuardFormatTest(unittest.TestCase):
    def compile_descriptor(self, source):
        image = abvm.Verifier.verify(
            abvm.Compiler().compile_amsj(source, ROUTES).image)
        return next(data for kind, _, data in image.constants
                    if kind == abvm.CONST_GUARD)

    def test_v6_serializes_pair_axes_and_allows_l1_overlap(self):
        raw = self.compile_descriptor(dual_source())
        version, count, *_ = abvm.GUARD_HEADER.unpack_from(raw)
        self.assertEqual((version, count), (6, 9))
        profile_size = (abvm.GUARD_PROFILE.size + abvm.GUARD_CUE_META.size
                        + abvm.GUARD_CUSTOM_TONES * abvm.GUARD_CUE_TONE.size
                        + abvm.GUARD_DUAL_AXES.size)
        self.assertEqual(len(raw), abvm.GUARD_HEADER.size + 9 * profile_size
                         + abvm.GUARD_CUE_META.size
                         + abvm.BUZZER_SYSTEM_CUE_COUNT
                         * (abvm.GUARD_CUE_META.size
                            + abvm.GUARD_CUSTOM_TONES * abvm.GUARD_CUE_TONE.size))
        offset = abvm.GUARD_HEADER.size + abvm.GUARD_PROFILE.size
        offset += abvm.GUARD_CUE_META.size
        offset += abvm.GUARD_CUSTOM_TONES * abvm.GUARD_CUE_TONE.size
        self.assertEqual(abvm.GUARD_DUAL_AXES.unpack_from(raw, offset),
                         (2000, 40, 100, 20))

    def test_missing_or_mixed_paired_profiles_fail_closed(self):
        source = dual_source()
        del source["nativeGuard"]["profiles"][0]["differenceLuxTolerance"]
        with self.assertRaisesRegex(abvm.AbvmError, "every profile"):
            abvm.Compiler().compile_amsj(source, ROUTES)

        source = dual_source()
        del source["nativeGuard"]["dualSensor"]
        with self.assertRaisesRegex(abvm.AbvmError, "dualSensor=true"):
            abvm.Compiler().compile_amsj(source, ROUTES)

    def test_pair_clearance_is_required(self):
        source = dual_source()
        profiles = source["nativeGuard"]["profiles"]
        profiles[1]["meanLuxCenter"] = profiles[0]["meanLuxCenter"]
        profiles[1]["differenceLuxCenter"] = profiles[0]["differenceLuxCenter"]
        with self.assertRaisesRegex(abvm.AbvmError, "lack 1 Lux clearance"):
            abvm.Compiler().compile_amsj(source, ROUTES)

    def test_legacy_v5_format_is_unchanged(self):
        source = json.loads(
            (ROOT / "firmware/abvm/tests/abvm_guard_smoke.amsj").read_text())
        raw = self.compile_descriptor(source)
        self.assertEqual(abvm.GUARD_HEADER.unpack_from(raw)[0], 5)


if __name__ == "__main__":
    unittest.main()