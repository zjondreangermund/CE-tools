# Import sheets from a saved DWG

Run `CE_IMPORTSHEETS` in the destination drawing. Choose a DWG in the file dialog, check layout names (or Select all), then click Import sheets. The source is read into a side database; no editor document is opened and its file is never saved or changed.

Paper-space entities, including viewports and title blocks, are cloned. All source model-space entities are copied once at their existing coordinates so every imported viewport can display them. Layout names and named page setups receive an Imported suffix on collisions. Imported symbol definitions are mangled to avoid overwriting destination layers, styles and blocks. Existing model geometry can also appear in imported views. This imports layout tabs, not a DST sheet-set registration.

External references, raster images, underlays, fonts, plotter files, data links and Civil data references are not embedded; their dependencies must remain available. Native/custom Civil objects depend on the host version and object enablers. An unsupported clone aborts the transaction rather than reporting a partial import as complete.

## Civil 3D acceptance checks

1. Prepare source A with two named layouts, a title block with attributes, two viewports with different scales/twists, one nonrectangular viewport clip, per-viewport frozen layers and model entities referenced by paper-space fields/dimensions. Close A.
2. Open B with a matching layout name and conflicting block/layer definitions. Import one layout from A. Verify B's existing geometry and definitions remain unchanged, the imported layout has a unique name, and the source does not open as an editor tab.
3. Verify source coordinates, viewport center/target/direction/scale/twist/lock, clipping, frozen layers, title-block attributes and plot settings. Confirm the model entities exist locally in B.
4. Repeat in a fresh destination with both layouts selected. Model entities must be copied once, not once per layout. Check paper-space field/geometry references against the copied local entities.
5. Cancel the file picker and layout picker; verify zero changes. Clear selection then Import; expect a popup and no changes.
6. Test an empty layout, a source with empty model space, long layout names, a repeated import, an unreadable DWG and a deliberately unsupported object. On failure verify no imported layouts/model or symbols remain.
7. Save, reopen and plot the destination. Confirm the source file was not modified. Recheck with a source containing Civil objects and external dependencies; verify missing dependencies are reported/understood rather than assumed embedded.

Repository CI can check command wiring and C# syntax; actual DWG cloning and display require the Civil 3D runtime.
