#!/usr/bin/env python3
from pathlib import Path

path = Path("src/CE.Tools.Civil3D/ProjectSetupCommands.cs")
text = path.read_text(encoding="utf-8-sig")
replacements = {
    '"\nCE_PROJECTSETUP cancelled. Existing project metadata was not changed."': '"\\nCE_PROJECTSETUP cancelled. Existing project metadata was not changed."',
    '"\nCE_PROJECTSETUP complete. Project metadata saved inside this DWG."': '"\\nCE_PROJECTSETUP complete. Project metadata saved inside this DWG."',
    '"\nCE_PROJECTSETUP cancelled. Existing metadata was not replaced. {0}"': '"\\nCE_PROJECTSETUP cancelled. Existing metadata was not replaced. {0}"',
}
for old, new in replacements.items():
    if old not in text:
        raise SystemExit(f"Missing Project Setup literal: {old!r}")
    text = text.replace(old, new)
path.write_text(text, encoding="utf-8")
print("Repaired Project Setup popup status string literals.")
