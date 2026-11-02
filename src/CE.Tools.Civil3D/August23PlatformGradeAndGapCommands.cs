using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

[assembly: CommandClass(typeof(CETools.Civil3D.August23PlatformGradeAndGapCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Fixed-endpoint platform helpers requested for the August 23 production pass.
    /// Constant-grade keeps each feature line's first/last endpoint levels unchanged.
    /// Gap joining creates a new verified feature line and never moves source pieces.
    /// </summary>
    public sealed class August23PlatformGradeAndGapCommands
    {
        private const double Tolerance = 0.0000001;

        [CommandMethod("CE_TOOLS", "CE_PLATFORMCONSTANTGRADE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ConstantGradeBetweenEndpoints()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            PromptSelectionResult selection = SelectFeatureLines(
                document.Editor,
                "\nSelect multiple open feature lines for constant grade between their fixed endpoints: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            int changed = 0;
            int skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Where(value => !value.IsNull).Distinct())
                {
                    CivilFeatureLine featureLine = OpenFeatureLine(transaction, id, OpenMode.ForWrite);
                    if (!Editable(featureLine, transaction) || featureLine.Closed)
                    {
                        skipped++;
                        continue;
                    }

                    Point3dCollection collection = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                    if (collection == null || collection.Count < 2)
                    {
                        skipped++;
                        continue;
                    }

                    List<Point3d> points = collection.Cast<Point3d>().ToList();
                    var chainage = new double[points.Count];
                    double total = 0.0;
                    for (int index = 1; index < points.Count; index++)
                    {
                        double dx = points[index].X - points[index - 1].X;
                        double dy = points[index].Y - points[index - 1].Y;
                        total += Math.Sqrt(dx * dx + dy * dy);
                        chainage[index] = total;
                    }
                    if (total <= Tolerance)
                    {
                        skipped++;
                        continue;
                    }

                    double startElevation = points[0].Z;
                    double endElevation = points[points.Count - 1].Z;
                    for (int index = 1; index < points.Count - 1; index++)
                    {
                        double elevation = startElevation +
                            (endElevation - startElevation) * (chainage[index] / total);
                        SetAbsoluteElevation(featureLine, points[index], index, elevation);
                    }
                    try { featureLine.RecordGraphicsModified(true); } catch { }
                    changed++;
                }
                transaction.Commit();
            }

            try { document.Editor.Regen(); } catch { }
            August21GraphicsRefreshManager.MarkDirty();
            PlatformDynamicRefreshManager.Queue();
            document.Editor.WriteMessage(
                "\nCE_PLATFORMCONSTANTGRADE complete. Graded={0}; skipped={1}. First/last endpoint elevations were kept fixed.",
                changed,
                skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMCLOSEGAPS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CloseSteppedFeatureLineGaps()
        {
            PlatformDynamicRefreshManager.EnsureInitialized();
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Close Stepped Feature-Line Gaps",
                "Join multiple open stepped feature-line pieces into one new feature line. The selected source endpoints stay exactly where they are; accepted gaps are bridged by new segments.");
            settings.AddPositiveDouble(
                "Gap", "Join", "Maximum gap tolerance",
                0.050,
                "Maximum accepted endpoint-to-endpoint gap between consecutive source pieces.");
            settings.AddText(
                "Name", "Join", "Output feature-line name",
                "CE-STEPPED-FL",
                "A unique suffix is added automatically if the requested name already exists.");
            settings.AddChoice("Output", "02 Output", "Joined output type", "Feature Line",
                "Make a new Civil 3D feature line or a 3D polyline.", new[] { "Feature Line", "Polyline" });
            settings.AddChoice("Sources", "02 Output", "Selected source objects", "Keep all",
                "Delete selected sources only after an explicit confirmation and a successfully created joined output.",
                new[] { "Keep all", "Remove selected after join" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            PromptSelectionResult selection = SelectFeatureLines(
                document.Editor,
                "\nSelect two or more open stepped feature-line pieces to join: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            IList<ObjectId> ids = selection.Value.GetObjectIds()
                .Where(value => !value.IsNull)
                .Distinct()
                .ToList();
            if (ids.Count < 2)
            {
                document.Editor.WriteMessage("\nCE_PLATFORMCLOSEGAPS cancelled. Select at least two feature-line pieces.");
                return;
            }

            int pieceCount;
            int vertexCount;
            double largestGap;
            string outputName;
            try
            {
                bool deleteSources = settings.Text("Sources") == "Remove selected after join";
                if (deleteSources && !DisciplineWorkflowDialogs.Confirm(
                    "CE Tools - Remove Source Geometry",
                    "This will DELETE the selected source feature lines/polylines after the new joined output has been generated. Continue?"))
                    return;
                if (settings.Text("Output") == "Feature Line" && !deleteSources &&
                    AllSourceFeatureLines(document.Database, ids))
                {
                    // Preserve established Civil 3D join behavior for the legacy workflow.
                    August21PlatformRelativeFatalSafety.CreateJoinedFeatureLine(
                        document, ids, Math.Max(0.000001, settings.Double("Gap", 0.050)),
                        SafeName(settings.Text("Name"), "CE-STEPPED-FL"),
                        out pieceCount, out vertexCount, out largestGap, out outputName);
                }
                else
                {
                    JoinSelectedSteppedGeometry(document, ids,
                        Math.Max(0.000001, settings.Double("Gap", 0.050)),
                        SafeName(settings.Text("Name"), "CE-STEPPED-FL"),
                        settings.Text("Output") == "Feature Line", deleteSources,
                        out pieceCount, out vertexCount, out largestGap, out outputName);
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_PLATFORMCLOSEGAPS stopped safely. " + exception.Message);
                return;
            }

            try { document.Editor.Regen(); } catch { }
            August21GraphicsRefreshManager.MarkDirty();
            PlatformDynamicRefreshManager.Queue();
            document.Editor.WriteMessage(
                "\nCE_PLATFORMCLOSEGAPS complete. Pieces={0}; vertices={1}; largest bridged gap={2:N3}; output='{3}'. Source feature lines were not moved.",
                pieceCount,
                vertexCount,
                largestGap,
                outputName);
        }

        private static bool AllSourceFeatureLines(Database database, IList<ObjectId> ids)
        {
            using (Transaction tr = database.TransactionManager.StartTransaction())
                return ids.All(id => tr.GetObject(id, OpenMode.ForRead, false) is CivilFeatureLine);
        }

        private static void JoinSelectedSteppedGeometry(Document document, IList<ObjectId> ids,
            double tolerance, string name, bool featureLineOutput, bool deleteSources,
            out int pieceCount, out int vertexCount, out double largestGap, out string outputName)
        {
            pieceCount = 0; vertexCount = 0; largestGap = 0; outputName = name;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                var pieces = new List<List<Point3d>>();
                foreach (ObjectId id in ids)
                {
                    Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || entity.IsErased) throw new InvalidOperationException("Source geometry unavailable.");
                    var points = new List<Point3d>();
                    var fl = entity as CivilFeatureLine;
                    var pl = entity as Polyline;
                    var pl3 = entity as Polyline3d;
                    if (fl != null)
                    {
                        if (fl.IsReferenceObject || fl.Closed)
                            throw new InvalidOperationException("Reference or closed feature lines cannot be joined.");
                        points.AddRange(fl.GetPoints(FeatureLinePointType.AllPoints).Cast<Point3d>());
                    }
                    else if (pl != null)
                    {
                        if (pl.Closed) throw new InvalidOperationException("Closed polylines cannot be joined.");
                        for (int i = 0; i < pl.NumberOfVertices; i++) points.Add(pl.GetPoint3dAt(i));
                    }
                    else if (pl3 != null)
                    {
                        if (pl3.Closed) throw new InvalidOperationException("Closed 3D polylines cannot be joined.");
                        foreach (ObjectId vertex in pl3)
                        {
                            PolylineVertex3d v = tr.GetObject(vertex, OpenMode.ForRead, false) as PolylineVertex3d;
                            if (v != null) points.Add(v.Position);
                        }
                    }
                    else throw new InvalidOperationException("Select only feature lines or open 2D/3D polylines.");
                    if (points.Count < 2) throw new InvalidOperationException("A source needs at least two vertices.");
                    pieces.Add(points);
                }
                var ordered = new List<Point3d>(pieces[0]);
                var remaining = pieces.Skip(1).ToList();
                while (remaining.Count > 0)
                {
                    double best = double.PositiveInfinity;
                    List<Point3d> selected = null;
                    bool reverse = false;
                    foreach (var candidate in remaining)
                    {
                        double startGap = ordered[ordered.Count - 1].DistanceTo(candidate[0]);
                        double endGap = ordered[ordered.Count - 1].DistanceTo(candidate[candidate.Count - 1]);
                        if (startGap < best) { best = startGap; selected = candidate; reverse = false; }
                        if (endGap < best) { best = endGap; selected = candidate; reverse = true; }
                    }
                    if (best > tolerance) throw new InvalidOperationException(
                        "A selected endpoint gap exceeds the maximum tolerance: " +
                        best.ToString("0.###", CultureInfo.InvariantCulture));
                    largestGap = Math.Max(largestGap, best);
                    var next = reverse ? selected.AsEnumerable().Reverse().ToList() : selected;
                    ordered.AddRange(next.Skip(best <= 1e-7 ? 1 : 0));
                    remaining.Remove(selected);
                }
                BlockTable table = tr.GetObject(document.Database.BlockTableId, OpenMode.ForRead) as BlockTable;
                BlockTableRecord space = tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;
                var joined = new Polyline3d(Poly3dType.SimplePoly, new Point3dCollection(ordered.ToArray()), false);
                joined.SetDatabaseDefaults(document.Database);
                space.AppendEntity(joined);
                tr.AddNewlyCreatedDBObject(joined, true);
                if (featureLineOutput)
                {
                    ObjectId fid = CivilFeatureLine.Create(name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6),
                        joined.ObjectId);
                    outputName = ((CivilFeatureLine)tr.GetObject(fid, OpenMode.ForRead)).Name;
                    if (!joined.IsErased) joined.Erase();
                }
                else outputName = joined.Handle.ToString();
                if (deleteSources)
                {
                    foreach (ObjectId id in ids)
                    {
                        Entity entity = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                        if (entity != null) entity.Erase();
                    }
                }
                pieceCount = pieces.Count;
                vertexCount = ordered.Count;
                tr.Commit();
            }
        }

        private static PromptSelectionResult SelectFeatureLines(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
        }

        private static CivilFeatureLine OpenFeatureLine(Transaction transaction, ObjectId id, OpenMode mode)
        {
            if (id.IsNull || id.IsErased) return null;
            try { return transaction.GetObject(id, mode, false) as CivilFeatureLine; }
            catch { return null; }
        }

        private static bool Editable(CivilFeatureLine featureLine, Transaction transaction)
        {
            if (featureLine == null || featureLine.IsReferenceObject) return false;
            try
            {
                LayerTableRecord layer = transaction.GetObject(featureLine.LayerId, OpenMode.ForRead, false) as LayerTableRecord;
                return layer == null || !layer.IsLocked;
            }
            catch { return false; }
        }

        private static void SetAbsoluteElevation(CivilFeatureLine featureLine, Point3d point, int index, double elevation)
        {
            if (double.IsNaN(elevation) || double.IsInfinity(elevation))
                throw new InvalidOperationException("A constant-grade elevation was non-finite.");
            try
            {
                if (featureLine.IsElevationRelativeToSurface(point))
                {
                    featureLine.SetPointRelativeElevation(point, false, elevation);
                    return;
                }
            }
            catch { }
            featureLine.SetPointElevation(index, elevation);
        }

        private static string SafeName(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }
    }
}
