#!/usr/bin/env python3
from pathlib import Path
root=Path(__file__).resolve().parents[3]
settings=(root/'ams-shell/src/Ams.UI/Services/DocumentService.cs').read_text()
ui=(root/'ams-shell/src/Ams.UI/MainWindow.AutoCycleUi.cs').read_text()
bundle=(root/'ams-shell/src/Ams.UI/Services/PipelinePlanBundle.cs').read_text()
for legacy in ('RestartMinMinutes','AutoResumeMinMinutes','PostRestartTaskbarSlot','PostRestartLaunchEnabled'):
    assert legacy in settings  # migration compatibility only
for retired in ('AutoResumeMinMinutes','PostRestartTaskbarSlot','PostRestartLaunchEnabled'):
    assert f'nameof(MainViewModel.{retired})' not in ui
assert 'چرخهٔ After و Startup' in ui
assert 'RestartMinMinutes' in ui and 'RestartMaxMinutes' in ui
assert 'PostRestartLaunchEnabled' not in ui
assert 'StripRetiredCycleHeaders' in bundle
assert 'RUNFOR|' in bundle and 'AUTORESUME|' in bundle and 'POSTLAUNCH|' in bundle
assert '!line.StartsWith("RUNFOR|"' not in bundle
print('route-driven AutoCycle UI: cycle window visible; retired headers stripped passed')
