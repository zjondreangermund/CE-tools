# Junction and water workflow acceptance

Run in Civil 3D on a copy of the drawing. Autodesk runtime validation is required in addition to repository CI.

- Grade two separated curved junction edges with the combined surface option. Expect one `CE-JUNCTION-GRADE-ALL` surface, with no triangles between junctions or beyond the toe boundaries. Compare the visible limits at concave corners and curved bellmouths.
- Select no grading with closed feature lines and no existing surface. Expect bounded surface/infill creation without new grading objects or daylight lines. Open lines without toes must report that they cannot bound a surface.
- Select away from all alignments. On an unambiguous outside edge every initial ray must increase its distance from every alignment. Between conflicting alignments, expect a safe skip and choose an explicit side.
- Run `CE_REBUILDALLSURFACES`; editable surfaces rebuild and referenced/unavailable surfaces are reported as skipped.
- Run `CE_WATERNETWORKMULTI` with multiple polylines sharing a vertex, plus a feature line with intermediate elevation points. Choose installed water pipe/structure parts and water styles. Expect one network and one shared node per coincident source vertex; intermediate elevation points and crossings without vertices must not gain nodes.
- Repeat the water command with skip completed; expect no duplicate network. Cancel either setup dialog; expect no drawing edits.

The water command uses the same Civil 3D pipe/structure network representation as the sewer command. It does not create a pressure network. Water parts lists and both styles must be named for water/potable use; stormwater, wastewater and sewer lists/styles are excluded. Existing pressure-network workflows remain available separately.
