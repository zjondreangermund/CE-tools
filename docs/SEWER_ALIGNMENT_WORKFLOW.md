# Sewer branch alignment workflow

`CE_SEWALIGN` extends the existing sewer-network tools without replacing or duplicating `CE_SEWSEQ`.

Recommended workflow:

1. Create or edit the Civil 3D gravity pipe network.
2. Run `CE_SEWSEQ` in `EntireNetwork` mode.
3. Review and approve the branch numbering preview.
4. Run `CE_SEWALIGN` from `CE Tools > Utilities > Pipe Network Tools`.
5. Select one pipe or structure from each required network.
6. Review and approve the branch-alignment preview.

Results:

- one siteless alignment for every CE-sequenced branch;
- alignment direction from the higher cover/rim endpoint to the lower cover/rim endpoint;
- alignment geometry follows straight pipes and sampled curved pipes;
- visible `Branch-1`, `Branch-2`, etc. plan labels;
- generated objects placed on `CE-SEWER-ALIGNMENT`;
- rerunning `CE_SEWALIGN` refreshes CE-generated branch alignments and labels instead of duplicating them;
- unrelated user-created alignments and text are not intentionally replaced.

The first release keeps sequencing and alignment generation as two controlled confirmations so the user can inspect the branch assignment before Civil 3D alignments are created. A later validated workflow may combine both actions behind one wizard while keeping the same commands available separately.
