#!/usr/bin/env python3
from pathlib import Path
root=Path(__file__).resolve().parents[3]
serializer=(root/'ams-shell/src/Ams.UI/Services/PipelineWorkspaceSerializer.cs').read_text()
ui=(root/'ams-shell/src/Ams.UI/MainWindow.ResumeEssentialsUi.cs').read_text()
schedule=(root/'ams-shell/src/Ams.UI/MainWindow.AutoCycleUi.cs').read_text()
assert '"ResumeEssentials" => null' in serializer
assert 'intentionally installs no UI' in ui
assert 'MarkResumeEssentialsCommand' not in ui
assert 'Resume Essentials' in schedule and 'منسوخ' in schedule
print('Resume Essentials retirement contract passed')
