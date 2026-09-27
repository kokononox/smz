#!/usr/bin/env python3
from pathlib import Path

root = Path(__file__).resolve().parents[3]
vm = (root / "ams-shell/src/Ams.UI/ViewModels/MainViewModel.AutoCycleExport.cs").read_text(encoding="utf-8")
modern_vm = (root / "ams-shell/src/Ams.UI/ViewModels/MainViewModel.AutoCycleModern.cs").read_text(encoding="utf-8")
modern_bundle = (root / "ams-shell/src/Ams.UI/Services/ModernAutoCycleFirmwareBundle.cs").read_text(encoding="utf-8")
ui = (root / "ams-shell/src/Ams.UI/MainWindow.AutoCycleExportUi.cs").read_text(encoding="utf-8")
firmware_ui = (root / "ams-shell/src/Ams.UI/MainWindow.AutoCycleFirmwareExportUi.cs").read_text(encoding="utf-8")
essentials_ui = (root / "ams-shell/src/Ams.UI/MainWindow.ResumeEssentialsUi.cs").read_text(encoding="utf-8")
schedule_ui = (root / "ams-shell/src/Ams.UI/MainWindow.AutoCycleUi.cs").read_text(encoding="utf-8")
kit = (root / "ams-shell/src/Ams.UI/MainWindow.AutoCycleStyles.cs").read_text(encoding="utf-8")
tabs_ui = (root / "ams-shell/src/Ams.UI/MainWindow.PipelineTabsUi.cs").read_text(encoding="utf-8")
assert "[RelayCommand]" in vm
assert "ExportAutoCyclePicoPlan" in vm
assert "PipelinePlanBundle.Export" in vm
assert "CapturePipelineWorkspaceForExport" in vm
assert "_currentFile ?? \"untitled\"" in vm
assert "PlanExporter.PlanBlockedException" in vm
assert "ExportAutoCyclePicoPlanCommand" in ui
assert "PlayOptBody" in ui
assert "چرخه‌ی خودکار" in vm and "چرخه‌ی خودکار" in ui
assert "MinHeight = 34" in kit and "MinWidth = 188" in kit
assert "Padding = new Thickness(10, 8, 10, 8)" in kit and "CornerRadius = new CornerRadius(8)" in kit
assert "#5E9FE8" in kit and "#B5BBC5" in kit and "Reorder(body)" in kit
assert "#10151C" in kit and "primary ? OnPrimary : Text" in kit
assert "ReorderExportSteps" in kit and "PlanStepTag" in ui and "FirmwareStepTag" in firmware_ui
assert "WrapPanel EnsureExportCard" in kit and "HorizontalAlignment = HorizontalAlignment.Right" in kit
assert "EnsureAdvancedPanel" in kit and "IsExpanded = false" in kit
assert "intentionally installs no UI" in essentials_ui
assert "EnsureAdvancedPanel(body)" in schedule_ui and "ReorderAdvanced" in schedule_ui
assert "چرخهٔ After و Startup" in schedule_ui
assert "Resume Essentials" in schedule_ui and "منسوخ" in schedule_ui
assert "PostRestartLaunchEnabled" not in schedule_ui and "BuildRangeRow" not in schedule_ui
assert "ساخت و کپی کامل پروژهٔ فعلی به درایو Pico" in ui
assert "ExportAutoCycleModernCommand" in ui and "ExportAutoCycleCompleteCommand" not in ui
assert ui.count("AutoCycleUiKit.Action(") == 1
assert "CapturePipelineWorkspaceForExport()" in modern_vm and "ExportCurrentProject(" in modern_vm
assert "PipelinePlanBundle.Export(" in modern_bundle and "PipelineWorkspaceSerializer.Serialize(workspace)" in modern_bundle
assert "RebuildManifest(stagingDir, manifestNames)" in modern_bundle
assert "ToolTip" in ui and "ToolTip" in firmware_ui
assert "GridUnitType.Star" in schedule_ui
assert "PipelineTabs" in tabs_ui and "Ctrl" not in tabs_ui or "ModifierKeys.Control" in tabs_ui
print("auto-cycle compact UI + pipeline tabs: 26 passed, 0 failed")
