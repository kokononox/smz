from pathlib import Path
import runpy

root = Path(__file__).resolve().parents[1]
repo = root.parents[1]
runpy.run_path(str(repo / "tools" / "apply_calibration_save_heap_fix.py"), run_name="__main__")
code = (root / "CIRCUITPY-MODERN" / "code.py").read_text(encoding="utf-8")
assert 'def _prepare_calibration_heap(self):' in code
assert 'def _release_plan_heap(self, emit_cal=False):' in code
assert 'proxy.module = None' in code
for name in ('plan_engine_exec', 'plan_engine_human', 'plan_engine_login', 'plan_engine_parallel', 'plan_engine_parse'):
    assert name in code
assert code.count('_prepare_calibration_heap(self)') == 4
assert 'self.samples = []\n        _prepare_calibration_heap(self)' in code
manifest = (root / "CIRCUITPY-MODERN" / "SHA256SUMS.txt").read_text(encoding="utf-8").splitlines()
assert len(manifest) == 26
assert any(line.endswith('  plan_engine_login.py') for line in manifest)
assert any(line.endswith('  plan_engine_parallel.py') for line in manifest)
print('calibration heap release contract: PASS')

workflow = (repo / ".github" / "workflows" / "classroom-studio-current.yml").read_text(encoding="utf-8")
assert 'apply_calibration_save_heap_fix.py' in workflow
assert 'test_calibration_heap_release.py' in workflow
print('Classroom Studio calibration build contract: PASS')
