# CE Tools reconciliation status

Branch: `preserve/all-ce-tools-work-2026-07-30`

Draft PR: #48

Tracker: #49

## Completed safeguards

- Main remains untouched while recovered versions are reconciled.
- Civil 3D 2023 is the default build target on the preservation branch.
- Windows Forms support is restored for dialog-based commands.
- The V54 Civil 3D source inventory is recorded.
- A preservation regression validator is present.
- GitHub Actions now runs the preservation, command-registry and presentation/client-book validators.

## Active reconciliation order

1. Sewer and stormwater branch naming, label repetition and scale-aware offset.
2. Alignment/profile creation, styles and band sets.
3. Survey coordinates, COGO links, dynamic tables and P1 numbering.
4. Parking blocks, arrows, reversal and boundary refresh.
5. Linked BOQs, quantity centres and cost estimates.
6. Floating workflow window and discipline command visibility.
7. Assets, PDFs, DWGs, spreadsheets and installation bundle.
8. Civil 3D 2023 compile and in-product regression test.

## Non-negotiable branch-label behaviour

The restored branch-label implementation must retain:

- `BranchLabelOffsetFactor = 2.75`;
- scale-aware paper distance;
- user choice for labels above or below the alignment;
- rotated text following branch geometry;
- repeated labels along long branches;
- annotative text using the selected paper height;
- refresh without duplicate generated labels.

## Merge policy

PR #48 stays in draft until all validators pass and the exact PR head has compiled and been tested in Civil 3D 2023. No recovered source file may replace a newer implementation without a file-by-file comparison and a recorded regression decision.
