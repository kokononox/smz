#!/usr/bin/env python3
"""Display presets, Custom dimensions and hostless soft steering contract."""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
UI = ROOT / "ams-shell" / "src" / "Ams.UI"
model = (UI / "Models/PipelineWorkspace.cs").read_text(encoding="utf-8")
serializer = (UI / "Services/PipelineWorkspaceSerializer.cs").read_text(encoding="utf-8")
view_model = (UI / "ViewModels/MainViewModel.DisplayProfile.cs").read_text(encoding="utf-8")
ui = (UI / "MainWindow.AutoCycleExportUi.cs").read_text(encoding="utf-8")
native = (ROOT / "firmware/abvm/pico/arm_uart_mouse.c").read_text(encoding="utf-8")

assert "FormatVersion = 9" in model
assert "DisplayProfile" in model and "displayProfile" in serializer
for preset in ("800x600", "1366x768", "1680x1050", "1920x1080", "Custom"):
    assert f'"{preset}"' in view_model
assert "DisplayCustomWidth" in view_model and "DisplayCustomHeight" in view_model
assert "IsCustomDisplayResolution" in view_model
assert "BooleanToVisibilityConverter" in ui
assert "Soft Boundary Steering فعال باشد" in ui

for token in (
    '"screenWidth"', '"screenHeight"', '"softBoundary"', '"softMarginPct"',
    "soft_steer_axis", "human_soft_margin_x", "human_soft_margin_y",
):
    assert token in native
assert "Registry" not in native

sys.path.insert(0, str(ROOT / "tools"))
import abvm

source = {
    "displayProfile": {
        "Preset": "Custom", "Width": 1366, "Height": 768,
        "SoftBoundaryEnabled": True, "SoftMarginPercent": 4,
    },
    "pipelines": {
        "Game": [{
            "Type": "randomMousePosition",
            "Props": {
                "motionIntent": "microTwitch",
                "twitchMinPx": 3, "twitchMaxPx": 14,
                "x": 0, "y": 0, "w": 100, "h": 100,
            },
            "Children": [], "Delay": 0,
        }]
    },
}
image = abvm.Verifier.verify(
    abvm.Compiler().compile_amsj(source, ("Game",)).image)
mouse = [
    json.loads(payload.decode())
    for kind, _, payload in image.constants
    if kind == abvm.CONST_MOUSE
]
assert len(mouse) == 1
assert mouse[0]["screenWidth"] == 1366
assert mouse[0]["screenHeight"] == 768
assert mouse[0]["softBoundary"] == 1
assert mouse[0]["softMarginPct"] == 4

legacy = {"pipelines": source["pipelines"]}
legacy_image = abvm.Verifier.verify(
    abvm.Compiler().compile_amsj(legacy, ("Game",)).image)
legacy_mouse = [
    json.loads(payload.decode())
    for kind, _, payload in legacy_image.constants
    if kind == abvm.CONST_MOUSE
][0]
assert legacy_mouse["screenWidth"] == 1920
assert legacy_mouse["screenHeight"] == 1080
assert legacy_mouse["softBoundary"] == 0

print("display presets + Custom resolution + hostless soft boundary contract passed")