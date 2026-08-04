# Changelog

All notable CE Tools changes will be recorded here.

## 0.61.0-alpha — 2026-08-04

### Added

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

- Stormwater, Sewer and Water confirmations now use modal Yes/No windows, while
  drawing-dependent insertion-point selection remains in the Civil 3D canvas.
- Standard annotation paper heights 1.8, 2.0, 2.5, 3.5 and 5.0 are selectable
  directly in each production settings window.
- High-use parent commands now launch described, grouped workflow buttons while
  preserving native Civil 3D canvas prompts for selections and geometry input.
- The verified installer now validates every manifest file in the source, staging
  and installed bundles, compares both Civil and Core DLLs, and records version,
  source commit and hashes before removing its rollback copy.
- Both supported Civil 3D 2023 build paths now create the same versioned release
  package before optional installation.
- The complete public AutoCAD command surface is now 397 unique commands.

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
