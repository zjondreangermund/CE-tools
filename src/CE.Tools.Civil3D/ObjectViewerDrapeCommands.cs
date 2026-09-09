using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.ObjectViewerDrapeCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// CE wrapper around Civil 3D's ObjectViewer. The source drawing is never
    /// modified for a draped preview: temporary display geometry is sampled to
    /// the chosen Civil surface and is removed as soon as ObjectViewer closes.
    /// </summary>
    public sealed class ObjectViewerDrapeCommands
    {
        private const string PreviewLayerName = "CE-VIEWER-DRAPED";

        [CommandMethod("CE_TOOLS", "CE_OBJECTVIEWER", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void OpenObjectViewer()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null) return;

            Editor editor = document.Editor;
            ObjectId[] selected = ReadSelection(editor);
            if (selected.Length == 0) return;

            List<SurfaceChoice> surfaces = civilDocument == null
                ? new List<SurfaceChoice>()
                : ReadSurfaces(document.Database, civilDocument);

            if (surfaces.Count == 0)
            {
                editor.SetImpliedSelection(selected);
                document.SendStringToExecute("_.OBJECTVIEWER ", true, false, false);
                return;
            }

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Object Viewer",
                "Preview the selected objects normally, or create temporary preview copies draped to a Civil 3D surface. Original objects are never changed.");
            settings.AddChoice(
                "Drape",
                "01 Surface drape",
                "Drape AutoCAD objects to surface",
                "Yes",
                "Curves are sampled along their length. Text, blocks, points and other entities are moved vertically at their reference/centre point for the preview only.",
                new[] { "Yes", "No" });
            settings.AddChoice(
                "Surface",
                "01 Surface drape",
                "Surface",
                surfaces[0].Name,
                "Civil 3D surface used to obtain preview elevations.",
                surfaces.Select(item => item.Name).ToArray());
            settings.AddPositiveDouble(
                "Spacing",
                "01 Surface drape",
                "Curve sampling spacing",
                5.0,
                "Maximum plan spacing between draped preview points. Smaller values follow the surface more closely.");
            settings.AddChoice(
                "IncludeSurface",
                "02 Viewer",
                "Include selected surface in Object Viewer",
                "Yes",
                "Shows the drape surface together with the temporary draped objects.",
                new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            bool drape = string.Equals(settings.Text("Drape"), "Yes", StringComparison.OrdinalIgnoreCase);
            SurfaceChoice surface = surfaces.FirstOrDefault(item =>
                string.Equals(item.Name, settings.Text("Surface"), StringComparison.OrdinalIgnoreCase)) ?? surfaces[0];
            bool includeSurface = string.Equals(settings.Text("IncludeSurface"), "Yes", StringComparison.OrdinalIgnoreCase);

            if (!drape)
            {
                var viewerIds = new List<ObjectId>(selected);
                if (includeSurface && !viewerIds.Contains(surface.Id)) viewerIds.Add(surface.Id);
                editor.SetImpliedSelection(viewerIds.ToArray());
                document.SendStringToExecute("_.OBJECTVIEWER ", true, false, false);
                return;
            }

            List<ObjectId> temporaryIds;
            int outsideSurface;
            try
            {
                temporaryIds = CreateDrapedPreview(
                    document,
                    selected,
                    surface.Id,
                    Math.Max(0.01, settings.Double("Spacing", 5.0)),
                    out outsideSurface);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_OBJECTVIEWER: draped preview could not be created. {0}", exception.Message);
                return;
            }

            if (temporaryIds.Count == 0)
            {
                editor.WriteMessage("\nCE_OBJECTVIEWER: no preview geometry could be created from the selected objects.");
                return;
            }

            var finalSelection = new List<ObjectId>(temporaryIds);
            if (includeSurface && !surface.Id.IsNull && !surface.Id.IsErased)
                finalSelection.Add(surface.Id);

            if (outsideSurface > 0)
                editor.WriteMessage("\nCE_OBJECTVIEWER: {0} sampled preview point(s) were outside the selected surface and retained their source elevation.", outsideSurface);

            ObjectViewerPreviewCleanup.Schedule(document, temporaryIds);
            editor.SetImpliedSelection(finalSelection.ToArray());
            document.SendStringToExecute("_.OBJECTVIEWER ", true, false, false);
        }

        private static ObjectId[] ReadSelection(Editor editor)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
                return implied.Value.GetObjectIds().Where(id => !id.IsNull && !id.IsErased).Distinct().ToArray();

            PromptSelectionResult result = editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect objects to view: ",
                MessageForRemoval = "\nRemove objects: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            return result.Status == PromptStatus.OK && result.Value != null
                ? result.Value.GetObjectIds().Where(id => !id.IsNull && !id.IsErased).Distinct().ToArray()
                : new ObjectId[0];
        }

        private static List<SurfaceChoice> ReadSurfaces(Database database, CivilDocument civilDocument)
        {
            var result = new List<SurfaceChoice>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civilDocument.GetSurfaceIds())
                {
                    CivilSurface surface = transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
                    if (surface != null) result.Add(new SurfaceChoice { Id = id, Name = surface.Name });
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static List<ObjectId> CreateDrapedPreview(
            Document document,
            IEnumerable<ObjectId> sourceIds,
            ObjectId surfaceId,
            double spacing,
            out int outsideSurface)
        {
            outsideSurface = 0;
            var created = new List<ObjectId>();
            Database database = document.Database;

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                CivilSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                if (surface == null) throw new InvalidOperationException("The selected Civil 3D surface is no longer available.");

                ObjectId layerId = EnsurePreviewLayer(database, transaction);
                BlockTableRecord modelSpace = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(database),
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (modelSpace == null) throw new InvalidOperationException("Model space is unavailable.");

                foreach (ObjectId sourceId in sourceIds.Where(id => !id.IsNull && !id.IsErased).Distinct())
                {
                    Entity source;
                    try { source = transaction.GetObject(sourceId, OpenMode.ForRead, false) as Entity; }
                    catch { continue; }
                    if (source == null || sourceId == surfaceId || source is CivilSurface) continue;

                    Entity preview = CreatePreviewEntity(source, surface, spacing, ref outsideSurface);
                    if (preview == null) continue;
                    preview.SetDatabaseDefaults(database);
                    preview.LayerId = layerId;
                    try { preview.Color = source.Color; } catch { }
                    try { preview.LinetypeId = source.LinetypeId; } catch { }
                    modelSpace.AppendEntity(preview);
                    transaction.AddNewlyCreatedDBObject(preview, true);
                    created.Add(preview.ObjectId);
                }

                transaction.Commit();
            }
            return created;
        }

        private static Entity CreatePreviewEntity(Entity source, CivilSurface surface, double spacing, ref int outsideSurface)
        {
            Curve curve = source as Curve;
            if (curve != null)
            {
                Point3dCollection points = SampleCurve(curve, surface, spacing, ref outsideSurface);
                if (points.Count >= 2)
                    return new Polyline3d(Poly3dType.SimplePoly, points, false);
            }

            DBPoint point = source as DBPoint;
            if (point != null)
            {
                DBPoint clone = point.Clone() as DBPoint;
                if (clone == null) return null;
                clone.Position = DrapePoint(point.Position, surface, ref outsideSurface);
                return clone;
            }

            DBText text = source as DBText;
            if (text != null)
            {
                DBText clone = text.Clone() as DBText;
                if (clone == null) return null;
                Point3d old = clone.Position;
                Point3d draped = DrapePoint(old, surface, ref outsideSurface);
                Vector3d shift = draped - old;
                clone.TransformBy(Matrix3d.Displacement(shift));
                return clone;
            }

            MText mtext = source as MText;
            if (mtext != null)
            {
                MText clone = mtext.Clone() as MText;
                if (clone == null) return null;
                Point3d old = clone.Location;
                Point3d draped = DrapePoint(old, surface, ref outsideSurface);
                clone.TransformBy(Matrix3d.Displacement(draped - old));
                return clone;
            }

            BlockReference block = source as BlockReference;
            if (block != null)
            {
                BlockReference clone = block.Clone() as BlockReference;
                if (clone == null) return null;
                Point3d old = clone.Position;
                Point3d draped = DrapePoint(old, surface, ref outsideSurface);
                clone.TransformBy(Matrix3d.Displacement(draped - old));
                return clone;
            }

            Entity generic = source.Clone() as Entity;
            if (generic == null) return null;
            try
            {
                Extents3d extents = source.GeometricExtents;
                Point3d centre = new Point3d(
                    (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
                    (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5,
                    (extents.MinPoint.Z + extents.MaxPoint.Z) * 0.5);
                Point3d draped = DrapePoint(centre, surface, ref outsideSurface);
                generic.TransformBy(Matrix3d.Displacement(new Vector3d(0.0, 0.0, draped.Z - centre.Z)));
            }
            catch { }
            return generic;
        }

        private static Point3dCollection SampleCurve(Curve curve, CivilSurface surface, double spacing, ref int outsideSurface)
        {
            var sourcePoints = new List<Point3d>();
            try
            {
                double start = curve.StartParam;
                double end = curve.EndParam;
                double startDistance = curve.GetDistanceAtParameter(start);
                double endDistance = curve.GetDistanceAtParameter(end);
                double length = Math.Abs(endDistance - startDistance);
                int divisions = Math.Max(1, Math.Min(2000, (int)Math.Ceiling(length / Math.Max(0.01, spacing))));
                for (int index = 0; index <= divisions; index++)
                {
                    double distance = startDistance + (endDistance - startDistance) * index / divisions;
                    sourcePoints.Add(curve.GetPointAtDist(distance));
                }
            }
            catch
            {
                try
                {
                    sourcePoints.Add(curve.StartPoint);
                    sourcePoints.Add(curve.EndPoint);
                }
                catch { }
            }

            var result = new Point3dCollection();
            Point3d? previous = null;
            foreach (Point3d point in sourcePoints)
            {
                Point3d draped = DrapePoint(point, surface, ref outsideSurface);
                if (!previous.HasValue || previous.Value.DistanceTo(draped) > 1e-9)
                {
                    result.Add(draped);
                    previous = draped;
                }
            }
            return result;
        }

        private static Point3d DrapePoint(Point3d point, CivilSurface surface, ref int outsideSurface)
        {
            try
            {
                return new Point3d(point.X, point.Y, surface.FindElevationAtXY(point.X, point.Y));
            }
            catch
            {
                outsideSurface++;
                return point;
            }
        }

        private static ObjectId EnsurePreviewLayer(Database database, Transaction transaction)
        {
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table == null) return database.Clayer;
            if (table.Has(PreviewLayerName)) return table[PreviewLayerName];

            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = PreviewLayerName, IsPlottable = false };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private sealed class SurfaceChoice
        {
            public ObjectId Id { get; set; }
            public string Name { get; set; }
        }
    }

    internal static class ObjectViewerPreviewCleanup
    {
        private sealed class Session
        {
            internal Document Document;
            internal List<ObjectId> TemporaryIds;
            internal bool ViewerStarted;
        }

        private static readonly Dictionary<Document, Session> Sessions = new Dictionary<Document, Session>();

        internal static void Schedule(Document document, IEnumerable<ObjectId> temporaryIds)
        {
            Cancel(document);
            var session = new Session
            {
                Document = document,
                TemporaryIds = temporaryIds.Where(id => !id.IsNull).Distinct().ToList()
            };
            Sessions[document] = session;
            document.CommandWillStart += OnCommandWillStart;
            document.CommandEnded += OnCommandFinished;
            document.CommandCancelled += OnCommandFinished;
            document.CommandFailed += OnCommandFinished;
        }

        private static void OnCommandWillStart(object sender, CommandEventArgs e)
        {
            Document document = sender as Document;
            Session session;
            if (document == null || !Sessions.TryGetValue(document, out session)) return;
            if (string.Equals(e.GlobalCommandName, "OBJECTVIEWER", StringComparison.OrdinalIgnoreCase))
                session.ViewerStarted = true;
        }

        private static void OnCommandFinished(object sender, CommandEventArgs e)
        {
            Document document = sender as Document;
            Session session;
            if (document == null || !Sessions.TryGetValue(document, out session) || !session.ViewerStarted) return;
            if (!string.Equals(e.GlobalCommandName, "OBJECTVIEWER", StringComparison.OrdinalIgnoreCase)) return;
            Erase(session);
            Detach(document);
        }

        private static void Cancel(Document document)
        {
            Session session;
            if (!Sessions.TryGetValue(document, out session)) return;
            Erase(session);
            Detach(document);
        }

        private static void Erase(Session session)
        {
            try
            {
                using (DocumentLock documentLock = session.Document.LockDocument())
                using (Transaction transaction = session.Document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in session.TemporaryIds)
                    {
                        if (id.IsNull || id.IsErased) continue;
                        try
                        {
                            DBObject value = transaction.GetObject(id, OpenMode.ForWrite, false);
                            if (value != null && !value.IsErased) value.Erase();
                        }
                        catch { }
                    }
                    transaction.Commit();
                }
            }
            catch { }
        }

        private static void Detach(Document document)
        {
            document.CommandWillStart -= OnCommandWillStart;
            document.CommandEnded -= OnCommandFinished;
            document.CommandCancelled -= OnCommandFinished;
            document.CommandFailed -= OnCommandFinished;
            Sessions.Remove(document);
        }
    }
}
