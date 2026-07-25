# CE Tools Master Items Phase 2 runtime test plan

Run only after the exact Phase 2 branch head compiles in Civil 3D 2023. Repeat in Civil 3D 2024 before merging.

## Safety

- Use disposable DWG copies.
- Keep a backup of the installed CE Tools bundle.
- Do not use the grading guides as final construction levels without engineer review.
- Keep `CE_PARKAUTOMONITOR` off during bulk test-data setup when automatic refresh is not desired.

## 1. Ribbon and startup

Confirm these commands appear and execute:

- `CE_PARKAUTOMONITOR`
- `CE_PARKAUTOREFRESHALL`
- `CE_PARKAUTOSTATUS`
- `CE_PARKGRADETOOLS`
- `CE_PARKGRADECREATE`
- `CE_PARKGRADEREFRESH`
- `CE_PARKGRADEINFO`
- `CE_PARKGRADECLEAR`

Close and reopen Civil 3D and confirm the parking monitor starts cleanly without duplicate Idle or database-event subscriptions.

## 2. Automatic parking option refresh

1. Create a linked option with `CE_PARKOPTIONS`.
2. Run `CE_PARKAUTOMONITOR`, choose On, and verify the boundary appears in status.
3. Grip-edit one boundary corner and finish the AutoCAD command.
4. Confirm linked bays refresh only after the grip command ends.
5. Confirm old bays are erased and no duplicates remain.
6. Verify every regenerated bay remains a closed polyline accepted by `CE_PKCOUNTX`.
7. Repeat with a concave boundary and a boundary containing arc segments.
8. Undo and redo the boundary edit; verify the linked layout follows both states.
9. Turn the monitor Off, edit the boundary and confirm no automatic refresh occurs.
10. Run `CE_PARKAUTOREFRESHALL` and confirm the deferred layout updates.

## 3. Monitor state and failure handling

1. Create two independent linked parking boundaries.
2. Modify both before Civil 3D becomes idle; verify both are refreshed.
3. Erase one source boundary and check `CE_PARKAUTOSTATUS` for a clear failure message.
4. Confirm a missing boundary does not prevent the remaining live boundary from refreshing.
5. Save, close and reopen; confirm linked handles are rediscovered from bay XData.

## 4. Low-point grading guides

1. Create a closed parking boundary with a known coordinate system.
2. Run `CE_PARKGRADECREATE`, choose LowPoint and 2%.
3. Enter a known reference elevation and pick a low point inside the boundary.
4. Verify the low point receives the reference elevation.
5. Independently check several boundary-vertex elevations using:
   `Z = low-point Z + plan distance × 0.02`.
6. Confirm guide objects are 3D polylines on `CE-PARK-GRADING-GUIDE`.
7. Confirm the source boundary and parking bays are unchanged.
8. Pick an outside low point and verify the command stops without creating geometry.

## 5. Crown grading guides

1. Run `CE_PARKGRADECREATE`, choose Crown and 2%.
2. Treat the entered reference elevation as the edge level.
3. Verify each guide centre is higher than both edge points.
4. Check that centre-to-edge grade equals the entered slope within tolerance.
5. Test different guide spacings and confirm the number of guides changes without changing the boundary.

## 6. Valley grading guides

1. Run `CE_PARKGRADECREATE`, choose Valley and 2%.
2. Treat the entered reference elevation as the centre-valley level.
3. Verify each guide centre is lower than both edge points.
4. Check edge-to-centre grade equals the entered slope within tolerance.
5. Confirm concave boundary intersections create only guide segments lying inside the boundary.

## 7. Linked grading refresh

1. Create grading guides and run `CE_PARKGRADEINFO` from both the boundary and one guide.
2. Grip-edit the boundary with automatic monitoring On.
3. Confirm grading guides and linked parking bays both refresh after the command ends.
4. Confirm no duplicate guides remain.
5. Change no source geometry and run `CE_PARKGRADEREFRESH`; verify deterministic replacement.
6. Run `CE_PARKGRADECLEAR`; confirm only grading guides are removed.
7. Confirm parking bays and source boundary remain.

## 8. Save, reopen and manual rebuild

1. Create linked parking bays and grading guides.
2. Save, close and reopen the DWG.
3. Run `CE_PARKAUTOSTATUS`; confirm both link types are counted.
4. Run `CE_PARKAUTOREFRESHALL` and compare all regenerated values.
5. Test UNDO/REDO around create, refresh and clear actions.

## Failure evidence

For each failure record:

1. exact commit and Civil 3D version;
2. command name;
3. complete command-line text;
4. screenshot of boundary, bays/guides and command line;
5. source boundary type, vertex count, arc presence and locked/reference state;
6. whether automatic monitoring was On or Off;
7. whether failure occurred during the source edit, idle refresh or manual refresh.
