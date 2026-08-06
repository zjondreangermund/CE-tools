#!/usr/bin/env python3
"""Make the temporary dynamic integration patch tolerant of escaped regex text."""

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
    candidates = [pattern]
    normalized = pattern.encode("utf-8").decode("unicode_escape")
    if normalized != pattern:
        candidates.append(normalized)
    for candidate in candidates:
        updated, count = re.subn(candidate, replacement, text, count=1, flags=re.S)
        if count == 1:
            write(path, updated)
            return
    raise SystemExit(
        f"Expected one regex match in {path}; tried {len(candidates)} forms: {pattern[:100]}")
'''
if new not in text:
    if old not in text:
        raise SystemExit("Temporary regex helper marker was not found")
    text = text.replace(old, new, 1)
path.write_text(text, encoding="utf-8")
print("Enabled normalized regex fallback for dynamic integration patch.")
