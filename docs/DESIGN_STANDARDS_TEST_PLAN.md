# CE Design Standards Library - Civil 3D 2023 test plan

## Commands

- `CE_DESIGNSTANDARDS`
- `CE_STDBROWSE`
- `CE_STDSEARCH`
- `CE_STDAPPLY`
- existing `CE_STANDARDINFO`

## Browse and search

1. Run `CE_STDBROWSE` and test every category: Namibia, Roads, Pavement, Drainage, Settlements, General and All.
2. Confirm each listed item shows code, title, region, discipline, authority, edition note and source family.
3. Run `CE_STDSEARCH` with codes such as `TRH4`, words such as `drainage`, and multiple terms such as `road materials`.
4. Confirm searches are case-insensitive and all entered terms must match.
5. Confirm an unmatched query reports no results and does not change the drawing.
6. Confirm browse and search are read-only and do not create an undo record.

## Apply as primary

1. Start with a drawing that has no CE standards metadata.
2. Run `CE_STDAPPLY`, enter `NAM-RA`, accept Primary and review the preview.
3. Press Enter at the confirmation prompt; confirm the default No leaves the drawing unchanged.
4. Repeat and explicitly select Yes.
5. Run `CE_STANDARDINFO` and confirm the region, discipline, primary standard, authority, verification note and selection date were stored.
6. Save, close and reopen the DWG; confirm the values remain.
7. When CE Project metadata already exists, confirm its Standards field is synchronised.

## Apply additional standards

1. With a primary standard already stored, apply `TRH17`, `TRH25`, `REDBOOK` and `SANS-CIVIL` as Additional.
2. Confirm existing primary metadata is preserved.
3. Confirm additional standards are separated cleanly and the same catalogue item is not added twice.
4. Confirm project metadata remains synchronised.
5. Confirm `CE_STANDARDCLEAR` still clears the shared standards record created by the library.

## Compatibility with existing standards selection

1. Use `CE_STANDARDSELECT` to create a manual/custom selection.
2. Apply one library item as Additional and confirm all manually entered fields are preserved where applicable.
3. Apply a library item as Primary and confirm the preview clearly shows which region, discipline, primary standard, revision and authority will be replaced.
4. Run `CE_STANDARDS`, `CE_STANDARDINFO` and `CE_STANDARDCLEAR`; confirm they operate on the same metadata record and no duplicate standards record exists.

## Ribbon

1. Confirm the CE Tools tab contains a compact `Standards` panel.
2. Confirm its `Design Standards` drop-down contains Browse, Search, Apply and Current Project Standards.
3. Confirm the Project panel still contains Standards Selection for manual project setup.
4. Reload the ribbon and restart Civil 3D; confirm no duplicate Standards panels or buttons appear.

## Release gate

- All modifying actions must default to No and commit in one transaction.
- Cancellation at every prompt must preserve existing metadata.
- Catalogue content must remain clearly marked as a reference aid, not automatic compliance verification.
- Verify current editions, amendments, adoption and licensing outside CE Tools before production design.
