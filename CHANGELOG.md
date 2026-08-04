# Changelog

All notable CE Tools changes will be recorded here.

## 0.61.0-alpha — 2026-08-04

### Added

- Searchable `CE_SETTINGS` settings centre covering 21 General, Survey, Roads,
  Parking, Stormwater, Sewer, Water, Flood and Production configuration workflows.
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
