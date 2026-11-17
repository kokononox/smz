#!/usr/bin/env python3
"""Regression test for shifted ASCII typing in the portable CircuitPython runtime."""
import ast
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
source_path = ROOT / "portable/plan3/CIRCUITPY/combined_guard_runtime.py"
tree = ast.parse(source_path.read_text(encoding="utf-8"))
combined = next(node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == "Combined")
methods = [node for node in combined.body if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)) and node.name in ("_text_key", "type_text")]
assert {node.name for node in methods} == {"_text_key", "type_text"}
probe = ast.ClassDef(name="TypingProbe", bases=[], keywords=[], body=methods, decorator_list=[], type_params=[])
namespace = {}
module = ast.fix_missing_locations(ast.Module(body=[probe], type_ignores=[]))
exec(compile(module, str(source_path), "exec"), namespace)

class FakeKeyboard:
    def __init__(self):
        self.down = set()
        self.events = []
    def press(self, *codes):
        for code in codes:
            self.down.add(code)
            self.events.append(("press", code, frozenset(self.down)))
    def release(self, *codes):
        for code in codes:
            self.down.discard(code)
            self.events.append(("release", code, frozenset(self.down)))

class FakeContext:
    def __init__(self): self.delays = []
    def gate(self): return True
    def sleep_ms(self, milliseconds): self.delays.append(milliseconds); return True

class Runtime(namespace["TypingProbe"]):
    def __init__(self): self.keyboard = FakeKeyboard()

# Check every US-layout punctuation pair, letter case, digits, and whitespace.
text = "aA1!@#$%^&*()_-+=[]{}\\|;:'\"`~,.<>/?  \n\t\b"
runtime = Runtime()
context = FakeContext()
runtime.type_text(text, 0, 0, context)

shifted_digits = "!@#$%^&*()"
punctuation = "-_=+[{]}\\|;:'\"`~,.<>/?"
punctuation_usages = (45, 46, 47, 48, 49, 51, 52, 53, 54, 55, 56)
expected = []
for ch in text:
    value = ord(ch)
    if 97 <= value <= 122: expected.append((value - 93, False))
    elif 65 <= value <= 90: expected.append((value - 61, True))
    elif 49 <= value <= 57: expected.append((value - 19, False))
    elif value == 48: expected.append((39, False))
    elif ch == " ": expected.append((44, False))
    elif ch in ("\n", "\r"): expected.append((40, False))
    elif ch == "\t": expected.append((43, False))
    elif ch == "\b": expected.append((42, False))
    elif ch in shifted_digits:
        index = shifted_digits.index(ch)
        expected.append((30 + index if index < 9 else 39, True))
    else:
        index = punctuation.index(ch)
        expected.append((punctuation_usages[index // 2], bool(index & 1)))

key_downs = [(code, state) for action, code, state in runtime.keyboard.events if action == "press" and code < 224]
assert len(key_downs) == len(expected), (len(key_downs), len(expected))
for (code, state), (expected_code, shifted) in zip(key_downs, expected):
    assert code == expected_code, (code, expected_code)
    assert (225 in state) == shifted, (code, shifted, state)
assert not runtime.keyboard.down, runtime.keyboard.down
assert context.delays.count(10) == len(text), context.delays

# An abort during the key-down interval still releases both the key and Shift.
class AbortDuringHold(FakeContext):
    def sleep_ms(self, milliseconds):
        self.delays.append(milliseconds)
        return milliseconds != 10

aborted_runtime = Runtime()
try:
    aborted_runtime.type_text("A", 0, 0, AbortDuringHold())
except RuntimeError as exc:
    assert str(exc) == "route aborted"
else:
    raise AssertionError("typing abort was ignored")
assert not aborted_runtime.keyboard.down, aborted_runtime.keyboard.down
assert [event[:2] for event in aborted_runtime.keyboard.events] == [
    ("press", 225), ("press", 4), ("release", 4), ("release", 225)
]

# Unsupported Unicode must fail closed, not become Enter or another key.
try:
    runtime.type_text("é", 0, 0, FakeContext())
except ValueError:
    pass
else:
    raise AssertionError("non-ASCII text was silently mapped")
print("type_text shift regression: 45 checks passed, 0 failed")
