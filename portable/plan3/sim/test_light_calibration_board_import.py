#!/usr/bin/env python3
"""Native light-calibration dump and Classroom Studio import contract."""
from pathlib import Path

root = Path(__file__).resolve().parents[3]
main = (root / "firmware/abvm/pico/main.c").read_text()
vm = (root / "ams-shell/src/Ams.UI/ViewModels/MainViewModel.LightProfiles.cs").read_text()
ui = (root / "ams-shell/src/Ams.UI/LightStateProfilesUiBootstrap.cs").read_text()
parser = (root / "ams-shell/src/Ams.UI/Services/LightCalibrationDumpParser.cs").read_text()
tests = (root / "tests/LightTelemetryTests/Program.cs").read_text()
bridge = (root / "ams-shell/bridge/bridge.py").read_text()

assert 'CALDUMP|LIGHT' in main
assert 'OK|CALDUMP|LIGHT|revision=%lu|mask=%02x|profiles=%s\\n' in main
assert 'calibration_store_light_get' in main
assert 'char profiles[256]' in main
assert 'printf("%s%u:%lu:%lu"' not in main
assert 'ImportLightStateProfilesFromBoardAsync' in vm
assert '_bridge.SendAsync("CALDUMP|LIGHT", 3)' in vm
assert 'ERR|COMMAND|unknown=CALDUMP|LIGHT' in vm
assert 'ERR|TIMEOUT|CALDUMP' in vm
assert 'LightCalibrationDumpParser.Apply' in vm
assert 'وارد کردن از برد' in ui
assert 'MessageBoxButton.YesNo' in ui
assert 'ProfileIds' in parser and '"desktop", "login-or-dc"' in parser
assert 'range.CenterLux' in parser and 'range.ToleranceLux' in parser
assert 'mismatched mask' in tests
assert 'line == "ERR|COMMAND|unknown=" + cmd' in bridge

# Old firmware must report the unsupported command immediately. Previously the
# correlation filter discarded this line and turned it into a timeout/disconnect.
import importlib.util
spec = importlib.util.spec_from_file_location("caldump_bridge_test", root / "ams-shell/bridge/bridge.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
link = module.PicoLink()
link.ser = object()
link._send = lambda command: None
lines = iter(["ERR|COMMAND|unknown=CALDUMP|LIGHT"])
link._read_line = lambda timeout: next(lines, None)
assert link.command("CALDUMP|LIGHT", timeout=0.1) == "ERR|COMMAND|unknown=CALDUMP|LIGHT"

print("native light calibration board import contract passed")