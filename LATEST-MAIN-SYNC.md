# CE Tools — Latest Main Sync

**Sync date:** 17 August 2026  
**Sync ID:** `2026-08-17-project-survey-production-comments-2`

This marker exists so downloaded/extracted repository copies can be checked against the current field-test source set.

Current August 17 expectations:

- Project Production is a one-page centre and does **not** contain Survey Location or Namibia LO/WGS84.
- Survey Production owns Survey Location / Coordinate System and Namibia LO/WGS84 conversion.
- Discipline Style Presets appears before Project Style Centre.
- Project Style Centre activates the saved discipline preset on first open when the current selection is still drawing defaults.
- Project Town / Coordinate System drives the Namibia LO central meridian.
- Drawing Book and Client Book use the Drawing Register **Title Block Source**, with the CE fallback only when the registered source cannot be inserted.
- Road corridor feature-line extraction and Platform slope feature-line production are included in the August 17 source set.

For Civil 3D 2023, use only:

`BUILD-INSTALL-CIVIL3D-2023.cmd`

The build stages the repository to a short local path, applies the final compatibility/field-test repairs, compiles, packages and installs CE Tools.
