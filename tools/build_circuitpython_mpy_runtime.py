#!/usr/bin/env python3
"""Precompile the Game runtime with CircuitPython 10.3.0 mpy-cross.

The source tree keeps .py entries so ordinary development and host tests remain
readable. The release workflow runs this tool before the app build; it replaces
only the eight Game entries in SHA256SUMS with their generated .mpy counterparts.
Classroom Studio then exports exactly the manifest-selected representation.
"""
from __future__ import annotations

import argparse
import hashlib
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / "portable" / "plan3" / "CIRCUITPY-MODERN"
MANIFEST = RUNTIME / "SHA256SUMS.txt"
GAME_MODULES = (
    "plan_engine_game",
    "plan_engine_game_core",
    "plan_engine_game_runtime",
    "plan_engine_game_actions",
    "plan_engine_game_events",
    "plan_engine_game_response",
    "plan_engine_game_parallel",
    "plan_engine_game_sound",
)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--compiler", required=True, type=Path)
    args = parser.parse_args()
    compiler = args.compiler.resolve()
    if not compiler.is_file():
        raise SystemExit("mpy-cross compiler not found: " + str(compiler))

    replacements: dict[str, str] = {}
    for stem in GAME_MODULES:
        source = RUNTIME / (stem + ".py")
        target = RUNTIME / (stem + ".mpy")
        target.unlink(missing_ok=True)
        subprocess.run(
            [str(compiler), "-o", str(target), str(source)],
            check=True,
        )
        data = target.read_bytes()
        # CircuitPython uses a `C` header (MicroPython uses `M`).
        if not data or data[:1] != b"C":
            raise RuntimeError("invalid mpy-cross output: " + target.name)
        replacements[source.name] = target.name

    lines: list[str] = []
    seen: set[str] = set()
    for raw in MANIFEST.read_text(encoding="utf-8").splitlines():
        if not raw.strip():
            continue
        _, name = raw.split("  ", 1)
        name = replacements.get(name, name)
        path = RUNTIME / name
        if not path.is_file() or name in seen:
            raise RuntimeError("invalid transformed manifest entry: " + name)
        seen.add(name)
        lines.append(sha256(path) + "  " + name)
    if len(lines) != 44 or set(replacements.values()) - seen:
        raise RuntimeError("modern manifest inventory mismatch after mpy build")
    MANIFEST.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("CircuitPython 10.3.0 Game MPY runtime built:", len(replacements), "modules")


if __name__ == "__main__":
    main()