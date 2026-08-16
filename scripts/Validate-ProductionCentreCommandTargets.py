#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"

# CommandMethod can be [CommandMethod("CE_CMD", ...)] or
# [CommandMethod("CE_TOOLS", "CE_CMD", ...)]. Capture the actual CE command.
OWNER_RE = re.compile(
    r"CommandMethod\s*\(\s*(?:\"[^\"]+\"\s*,\s*)?\"(CE_[A-Z0-9_]+)\"",
    re.IGNORECASE | re.MULTILINE,
)

# Production/menu actions in CE Tools consistently put the command target in
# argument two. Capture only that argument so CE_* names mentioned in notes do
# not become false references.
TARGET_RE = re.compile(
    r"(?:new\s+DisciplineWorkflowAction|\bA|\bAction|\bRoadAction)\s*\(\s*"
    r"\"(?:[^\"\\]|\\.)*\"\s*,\s*\"(CE_[A-Z0-9_]+)\"",
    re.IGNORECASE | re.MULTILINE,
)


def main() -> int:
    if not SRC.is_dir():
        print(f"Civil 3D source folder not found: {SRC}", file=sys.stderr)
        return 2

    owners: dict[str, list[str]] = {}
    targets: dict[str, list[str]] = {}

    for path in sorted(SRC.glob("*.cs")):
        text = path.read_text(encoding="utf-8-sig", errors="replace")
        for match in OWNER_RE.finditer(text):
            command = match.group(1).upper()
            owners.setdefault(command, []).append(path.name)
        for match in TARGET_RE.finditer(text):
            command = match.group(1).upper()
            targets.setdefault(command, []).append(path.name)

    missing = sorted(command for command in targets if command not in owners)
    duplicates = sorted(command for command, files in owners.items() if len(files) > 1)

    print(
        f"Production-centre command audit: owners={len(owners)}; "
        f"menu targets={len(targets)}; missing={len(missing)}."
    )

    if duplicates:
        # Duplicate CommandMethod ownership is already covered by the aggregate
        # owner validator; print it here only as useful context.
        print(f"Command names with multiple source owners={len(duplicates)} (informational).")

    if missing:
        print("Production/menu targets without a live CommandMethod owner:", file=sys.stderr)
        for command in missing:
            files = ", ".join(sorted(set(targets[command])))
            print(f"  {command} <- {files}", file=sys.stderr)
        return 1

    print("All CE production/menu command targets have live source owners.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
