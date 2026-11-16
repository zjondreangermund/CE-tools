# Horizontal curves: Constrained on Both Sides (Free)

Command: `CE_ALIGNCURVESFREE`
Ribbon: CE Tools > Geometry > Road Tools > Horizontal Curves to Free.

1. Choose **All** for the current drawing, or **Selected** to pick multiple alignments (preselection is supported).
2. Read the eligible/already-Free/skipped summary and individual skipped reasons in the command history.
3. Choose **Yes** to apply. **No**, Enter at confirmation, or Escape makes no changes.

The command converts standalone fixed/floating circular arcs between two fixed straight alignment entities. Already-Free arcs remain unchanged. Reference alignments, terminal/disconnected arcs, spiral/compound groups, arcs adjacent to curves, and arcs with dependent neighbouring lines are skipped and reported. These require individual review in Alignment Layout Tools.

Each conversion retains the original radius, turn direction and major/minor arc solution. The alignment itself is retained. Civil 3D solves the tangent points, so endpoints and downstream chainages can change. The command verifies connectivity, tangency, line directions and the outer line endpoints. A failed conversion rolls back that curve and its neighbouring changes; other valid curves can still be converted.

The managed API exposes `Constraint1` as read-only. The command therefore removes only the eligible arc and recreates it with `AddFreeCurve`, rather than assigning the UI dropdown. Curve subentity IDs change; check curve-specific labels, design criteria, station equations, profiles and corridors afterwards. It does not rebuild corridors automatically.

## Validation

- `dotnet run --project tests/CE.Tools.AlignmentFreeCurve.Tests`: compiles the actual command against API-contract doubles and tests selected/all scope, reference exclusions, cancellation, successful conversion, rejection of wrong radius/direction/tangency/connectivity, transaction rollback and ineligible neighbours.
- Repository C# syntax/shared-alias/modal checks.
- Native Civil 3D testing is still required; doubles do not reproduce the native solver.

### Civil 3D acceptance checks

Use a copy of the drawing. Include fixed three-point and floating curves between fixed lines, clockwise/counterclockwise turns and curves already Free. Verify the Alignment Entities vista reports **Constrained on Both Sides (Free)** and retains each radius. Repeat the command to check already-Free counts. Confirm an unselected alignment stays unchanged; test All with a reference alignment. Include an impossible radius and verify that its original arc and line geometry remain intact. Check labels and downstream stations, and test UNDO after a batch.

## Autodesk API references

- Constraint1 (read-only): https://help.autodesk.com/cloudhelp/2026/ENU/Civil3D-API/files/html/b7a3d721-b58d-7fdf-aed1-a594a9e79fc4.htm
- AddFreeCurve (radius overload): https://help.autodesk.com/cloudhelp/2026/ENU/Civil3D-API/files/html/979457ec-e15a-a89e-ca88-2e4778b464ba.htm
- Remove (rejects dependent entities): https://help.autodesk.com/cloudhelp/2026/ENU/Civil3D-API/files/html/61423a9c-4c6b-a2fe-37a2-a6f10c2743a3.htm
