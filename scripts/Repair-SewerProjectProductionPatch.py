#!/usr/bin/env python3
from pathlib import Path
import re

path = Path(__file__).with_name("Apply-SewerProjectProductionPopupFix.py")
text = path.read_text(encoding="utf-8")
pattern = re.compile(
    r"old_title = '''.*?production = replace_once\(production, old_title, new_title, \"linked drawing title\"\)\n",
    re.S,
)
replacement = r"""production = replace_regex(
    production,
    r'''                title\.Contents = string\.Join\(.*?                AddBookGenerated\(transaction, paperSpace, title, package\.LayoutName, generated\);''',
    r'''                title.Contents = string.Join(
                    "\\\\P",
                    registerRow.DrawingNumber + "  |  " + registerRow.Title.ToUpperInvariant(),
                    ValueOrNotSet(drawingRegister.Header("Project Name")) +
                        "  |  " + ValueOrNotSet(drawingRegister.Header("Client")),
                    registerRow.Paper + " | Scale " + registerRow.Scale +
                        " | Stage " + registerRow.Stage +
                        " | Rev " + registerRow.Revision +
                        " | " + registerRow.IssueDate);
                AddBookGenerated(transaction, paperSpace, title, package.LayoutName, generated);''',
    "linked drawing title",
    flags=re.S)
"""
updated, count = pattern.subn(lambda match: replacement, text, count=1)
if count != 1:
    raise SystemExit(f"Could not repair drawing-title matcher; matches={count}")
path.write_text(updated, encoding="utf-8")
print("Repaired drawing-title patch matcher.")
