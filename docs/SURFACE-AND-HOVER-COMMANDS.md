# Surface and live hover commands

The four supplied command owners are installed under canonical filenames in `src/CE.Tools.Civil3D` and included in the downloadable source update kit. Build and install CE Tools for your Civil 3D release after applying the kit. The Help Centre's command catalogue discovers their command registrations.

| Command | Purpose |
| --- | --- |
| `CE_SURFACECROPANDREDUCE` | Sample a selected TIN inside a closed polyline into a separate reduced surface. Sample spacing controls approximation; the source surface is retained. |
| `CE_AUTOCADOBJECTHOVER` | Display native AutoCAD object properties under the cursor. |
| `CE_ROADMODELHOVER` | Inspect road alignment and selected surface information. |
| `CE_NETWORKMODELHOVER` | Inspect gravity/pressure network information and selected EG surface. |

Hover commands use a modeless report window while point tracking is active. Press Enter or Esc in the drawing to finish. Surface selectors remain interactive. These windows now open without activation, are not globally always-on-top, and coalesce pending label updates instead of adding a UI callback for every report. Modeless opening is inside the cleanup scope so an opening failure cannot leave a PointMonitor subscription attached. Road-report escaped newlines were corrected. `HoverInspectorForm.cs` is a required shared support file included in the kit.

No supplied hover command calls Cursor.Hide, changes CURSORSIZE or changes global graphics settings. These source changes address possible focus/redraw interference; they do not prove or guarantee a fix for a stationary cursor disappearing on a particular machine. Exit hover commands first. If the issue persists outside them, investigate the graphics/driver path using GRAPHICSCONFIG and compare hardware acceleration settings, restoring the setting if it does not help.

The surface tool creates a sampled approximation; it is not a lossless surface simplifier. Review the result before design use. Use a closed straight-segment lightweight polyline boundary; the supplied point-in-polygon sampler uses boundary vertices, not curved-edge tessellation.

Validation: command-owner and production-menu source checks; repository source/syntax checks; compilation of the new commands against Civil 3D 2024/AutoCAD 2024 reference APIs. Native Civil 3D UI, cursor and DWG tests require Windows and remain pending.
