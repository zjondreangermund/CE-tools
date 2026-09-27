# Feature-line colours and profile bands — 27 September 2026

## Faults found

1. `StyleBase.Name` overrides only the setter. The previous reflection helper
   read that property, caught the exception, and returned an empty name.
   `CE_FLAPPEARANCE` then returned before assigning its colour style. This matches
   the screenshot: Properties says Yellow, the style remains Basic, and the
   command reports zero visible colour styles. Band style names were read through
   the same invalid getter, preventing correct ground/left/centre/right matching.
2. `GetTopBandItems` and `GetBottomBandItems` return collections that must be set
   back on the owning view. Both label commands discarded their edits and counted
   values from temporary wrappers as verified. This explains why enabling labels
   reported success while the native band-label count stayed at zero.

## Repair

- Read style names through the inherited Civil DBObject getter. Use the typed
  feature-line style collection, clone the current style, set its plan/model
  component colours, assign it, then reopen and verify the saved style and colour.
- Save both band locations through `SetTopBandItems` / `SetBottomBandItems` with
  the profile view open for write. Verify newly retrieved collections and report
  errors rather than swallowing failed collection writes.
- Bind original road profiles first. Attempt the existing hidden-copy fallback
  only after a real binding failure, rather than for every displayed profile.
- Identify network bands by native band type. A profile-data row called
  “Pipe invert” must still receive profile sources, not a network ID.

## Existing drawings

Load the DLL rebuilt from this commit after restarting Civil 3D. Run:

1. `CE_FLAPPEARANCE`, select feature lines, and choose the colour.
2. `CE_PROFILEBANDLABELSMULTI`, select the existing profile views.
   Use `CE_ROADBANDLABELS` when importing/replacing their band set.

The source archive is not a compiled plug-in; an older DLL cannot apply these
changes. A style name such as `CE-FL-ACI-2-Basic` should replace Basic on the
selected yellow lines. Check the unselected drawing display and the command's
verified style count. Band values should populate within the existing rows.

## Evidence and verification limits

- Autodesk [StyleBase.Name](https://help.autodesk.com/cloudhelp/2022/ENU/Civil3D-API/files/html/95ddfb29-1f21-cb76-e3d5-a3a94d376332.htm)
  documents the setter-only override.
- Autodesk [FeatureLineStyles](https://help.autodesk.com/cloudhelp/2022/ENU/Civil3D-API/files/html/89a36760-71c7-1524-6290-93627d2154dc.htm)
  exposes the typed collection.
- Autodesk [SetBottomBandItems](https://help.autodesk.com/cloudhelp/2022/ENU/Civil3D-API/files/html/7706a0f4-06f9-86e4-ccb1-f7a585772355.htm)
  and the [accepted API example](https://forums.autodesk.com/t5/civil-3d-customization-forum/update-subproperties-of-profileviewbanditem/td-p/6915065)
  show the required collection write-back.

`CE.Tools.Civil3D.ApiContract.Tests` executes the actual repair helpers against
managed test doubles with setter-only names and detached band collections.
It checks persisted readback, both band locations, write failures, read-only
owners, batch independence, and preservation of shared feature-line styles.
It does not emulate Autodesk's native graphics engine. Final rendering and a full
Civil 3D build still require the Autodesk installation and the affected DWG.
