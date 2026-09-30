import unittest
from pathlib import Path
import sys


class AbvmArmUartContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.root = Path(__file__).resolve().parents[3]
        cls.pico = cls.root / "firmware" / "abvm" / "pico"
        cls.arm = (cls.pico / "arm_uart_mouse.c").read_text(encoding="utf-8")
        cls.main = (cls.pico / "main.c").read_text(encoding="utf-8")
        cls.calibration = (cls.pico / "calibration_runtime.c").read_text(encoding="utf-8")
        cls.storage = (cls.pico / "calibration_store.c").read_text(encoding="utf-8")

    def test_probe_requires_relative_mouse_and_async_sound(self):
        self.assertIn('queue_payload("HVER", now, ARM_PROBE)', self.arm)
        self.assertIn('strstr(rx,"|REL=1")', self.arm)
        self.assertIn('strstr(rx,"|ASND=1")', self.arm)
        self.assertIn("arm-ready=%u|arm-usb=%u|arm-ver=%s", self.main)

    def test_transport_errors_are_bounded_and_observable(self):
        self.assertIn("#define ARM_RETRY_MAX 2u", self.arm)
        self.assertIn('!strcmp(rx,"ERR|CKSUM")', self.arm)
        self.assertIn('!strcmp(rx,"ERR|NOFRAME")', self.arm)
        self.assertIn('"reply=%.*s"', self.arm)
        self.assertIn("ERR|ARM|detail=%s|version=%s", self.main)

    def test_release_all_is_idempotent_while_halt_is_pending(self):
        self.assertIn("state==ARM_FAULT || state==ARM_HALT", self.arm)
        self.assertIn('state==ARM_IDLE&&!strcmp(rx,"OK|HALT")', self.arm)
        self.assertIn("arm_uart_mouse_discard_completion", self.arm)

    def test_parallel_sound_and_mouse_use_bounded_backpressure(self):
        self.assertIn("deferred_mouse_pending", self.arm)
        self.assertIn("queue_deferred_mouse", self.arm)
        self.assertNotIn("state != ARM_IDLE || completion_pending", self.arm)
        self.assertIn("state == ARM_SOUND_CAL || completion_pending", self.arm)
        self.assertIn("deferred_mouse_pending || halt_pending", self.arm)
        self.assertIn("halt_pending=true", self.arm)
        self.assertIn("state==ARM_IDLE&&halt_pending", self.arm)
        self.assertNotIn('queue_payload(command,now,ARM_MOVE)) return ARM_MOUSE_INVALID;\n    lane=', self.arm)

    def test_native_rmouse_preserves_human_motion_controls(self):
        for key in (
            "x", "y", "w", "h",
            "curveMinPct", "curveMaxPct",
            "moveTimeMin", "moveTimeMax",
            "pauseBeforeMin", "pauseBeforeMax",
            "pauseAfterMin", "pauseAfterMax",
            "midPauseChance", "midPauseMin", "midPauseMax",
            "idleEveryMin", "idleEveryMax",
            "idlePauseMin", "idlePauseMax",
            "overshootChance",
        ):
            self.assertIn(f'"{key}"', self.arm)
        self.assertIn("HUMAN_PATH_BEFORE", self.arm)
        self.assertIn("HUMAN_PATH_MOVE", self.arm)
        self.assertIn("HUMAN_PATH_CORRECT", self.arm)
        self.assertIn("HUMAN_PATH_AFTER", self.arm)
        self.assertIn("q16_bezier", self.arm)
        self.assertIn("smooth_q16", self.arm)
        self.assertIn("human_path.mid_pause_ms", self.arm)
        self.assertIn("human_moves_since_idle", self.arm)
        self.assertIn("human_path.correction_steps", self.arm)
        self.assertIn("target_x - human_virtual_x", self.arm)
        self.assertNotIn(
            'snprintf(command,sizeof(command),"MMOVE|%ld,%ld,rel,2",'
            '(long)dx,(long)dy)',
            self.arm,
        )

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

    def test_classroom_scal_proxy_is_async_and_bounded(self):
        self.assertIn('!strncmp(line, "SCAL|", 5)', self.main)
        self.assertIn("arm_uart_sound_calibration_start(now,(uint16_t)duration)", self.main)
        self.assertIn("ui_sound_calibration_pending", self.main)
        self.assertIn("arm_uart_sound_calibration_take(&average,&peak)", self.main)
        self.assertIn('OK|SCAL|avg=%u|max=%u', self.main)
        self.assertIn('ERR|TIMEOUT|SCAL', self.main)

    def test_classroom_threshold_controls_native_watch_and_hit_runs_f(self):
        sys.path.insert(0, str(self.root / "tools"))
        import abvm
        source = {"pipelines": {"Game": [{
            "Type": "waitForSound",
            "Props": {
                "calibrationId": 2, "threshold": 8, "peakMin": 76,
                "minDurationMs": 20, "timeoutMinSec": 1,
                "timeoutMaxSec": 1,
            },
            "Children": [{
                "Type": "keystroke",
                "Props": {"key": "F", "holdMin": 80, "holdMax": 180},
                "Children": [], "Delay": 0,
            }],
            "Delay": 0,
        }]}}
        program = abvm.Compiler().compile_amsj(source, ("Game",))
        image = abvm.Verifier.verify(program.image)
        descriptors = [abvm.SOUND.unpack(payload)
                       for kind, _, payload in image.constants
                       if kind == abvm.CONST_SOUND]
        self.assertEqual(descriptors, [(2, 8, 20)])
        hit = abvm.ReferenceVm(program.image, detected_profiles=(2,)).run("Game")
        miss = abvm.ReferenceVm(program.image).run("Game")
        self.assertTrue(any(event[0] == "KEY" and event[1] == (70,)
                            for event in hit))
        self.assertFalse(any(event[0] == "KEY" for event in miss))

    def test_global_whisper_persists_peak_range_and_native_checks_upper_edge(self):
        sys.path.insert(0, str(self.root / "tools"))
        import abvm
        source = {
            "soundProfiles": [{
                "Id": 1, "Enabled": True, "ResponseTab": "Whisper",
                "PeakMin": 20, "PeakMax": 80, "MinDurationMs": 60,
            }, {
                "Id": 3, "Enabled": True, "ResponseTab": "WhisperRepeat",
                "PeakMin": 81, "PeakMax": 140, "MinDurationMs": 70,
            }],
            "pipelines": {"Game": [], "Whisper": [], "WhisperRepeat": []},
        }
        program = abvm.Compiler().compile_amsj(
            source, ("Game", "Whisper", "WhisperRepeat"))
        image = abvm.Verifier.verify(program.image)
        descriptors = [
            abvm.SOUND.unpack(payload)
            for kind, _, payload in image.constants
            if kind == abvm.CONST_SOUND]
        self.assertIn((1, 20, 60 | (80 << 16) | (2 << 26)), descriptors)
        self.assertIn((3, 81, 70 | (140 << 16) | (2 << 26)), descriptors)
        self.assertIn("peak<=whisper_profiles[i].maximum", self.main)
        self.assertIn("BUZZER_CUE_WHISPER", self.main)
        self.assertIn("BUZZER_CUE_WHISPER_REPEAT", self.main)
        self.assertIn("WHISPER_REPEAT_ROUTE_ID", self.main)
        self.assertIn("reason=cooldown", self.main)
        self.assertIn("reason=input-locked", self.main)
        self.assertIn("service_pending_sound_whisper", self.main)
        self.assertIn("cycle_runtime_restart_critical", self.main)
        keyboard_h = (self.pico / "hid_keyboard.h").read_text(encoding="utf-8")
        keyboard_c = (self.pico / "hid_keyboard.c").read_text(encoding="utf-8")
        self.assertIn("hid_keyboard_locked", keyboard_h)
        self.assertIn("actor.persistent_modifiers", keyboard_c)
        self.assertIn("actor.persistent_keys", keyboard_c)
        self.assertIn("source=sound", self.main)

    def test_saved_calibration_is_explicit_and_runtime_logs_effective_values(self):
        self.assertIn("if (!sound_threshold)", self.arm)
        self.assertNotIn(
            "(void)calibration_store_sound_get(sound_profile",
            self.arm)
        self.assertIn("arm_uart_sound_uses_calibration", self.arm)
        self.assertIn("threshold=%u|min=%u|config=%s|source=arm", self.main)

    def test_native_direct_run_compatibility_is_nonblocking(self):
        self.assertIn('!strncmp(line, "SETRES|", 7)', self.main)
        self.assertIn('!strncmp(line, "MMOVE|", 6)', self.main)
        self.assertIn("arm_uart_mouse_submit_live(command,now)", self.main)
        self.assertIn("ARM_LIVE_LANE", self.arm)
        self.assertIn("lane!=ARM_LIVE_LANE", self.arm)
        self.assertIn("submit_direct(command,now,ARM_LIVE_LANE)", self.arm)
        self.assertIn('"MCLICK|"', self.main)
        self.assertIn('"MWHEEL|"', self.main)
        self.assertIn('"KBDARM|"', self.main)
        self.assertIn("arm_uart_mouse_take_live_reply", self.main)
        self.assertIn("sound_result_latched", self.arm)
        self.assertIn('queue_payload("ASNDCANCEL",now,ARM_SOUND_CANCEL)', self.arm)
        self.assertIn("direct_lane == ARM_LIVE_LANE && sound_result_latched", self.arm)
        self.assertIn("arm-usb=%u", self.main)
        keyboard = (self.pico / "hid_keyboard.c").read_text(encoding="utf-8")
        self.assertIn("hid_keyboard_submit_live", self.main)
        self.assertIn('"KCOMBO|"', keyboard)
        self.assertIn('"KDOWN|"', keyboard)
        self.assertIn('"KUP|"', keyboard)
        self.assertIn('"KTEXT|"', keyboard)
        self.assertIn("hid_keyboard_take_live_reply", self.main)
        self.assertIn('"WLUX|"', self.main)
        self.assertIn('"TRGLUX|"', self.main)
        self.assertIn("light_sensor_live_start", self.main)
        self.assertIn('"TRGSND|"', self.main)
        self.assertIn("arm_uart_mouse_submit_internal", self.main)
        self.assertIn('!strncmp(line, "WSND|", 5)', self.main)
        self.assertIn("arm_uart_sound_test_start", self.main)
        self.assertIn('!strncmp(line, "BEEP|", 5)', self.main)
        self.assertIn("ui_buzzer_reply_pending", self.main)

    def test_private_arm_link_exposes_helper_free_host_usb_lifecycle(self):
        self.assertIn('EVT|HOSTUSB|DOWN', self.arm)
        self.assertIn('EVT|HOSTUSB|SUSPEND', self.arm)
        self.assertIn('EVT|HOSTUSB|UP', self.arm)
        self.assertIn("arm_uart_host_usb_seen", self.arm)
        self.assertIn("arm_uart_host_usb_state", self.arm)


if __name__ == "__main__":
    unittest.main()
