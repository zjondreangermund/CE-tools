# CE Tools — Latest Main Sync

**Sync date:** 18 August 2026  
**Sync ID:** `2026-08-18-survey-dynamic-stability-3`

This marker exists so downloaded/extracted repository copies can be checked against the current field-test source set.

Current Project / Survey expectations:

- Project Production is a one-page centre and does **not** contain Survey Location or Namibia LO/WGS84.
- Survey Production owns Survey Location / Coordinate System and Namibia LO/WGS84 conversion.
- Windhoek resolves to Namibia **LO17** automatically from the saved Project/Survey town.
- Discipline Style Presets appears before Project Style Centre.
- Project Style Centre activates the saved discipline preset on first open when the current selection is still drawing defaults.
- Drawing Book and Client Book use the Drawing Register **Title Block Source**, with the CE fallback only when the registered source cannot be inserted.
- Survey Production **PREPARE** opens **CE-Background Tools**. The older Background/XREF manager is available from inside that window rather than as the Survey Production front door.
- Grid Setting-Out is a dedicated **multiple-polyline** linked workflow with **Perimeter** and **Full grid** modes; it does not route to Vertex Setting-Out.
- Grid and Vertex COGO outputs retain dynamic source links. Moving/stretching source geometry queues one settled automatic dependency refresh after the editing command completes.
- CE logical point names use linked data / Raw Description during dynamic refresh so Civil 3D is not repeatedly forced through duplicate Point Name dialogs.
- Survey Linked/Annotative Refresh uses one coordinated undo-suppressed refresh pass and does not restore or auto-solve COGO label offsets.
- Normal automatic refresh does not re-run point-style, annotation-overlap or table-centering presentation solvers.
- Automatic refresh is filtered to engineering dependency/source objects rather than CE-generated Table/MText/MLeader/Xrecord output changes, preventing feedback loops and repeated flicker.
- Annotation-scale maintenance is automatic and excluded from AutoCAD Undo bookkeeping; linked grid/vertex/coordinate tables recalculate from current paper height and current drawing annotation scale.
- Site Grid retains the August 18 refresh-loop/Undo suppression fix.

For Civil 3D 2023, use only:

`BUILD-INSTALL-CIVIL3D-2023.cmd`

The build stages the repository to a short local path, applies the final compatibility/field-test repairs, compiles, packages and installs CE Tools.
