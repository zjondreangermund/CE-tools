# CE_SEWSEQ validation checklist

## Entire-network mode

- Run `CE_SEWSEQ` and accept the default `EntireNetwork` mode.
- Select one pipe or structure from a complete gravity network and confirm CE Tools expands the selection to the entire Civil 3D network.
- Preselect a pipe or structure before starting the command and confirm implied selection works.
- Select parts from two different networks and confirm both complete networks appear in the preview.
- Confirm the preview shows each network, structure count, pipe count, branch count, branch length, highest rim and lowest rim.
- Confirm the longest route starting at the highest-rim structure becomes `Branch-1`.
- Confirm the remaining branches are ordered from longest to shortest.
- Confirm each branch is oriented from its higher endpoint rim toward its lower endpoint rim.
- Confirm structure names use `MH1.1`, `MH1.2`, ... for Branch-1 and `MH2.1`, `MH2.2`, ... for Branch-2.
- Confirm pipe names use `P1.1`, `P1.2`, ... for Branch-1 and `P2.1`, `P2.2`, ... for Branch-2.
- Confirm a junction shared by multiple paths keeps the name of the first/longest branch that claimed it.
- Confirm all structures and all connected pipes receive exactly one final name.
- Confirm each part description contains its `Branch-n` assignment.
- Press Enter or choose No at the confirmation prompt and confirm no names change.
- Choose Yes and confirm the complete rename is one undoable transaction.
- Test a disconnected structure inside the same Civil network and confirm it is assigned a final branch/manhole name.
- Test a pipe with an unconnected end and confirm the command stops before changing anything.
- Test a referenced network and confirm it is rejected without changes.
- Test a looped or parallel-path network and confirm the command reports that whole-network mode currently requires a tree-shaped gravity network.

## Selected-path compatibility mode

- Run `CE_SEWSEQ`, choose `SelectedPath`, and select start and end structures in one simple gravity network.
- Confirm the existing shortest-path workflow still works.
- Confirm structures become `MH1`, `MH2`, ... from the selected start to end.
- Confirm pipes become `P1`, `P2`, ... from the selected start to end.
- Select the same path in reverse and confirm the numbering direction reverses.
- Select structures from different networks and confirm no changes are made.
- Select two disconnected structures and confirm no changes are made.
- Test a network with an alternate route/loop and confirm selected-path mode still follows the shortest connected path.
- Confirm one UNDO reverses the complete selected-path rename operation.
