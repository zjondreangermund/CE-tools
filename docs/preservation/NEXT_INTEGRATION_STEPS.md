# Next Integration Steps

1. Wire `SewerBranchLabelPlacement` into `SewerBranchAlignmentCommands` without replacing newer branch sequencing logic.
2. Restore the V54 automatic refresh hook only after checking current event subscriptions and undo behaviour.
3. Compare V50/V52/V54 command registries and add only genuinely missing commands.
4. Reconcile assets separately from source: DWG, PDF, XLSX, images and standards files.
5. Run the preservation workflow and command registry checks.
6. Compile on Civil 3D 2023 with the exact PR head.
7. Keep PR #48 in draft until branch labels, profiles, BOQs, parking, survey utilities and client books pass regression testing.
