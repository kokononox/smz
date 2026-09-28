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
for node in ast.walk(tree):
    if not isinstance(node, ast.For):
        continue
    if not isinstance(node.target, ast.Name) or node.target.id != "_name":
        continue
    if isinstance(node.iter, (ast.Tuple, ast.List)):
        values = [ast.literal_eval(item) for item in node.iter.elts]
        if all(isinstance(value, str) for value in values):
            bootstrap_files = values
            break

assert bootstrap_files is not None, "boot manifest extension inventory not found"

runtime_inventory = set(guard.HASHED_BUNDLE_FILES)
runtime_inventory.update(bootstrap_files)
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

print("Modern boot manifest inventory: 35/35 accepted, including Whisper and Splash")
