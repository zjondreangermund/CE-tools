using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.UtilityPlanningCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Cadastral utility route preparation. The tool deliberately creates linked
    /// planning geometry first; final Civil 3D pipe/pressure-network parts remain
    /// under the Sewer, Stormwater and Water production workflows where the active
    /// parts list, catalogue and authority standards are available.
    /// </summary>
    public sealed class UtilityPlanningCommands
    {
        private const string RegApp = "CE_UTILITY_ROUTE";

        [CommandMethod("CE_TOOLS", "CE_UTILITYPLANNER", CommandFlags.Modal)]
        public void UtilityPlanner()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Cadastral Utility Planner",
                "Prepare linked cadastral routes and then continue into the existing CE sewer, stormwater, water, BOQ, costing and excavation workflows.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Create / refresh cadastral utility routes", "CE_UTILITYROUTES", "Offset selected closed erf/cadastral boundaries, add planning manhole points and produce a route/constraint report.", "01 Route Planning"),
                    new DisciplineWorkflowAction("Refresh linked utility routes", "CE_UTILITYROUTESREFRESH", "Rebuild CE utility planning routes from their source cadastral polylines after boundary edits.", "01 Route Planning"),
                    new DisciplineWorkflowAction("Prepare crossings and junctions", "CE_PLBREAKJUNCTIONS", "Break route polylines at true crossings and T-junctions before network production.", "02 Network Preparation"),
                    new DisciplineWorkflowAction("Sewer production", "CE_SEWTOOLS", "Sequence, label, align and profile the sewer design using the active Civil 3D parts/network standards.", "03 Discipline Production"),
                    new DisciplineWorkflowAction("Stormwater production", "CE_SWTOOLS", "Build and analyse stormwater branches from prepared route polylines or an existing network.", "03 Discipline Production"),
                    new DisciplineWorkflowAction("Water production", "CE_WATERTOOLS", "Build water alignments, profiles and placement reviews from prepared routes or pressure pipes.", "03 Discipline Production"),
                    new DisciplineWorkflowAction("Network asset schedule and BOQ", "CE_NETWORKSCHEDULETOOLS", "Create dynamic network schedules and summarized BOQ data from the resulting Civil 3D networks.", "04 Quantities"),
                    new DisciplineWorkflowAction("Water / sewer cost estimate", "CE_WSCOSTTOOLS", "Create linked water and sewer cost estimates and spreadsheet output.", "04 Quantities"),
                    new DisciplineWorkflowAction("Sewer excavation", "CE_SEWEREXCAVATION", "Calculate linked excavation quantities using real pipe geometry, cover and bedding settings.", "04 Quantities")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_UTILITYROUTES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateUtilityRoutes()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            UtilityRouteSettings settings = EditSettings();
            if (settings == null) return;

            PromptSelectionOptions options = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect closed cadastral/erf boundary polylines: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            };
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
            });
            PromptSelectionResult selected = document.Editor.GetSelection(options, filter);
            if (selected.Status != PromptStatus.OK || selected.Value == null) return;

            UtilityRouteResult result = BuildRoutes(document, selected.Value.GetObjectIds(), settings, false);
            ShowResult(document, result, settings);
        }

        [CommandMethod("CE_TOOLS", "CE_UTILITYROUTESREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshUtilityRoutes()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            UtilityRouteResult result = RefreshRoutes(document);
            document.Editor.WriteMessage(
                "\nCE_UTILITYROUTESREFRESH complete. Routes rebuilt={0}; planning points={1}; warnings={2}.",
                result.Routes,
                result.PlanningPoints,
                result.Warnings.Count);
        }

        private static UtilityRouteSettings EditSettings()
        {
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Cadastral Utility Route Settings",
                "Prepare utility routes from closed erf/cadastral boundaries. Values are design constraints and are written to each linked route for refresh and reporting.");
            model.AddDouble("Offset", "01 Route", "Boundary offset (m)", 1.5, "Typical sewer route offset from the erf boundary.");
            model.AddChoice("RouteMode", "01 Route", "Route option", "Inside road reserve", "Inside road reserve offsets the cadastral boundary. Midblock sewer centreline creates an open centreline through the selected block/erf footprint for sewer planning.", new[] { "Inside road reserve", "Midblock sewer centreline" });
            model.AddDouble("ManholeSpacing", "02 Manholes", "Maximum manhole spacing (m)", 80.0, "Planning points are added at vertices and at this maximum spacing.");
            model.AddChoice("CornerManholes", "02 Manholes", "Place planning manholes at erf corners", "Yes", "Corner points remain planning references until converted to Civil 3D structures.", new[] { "Yes", "No" });
            model.AddDouble("MinSlope", "03 Pipe Constraints", "Minimum pipe slope (%)", 0.5, "Constraint reported to sewer/stormwater design review.");
            model.AddDouble("MaxSlope", "03 Pipe Constraints", "Maximum pipe slope (%)", 15.0, "Constraint reported to sewer/stormwater design review.");
            model.AddDouble("MinCover", "03 Pipe Constraints", "Minimum pipe cover (m)", 0.9, "Constraint carried into design review and excavation.");
            model.AddDouble("MaxCover", "03 Pipe Constraints", "Maximum pipe cover (m)", 4.0, "Constraint carried into design review and excavation.");
            model.AddDouble("MinDrop", "03 Pipe Constraints", "Minimum structure drop (m)", 0.05, "Minimum planning drop at structures.");
            model.AddDouble("MaxDrop", "03 Pipe Constraints", "Maximum structure drop (m)", 1.0, "Maximum planning drop before special design review.");
            model.AddDouble("AngleWarning", "04 Warnings", "Warn when included pipe angle is below (degrees)", 90.0, "Sharp route corners are flagged in the planning report.");
            model.AddChoice("HouseConnections", "05 Connections", "House connection planning", "Best connection side", "Stores the intended house-connection planning mode for downstream design.", new[] { "Best connection side", "Do not plan house connections" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return null;
            var settings = new UtilityRouteSettings
            {
                Offset = Math.Abs(model.Double("Offset", 1.5)),
                RouteMode = model.Text("RouteMode"),
                ManholeSpacing = Math.Max(1.0, model.Double("ManholeSpacing", 80.0)),
                CornerManholes = string.Equals(model.Text("CornerManholes"), "Yes", StringComparison.OrdinalIgnoreCase),
                MinSlope = model.Double("MinSlope", 0.5),
                MaxSlope = model.Double("MaxSlope", 15.0),
                MinCover = model.Double("MinCover", 0.9),
                MaxCover = model.Double("MaxCover", 4.0),
                MinDrop = model.Double("MinDrop", 0.05),
                MaxDrop = model.Double("MaxDrop", 1.0),
                AngleWarning = model.Double("AngleWarning", 90.0),
                HouseConnections = model.Text("HouseConnections")
            };
            if (settings.MaxSlope < settings.MinSlope || settings.MaxCover < settings.MinCover || settings.MaxDrop < settings.MinDrop)
                return null;
            return settings;
        }

        private static UtilityRouteResult BuildRoutes(Document document, IEnumerable<ObjectId> sourceIds, UtilityRouteSettings settings, bool refresh)
        {
            var result = new UtilityRouteResult();
            Database database = document.Database;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction);
                ObjectId routeLayer = GetOrCreateLayer(database, transaction, "CE-UTILITY-ROUTE");
                ObjectId pointLayer = GetOrCreateLayer(database, transaction, "CE-UTILITY-PLANNING-POINT");
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite, false) as BlockTableRecord;
                foreach (ObjectId sourceId in sourceIds.Distinct())
                {
                    Polyline source = transaction.GetObject(sourceId, OpenMode.ForRead, false) as Polyline;
                    if (source == null || !source.Closed || source.NumberOfVertices < 3)
                    {
                        result.Warnings.Add("Skipped non-closed cadastral polyline " + sourceId.Handle);
                        continue;
                    }
                    Polyline route = CreatePlanningRoute(source, settings);
                    if (route == null)
                    {
                        result.Warnings.Add("Could not create a stable inward offset for cadastral polyline " + sourceId.Handle);
                        continue;
                    }
                    route.SetDatabaseDefaults(database);
                    route.LayerId = routeLayer;
                    model.AppendEntity(route);
                    transaction.AddNewlyCreatedDBObject(route, true);
                    WriteLink(route, sourceId, settings);
                    result.Routes++;
                    result.TotalLength += route.Length;
                    result.PlanningPoints += AddPlanningPoints(model, transaction, route, pointLayer, settings, result.Warnings);
                }
                transaction.Commit();
            }
            return result;
        }

        private static UtilityRouteResult RefreshRoutes(Document document)
        {
            var result = new UtilityRouteResult();
            Database database = document.Database;
            var links = new List<UtilityRouteLink>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead, false) as BlockTableRecord;
                foreach (ObjectId id in model)
                {
                    Polyline route = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                    UtilityRouteLink link;
                    if (route != null && TryReadLink(database, route, out link))
                    {
                        link.RouteId = id;
                        links.Add(link);
                    }
                }
            }
            if (links.Count == 0) return result;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                ObjectId pointLayer = GetOrCreateLayer(database, transaction, "CE-UTILITY-PLANNING-POINT");
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite, false) as BlockTableRecord;
                foreach (UtilityRouteLink link in links)
                {
                    Polyline oldRoute = transaction.GetObject(link.RouteId, OpenMode.ForWrite, false) as Polyline;
                    Polyline source = transaction.GetObject(link.SourceId, OpenMode.ForRead, false) as Polyline;
                    if (oldRoute == null || source == null) { result.Warnings.Add("A linked source or route was deleted."); continue; }
                    Polyline rebuilt = CreatePlanningRoute(source, link.Settings);
                    if (rebuilt == null) { result.Warnings.Add("Could not refresh route " + link.RouteId.Handle); continue; }
                    oldRoute.UpgradeOpen();
                    while (oldRoute.NumberOfVertices > 0) oldRoute.RemoveVertexAt(oldRoute.NumberOfVertices - 1);
                    for (int index = 0; index < rebuilt.NumberOfVertices; index++)
                        oldRoute.AddVertexAt(index, rebuilt.GetPoint2dAt(index), rebuilt.GetBulgeAt(index), rebuilt.GetStartWidthAt(index), rebuilt.GetEndWidthAt(index));
                    oldRoute.Closed = rebuilt.Closed;
                    oldRoute.Elevation = rebuilt.Elevation;
                    result.Routes++;
                    result.TotalLength += oldRoute.Length;
                    result.PlanningPoints += AddPlanningPoints(model, transaction, oldRoute, pointLayer, link.Settings, result.Warnings);
                    rebuilt.Dispose();
                }
                transaction.Commit();
            }
            return result;
        }

        private static Polyline CreatePlanningRoute(Polyline source, UtilityRouteSettings settings)
        {
            if (settings != null && string.Equals(settings.RouteMode, "Midblock sewer centreline", StringComparison.OrdinalIgnoreCase))
                return CreateMidblockRoute(source, settings.Offset);
            return CreateInwardOffset(source, settings == null ? 0.0 : settings.Offset);
        }

        private static Polyline CreateMidblockRoute(Polyline source, double endInset)
        {
            if (source == null || source.NumberOfVertices < 3) return null;
            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            for (int index = 0; index < source.NumberOfVertices; index++)
            {
                Point2d point = source.GetPoint2dAt(index);
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }
            if (double.IsNaN(minX) || double.IsInfinity(minX) || double.IsNaN(minY) || double.IsInfinity(minY) || double.IsNaN(maxX) || double.IsInfinity(maxX) || double.IsNaN(maxY) || double.IsInfinity(maxY)) return null;
            double width = maxX - minX;
            double height = maxY - minY;
            if (width <= 1e-6 || height <= 1e-6) return null;
            double inset = Math.Max(0.0, Math.Min(Math.Abs(endInset), 0.25 * Math.Max(width, height)));
            var route = new Polyline(2);
            if (width >= height)
            {
                double y = 0.5 * (minY + maxY);
                route.AddVertexAt(0, new Point2d(minX + inset, y), 0.0, 0.0, 0.0);
                route.AddVertexAt(1, new Point2d(maxX - inset, y), 0.0, 0.0, 0.0);
            }
            else
            {
                double x = 0.5 * (minX + maxX);
                route.AddVertexAt(0, new Point2d(x, minY + inset), 0.0, 0.0, 0.0);
                route.AddVertexAt(1, new Point2d(x, maxY - inset), 0.0, 0.0, 0.0);
            }
            route.Closed = false;
            route.Elevation = source.Elevation;
            return route;
        }

        private static Polyline CreateInwardOffset(Polyline source, double distance)
        {
            if (distance <= 0.0) return ClonePolyline(source);
            var candidates = new List<Polyline>();
            foreach (double sign in new[] { -1.0, 1.0 })
            {
                try
                {
                    DBObjectCollection curves = source.GetOffsetCurves(sign * distance);
                    foreach (DBObject value in curves)
                    {
                        Polyline polyline = value as Polyline;
                        if (polyline != null && polyline.Closed && polyline.NumberOfVertices >= 3) candidates.Add(polyline);
                        else value.Dispose();
                    }
                }
                catch { }
            }
            if (candidates.Count == 0) return null;
            double sourceArea = Math.Abs(source.Area);
            Polyline chosen = candidates
                .Where(item => Math.Abs(item.Area) < sourceArea)
                .OrderByDescending(item => Math.Abs(item.Area))
                .FirstOrDefault() ?? candidates.OrderBy(item => Math.Abs(item.Area)).First();
            foreach (Polyline other in candidates.Where(item => !ReferenceEquals(item, chosen))) other.Dispose();
            return chosen;
        }

        private static Polyline ClonePolyline(Polyline source)
        {
            var result = new Polyline(source.NumberOfVertices);
            for (int i = 0; i < source.NumberOfVertices; i++)
                result.AddVertexAt(i, source.GetPoint2dAt(i), source.GetBulgeAt(i), source.GetStartWidthAt(i), source.GetEndWidthAt(i));
            result.Closed = source.Closed;
            result.Elevation = source.Elevation;
            return result;
        }

        private static int AddPlanningPoints(BlockTableRecord model, Transaction transaction, Polyline route, ObjectId layerId, UtilityRouteSettings settings, IList<string> warnings)
        {
            int added = 0;
            var distances = new SortedSet<double>();
            if (settings.CornerManholes)
            {
                for (int i = 0; i < route.NumberOfVertices; i++)
                {
                    try { distances.Add(route.GetDistAtPoint(route.GetPoint3dAt(i))); } catch { }
                    double angle = IncludedAngle(route, i);
                    if (angle > 0.0 && angle < settings.AngleWarning)
                        warnings.Add(string.Format(CultureInfo.CurrentCulture, "Route corner {0} has included angle {1:N1}°, below {2:N1}°.", i + 1, angle, settings.AngleWarning));
                }
            }
            for (double distance = settings.ManholeSpacing; distance < route.Length - 0.001; distance += settings.ManholeSpacing)
                distances.Add(distance);
            foreach (double distance in distances)
            {
                Point3d point;
                try { point = route.GetPointAtDist(distance); }
                catch { continue; }
                var marker = new DBPoint(point) { LayerId = layerId };
                marker.SetDatabaseDefaults(model.Database);
                model.AppendEntity(marker);
                transaction.AddNewlyCreatedDBObject(marker, true);
                added++;
            }
            return added;
        }

        private static double IncludedAngle(Polyline route, int index)
        {
            if (route.NumberOfVertices < 3) return 0.0;
            int previous = (index - 1 + route.NumberOfVertices) % route.NumberOfVertices;
            int next = (index + 1) % route.NumberOfVertices;
            Vector2d a = route.GetPoint2dAt(previous) - route.GetPoint2dAt(index);
            Vector2d b = route.GetPoint2dAt(next) - route.GetPoint2dAt(index);
            if (a.Length < 1e-9 || b.Length < 1e-9) return 0.0;
            double dot = a.GetNormal().DotProduct(b.GetNormal());
            dot = Math.Max(-1.0, Math.Min(1.0, dot));
            return Math.Acos(dot) * 180.0 / Math.PI;
        }

        private static void ShowResult(Document document, UtilityRouteResult result, UtilityRouteSettings settings)
        {
            var rows = new List<IList<string>>
            {
                new List<string> { "Routes created", result.Routes.ToString(CultureInfo.CurrentCulture) },
                new List<string> { "Total planning length", result.TotalLength.ToString("N2", CultureInfo.CurrentCulture) + " m" },
                new List<string> { "Planning manhole points", result.PlanningPoints.ToString(CultureInfo.CurrentCulture) },
                new List<string> { "Boundary offset", settings.Offset.ToString("N2", CultureInfo.CurrentCulture) + " m" },
                new List<string> { "Route mode", settings.RouteMode ?? string.Empty },
                new List<string> { "Manhole spacing", settings.ManholeSpacing.ToString("N1", CultureInfo.CurrentCulture) + " m" },
                new List<string> { "Slope range", settings.MinSlope.ToString("N2", CultureInfo.CurrentCulture) + "% to " + settings.MaxSlope.ToString("N2", CultureInfo.CurrentCulture) + "%" },
                new List<string> { "Cover range", settings.MinCover.ToString("N2", CultureInfo.CurrentCulture) + " m to " + settings.MaxCover.ToString("N2", CultureInfo.CurrentCulture) + " m" },
                new List<string> { "Drop range", settings.MinDrop.ToString("N2", CultureInfo.CurrentCulture) + " m to " + settings.MaxDrop.ToString("N2", CultureInfo.CurrentCulture) + " m" },
                new List<string> { "Warnings", result.Warnings.Count.ToString(CultureInfo.CurrentCulture) }
            };
            foreach (string warning in result.Warnings.Take(20)) rows.Add(new List<string> { "Warning", warning });
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Utility Route Planning Report",
                "Linked planning geometry only. Convert/sequence it through the Sewer, Stormwater or Water workflow using the active Civil 3D part catalogue and authority standards.",
                new List<string> { "Item", "Value" },
                rows,
                "CE TOOLS UTILITY ROUTE PLANNING");
        }

        private static void WriteLink(Polyline route, ObjectId sourceId, UtilityRouteSettings settings)
        {
            route.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegApp),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, sourceId.Handle.ToString()),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, settings.Serialize()));
        }

        private static bool TryReadLink(Database database, Polyline route, out UtilityRouteLink link)
        {
            link = null;
            ResultBuffer buffer = route.GetXDataForApplication(RegApp);
            if (buffer == null) return false;
            TypedValue[] values = buffer.AsArray();
            if (values.Length < 3) return false;
            try
            {
                long handle = long.Parse(Convert.ToString(values[1].Value, CultureInfo.InvariantCulture), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                ObjectId source = database.GetObjectId(false, new Handle(handle), 0);
                UtilityRouteSettings settings = UtilityRouteSettings.Deserialize(Convert.ToString(values[2].Value, CultureInfo.InvariantCulture));
                if (source.IsNull || settings == null) return false;
                link = new UtilityRouteLink { SourceId = source, Settings = settings };
                return true;
            }
            catch { return false; }
        }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(database.RegAppTableId, OpenMode.ForRead, false) as RegAppTable;
            if (table.Has(RegApp)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegApp };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static ObjectId GetOrCreateLayer(Database database, Transaction transaction, string name)
        {
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            var record = new LayerTableRecord { Name = name };
            ObjectId id = layers.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return id;
        }

        private static Document ActiveDocument() { return AcApplication.DocumentManager.MdiActiveDocument; }
    }

    internal sealed class UtilityRouteSettings
    {
        internal double Offset { get; set; }
        internal string RouteMode { get; set; }
        internal double ManholeSpacing { get; set; }
        internal bool CornerManholes { get; set; }
        internal double MinSlope { get; set; }
        internal double MaxSlope { get; set; }
        internal double MinCover { get; set; }
        internal double MaxCover { get; set; }
        internal double MinDrop { get; set; }
        internal double MaxDrop { get; set; }
        internal double AngleWarning { get; set; }
        internal string HouseConnections { get; set; }

        internal string Serialize()
        {
            return string.Join("|", new[]
            {
                Offset.ToString("R", CultureInfo.InvariantCulture), RouteMode ?? string.Empty,
                ManholeSpacing.ToString("R", CultureInfo.InvariantCulture), CornerManholes ? "1" : "0",
                MinSlope.ToString("R", CultureInfo.InvariantCulture), MaxSlope.ToString("R", CultureInfo.InvariantCulture),
                MinCover.ToString("R", CultureInfo.InvariantCulture), MaxCover.ToString("R", CultureInfo.InvariantCulture),
                MinDrop.ToString("R", CultureInfo.InvariantCulture), MaxDrop.ToString("R", CultureInfo.InvariantCulture),
                AngleWarning.ToString("R", CultureInfo.InvariantCulture), HouseConnections ?? string.Empty
            });
        }

        internal static UtilityRouteSettings Deserialize(string value)
        {
            string[] p = (value ?? string.Empty).Split('|');
            if (p.Length < 12) return null;
            try
            {
                return new UtilityRouteSettings
                {
                    Offset = double.Parse(p[0], CultureInfo.InvariantCulture), RouteMode = p[1],
                    ManholeSpacing = double.Parse(p[2], CultureInfo.InvariantCulture), CornerManholes = p[3] == "1",
                    MinSlope = double.Parse(p[4], CultureInfo.InvariantCulture), MaxSlope = double.Parse(p[5], CultureInfo.InvariantCulture),
                    MinCover = double.Parse(p[6], CultureInfo.InvariantCulture), MaxCover = double.Parse(p[7], CultureInfo.InvariantCulture),
                    MinDrop = double.Parse(p[8], CultureInfo.InvariantCulture), MaxDrop = double.Parse(p[9], CultureInfo.InvariantCulture),
                    AngleWarning = double.Parse(p[10], CultureInfo.InvariantCulture), HouseConnections = p[11]
                };
            }
            catch { return null; }
        }
    }

    internal sealed class UtilityRouteResult
    {
        internal UtilityRouteResult() { Warnings = new List<string>(); }
        internal int Routes { get; set; }
        internal int PlanningPoints { get; set; }
        internal double TotalLength { get; set; }
        internal List<string> Warnings { get; private set; }
    }

    internal sealed class UtilityRouteLink
    {
        internal ObjectId RouteId { get; set; }
        internal ObjectId SourceId { get; set; }
        internal UtilityRouteSettings Settings { get; set; }
    }
}
