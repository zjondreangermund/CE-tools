# CE_SEWALIGN implementation details

The sewer branch alignment utility is implemented as a new subcommand inside the existing Pipe Network Tools module. It does not duplicate the existing sequencing logic.

Source:

- `src/CE.Tools.Civil3D/SewerBranchAlignmentCommands.cs`

Behaviour:

- Reads CE branch identifiers from names such as `P1.1` and descriptions such as `Branch-1`.
- Expands selected pipes or structures to their complete gravity networks.
- Reconstructs each branch as a continuous path.
- Starts each branch at the endpoint with the higher structure rim elevation.
- Samples curved pipe geometry and uses straight pipe geometry directly.
- Creates a siteless alignment using the first available alignment style and alignment label-set style in the drawing.
- Uses layer `CE-SEWER-ALIGNMENT`.
- Places a midpoint MText label displaying the branch name.
- Tags generated alignments and labels with CE Tools XData.
- Refreshes tagged objects on rerun while protecting unrelated user-created objects.
- Uses a default-No confirmation and one transaction.

Public commands:

- `CE_SEWSEQ` — sequence and name the network branches.
- `CE_SEWALIGN` — create or refresh the branch alignments and plan labels.

Ribbon:

- `CE Tools > Utilities > Pipe Network Tools > Sewer Network Sequencing`
- `CE Tools > Utilities > Pipe Network Tools > Create / Refresh Branch Alignments`
