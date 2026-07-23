# Sewer branch alignment release notes

Added `CE_SEWALIGN` as a new subcommand inside the existing Pipe Network Tools module.

The command:

- detects branches created by `CE_SEWSEQ`;
- creates one siteless Civil 3D alignment for every branch;
- follows pipe geometry from the highest rim/cover endpoint toward the lowest;
- samples curved pipes for the plan path;
- displays the branch name in model space;
- refreshes only CE-generated branch alignments and labels when rerun;
- avoids overwriting unrelated alignments with the same preferred name;
- uses one drawing transaction and defaults confirmation to `No`.

Ribbon location:

`CE Tools > Utilities > Pipe Network Tools > Create / Refresh Branch Alignments`

Civil 3D 2023 compilation and live drawing validation remain required before installation.
