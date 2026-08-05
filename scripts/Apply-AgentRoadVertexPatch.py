#!/usr/bin/env python3
"""Apply the CE road assembly, vertex setting-out and multi-offset integration patch."""

from __future__ import annotations

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"


def read(name: str) -> str:
    path = SRC / name
    if not path.is_file():
        raise SystemExit(f"Missing source file: {path}")
    return path.read_text(encoding="utf-8-sig")


def write(name: str, text: str) -> None:
    (SRC / name).write_text(text, encoding="utf-8")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if new in text:
        return text
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected one marker, found {count}")
    return text.replace(old, new, 1)


# 1. Road corridor assembly picker: use the robust resolver for Civil 3D 2023.
road = read("RoadProductionCommentCommands.cs")
road_old = """            IEnumerable ids = InvokeEnumerable(civilDocument, methodName);\n            if (ids == null) return result;"""
road_new = """            IEnumerable ids = string.Equals(\n                    methodName,\n                    \"GetAssemblyIds\",\n                    StringComparison.OrdinalIgnoreCase)\n                ? (IEnumerable)CivilAssemblyResolver.GetAssemblyIds(\n                    civilDocument,\n                    document.Database)\n                : InvokeEnumerable(civilDocument, methodName);\n            if (ids == null) return result;"""
road = replace_once(
    road,
    road_old,
    road_new,
    "RoadProductionCommentCommands assembly resolver",
)
write("RoadProductionCommentCommands.cs", road)


# 2. Assembly register and creation workflow: share the same resolver.
assembly = read("CeAssemblyCommands.cs")
if "CivilAssemblyResolver.GetAssemblyIds" not in assembly:
    pattern = re.compile(
        r"        internal static IList<ObjectId> ReadAssemblyIds\(CivilDocument civilDocument\)\n"
        r"        \{.*?\n"
        r"        \}\n\n"
        r"        private static int CountValues",
        re.DOTALL,
    )
    replacement = """        internal static IList<ObjectId> ReadAssemblyIds(CivilDocument civilDocument)\n        {\n            Document document = ActiveDocument();\n            return document == null\n                ? new List<ObjectId>()\n                : CivilAssemblyResolver.GetAssemblyIds(\n                    civilDocument,\n                    document.Database);\n        }\n\n        private static int CountValues"""
    assembly, count = pattern.subn(replacement, assembly, count=1)
    if count != 1:
        raise SystemExit("CeAssemblyCommands: ReadAssemblyIds method was not found")
write("CeAssemblyCommands.cs", assembly)


# 3. Linked stepped offsets: expose a multi-source selection rebuild command.
feature = read("FeatureLineRelativeCommands.cs")
menu_old = """                    new DisciplineWorkflowAction(\"Update all offsets from source\", \"CE_FLRELUPDATE\", \"Select the source or any child and immediately rebuild the complete linked set.\", \"02 Maintain\"),"""
menu_new = menu_old + """\n                    new DisciplineWorkflowAction(\"Update multiple source sets\", \"CE_FLRELUPDATEMULTI\", \"Select multiple source feature lines or linked children and rebuild only those complete stepped-offset sets.\", \"02 Maintain\"),"""
feature = replace_once(
    feature,
    menu_old,
    menu_new,
    "FeatureLineRelativeCommands menu",
)

command_marker = """        [CommandMethod(\"CE_TOOLS\", \"CE_FLRELINFO\", CommandFlags.Modal)]"""
command_block = """        [CommandMethod(\n            \"CE_TOOLS\",\n            \"CE_FLRELUPDATEMULTI\",\n            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]\n        public void UpdateMultipleCommand()\n        {\n            Document document = AcApplication.DocumentManager.MdiActiveDocument;\n            if (document != null) UpdateMultiple(document);\n        }\n\n"""
if "public void UpdateMultipleCommand()" not in feature:
    if command_marker not in feature:
        raise SystemExit("FeatureLineRelativeCommands: info command marker was not found")
    feature = feature.replace(command_marker, command_block + command_marker, 1)

