# Water and Sewer Cost Estimate Test Plan

Target host: Civil 3D 2023.

## Ribbon and creation

1. Confirm **Analysis > Water & Sewer Cost Estimate** contains Tools, Create, Refresh, Information and Automatic Cost Refresh.
2. Run `CE_WSCOSTCREATE`, select the approved template when required, and create a new workbook.
3. Confirm the workbook opens without a repair warning and retains its rates, formulas, formatting and page layout.
4. Confirm `CE_WSCOSTINFO` reports the workbook path, drawing-unit scale, water/sewer asset counts and automatic state.

## Explicit refresh

1. Change representative water and sewer design geometry.
2. Run `CE_WSCOSTREFRESH`.
3. Confirm model-derived quantities update while rates and user-edited workbook content remain unchanged.
4. Confirm a missing or locked workbook fails safely and the drawing remains usable.

## Deferred automatic refresh

1. Turn automatic refresh on with `CE_WSCOSTAUTO`.
2. Edit relevant design geometry and complete the AutoCAD command.
3. Confirm the workbook refresh occurs only after the command ends, not during `ObjectModified` or `ObjectErased`.
4. Test Move, Stretch, Erase and Undo, checking the workbook quantities after each completed command.
5. Turn automatic refresh off and confirm subsequent drawing commands do not update the workbook.
6. Open a second drawing and confirm events and refresh state remain document-specific.
7. Close the linked drawing and confirm Civil 3D closes it without retained event-handler errors.

## Release gate

- Source validators and command registry pass.
- Civil 3D 2023 Release compilation passes.
- Workbook output opens in Microsoft Excel without repair prompts.
- Automatic refresh passes Move, Stretch, Erase, Undo, drawing switch and drawing close tests.
