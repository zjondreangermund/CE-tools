#!/usr/bin/env python3
"""Source-level checks for the Oct 27 uploaded grading/infill integration.

These tests do not execute Autodesk Civil 3D and are not proof of native
grading group/infill creation or of correct TIN topology on a live drawing.
"""
from pathlib import Path

root = Path(__file__).resolve().parents[1]
s = (root / "src/CE.Tools.Civil3D/August23PlatformDynamicGradingCommands.cs").read_text(encoding="utf-8")

checks = {
    "original shared command registrations retained":
        s.count('"CE_JUNCTIONGRADETOSURFACE"') == 1 and
        s.count('"CE_PLATFORMGRADETOSURFACE"') == 1,
    "cut/fill daylight calculations retained":
        "TryBuildDaylight(" in s and "TryFindDaylight(" in s and "BuildOrRefreshGrade(" in s,
    "noded intersections handle T and X shapes":
        "TryPlanSegmentIntersection(" in s and "rawSegments[i].Cuts.Add(ti)" in s
        and "rawSegments[j].Cuts.Add(tj)" in s,
    "closed feature-line final segment preserved":
        "int segmentCount = source.Closed ? points.Count : points.Count - 1;" in s
        and "points[(segment + 1) % points.Count]" in s,
    "boundary vertices taken from source, not daylight samples":
        "var points = source.Points == null ? new List<Point3d>() : source.Points.ToList();" in s,
    "generated daylight rays excluded from boundary graph":
        "IsGeneratedDaylightSlopeLine(document.Database, source)" in s,
    "separate bounded TIN face creation":
        "TryCreateCombinedJunctionInfillSurface(" in s and
        "SurfaceBoundaryType.Outer" in s,
    "kerb and gutter strip pairing":
        "CreateKerbGutterStripInfills(" in s
        and "AddVirtualKerbGutterEndCaps(" in s,
    "repeat commands do not create numbered duplicate TINs":
        "FindSurfaceByName(document.Database, separateName)" in s
        and "FindSurfaceByName(document.Database, name)" in s
        and "UniqueSurfaceName(database, requestedName)" not in s,
    "existing infill weeding preserved":
        "WeedInfillBoundary(uniquePoints," in s,
    "user-specified infill spacing preserved":
        '_infillMaxBoundarySegmentLength' in s and
        '"InfillMaxBoundarySegment"' in s,
    "both explicit grading and infill result reports":
        "CE Tools - Grading / Infill Results" in s
        and "PlatformDynamicRefreshManager.Queue()" in s,
}
for name, ok in checks.items():
    if not ok:
        raise AssertionError("Noded junction infill integration failed: " + name)
    print("PASS:", name)
