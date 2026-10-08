"""Regression guard for Oct 8 profile-view/corridor/network-part field fixes."""
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
SRC=ROOT/"src"/"CE.Tools.Civil3D"
errors=[]

profile=(SRC/"ProfileViewPropertyMatchCommands.cs").read_text(encoding="utf-8-sig")
corridor=(SRC/"RoadCorridorCompletionCommands.cs").read_text(encoding="utf-8-sig")
lock=(SRC/"NetworkPartLockCommands.cs").read_text(encoding="utf-8-sig")
menu=(SRC/"September11FieldCompletionMenu.cs").read_text(encoding="utf-8-sig")

for marker in [
    "Do not let the source pick satisfy the next multi-target prompt",
    "document.Editor.SetImpliedSelection(new ObjectId[0]);",
]:
    if marker not in profile:
        errors.append("profile-view multi-target fix missing: "+marker)

for marker in [
    "FindDesignProfile(alignment, transaction);",
    "profile.ProfileType == ProfileType.FG",
    "typedBaseline.SetAlignmentAndProfile",
    '"BasicSidewalk cross slope",\n                "Keep current"',
]:
    if marker not in corridor:
        errors.append("corridor design-profile/assembly preservation fix missing: "+marker)

if "FindDesignProfile(alignment, transaction) ??" in corridor:
    errors.append("corridor still falls back from design profile to EG")

for marker in [
    'CommandMethod("CE_TOOLS", "CE_NETWORKPARTLOCKS"',
    '"Lock all", "Unlock all"',
    "TrySetLock",
]:
    if marker not in lock:
        errors.append("network part lock/unlock missing: "+marker)

if '"CE_NETWORKPARTLOCKS"' not in menu:
    errors.append("network part lock command missing from Field Completion menu")

if errors:
    print("Oct 8 field regression FAILED")
    for e in errors:
        print(" -",e)
    raise SystemExit(1)
print("Oct 8 field regression passed.")
