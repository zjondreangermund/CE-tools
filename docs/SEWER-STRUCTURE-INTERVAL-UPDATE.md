# CE_SEWERSTRUCTUREINTERVALUPDATE

Open FINAL SEWER PRODUCTION → Existing Network Maintenance → CE-Update Existing Sewer Structure Intervals, or enter `CE_SEWERSTRUCTUREINTERVALUPDATE`.

Choose the existing gravity sewer network and minimum interval in the popup, review the branch span preview, then apply. Distances are drawing units (60 means 60 m in a metre-based drawing).

The command measures physical distance along the existing branch alignment, independent of displayed station equations. Each fixed-point span is divided into `max(1, floor(length / minimum))` equal intervals. A 242.88 m span and 60 m minimum gives 0, 60.72, 121.44, 182.16 and 242.88 m. This is a **minimum**, not a maximum: a 79.93 m span remains one interval. A shorter span between protected points remains short and is reported in the preview.

Branch endpoints, alignment vertices, junctions and design transitions are protected. Redundant intermediate structures can be removed and replaced when adjacent straight pipes have matching grade, size, presentation, hydraulic properties and view membership. Rim/sump elevation changes also protect structures. New structure rims and sumps interpolate the existing design; they are not resampled from a potentially changed surface.

The original branch alignments, IDs, geometry, start station and station equations remain in place. New parts reference those alignments and their stations update from position. Alignment regeneration would risk changing dependent profiles, so this command does not recreate alignment entities or profile views. New parts are drawn in the original profile/section views and receive source plan/profile label styles. Retained native labels keep their IDs and recalculate from renamed parts. Deleted intermediate parts necessarily lose their labels; replacement labels use the same source styles.

Numbering follows physical alignment distance, using the branch number from each alignment's name, e.g. Branch-8 → P8.1, P8.2 and MH8.1, MH8.2. Each junction is named once. Existing branch names/numbers remain; alignments must have unique numbered names. Unconnected orphan structures are not renamed.

All network edits, renumbering, label creation and verification share one transaction. Failures abort it. Verification checks branch coverage, pipe direction, elevations, part size and captured properties, plus retained structure rim/sump levels and positions. CE background sequence/refresh queues do not enqueue a competing redraw from these edits. Rules are not explicitly applied.

## Supported scope and checks before use

This is source code, not a compiled installer. Rebuild/install CE Tools for the target Civil 3D version. Test on a copy of a drawing before production use.

Curved/offset pipes, disconnected pipes, referenced networks, overlapping runs on the same alignment, individual rule overrides, spanning pipe labels or custom extension-dictionary data on pipes requiring edits stop the update for review. Custom-data structures and design transitions remain fixed. The command does not migrate arbitrary third-party metadata or manually overridden label text to replacement objects. If a native API changes an elevation/property during connection or splitting, preservation checks stop the update rather than accept a redesigned network.

## Validation

- 125 core checks pass, including the supplied 242.88/60 example, nonzero chainages, exact multiples, short protected spans and invalid/excessive intervals.
- 85 repository Python source checks pass.
- Civil 3D source syntax/shared aliases/modal checks pass.
- The command and planner compile against Autodesk Civil3D.NET 13.6.1781 and AutoCAD.NET 24.3.0 (.NET Framework 4.8) with zero warnings/errors. The isolated compile project substitutes only the CE popup/queue wrappers; Autodesk API types come from Autodesk reference packages.
- Full Windows add-in compilation, Civil 3D 2023 runtime and live DWG mutation/profile-display testing have not been performed here.

Reproduce numerical tests with `dotnet run --project tests/CE.Tools.Core.Tests` and the API compile check with `dotnet build tests/CE.Tools.SewerIntervals.ApiCompile.Tests`.

Live acceptance: use a numbered straight branch with several removable intermediate structures and a 242.88 m protected span; request 60; check five structures at the above distances. Repeat and expect no changes. Check reversed pipe direction, a fixed vertex, a junction, a grade break, size/style changes, multiple profile views and station equations. Compare before/after inverts, rims, sumps, pipe sizes, rules and profile styles/bands. Verify labels and UNDO/REDO; confirm unsupported cases leave the DWG unchanged.
