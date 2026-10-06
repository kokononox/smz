#!/usr/bin/env python3
"""Native Guard descriptor and route compiler contract."""
import json
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[3]))
from tools import abvm

source = json.loads((Path(__file__).resolve().parents[3] /
    "firmware/abvm/tests/abvm_guard_smoke.amsj").read_text())
routes = ("Desktop", "Restart", "Startup", "LoginOrDc", "Dc",
          "CharacterDashboard", "EnteringGameLoading", "Game", "Targeted", "Whisper",
          "WhisperRepeat", "Finish", "TargetedRepeat")
program = abvm.Compiler().compile_amsj(source, routes)
image = abvm.Verifier.verify(program.image)
assert image.flags & abvm.FLAG_HAS_GUARD
guards = [payload for kind, _, payload in image.constants if kind == abvm.CONST_GUARD]
assert len(guards) == 1 and guards[0][0] == 5
v4_profile_size = (abvm.GUARD_PROFILE.size + abvm.GUARD_CUE_META.size
                   + abvm.GUARD_CUSTOM_TONES * abvm.GUARD_CUE_TONE.size)
v5_size = (abvm.GUARD_HEADER.size + 9 * v4_profile_size
    + abvm.GUARD_CUE_META.size
    + abvm.BUZZER_SYSTEM_CUE_COUNT
      * (abvm.GUARD_CUE_META.size + abvm.GUARD_CUSTOM_TONES * abvm.GUARD_CUE_TONE.size))
assert len(guards[0]) == v5_size
assert {route.route_id for route in image.routes} >= {1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 13, 14}
targeted_first_tones = {}
for index in range(9):
    record_at = abvm.GUARD_HEADER.size + index * v4_profile_size
    profile_id = guards[0][record_at]
    if profile_id in (6, 9):
        tone_at = record_at + abvm.GUARD_PROFILE.size + abvm.GUARD_CUE_META.size
        targeted_first_tones[profile_id] = abvm.GUARD_CUE_TONE.unpack_from(
            guards[0], tone_at)
assert targeted_first_tones[6] != targeted_first_tones[9], \
    "Targeted New and Repeat must carry distinct notification motifs"

# The same profiles without system buzzer records remain a valid v4 compiler
# contract when their calibration cues use preset IDs without resolved notes.
legacy_v4 = json.loads(json.dumps(source))
legacy_v4["nativeGuard"].pop("buzzerCues")
for profile in legacy_v4["nativeGuard"]["profiles"]:
    profile.pop("calibrationCuePattern", None)
legacy_v4_guard = next(payload for kind, _, payload in
    abvm.Verifier.verify(abvm.Compiler().compile_amsj(legacy_v4, routes).image).constants
    if kind == abvm.CONST_GUARD)
assert legacy_v4_guard[0] == 4
assert len(legacy_v4_guard) == abvm.GUARD_HEADER.size + 9 * v4_profile_size
events = abvm.ReferenceVm(program.image).run("Game")
assert not any(event[0] == "KEY" for event in events), "forward GOTO must skip X"
assert any(event[0] == "DELAY" for event in events)

# Guard v5 appends all formerly hard-coded buzzer cues.  Every record remains
# bounded to eight notes and is verified as part of the ABP image.
with_buzzer = json.loads(json.dumps(source))
with_buzzer["nativeGuard"]["buzzerCues"] = [
    {"id": cue_id, "pattern": "440:100,30;880:180", "volume": 80,
     "envelope": "smooth", "tempo": 100}
    for cue_id in range(1, abvm.BUZZER_SYSTEM_CUE_COUNT + 1)
]
buzzer_image = abvm.Verifier.verify(abvm.Compiler().compile_amsj(with_buzzer, routes).image)
buzzer_guard = next(payload for kind, _, payload in buzzer_image.constants
                    if kind == abvm.CONST_GUARD)
assert buzzer_guard[0] == 5
assert len(buzzer_guard) == v5_size

# A selected preset may carry per-assignment speed/style.  Its concrete
# pattern is compiled into the Guard record so the board schedules it locally.
styled_preset = json.loads(json.dumps(source))
styled_preset["nativeGuard"]["profiles"][0].update({
    "calibrationCuePattern": "440:100,40;880:200",
    "calibrationCueVolume": 42,
    "calibrationCueEnvelope": "smooth",
    "calibrationCueTempo": 200,
})
styled_guard = next(payload for kind, _, payload in
    abvm.Verifier.verify(abvm.Compiler().compile_amsj(styled_preset, routes).image).constants
    if kind == abvm.CONST_GUARD)
meta_at = abvm.GUARD_HEADER.size + abvm.GUARD_PROFILE.size
tone_at = meta_at + abvm.GUARD_CUE_META.size
assert abvm.GUARD_CUE_META.unpack_from(styled_guard, meta_at)[:3] == (2, 42, 1)
assert abvm.GUARD_CUE_TONE.unpack_from(styled_guard, tone_at) == (440, 50, 20)

# Build 119 projects use the legacy UI names Launch and LaunchRecovery.
# Native export requests their canonical Restart and Dc names.  The compiler
# must migrate both aliases without requiring users to edit working projects.
legacy = json.loads(json.dumps(source))
legacy["pipelines"]["Launch"] = legacy["pipelines"].pop("Restart")
legacy["pipelines"]["LaunchRecovery"] = legacy["pipelines"].pop("Dc")
legacy_image = abvm.Verifier.verify(abvm.Compiler().compile_amsj(legacy, routes).image)
assert {route.route_id for route in legacy_image.routes} >= {1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14}

bad = json.loads(json.dumps(source))
bad["nativeGuard"]["profiles"][5]["luxCenter"] = 200
try:
    abvm.Compiler().compile_amsj(bad, routes)
except abvm.AbvmError as exc:
    assert "overlap" in str(exc)
else:
    raise AssertionError("overlapping Native Guard profiles were accepted")
print("ABVM native global Guard compiler contract passed")
