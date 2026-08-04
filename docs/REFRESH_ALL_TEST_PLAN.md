# Shared Linked-Output Refresh Test Plan

## Scope

Validate `CE_REFRESHALL` and `CE_REFRESHSTATUS` in Civil 3D 2023 using a copy of a representative project drawing. The shared command refreshes only linked outputs that can be rebuilt without additional input. Client books and project summaries remain separate, confirmed workflows.

## Preconditions

- Install the current `CE Tools.bundle` in Civil 3D 2023.
- Open a test drawing containing as many of these CE links as practical: coordinate tables, setting-out schedules, parking numbers, surface-comparison labels/tables, linked BOQs, water/sewer cost workbook, and dynamic cross sections.
- Save a recoverable copy before testing.

## Status checks

1. Run `CE_REFRESHSTATUS` from the command line and from **Analysis > Quantity & BOQ Tools > Linked Output Refresh Status**.
2. Confirm the pop-up reports the expected linked-object counts.
3. Confirm dynamic-section manager and pending state are readable.
4. Confirm automatic cost-estimate refresh reports `On` only when a valid linked workbook exists.
5. Choose **Place Table**, confirm the table is centred/readable, then Undo it.

## Refresh checks

1. Modify source geometry for each available linked-output type.
2. Run `CE_REFRESHALL` from the ribbon.
3. Confirm every available linked output updates and the command-line summary reports its processed count.
4. Confirm a missing or stale source in one module does not prevent other modules from refreshing.
5. Run `CE_REFRESHALL` again without model changes and confirm it completes safely.
6. Undo the source changes and refresh again; confirm outputs return to the expected state.
7. Save, close, reopen and repeat the status and refresh commands.

## Regression checks

- Run each existing single-module refresh command and confirm it still works.
- Confirm `CE_REFRESHALL` does not rebuild client books, project summaries, layouts or issue sheets.
- Confirm no duplicate-command warning appears while loading the plugin.
- Switch between two drawings and confirm only the active drawing is processed.
- Review the linked water/sewer workbook in Excel and confirm rates and formatting remain unchanged.

## Release gate

- [ ] Civil 3D 2023 build passes.
- [ ] Ribbon and General workflow entries execute both commands.
- [ ] All linked-output types available in the test drawing pass.
- [ ] Stale-link isolation passes.
- [ ] Repeated refresh and Undo pass.
- [ ] Save/reopen and drawing-switch checks pass.
