# Batch 8 Civil 3D Test Plan — Ribbon Presentation and Client Books

## Purpose

Validate the redesigned CE Tools ribbon and the linked A4/A3 project-closeout
client-book workflow in Civil 3D 2023 and Civil 3D 2024.

This plan must be completed against the exact PR head before merge. GitHub
source validators do not compile or execute Autodesk APIs.

## Test environment record

Record before testing:

- Civil 3D version and build number
- Windows version
- CE Tools Git commit SHA
- DLL SHA-256
- drawing name and copy location
- tester and date
- screen scaling percentage and monitor resolution
- AutoCAD colour theme

Test at a minimum on:

- 1920 × 1080 at 100% scaling
- 1920 × 1080 at 125% scaling
- a smaller or laptop display where ribbon panels must collapse cleanly
- both dark and light AutoCAD themes when available

## 1. Build validation

Run:

```powershell
.\scripts\Build-CE-Tools.ps1 -Version 2023 -Configuration Release
.\scripts\Build-CE-Tools.ps1 -Version 2024 -Configuration Release
```

Acceptance:

- both builds complete with no compiler errors;
- `RibbonVisuals.cs` resolves all WPF types;
- `RibbonMenuButton.Image` and `LargeImage` compile against both Autodesk hosts;
- `ClientBookCommands.cs` resolves Layout, Table, MText, Xrecord and file-dialog APIs;
- the application bundle contains the exact tested DLLs.

## 2. Ribbon appearance

Start Civil 3D with CE Tools installed through the application bundle.

Acceptance:

- one `CE TOOLS` tab is shown;
- no duplicate tab or panels appear after workspace changes or ribbon reload;
- the tab contains these clearly separated panels:
  - Project
  - Survey
  - Drawings
  - Geometry
  - Corridors
  - Site Design
  - Utilities
  - Standards & Details
  - Analysis
  - Production
- panel titles are readable and consistently capitalised;
- primary flyout buttons use large blue/green branded icons;
- icons remain sharp at 100%, 125% and 150% display scaling;
- icon backgrounds remain legible in dark and light themes;
- multi-line button labels are not clipped;
- flyout arrows are visible;
- the ribbon collapses gracefully on a smaller display without horizontal
  corruption or inaccessible commands.

## 3. Ribbon command preservation

Open every flyout and confirm the commands shown are appropriate to its panel.

At minimum verify:

- `CE_BMVERT`
- `CE_TLENGTH`
- `CE_TAREA`
- project setup and restore commands
- linked survey commands
- feature-line commands
- alignment, profile and surface reports
- corridor rebuild
- parking validation and numbering
- linked BOQ commands
- dynamic cross-section commands
- reports and existing drawing-book commands
- new client-book commands

Acceptance:

- no working Batch 1–7 command has been removed;
- no ribbon item launches the wrong command;
- no duplicate global command-name warning appears at startup;
- menu tooltips describe the actual workflow.

## 4. Vector-icon stability

Perform:

1. switch between light and dark themes;
2. switch workspaces;
3. hide and restore the ribbon;
4. use `RIBBONCLOSE` and `RIBBON`;
5. open a second drawing;
6. close and restart Civil 3D.

Acceptance:

- icons remain visible;
- no missing-resource placeholder appears;
- no external PNG path is required;
- no unhandled WPF exception is written to the command line;
- memory use does not grow significantly after repeated ribbon recreation.

## 5. Project closeout command

Prepare a drawing with:

- completed CE project metadata;
- several road/site/utility layers and objects;
- at least one linked BOQ;
- at least one linked dynamic cross section;
- existing project paper-space layouts.

Run:

```text
CE_PROJECTCLOSEOUT
```

Enter an issue stage and revision, review the preview and confirm.

Acceptance:

- both A4 and A3 page sets are created;
- seven layouts are created for each paper family:
  - 00 Cover and Issue Information
  - 01 Project Summary
  - 02 Design Discipline Summary
  - 03 Quantity Summary
  - 04 Drawing Register
  - 05 Cross-Section Register
  - 06 Typical Detail Schedule
- existing non-CE layouts are retained;
- existing non-CE entities inside layouts are not erased;
- each generated page has a true-size paper frame;
- each page has a consistent CE Tools header and title block;
- project, client, location, stage, revision and issue date are correct;
- A4 pages use 297 × 210 mm geometry;
- A3 pages use 420 × 297 mm geometry;
- page content remains inside margins;
- no table extends beyond the paper frame.

