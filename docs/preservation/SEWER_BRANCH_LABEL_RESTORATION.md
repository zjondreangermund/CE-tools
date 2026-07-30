# Sewer Branch Label Restoration

The preserved branch-label behaviour from the V54 working source is now represented in active source through `SewerBranchLabelPlacement.cs`.

## Preserved rules

- Scale-aware offset based on paper height and the active annotation scale.
- Offset factor: `2.75`.
- Above/below alignment placement through the normal vector.
- Repeated labels along the full branch length.
- Maximum 200 labels per branch.
- Readable rotation normalisation.
- Annotative MText with background fill.
- Supported paper heights remain 2.5 mm, 3.5 mm and 5.0 mm in the calling workflow.

## Integration gate

The next step is to replace the single midpoint-label block in `SewerBranchAlignmentCommands.cs` with calls to this helper. The helper was added first so the recovered geometry, scale and rotation rules are preserved independently while the larger V50–V60 source reconciliation continues.

No file on `main` has been overwritten. Work remains isolated in draft PR #48.
