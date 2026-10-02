#!/usr/bin/env python3
"""Native light-calibration dump and Classroom Studio import contract."""
from pathlib import Path

root = Path(__file__).resolve().parents[3]
main = (root / "firmware/abvm/pico/main.c").read_text()
vm = (root / "ams-shell/src/Ams.UI/ViewModels/MainViewModel.LightProfiles.cs").read_text()
ui = (root / "ams-shell/src/Ams.UI/LightStateProfilesUiBootstrap.cs").read_text()
parser = (root / "ams-shell/src/Ams.UI/Services/LightCalibrationDumpParser.cs").read_text()
tests = (root / "tests/LightTelemetryTests/Program.cs").read_text()

assert 'CALDUMP|LIGHT' in main
assert 'OK|CALDUMP|LIGHT|revision=%lu|mask=%02x|profiles=' in main
assert 'calibration_store_light_get' in main
assert 'ImportLightStateProfilesFromBoardAsync' in vm
assert '_bridge.SendAsync("CALDUMP|LIGHT", 3)' in vm
assert 'LightCalibrationDumpParser.Apply' in vm
assert 'وارد کردن از برد' in ui
assert 'MessageBoxButton.YesNo' in ui
assert 'ProfileIds' in parser and '"desktop", "login-or-dc"' in parser
assert 'range.CenterLux' in parser and 'range.ToleranceLux' in parser
assert 'mismatched mask' in tests

print("native light calibration board import contract passed")