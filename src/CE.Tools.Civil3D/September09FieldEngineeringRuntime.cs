using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilPolylineOptions = Autodesk.Civil.DatabaseServices.PolylineOptions;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivilSurfaceSlopeLabel = Autodesk.Civil.DatabaseServices.SurfaceSlopeLabel;

namespace CETools.Civil3D
{
    /// <summary>
    /// Final September 09 field-engineering runtime.  This class deliberately owns
    /// no CommandMethod attributes; registered front doors live in the companion
    /// command class so historical staged repair scripts cannot create duplicates.
    /// </summary>
    internal static class September09FieldEngineeringRuntime
    {
        private const double Tol = 1e-7;
        private const string RoadCentreLayer = "CE-ROAD-CENTRELINE";

        // -----------------------------------------------------------------
        // Grid Setting-Out: DESIGN LEVEL - NG LEVEL
        // -----------------------------------------------------------------
        internal static void GridDifferenceActive()
        {
            Document document = Active();
            if (document == null) return;
            int changed = EnsureGridDifferenceColumns(document);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_GRIDDIFFERENCE complete. Grid Setting-Out tables updated={0}. DIFFERENCE = DESIGN LEVEL - NG LEVEL.",
                changed);
        }

        internal static int EnsureGridDifferenceColumns(Document document)
        {
            if (document == null) return 0;
            int changed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = transaction.GetObject(
                    document.Database.BlockTableId,
                    OpenMode.ForRead,
                    false) as BlockTable;
                if (blockTable == null) return 0;

                foreach (ObjectId recordId in blockTable)
                {
                    BlockTableRecord record = null;
                    try
                    {
                        record = transaction.GetObject(recordId, OpenMode.ForRead, false) as BlockTableRecord;
                    }
                    catch { }
                    if (record == null || record.IsLayout == false && record.IsAnonymous) continue;

                    foreach (ObjectId entityId in record)
                    {
                        Table table = null;
                        try
                        {
                            table = transaction.GetObject(entityId, OpenMode.ForWrite, false) as Table;
                        }
                        catch { }
                        if (table == null || !LooksLikeGridTable(table)) continue;
                        if (EnsureGridDifferenceColumn(table)) changed++;
                    }
                }
                transaction.Commit();
            }
            return changed;
        }

        internal static bool EnsureGridDifferenceColumn(Table table)
        {
            if (table == null || table.Rows.Count < 2 || table.Columns.Count < 2) return false;

            int headerRow;
            int ngColumn;
            int designColumn;
            int differenceColumn;
            if (!FindGridColumns(table, out headerRow, out ngColumn, out designColumn, out differenceColumn))
                return false;

            bool changed = false;
            if (differenceColumn < 0)
            {
                int insertAt = Math.Min(table.Columns.Count, designColumn + 1);
                double width = 18.0;
                try { width = Math.Max(1.0, table.Columns[designColumn].Width); } catch { }
                table.InsertColumns(insertAt, width, 1);
                differenceColumn = insertAt;
                if (ngColumn >= insertAt) ngColumn++;
                if (designColumn >= insertAt) designColumn++;
                changed = true;
            }

            if (!string.Equals(CellText(table, headerRow, differenceColumn), "DIFFERENCE", StringComparison.OrdinalIgnoreCase))
            {
                table.Cells[headerRow, differenceColumn].TextString = "DIFFERENCE";
                changed = true;
            }

            for (int row = headerRow + 1; row < table.Rows.Count; row++)
            {
                double ngValue;
                double designValue;
                if (!TryCellDouble(table, row, ngColumn, out ngValue) ||
                    !TryCellDouble(table, row, designColumn, out designValue))
                    continue;

                string value = (designValue - ngValue).ToString("0.000", CultureInfo.InvariantCulture);
                if (!string.Equals(CellText(table, row, differenceColumn), value, StringComparison.Ordinal))
                {
                    table.Cells[row, differenceColumn].TextString = value;
                    changed = true;
                }
            }
            return changed;
        }

        private static bool LooksLikeGridTable(Table table)
        {
            int rows = Math.Min(table.Rows.Count, 3);
            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < table.Columns.Count; col++)
                {
                    string text = CellText(table, row, col);
                    if (text.IndexOf("CE GRID SETTING-OUT", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            int header;
            int ng;
            int design;
            int difference;
            return FindGridColumns(table, out header, out ng, out design, out difference);
        }

        private static bool FindGridColumns(
            Table table,
            out int headerRow,
            out int ngColumn,
            out int designColumn,
            out int differenceColumn)
        {
            headerRow = -1;
            ngColumn = -1;
            designColumn = -1;
            differenceColumn = -1;
            int rows = Math.Min(table.Rows.Count, 5);
            for (int row = 0; row < rows; row++)
            {
                int ng = -1;
                int design = -1;
                int difference = -1;
                for (int col = 0; col < table.Columns.Count; col++)
                {
                    string text = NormalizeHeader(CellText(table, row, col));
                    if (text == "NG LEVEL") ng = col;
                    else if (text == "DESIGN LEVEL") design = col;
                    else if (text == "DIFFERENCE") difference = col;
                }
                if (ng >= 0 && design >= 0)
                {
                    headerRow = row;
                    ngColumn = ng;
                    designColumn = design;
                    differenceColumn = difference;
                    return true;
                }
            }
            return false;
        }

        private static string NormalizeHeader(string value)
        {
            return (value ?? string.Empty).Trim().Replace("\r", " ").Replace("\n", " ").ToUpperInvariant();
        }

        private static string CellText(Table table, int row, int column)
        {
            try { return table.Cells[row, column].TextString ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static bool TryCellDouble(Table table, int row, int column, out double value)
        {
            value = 0.0;
            string text = CellText(table, row, column).Trim();
            return double.TryParse(
                       text,
                       NumberStyles.Float | NumberStyles.AllowThousands,
                       CultureInfo.InvariantCulture,
                       out value) ||
                   double.TryParse(
                       text,
                       NumberStyles.Float | NumberStyles.AllowThousands,
                       CultureInfo.CurrentCulture,
                       out value);
        }

        // -----------------------------------------------------------------
        // Road reserve centre polylines from cadastral / erf boundaries
        // -----------------------------------------------------------------
        internal static void RoadReserveCentrePolylines(Document document)
        {
            if (document == null) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Road Reserve Centre Polylines",
                "Select multiple closed cadastral/erf polylines. CE detects facing, near-parallel reserve boundaries and creates finite red road-centre polylines midway between them. Source cadastral polylines are never changed or erased.");
            settings.AddPositiveDouble("MinWidth", "01 Reserve detection", "Minimum road reserve width", 4.0,
                "Ignore paired boundaries closer than this plan distance.");
            settings.AddPositiveDouble("MaxWidth", "01 Reserve detection", "Maximum road reserve width", 40.0,
                "Ignore paired boundaries farther apart than this plan distance.");
            settings.AddPositiveDouble("Angle", "01 Reserve detection", "Maximum parallel angle (degrees)", 8.0,
                "Maximum direction difference for two cadastral sides to be treated as opposite road-reserve boundaries.");
            settings.AddPositiveDouble("Overlap", "01 Reserve detection", "Minimum common edge length", 4.0,
                "Minimum projected overlap needed before a centre segment is created.");
            settings.AddPositiveDouble("Join", "02 Junctions", "Junction extension distance", 20.0,
                "Finite centre segments may extend to the nearest intersecting road-centre support line within this distance, closing T and X junctions.");
            settings.AddChoice("Facing", "02 Junctions", "Boundary pairing", "Facing parcel sides only",
                "Facing parcel sides uses polygon centroids to reject same-side/false parallel pairs. Use All parallel boundaries only for unusual cadastral geometry.",
                new[] { "Facing parcel sides only", "All parallel boundaries" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            double minWidth = settings.Double("MinWidth", 4.0);
            double maxWidth = settings.Double("MaxWidth", 40.0);
            if (maxWidth < minWidth)
            {
                double swap = maxWidth;
                maxWidth = minWidth;
                minWidth = swap;
            }
            double angle = Math.Max(0.1, Math.Min(30.0, settings.Double("Angle", 8.0)));
            double minOverlap = settings.Double("Overlap", 4.0);
            double junctionDistance = settings.Double("Join", 20.0);
            bool facingOnly = !string.Equals(settings.Text("Facing"), "All parallel boundaries", StringComparison.OrdinalIgnoreCase);

            List<ObjectId> ids = SelectClosedCadastralPolylines(document);
            if (ids.Count < 2)
            {
                document.Editor.WriteMessage("\nCE_ROADRESERVECENTRELINES requires at least two closed lightweight cadastral/erf polylines.");
                return;
            }

            List<ParcelBoundary> parcels;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                parcels = ReadParcels(transaction, ids);
            }
            if (parcels.Count < 2)
            {
                document.Editor.WriteMessage("\nNo usable closed cadastral/erf polylines were found.");
                return;
            }

            List<CentreSegment> centres = BuildRoadCentreCandidates(
                parcels,
                minWidth,
                maxWidth,
                angle,
                minOverlap,
                facingOnly);
            centres = MergeCollinearCentres(centres, angle, 0.25, 0.50);
            SnapCentreJunctions(centres, junctionDistance);
            centres = centres.Where(item => Distance(item.A, item.B) > Tol).ToList();

            if (centres.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nNo road reserves matched the current width/angle/overlap settings. Try All parallel boundaries or increase the maximum width/angle.");
                return;
            }

            int created = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId layerId = GetOrCreateRoadCentreLayer(document.Database, transaction);
                BlockTableRecord space = transaction.GetObject(
                    document.Database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (space == null) return;

                foreach (CentreSegment centre in centres)
                {
                    var polyline = new Polyline(2);
                    polyline.SetDatabaseDefaults(document.Database);
                    polyline.LayerId = layerId;
                    polyline.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                    polyline.Elevation = 0.0;
                    polyline.AddVertexAt(0, centre.A, 0.0, 0.0, 0.0);
                    polyline.AddVertexAt(1, centre.B, 0.0, 0.0, 0.0);
                    space.AppendEntity(polyline);
                    transaction.AddNewlyCreatedDBObject(polyline, true);
                    created++;
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_ROADRESERVECENTRELINES complete. Cadastral sources={0}; finite road-centre polylines created={1}; source polylines kept unchanged. Use CE_CONSTRUCTIONFILLET / CE_MULTIFILLET for specified-radius junction fillets.",
                parcels.Count,
                created);
        }

        private static List<ObjectId> SelectClosedCadastralPolylines(Document document)
        {
            var ids = new List<ObjectId>();
            PromptSelectionResult implied = document.Editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null)
                ids.AddRange(FilterClosedLightweight(document.Database, implied.Value.GetObjectIds()));

            if (ids.Count < 2)
            {
                var options = new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect MULTIPLE closed cadastral / erf LWPOLYLINE boundaries: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                };
                var filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
                });
                PromptSelectionResult selection = document.Editor.GetSelection(options, filter);
                if (selection.Status == PromptStatus.OK && selection.Value != null)
                    ids = FilterClosedLightweight(document.Database, selection.Value.GetObjectIds());
            }
            return ids.Distinct().ToList();
        }

        private static List<ObjectId> FilterClosedLightweight(Database database, IEnumerable<ObjectId> source)
        {
            var ids = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in source ?? Enumerable.Empty<ObjectId>())
                {
                    Polyline polyline = null;
                    try { polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch { }
                    if (polyline != null && polyline.Closed && polyline.NumberOfVertices >= 3)
                        ids.Add(id);
                }
            }
            return ids;
        }

        private static List<ParcelBoundary> ReadParcels(Transaction transaction, IList<ObjectId> ids)
        {
            var result = new List<ParcelBoundary>();
            for (int sourceIndex = 0; sourceIndex < ids.Count; sourceIndex++)
            {
                Polyline polyline = null;
                try { polyline = transaction.GetObject(ids[sourceIndex], OpenMode.ForRead, false) as Polyline; }
                catch { }
                if (polyline == null || !polyline.Closed || polyline.NumberOfVertices < 3) continue;

                var vertices = new List<Point2d>();
                for (int index = 0; index < polyline.NumberOfVertices; index++)
                    vertices.Add(polyline.GetPoint2dAt(index));
                Point2d centroid = PolygonCentroid(vertices);
                var edges = new List<BoundarySegment>();
                for (int index = 0; index < polyline.NumberOfVertices; index++)
                {
                    int next = (index + 1) % polyline.NumberOfVertices;
                    Point2d a = vertices[index];
                    Point2d b = vertices[next];
                    if (Distance(a, b) <= Tol) continue;
                    edges.Add(new BoundarySegment(sourceIndex, a, b, centroid));
                }
                if (edges.Count > 0)
                    result.Add(new ParcelBoundary(sourceIndex, centroid, edges));
            }
            return result;
        }

        private static List<CentreSegment> BuildRoadCentreCandidates(
            IList<ParcelBoundary> parcels,
            double minWidth,
            double maxWidth,
            double angleDegrees,
            double minOverlap,
            bool facingOnly)
        {
            var result = new List<CentreSegment>();
            double cosTolerance = Math.Cos(angleDegrees * Math.PI / 180.0);
            for (int first = 0; first < parcels.Count; first++)
            {
                for (int second = first + 1; second < parcels.Count; second++)
                {
                    ParcelBoundary aParcel = parcels[first];
                    ParcelBoundary bParcel = parcels[second];
                    foreach (BoundarySegment a in aParcel.Edges)
                    {
                        foreach (BoundarySegment b in bParcel.Edges)
                        {
                            CentreSegment centre;
                            if (TryRoadCentre(a, b, minWidth, maxWidth, minOverlap, cosTolerance, facingOnly, out centre))
                                AddUniqueCentre(result, centre);
                        }
                    }
                }
            }
            return result;
        }

        private static bool TryRoadCentre(
            BoundarySegment a,
            BoundarySegment b,
            double minWidth,
            double maxWidth,
            double minOverlap,
            double cosTolerance,
            bool facingOnly,
            out CentreSegment centre)
        {
            centre = null;
            Vector2d da = Unit(a.A, a.B);
            Vector2d db = Unit(b.A, b.B);
            double dot = da.X * db.X + da.Y * db.Y;
            if (Math.Abs(dot) < cosTolerance) return false;
            if (dot < 0.0) db = new Vector2d(-db.X, -db.Y);

            Vector2d normal = new Vector2d(-da.Y, da.X);
            Point2d ma = Mid(a.A, a.B);
            Point2d mb = Mid(b.A, b.B);
            double signedWidth = Dot(Sub(mb, ma), normal);
            double width = Math.Abs(signedWidth);
            if (width < minWidth - Tol || width > maxWidth + Tol) return false;

            Vector2d gapNormal = signedWidth >= 0.0
                ? normal
                : new Vector2d(-normal.X, -normal.Y);
            if (facingOnly)
            {
                double sideA = Dot(Sub(a.Centroid, ma), gapNormal);
                double sideB = Dot(Sub(b.Centroid, mb), gapNormal);
                if (!(sideA < -Tol && sideB > Tol)) return false;
            }

            double a0 = Dot(ToVector(a.A), da);
            double a1 = Dot(ToVector(a.B), da);
            double b0 = Dot(ToVector(b.A), da);
            double b1 = Dot(ToVector(b.B), da);
            double start = Math.Max(Math.Min(a0, a1), Math.Min(b0, b1));
            double end = Math.Min(Math.Max(a0, a1), Math.Max(b0, b1));
            if (end - start < minOverlap) return false;

            double normalA = Dot(ToVector(ma), normal);
            double normalB = Dot(ToVector(mb), normal);
            double centreNormal = (normalA + normalB) * 0.5;
            Point2d p0 = FromBasis(da, normal, start, centreNormal);
            Point2d p1 = FromBasis(da, normal, end, centreNormal);
            if (Distance(p0, p1) <= Tol) return false;

            centre = new CentreSegment(p0, p1);
            return true;
        }

        private static void AddUniqueCentre(ICollection<CentreSegment> centres, CentreSegment candidate)
        {
            foreach (CentreSegment existing in centres)
            {
                if ((Distance(existing.A, candidate.A) < 0.05 && Distance(existing.B, candidate.B) < 0.05) ||
                    (Distance(existing.A, candidate.B) < 0.05 && Distance(existing.B, candidate.A) < 0.05))
                    return;
            }
            centres.Add(candidate);
        }

        private static List<CentreSegment> MergeCollinearCentres(
            List<CentreSegment> input,
            double angleDegrees,
            double lineTolerance,
            double gapTolerance)
        {
            var items = input.Select(item => new CentreSegment(item.A, item.B)).ToList();
            double cosTolerance = Math.Cos(Math.Min(5.0, Math.Max(0.1, angleDegrees)) * Math.PI / 180.0);
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < items.Count && !changed; i++)
                {
                    for (int j = i + 1; j < items.Count; j++)
                    {
                        CentreSegment merged;
                        if (!TryMergeCentres(items[i], items[j], cosTolerance, lineTolerance, gapTolerance, out merged))
                            continue;
                        items[i] = merged;
                        items.RemoveAt(j);
                        changed = true;
                        break;
                    }
                }
            }
            return items;
        }

        private static bool TryMergeCentres(
            CentreSegment a,
            CentreSegment b,
            double cosTolerance,
            double lineTolerance,
            double gapTolerance,
            out CentreSegment merged)
        {
            merged = null;
            Vector2d direction = Unit(a.A, a.B);
            Vector2d other = Unit(b.A, b.B);
            if (Math.Abs(Dot(direction, other)) < cosTolerance) return false;
            Vector2d normal = new Vector2d(-direction.Y, direction.X);
            double na = Dot(ToVector(Mid(a.A, a.B)), normal);
            double nb = Dot(ToVector(Mid(b.A, b.B)), normal);
            if (Math.Abs(na - nb) > lineTolerance) return false;

            double a0 = Dot(ToVector(a.A), direction);
            double a1 = Dot(ToVector(a.B), direction);
            double b0 = Dot(ToVector(b.A), direction);
            double b1 = Dot(ToVector(b.B), direction);
            double amin = Math.Min(a0, a1);
            double amax = Math.Max(a0, a1);
            double bmin = Math.Min(b0, b1);
            double bmax = Math.Max(b0, b1);
            double gap = Math.Max(0.0, Math.Max(amin, bmin) - Math.Min(amax, bmax));
            if (gap > gapTolerance) return false;

            double start = Math.Min(amin, bmin);
            double end = Math.Max(amax, bmax);
            double n = (na + nb) * 0.5;
            merged = new CentreSegment(
                FromBasis(direction, normal, start, n),
                FromBasis(direction, normal, end, n));
            return true;
        }

        private static void SnapCentreJunctions(IList<CentreSegment> centres, double maximumDistance)
        {
            if (maximumDistance <= Tol) return;
            for (int i = 0; i < centres.Count; i++)
            {
                CentreSegment source = centres[i];
                Point2d bestStart = source.A;
                Point2d bestEnd = source.B;
                double startDistance = maximumDistance + Tol;
                double endDistance = maximumDistance + Tol;

                for (int j = 0; j < centres.Count; j++)
                {
                    if (i == j) continue;
                    Point2d crossing;
                    if (!TrySupportIntersection(source, centres[j], out crossing)) continue;
                    double ds = Distance(source.A, crossing);
                    double de = Distance(source.B, crossing);
                    if (ds < startDistance && ds <= maximumDistance)
                    {
                        startDistance = ds;
                        bestStart = crossing;
                    }
                    if (de < endDistance && de <= maximumDistance)
                    {
                        endDistance = de;
                        bestEnd = crossing;
                    }
                }
                if (Distance(bestStart, bestEnd) > Tol)
                {
                    source.A = bestStart;
                    source.B = bestEnd;
                }
            }
        }

        private static bool TrySupportIntersection(CentreSegment first, CentreSegment second, out Point2d intersection)
        {
            intersection = Point2d.Origin;
            Vector2d r = Sub(first.B, first.A);
            Vector2d s = Sub(second.B, second.A);
            double cross = Cross(r, s);
            if (Math.Abs(cross) <= 1e-9) return false;
            Vector2d delta = Sub(second.A, first.A);
            double t = Cross(delta, s) / cross;
            intersection = new Point2d(first.A.X + r.X * t, first.A.Y + r.Y * t);
            return IsFinite(intersection.X) && IsFinite(intersection.Y);
        }

        private static ObjectId GetOrCreateRoadCentreLayer(Database database, Transaction transaction)
        {
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers.Has(RoadCentreLayer)) return layers[RoadCentreLayer];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = RoadCentreLayer,
                Color = Color.FromColorIndex(ColorMethod.ByAci, 1)
            };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        // -----------------------------------------------------------------
        // Native Civil 3D surface slope labels + slope workflow selector
        // -----------------------------------------------------------------
        internal static void SurfaceSlopeLabels(Document document)
        {
            if (document == null) return;
            PromptEntityOptions options = new PromptEntityOptions("\nSelect Civil 3D surface for native dynamic slope labels: ");
            options.SetRejectMessage("\nSelect a Civil 3D surface.");
            options.AddAllowedClass(typeof(CivilSurface), false);
            PromptEntityResult selected = document.Editor.GetEntity(options);
            if (selected.Status != PromptStatus.OK) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Native Civil 3D Surface Slope Labels",
                "Create real Civil 3D SurfaceSlopeLabel elements attached to the selected surface. Because these are native Civil labels, the displayed slope/arrow follows the surface and its Civil 3D label style instead of CE drawing Leader/MText geometry.");
            settings.AddChoice("Placement", "01 Placement", "Placement", "Pick locations",
                "Pick locations interactively or sample the complete surface extents on a regular grid.",
                new[] { "Pick locations", "Grid sample" });
            settings.AddPositiveDouble("Spacing", "01 Placement", "Grid sample spacing", 20.0,
                "Used only for Grid sample placement.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            int created = 0;
            if (string.Equals(settings.Text("Placement"), "Grid sample", StringComparison.OrdinalIgnoreCase))
            {
                List<Point2d> points = CollectSurfaceGridPoints(
                    document.Database,
                    selected.ObjectId,
                    settings.Double("Spacing", 20.0),
                    5000);
                foreach (Point2d point in points)
                {
                    try
                    {
                        CivilSurfaceSlopeLabel.Create(selected.ObjectId, point);
                        created++;
                    }
                    catch { }
                }
            }
            else
            {
                document.Editor.WriteMessage("\nPick native slope-label locations on the selected surface. Press Enter when finished.");
                while (true)
                {
                    PromptPointOptions pointOptions = new PromptPointOptions("\nPick slope-label location <Enter to finish>: ")
                    {
                        AllowNone = true
                    };
                    PromptPointResult pointResult = document.Editor.GetPoint(pointOptions);
                    if (pointResult.Status == PromptStatus.None || pointResult.Status == PromptStatus.Cancel) break;
                    if (pointResult.Status != PromptStatus.OK) break;
                    Point3d wcs = pointResult.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
                    if (!PointIsOnSurface(document.Database, selected.ObjectId, wcs.X, wcs.Y))
                    {
                        document.Editor.WriteMessage("\nPoint is outside the usable surface extents; skipped.");
                        continue;
                    }
                    try
                    {
                        CivilSurfaceSlopeLabel.Create(selected.ObjectId, new Point2d(wcs.X, wcs.Y));
                        created++;
                    }
                    catch (System.Exception exception)
                    {
                        document.Editor.WriteMessage("\nSlope label skipped: {0}", exception.Message);
                    }
                }
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_SURFACESLOPEARROWS complete. Native Civil 3D SurfaceSlopeLabel elements created={0}. They remain surface-linked and use Civil 3D label styling.",
                created);
        }

        internal static void SlopeAnnotations(Document document)
        {
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Slope Annotation Options",
                "Choose the engineering slope relationship. Surface slopes are native Civil 3D SurfaceSlopeLabel elements. Feature-line workflows remain linked to their selected feature-line geometry.");
            settings.AddChoice("Mode", "01 Slope type", "Slope type", "Surface slope - native Civil 3D label",
                "Surface = native Civil 3D dynamic surface label; Between = crossfall between two feature lines; Along = longitudinal slope along selected feature lines.",
                new[]
                {
                    "Surface slope - native Civil 3D label",
                    "Slope between two feature lines",
                    "Slope along feature lines"
                });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            string mode = settings.Text("Mode");
            if (string.Equals(mode, "Slope between two feature lines", StringComparison.OrdinalIgnoreCase))
            {
                new August27DynamicSlopeGridHatchCommands().FeatureLineCrossfallArrows();
                return;
            }
            if (string.Equals(mode, "Slope along feature lines", StringComparison.OrdinalIgnoreCase))
            {
                August27DynamicSlopeGridHatchCommands.FeatureLineSlopeArrows(document);
                return;
            }
            SurfaceSlopeLabels(document);
        }

        private static List<Point2d> CollectSurfaceGridPoints(Database database, ObjectId surfaceId, double spacing, int maximum)
        {
            var result = new List<Point2d>();
            spacing = Math.Max(0.001, spacing);
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                if (surface == null) return result;
                Extents3d extents;
                try { extents = surface.GeometricExtents; }
                catch { return result; }

                for (double x = extents.MinPoint.X; x <= extents.MaxPoint.X + Tol && result.Count < maximum; x += spacing)
                {
                    for (double y = extents.MinPoint.Y; y <= extents.MaxPoint.Y + Tol && result.Count < maximum; y += spacing)
                    {
                        try
                        {
                            double z = surface.FindElevationAtXY(x, y);
                            if (!double.IsNaN(z) && !double.IsInfinity(z)) result.Add(new Point2d(x, y));
                        }
                        catch { }
                    }
                }
            }
            return result;
        }

        private static bool PointIsOnSurface(Database database, ObjectId surfaceId, double x, double y)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilSurface surface = null;
                try { surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface; }
                catch { }
                if (surface == null) return false;
                try
                {
                    double z = surface.FindElevationAtXY(x, y);
                    return !double.IsNaN(z) && !double.IsInfinity(z);
                }
                catch { return false; }
            }
        }

        // -----------------------------------------------------------------
        // Sewer alignment creation: named-style first, ObjectId fallback
        // -----------------------------------------------------------------
        internal static ObjectId CreateSewerAlignmentSafe(
            Database database,
            CivilDocument civilDocument,
            Transaction transaction,
            CivilPolylineOptions polylineOptions,
            string alignmentName,
            ObjectId layerId,
            ObjectId alignmentStyleId,
            string alignmentStyleName,
            ObjectId labelSetStyleId,
            string labelSetStyleName)
        {
            if (database == null || civilDocument == null || transaction == null)
                throw new ArgumentNullException("Civil 3D alignment creation context is incomplete.");
            if (polylineOptions == null || polylineOptions.PlineId.IsNull)
                throw new InvalidOperationException("The temporary sewer alignment source polyline is unavailable.");

            polylineOptions.EraseExistingEntities = false;
            string layerName = "0";
            try
            {
                LayerTableRecord layer = transaction.GetObject(layerId, OpenMode.ForRead, false) as LayerTableRecord;
                if (layer != null && !string.IsNullOrWhiteSpace(layer.Name)) layerName = layer.Name;
            }
            catch { }

            ObjectId alignmentId = ObjectId.Null;
            string namedFailure = null;
            try
            {
                alignmentId = CivilAlignment.Create(
                    civilDocument,
                    polylineOptions,
                    alignmentName,
                    null,
                    layerName,
                    alignmentStyleName,
                    labelSetStyleName);
            }
            catch (System.Exception exception)
            {
                namedFailure = exception.Message;
            }

            if (alignmentId.IsNull)
            {
                try
                {
                    alignmentId = CivilAlignment.Create(
                        civilDocument,
                        polylineOptions,
                        alignmentName,
                        ObjectId.Null,
                        layerId,
                        alignmentStyleId,
                        labelSetStyleId);
                }
                catch (System.Exception exception)
                {
                    throw new InvalidOperationException(
                        "Civil 3D could not create sewer alignment '" + alignmentName +
                        "'. Named-style attempt: " + (namedFailure ?? "unknown error") +
                        "; ObjectId fallback: " + exception.Message,
                        exception);
                }
            }

            if (alignmentId.IsNull)
                throw new InvalidOperationException("Civil 3D returned a null alignment id for '" + alignmentName + "'.");

            try
            {
                DBObject temporary = transaction.GetObject(polylineOptions.PlineId, OpenMode.ForWrite, false);
                if (temporary != null && !temporary.IsErased) temporary.Erase();
            }
            catch
            {
                // Alignment creation succeeded; failure to clean the temporary source
                // should not roll back a valid Civil alignment.
            }
            return alignmentId;
        }

        // -----------------------------------------------------------------
        // Small plan geometry helpers
        // -----------------------------------------------------------------
        private static Point2d PolygonCentroid(IList<Point2d> vertices)
        {
            if (vertices == null || vertices.Count == 0) return Point2d.Origin;
            double twiceArea = 0.0;
            double cx = 0.0;
            double cy = 0.0;
            for (int i = 0; i < vertices.Count; i++)
            {
                Point2d a = vertices[i];
                Point2d b = vertices[(i + 1) % vertices.Count];
                double cross = a.X * b.Y - b.X * a.Y;
                twiceArea += cross;
                cx += (a.X + b.X) * cross;
                cy += (a.Y + b.Y) * cross;
            }
            if (Math.Abs(twiceArea) <= Tol)
                return new Point2d(vertices.Average(item => item.X), vertices.Average(item => item.Y));
            return new Point2d(cx / (3.0 * twiceArea), cy / (3.0 * twiceArea));
        }

        private static Vector2d Unit(Point2d a, Point2d b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= Tol) return new Vector2d(1.0, 0.0);
            return new Vector2d(dx / length, dy / length);
        }

        private static Vector2d ToVector(Point2d point)
        {
            return new Vector2d(point.X, point.Y);
        }

        private static Vector2d Sub(Point2d a, Point2d b)
        {
            return new Vector2d(a.X - b.X, a.Y - b.Y);
        }

        private static double Dot(Vector2d a, Vector2d b)
        {
            return a.X * b.X + a.Y * b.Y;
        }

        private static double Cross(Vector2d a, Vector2d b)
        {
            return a.X * b.Y - a.Y * b.X;
        }

        private static Point2d FromBasis(Vector2d direction, Vector2d normal, double along, double across)
        {
            return new Point2d(
                direction.X * along + normal.X * across,
                direction.Y * along + normal.Y * across);
        }

        private static Point2d Mid(Point2d a, Point2d b)
        {
            return new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
        }

        private static double Distance(Point2d a, Point2d b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class ParcelBoundary
        {
            internal readonly int SourceIndex;
            internal readonly Point2d Centroid;
            internal readonly List<BoundarySegment> Edges;

            internal ParcelBoundary(int sourceIndex, Point2d centroid, List<BoundarySegment> edges)
            {
                SourceIndex = sourceIndex;
                Centroid = centroid;
                Edges = edges;
            }
        }

        private sealed class BoundarySegment
        {
            internal readonly int SourceIndex;
            internal readonly Point2d A;
            internal readonly Point2d B;
            internal readonly Point2d Centroid;

            internal BoundarySegment(int sourceIndex, Point2d a, Point2d b, Point2d centroid)
            {
                SourceIndex = sourceIndex;
                A = a;
                B = b;
                Centroid = centroid;
            }
        }

        private sealed class CentreSegment
        {
            internal Point2d A;
            internal Point2d B;

            internal CentreSegment(Point2d a, Point2d b)
            {
                A = a;
                B = b;
            }
        }
    }
}
