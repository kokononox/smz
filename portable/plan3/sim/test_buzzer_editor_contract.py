from pathlib import Path

root = Path(__file__).resolve().parents[3]
defs = (root / "ams-shell/src/Ams.UI/Models/StepDefinitions.cs").read_text()
dialog = (root / "ams-shell/src/Ams.UI/Views/StepDialog.xaml.cs").read_text()
vm = (root / "ams-shell/src/Ams.UI/ViewModels/MainViewModel.cs").read_text()
exporter = (root / "ams-shell/src/Ams.UI/Services/PicoFirmwareExporter.cs").read_text()
main = (root / "firmware/abvm/pico/main.c").read_text()
buzzer = (root / "firmware/abvm/pico/buzzer.c").read_text()

for preset in ("notification", "error", "rising", "falling"):
    assert f'"{preset}"' in defs
for prop in ('new("volume"', 'new("envelope"'):
    assert prop in defs
for envelope in ("sharp", "smooth", "fade-in", "fade-out"):
    assert f'"{envelope}"' in defs

assert "شنیدن صدای انتخاب‌شده روی بازر" in dialog
assert "PreviewBuzzerAsync" in vm
assert 'BuildBuzzerCommands(values)' in vm
assert 'BEEP|{freq},{duration},{volume},{envelope}' in defs

assert 'len(fields) < 2 or len(fields) > 4' in exporter
assert 'buzzer_play_tone_ex' in main
assert 'ENVELOPE_UPDATE_MS 4u' in buzzer
assert 'tone_on_level' in buzzer

print("buzzer editor contract: presets, volume, envelopes and hardware preview passed")