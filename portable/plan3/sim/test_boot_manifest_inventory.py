#!/usr/bin/env python3
"""Boot verifier must accept every file emitted in the modern manifest."""

import ast
import importlib.util
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
RUNTIME = ROOT / "portable/plan3/CIRCUITPY-MODERN"

spec = importlib.util.spec_from_file_location(
    "modern_live_light_guard", RUNTIME / "live_light_guard.py"
)
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)

tree = ast.parse((RUNTIME / "code.py").read_text(encoding="utf-8"))
bootstrap_files = None
bootstrap_stems = None
for node in ast.walk(tree):
    if not isinstance(node, ast.For):
        continue
    if not isinstance(node.target, ast.Name):
        continue
    if isinstance(node.iter, (ast.Tuple, ast.List)):
        values = [ast.literal_eval(item) for item in node.iter.elts]
        if all(isinstance(value, str) for value in values):
            if node.target.id == "_name":
                bootstrap_files = values
            elif node.target.id == "_stem":
                bootstrap_stems = values

assert bootstrap_files is not None, "boot manifest extension inventory not found"
assert bootstrap_stems is not None, "boot dynamic PY/MPY inventory not found"

runtime_inventory = set(guard.HASHED_BUNDLE_FILES)
runtime_inventory.update(bootstrap_files)
runtime_inventory.update(
    stem + (".mpy" if (RUNTIME / (stem + ".mpy")).is_file() else ".py")
    for stem in bootstrap_stems
)
manifest_inventory = {
    line.split("  ", 1)[1].strip()
    for line in (RUNTIME / "SHA256SUMS.txt").read_text(encoding="utf-8").splitlines()
    if line.strip()
}

assert manifest_inventory == runtime_inventory, (
    "boot/manifest inventory mismatch: "
    f"missing={sorted(manifest_inventory - runtime_inventory)} "
    f"unexpected={sorted(runtime_inventory - manifest_inventory)}"
)
assert {"whisper_steps.txt", "splash_steps.txt"} <= runtime_inventory

print("Modern boot manifest inventory accepted, including dynamic Game PY/MPY")
