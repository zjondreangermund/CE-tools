#!/usr/bin/env python3
"""Apply the targeted Civil 3D 2023 compile fixes reported by the Windows build."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
COGO = ROOT / "src" / "CE.Tools.Civil3D" / "CogoPointProjectStyleCommands.cs"

text = COGO.read_text(encoding="utf-8")
old = "offset = Vector3d.Zero;"
new = "offset = new Vector3d(0.0, 0.0, 0.0);"

if old in text:
    text = text.replace(old, new, 1)
elif new not in text:
    raise SystemExit("Expected Vector3d.Zero assignment was not found.")

COGO.write_text(text, encoding="utf-8")
print("Civil 3D 2023 compile hotfix applied.")
