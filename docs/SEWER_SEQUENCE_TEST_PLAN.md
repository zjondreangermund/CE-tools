# CE_SEWSEQ validation checklist

- Select start and end structures in one simple gravity network.
- Confirm the network becomes `Branch-1` when no branch names exist.
- Confirm structures become `MH1`, `MH2`, ... from start to end.
- Confirm pipes become `P1`, `P2`, ... from start to end.
- Run on a second unnamed network and confirm it becomes `Branch-2`.
- Select the same path in reverse and confirm the numbering direction reverses.
- Select structures from different networks and confirm no changes are made.
- Select two disconnected structures and confirm no changes are made.
- Test a network with an alternate route/loop; confirm CE Tools reports the selected shortest connected path.
- Confirm one UNDO reverses the complete rename operation.
