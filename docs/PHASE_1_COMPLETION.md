# CE Tools Phase 1 Completion

## Source-complete milestone

Phase 1 is complete in source on `main`. It contains 413 unique commands and
preserves the full recovered V54/V60 command surface. This milestone means each
original utility family has working source, a visual entry point, documentation
and a source regression gate. It does not replace the separate Civil 3D 2023
compile and live-drawing validation gate.

| Original Phase 1 family | Main entry | Source-complete implementation |
|---|---|---|
| Feature Line Utilities | `CE_FLTOOLS` | Reports, elevation changes, construction, surface elevations, point editing, weeding and linked stepped offsets |
| Alignment Utilities | `CE_ALTOOLS` | Reports, station/offset inquiry and shared dynamic annotations |
| Drawing Cleanup | `CE_DRAWCLEAN` | Visual selection plus direct full, OVERKILL, AUDIT and PURGE commands with confirmation |
| Survey Cleanup | `CE_SURVEYCLEANUP` | Survey-surface comparison, reversible correction/repair, coordinates and drawing cleanup |
| Background Preparation | `CE_BACKGROUNDTOOLS` | Audit, light-background preparation, XREF split, information and backup |
| Viewport Tools | `CE_VIEWPORTTOOLS` | All-layout report and confirmed lock/unlock controls for floating paper-space viewports |
| Hatch Tools | `CE_HATCHTOOLS` | Create, edit, match and draw-order controls for civil hatches |
| Layer Manager | `CE_LAYERTOOLS` | Readable layer register plus direct access to AutoCAD Layer Properties |
| Excel Tools | `CE_EXCELTOOLS` | Linked BOQ, setting-out, survey-comparison, report and drawing-book exports |
| Coordinate Utilities | `CE_COORDINATE` | Pick/COGO labels, crosses, linked XYZ tables, multi-polyline vertex points and dynamic refresh |
| Label Utilities | `CE_LABELTOOLS` | Shared paper-height settings and coordinate/alignment/profile/surface/feature-line/corridor/parking annotations |
| Parking Utilities | `CE_PKTOOLS` | Closed rows, double rows, counting, linked numbering, boundary options, reports, grading and automatic refresh |

## Cross-cutting completion

- `CE_PHASE1` opens every Phase 1 family from one grouped WPF window.
- Parent utility menus use visual windows; drawing object selection, grip editing
  and insertion points remain native Civil 3D canvas interactions.
- Annotation settings use 1.8, 2.0, 2.5, 3.5 and 5.0 mm paper heights with
  MLeader, MText and supported COGO output.
- The automatic Ctrl+F workflow centre includes complete General, Survey, Roads,
  Stormwater, Sewer, Water, Bulk Water and Flood step sequences.
- The command registry, Phase 1 ledger and all relevant ribbon launchers are
  protected by `Validate-PhaseOneCompletion.py`.

## Separate Civil 3D 2023 release gate

The following work is deliberately recorded as validation rather than missing
Phase 1 source:

1. Compile the exact `main` commit against Civil 3D 2023/.NET Framework 4.8.
2. Run command-specific tests in disposable Civil 3D drawings.
3. Correct Autodesk API/runtime errors, style differences and event sequencing.
4. Verify one-step Undo, save/reopen, drawing switching and dynamic refresh.
5. Build and install the versioned package, then verify the installed manifest.
6. Code-sign the public release after the source/runtime gate passes.
