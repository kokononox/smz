#!/usr/bin/env python3
import importlib.util
import json
import tempfile
from pathlib import Path

root = Path(__file__).resolve().parents[3]
module_path = root / "portable/plan3/CIRCUITPY-MODERN/sound_step_calibration.py"
spec = importlib.util.spec_from_file_location("sound_step_calibration_test", module_path)
cal = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cal)

class Controls:
    running = False

class Arm:
    def __init__(self):
        self.replies = []
    def send(self, command, timeout):
        assert command == "SCAL|250" and timeout == 2.0
        return self.replies.pop(0)

class Owner:
    def __init__(self):
        self.controls = Controls()
        self.calibrating = False
        self.sound_calibrating = False
        self.sound_calibration_id = 1
        self.sound_calibration_phase = None
        self.sound_calibration_started = 0
        self.sound_calibration_silence = 0
        self.sound_calibration_peak = 0
        self.sound_calibration_pending = None
        self.sound_bindings = {}
        self.sound_profiles = {}
        self.arm = Arm()
        self.lines = []
    def emit(self, line): self.lines.append(line)
    def prepare_calibration_heap(self): pass
    def calibration_enter_tone(self): pass
    def calibration_exit_tone(self): pass
    def _cal_beep(self, frequency, duration): pass
    def cal_save_error_tone(self): pass
    def cal_save_success_tone(self): pass
    def cal_stage_complete_tone(self): pass

with tempfile.TemporaryDirectory() as td:
    td = Path(td)
    cal._PATH = str(td / "sound-step-calibration.json")
    cal._BACKUP = str(td / "sound-step-calibration.bak")
    cal._TEMP = str(td / "sound-step-calibration.tmp")
    route = td / "game_steps.txt"
    route.write_text("PLAN|2\nWSNDP|1,aaaaaaaaaaaa,76,20,20000\nWSNDP|2,bbbbbbbbbbbb,90,20,20000\n")
    cal._ROUTES = (str(route),)
    cal._SILENCE_SECONDS = 0
    cal._TARGET_SECONDS = 0

    owner = Owner()
    assert cal.start(owner) and owner.sound_calibrating and owner.sound_calibration_id == 1
    assert cal.begin_sample(owner)
    owner.arm.replies = ["OK|SCAL|avg=4|max=10", "OK|SCAL|avg=35|max=130"]
    cal.tick(owner)
    assert owner.sound_calibration_phase == "sound"
    cal.tick(owner)
    assert owner.sound_calibration_phase == "complete"
    assert owner.sound_calibration_pending["1"]["threshold"] == 70

    assert cal.select_next(owner) and owner.sound_calibration_id == 2
    assert cal.begin_sample(owner)
    owner.arm.replies = ["OK|SCAL|avg=5|max=12", "OK|SCAL|avg=40|max=150"]
    cal.tick(owner); cal.tick(owner)
    assert cal.finish(owner)
    saved = json.loads(Path(cal._PATH).read_text())
    assert set(saved["profiles"]) == {"1", "2"}
    assert saved["checksum"] == cal._checksum(saved["profiles"])

    exact = Owner()
    cal._ROUTES = (str(route),)
    assert cal.resolve(exact, 1, "aaaaaaaaaaaa", 76, 20) == (70, 20)
    assert cal.resolve(exact, 1, "cccccccccccc", 76, 20) == (76, 20)
    assert any("ERR|SOUNDCAL|BINDING|id=1" in line for line in exact.lines)

    first = cal._load()
    changed = dict(first)
    changed["1"] = dict(changed["1"])
    changed["1"]["threshold"] += 1
    cal._save(changed)
    Path(cal._PATH).write_text("corrupt")
    recovered = Owner()
    assert cal._load(recovered)["1"]["threshold"] == first["1"]["threshold"]
    assert any("mode=recovered-backup" in line for line in recovered.lines)

bridge = (root / "ams-shell/bridge/bridge.py").read_text()
assert "fallback = None" in bridge
assert '"stage": "pico_fallback"' in bridge
assert "Remember a direct/legacy board, but keep scanning" in bridge
assert "detected = detect_board_port()" in bridge

bundle = (root / "ams-shell/src/Ams.UI/Services/ModernAutoCycleFirmwareBundle.cs").read_text()
assert '"sound_step_calibration.py"' in bundle
assert module_path.exists()
print("portable sound-step calibration and Pico fallback contracts passed")

# Behavioral regression: a generic direct-board reply on COM30 must not stop the
# scan before a Pico brain on COM31 is found.
import sys
import types
bridge_path = root / "ams-shell/bridge/bridge.py"
bridge_spec = importlib.util.spec_from_file_location("bridge_port_test", bridge_path)
bridge_module = importlib.util.module_from_spec(bridge_spec)
bridge_spec.loader.exec_module(bridge_module)

class Port:
    def __init__(self, device):
        self.device = device; self.vid = 0x2E8A; self.pid = 0x0005
        self.description = "USB serial"; self.manufacturer = "test"

class SerialPort:
    def __init__(self, device, *args, **kwargs):
        self.device = device; self.sent = False
    def reset_input_buffer(self): pass
    def reset_output_buffer(self): pass
    def write(self, data): self.sent = True; return len(data)
    def flush(self): pass
    def read(self, size):
        if not self.sent: return b""
        self.sent = False
        return (b"OK|HELLO|AMS_BOARD\n" if self.device == "COM30" else
                b"OK|PONG|combined-pico|role=brain\n")
    def close(self): pass

serial_module = types.ModuleType("serial")
serial_module.Serial = SerialPort
tools_module = types.ModuleType("serial.tools")
tools_module.list_ports = types.SimpleNamespace(comports=lambda: [Port("COM30"), Port("COM31")])
old_serial = sys.modules.get("serial"); old_tools = sys.modules.get("serial.tools")
sys.modules["serial"] = serial_module; sys.modules["serial.tools"] = tools_module
try:
    assert bridge_module.detect_board_port() == "COM31"
finally:
    if old_serial is None: sys.modules.pop("serial", None)
    else: sys.modules["serial"] = old_serial
    if old_tools is None: sys.modules.pop("serial.tools", None)
    else: sys.modules["serial.tools"] = old_tools
print("brain-first multi-port detection passed")
