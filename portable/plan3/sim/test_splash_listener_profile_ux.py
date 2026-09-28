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

assert "FormatVersion = 5" in model
assert "TimeoutMinSec" in model and "TimeoutMaxSec" in model
assert "SplashTimeoutMinSec" in vm and "SplashTimeoutMaxSec" in vm
assert "Timeout حداقل هر پرتاب" in ui and "Timeout حداکثر هر پرتاب" in ui

# New-project UI exposes the dedicated scoped marker, not generic Wait For Sound.
assert xaml.count('CommandParameter="splashListener"') == 3
assert 'CommandParameter="waitForSound"' not in xaml
assert '["splashListener"] = new StepDefinition' in steps
assert 'Label = "Wait For Sound (Legacy)"' in steps

# Old responseRoute=splash documents migrate without changing the portable contract.
assert "MigrateScopedSplash(workspace, version)" in serializer
assert 'node.Type = "splashListener"' in serializer
assert 'node.Type == "waitForSound"' in serializer
assert 'responseRoute", "inline") == "splash"' in serializer

assert 'case "splashListener":EmitSplashListener(n);return;' in exporter
assert 'WPROFILE|splash,18000,22000' in exporter
assert "ApplySplashProfileTimeout" in bundle
assert "splash.TimeoutMinSec * 1000" in bundle
assert "splash.TimeoutMaxSec * 1000" in bundle
assert "استپ Splash Listener فقط در تب Game مجاز است" in bundle
assert "برای Splash Listener، پروفایل Splash" in bundle

print("splash listener/profile UX contract passed")