method_marker = """        public static int RefreshAll(Document document)"""
method_block = """        private static void UpdateMultiple(Document document)\n        {\n            Editor editor = document.Editor;\n            PromptSelectionResult selection = editor.SelectImplied();\n            if (selection.Status != PromptStatus.OK ||\n                selection.Value == null ||\n                selection.Value.Count == 0)\n            {\n                selection = editor.GetSelection(new PromptSelectionOptions\n                {\n                    MessageForAdding = \"\\nSelect multiple source feature lines or linked children: \",\n                    AllowDuplicates = false,\n                    RejectObjectsFromNonCurrentSpace = true\n                });\n            }\n            if (selection.Status != PromptStatus.OK || selection.Value == null) return;\n\n            var groups = new Dictionary<ObjectId, List<ChildRecord>>();\n            int rejected = 0;\n            try\n            {\n                using (Transaction transaction =\n                    document.Database.TransactionManager.StartTransaction())\n                {\n                    BlockTableRecord modelSpace = GetModelSpace(\n                        document.Database,\n                        transaction,\n                        OpenMode.ForRead);\n                    foreach (SelectedObject selectedObject in selection.Value)\n                    {\n                        if (selectedObject == null || selectedObject.ObjectId.IsNull)\n                        {\n                            rejected++;\n                            continue;\n                        }\n\n                        CivilFeatureLine selected = OpenFeatureLine(\n                            transaction,\n                            selectedObject.ObjectId,\n                            OpenMode.ForRead);\n                        if (selected == null)\n                        {\n                            rejected++;\n                            continue;\n                        }\n\n                        Relation relation;\n                        ObjectId sourceId = TryReadRelation(\n                                selected,\n                                transaction,\n                                out relation)\n                            ? ResolveHandle(document.Database, relation.SourceHandle)\n                            : selected.ObjectId;\n                        if (sourceId.IsNull || sourceId.IsErased || groups.ContainsKey(sourceId))\n                            continue;\n\n                        CivilFeatureLine source = OpenFeatureLine(\n                            transaction,\n                            sourceId,\n                            OpenMode.ForRead);\n                        EnsureEditable(source, transaction);\n                        List<ChildRecord> children = FindChildren(\n                            modelSpace,\n                            source.Handle.ToString(),\n                            transaction);\n                        if (children.Count == 0)\n                        {\n                            rejected++;\n                            continue;\n                        }\n                        groups.Add(sourceId, children);\n                    }\n                }\n            }\n            catch (System.Exception exception)\n            {\n                editor.WriteMessage(\n                    \"\\nCE_FLRELUPDATEMULTI cancelled during source discovery. \" +\n                    exception.Message);\n                return;\n            }\n\n            if (groups.Count == 0)\n            {\n                editor.WriteMessage(\n                    \"\\nCE_FLRELUPDATEMULTI: no selected linked stepped-offset source sets were found.\");\n                return;\n            }\n\n            int rebuilt = 0;\n            int failed = 0;\n            foreach (KeyValuePair<ObjectId, List<ChildRecord>> group in groups)\n            {\n                try\n                {\n                    rebuilt += RebuildChildren(\n                        document,\n                        group.Key,\n                        group.Value);\n                }\n                catch (System.Exception exception)\n                {\n                    failed++;\n                    editor.WriteMessage(\n                        \"\\nA selected linked stepped-offset set was skipped. \" +\n                        exception.Message);\n                }\n            }\n\n            document.Editor.Regen();\n            editor.WriteMessage(\n                \"\\nCE_FLRELUPDATEMULTI complete. Source sets={0}; linked feature lines rebuilt={1}; rejected={2}; failed sets={3}.\",\n                groups.Count,\n                rebuilt,\n                rejected,\n                failed);\n        }\n\n"""
if "private static void UpdateMultiple(Document document)" not in feature:
    if method_marker not in feature:
        raise SystemExit("FeatureLineRelativeCommands: RefreshAll marker was not found")
    feature = feature.replace(method_marker, method_block + method_marker, 1)
write("FeatureLineRelativeCommands.cs", feature)


# 4. Automatic linked refresh: include vertex setting-out groups.
presentation = read("CommentPresentationCommands.cs")
presentation_old = """                summary.FeatureLines +=\n                    FeatureLineRelativeCommands.RefreshAll(document);"""
presentation_new = presentation_old + """\n                VertexSettingOutCommands.RefreshAll(document);"""
presentation = replace_once(
    presentation,
    presentation_old,
    presentation_new,
    "CommentPresentationCommands vertex refresh",
)
write("CommentPresentationCommands.cs", presentation)


