#!/usr/bin/env python3
"""Regression: do not lock text; batch draw existing profiles in views."""
from pathlib import Path
root=Path(__file__).resolve().parents[1]/"src"/"CE.Tools.Civil3D"
v=(root/"ViewportTextUprightCommands.cs").read_text(encoding="utf-8-sig")
p=(root/"ProfileViewAddProfilesCommands.cs").read_text(encoding="utf-8-sig")
assert v.startswith("#if CE_CIVIL3D_2024")
assert "internal static bool AutoEnabled = false;" in v
assert '"CE_VPTEXTUNLOCK"' in v
assert "ClearNativePaperOrientation(" in v
assert "TrySetNativePaperOrientation(entity, false)" in v
assert "if (IsAlreadyAnnotative(entity))\n                            continue;" in v
assert "if (TryEnableNativePaperOrientation(entity))" not in v
assert "selected == null" in v  # explicit all with no implicit mutation on open
assert "entity is DBText || entity is MText" in v
assert p.startswith("#if CE_CIVIL3D_2024")
assert '"CE_PROFILEADDTOVIEWS"' in p
assert '"All", StringComparison.OrdinalIgnoreCase' in p
assert "SelectImplied()" in p and "GetSelection(" in p
assert "CheckedListBox" in p and "SetItemChecked" in p
assert "profiles.Where(p =>" in p
assert "p.AlignmentId == info.AlignmentId" in p
assert "view.GraphOverrides as IEnumerable" in p
assert '"Draw", BindingFlags.Public' in p
assert "draw.SetValue(item, true, null)" in p
assert "view.IsReferenceObject" in p
assert "tr.Commit();" in p
assert "AddProfile" not in p.replace("CE_PROFILEADDTOVIEWS","").replace("AddProfilesToViews","")  # native Draw, no duplicates
print("PASS: 2024 orientation no automatic locks, manual Selected/All unlock; profile Draw selection per matching alignment.")
