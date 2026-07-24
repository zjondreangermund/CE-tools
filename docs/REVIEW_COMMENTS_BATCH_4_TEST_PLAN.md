# CE Tools Review Comments — Batch 4 Validation Plan

## Scope

This plan validates the command-defect and workflow-improvement batch from the review received on 23 July 2026.

The batch introduces hardened direct workflows for:

- Corridor rebuild
- Feature-line raise/lower
- Feature-line elevations from a selected surface
- Constant grade between feature-line endpoints
- Parking validation/counting
- Parking validation/numbering

The original command names remain available at the command line while the CE Tools ribbon routes users to the hardened commands during validation.

## Build matrix

Compile the complete solution for:

- Civil 3D 2023 / AutoCAD 2023 managed assemblies
- Civil 3D 2024 / AutoCAD 2024 managed assemblies
- .NET Framework 4.8, x64

The following repository checks must pass before in-product testing:

- Report-presentation source validation
- Shared-annotation source validation
- Batch 4 workflow source validation
- Core geometry tests

## General mutation checks

For every modifying command:

1. Save a test drawing under a new name.
2. Run the command with a valid selection and cancel at the review step.
3. Confirm no objects or elevations changed.
4. Run again and accept the review.
5. Confirm only the selected editable objects changed.
6. Undo once and confirm the complete command is reversed.
7. Test objects on locked layers.
8. Test referenced Civil 3D objects where available.
9. Mix supported and unsupported objects and confirm the command explains rejected objects.
10. Save, close and reopen the drawing and confirm accepted changes persist.

## Corridor rebuild — `CE_CORREBUILDX`

### Functional checks

1. Create or open a drawing with at least two editable corridors.
2. Make one corridor out of date and leave one current.
3. Select both corridors.
4. Confirm the review pop-up states that `Corridor.Rebuild()` will be called.
5. Confirm the preview distinguishes out-of-date and already-current corridors.
6. Select **Rebuild**.
7. Confirm both editable corridors rebuild and the command does not display the corridor report instead.
8. Confirm the editor regenerates after completion.
9. Confirm the completion message reports the number for which `Corridor.Rebuild()` was called.

### Rejection checks

- Non-corridor object → `Not a Civil 3D corridor`.
- Data-shortcut/reference corridor → read-only rejection.
- Corridor on locked layer → locked-layer rejection.
- Empty or cancelled selection → no changes.

### Legacy verification

Run `CE_CORREBUILD` at the command line and confirm the existing command still dispatches to the original rebuild method. This is retained for compatibility while `CE_CORREBUILDX` is the ribbon workflow.

## Feature-line raise/lower — `CE_FLRAISEX`

1. Select several ordinary feature lines with PI and elevation points.
2. Enter a positive difference.
3. Confirm the review shows action, difference, feature-line count, point count and before/after elevation ranges.
4. Accept and verify every editable point elevation increases by the entered difference.
5. Repeat with a negative difference and confirm elevations decrease.
6. Test feature-line points that are relative to a surface and confirm their relative offsets change by the entered difference.
7. Confirm the command edits elevations and does not open the feature-line report.
8. Undo once and verify all changed feature lines return to their original elevations.

### Rejection checks

- Corridor feature line or another derived feature-line type.
- Data-shortcut/reference feature line.
- Feature line on a locked layer.
- Feature line containing no editable points.
- Ordinary AutoCAD line or polyline.

### Legacy verification

Run `CE_FLRAISE` at the command line and confirm the original compatibility command still dispatches to its elevation-edit method.

## Surface-selection workflow — `CE_FLSURFACEUI`

1. Create at least three surfaces with clearly different names and elevation ranges.
2. Select one or more ordinary feature lines.
3. Confirm a modal surface-selection grid opens.
4. Confirm it displays surface name, runtime type, style, minimum elevation, maximum elevation and current/out-of-date state.
5. Resize the window and verify all buttons remain visible.
6. Select a surface by clicking **Select Surface**.
7. Repeat using a double-click on the surface row.
8. Choose both intermediate-grade-break options in separate tests.
9. Review and accept the assignment.
10. Confirm the selected feature lines receive elevations from the chosen surface—not from a different surface.
11. Cancel the surface window and verify no feature-line changes occur.
12. Test a surface that does not cover the complete feature line and confirm the transaction fails safely without partial committed changes.

