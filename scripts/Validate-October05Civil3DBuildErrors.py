"""Regression guard for the October 5 real Civil 3D 2023 build errors."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"
errors = []

profile = (SRC / "ProfileViewBatchCommands.cs").read_text(encoding="utf-8-sig")
edge = (SRC / "RoadEdgeLevelProfileCommands.cs").read_text(encoding="utf-8-sig")

for marker in [
    "using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;",
    "using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;",
    "using ProfilePVI = Autodesk.Civil.DatabaseServices.ProfilePVI;",
]:
    if marker not in profile:
        errors.append("ProfileViewBatchCommands missing Civil 3D 2023 alias: " + marker)

for marker in [
    "CivilAlignment alignment",
    "CivilProfile profile",
    "foreach (ProfilePVI pvi in profile.PVIs)",
]:
    if marker not in profile:
        errors.append("Profile batch vertical-fit code missing expected Civil type usage: " + marker)

if "alignment.GetProfileIds().ToList()" in edge:
    errors.append("RoadEdgeLevelProfileCommands still calls unsupported ObjectIdCollection.ToList()")

for marker in [
    "var existingProfileIds =",
    "new List<ObjectId>()",
    "foreach (ObjectId existingId in",
    "alignment.GetProfileIds())",
]:
    if marker not in edge:
        errors.append("Civil 3D-safe ObjectIdCollection copy missing: " + marker)

if errors:
    print("October 5 Civil 3D real build error regression FAILED")
    for error in errors:
        print(" -", error)
    raise SystemExit(1)

print("October 5 Civil 3D real build error regression passed.")
