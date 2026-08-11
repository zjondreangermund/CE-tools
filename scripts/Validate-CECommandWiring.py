#!/usr/bin/env python3
"""Audit CE Tools command wiring in a staged source tree.

The audit is intentionally generic: it discovers CE_* CommandMethod declarations
and compares them with commands invoked by ribbon/workflow helper calls and
SendStringToExecute command chains. It is designed to run *after* all Civil 3D
2023 staging injectors have modified the disposable source copy.
"""

from __future__ import annotations

import argparse
import collections
import pathlib
import re
import sys


def line_number(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("repo_root", nargs="?", default=".")
    args = parser.parse_args()

    root = pathlib.Path(args.repo_root).resolve()
    source = root / "src" / "CE.Tools.Civil3D"
    if not source.is_dir():
        print(f"CE command wiring audit failed: source folder missing: {source}", file=sys.stderr)
        return 1

    files = sorted(source.glob("*.cs"))
    if not files:
        print("CE command wiring audit failed: no Civil3D C# files found", file=sys.stderr)
        return 1

    declaration_re = re.compile(
        r"CommandMethod\s*\(\s*(?:\"[^\"]+\"\s*,\s*)?\"(?P<cmd>CE_[A-Z0-9_]+)\"",
        re.IGNORECASE | re.MULTILINE,
    )

    # CE commands exposed through the project's UI/workflow helpers. These are
    # deliberately limited to invocation shapes instead of every CE_* string so
    # Xrecord/regapp/schema names are not mistaken for commands.
    helper_re = re.compile(
        r"\b(?:Cmd|Action|RoadAction|WorkflowAction)\s*\(\s*\"[^\"]*\"\s*,\s*\"(?P<cmd>CE_[A-Z0-9_]+)\b",
        re.IGNORECASE | re.MULTILINE,
    )
    discipline_re = re.compile(
        r"(?:new\s+)?DisciplineWorkflowAction\s*\(\s*\"[^\"]*\"\s*,\s*\"(?P<cmd>CE_[A-Z0-9_]+)\b",
        re.IGNORECASE | re.MULTILINE,
    )
    send_re = re.compile(
        r"SendStringToExecute\s*\(\s*\"(?P<body>(?:[^\"\\]|\\.)*)\"",
        re.IGNORECASE | re.MULTILINE,
    )
    ce_token_re = re.compile(r"\bCE_[A-Z0-9_]+\b", re.IGNORECASE)

    declarations: dict[str, list[tuple[pathlib.Path, int]]] = collections.defaultdict(list)
    references: dict[str, list[tuple[pathlib.Path, int, str]]] = collections.defaultdict(list)

    texts: dict[pathlib.Path, str] = {}
    for path in files:
        text = path.read_text(encoding="utf-8-sig")
        texts[path] = text
        for match in declaration_re.finditer(text):
            cmd = match.group("cmd").upper()
            declarations[cmd].append((path, line_number(text, match.start())))

        for label, pattern in (("UI/workflow", helper_re), ("discipline workflow", discipline_re)):
            for match in pattern.finditer(text):
                cmd = match.group("cmd").upper()
                references[cmd].append((path, line_number(text, match.start()), label))

        for match in send_re.finditer(text):
            body = bytes(match.group("body"), "utf-8").decode("unicode_escape")
            for token in ce_token_re.findall(body):
                cmd = token.upper()
                references[cmd].append((path, line_number(text, match.start()), "SendStringToExecute"))

    errors: list[str] = []

    for cmd, owners in sorted(declarations.items()):
        if len(owners) > 1:
            locations = ", ".join(f"{p.name}:{line}" for p, line in owners)
            errors.append(f"Duplicate CommandMethod declaration {cmd}: {locations}")

    # CE_TOOLS is a CommandMethod group name, not an executable command.
    ignored = {"CE_TOOLS"}
    for cmd, refs in sorted(references.items()):
        if cmd in ignored:
            continue
        if cmd not in declarations:
            locations = ", ".join(
                f"{p.name}:{line} ({kind})" for p, line, kind in refs[:8]
            )
            more = "" if len(refs) <= 8 else f" +{len(refs) - 8} more"
            errors.append(f"Referenced CE command has no CommandMethod owner: {cmd}: {locations}{more}")

    # Required staged production-style isolation. If a discipline has no preset,
    # ActivateForProduction must reset to drawing defaults rather than retaining
    # the previous discipline's active selection.
    production_path = source / "August11ProductionCentreCommands.cs"
    preset_path = source / "August11DisciplineStylePresetCommands.cs"
    if production_path.exists() and preset_path.exists():
        production = texts.get(production_path) or production_path.read_text(encoding="utf-8-sig")
        presets = texts.get(preset_path) or preset_path.read_text(encoding="utf-8-sig")
        if "ActivateForProduction" not in presets:
            errors.append("Discipline preset manager is missing ActivateForProduction isolation")
        for discipline in (
            "Platforms", "Roads", "Stormwater", "Sewer", "Water", "Bulk Water", "Parking", "Flood"
        ):
            marker = f'ActivateForProduction(Active() == null ? null : Active().Database, "{discipline}")'
            if marker not in production:
                errors.append(f"Production Centre does not safely activate/reset the {discipline} style preset")

    style_centre = source / "ProjectStyleCenterCommands.cs"
    if style_centre.exists():
        style_text = texts.get(style_centre) or style_centre.read_text(encoding="utf-8-sig")
        for discipline in ("Roads", "Stormwater", "Sewer", "Water", "Platforms", "Bulk Water", "Parking", "Flood"):
            if f'"{discipline}"' not in style_text:
                errors.append(f"Project Style Centre is missing production discipline choice: {discipline}")
        if "August11DisciplineStylePresetManager.SavePreset(document.Database, selection);" not in style_text:
            errors.append("Project Style Centre save does not snapshot the selected discipline preset")

    if errors:
        print("CE command wiring audit FAILED:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        print(
            f"\nDeclarations={len(declarations)}; referenced commands={len(references)}; files={len(files)}",
            file=sys.stderr,
        )
        return 1

    print(
        "CE command wiring audit passed: "
        f"{len(declarations)} unique CE CommandMethod declarations, "
        f"{len(references)} referenced CE commands, {len(files)} Civil3D source files; "
        "no duplicate owners, no missing UI/workflow/SendString command targets, and discipline style isolation is wired."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
