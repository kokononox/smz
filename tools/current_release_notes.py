#!/usr/bin/env python3
import argparse
from pathlib import Path

REQUIRED = (
    "**Previous build:**",
    "**Status:**",
    "### Problem observed",
    "### Root cause",
    "### Change",
    "### Validation",
    "### Next test",
)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--changelog", default="CHANGELOG-CURRENT.md")
    parser.add_argument("--output", default="release-notes.md")
    parser.add_argument("--build-number", required=True)
    parser.add_argument("--sha", required=True)
    args = parser.parse_args()

    path = Path(args.changelog)
    lines = path.read_text(encoding="utf-8").splitlines()
    starts = [i for i, line in enumerate(lines) if line.startswith("## Build ")]
    if not starts:
        raise SystemExit("CHANGELOG-CURRENT.md has no Build section")
    start = starts[0]
    end = next((i for i in starts[1:] if i > start), len(lines))
    section = "\n".join(lines[start:end]).strip()
    missing = [token for token in REQUIRED if token not in section]
    if missing:
        raise SystemExit("current changelog entry is incomplete: " + ", ".join(missing))
    section = section.replace("{{BUILD_NUMBER}}", args.build_number)
    section = section.replace("{{COMMIT_SHA}}", args.sha)
    section += (
        "\n\n[Full hardware changelog](https://github.com/noonoix/smz/blob/"
        + args.sha + "/CHANGELOG-CURRENT.md)\n"
    )
    Path(args.output).write_text(section, encoding="utf-8")
    print("release notes generated for build", args.build_number)


if __name__ == "__main__":
    main()