## 6. A4-only and A3-only books

Run `CE_CLIENTBOOK` in a clean copy of the test drawing.

Test separately:

- A4
- A3
- Both

Acceptance:

- only the selected paper family is created;
- stable layout names are used;
- re-running the command refreshes CE-generated content instead of duplicating it;
- the command reports created, refreshed and failed page counts.

## 7. Linked refresh

After creating a client book:

1. change the project name or client metadata;
2. add and delete model-space design objects;
3. refresh or modify a linked BOQ;
4. add a linked dynamic cross section;
5. rename or add a project layout;
6. run `CE_CLIENTBOOKREFRESH`.

Acceptance:

- all linked client-book pages refresh;
- old CE-generated page entities are removed;
- replacement entities are created once;
- project and production counts change correctly;
- the drawing register reflects current non-client layouts;
- the cross-section register reflects current linked source lines;
- stage and revision stored with each book are retained;
- stale generated handles do not stop other pages from refreshing;
- Undo restores the complete refresh transaction safely where supported.

## 8. Client-book information

Run:

```text
CE_CLIENTBOOKINFO
```

Acceptance:

- a grid lists paper family, sheet number, title, layout, stage and revision;
- valid and stale generated-handle totals are shown;
- optional table placement works;
- no internal ObjectId is exposed as a user-facing identifier.

Delete one generated page object manually and rerun the command.

Acceptance:

- the page reports at least one stale handle;
- `CE_CLIENTBOOKREFRESH` repairs the page.

## 9. Excel index

Run:

```text
CE_CLIENTBOOKINDEX
```

Acceptance:

- an `.xlsx` file is created;
- all linked A4/A3 pages are listed in sheet order;
- stage and revision are included;
- Microsoft Excel opens the workbook without a repair warning;
- LibreOffice compatibility may be observed but does not replace Excel testing.

## 10. Summary content

Validate each generated page:

### Cover

- project and client names are correct;
- location, stage, revision and issue date are present;
- linked BOQ, section and layout status is current.

### Project Summary

- all stored project metadata fields are represented;
- missing values are clearly marked rather than guessed.

### Design Discipline Summary

- detected disciplines and object totals are reasonable;
- classification is checked against the test drawing's naming standards.

### Quantity Summary

- length, area and volume values are plausible;
- the sheet is clearly identified as a summary rather than a certified BOQ.

### Drawing Register

- ordinary project layouts are listed;
- client-book pages are not recursively listed as project drawings.

### Cross-Section Register

- each linked section source is listed once;
- layer and name/type are correct.

### Typical Detail Schedule

- the schedule includes transport, parking, drainage, sewer and water detail
  categories inspired by the provided presentation examples;
- it does not claim that reference-image dimensions are approved design data;
- relevant details are marked as suggested;
- approved DWG blocks remain a separate engineering review requirement.

## 11. Plot and PDF review

Assign office-approved plot settings manually.

Publish:

- the complete A4 client book;
- the complete A3 client book.

Acceptance:

- correct paper sizes are selected;
- linework, title blocks and tables are readable;
- cyan/yellow screen accents plot according to the approved CTB/STB;
- no content is clipped;
- page order is correct;
- PDFs open without errors;
- file size and visual quality are suitable for client issue.

CE Tools must not silently choose or overwrite PC3, CTB/STB or canonical media
settings.

## 12. Failure and transaction tests

Test cancellation at every prompt.

Also test:

- locked layers;
- a read-only drawing;
- malformed project metadata;
- a drawing with no model objects;
- a drawing with no linked BOQ or cross section;
- an existing layout with the same name;
- a partially deleted client book;
- a failed table creation;
- closing the drawing during normal idle time after generation.

Acceptance:

- cancellation does not mutate the drawing;
- a failed page does not erase unrelated layout content;
- errors identify the affected layout;
- the command continues reporting other page results where safe;
- no drawing corruption or Civil 3D crash occurs.

## Release gate

Batch 8 may leave draft status only after:

- Civil 3D 2023 compile passes;
- Civil 3D 2024 compile passes;
- ribbon appearance passes at required display scales;
- all preserved commands are reachable;
- A4 and A3 books pass repeated generation and refresh;
- Excel opens the generated index without repair;
- A4 and A3 PDFs pass client readability review;
- no approved office plotting configuration is overwritten;
- all defects are fixed and the exact new PR head is retested.
