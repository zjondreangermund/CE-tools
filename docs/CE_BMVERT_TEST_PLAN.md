# CE_BMVERT v0.1 Civil 3D Test Plan

Run these tests on a copy of a drawing before production use.

## 1. Straight open polyline

1. Draw a 10.000-unit straight `LWPOLYLINE`.
2. Run `CE_BMVERT`.
3. Choose **Number** and enter `10`.
4. Confirm nine new interior vertices at 1-unit chainages.
5. Confirm the original remains one polyline.
6. Run `UNDO` once and confirm the complete operation is reversed.

## 2. Single arc segment

1. Draw an arc polyline with a known length.
2. Run **Number = 4**.
3. Confirm three new vertices were inserted.
4. Confirm every resulting portion remains an arc, not a chord approximation.
5. Confirm the total polyline length is unchanged within drawing precision.

## 3. Mixed line-and-arc bellmouth

1. Draw a tangent-arc-tangent bellmouth polyline.
2. Record its total length and original tangent points.
3. Run **Maximum = 0.5** drawing units.
4. Confirm the generated equal spacing is not greater than 0.5.
5. Confirm tangent points and curve geometry remain unchanged.
6. Confirm no visible kink was introduced at inserted arc vertices.

## 4. Multiple polylines

1. Select at least 20 line-and-arc polylines of different lengths.
2. Run **Maximum = 0.5**.
3. Confirm every object remains a single polyline.
4. Confirm short polylines are handled without an error.
5. Review the command-line totals.

## 5. Closed polyline

1. Draw a closed line-and-arc polyline.
2. Run **Number = 12**.
3. Confirm the closing arc or line segment is handled correctly.
4. Confirm the polyline remains closed.

## 6. Widths and entity properties

Test a polyline with:

- constant width;
- variable start and end widths;
- non-default layer, colour, linetype, elevation and normal;
- attached XData where available.

Confirm the same entity retains its properties and widths remain continuous at inserted vertices.

## 7. Existing station vertex

Create a polyline where a requested equal-chainage station already coincides with an original vertex. Confirm CE_BMVERT does not add a duplicate vertex at that chainage.

## 8. Production-scale selection

Use a copied project drawing containing approximately 100 intersections. Record:

- selected polyline count;
- inserted vertex count;
- run time;
- any skipped object handles;
- before and after DWG file size;
- visual QA outcome.

## Acceptance criteria for beta promotion

- No geometry movement beyond normal database precision.
- No arc-to-chord conversion.
- No object replacement or loss of entity properties.
- Closed polylines remain closed.
- One command-level undo restores the drawing.
- No failures in the copied 100-intersection project test.
