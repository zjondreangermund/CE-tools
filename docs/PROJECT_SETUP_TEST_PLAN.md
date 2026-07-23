# CE Project Setup Test Plan

Target host: Civil 3D 2023.

Commands:

- `CE_PROJECT`
- `CE_PROJECTSETUP`
- `CE_PROJECTINFO`
- `CE_PROJECTCLEAR`

Always test on a copy of a drawing.

## Build and load

1. Build the Release configuration against Civil 3D 2023.
2. Install the user bundle.
3. Restart Civil 3D.
4. Confirm the **Project** panel appears on the **CE Tools** ribbon.
5. Confirm **Project Setup** launches `CE_PROJECT` and **Project Info** launches `CE_PROJECTINFO`.

## CE_PROJECTSETUP

Enter values for:

- Project Name
- Client
- Country
- Town
- Coordinate System
- Standards
- Drawing Template
- Units

Verify:

- all eight fields accept spaces;
- the values are reported after saving;
- rerunning Setup shows the stored values as defaults;
- changing one or more values replaces the complete record in one transaction;
- cancelling at any prompt leaves the previous record unchanged;
- the drawing can be saved, closed and reopened with the metadata intact;
- Save As produces a new drawing containing the same metadata;
- the metadata travels when the drawing is copied to another folder or computer;
- the command does not create visible drawing objects.

## CE_PROJECTINFO

Verify:

- all eight stored fields are reported correctly;
- fields intentionally left empty are shown as not set;
- a drawing without CE metadata reports that no record exists;
- the command makes no drawing changes.

## CE_PROJECTCLEAR

Verify:

- the current metadata is displayed before confirmation;
- pressing Enter or selecting No leaves the record unchanged;
- selecting Yes removes only the CE Tools project metadata XRecord;
- other Named Objects Dictionary records remain untouched;
- running Clear again reports that the record is absent;
- cancellation or failure does not partially alter the dictionary.

## Drawing lifecycle

Test metadata through:

- new drawing from a DWT;
- existing drawing;
- Save;
- Save As;
- drawing recovery where practical;
- insertion or external referencing, confirming that CE metadata remains associated with its source DWG and is not mistaken for host-drawing metadata.

## Release gate

Do not mark CE Project Setup production-ready until:

- Civil 3D 2023 compiles without errors;
- save/reopen and Save As tests pass;
- existing values are retained after cancelled edits;
- the Named Objects Dictionary record is inspected and confirmed portable;
- Clear removes only the intended XRecord;
- ribbon buttons and all direct commands work;
- known limitations are documented.
