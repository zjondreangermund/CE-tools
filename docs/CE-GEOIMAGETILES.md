# CE Tools – Georeferenced JPEG tile workflow

## Commands

| Command | Purpose |
| --- | --- |
| `CE_GEOIMAGETILES` | Split a licensed, already-georeferenced local JPEG into high-quality JPEG tiles (1024/2048/4096 px; quality 90/95/100), write a `.jgw` for each tile, and attach the resulting tiles to the current drawing. Can also insert a selection of existing georeferenced JPEG tiles. |
| `CE_GEOIMAGEINSERT` | Insert multiple existing JPEGs accompanied by `.jgw` or `.jpgw` world files without splitting. |
| `CE_GEOIMAGECALIBRATE` | Create a JPEG world file by picking the actual outer upper-left, upper-right and lower-left image corners in the drawing (WCS control-point locations). Inserts the calibrated JPEG. |

Open **CE Road Production** or **CE Road Layout Production** for these commands. They also work when typed directly in Civil 3D.

### Important: imagery licensing

This tool **does not download tiles from Google Earth, Google Maps or any imagery service**. Google generally restricts automated tile extraction, caching and offline use; Google Earth content also has attribution and use restrictions. Do not attempt automated screenshot tiling or reuse Google imagery commercially unless separately authorized, and do not strip copyright/attribution notices. Work with orthophotography from a provider permitting local CAD use, or images you hold rights to process. Verify each provider's license before conversion.

### Preparing an accurate source raster

1. Set the DWG coordinate system and confirm whether the drawing uses projected metres (e.g. the appropriate zone/datum) rather than longitude/latitude degrees.
2. In an orthophoto/GIS workflow, reproject the **image pixels** into the same projected coordinate system as the DWG **before** you create or use the world file. A world file does *not* perform CRS transformations.
3. Supply `site.jpg` alongside `site.jgw`. The six lines are **A, D, B, E, C, F** (pixel centre coordinate convention). A typical north-up image has positive A and negative E, with B and D zero.
4. For a single ordinary JPG without a world file, use `CE_GEOIMAGECALIBRATE` only if three *actual image corners* have accurately known, perpendicular drawing positions. Prefer survey control/GIS orthorectification; ground imagery captured obliquely is not made accurate by merely placing its image corners.
5. Run `CE_GEOIMAGETILES`, confirm identical projected CRS and drawing units, select the authorized source image, choose tile size and JPEG quality. Each cropped tile receives its pixel-correct `.jgw`; no automatic stitching or fake enlargement occurs.
6. Images are attached externally as AutoCAD `RasterImage` entities on the `CE-AERIAL-IMAGERY` layer. Preserve their disk folder and world files. Raster entities link to the JPEGs; the JPEG bytes do not become part of the DWG.
7. Confirm positional accuracy at independent surveyed control points, along the image perimeter and across several tiles. Never infer survey-grade positional accuracy from sharp-looking pixels.

### Pixel-to-drawing placement

A world file maps the **centre** of source pixel `(column,row)` as

`X=A*column+B*row+C; Y=D*column+E*row+F`.

For a crop starting at `(col0,row0)`, the new world file retains A,B,D,E and sets
`C'=C+A*col0+B*row0`; `F'=F+D*col0+E*row0`.

The insert computes the **outer lower-left corner** for a JPEG of width W and height H as

`(C-0.5*A-0.5*B+H*B, F-0.5*D-0.5*E+H*E)`,

with image U axis `(A*W,D*W)` and V axis `(-B*H,-E*H)`. This respects scale and rotation. Sheared grids are explicitly rejected until warped in GIS. A `.jgw` has no EPSG/CRS tag, so the user's confirmed CRS match is necessary.

### Practical limits and quality

- Maximum 256 raster images in one insertion; maximum 180 million source pixels in the in-process JPEG split. For large projects prepare tiles in GIS outside Civil 3D and use `CE_GEOIMAGEINSERT`.
- JPEG quality 95 is a good visual/size compromise. Larger JPEG pixel dimensions do not increase the source imagery's actual spatial resolution.
- Tiling must respect the image provider's reproduction and attribution conditions.
- Before engineering use, verify coordinate axes, plotted limits, north orientation, dimensions, external references and known survey points. Correct georeferencing depends on correct source metadata; the app cannot invent geospatial accuracy.
- `CE_GEOIMAGECALIBRATE` performs a 3-corner rectangular affine fit only. It does **not** orthorectify oblique imagery or transform between coordinate reference systems.