# 5. Ribbon access for the new setting-out and multi-source offset commands.
ribbon = read("PluginEntry.cs")
coordinate_old = """                        Cmd(\"Polyline Vertex Linked Points\", \"CE_COORDPOLY2 \", \"Create sequential COGO points in polyline direction and a linked Point Name, Y, X, Z table.\"))"""
coordinate_new = """                        Cmd(\"Polyline Vertex Linked Points\", \"CE_COORDPOLY2 \", \"Create sequential COGO points in polyline direction and a linked Point Name, Y, X, Z table.\"),\n                        Cmd(\"Multi-Source Vertex Setting-Out\", \"CE_VERTEXSETTINGOUT \", \"Create dynamic COGO, MText or MLeader points from multiple polylines and feature lines, including arc centres and long-segment points.\"),\n                        Cmd(\"Refresh Vertex Setting-Out\", \"CE_VERTEXSETTINGOUTREFRESH \", \"Recalculate linked vertex points, names, coordinates, radius dimensions and table rows.\"),\n                        Cmd(\"Export Vertex Setting-Out\", \"CE_VERTEXSETTINGOUTEXPORT \", \"Refresh and export a linked vertex setting-out table to Excel.\"))"""
ribbon = replace_once(
    ribbon,
    coordinate_old,
    coordinate_new,
    "PluginEntry coordinate menu",
)

offset_old = """                        Cmd(\"Update Linked Offset Set\", \"CE_FLRELUPDATE \", \"Select the source or any child and immediately refresh the complete linked offset set.\"),"""
offset_new = offset_old + """\n                        Cmd(\"Update Multiple Offset Sets\", \"CE_FLRELUPDATEMULTI \", \"Select multiple source feature lines or linked children and rebuild only those stepped-offset sets.\"),"""
ribbon = replace_once(
    ribbon,
    offset_old,
    offset_new,
    "PluginEntry feature-line menu",
)
write("PluginEntry.cs", ribbon)


# 6. Source-level validation suitable for environments without Autodesk binaries.
checks = {
    "CivilAssemblyResolver.cs": (
        "GetAssemblyIds(",
        '"AssemblyCollection"',
        "AddFromDatabase(",
    ),
    "RoadProductionCommentCommands.cs": (
        "CivilAssemblyResolver.GetAssemblyIds",
        '"CE_ROADCORRIDORS"',
    ),
    "CeAssemblyCommands.cs": (
        "CivilAssemblyResolver.GetAssemblyIds",
        '"CE_ASSEMBLYREPORT"',
    ),
    "VertexSettingOutGeometry.cs": (
        '"ARC MIDPOINT"',
        '"ARC CENTER"',
        '"TANGENT 1/4"',
        '"TANGENT 3/4"',
        "new VertexRadialDimension",
    ),
    "VertexSettingOutCommands.cs": (
        '"CE_VERTEXSETTINGOUT"',
        '"CE_VERTEXSETTINGOUTREFRESH"',
        '"CE_VERTEXSETTINGOUTEXPORT"',
        "internal static int RefreshAll(Document document)",
        "SimpleXlsxWriter.Write",
        "new RadialDimension",
    ),
    "FeatureLineRelativeCommands.cs": (
        '"CE_FLRELUPDATEMULTI"',
        "private static void UpdateMultiple(Document document)",
    ),
    "CommentPresentationCommands.cs": (
        "VertexSettingOutCommands.RefreshAll(document)",
    ),
    "PluginEntry.cs": (
        '"CE_VERTEXSETTINGOUT "',
        '"CE_FLRELUPDATEMULTI "',
    ),
}

errors: list[str] = []
for name, markers in checks.items():
    text = read(name)
    for marker in markers:
        if marker not in text:
            errors.append(f"{name} is missing marker: {marker}")
    if text.count("{") != text.count("}"):
        errors.append(f"{name} has unbalanced braces")

if errors:
    print("CE Tools road/vertex integration validation failed:", file=sys.stderr)
    for error in errors:
        print(f"- {error}", file=sys.stderr)
    raise SystemExit(1)

print(
    "CE Tools road/vertex integration passed: Civil 3D 2023 assembly fallbacks, "
    "multi-source vertex setting-out, dynamic linked tables/exports, radius dimensions, "
    "automatic refresh and multi-source stepped-offset updates are wired."
)
