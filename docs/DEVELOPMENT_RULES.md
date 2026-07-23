# CE Tools Development Rules

## No duplicate tools

Before adding a new command, ribbon button, source file, issue or workflow:

1. Search the existing `CE_` commands, source files, ribbon modules, open issues and `docs/IMPLEMENTATION_STATUS.md`.
2. Extend an existing module when the requested function belongs to that module.
3. Add a new direct command only when the workflow is genuinely different.
4. Do not create a second ribbon button for a function that already exists; add it to the existing flyout/subcommand group.
5. Keep one application entry point and one ribbon builder only.

## Current overlap mapping

- Entire-network sewer renaming is an enhancement to `CE_SEWSEQ`, not a new sewer renaming tool.
- Polyline-vertex COGO points and XYZ tables extend `CE_COORDINATE` / Survey Tools.
- Change-all-to-colour-250 remains `CE_COLOR250` / `COLOR250`.
- Design-standard libraries and manuals extend `CE_STANDARDS` and the Standards category.
- Automatic parking layout and standards-based parking options extend `CE_PKTOOLS`.
- Relative or linked feature-line workflows extend `CE_FLTOOLS` / `CE_FLEDIT`.
- Alignment creation validation and naming automation extend `CE_ALTOOLS`.
- Profile, surface and corridor presentation or cleanup functions extend their existing modules.
- Quantity schedules and model-driven BOQ features extend the Analysis / Quantity module rather than creating unrelated quantity commands.

## Enhancement naming

When expanding an existing module:

- Preserve the existing public command.
- Add a clear subcommand or direct command under the same module.
- Preserve existing behaviour unless the change is an explicit correction.
- Record the change in the existing GitHub issue where practical.

## Release safety

- Build and compile before installation.
- Do not install when the build reports errors.
- Test new enhancements on copies of drawings.
- Avoid duplicate class names, assembly attributes, command names and ribbon IDs.
