# CE Tools Master Items Phase 2 runtime test plan

Run only after the exact Phase 2 branch head compiles in Civil 3D 2023. Repeat in Civil 3D 2024 before merging.

## Safety

- Use disposable DWG copies.
- Keep a backup of the installed CE Tools bundle.
- Do not use grading guides, quantity templates or generated section notes as final issue information without engineer review.
- Keep `CE_PARKAUTOMONITOR` off during bulk test-data setup when automatic refresh is not desired.

## 1. Ribbon and startup

Confirm these command groups appear and execute:

- Dynamic parking and grading: `CE_PARKAUTOMONITOR`, `CE_PARKAUTOREFRESHALL`, `CE_PARKAUTOSTATUS`, `CE_PARKGRADETOOLS`, `CE_PARKGRADECREATE`, `CE_PARKGRADEREFRESH`, `CE_PARKGRADEINFO`, `CE_PARKGRADECLEAR`.
- Standard quantities: parking/driveway and sidewalk create, refresh, information and export commands shown in Quantity & BOQ Tools.
- Profile views: batch tools, apply, report and cleanup commands shown in Profile Tools.
- Detailed sections: `CE_SECTIONDETAILTOOLS`, `CE_SECTIONDETAILCREATE`, `CE_SECTIONDETAILREFRESH`, `CE_SECTIONDETAILINFO`, `CE_SECTIONDETAILCLEAR`.

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
5. Independently check several boundary-vertex elevations using `Z = low-point Z + plan distance × 0.02`.
6. Confirm guide objects are 3D polylines on `CE-PARK-GRADING-GUIDE`.
7. Confirm the source boundary and parking bays are unchanged.
8. Pick an outside low point and verify the command stops without creating geometry.

## 5. Crown and valley grading guides

1. Run `CE_PARKGRADECREATE`, choose Crown and 2%.
2. Treat the entered reference elevation as the edge level.
3. Verify each guide centre is higher than both edge points and the centre-to-edge grade equals 2% within tolerance.
4. Repeat using Valley; treat the reference elevation as the centre-valley level.
5. Verify each centre is lower than both edges and edge-to-centre grade equals 2% within tolerance.
6. Confirm concave boundary intersections create only guide segments lying inside the boundary.
7. Test several guide spacings.

## 6. Linked grading refresh

1. Create grading guides and run `CE_PARKGRADEINFO` from both the boundary and one guide.
2. Grip-edit the boundary with automatic monitoring On.
3. Confirm grading guides and linked parking bays both refresh after the command ends.
4. Confirm no duplicate guides remain.
5. Change no source geometry and run `CE_PARKGRADEREFRESH`; verify deterministic replacement.
6. Run `CE_PARKGRADECLEAR`; confirm only grading guides are removed.
7. Confirm parking bays and source boundary remain.

## 7. Standards-based quantity templates

Use simple closed boundaries and linear objects with independently known areas and lengths.

1. Create a parking/driveway template schedule.
2. Verify every paving, bedding, G5, G6, roadbed, kerb/channel, marking, sign and allowance row against hand calculations.
3. Change one source boundary and run the linked refresh command.
4. Confirm matching rates or user-entered values are preserved where the workflow promises preservation.
5. Create a sidewalk template and repeat area, bedding, subbase and edge-restraint checks.
6. Export each schedule to XLSX and open it without an Excel repair warning.
7. Test deleted, locked and XREF-dependent source objects; valid rows must remain and missing items must be reported.

## 8. Batch profile-view cleanup

Create at least three Civil 3D profile views with different styles, band sets and manually constrained elevation ranges.

1. Run the batch report and compare names, alignments, station ranges, elevation ranges, styles and band-set status.
2. Apply one known profile-view style to all selected views.
3. Apply one known band set to all selected views.
4. Run automatic fit and verify the station/elevation extents encompass displayed profiles without excessive blank space.
5. Run rebuild/update and confirm Civil 3D regenerates the selected views.
6. Test the optional overlap-cleanup handoff.
7. Verify unsupported API operations are reported explicitly and do not appear as successful changes.
8. Repeat all tests in both Civil 3D 2023 and 2024 because API members may differ.

## 9. Detailed-section annotation — road and parking

Create disposable road and parking section geometry using lines, closed polylines and arcs.

1. Run `CE_SECTIONDETAILCREATE` and select Road.
2. Confirm the overall width and height dimensions match independent measurements.
3. Confirm the title, discipline note and component register are created on `CE-SECTION-DETAIL-ANNO`.
4. Verify every register handle resolves to the intended source object.
5. Repeat using Parking and confirm the parking-specific verification note.
6. Confirm selected source geometry, layers and colours are unchanged.
7. Grip-edit source geometry, run `CE_SECTIONDETAILREFRESH` and verify dimensions/register values update without duplicates.

## 10. Detailed-section annotation — utilities

Create stormwater, sewer and water trench sections containing circular pipe geometry and trench/layer lines.

1. Run the workflow separately for Stormwater, Sewer and Water.
2. Verify circular elements receive the correct discipline label and measured diameter.
3. Confirm overall dimensions and component measurements independently.
4. Delete one linked source, run refresh and verify the missing item is reported while live-source annotation is rebuilt.
5. Run `CE_SECTIONDETAILINFO` and compare live/missing source counts.
6. Save, close and reopen the DWG; verify XData links survive.
7. Run `CE_SECTIONDETAILCLEAR` and confirm only generated dimensions, labels, notes and the register are erased.
8. Test UNDO/REDO around create, refresh and clear.

## 11. Save, reopen and manual rebuild

1. Create linked parking bays, grading guides, quantity schedules and detailed-section annotations.
2. Save, close and reopen the DWG.
3. Run information/status commands for each link type.
4. Run explicit refresh commands and compare all regenerated values.
5. Confirm no source geometry is erased by information or clear commands beyond the specifically confirmed generated objects.

## Failure evidence

For each failure record:

1. exact commit and Civil 3D version;
2. command name;
3. complete command-line text;
4. screenshot of the drawing and command line;
5. source object types, layers, locked/reference state and missing-handle state;
6. whether automatic monitoring was On or Off where relevant;
7. whether failure occurred during preview, confirmation, transaction commit, idle refresh or manual refresh.
