from pathlib import Path

root = Path(__file__).resolve().parents[3]

model = (root / "ams-shell/src/Ams.UI/Models/PipelineWorkspace.cs").read_text(encoding="utf-8")
vm = (root / "ams-shell/src/Ams.UI/ViewModels/MainViewModel.SoundProfiles.cs").read_text(encoding="utf-8")
ui = (root / "ams-shell/src/Ams.UI/MainWindow.AutoCycleExportUi.cs").read_text(encoding="utf-8")
xaml = (root / "ams-shell/src/Ams.UI/MainWindow.xaml").read_text(encoding="utf-8")
steps = (root / "ams-shell/src/Ams.UI/Models/StepDefinitions.cs").read_text(encoding="utf-8")
serializer = (root / "ams-shell/src/Ams.UI/Services/PipelineWorkspaceSerializer.cs").read_text(encoding="utf-8")
exporter = (root / "ams-shell/src/Ams.UI/Services/PlanExporter.cs").read_text(encoding="utf-8")
bundle = (root / "ams-shell/src/Ams.UI/Services/PipelinePlanBundle.cs").read_text(encoding="utf-8")

assert "FormatVersion = 7" in model
assert "TimeoutMinSec" in model and "TimeoutMaxSec" in model
assert "WhisperSoundEnabled" in vm
assert "WhisperRepeatSoundEnabled" in vm
assert "صدای Catch داخل استپ صریح Wait For Sound تنظیم می‌شود" in ui
assert 'ProfileCard("اسپلش"' not in ui

# Build 119 restores the explicit per-cast Wait For Sound editor.
assert xaml.count('CommandParameter="waitForSound"') == 3
assert 'CommandParameter="splashListener"' not in xaml
assert '["splashListener"] = new StepDefinition' in steps
assert 'Label = "Splash Listener (Legacy)"' in steps
assert 'Label = "Wait For Sound"' in steps

# Build 95-118 projects migrate back to an explicit node with visible children.
assert "MigrateExplicitCatchWait(workspace)" in serializer
assert 'node.Type == "waitForSound"' in serializer
assert 'node.Type = "waitForSound"' in serializer
assert "StepTreeSerializer.Restore(responseSnapshot)" in serializer
assert "profile.Enabled = false" in serializer

assert 'case "splashListener":EmitSplashListener(n);return;' in exporter
assert "FindCatchWaits" in bundle
assert '"splash_steps.txt", "scoped"' in bundle
assert "catchWait.Children" in bundle
assert "x.ResponseTab == PipelineKind.Whisper" in bundle

print("explicit catch wait + global whisper UX contract passed")