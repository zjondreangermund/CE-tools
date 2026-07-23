# Polyline Tools Civil 3D 2023 test plan

## Ribbon

- Confirm the CE Tools tab contains category panels instead of the previous individual panels.
- Confirm Project, Survey, Drawings, Geometry, Site Design, Utilities and Analysis display drop-down arrows.
- Confirm each drop-down shows only its related commands.
- Confirm the old individual CE Tools panels are absent after restarting Civil 3D.
- Confirm every listed command still works from the command line.

## CE_PLDIR

- Test one open lightweight polyline with one midpoint arrow.
- Reverse the polyline direction and confirm the arrow reverses.
- Test multiple arrows using an entered spacing.
- Test a polyline containing line and arc segments.
- Test old 2D and 3D polylines.
- Run Add twice and confirm existing linked arrows are replaced instead of duplicated.
- Clear arrows for selected polylines only.
- Clear all CE direction arrows in the current space.
- Confirm unrelated solids are not erased.
- Confirm a locked-layer polyline is skipped.
- Confirm cancellation and default No make no changes.
- Confirm one UNDO reverses the completed Add or Clear operation.

## CE_COORDPOLY

- Test an open lightweight polyline and confirm COGO points follow vertex order.
- Reverse the polyline and confirm the created sequence reverses.
- Test a closed polyline and confirm the start vertex is not duplicated.
- Test old 2D and 3D polylines.
- Confirm coincident consecutive vertices do not create duplicate points.
- Confirm the drawing next-point-number setting supplies sequential Civil 3D point numbers.
- Confirm raw descriptions use the entered prefix and sequence.
- Confirm the XYZ table point numbers, descriptions and coordinates match the created COGO points.
- Confirm table coordinates are WCS values when a rotated UCS is active.
- Cancel before confirmation and confirm no points or table are created.
- Force a table failure where practical and confirm created points are removed where possible.
- Confirm one UNDO reverses the point-and-table operation.
