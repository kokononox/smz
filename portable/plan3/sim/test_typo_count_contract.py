#!/usr/bin/env python3
"""Human typo interval contract.

The persisted Studio keys remain typoEveryMin/Max for .amsj compatibility.
New portable plans use typochars=min,max, meaning the eligible-character
distance between corrections. Legacy typos=count and typo=word-cadence plans
remain readable.
"""
from pathlib import Path
import importlib.util
import random
import sys

ROOT = Path(__file__).resolve().parents[1]
MODERN = ROOT / "CIRCUITPY-MODERN"
SPLIT = ROOT / "CIRCUITPY-SPLIT"
REPO = ROOT.parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


sys.path.insert(0, str(MODERN))
for module_name in ("plan_engine_parse", "plan_engine_human"):
    sys.modules.pop(module_name, None)
parse = __import__("plan_engine_parse")
human = __import__("plan_engine_human")
split_typing = load("typo_interval_split", SPLIT / "plan_typing.py")


def replay(commands):
    text = ""
    backspaces = 0
    for command in commands:
        if command[0] == "KTEXT":
            text += command[3]
        elif command[0] == "KCOMBO" and command[1] == 8:
            text = text[:-1]
            backspaces += 1
    return text, backspaces


plan = parse.parse_plan(
    "PLAN|2\nTYPE|text=zodiak999999|h=111,250|typochars=7,12"
)
params = plan[1][1]
assert params["typochars"] == (7, 12)

for planner in (human.plan_typing, split_typing.plan_typing):
    # A 12-character password gets one correction, never 7..12 corrections.
    for seed in range(40):
        random.seed(seed)
        final_text, count = replay(planner(params["text"], params))
        assert final_text == "zodiak999999"
        assert count == 1, (seed, count)

    # Intervals are re-rolled and remain within the configured character bounds.
    text = "abcdefghijklmnopqrstuvwxyz"
    observed = set()
    for seed in range(80):
        random.seed(seed)
        commands = planner(text, {"h": (1, 1), "typochars": (7, 12)})
        final_text, count = replay(commands)
        assert final_text == text
        assert 2 <= count <= 3, (seed, count)
        observed.add(count)
    assert observed == {2, 3}, observed

    # Legacy explicit count plans remain readable.
    random.seed(7)
    final_text, count = replay(planner("abcdef", {"h": (1, 1), "typos": (2, 2)}))
    assert final_text == "abcdef" and count == 2

    # Legacy every-N-words wire contract remains readable.
    random.seed(11)
    final_text, count = replay(planner("one two", {"h": (1, 1), "typo": (1, 1)}))
    assert final_text == "one two" and count == 2


route = (MODERN / "login_or_dc_steps.txt").read_text(encoding="utf-8")
assert "|typochars=3,5" in route and "|typos=3,5" not in route

exporter = (REPO / "ams-shell/src/Ams.UI/Services/PlanExporter.cs").read_text(encoding="utf-8")
assert 'parts.Add("typochars=" + y0 + "," + y1)' in exporter
assert '"typochars"' in (MODERN / "plan_engine_parse.py").read_text(encoding="utf-8")
assert "typo_char_mode = typo_char_max > 0" in (
    MODERN / "plan_engine_human.py").read_text(encoding="utf-8")

labels = (REPO / "ams-shell/src/Ams.UI/Models/StepTextsFa.cs").read_text(encoding="utf-8")
assert "فاصلهٔ بین خطاهای تایپی" in labels

generator = (ROOT / "tools/plan_gen.py").read_text(encoding="utf-8")
assert 'parts.append("typochars=%d,%d"' in generator

print("human typo character interval contract: PASS")