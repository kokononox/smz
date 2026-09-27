#!/usr/bin/env python3
from pathlib import Path
root=Path(__file__).resolve().parents[3]
settings=(root/'ams-shell/src/Ams.UI/Services/DocumentService.cs').read_text()
ui=(root/'ams-shell/src/Ams.UI/MainWindow.AutoCycleUi.cs').read_text()
bundle=(root/'ams-shell/src/Ams.UI/Services/PipelinePlanBundle.cs').read_text()
for legacy in ('RestartMinMinutes','AutoResumeMinMinutes','PostRestartTaskbarSlot','PostRestartLaunchEnabled'):
    assert legacy in settings  # migration compatibility only
    assert f'nameof(MainViewModel.{legacy})' not in ui
assert 'چرخهٔ After و Startup' in ui
assert 'PostRestartLaunchEnabled' not in ui and 'BuildRangeRow' not in ui
assert 'StripLegacyCycleHeaders' in bundle
assert 'RUNFOR|' in bundle and 'AUTORESUME|' in bundle and 'POSTLAUNCH|' in bundle
print('route-driven AutoCycle UI: legacy settings hidden and headers stripped passed')
