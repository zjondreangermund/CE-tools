#!/usr/bin/env python3
"""Audit CE Tools command and behavioral wiring in a staged source tree.

Run this after all Civil 3D 2023 staging injectors. It discovers CE CommandMethod
owners, checks UI/workflow/SendString targets, and guards the behavioral wiring
that field testing showed can look connected while still acting incorrectly.
"""

from __future__ import annotations

import argparse
import collections
import pathlib
import re
import sys


def line_number(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


def read(texts: dict[pathlib.Path, str], path: pathlib.Path) -> str:
    return texts.get(path) or (path.read_text(encoding="utf-8-sig") if path.exists() else "")


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
            declarations[match.group("cmd").upper()].append((path, line_number(text, match.start())))

        for label, pattern in (("UI/workflow", helper_re), ("discipline workflow", discipline_re)):
            for match in pattern.finditer(text):
                references[match.group("cmd").upper()].append(
                    (path, line_number(text, match.start()), label)
                )

        for match in send_re.finditer(text):
            # CE command tokens themselves never require C# escape decoding.
            for token in ce_token_re.findall(match.group("body")):
                references[token.upper()].append(
                    (path, line_number(text, match.start()), "SendStringToExecute")
                )

    errors: list[str] = []

    for cmd, owners in sorted(declarations.items()):
        if len(owners) > 1:
            locations = ", ".join(f"{p.name}:{line}" for p, line in owners)
            errors.append(f"Duplicate CommandMethod declaration {cmd}: {locations}")

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

    # Discipline style isolation: opening a discipline with no saved preset must
    # reset to its own drawing defaults rather than inherit the previous one.
    production_path = source / "August11ProductionCentreCommands.cs"
    preset_path = source / "August11DisciplineStylePresetCommands.cs"
    production = read(texts, production_path)
    presets = read(texts, preset_path)
    if "ActivateForProduction" not in presets or "var clean = new ProjectStyleSelection" not in presets:
        errors.append("Discipline preset manager is missing clean-default ActivateForProduction isolation")
    for discipline in (
        "Platforms", "Roads", "Stormwater", "Sewer", "Water", "Bulk Water", "Parking", "Flood"
    ):
        marker = f'ActivateForProduction(Active() == null ? null : Active().Database, "{discipline}")'
        if marker not in production:
            errors.append(f"Production Centre does not safely activate/reset the {discipline} style preset")

    style_path = source / "ProjectStyleCenterCommands.cs"
    style_text = read(texts, style_path)
    for discipline in ("Roads", "Stormwater", "Sewer", "Water", "Platforms", "Bulk Water", "Parking", "Flood"):
        if f'"{discipline}"' not in style_text:
            errors.append(f"Project Style Centre is missing production discipline choice: {discipline}")
    if "August11DisciplineStylePresetManager.SavePreset(document.Database, selection);" not in style_text:
        errors.append("Project Style Centre save does not snapshot the selected discipline preset")

    # Linked multi-surface tables must refresh regardless of active Model/Layout tab.
    survey = read(texts, source / "August11SurveyRuntimeCommands.cs")
    if "if (space == null || !space.IsLayout) continue;" not in survey:
        errors.append("Multi-surface coordinate refresh still scans only the current space")
    if "document.Database.CurrentSpaceId" in re.search(
        r"(?s)internal static int RefreshMultiSurfaceTables\(Document document\).*?(?=private static Table BuildMultiSurfaceTable)",
        survey,
    ).group(0) if re.search(
        r"(?s)internal static int RefreshMultiSurfaceTables\(Document document\).*?(?=private static Table BuildMultiSurfaceTable)",
        survey,
    ) else "":
        errors.append("RefreshMultiSurfaceTables still depends on CurrentSpaceId")

    # Road names should use CE source-handle metadata before any spatial fallback.
    road_names = read(texts, source / "August11RoadNamingCurveCommands.cs")
    for marker in (
        "ReadRoadProductionSource(entity)",
        "ReadRoadLayoutParent(entity, transaction)",
        "string.Equals(label.SourceHandle, productionSource, StringComparison.OrdinalIgnoreCase)",
    ):
        if marker not in road_names:
            errors.append(f"ROAD-n metadata-first synchronization is missing: {marker}")

    # Midblock automatic direction must consider the row spread, not sum lot sizes.
    midblock = read(texts, source / "August11MidblockSewerProductionCommands.cs")
    if "double centreSpanX = parcels.Max(item => item.Center.X)" not in midblock:
        errors.append("Midblock automatic row orientation is not parcel-spread based")
    if "double totalWidth = parcels.Sum(item => item.Width);" in midblock:
        errors.append("Old Midblock sum-of-lot-width automatic orientation remains")

    # New bellmouths carry a CE junction GUID. Trimming must group by it first.
    bellmouth = read(texts, source / "August11BellmouthTrimCommands.cs")
    for marker in ("TryReadStoredJunctionGroup", "RoadLayoutRecordKey", "exact.TryGetValue(storedGroup"):
        if marker not in bellmouth:
            errors.append(f"Bellmouth exact stored-group trimming is missing: {marker}")

    # Async network source markers must use the exact document that launched the batch.
    network = read(texts, source / "August11NetworkBatchCommands.cs")
    for marker in (
        "NetworkSourceMarker.Mark(_document, _current, _discipline)",
        "internal static void Mark(Document document, ObjectId id, string discipline)",
        "internal static int Clear(Document document, IEnumerable<ObjectId> ids)",
        "using (DocumentLock documentLock = document.LockDocument())",
    ):
        if marker not in network:
            errors.append(f"Network batch exact-document source-marker wiring is missing: {marker}")

    # Guard already-proven platform point-based elevation repair.
    platform = read(texts, source / "PlatformProductionCommands.cs")
    if "else featureLine.SetPointElevation(index, elevation);" in platform:
        errors.append("Platform slope still contains unsafe AllPoints numeric-index elevation write")
    if "child.SetPointElevation(index, sourcePoint.Z + dz);" in platform:
        errors.append("Platform stepped-offset transfer still contains unsafe numeric-index elevation write")

    if errors:
        print("CE command/behavior wiring audit FAILED:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        print(
            f"\nDeclarations={len(declarations)}; referenced commands={len(references)}; files={len(files)}",
            file=sys.stderr,
        )
        return 1

    print(
        "CE command/behavior wiring audit passed: "
        f"{len(declarations)} unique CE CommandMethod declarations, "
        f"{len(references)} referenced CE commands, {len(files)} Civil3D source files; "
        "no duplicate/missing command targets and the audited style, table, road-name, "
        "midblock, bellmouth, network-marker and platform behaviors are wired."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
