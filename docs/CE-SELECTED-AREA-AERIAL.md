# CE_AERIALAREA — pick aerial imagery directly in Civil 3D

## Workflow

1. Load the updated CE Tools DLL, switch to **Model** space, and type `CE_AERIALAREA` or choose **CE-Aerial Imagery from Selected Area** in Road / Geometry Production.
2. In the dark-mode settings dialog, enter a licensed **OGC WMS 1.1.1** aerial orthophoto service **HTTPS GetMap endpoint**, its exact **layer identifier**, and the **projected EPSG code** shared by the service and the current Civil 3D drawing. These service settings are preserved for subsequent runs.
3. Confirm that your service provider permits **offline JPEG downloads and CAD storage/use**. Google Earth and Google Maps are **not an interchangeable aerial tile download service** and are excluded from this command.
4. Choose requested pixel size (0.10, 0.25, 0.50 or 1 m/pixel), tile width (1024/2048), and whether to insert or just save.
5. Click two opposite corners of the desired area directly in model space. Picked points are converted from UCS to WCS and resolved into the requested rectangular footprint.
6. Pick a **permanent** output folder. CE Tools requests adjacent JPEG WMS tiles, saves a `.jgw` world file per tile, and optionally attaches them automatically to model space on `CE-AERIAL-IMAGERY`.
7. Check known surveyed control points and the provider's image capture date and attribution before using imagery as a design background.

## Geospatial and licensing limits

- The DWG and WMS must **already share the same projected coordinate reference system and drawing units**. This command does not transform data between EPSG systems and does not detect the DWG's EPSG by itself.
- The raster resolution is **requested output resolution**, not a guarantee of source imagery ground sampling distance. Unavailable, low-resolution, white, or cloudy imagery remains a provider/coverage issue.
- Only an authorized, externally supplied WMS aerial imagery service is supported. This command does not scrape Google Earth, remove watermarks, or fetch Google Maps static imagery for storage.
- JPEG + JGW does not store EPSG metadata inside the files; keep a separate record of the confirmed EPSG and imagery provider.
- WMS 1.1.1 uses `SRS=EPSG:<code>` and `BBOX=minx,miny,maxx,maxy`. It returns server-rendered JPEG images. The image is not a surveyed plan; geoaccuracy depends on orthorectification and the source imagery.
- The requested region is divided into at most 64 contiguous tiles (maximum 130 million output pixels), 1024/2048 pixels per tile. Large areas must be selected in smaller patches or use a coarser pixel size.
- Output files are external references and must not be moved or deleted after importing into the DWG.
- You need a service URL and imagery layer that are actually available in your chosen area. Merely installing this command cannot make detailed aerial imagery available everywhere.

## Comparison with existing commands

- `CE_AERIALAREA` — pick rectangle and request external licensed WMS aerial imagery **directly from Civil 3D**, save JPEG/JGW, insert.
- `CE_GEOIMAGETILES` — split already obtained georeferenced JPEGs into smaller tiles.
- `CE_GEOIMAGEINSERT` — insert existing JPEG/JGW orthophoto tiles.
- `CE_GEOIMAGECALIBRATE` — calibrate three known image corners, only for rectifiable imagery.

## Native testing

GitHub checks validate command and geometry source, not AutoCAD SDK behavior. Compile and install the DLL using the existing Civil 3D 2024 safe installer, then test: model-space selection, UCS/WCS transformation, WMS service permissions, pixel image size, adjacent tile alignment, raster image attachments, and an independent known survey control point.
