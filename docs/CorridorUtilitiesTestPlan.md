# Corridor Utilities Test Plan

Target host: Civil 3D 2023.

Commands:

- `CE_CORTOOLS`
- `CE_CORREPORT`
- `CE_CORBASE`
- `CE_CORREBUILD`

Always test on a copy of a drawing.

## Build and load

1. Build the Release configuration against Civil 3D 2023.
2. Install the user bundle.
3. Restart Civil 3D.
4. Confirm the **Corridors** panel appears on the **CE Tools** ribbon.
5. Confirm **Corridor Tools**, **Baseline & Regions** and **Rebuild Corridors** launch the correct commands.

## CE_CORREPORT

Test selections containing:

- one corridor;
- several corridors;
- a corridor with multiple baselines;
- a corridor with multiple regions;
- a corridor with corridor surfaces;
- a corridor with automatic rebuild enabled and disabled;
- an out-of-date corridor;
- a data-reference corridor;
- mixed corridors and non-corridor objects;
- a preselection set.

Verify:

- name, style and code set style are correct;
- baseline, region and corridor-surface counts match Corridor Properties;
- feature-line code count is reasonable for the corridor;
- automatic rebuild, out-of-date and reference states are correct;
- maximum triangle side length matches Corridor Properties;
- unsupported objects are skipped without stopping the command;
- the command makes no drawing changes.

## CE_CORBASE

Test:

- alignment/profile baselines;
- feature-line baselines;
- offset baselines where available;
- multiple regions using different assemblies;
- regions marked for processing;
- corridors with missing or referenced source objects.

Verify:

- baseline name and type are correct;
- start, end and calculated length match Corridor Properties;
- alignment, profile and feature-line source names are correct;
- region count is correct;
- region name, station range, length and assembly are correct;
- processing state is reported correctly;
- the command makes no drawing changes.

## CE_CORREBUILD

1. Select only up-to-date corridors.
2. Select one out-of-date corridor.
3. Select several out-of-date corridors.
4. Include a referenced corridor.
5. Include a corridor on a locked layer.
6. Cancel at the confirmation prompt.
7. Confirm the rebuild.
8. Undo once where Civil 3D supports corridor rebuild undo.

Verify:

- the preview separates rebuildable, up-to-date and skipped corridors;
- pressing Enter or choosing No makes no changes;
- referenced and locked-layer corridors are skipped;
- accepted out-of-date corridors rebuild successfully;
- a failure rolls back the complete transaction;
- the final count matches the corridors actually rebuilt;
- large corridor rebuilds do not trigger repeated rebuilds inside a loop beyond one call per corridor.

## Release gate

Do not mark the corridor utilities production-ready until:

- Civil 3D 2023 compiles without errors;
- all ribbon and command tests pass;
- baseline and region source names are validated for alignment/profile and feature-line baselines;
- reference and locked-layer skipping is confirmed;
- preview, cancellation and transaction rollback are confirmed;
- known limitations are documented.
