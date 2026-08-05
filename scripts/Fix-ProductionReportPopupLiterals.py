#!/usr/bin/env python3
from pathlib import Path
import re

path = Path("src/CE.Tools.Civil3D/ProductionReportCommands.cs")
text = path.read_text(encoding="utf-8-sig")
pattern = re.compile(
    r'"[ \t]*\r?\n[ \t]*(CE_(?:DRAWINGBOOK|BOOKINDEX) (?:complete|failed)\.[^"\r\n]*)"'
)
text, count = pattern.subn(lambda match: '"\\n' + match.group(1) + '"', text)
if count != 4:
    raise SystemExit(f"Expected four broken production status literals; repaired {count}.")
path.write_text(text, encoding="utf-8")
print("Repaired four drawing-book and book-index status string literals.")
