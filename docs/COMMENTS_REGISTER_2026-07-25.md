# CE Tools comments register — 25 July 2026

Source review file: `CE Tools - Comments - 25-07-2026.docx`.

## Imported colour rules

- **Red:** no update was recorded in the review document; implement first.
- **Yellow:** new active comment; deliver in grouped follow-up batches.
- **Green:** previously corrected; preserve and regression-test.
- **Struck through:** superseded by a newer comment; exclude from active scope.

## Imported counts

The colour-aware document review found:

- 4 active red requirements;
- 313 active yellow requirements after removing repeated wording within each section;
- 8 green regression requirements;
- 42 struck-through requirements excluded as superseded.

## Current branch and review boundary

- Branch: `followup/comments-2026-07-25`
- Draft PR: `#37`
- Base: the exact PR #36 head that compiled successfully in Civil 3D 2023.
- GitHub source validation does not replace Autodesk compilation or runtime testing.

## Red requirements

### Project Setup

- [x] Replace separate project setup prompts with one pop-up window.
- [x] Keep the option to place a table showing all saved project results.
- [ ] Civil 3D 2023 runtime test.
- [ ] Civil 3D 2024 runtime test.

Implementation notes:

- `ProjectSetupPopupWindow` presents all fields in one window and preloads existing DWG values.
- The existing review, transaction, clear/restore backup and optional drawing-table workflows remain in place.

### Alignment labels

- [x] Route the legacy alignment-label workflow to the shared annotation settings.
- [x] Present 1.8, 2.0 and 5.0 text-height choices instead of inheriting a drawing text size such as 5000.
- [ ] Civil 3D 2023 runtime test.
- [ ] Civil 3D 2024 runtime test.

### Parking count and numbering

- [x] Generate every single-row parking bay as an individual closed polyline.
- [x] Generate every double-row parking bay as an individual closed polyline.
- [x] Keep the generated bays directly selectable by report, count and numbering commands.
- [ ] Civil 3D 2023 runtime test.
- [ ] Civil 3D 2024 runtime test.

## Yellow requirements started in this PR

### Ribbon names

- [x] Prefix CE Tools panel, flyout and command labels with `CE -`.
- [ ] Confirm that all names remain readable at normal Civil 3D ribbon widths.
- [ ] Confirm that no menu text is clipped badly in Civil 3D 2023 and 2024.

### Floating second-screen launcher

- [x] Add `CE_TOOLSPALETTE`.
- [x] Read the currently loaded CE Tools ribbon commands rather than maintaining a duplicate fixed command list.
- [x] Display each command as an individual searchable button without flyout navigation.
- [x] Keep the original CE TOOLS ribbon tab available.
- [x] Use a normal modeless, resizable window that can be dragged to another monitor.
- [ ] Test icon rendering, search, focus, command execution and restart behaviour in both supported Civil 3D versions.

## Remaining yellow batch roadmap

The detailed wording remains in the source review document. Work is grouped to avoid destabilising the entire add-in at once.

| Batch | Scope | Imported active comments |
|---|---|---:|
| A | Project/ribbon styles and undo/redo review | 3 remaining |
| B | Coordinate Tools and Survey Utilities | 36 |
| C | Drawing, Cleanup, Hatch and basic Road Tools | 7 |
| D | Feature Line Tools | 22 |
| E | Alignment, Profile and Surface Tools | 52 |
| F | Corridor and Parking Tools | 29 |
| G | Pipe Networks, water, sewer and stormwater production | 64 |
| H | Quantities, BOQ and reports | 56 |
| I | Dynamic cross sections and intersections | 10 |
| J | Plan production, books, printing and refresh | 9 |
| K | Typical details library and dynamic variants | remaining library/workflow comments |

Counts are planning aids; repeated cross-discipline requirements such as annotative text, dynamic tables and overlap correction will be implemented through shared infrastructure rather than copied independently into every command.

## Green requirements to preserve

Regression coverage must retain these previously corrected behaviours:

- project information can be recovered after clear;
- coordinate-system assignment opens Autodesk's native selection interface;
- polyline direction arrows work and remain linked;
- Bellmouth Densifier remains available;
- constant grades between feature-line endpoints remain available;
- Total Length remains working;
- Total Area remains working;
- any other green-highlighted behaviour in the source review remains unchanged.

## Superseded comments

Struck-through items are not implementation targets. A newer non-struck comment takes precedence, and old behaviour must not be reintroduced merely to satisfy superseded wording.
