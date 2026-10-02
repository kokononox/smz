#!/usr/bin/env python3
from pathlib import Path
root = Path(__file__).resolve().parents[3]
model = (root / "ams-shell/src/Ams.UI/Models/PipelineWorkspace.cs").read_text(encoding="utf-8")
viewmodel = (root / "ams-shell/src/Ams.UI/ViewModels/MainViewModel.PipelineTabs.cs").read_text(encoding="utf-8")
serializer = (root / "ams-shell/src/Ams.UI/Services/PipelineWorkspaceSerializer.cs").read_text(encoding="utf-8")
bundle = (root / "ams-shell/src/Ams.UI/Services/PipelinePlanBundle.cs").read_text(encoding="utf-8")
dashboard = (root / "ams-shell/src/Ams.UI/MainWindow.AutoCycleExportUi.cs").read_text(encoding="utf-8")
spec = (root / "docs/launch-pipeline-tabs.md").read_text(encoding="utf-8")
for name in ("Launch", "Main", "LaunchRecovery", "MainRecovery", "ResumeEssentials"):
    assert name in model
for filename in ("launch_steps.txt", "plan.txt", "launch_recovery.txt", "main_recovery.txt", "resume_essentials.txt"):
    assert filename in model
assert "FromLegacy" in model and "PipelineKind.Main" in model
assert "pipelineVersion" in serializer and "FixParents" in serializer
assert "Startup" in model and "startup_steps.txt" in model
assert "5, 10, 20, 40, 80" in spec
assert "transient editor UI state" in spec
assert "SelectedNodes = new()" in viewmodel and "_undo.Clear()" in viewmodel and "_redo.Clear()" in viewmodel
assert "optional GP6 buzzer/error signalling is tracked as a hardware follow-up" in spec
assert 'PipelineKind.Whisper' in model and 'whisper_steps.txt' in model
assert 'PipelineKind.WhisperRepeat' in model and 'whisper_repeat_steps.txt' in model
assert 'PipelineKind.Splash' in model and 'splash_steps.txt' in model
assert 'FormatVersion = 10' in model and 'soundProfiles' in serializer
assert 'x.Kind != PipelineKind.Splash' in viewmodel
assert 'MigrateExplicitCatchWait' in serializer
assert 'AddGameSoundWatch' in bundle and 'SOUNDWATCH|' in bundle and 'FindCatchWaits' in bundle
for token in ('WhisperPeakMin', 'WhisperPriority', 'WhisperRepeatPeakMin',
              'WhisperRepeatPriority', 'SoundProfileSummary',
              'صدای Catch داخل استپ صریح Wait For Sound تنظیم می‌شود'):
    assert token in dashboard, token
print("pipeline tabs + global Whisper/explicit Catch contract: 29 passed, 0 failed")
