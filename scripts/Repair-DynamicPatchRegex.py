#!/usr/bin/env python3
"""Make the temporary dynamic integration patch use exact method boundaries."""

from pathlib import Path

path = Path(__file__).resolve().parent / "Apply-DynamicCogoVertexSewerFix.py"
text = path.read_text(encoding="utf-8-sig")
old = '''def regex_once(path: Path, pattern: str, replacement: str) -> None:
    text = read(path)
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise SystemExit(f"Expected one regex match in {path}; found {count}: {pattern[:100]}")
    write(path, updated)
'''
new = '''def regex_once(path: Path, pattern: str, replacement: str) -> None:
    text = read(path)
    boundaries = None
    if "ResultBuffer LinkBuffer" in pattern:
        boundaries = (
            "        private static ResultBuffer LinkBuffer",
            "        private static bool TryReadEntityLink",
        )
    elif "ResolveSelectedPointStyles" in pattern:
        boundaries = (
            "        private static void ResolveSelectedPointStyles",
            "        private static string ReadSelectedStyle",
        )
    elif "IsBranchOnePipe" in pattern:
        boundaries = (
            "            candidates = candidates.Where",
            "        private static SewerBranchPath BuildSimplePath",
        )
    if boundaries is not None:
        start = text.find(boundaries[0])
        end = text.find(boundaries[1], start + 1)
        if start >= 0 and end > start:
            suffix_start = end + len(boundaries[1])
            write(path, text[:start] + replacement + text[suffix_start:])
            return
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count == 1:
        write(path, updated)
        return
    raise SystemExit(
        f"Expected one exact method-boundary match in {path}: {pattern[:100]}")
'''
if new not in text:
    if old not in text:
        raise SystemExit("Temporary regex helper marker was not found")
    text = text.replace(old, new, 1)
path.write_text(text, encoding="utf-8")
print("Enabled exact method-boundary patching for dynamic integration.")
