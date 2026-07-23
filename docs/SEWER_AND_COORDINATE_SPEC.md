# Sewer sequence and coordinate suite specification

## CE_SEWSEQ

The first release uses the minimum-click workflow requested by the lead user:

1. Select the start structure/manhole.
2. Select the end structure/manhole.
3. CE Tools traces the connected pipe-network path between them.
4. The pipe network is automatically assigned the next available branch name: `Branch-1`, `Branch-2`, and so on.
5. Structures on the selected path are renamed `MH1`, `MH2`, `MH3`, ... in start-to-end order.
6. Pipes on the selected path are renamed `P1`, `P2`, `P3`, ... in start-to-end order.

No manual selection of every intermediate pipe or manhole is required.

The command must stop without making changes when:

- the selected objects are not Civil 3D gravity-network structures;
- the two structures belong to different networks;
- no connected path exists;
- a target name conflicts with an unselected part in the same network.

## CE_COORDINATE

`CE_COORDINATE` opens one command-line menu containing all first-release coordinate workflows:

- **Pick** — pick a point and place an XYZ MLeader.
- **Cogo** — batch-label selected Civil 3D COGO points.
- **Cross** — place a coordinate cross and XYZ label.
- **Table** — create a setting-out table from selected AutoCAD points and/or Civil 3D COGO points.

Coordinates are reported in drawing/WCS coordinates. The first release uses drawing units and the current drawing text/table defaults.