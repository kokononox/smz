#!/usr/bin/env python3
"""Regression: worker must close over main.state, never shadow it locally."""
import ast
from pathlib import Path

root = Path(__file__).resolve().parents[3]
source = (root / "ams-shell/bridge/bridge.py").read_text(encoding="utf-8")
tree = ast.parse(source)
main = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "main")
worker = next(n for n in main.body if isinstance(n, ast.FunctionDef) and n.name == "worker")
local_state_writes = [
    n for n in ast.walk(worker)
    if isinstance(n, ast.Name) and n.id == "state" and isinstance(n.ctx, ast.Store)
]
assert not local_state_writes, (
    "worker shadows main.state; any assignment makes every state[\"link\"] read "
    "raise UnboundLocalError during connect"
)
assert "arm_usb_state = presence.split" in source
print("bridge worker state-scope regression: PASS")
