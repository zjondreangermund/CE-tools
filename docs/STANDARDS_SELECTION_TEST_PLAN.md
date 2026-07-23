# Standards Selection Test Plan

Target host: Civil 3D 2023.

Commands:

- `CE_STANDARDS`
- `CE_STANDARDSELECT`
- `CE_STANDARDINFO`
- `CE_STANDARDCLEAR`

Always test on a copy of a drawing.

## Build and load

1. Build the Release configuration against Civil 3D 2023.
2. Install the user bundle.
3. Restart Civil 3D.
4. Confirm **Standards Selection** appears on the temporary **Project** panel.
5. Confirm the ribbon button launches `CE_STANDARDS`.

## CE_STANDARDSELECT

Test each region/framework option:

- Namibia;
- South Africa;
- International;
- Custom.

Enter and verify:

- design discipline;
- primary standard;
- additional standards;
- edition or revision;
- approval authority;
- notes;
- the automatic selection date.

Verify:

- the preview displays all proposed fields;
- pressing Enter or choosing No at confirmation makes no change;
- choosing Yes writes one `CE_TOOLS/STANDARDS_SELECTION` XRecord;
- rerunning the command shows existing values as defaults;
- entering `-` clears an optional field;
- values containing spaces and punctuation are preserved;
- one transaction writes the complete record;
- cancelling at any prompt leaves the old record unchanged.

## Project metadata synchronisation

1. Run `CE_PROJECTSETUP` and create project metadata.
2. Run `CE_STANDARDSELECT` and save a standards selection.
3. Run `CE_PROJECTINFO`.
4. Confirm the project **Standards** field contains the primary, additional and edition/revision summary.
5. Repeat in a drawing with no CE Project metadata and confirm standards selection still saves successfully.

## CE_STANDARDINFO

Verify:

- a drawing with no selection reports that no record exists;
- every stored field is reported correctly;
- the command makes no drawing changes;
- the status clearly states that compliance is not automatically verified.

## CE_STANDARDCLEAR

1. Display an existing selection.
2. Press Enter or choose No and confirm the record remains.
3. Choose Yes and confirm the standards record is removed.
4. Where project metadata exists, confirm its Standards field is also cleared.
5. Run the command again and confirm it safely reports that no selection exists.

## Persistence

Verify the record after:

- save and reopen;
- Save As to a new DWG;
- copying the DWG to another folder;
- creating a drawing from a template that already contains CE Tools metadata.

## Release gate

Do not mark Standards Selection production-ready until:

- Civil 3D 2023 compiles without errors;
- all ribbon and direct command tests pass;
- Named Objects Dictionary storage persists correctly;
- project-metadata synchronisation is confirmed;
- cancellation, confirmation and replacement safety are confirmed;
- the UI states that selected standards must still be verified against the project contract, authority requirements and current editions.
