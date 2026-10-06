#!/usr/bin/env python3
"""Global 30-second hand profile and board-only twitch contract."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
ui = ROOT / "ams-shell/src/Ams.UI"

model = (ui / "Models/PipelineWorkspace.cs").read_text(encoding="utf-8")
serializer = (ui / "Services/PipelineWorkspaceSerializer.cs").read_text(encoding="utf-8")
capture = (ui / "Services/HandMovementSample.cs").read_text(encoding="utf-8")
exporter = (ui / "Services/NativeUf2Exporter.cs").read_text(encoding="utf-8")
view_model = (ui / "ViewModels/MainViewModel.HumanMouseProfile.cs").read_text(encoding="utf-8")
step_defs = (ui / "Models/StepDefinitions.cs").read_text(encoding="utf-8")
compiler = (ROOT / "tools/abvm.py").read_text(encoding="utf-8")
native = (ROOT / "firmware/abvm/pico/arm_uart_mouse.c").read_text(encoding="utf-8")

assert "FormatVersion = 12" in model
assert "HumanMouseProfile" in model and "DurationMs >= 30_000" in model
assert "humanMouseProfile" in serializer
assert "ProfileCaptureDurationMs = 30_000" in capture
assert "CaptureHumanMouseProfile" in view_model
assert "Native Export به پروفایل سراسری دست نیاز دارد" in exporter
assert '"motionIntent"' in step_defs
assert '"microTwitch"' in step_defs and '"mediumTwitch"' in step_defs
assert "compact_human_mouse_profile" in compiler
for token in ("handSignature", "handTempoMs", "handSpeedMin", "handSpeedMax",
              "handProfileV2", "handEfficiencyPct", "handCorrectionPct",
              "handMicroPct", "handMediumPct", "handBurstP50Px",
              "relativeMode", "relativeMin", "relativeMax"):
    assert token in compiler and token in native
for token in ("handPauseP50Ms", "handPauseP90Ms", "handTurnP50Deg",
              "handTurnP90Deg", "handLongPct"):
    assert token in compiler
assert "AmbientOutsideGameEnabled" in model
assert "AmbientOutsideGameEnabled" in view_model
assert "compile_ambient_mouse" in compiler
assert "arm_uart_mouse_submit_ambient" in native
assert "registry" not in native.lower()
print("global hand profile V2 + ambient board-only mouse contract passed")