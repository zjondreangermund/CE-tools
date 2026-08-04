# Workflow Command Centre Test Plan

Target host: Civil 3D 2023.

## Startup and shortcut

1. Build and install the Release bundle for Civil 3D 2023.
2. Start Civil 3D and wait until the CE TOOLS ribbon is visible.
3. Confirm the workflow command centre opens automatically once per CE Tools load.
4. Close the window and press Ctrl+F while the drawing editor/ribbon has focus.
5. Confirm the same modeless window opens and Civil 3D remains responsive.
6. Run `CE_WORKFLOWS` and `CE_TOOLSPALETTE`; both must activate the same window without creating duplicates.

## Discipline tabs

Confirm these tabs are visible:

- General
- Survey
- Roads
- Stormwater
- Sewer
- Water
- Bulk Water
- Flood

## Survey workflow

Confirm the Survey tab contains clickable workflow steps for:

1. `CE_COORDSYSASSIGN`
2. `CE_COORDPICK2`
3. `CE_COORDCROSS2`
4. `CE_COORDPOLY2`
5. `CE_COORDTABLE2`
6. `CE_COORDREFRESH`
7. `CE_PLDIR`

Confirm the available-command area also shows the current Survey and Coordinate Tools ribbon commands. Test the search box with `coordinate`, `COGO`, `table`, and `direction`, and confirm only matching Survey-tab commands remain visible.

## Regression checks

- Existing CE TOOLS ribbon panels remain visible and clickable.
- Closing and reopening the workflow window does not duplicate event handlers or windows.
- Escape clears the workflow search field without closing Civil 3D.
- Clicking a workflow button sends the exact existing command and does not introduce a duplicate command implementation.
