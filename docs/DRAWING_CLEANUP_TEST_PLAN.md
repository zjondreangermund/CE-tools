# CE_DRAWCLEAN Civil 3D 2023 validation plan

## Safety setup

- Close and reopen Civil 3D after installing the latest bundle.
- Test on a copy of a drawing, not the only project file.
- Save the drawing before each test so the result can be compared and undone.

## Full cleanup

1. Create duplicate lines, overlapping line segments and duplicate polylines.
2. Add at least one unused layer, linetype, text style and block definition.
3. Run `CE_DRAWCLEAN` and accept `All`.
4. Confirm the preview reports OVERKILL, AUDIT and PURGE.
5. Press Enter at the confirmation prompt and confirm nothing changes because the default is No.
6. Run again, choose Yes and confirm:
   - duplicate/overlapping supported geometry is cleaned;
   - AUDIT runs with fixes enabled;
   - unused named objects are purged through repeated passes;
   - the command returns to a normal command prompt.

## Preselection

1. Preselect only a small group of duplicate geometry.
2. Run `CE_DRAWCLEAN Overkill`.
3. Confirm only the preselected objects are passed to OVERKILL.
4. Confirm unrelated geometry remains unchanged.

## Individual stages

- Run `CE_DRAWCLEAN Overkill` and confirm AUDIT/PURGE do not run.
- Run `CE_DRAWCLEAN Audit` and confirm OVERKILL/PURGE do not run.
- Run `CE_DRAWCLEAN Purge` and confirm OVERKILL/AUDIT do not run.

## Ribbon

- Open the CE Tools tab.
- Confirm the Drawings panel contains neat rows for `Drawing Tools` and `Drawing Cleanup`.
- Open the Drawing Cleanup drop-down and test Full Cleanup, OVERKILL Only, AUDIT Only and PURGE Only.
- Confirm no old duplicate Drawings panels or duplicate command buttons remain.

## Release gate

- Build succeeds against AutoCAD/Civil 3D 2023 managed assemblies.
- No command remains active or stuck at a prompt after cleanup.
- One UNDO can reverse OVERKILL database changes where supported by AutoCAD.
- The drawing saves, closes and reopens normally after cleanup.
