#!/usr/bin/env python3
"""Correct raw-regex escaping in the temporary dynamic integration patch."""

from pathlib import Path

path = Path(__file__).resolve().parent / "Apply-DynamicCogoVertexSewerFix.py"
text = path.read_text(encoding="utf-8-sig")
replacements = {
    r'r"        private static ResultBuffer LinkBuffer\\(string type, string groupId, string key\\)\\n        \\{.*?\\n        \\}\\n\\n        private static bool TryReadEntityLink"':
        r'r"        private static ResultBuffer LinkBuffer\(string type, string groupId, string key\)\n        \{.*?\n        \}\n\n        private static bool TryReadEntityLink"',
    r'r"        private static void ResolveSelectedPointStyles\\(\\n            Database database,\\n            Transaction transaction,\\n            out ObjectId pointStyleId,\\n            out ObjectId pointLabelStyleId\\)\\n        \\{.*?\\n        \\}\\n\\n        private static string ReadSelectedStyle"':
        r'r"        private static void ResolveSelectedPointStyles\(\n            Database database,\n            Transaction transaction,\n            out ObjectId pointStyleId,\n            out ObjectId pointLabelStyleId\)\n        \{.*?\n        \}\n\n        private static string ReadSelectedStyle"',
    r'r"            candidates = candidates\\.Where\\(edge =>\\n            \\{\\n                return edge != null && IsBranchOnePipe\\(edge\\.Id, topology\\);\\n            \\}\\)\\.ToList\\(\\);\\n            return BuildSimplePath\\(topology, candidates\\);\\n        \\}\\n\\n        private static bool IsBranchOnePipe\\(.*?\\n        \\}\\n\\n        private static SewerBranchPath BuildSimplePath"':
        r'r"            candidates = candidates\.Where\(edge =>\n            \{\n                return edge != null && IsBranchOnePipe\(edge\.Id, topology\);\n            \}\)\.ToList\(\);\n            return BuildSimplePath\(topology, candidates\);\n        \}\n\n        private static bool IsBranchOnePipe\(.*?\n        \}\n\n        private static SewerBranchPath BuildSimplePath"',
}
for old, new in replacements.items():
    if old in text:
        text = text.replace(old, new, 1)
    elif new not in text:
        raise SystemExit(f"Regex repair marker not found: {old[:100]}")
path.write_text(text, encoding="utf-8")
print("Corrected dynamic integration regex matchers.")
