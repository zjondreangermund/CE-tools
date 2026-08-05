#!/usr/bin/env python3
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
OUT = ROOT / "docs" / "analysis" / "PRODUCTION_POPUP_TARGETS.md"
TOKENS = [
    "CE_DRAWINGBOOK", "CE_BOOKINDEX", "CE_CLIENTBOOK", "CE_PROJECTCLOSEOUT",
    "CE_PROJECTSETUP", "BuildTitleBlock", "DrawingRegister", "PromptStringOptions",
    "PromptKeywordOptions", "GetString(", "GetKeywords(", "ProjectSetupPopupWindow"
]

sections = ["# Production popup target scan", ""]
for path in sorted(SRC.glob("*.cs")):
    text = path.read_text(encoding="utf-8-sig")
    hits = [token for token in TOKENS if token in text]
    if not hits:
        continue
    lines = text.splitlines()
    sections.append(f"## {path.name}")
    sections.append("Hits: " + ", ".join(f"`{hit}`" for hit in hits))
    sections.append("")
    match_lines = sorted({i for i, line in enumerate(lines) if any(token in line for token in hits)})
    ranges = []
    for index in match_lines:
        start = max(0, index - 18)
        end = min(len(lines), index + 55)
        if ranges and start <= ranges[-1][1] + 5:
            ranges[-1] = (ranges[-1][0], max(ranges[-1][1], end))
        else:
            ranges.append((start, end))
    for start, end in ranges:
        sections.append(f"### Lines {start + 1}-{end}")
        sections.append("```csharp")
        for number in range(start, end):
            sections.append(f"{number + 1:05d}: {lines[number]}")
        sections.append("```")
        sections.append("")

OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text("\n".join(sections), encoding="utf-8")
print(OUT.relative_to(ROOT))
