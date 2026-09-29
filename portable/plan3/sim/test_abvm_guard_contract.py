#!/usr/bin/env python3
"""Native Guard descriptor and route compiler contract."""
import json
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[3]))
from tools import abvm

source = json.loads((Path(__file__).resolve().parents[3] /
    "firmware/abvm/tests/abvm_guard_smoke.amsj").read_text())
routes = ("Desktop", "Restart", "Startup", "LoginOrDc", "Dc",
          "CharacterDashboard", "EnteringGameLoading", "Game", "Targeted", "Whisper")
program = abvm.Compiler().compile_amsj(source, routes)
image = abvm.Verifier.verify(program.image)
assert image.flags & abvm.FLAG_HAS_GUARD
guards = [payload for kind, _, payload in image.constants if kind == abvm.CONST_GUARD]
assert len(guards) == 1 and len(guards[0]) == abvm.GUARD_HEADER.size + 6 * abvm.GUARD_PROFILE.size
assert {route.route_id for route in image.routes} >= {1, 2, 3, 4, 5, 6, 7, 8, 9, 10}
events = abvm.ReferenceVm(program.image).run("Game")
assert not any(event[0] == "KEY" for event in events), "forward GOTO must skip X"
assert any(event[0] == "DELAY" for event in events)

bad = json.loads(json.dumps(source))
bad["nativeGuard"]["profiles"][5]["luxCenter"] = 200
try:
    abvm.Compiler().compile_amsj(bad, routes)
except abvm.AbvmError as exc:
    assert "overlap" in str(exc)
else:
    raise AssertionError("overlapping Native Guard profiles were accepted")
print("ABVM native global Guard compiler contract passed")
