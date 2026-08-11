using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August11MidblockSewerProductionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Produces one continuous sewer planning route per cadastral erf row instead
    /// of one broken line per erf.  The route can use a chosen side or select the
    /// lower side from a Civil 3D surface.  Planning manholes are placed with the
    /// requested 1.2 m diameter and configurable 60/80 m maximum spacing, while
    /// preferring positions 1.5 m from nearby erf corners.
    /// </summary>
    public sealed class August11MidblockSewerProductionCommands
    {
        private const string LinkKey = "CE_MIDBLOCK_SEWER_PRODUCTION";
        private const string RouteLayer = "CE-SEWER-MIDBLOCK-ROUTE";
        private const string ManholeLayer = "CE-SEWER-MIDBLOCK-MH";
        private const string LabelLayer = "CE-SEWER-MIDBLOCK-LABEL";
        private const double Tol = 1e-7;

        [CommandMethod("CE_TOOLS", "CE_MIDBLOCKSEWERPRODUCTION", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateProductionRoutes()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Continuous Midblock Sewer Production",
                "Select cadastral erf boundaries once. CE Tools groups adjacent erfs into rows, creates one continuous route on the chosen/low side of each row, then places planning manholes to the selected maximum spacing while preferring positions near erf corners.");
            model.AddChoice("Scope", "01 Erfs", "Cadastral erfs", "Selected", "Use selected closed lightweight polylines or all non-CE closed lightweight polylines in model space.", new[] { "Selected", "All" });
            model.AddChoice("Orientation", "02 Route", "Row direction", "Automatic", "Automatic uses the dominant parcel-row direction; horizontal or vertical can be forced.", new[] { "Automatic", "Horizontal", "Vertical" });
            model.AddChoice("Side", "02 Route", "Route side", "Automatic low side from surface", "Choose the row side explicitly or let CE Tools compare both sides on a selected surface.", new[] { "Automatic low side from surface", "Bottom / Left side", "Top / Right side" });
            model.AddPositiveDouble("RouteInset", "02 Route", "Route inset from erf side", 1.5, "Offset the route this distance inside the selected outer erf side.");
            model.AddChoice("Spacing", "03 Manholes", "Maximum manhole spacing", "60 m", "Maximum planning pipe/manhole interval.", new[] { "60 m", "80 m", "Custom" });
            model.AddPositiveDouble("CustomSpacing", "03 Manholes", "Custom spacing", 60.0, "Used only when Custom is selected.");
            model.AddPositiveDouble("CornerOffset", "03 Manholes", "Preferred offset from erf corner", 1.5, "Prefer a manhole position this distance from nearby erf corners while maintaining maximum spacing.");
            model.AddPositiveDouble("ManholeDiameter", "03 Manholes", "Planning manhole diameter", 1.2, "Diameter of the planning manhole circle.");
            model.AddChoice("Replace", "04 Output", "Existing CE midblock production", "Replace existing", "Replace previous CE production routes/manholes or retain them.", new[] { "Replace existing", "Keep existing" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            List<ObjectId> parcelIds = ResolveParcels(document, model.Text("Scope"));
            if (parcelIds.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_MIDBLOCKSEWERPRODUCTION: no closed cadastral erf polylines were found.");
                return;
            }

            ObjectId surfaceId = ObjectId.Null;
            if (string.Equals(model.Text("Side"), "Automatic low side from surface", StringComparison.OrdinalIgnoreCase))
                surfaceId = PromptSurface(document);

            double maxSpacing = string.Equals(model.Text("Spacing"), "80 m", StringComparison.OrdinalIgnoreCase)
                ? 80.0
                : string.Equals(model.Text("Spacing"), "Custom", StringComparison.OrdinalIgnoreCase)
                    ? Math.Max(1.0, model.Double("CustomSpacing", 60.0))
                    : 60.0;
            double routeInset = Math.Max(0.0, model.Double("RouteInset", 1.5));
            double cornerOffset = Math.Max(0.0, model.Double("CornerOffset", 1.5));
            double manholeDiameter = Math.Max(0.1, model.Double("ManholeDiameter", 1.2));

            int routes = 0;
            int manholes = 0;
            int skipped = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return;
                if (string.Equals(model.Text("Replace"), "Replace existing", StringComparison.OrdinalIgnoreCase))
                    EraseExisting(space, transaction);
                ObjectId routeLayer = EnsureLayer(document.Database, transaction, RouteLayer, true);
                ObjectId mhLayer = EnsureLayer(document.Database, transaction, ManholeLayer, true);
                ObjectId labelLayer = EnsureLayer(document.Database, transaction, LabelLayer, true);

                List<ParcelBox> parcels = ReadParcels(parcelIds, transaction);
                if (parcels.Count == 0) return;
                bool horizontal = ResolveHorizontal(parcels, model.Text("Orientation"));
                List<List<ParcelBox>> rows = ClusterRows(parcels, horizontal);
                Surface surface = null;
                if (!surfaceId.IsNull)
                {
                    try { surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as Surface; }
                    catch { surface = null; }
                }

                int routeNumber = 1;
                foreach (List<ParcelBox> row in rows)
                {
                    RowGeometry geometry;
                    if (!TryBuildRowGeometry(row, horizontal, routeInset, model.Text("Side"), surface, out geometry))
                    {
                        skipped++;
                        continue;
                    }
                    Polyline route = CreateRoute(geometry);
                    if (route == null || route.Length <= Tol)
                    {
                        if (route != null) route.Dispose();
                        skipped++;
                        continue;
                    }
                    route.SetDatabaseDefaults(document.Database);
                    route.LayerId = routeLayer;
                    space.AppendEntity(route);
                    transaction.AddNewlyCreatedDBObject(route, true);
                    string group = Guid.NewGuid().ToString("N");
                    WriteLink(route, transaction, group, "ROUTE", routeNumber, maxSpacing, geometry.SideName);
                    routes++;

                    List<double> cornerStations = BuildPreferredCornerStations(row, geometry, cornerOffset);
                    List<double> stations = BuildManholeStations(route.Length, maxSpacing, cornerStations);
                    int mhIndex = 1;
                    foreach (double station in stations)
                    {
                        Point3d location;
                        try { location = route.GetPointAtDist(Math.Max(0.0, Math.Min(route.Length, station))); }
                        catch { continue; }
                        var circle = new Circle(location, Vector3d.ZAxis, manholeDiameter * 0.5);
                        circle.SetDatabaseDefaults(document.Database);
                        circle.LayerId = mhLayer;
                        space.AppendEntity(circle);
                        transaction.AddNewlyCreatedDBObject(circle, true);
                        WriteLink(circle, transaction, group, "MANHOLE", routeNumber, maxSpacing, geometry.SideName);

                        var text = new DBText
                        {
                            Position = location + new Vector3d(manholeDiameter, manholeDiameter, 0.0),
                            TextString = "MH-" + routeNumber.ToString(CultureInfo.InvariantCulture) + "." + mhIndex.ToString(CultureInfo.InvariantCulture),
                            Height = Math.Max(PaperAnnotationScale.ModelTextHeight(document.Database, 2.0), 0.001),
                            Rotation = horizontal ? 0.0 : Math.PI * 0.5,
                            LayerId = labelLayer
                        };
                        text.SetDatabaseDefaults(document.Database);
                        space.AppendEntity(text);
                        transaction.AddNewlyCreatedDBObject(text, true);
                        WriteLink(text, transaction, group, "LABEL", routeNumber, maxSpacing, geometry.SideName);
                        mhIndex++;
                        manholes++;
                    }
                    routeNumber++;
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            UniversalDynamicRefreshManager.Queue();
            document.Editor.WriteMessage("\nCE_MIDBLOCKSEWERPRODUCTION complete. Continuous routes={0}; planning manholes={1}; skipped rows={2}; max spacing={3:0.###} m.", routes, manholes, skipped, maxSpacing);
        }

        [CommandMethod("CE_TOOLS", "CE_MIDBLOCKSEWERINFO", CommandFlags.Modal)]
        public void Info()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            int routes = 0;
            int manholes = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (space != null)
                {
                    foreach (ObjectId id in space)
                    {
                        Entity entity;
                        try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                        catch { continue; }
                        string kind;
                        if (!TryReadKind(entity, transaction, out kind)) continue;
                        if (string.Equals(kind, "ROUTE", StringComparison.OrdinalIgnoreCase)) routes++;
                        if (string.Equals(kind, "MANHOLE", StringComparison.OrdinalIgnoreCase)) manholes++;
                    }
                }
            }
            PopupTablePresenter.ShowReview(
                "CE Tools - Midblock Sewer Production",
                "Continuous erf-row route and planning manhole inventory.",
                new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("Routes", routes.ToString(CultureInfo.InvariantCulture)),
                    new KeyValuePair<string, string>("Planning manholes", manholes.ToString(CultureInfo.InvariantCulture)),
                    new KeyValuePair<string, string>("Network handoff", "CE_NETWORKFROMPOLYLINESBATCH"),
                    new KeyValuePair<string, string>("Sewer production", "CE_SEWERPRODUCTIONCENTRE")
                },
                "Close");
        }

        private static List<ObjectId> ResolveParcels(Document document, string scope)
        {
            if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection = document.Editor.SelectImplied();
                if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
                {
                    selection = document.Editor.GetSelection(
                        new PromptSelectionOptions { MessageForAdding = "\nSelect closed cadastral erf polylines: ", AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true },
                        new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") }));
                }
                if (selection.Status != PromptStatus.OK || selection.Value == null) return new List<ObjectId>();
                return FilterClosed(document.Database, selection.Value.GetObjectIds());
            }
            var ids = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return ids;
                foreach (ObjectId id in space)
                {
                    Polyline polyline;
                    try { polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch { continue; }
                    if (polyline != null && polyline.Closed && !IsGenerated(polyline, transaction)) ids.Add(id);
                }
            }
            return ids;
        }

        private static List<ObjectId> FilterClosed(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids.Where(value => !value.IsNull && !value.IsErased).Distinct())
                {
                    Polyline polyline;
                    try { polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch { continue; }
                    if (polyline != null && polyline.Closed) result.Add(id);
                }
            }
            return result;
        }

        private static ObjectId PromptSurface(Document document)
        {
            var options = new PromptEntityOptions("\nSelect existing-ground surface for automatic low-side routing, or press Esc to use Bottom/Left: ");
            options.SetRejectMessage("\nSelect a Civil 3D surface.");
            options.AddAllowedClass(typeof(Surface), true);
            PromptEntityResult result = document.Editor.GetEntity(options);
            return result.Status == PromptStatus.OK ? result.ObjectId : ObjectId.Null;
        }

        private static List<ParcelBox> ReadParcels(IEnumerable<ObjectId> ids, Transaction transaction)
        {
            var result = new List<ParcelBox>();
            foreach (ObjectId id in ids)
            {
                Polyline polyline;
                try { polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch { continue; }
                if (polyline == null || !polyline.Closed) continue;
                Extents3d extents;
                try { extents = polyline.GeometricExtents; }
                catch { continue; }
                if (extents.MaxPoint.X - extents.MinPoint.X <= Tol || extents.MaxPoint.Y - extents.MinPoint.Y <= Tol) continue;
                result.Add(new ParcelBox(id, extents));
            }
            return result;
        }

        private static bool ResolveHorizontal(IList<ParcelBox> parcels, string selection)
        {
            if (string.Equals(selection, "Horizontal", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(selection, "Vertical", StringComparison.OrdinalIgnoreCase)) return false;
            double totalWidth = parcels.Sum(item => item.Width);
            double totalHeight = parcels.Sum(item => item.Height);
            return totalWidth >= totalHeight;
        }

        private static List<List<ParcelBox>> ClusterRows(List<ParcelBox> parcels, bool horizontal)
        {
            double typical = Median(parcels.Select(item => horizontal ? item.Height : item.Width));
            double tolerance = Math.Max(typical * 0.65, 0.5);
            List<ParcelBox> sorted = parcels.OrderBy(item => horizontal ? -item.Center.Y : item.Center.X).ThenBy(item => horizontal ? item.Center.X : -item.Center.Y).ToList();
            var rows = new List<List<ParcelBox>>();
            foreach (ParcelBox parcel in sorted)
            {
                double cross = horizontal ? parcel.Center.Y : parcel.Center.X;
                List<ParcelBox> best = null;
                double bestDelta = double.MaxValue;
                foreach (List<ParcelBox> row in rows)
                {
                    double rowCross = row.Average(item => horizontal ? item.Center.Y : item.Center.X);
                    double delta = Math.Abs(cross - rowCross);
                    if (delta <= tolerance && delta < bestDelta)
                    {
                        best = row;
                        bestDelta = delta;
                    }
                }
                if (best == null)
                {
                    best = new List<ParcelBox>();
                    rows.Add(best);
                }
                best.Add(parcel);
            }
            foreach (List<ParcelBox> row in rows)
                row.Sort((a, b) => (horizontal ? a.Center.X.CompareTo(b.Center.X) : a.Center.Y.CompareTo(b.Center.Y)));
            return rows.Where(row => row.Count > 0).ToList();
        }

        private static bool TryBuildRowGeometry(List<ParcelBox> row, bool horizontal, double inset, string sideChoice, Surface surface, out RowGeometry geometry)
        {
            geometry = new RowGeometry();
            if (row == null || row.Count == 0) return false;
            double minX = row.Min(item => item.Extents.MinPoint.X);
            double maxX = row.Max(item => item.Extents.MaxPoint.X);
            double minY = row.Min(item => item.Extents.MinPoint.Y);
            double maxY = row.Max(item => item.Extents.MaxPoint.Y);
            bool highSide = string.Equals(sideChoice, "Top / Right side", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(sideChoice, "Automatic low side from surface", StringComparison.OrdinalIgnoreCase))
            {
                if (surface != null)
                {
                    double first = horizontal
                        ? AverageSurface(surface, new[] { new Point2d(minX, minY + inset), new Point2d((minX + maxX) * 0.5, minY + inset), new Point2d(maxX, minY + inset) })
                        : AverageSurface(surface, new[] { new Point2d(minX + inset, minY), new Point2d(minX + inset, (minY + maxY) * 0.5), new Point2d(minX + inset, maxY) });
                    double second = horizontal
                        ? AverageSurface(surface, new[] { new Point2d(minX, maxY - inset), new Point2d((minX + maxX) * 0.5, maxY - inset), new Point2d(maxX, maxY - inset) })
                        : AverageSurface(surface, new[] { new Point2d(maxX - inset, minY), new Point2d(maxX - inset, (minY + maxY) * 0.5), new Point2d(maxX - inset, maxY) });
                    if (!double.IsNaN(first) && !double.IsNaN(second)) highSide = second < first;
                    else highSide = false;
                }
                else highSide = false;
            }

            if (horizontal)
            {
                double y = highSide ? maxY - inset : minY + inset;
                double startX = minX + Math.Min(inset, Math.Max(0.0, (maxX - minX) * 0.1));
                double endX = maxX - Math.Min(inset, Math.Max(0.0, (maxX - minX) * 0.1));
                if (endX - startX <= Tol) return false;
                geometry = new RowGeometry(new Point2d(startX, y), new Point2d(endX, y), true, highSide ? "TOP" : "BOTTOM");
            }
            else
            {
                double x = highSide ? maxX - inset : minX + inset;
                double startY = minY + Math.Min(inset, Math.Max(0.0, (maxY - minY) * 0.1));
                double endY = maxY - Math.Min(inset, Math.Max(0.0, (maxY - minY) * 0.1));
                if (endY - startY <= Tol) return false;
                geometry = new RowGeometry(new Point2d(x, startY), new Point2d(x, endY), false, highSide ? "RIGHT" : "LEFT");
            }
            return true;
        }

        private static double AverageSurface(Surface surface, IEnumerable<Point2d> points)
        {
            var values = new List<double>();
            foreach (Point2d point in points)
            {
                try
                {
                    double value = surface.FindElevationAtXY(point.X, point.Y);
                    if (!double.IsNaN(value) && !double.IsInfinity(value)) values.Add(value);
                }
                catch { }
            }
            return values.Count == 0 ? double.NaN : values.Average();
        }

        private static Polyline CreateRoute(RowGeometry geometry)
        {
            if (geometry.Start.GetDistanceTo(geometry.End) <= Tol) return null;
            var route = new Polyline(2) { Closed = false };
            route.AddVertexAt(0, geometry.Start, 0.0, 0.0, 0.0);
            route.AddVertexAt(1, geometry.End, 0.0, 0.0, 0.0);
            return route;
        }

        private static List<double> BuildPreferredCornerStations(IEnumerable<ParcelBox> row, RowGeometry geometry, double offset)
        {
            double length = geometry.Start.GetDistanceTo(geometry.End);
            var stations = new List<double>();
            foreach (ParcelBox parcel in row)
            {
                if (geometry.Horizontal)
                {
                    foreach (double x in new[] { parcel.Extents.MinPoint.X + offset, parcel.Extents.MaxPoint.X - offset })
                    {
                        double station = x - geometry.Start.X;
                        if (station > Tol && station < length - Tol) stations.Add(station);
                    }
                }
                else
                {
                    foreach (double y in new[] { parcel.Extents.MinPoint.Y + offset, parcel.Extents.MaxPoint.Y - offset })
                    {
                        double station = y - geometry.Start.Y;
                        if (station > Tol && station < length - Tol) stations.Add(station);
                    }
                }
            }
            return stations.OrderBy(value => value).Distinct(new StationComparer(0.05)).ToList();
        }

        private static List<double> BuildManholeStations(double length, double maximum, List<double> preferred)
        {
            var result = new List<double> { 0.0 };
            if (length <= Tol) return result;
            double current = 0.0;
            int guard = 0;
            while (length - current > maximum + Tol && guard++ < 10000)
            {
                double limit = current + maximum;
                double minimumAdvance = Math.Min(Math.Max(maximum * 0.45, 1.0), maximum);
                double candidate = preferred.Where(value => value > current + minimumAdvance && value <= limit + Tol).DefaultIfEmpty(double.NaN).Max();
                double next = double.IsNaN(candidate) ? limit : candidate;
                if (next <= current + Tol) next = Math.Min(length, current + maximum);
                result.Add(next);
                current = next;
            }
            if (Math.Abs(result[result.Count - 1] - length) > Tol) result.Add(length);
            return result;
        }

        private static double Median(IEnumerable<double> input)
        {
            double[] values = input.Where(value => value > Tol).OrderBy(value => value).ToArray();
            if (values.Length == 0) return 1.0;
            int middle = values.Length / 2;
            return values.Length % 2 == 0 ? (values[middle - 1] + values[middle]) * 0.5 : values[middle];
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction, string name, bool plottable)
        {
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table.Has(name))
            {
                LayerTableRecord existing = transaction.GetObject(table[name], OpenMode.ForWrite, false) as LayerTableRecord;
                if (existing != null) existing.IsPlottable = plottable;
                return table[name];
            }
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name, IsPlottable = plottable, Color = Color.FromColorIndex(ColorMethod.ByAci, 3) };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static void WriteLink(Entity entity, Transaction transaction, string group, string kind, int route, double spacing, string side)
        {
            if (entity.ExtensionDictionary.IsNull) entity.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(LinkKey)) record = transaction.GetObject(dictionary.GetAt(LinkKey), OpenMode.ForWrite, false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(LinkKey, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            if (record != null)
            {
                record.Data = new ResultBuffer(
                    new TypedValue((int)DxfCode.Text, group ?? string.Empty),
                    new TypedValue((int)DxfCode.Text, kind ?? string.Empty),
                    new TypedValue((int)DxfCode.Int32, route),
                    new TypedValue((int)DxfCode.Real, spacing),
                    new TypedValue((int)DxfCode.Text, side ?? string.Empty));
            }
        }

        private static void EraseExisting(BlockTableRecord space, Transaction transaction)
        {
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                Entity entity;
                try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; }
                catch { continue; }
                if (entity == null || entity.ExtensionDictionary.IsNull) continue;
                try
                {
                    DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                    if (dictionary != null && dictionary.Contains(LinkKey)) entity.Erase();
                }
                catch { }
            }
        }

        private static bool TryReadKind(Entity entity, Transaction transaction, out string kind)
        {
            kind = string.Empty;
            if (entity == null || entity.ExtensionDictionary.IsNull) return false;
            try
            {
                DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                if (dictionary == null || !dictionary.Contains(LinkKey)) return false;
                Xrecord record = transaction.GetObject(dictionary.GetAt(LinkKey), OpenMode.ForRead, false) as Xrecord;
                if (record == null || record.Data == null) return false;
                TypedValue[] values = record.Data.AsArray();
                if (values.Length < 2) return false;
                kind = Convert.ToString(values[1].Value, CultureInfo.InvariantCulture);
                return true;
            }
            catch { return false; }
        }

        private static bool IsGenerated(Entity entity, Transaction transaction)
        {
            string kind;
            return TryReadKind(entity, transaction, out kind);
        }

        private sealed class ParcelBox
        {
            internal ParcelBox(ObjectId id, Extents3d extents)
            {
                Id = id;
                Extents = extents;
                Center = new Point2d((extents.MinPoint.X + extents.MaxPoint.X) * 0.5, (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5);
            }
            internal ObjectId Id { get; private set; }
            internal Extents3d Extents { get; private set; }
            internal Point2d Center { get; private set; }
            internal double Width { get { return Extents.MaxPoint.X - Extents.MinPoint.X; } }
            internal double Height { get { return Extents.MaxPoint.Y - Extents.MinPoint.Y; } }
        }

        private struct RowGeometry
        {
            internal RowGeometry(Point2d start, Point2d end, bool horizontal, string sideName)
            {
                Start = start;
                End = end;
                Horizontal = horizontal;
                SideName = sideName;
            }
            internal Point2d Start;
            internal Point2d End;
            internal bool Horizontal;
            internal string SideName;
        }

        private sealed class StationComparer : IEqualityComparer<double>
        {
            private readonly double _tolerance;
            internal StationComparer(double tolerance) { _tolerance = Math.Max(tolerance, 1e-9); }
            public bool Equals(double x, double y) { return Math.Abs(x - y) <= _tolerance; }
            public int GetHashCode(double obj) { return Math.Round(obj / _tolerance).GetHashCode(); }
        }
    }
}
