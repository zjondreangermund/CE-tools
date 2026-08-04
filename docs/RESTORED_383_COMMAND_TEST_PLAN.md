# Restored 383-Command Civil 3D 2023 Test Plan

## Scope

This reconciliation restores the preserved V54/V60 command surface without replacing newer working implementations. The source registry must report at least 380 unique AutoCAD commands; the reconciled baseline currently reports 383.

## Load and registry checks

1. Build from a short local staging path for Civil 3D 2023 and .NET Framework 4.8.
2. Confirm staging reports that active source files are retained and recovery fallbacks are not applied.
3. Load the built bundle and confirm there are no duplicate-command or missing-type errors.
4. Confirm the CE TOOLS ribbon and floating Ctrl+F workflow window open.
5. Search the floating window for Stormwater, Sewer, Water, Flood, Parking, Xref, Hydraulic, Surface Correction, Network Schedule and Typical Details.

## Priority restored workflows

- Survey: continuous linked coordinates, polyline arrow refresh and reverse.
- Parking: boundary options, optimizer, grading, skew validation and automatic refresh.
- Stormwater: sequence, alignment, refresh, profile, settings and information.
- Sewer: selected-main sequence, format, profile, label sort/freeze and excavation schedule.
- Water: sequence, alignments, profiles, asset markers and refresh.
- Surfaces: correction, simplification, spike/hole repair, flow, catchment and ponding.
- Analysis: hydraulic review, return periods, flood reports/frames/animation and pump review.
- Production: road-section, network, standard-quantity and detailed-section schedules.
- Exchange: background/Xref backup and restore, project Xref split and model package workflows.
- Standards: typical details and the 33-record engineering asset catalogue.

## Regression checks

- Run `CE_REFRESHALL` and confirm restored annotation and schedule link families are included.
- Enable `CE_AUTOREFRESH`, edit linked sources and confirm refresh occurs only after the command ends.
- Confirm the newer compact coordinate table retains Point, Point Name, Y/Northing, X/Easting and Z/Elevation columns.
- Confirm `CE_PKNUMBERREFRESH` remains available in addition to the preserved parking commands.
- Confirm existing client books, drawing books, cost estimates and workflow-window behavior remain unchanged.

## Release gate

- [ ] 383 unique commands load without duplicates.
- [ ] Civil 3D 2023 compilation passes with zero errors.
- [ ] Priority restored workflows pass on copied drawings.
- [ ] Automatic manager initialization and drawing-close cleanup pass.
- [ ] Ribbon and Ctrl+F workflow search expose restored modules.
- [ ] Installer bundle hash verification passes.
