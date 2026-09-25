from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
road = (ROOT / "src/CE.Tools.Civil3D/September18RoadJunctionCompletionCommands.cs").read_text(encoding="utf-8")
sewer = (ROOT / "src/CE.Tools.Civil3D/SewerProductionCommands.cs").read_text(encoding="utf-8")
bands = (ROOT / "src/CE.Tools.Civil3D/ProfileViewBandDataBinder.cs").read_text(encoding="utf-8")
offsets = (ROOT / "src/CE.Tools.Civil3D/FeatureLineRelativeCommands.cs").read_text(encoding="utf-8")

checks = {
    "road selection and refresh": [
        'model.AddChoice("Roads", "01 Surfaces", "Roads", "ALL"',
        "ReadRoadChoiceNames(document.Database, civilDocument)",
        'model.AddChoice("RefreshCorridors"',
        'TryInvoke(corridor, "Rebuild")',
        "RefreshSurfaceProfile(existingProfile)",
        "RefreshSurfaceProfile(SafeOpen<DBObject>(transaction, newProfileId, OpenMode.ForWrite))",
    ],
    "junction feature-line elevation and intermediate points": [
        '[CommandMethod("CE_TOOLS", "CE_ROADJUNCTIONFEATURELINESTOP", CommandFlags.Modal | CommandFlags.UsePickSet',
        'model.AddChoice("PointMode"',
        "AddLineVerticesToSurface(",
        "TryAssignFeatureLineElevations(line, topSurfaces, out unresolvedPoints)",
        "TryFindSurfaceElevation(surfaces, point.X, point.Y, out elevation)",
        "new Vector2d(-distance, 0.0)",
        "tin.AddVertices(new Point3dCollection(new[] { point }));",
    ],
    "junction endpoint recovery": [
        'model.AddChoice("Roads", "01 Surfaces", "Roads", "ALL"',
        "CivilFeatureLine featureLine = entity as CivilFeatureLine;",
        "featurePoints[featurePoints.Count - 1]",
        "if (!IsFinite(point.Z) || Math.Abs(point.Z) <= 0.001)",
    ],
    "multiple stepped feature lines": [
        'CommandMethod("CE_TOOLS", "CE_FLRELCREATE", CommandFlags.Modal | CommandFlags.UsePickSet',
        'MessageForAdding = "\\nSelect one or more SOURCE feature lines for stepped offsets: "',
        "sourceIds.Count",
        "for (int sourceIndex = 0; sourceIndex < sourceIds.Count; sourceIndex++)",
        "localCreated++",
    ],
}

errors = []
for section, markers in checks.items():
    source = offsets if section == "multiple stepped feature lines" else road
    for marker in markers:
        if marker not in source:
            errors.append(f"{section}: missing marker {marker!r}")

for marker in [
    "connectedStructureIds.Add(pipe.StartStructureId)",
    "connectedStructureIds.Add(pipe.EndStructureId)",
    "connectedStructureIds.Contains(structureId)",
]:
    if marker not in sewer:
        errors.append(f"sewer branch parts: missing marker {marker!r}")

if "identity.Contains(\"STRUCTURE\")" not in bands or "identity.Contains(\"MANHOLE\")" not in bands:
    errors.append("sewer structure/manhole data bands are not linked to the network")

profile_method = road.split("public void AddRoadTopBottomSurfacesToProfileViews()", 1)[1].split(
    "private static void EnsureProfileVisibleInView", 1
)[0]
rebuild_at = profile_method.find('TryInvoke(corridor, "Rebuild")')
surface_at = profile_method.find("List<CivilSurface> surfaces")
if rebuild_at < 0 or surface_at < 0 or rebuild_at >= surface_at:
    errors.append("road corridors must rebuild before crossing-surface profiles are refreshed")

if errors:
    print("September 25 road/sewer profile validation FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("September 25 road/sewer profile validation passed.")