## Constant grade — `CE_FLCONSTGRADE`

1. Select multiple open ordinary feature lines with different endpoint elevations and intermediate PI/elevation points.
2. Record every endpoint and intermediate elevation.
3. Run the command and review the feature-line and point totals.
4. Accept the operation.
5. Confirm each feature line retains its own first and last endpoint elevations.
6. Confirm every intermediate existing point lies on a linear elevation interpolation between those endpoints using cumulative plan distance through the existing feature-line points.
7. Confirm multiple selected feature lines are processed in one transaction.
8. Undo once and confirm all original intermediate elevations return.

### Rejection checks

- Closed feature line or coincident endpoints.
- Feature line with insufficient plan length.
- Referenced feature line.
- Locked-layer feature line.
- Derived/corridor feature line.

### Geometry limitation to verify

The first implementation uses cumulative plan distance through the existing feature-line points. For curved feature-line segments, compare results with Civil 3D's native grade tools. Record any meaningful difference so a later release can replace chord-based distance with exact curve-chainage evaluation if required.

## Parking validation and count — `CE_PKCOUNTX`

Prepare a selection containing:

- Valid static block reference
- Valid dynamic block reference
- Valid closed parking polyline
- Open polyline
- Two-vertex closed polyline where Civil 3D permits it
- Zero-area closed polyline
- Ordinary line
- Text object
- Object on a locked layer
- Xref block reference

Confirm the pop-up separates **Accepted** groups from **Rejected** reasons and reports a count for each reason.

Confirm accepted block references group by effective block definition and accepted closed polylines group by layer.

Select **Place Table** and confirm the drawing table contains the same accepted groups and rejected reasons as the pop-up.

## Parking validation and numbering — `CE_PKNUMBER2`

1. Set annotation height to 1.8 and enable marker circles.
2. Select a mix of valid and rejected parking objects.
3. Enter prefix, starting number and increment.
4. Confirm the review lists accepted count, rejected count, every rejection reason, text height, marker setting and selection-set order.
5. Accept and verify only validated objects receive numbers.
6. Confirm numbers are placed at object extents centres.
7. Confirm labels remain on the source object's layer.
8. Confirm marker circles use the same source layer.
9. Repeat at heights 2.0 and 5.0.
10. Repeat with marker circles disabled.
11. Test positive and negative increments; reject zero increment.
12. Undo once and confirm all parking text and marker circles from that run are removed.

## Ribbon checks

Confirm these ribbon entries launch the hardened workflows:

- Feature Line Tools → Raise / Lower → `CE_FLRAISEX`
- Feature Line Tools → Constant Grade Between Endpoints → `CE_FLCONSTGRADE`
- Feature Line Tools → Elevations from Surface → `CE_FLSURFACEUI`
- Corridor Tools → Rebuild Corridors → `CE_CORREBUILDX`
- Parking Tools → Validate and Count Bays → `CE_PKCOUNTX`
- Parking Tools → Validate and Number Bays → `CE_PKNUMBER2`

Confirm the following working commands remain visible and operational:

- Bellmouth Densifier
- Total Length
- Total Area
- Polyline Vertex COGO Points
- Feature-line weed tools
- Feature-line linked stepped offsets

## Release decision

Keep the pull request in draft until:

- Both Civil 3D versions compile successfully.
- Every mutation command passes one-step Undo testing.
- Corridor rebuild is visually confirmed to rebuild rather than report.
- Feature-line raise/lower is numerically confirmed to change elevations.
- Surface selection returns the surface chosen in the pop-up.
- Constant-grade results are accepted for straight and curved test geometry.
- Parking rejection explanations match the selected invalid objects.
