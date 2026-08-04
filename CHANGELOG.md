# Changelog

All notable CE Tools changes will be recorded here.

## 0.61.0-alpha — 2026-08-04

### Added

- Source-complete Phase 1 utility hub `CE_PHASE1` covering the original Feature
  Line, Alignment, Drawing/Survey Cleanup, Background, Viewport, Hatch, Layer,
  Excel, Coordinate, Label and Parking families.
- Real paper-space viewport reporting and confirmed all-layout lock/unlock tools.
- Readable layer register plus direct AutoCAD Layer Properties access.
- Excel, Label and Survey Cleanup visual workflow hubs built from existing linked
  exports, dynamic annotation and reversible surface-correction commands.
- Direct `CE_DRAWCLEANALL`, `CE_DRAWOVERKILL`, `CE_DRAWAUDIT` and
  `CE_DRAWPURGE` commands, eliminating ribbon-supplied keyword input.
- Searchable `CE_SETTINGS` settings centre covering 21 General, Survey, Roads,
  Parking, Stormwater, Sewer, Water, Flood and Production configuration workflows.
- Dedicated WPF workflow launchers for `CE_SWTOOLS`, `CE_SEWTOOLS` and
  `CE_WATERTOOLS`, replacing the former keyword menus with ordered production steps.
- Shared visual launchers for Parking, parking grading/monitoring, BOQ, Hydraulic,
  Flood, Hydrology, dynamic cross sections, polyline direction, coordinates,
  coordinate systems, profile utilities and drawing production.
- Drawing-style dropdown catalogues for Stormwater, Sewer and Water alignment,
  profile, profile-view, label-set and band-set settings.
- Persisted profile-view columns and horizontal/vertical spacing for all three
  utility disciplines, plus discipline-specific surface-selection windows.
- `CE_SETTINGSAUDIT` settings-coverage report and direct `CE_SETTINGSCENTER` alias.
- `CE_ABOUT`, `CE_VERSION` and `CE_RELEASEINFO` exact loaded-build reporting.
- `CE_INSTALLVERIFY` SHA-256 verification against the bundle release manifest.
- `CE_UPDATECHECK` safe GitHub release check that never silently replaces a DLL
  while Civil 3D is running.
- V61 version metadata shared by the .NET assemblies and Autodesk application bundle.
- Versioned Civil 3D 2023 release ZIPs containing a double-click administrator
  installer, source-commit manifest, SHA-256 checksum register and installation guide.
- GitHub Actions source gates and an opt-in self-hosted Windows/Civil3D2023 build,
  artifact and tagged-release workflow.

### Changed

- Repaired the installed Civil 3D 2023 presentation path: the CE TOOLS ribbon
  now falls back to text-only controls if cached icon creation fails, and menu
  item identifiers are unique across the complete command surface.
- Corrected CE annotation semantics so 1.8, 2.0, 2.5, 3.5 and 5.0 are paper
  text heights; MText, MLeaders, branch labels and tables now calculate their
  model-space size from the active annotation scale and drawing units.
- Sewer and stormwater branch labels repeat along long branches and use a
  selectable Above, Below or Alternating perpendicular offset instead of being
  placed directly over the alignment.
- Linked coordinate naming now writes the Civil 3D COGO raw description and
  coordinate tables prefer that raw description, keeping visible point labels
  and table names such as P1 synchronized. Dynamic refresh also migrates P1/P2
  names stored by earlier builds into the corresponding raw descriptions.
- Project Style Centre now reads Civil 3D style collections through enumerable,
  object-ID and indexed collection APIs and keeps its selector area clear at
  Civil 3D display scaling.
- Coordinate naming/register choices, stormwater source/main-branch choices and
  ribbon visual settings now use popup windows while drawing picks remain in the
  Civil 3D canvas.
- Stormwater, Sewer and Water confirmations now use modal Yes/No windows, while
  drawing-dependent insertion-point selection remains in the Civil 3D canvas.
- Standard annotation paper heights 1.8, 2.0, 2.5, 3.5 and 5.0 are selectable
  directly in each production settings window.
- High-use parent commands now launch described, grouped workflow buttons while
  preserving native Civil 3D canvas prompts for selections and geometry input.
- Shared annotation settings now use a WPF settings window with all five standard
  paper heights and MLeader/MText/COGO output choices.
- General, Survey, Stormwater, Sewer, Water, Bulk Water and Flood workflow tabs
  now include complete ordered step sequences instead of partial summaries.
- Reconciled the first V61 Civil 3D 2023 compiler report: shared report calls
  accept a safe default table title, dynamic annotation creation returns generated
  object IDs, linked feature-line refresh is callable by the shared refresh engine,
  stored coordinate parsing initializes short-circuited values, ribbon icon mode
  compatibility is restored, surface choices are shared with production modules,
  and survey workflows import LINQ explicitly.
- Restored the legacy report-row overload used by engineering asset, culvert,
  hydrology, audit, pump and project reports; its first row is preserved as the
  table headings while the remaining rows stay as report data.
- The verified installer now validates every manifest file in the source, staging
  and installed bundles, compares both Civil and Core DLLs, and records version,
  source commit and hashes before removing its rollback copy.
- Both supported Civil 3D 2023 build paths now create the same versioned release
  package before optional installation.
- The complete public AutoCAD command surface is now 412 unique commands.

## 0.1.0-alpha — 2026-07-23

### Added

- `CE_BMVERT` batch densification command for lightweight polylines.
- Equal-chainage **Maximum spacing** and **Number of intervals** modes.
- True arc preservation using split bulge calculations.
- Support for open and closed line-and-arc polylines.
- Variable-width continuity at inserted vertices.
- CE Tools ribbon tab with a Roads panel.
- Civil 3D 2023 and 2024 application bundle configurations.
- Build, installation, uninstallation and Civil 3D validation instructions.
- Host-independent automated tests for spacing plans and bulge mathematics.
