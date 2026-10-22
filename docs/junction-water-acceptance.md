# Junction and water workflow acceptance

Run in Civil 3D on a copy of the drawing. Autodesk runtime validation is required in addition to repository CI.

- Grade two separated curved junction edges with the combined surface option. Expect one `CE-JUNCTION-GRADE-ALL` surface, with no triangles between junctions or beyond the toe boundaries. Compare the visible limits at concave corners and curved bellmouths.
- Select no grading with closed feature lines and no existing surface. Expect bounded surface/infill creation without new grading objects or daylight lines. Open lines without toes must report that they cannot bound a surface.
- Select away from all alignments. On an unambiguous outside edge every initial ray must increase its distance from every alignment. Between conflicting alignments, expect a safe skip and choose an explicit side.
- Run `CE_REBUILDALLSURFACES`; editable surfaces rebuild and referenced/unavailable surfaces are reported as skipped.
- Run `CE_WATERNETWORKMULTI` with multiple polylines sharing a vertex, plus a feature line with intermediate elevation points. Choose installed water pipe/structure parts and water styles. Expect one network and one shared node per coincident source vertex; intermediate elevation points and crossings without vertices must not gain nodes.
- Repeat the water command with skip completed; expect no duplicate network. Cancel either setup dialog; expect no drawing edits.

The water command uses the same Civil 3D pipe/structure network representation as the sewer command. It does not create a pressure network. Water parts lists and both styles must be named for water/potable use; stormwater, wastewater and sewer lists/styles are excluded. Existing pressure-network workflows remain available separately.

## Surface style and infill follow-up

- The reported 52-source case selected open feature lines with no linked toes. Such strings cannot bound a TIN strip, and the native infill selector accepts closed feature lines only. Expect an explicit boundary-required popup and zero created outputs, rather than interpreting zero counts as success. Join/close the intended outline before infilling; this command does not invent closures across open gaps.
- Choose an installed Surface style in the grade-to-surface or standalone infill popup. Verify the chosen style on combined, per-group and bounded fallback surfaces. Verify the target surface style is unchanged. Default/keep existing must preserve the style of a replaced named connected surface.
- With no existing target surface, choose surfaces/infill only and a closed outline. Verify output without new grading. Grade-to-surface still requires a valid target.
- Where the host exposes instance CreateInfill, verify the infill remains after command completion and save/reopen; its transaction must commit before reporting success.
- Where only the bounded TIN fallback works, rerun native infill on the same closed feature line. Expect the existing surface to be reused and its newly selected style applied, rather than another suffixed surface.

Runtime verification is still required in Civil 3D; screenshots alone cannot establish that an infill object persisted or triangulated correctly.
