using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using StyleBase = Autodesk.Civil.DatabaseServices.Styles.StyleBase;

[assembly: CommandClass(typeof(CETools.Civil3D.September11FieldCompletionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Field-completion commands added after the September 10 Civil 3D 2023 pass.
    /// The implementation deliberately keeps the risky Civil operations separated:
    /// surface/rule writes commit before profiles are queued, Google Earth export is
    /// read-only, hatch boundaries are new ordinary polylines, and road-centre cleanup
    /// creates/verifies replacements before erasing generated source segments.
    /// </summary>
    public sealed class September11FieldCompletionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ROADCENTRECLEAN", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CleanRoadCentres()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            September11FieldCompletionRuntime.CleanExistingRoadCentres(document);
        }

        [CommandMethod("CE_TOOLS", "CE_GOOGLEEARTHLINEWORK", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void GoogleEarthLinework()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            ExportMultipleLineworkToGoogleEarth(document);
        }

        [CommandMethod("CE_TOOLS", "CE_HATCHBOUNDARIES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void HatchBoundaries()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            CreateSeparateHatchBoundaries(document);
        }

        [CommandMethod("CE_TOOLS", "CE_SEWRECALC", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SewerRecalculateSurfaceRules()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            RecalculateSewerSurfaceRules(document, civilDocument);
        }

        [CommandMethod("CE_TOOLS", "CE_SURVEYLOCATIONNAMIBIA", CommandFlags.Modal | CommandFlags.Redraw)]
        public void NamibiaTownCoordinateSystem()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            // Keep one canonical town -> LO workflow.  CE_SURVEYLOCATION already
            // contains the full Namibia town catalogue (Windhoek -> LO17, coastal
            // towns -> LO15, Katima Mulilo -> LO25, Opuwo -> LO13, etc.).
            document.SendStringToExecute("CE_SURVEYLOCATION ", true, false, true);
        }

        private static void ExportMultipleLineworkToGoogleEarth(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                selection = editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect MULTIPLE separate open lines/polylines/feature lines to display in Google Earth: ",
                    MessageForRemoval = "\nRemove linework from Google Earth export: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            }
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
                return;

            var paths = new List<GoogleEarthPath>();
            int rejected = 0;
            string lastError = string.Empty;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    Entity entity = null;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { }
                    if (entity == null)
                    {
                        rejected++;
                        continue;
                    }

                    List<Point3d> drawingPoints = ReadLineworkPoints(entity, transaction);
                    if (drawingPoints.Count < 2)
                    {
                        rejected++;
                        continue;
                    }

                    var geographic = new List<Point3d>();
                    bool ok = true;
                    foreach (Point3d drawingPoint in drawingPoints)
                    {
                        Point3d geo;
                        string error;
                        if (!NamibiaCoordinateRuntime.TryDrawingToWgs84(
                                document.Database,
                                drawingPoint,
                                out geo,
                                out error))
                        {
                            lastError = error;
                            ok = false;
                            break;
                        }
                        geographic.Add(geo);
                    }
                    if (!ok || geographic.Count < 2)
                    {
                        rejected++;
                        continue;
                    }

                    paths.Add(new GoogleEarthPath(
                        LineworkName(entity),
                        geographic));
                }
            }

            if (paths.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_GOOGLEEARTHLINEWORK stopped. No selected linework could be transformed to WGS84. {0} Run CE_SURVEYLOCATION (town -> Namibia LO) if the drawing coordinate system is not assigned.",
                    string.IsNullOrWhiteSpace(lastError) ? string.Empty : lastError);
                return;
            }

            string folder = Path.Combine(Path.GetTempPath(), "CE Tools", "Google Earth");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(
                folder,
                "CE_Multiple_Linework_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".kml");
            File.WriteAllText(file, BuildLineworkKml(paths), new UTF8Encoding(false));

            bool opened = false;
            try
            {
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
                opened = true;
            }
            catch { }

            editor.WriteMessage(
                "\nCE_GOOGLEEARTHLINEWORK complete. Separate line strings={0}; rejected={1}; Google Earth launched={2}; KML={3}",
                paths.Count,
                rejected,
                opened ? "Yes" : "No - open the KML manually",
                file);
        }

        private static List<Point3d> ReadLineworkPoints(Entity entity, Transaction transaction)
        {
            var result = new List<Point3d>();

            Line line = entity as Line;
            if (line != null)
            {
                result.Add(line.StartPoint);
                result.Add(line.EndPoint);
                return result;
            }

            Polyline polyline = entity as Polyline;
            if (polyline != null)
            {
                int segmentCount = polyline.Closed
                    ? polyline.NumberOfVertices
                    : Math.Max(0, polyline.NumberOfVertices - 1);
                if (polyline.NumberOfVertices > 0)
                    result.Add(polyline.GetPoint3dAt(0));

                for (int index = 0; index < segmentCount; index++)
                {
                    int next = (index + 1) % polyline.NumberOfVertices;
                    if (polyline.GetSegmentType(index) == SegmentType.Arc)
                    {
                        try
                        {
                            CircularArc2d arc = polyline.GetArcSegment2dAt(index);
                            double delta = arc.EndAngle - arc.StartAngle;
                            if (!arc.IsClockWise)
                            {
                                while (delta <= 0.0) delta += Math.PI * 2.0;
                            }
                            else
                            {
                                while (delta >= 0.0) delta -= Math.PI * 2.0;
                            }
                            int divisions = Math.Max(2, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 18.0)));
                            for (int step = 1; step < divisions; step++)
                            {
                                double angle = arc.StartAngle + delta * step / divisions;
                                result.Add(new Point3d(
                                    arc.Center.X + arc.Radius * Math.Cos(angle),
                                    arc.Center.Y + arc.Radius * Math.Sin(angle),
                                    polyline.Elevation));
                            }
                        }
                        catch { }
                    }
                    result.Add(polyline.GetPoint3dAt(next));
                }
                return RemoveConsecutiveDuplicates(result);
            }

            Polyline2d polyline2d = entity as Polyline2d;
            if (polyline2d != null)
            {
                foreach (ObjectId vertexId in polyline2d)
                {
                    Vertex2d vertex = transaction.GetObject(vertexId, OpenMode.ForRead, false) as Vertex2d;
                    if (vertex != null) result.Add(vertex.Position);
                }
                if (polyline2d.Closed && result.Count > 1) result.Add(result[0]);
                return RemoveConsecutiveDuplicates(result);
            }

            Polyline3d polyline3d = entity as Polyline3d;
            if (polyline3d != null)
            {
                foreach (ObjectId vertexId in polyline3d)
                {
                    PolylineVertex3d vertex = transaction.GetObject(vertexId, OpenMode.ForRead, false) as PolylineVertex3d;
                    if (vertex != null) result.Add(vertex.Position);
                }
                if (polyline3d.Closed && result.Count > 1) result.Add(result[0]);
                return RemoveConsecutiveDuplicates(result);
            }

            CivilFeatureLine featureLine = entity as CivilFeatureLine;
            if (featureLine != null)
            {
                Point3dCollection points = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                if (points != null)
                    foreach (Point3d point in points) result.Add(point);
                return RemoveConsecutiveDuplicates(result);
            }

            // Ordinary AutoCAD curve fallback for arcs/splines that are useful as
            // Google Earth linework.  Sampling is read-only and never alters source geometry.
            Curve curve = entity as Curve;
            if (curve != null)
            {
                try
                {
                    double start = curve.StartParam;
                    double end = curve.EndParam;
                    int divisions = 48;
                    for (int index = 0; index <= divisions; index++)
                    {
                        double parameter = start + (end - start) * index / divisions;
                        result.Add(curve.GetPointAtParameter(parameter));
                    }
                }
                catch { }
            }
            return RemoveConsecutiveDuplicates(result);
        }

        private static List<Point3d> RemoveConsecutiveDuplicates(IEnumerable<Point3d> source)
        {
            var result = new List<Point3d>();
            foreach (Point3d point in source ?? Enumerable.Empty<Point3d>())
            {
                if (result.Count == 0 || result[result.Count - 1].DistanceTo(point) > 1e-9)
                    result.Add(point);
            }
            return result;
        }

        private static string LineworkName(Entity entity)
        {
            string layer = string.Empty;
            try { layer = entity.Layer; } catch { }
            string handle = string.Empty;
            try { handle = entity.Handle.ToString(); } catch { }
            if (string.IsNullOrWhiteSpace(layer)) layer = "CE Linework";
            return string.IsNullOrWhiteSpace(handle) ? layer : layer + " - " + handle;
        }

        private static string BuildLineworkKml(IEnumerable<GoogleEarthPath> paths)
        {
            var builder = new StringBuilder();
            builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            builder.AppendLine("<kml xmlns=\"http://www.opengis.net/kml/2.2\">");
            builder.AppendLine("  <Document>");
            builder.AppendLine("    <name>CE Tools - Multiple Linework</name>");
            foreach (GoogleEarthPath path in paths)
            {
                builder.AppendLine("    <Placemark>");
                builder.Append("      <name>").Append(SecurityElement.Escape(path.Name) ?? string.Empty).AppendLine("</name>");
                builder.AppendLine("      <LineString>");
                builder.AppendLine("        <tessellate>1</tessellate>");
                builder.AppendLine("        <altitudeMode>clampToGround</altitudeMode>");
                builder.AppendLine("        <coordinates>");
                foreach (Point3d point in path.Points)
                {
                    builder.Append("          ")
                        .Append(point.X.ToString("0.##########", CultureInfo.InvariantCulture))
                        .Append(',')
                        .Append(point.Y.ToString("0.##########", CultureInfo.InvariantCulture))
                        .AppendLine(",0");
                }
                builder.AppendLine("        </coordinates>");
                builder.AppendLine("      </LineString>");
                builder.AppendLine("    </Placemark>");
            }
            builder.AppendLine("  </Document>");
            builder.AppendLine("</kml>");
            return builder.ToString();
        }

        private static void CreateSeparateHatchBoundaries(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                selection = editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect hatches. Each hatch loop will become its OWN closed boundary polyline, including shared edges between adjacent/touching hatches: ",
                    MessageForRemoval = "\nRemove hatches from boundary creation: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                },
                new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "HATCH") }));
            }
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
                return;

            int created = 0;
            int skippedLoops = 0;
            int hatchCount = 0;
            var newIds = new List<ObjectId>();

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId layerId = GetOrCreateBoundaryLayer(document.Database, transaction);
                foreach (ObjectId hatchId in selection.Value.GetObjectIds().Distinct())
                {
                    Hatch hatch = null;
                    try { hatch = transaction.GetObject(hatchId, OpenMode.ForRead, false) as Hatch; }
                    catch { }
                    if (hatch == null) continue;
                    hatchCount++;

                    BlockTableRecord owner = transaction.GetObject(hatch.OwnerId, OpenMode.ForWrite, false) as BlockTableRecord;
                    if (owner == null) continue;

                    for (int loopIndex = 0; loopIndex < hatch.NumberOfLoops; loopIndex++)
                    {
                        HatchLoop loop;
                        try { loop = hatch.GetLoopAt(loopIndex); }
                        catch { skippedLoops++; continue; }

                        if (loop == null || !loop.IsPolyline || loop.Polyline == null || loop.Polyline.Count < 2)
                        {
                            skippedLoops++;
                            continue;
                        }

                        var vertices = new List<BulgeVertex>();
                        foreach (BulgeVertex vertex in loop.Polyline) vertices.Add(vertex);
                        if (vertices.Count > 2 &&
                            vertices[0].Vertex.GetDistanceTo(vertices[vertices.Count - 1].Vertex) <= 1e-9)
                            vertices.RemoveAt(vertices.Count - 1);
                        if (vertices.Count < 2)
                        {
                            skippedLoops++;
                            continue;
                        }

                        var boundary = new Polyline(vertices.Count);
                        boundary.SetDatabaseDefaults(document.Database);
                        boundary.LayerId = layerId;
                        boundary.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                        boundary.Elevation = hatch.Elevation;
                        for (int index = 0; index < vertices.Count; index++)
                        {
                            boundary.AddVertexAt(
                                index,
                                vertices[index].Vertex,
                                vertices[index].Bulge,
                                0.0,
                                0.0);
                        }
                        boundary.Closed = true;
                        owner.AppendEntity(boundary);
                        transaction.AddNewlyCreatedDBObject(boundary, true);
                        newIds.Add(boundary.ObjectId);
                        created++;
                    }
                }
                transaction.Commit();
            }

            if (newIds.Count > 0)
            {
                try { editor.SetImpliedSelection(newIds.ToArray()); } catch { }
            }
            editor.Regen();
            editor.WriteMessage(
                "\nCE_HATCHBOUNDARIES complete. Hatches={0}; separate closed boundaries created={1}; unsupported non-polyline loops skipped={2}. Adjacent/touching hatches remain separate, so their shared boundary line is retained.",
                hatchCount,
                created,
                skippedLoops);
        }

        private static ObjectId GetOrCreateBoundaryLayer(Database database, Transaction transaction)
        {
            const string layerName = "CE-HATCH-BOUNDARY";
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers.Has(layerName)) return layers[layerName];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = layerName,
                Color = Color.FromColorIndex(ColorMethod.ByAci, 7)
            };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static void RecalculateSewerSurfaceRules(Document document, CivilDocument civilDocument)
        {
            Editor editor = document.Editor;
            PromptEntityOptions options = new PromptEntityOptions(
                "\nSelect one sewer pipe or structure from the gravity network to recalculate: ");
            PromptEntityResult selected = editor.GetEntity(options);
            if (selected.Status != PromptStatus.OK) return;

            ObjectId networkId = ObjectId.Null;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                DBObject value = transaction.GetObject(selected.ObjectId, OpenMode.ForRead, false);
                CivilPipe pipe = value as CivilPipe;
                CivilStructure structure = value as CivilStructure;
                networkId = pipe != null ? pipe.NetworkId : structure != null ? structure.NetworkId : ObjectId.Null;
            }
            if (networkId.IsNull)
            {
                editor.WriteMessage("\nCE_SEWRECALC: select a Civil 3D gravity-network pipe or structure.");
                return;
            }

            List<NamedId> surfaces = ReadNamedObjects(
                document.Database,
                civilDocument.GetSurfaceIds(),
                delegate(DBObject value) { return (value as CivilSurface)?.Name; });
            if (surfaces.Count == 0)
            {
                editor.WriteMessage("\nCE_SEWRECALC: no Civil 3D surface exists in this drawing.");
                return;
            }

            List<NamedId> pipeRules = ReadNamedStyles(
                document.Database,
                civilDocument.Styles.PipeRuleSetStyles.Cast<ObjectId>());
            List<NamedId> structureRules = ReadNamedStyles(
                document.Database,
                civilDocument.Styles.StructureRuleSetStyles.Cast<ObjectId>());

            string preferredSurface = PreferredName(surfaces, new[] { "design", "finished", "ground", "ng", "surface" });
            string[] pipeChoices = StyleChoices(pipeRules);
            string[] structureChoices = StyleChoices(structureRules);

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Sewer Surface / Rules Recalculation",
                "Re-links EVERY editable pipe and structure in the selected gravity network to one surface, assigns the chosen rule sets, then applies Civil 3D rules in one committed transaction. Profiles are queued only AFTER that transaction has finished.");
            model.AddChoice("Surface", "01 Surface", "Reference surface", preferredSurface,
                "Surface assigned to every pipe and structure before Civil 3D rules are evaluated.",
                surfaces.Select(item => item.Name).ToArray());
            model.AddChoice("PipeRules", "02 Rules", "Pipe rule set", pipeChoices[0],
                "Choose a named Civil 3D pipe rule set, or keep the current/default rule set.", pipeChoices);
            model.AddChoice("StructureRules", "02 Rules", "Structure rule set", structureChoices[0],
                "Choose a named Civil 3D structure rule set, or keep the current/default rule set.", structureChoices);
            model.AddChoice("After", "03 Profiles", "After recalculation", "Return to drawing",
                "For profile stability, CE_SEWPROFILE is queued only after the network surface/rule transaction has committed.",
                new[] { "Return to drawing", "Open CE_SEWPROFILE after commit" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            NamedId surface = FindNamed(surfaces, model.Text("Surface"));
            if (surface == null || surface.Id.IsNull) return;
            ObjectId pipeRuleId = ResolveStyle(pipeRules, model.Text("PipeRules"));
            ObjectId structureRuleId = ResolveStyle(structureRules, model.Text("StructureRules"));

            int pipes;
            int structures;
            int ruleFailures;
            string error;
            bool linked = September10SewerAuditRuntime.LinkExistingPartsToSurface(
                document,
                networkId,
                surface.Id,
                pipeRuleId,
                structureRuleId,
                out pipes,
                out structures,
                out ruleFailures,
                out error);
            if (!linked)
            {
                editor.WriteMessage("\nCE_SEWRECALC failed. No partial surface/rule recalculation was committed. {0}", error);
                return;
            }

            editor.Regen();
            editor.WriteMessage(
                "\nCE_SEWRECALC complete. Surface='{0}'; pipes recalculated={1}; structures recalculated={2}; rule failures={3}. Surface/rule writes are committed before any profile command starts.",
                surface.Name,
                pipes,
                structures,
                ruleFailures);

            if (string.Equals(model.Text("After"), "Open CE_SEWPROFILE after commit", StringComparison.OrdinalIgnoreCase))
            {
                // Critical stability boundary: queue the profile command instead of
                // entering Civil profile creation from inside the network write path.
                document.SendStringToExecute("CE_SEWPROFILE ", true, false, true);
            }
        }

        private static List<NamedId> ReadNamedObjects(
            Database database,
            IEnumerable<ObjectId> ids,
            Func<DBObject, string> nameReader)
        {
            var result = new List<NamedId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids ?? Enumerable.Empty<ObjectId>())
                {
                    try
                    {
                        DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                        string name = nameReader(value);
                        if (!string.IsNullOrWhiteSpace(name)) result.Add(new NamedId(id, name));
                    }
                    catch { }
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static List<NamedId> ReadNamedStyles(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<NamedId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids ?? Enumerable.Empty<ObjectId>())
                {
                    try
                    {
                        StyleBase style = transaction.GetObject(id, OpenMode.ForRead, false) as StyleBase;
                        if (style != null && !string.IsNullOrWhiteSpace(style.Name))
                            result.Add(new NamedId(id, style.Name));
                    }
                    catch { }
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static string[] StyleChoices(IList<NamedId> styles)
        {
            var values = new List<string> { "<Keep current/default rule set>" };
            if (styles != null) values.AddRange(styles.Select(item => item.Name));
            return values.ToArray();
        }

        private static ObjectId ResolveStyle(IList<NamedId> styles, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("<", StringComparison.Ordinal))
                return ObjectId.Null;
            NamedId value = FindNamed(styles, name);
            return value == null ? ObjectId.Null : value.Id;
        }

        private static NamedId FindNamed(IEnumerable<NamedId> values, string name)
        {
            return (values ?? Enumerable.Empty<NamedId>()).FirstOrDefault(
                item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static string PreferredName(IList<NamedId> values, IEnumerable<string> keywords)
        {
            foreach (string keyword in keywords ?? Enumerable.Empty<string>())
            {
                NamedId value = values.FirstOrDefault(
                    item => item.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
                if (value != null) return value.Name;
            }
            return values.Count == 0 ? string.Empty : values[0].Name;
        }

        private sealed class NamedId
        {
            internal NamedId(ObjectId id, string name)
            {
                Id = id;
                Name = name ?? string.Empty;
            }
            internal ObjectId Id;
            internal string Name;
        }

        private sealed class GoogleEarthPath
        {
            internal GoogleEarthPath(string name, IList<Point3d> points)
            {
                Name = name ?? string.Empty;
                Points = points == null ? new List<Point3d>() : new List<Point3d>(points);
            }
            internal string Name;
            internal IList<Point3d> Points;
        }
    }

    internal static class September11FieldCompletionRuntime
    {
        private const string RoadCentreLayer = "CE-ROAD-CENTRELINE";
        private const double JoinTolerance = 0.01;
        private const double CollinearAngleDegrees = 0.5;

        internal static void RoadReserveCentrePolylines(Document document)
        {
            if (document == null) return;
            HashSet<ObjectId> before = ReadRoadCentrePolylineIds(document.Database);
            September09FieldEngineeringRuntime.RoadReserveCentrePolylines(document);
            HashSet<ObjectId> after = ReadRoadCentrePolylineIds(document.Database);
            after.ExceptWith(before);
            if (after.Count > 0)
                CleanRoadCentreIds(document, after.ToList(), "newly generated road centres");
        }

        internal static void CleanExistingRoadCentres(Document document)
        {
            if (document == null) return;
            List<ObjectId> ids = SelectedPolylines(document);
            if (ids.Count == 0)
                ids = ReadRoadCentrePolylineIds(document.Database).ToList();
            if (ids.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_ROADCENTRECLEAN: select open road-centre polylines or create road-reserve centres first.");
                return;
            }
            CleanRoadCentreIds(document, ids, "selected/existing road centres");
        }

        private static HashSet<ObjectId> ReadRoadCentrePolylineIds(Database database)
        {
            var ids = new HashSet<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return ids;
                foreach (ObjectId id in space)
                {
                    Polyline polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                    if (polyline != null &&
                        string.Equals(polyline.Layer, RoadCentreLayer, StringComparison.OrdinalIgnoreCase))
                        ids.Add(id);
                }
            }
            return ids;
        }

        private static List<ObjectId> SelectedPolylines(Document document)
        {
            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
                return new List<ObjectId>();

            var result = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    Polyline polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                    if (polyline != null && polyline.NumberOfVertices >= 2) result.Add(id);
                }
            }
            return result;
        }

        private static void CleanRoadCentreIds(Document document, IList<ObjectId> ids, string label)
        {
            var edges = new List<RoadEdge>();
            var processedSources = new HashSet<ObjectId>();
            ObjectId layerId = ObjectId.Null;
            int skippedCurved = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids.Distinct())
                {
                    Polyline polyline = null;
                    try { polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch { }
                    if (polyline == null || polyline.NumberOfVertices < 2) continue;

                    bool hasBulges = false;
                    int segmentCount = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
                    for (int index = 0; index < segmentCount; index++)
                    {
                        if (Math.Abs(polyline.GetBulgeAt(index)) > 1e-10)
                        {
                            hasBulges = true;
                            break;
                        }
                    }
                    if (hasBulges)
                    {
                        skippedCurved++;
                        continue;
                    }

                    if (layerId.IsNull) layerId = polyline.LayerId;
                    processedSources.Add(id);
                    for (int index = 0; index < segmentCount; index++)
                    {
                        Point2d a = polyline.GetPoint2dAt(index);
                        Point2d b = polyline.GetPoint2dAt((index + 1) % polyline.NumberOfVertices);
                        if (a.GetDistanceTo(b) <= 1e-9) continue;
                        edges.Add(new RoadEdge(a, b));
                    }
                }
            }

            if (edges.Count == 0 || processedSources.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_ROADCENTRECLEAN: no straight road-centre segments were available; curved polylines skipped={0}.", skippedCurved);
                return;
            }

            List<List<Point2d>> chains = BuildChains(edges);
            var cleaned = chains
                .Select(SimplifyChain)
                .Where(points => points.Count >= 2)
                .ToList();
            if (cleaned.Count == 0) return;

            var replacements = new List<ObjectId>();
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                    if (space == null) return;

                    foreach (List<Point2d> points in cleaned)
                    {
                        bool closed = points.Count > 2 && points[0].GetDistanceTo(points[points.Count - 1]) <= JoinTolerance;
                        int count = closed ? points.Count - 1 : points.Count;
                        if (count < 2) continue;

                        var polyline = new Polyline(count);
                        polyline.SetDatabaseDefaults(document.Database);
                        if (!layerId.IsNull) polyline.LayerId = layerId;
                        polyline.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                        polyline.Elevation = 0.0;
                        for (int index = 0; index < count; index++)
                            polyline.AddVertexAt(index, points[index], 0.0, 0.0, 0.0);
                        polyline.Closed = closed;
                        space.AppendEntity(polyline);
                        transaction.AddNewlyCreatedDBObject(polyline, true);
                        replacements.Add(polyline.ObjectId);
                    }
                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_ROADCENTRECLEAN stopped before source erase. Original linework was kept. {0}", exception.Message);
                return;
            }

            if (!VerifyReplacements(document.Database, replacements, cleaned.Count))
            {
                document.Editor.WriteMessage("\nCE_ROADCENTRECLEAN verification failed. Original linework was kept; replacement candidates remain for review.");
                return;
            }

            int erased = 0;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in processedSources)
                    {
                        try
                        {
                            Entity source = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                            if (source != null && !source.IsErased)
                            {
                                source.Erase();
                                erased++;
                            }
                        }
                        catch { }
                    }
                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_ROADCENTRECLEAN created verified replacements but could not erase every old segment. No source was erased outside the committed cleanup transaction. {0}", exception.Message);
            }

            try { document.Editor.SetImpliedSelection(replacements.ToArray()); } catch { }
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_ROADCENTRECLEAN complete for {0}. Old polylines processed={1}; joined chains={2}; old polylines erased={3}; curved polylines kept={4}. Straight intermediate vertices are removed; bend and T/X junction vertices are retained.",
                label,
                processedSources.Count,
                replacements.Count,
                erased,
                skippedCurved);
        }

        private static bool VerifyReplacements(Database database, IList<ObjectId> ids, int expected)
        {
            if (ids == null || ids.Count != expected || ids.Count == 0) return false;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Polyline polyline = null;
                    try { polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch { }
                    if (polyline == null || polyline.IsErased || polyline.NumberOfVertices < 2)
                        return false;
                }
            }
            return true;
        }

        private static List<List<Point2d>> BuildChains(IList<RoadEdge> edges)
        {
            var nodes = new Dictionary<string, RoadNode>(StringComparer.Ordinal);
            var adjacency = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int index = 0; index < edges.Count; index++)
            {
                RoadEdge edge = edges[index];
                edge.AKey = NodeKey(edge.A);
                edge.BKey = NodeKey(edge.B);
                AccumulateNode(nodes, edge.AKey, edge.A);
                AccumulateNode(nodes, edge.BKey, edge.B);
                AddAdjacency(adjacency, edge.AKey, index);
                AddAdjacency(adjacency, edge.BKey, index);
            }

            bool[] used = new bool[edges.Count];
            var chains = new List<List<Point2d>>();
            foreach (KeyValuePair<string, List<int>> pair in adjacency)
            {
                if (pair.Value.Count == 2) continue;
                foreach (int edgeIndex in pair.Value)
                {
                    if (used[edgeIndex]) continue;
                    chains.Add(TraceChain(pair.Key, edgeIndex, edges, nodes, adjacency, used));
                }
            }

            for (int edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
            {
                if (used[edgeIndex]) continue;
                chains.Add(TraceChain(edges[edgeIndex].AKey, edgeIndex, edges, nodes, adjacency, used));
            }
            return chains;
        }

        private static List<Point2d> TraceChain(
            string startKey,
            int firstEdge,
            IList<RoadEdge> edges,
            IDictionary<string, RoadNode> nodes,
            IDictionary<string, List<int>> adjacency,
            bool[] used)
        {
            var result = new List<Point2d> { nodes[startKey].Point };
            string current = startKey;
            int edgeIndex = firstEdge;
            int guard = 0;
            while (edgeIndex >= 0 && edgeIndex < edges.Count && guard++ < edges.Count + 5)
            {
                if (used[edgeIndex]) break;
                used[edgeIndex] = true;
                RoadEdge edge = edges[edgeIndex];
                string next = string.Equals(edge.AKey, current, StringComparison.Ordinal) ? edge.BKey : edge.AKey;
                result.Add(nodes[next].Point);

                List<int> touching;
                if (!adjacency.TryGetValue(next, out touching) || touching.Count != 2)
                    break;

                int candidate = touching[0] == edgeIndex ? touching[1] : touching[0];
                if (used[candidate]) break;
                current = next;
                edgeIndex = candidate;
            }
            return result;
        }

        private static List<Point2d> SimplifyChain(IList<Point2d> source)
        {
            var points = new List<Point2d>();
            foreach (Point2d point in source ?? new Point2d[0])
            {
                if (points.Count == 0 || points[points.Count - 1].GetDistanceTo(point) > 1e-9)
                    points.Add(point);
            }
            if (points.Count < 3) return points;

            bool changed = true;
            double sinTolerance = Math.Sin(CollinearAngleDegrees * Math.PI / 180.0);
            while (changed && points.Count >= 3)
            {
                changed = false;
                for (int index = 1; index < points.Count - 1; index++)
                {
                    Vector2d a = points[index] - points[index - 1];
                    Vector2d b = points[index + 1] - points[index];
                    if (a.Length <= 1e-9 || b.Length <= 1e-9)
                    {
                        points.RemoveAt(index);
                        changed = true;
                        break;
                    }
                    double cross = Math.Abs(a.X * b.Y - a.Y * b.X) / (a.Length * b.Length);
                    double dot = a.DotProduct(b);
                    if (cross <= sinTolerance && dot >= 0.0)
                    {
                        points.RemoveAt(index);
                        changed = true;
                        break;
                    }
                }
            }
            return points;
        }

        private static string NodeKey(Point2d point)
        {
            long x = (long)Math.Round(point.X / JoinTolerance, MidpointRounding.AwayFromZero);
            long y = (long)Math.Round(point.Y / JoinTolerance, MidpointRounding.AwayFromZero);
            return x.ToString(CultureInfo.InvariantCulture) + ":" + y.ToString(CultureInfo.InvariantCulture);
        }

        private static void AccumulateNode(IDictionary<string, RoadNode> nodes, string key, Point2d point)
        {
            RoadNode value;
            if (!nodes.TryGetValue(key, out value))
            {
                nodes[key] = new RoadNode(point);
                return;
            }
            value.Add(point);
        }

        private static void AddAdjacency(IDictionary<string, List<int>> adjacency, string key, int edgeIndex)
        {
            List<int> values;
            if (!adjacency.TryGetValue(key, out values))
            {
                values = new List<int>();
                adjacency[key] = values;
            }
            values.Add(edgeIndex);
        }

        private sealed class RoadEdge
        {
            internal RoadEdge(Point2d a, Point2d b)
            {
                A = a;
                B = b;
            }
            internal Point2d A;
            internal Point2d B;
            internal string AKey;
            internal string BKey;
        }

        private sealed class RoadNode
        {
            private double _sumX;
            private double _sumY;
            private int _count;
            internal RoadNode(Point2d point) { Add(point); }
            internal void Add(Point2d point)
            {
                _sumX += point.X;
                _sumY += point.Y;
                _count++;
            }
            internal Point2d Point
            {
                get { return new Point2d(_sumX / Math.Max(1, _count), _sumY / Math.Max(1, _count)); }
            }
        }
    }
}
