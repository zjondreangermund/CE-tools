# CE_FLREL Civil 3D 2023 validation plan

## Release gate

Do not mark the linked stepped-offset workflow production-ready until every modifying test passes with one undo operation and no partial result after cancellation or an exception.

## Create linked offsets

1. Create an open ordinary feature line containing straight and curved plan segments, PI elevations and intermediate elevation points.
2. Run `CE_FLREL` and accept the default `Create` option.
3. Pick the left side, enter a horizontal step of `1.500`, a vertical step of `-0.150`, and create three offsets.
4. Confirm the preview shows cumulative offsets of `1.500`, `3.000`, `4.500` and vertical differences of `-0.150`, `-0.300`, `-0.450`.
5. Confirm the created names follow the entered prefix and remain unique.
6. Confirm every child uses the source layer, feature-line style and site.
7. Confirm straight and curved plan geometry follows the selected side.
8. Confirm child elevations follow the source with the stored vertical difference.
9. Repeat on the right side and confirm the side is determined by the picked point, not by guessing the source direction.
10. Repeat with a closed feature line.

## Relationship information

1. Run `CE_FLRELINFO` on each child.
2. Confirm source name, signed horizontal offset, vertical difference and step sequence are correct.
3. Run `CE_FLRELINFO` on an unrelated feature line and confirm it reports that no CE relationship exists.

## Update linked offsets

1. Change only source elevations, then run `CE_FLRELUPDATE` by selecting the source.
2. Confirm every child is refreshed with the original horizontal and vertical relationship.
3. Change the source plan geometry, including one curve, then run `CE_FLRELUPDATE` by selecting one child.
4. Confirm all children belonging to that source are rebuilt and retain their names, layers, styles, sites and stored offsets.
5. Confirm unrelated feature lines are untouched.
6. Confirm one `UNDO` reverses the complete update.

## Detach

1. Run `CE_FLRELDETACH` on one child and press Enter at the default `No`; confirm nothing changes.
2. Repeat and select `Yes`; confirm the geometry remains but `CE_FLRELINFO` reports no relationship.
3. Change the source and update the remaining children; confirm the detached line is not changed.

## Safety and failure cases

- Cancel at every prompt; no objects should be created or changed.
- Press Enter at every confirmation prompt; the default must be `No`.
- Test source and child objects on locked layers.
- Test a referenced or data-shortcut feature line.
- Test a very large offset that produces multiple/self-intersecting offset curves; the transaction must abort cleanly.
- Delete the source, then run `CE_FLRELUPDATE` or `CE_FLRELINFO` on a child; the missing source must be reported safely.
- Confirm source geometry and source metadata are never modified.
- Confirm the `Feature Line Tools` ribbon drop-down contains Create, Update, Info and Detach entries without duplicate buttons.
