# CE_FLWEED validation checklist

- Test on a copy of a Civil 3D 2023 drawing.
- Select one ordinary open feature line containing PI points and multiple elevation points.
- Run with the defaults: vertical tolerance `0.010` and minimum spacing `0.500`.
- Confirm the preview reports the number of candidate points and affected feature lines.
- Press Enter at the confirmation prompt and confirm no points are deleted.
- Run again, answer `Yes`, and confirm only elevation points are deleted; PI points must remain.
- Confirm one `UNDO` restores all deleted points from the command run.
- Enter minimum spacing `0` and confirm the command uses only the vertical-deviation test.
- Test multiple selected feature lines and confirm one transaction updates the full selection.
- Test a referenced feature line, a feature line on a locked layer, a corridor-derived feature line and a closed feature line; each must be skipped safely.
- Test a line with no removable elevation points and confirm the drawing is unchanged.
- Test adjacent dense points and confirm the conservative first pass does not remove adjacent candidates in one run.
- Run the command a second time and confirm further safe simplification is possible.
- Compare the resulting profile/elevations against the original and confirm the retained grade remains within the entered vertical tolerance.
