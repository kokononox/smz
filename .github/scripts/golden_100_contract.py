#!/usr/bin/env python3
from hashlib import sha256
from pathlib import Path
import re
import sys

root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('portable/plan3/CIRCUITPY')
expected_files = {
    'README-FLASH.md', 'SHA256SUMS.txt', 'autocycle.amsj', 'boot.py',
    'boot_out.txt', 'character_dashboard_steps.txt', 'code.py',
    'combined_guard_runtime.py', 'desktop_steps.txt',
    'entering_game_loading_steps.txt', 'error_policy.py', 'game_steps.txt',
    'guard-calibration.json', 'guard-transition.json',
    'guard_calibration_protocol.py', 'guard_transition.py',
    'live_light_guard.py', 'login_or_dc_steps.txt', 'pico-calibration.json',
    'plan.txt', 'plan_engine.py', 'restart_steps.txt',
    'resumable_steps.txt', 'settings.toml', 'targeted_steps.txt',
}
expected_manifest = {
    'boot.py', 'character_dashboard_steps.txt', 'code.py',
    'combined_guard_runtime.py', 'desktop_steps.txt',
    'entering_game_loading_steps.txt', 'error_policy.py', 'game_steps.txt',
    'guard-calibration.json', 'guard-transition.json',
    'guard_calibration_protocol.py', 'guard_transition.py',
    'live_light_guard.py', 'login_or_dc_steps.txt', 'pico-calibration.json',
    'plan.txt', 'plan_engine.py', 'README-FLASH.md',
    'restart_steps.txt', 'resumable_steps.txt', 'targeted_steps.txt',
}
actual_files = {p.name for p in root.iterdir() if p.is_file()}
if actual_files != expected_files:
    raise SystemExit(f'inventory mismatch: actual-only={sorted(actual_files - expected_files)} expected-only={sorted(expected_files - actual_files)}')
rows = []
for line in (root / 'SHA256SUMS.txt').read_text(encoding='utf-8').splitlines():
    if line.strip():
        digest, name = line.split(None, 1)
        rows.append((digest, name))
if len(rows) != 21 or {name for _, name in rows} != expected_manifest:
    raise SystemExit('SHA256SUMS.txt must contain exactly the 21 Golden-100 entries')
for digest, name in rows:
    actual = sha256((root / name).read_bytes()).hexdigest()
    if actual != digest:
        raise SystemExit(f'SHA256 mismatch: {name}: expected {digest}, got {actual}')
py = list(root.glob('*.py'))
text = '\n'.join(p.read_text(encoding='utf-8', errors='ignore') for p in py)
if re.search(r'^\s*(?:from|import)\s+adafruit_hid\b', text, re.MULTILINE):
    raise SystemExit('adafruit_hid import found')
code = (root / 'code.py').read_text(encoding='utf-8')
for marker in ('_run_light_route', 'fh.readline()', 'self.guard.last_decision = None'):
    if marker not in code:
        raise SystemExit(f'missing Golden 100 marker: {marker}')
print('Golden 100 inventory: 25 files')
print('Golden 100 SHA256 manifest: 21/21 verified')
print('Golden 100 runtime markers and no adafruit_hid import: PASS')
