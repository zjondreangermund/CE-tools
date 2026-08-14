using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivilProfileView = Autodesk.Civil.DatabaseServices.ProfileView;
using CivilPolylineOptions = Autodesk.Civil.DatabaseServices.PolylineOptions;

[assembly: CommandClass(typeof(CETools.Civil3D.August14SafeProductionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Conservative Civil 3D fallbacks for field cases where the richer linked
    /// production path is rejected by dirty source geometry or a host-specific
    /// projection/profile-view API. These commands deliberately do less: they
    /// sanitize source geometry and use the documented direct Civil APIs only.
    /// </summary>
    public sealed class August14SafeProductionCommands
    {
        private const double PointTolerance = 1e-6;

        [CommandMethod("CE_TOOLS", "CE_SWALIGNSAFE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SafeStormwaterAlignments()
        {
            Document document = Active();
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;
            Editor editor = document.Editor;

            string[] alignmentStyles = ProductionStyleCatalog.ReadNames(
                document.Database,
                civil.Styles.AlignmentStyles,
                "Alignment Style");
            string[] labelSets = ProductionStyleCatalog.ReadNames(
                document.Database,
                civil.Styles.LabelSetStyles.AlignmentLabelSetStyles,
                "Alignment Label Set Style");
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Safe Stormwater Alignments",
                "Fallback for source polylines that fail the richer SW alignment workflow. CE Tools removes consecutive duplicate XY vertices, flattens source Z for alignment geometry, skips zero-length strings and calls Civil 3D Alignment.Create directly.");
            model.AddChoice("AlignmentStyle", "01 Styles", "Alignment style", First(alignmentStyles), "Civil 3D alignment style for all generated fallback alignments.", alignmentStyles);
            model.AddChoice("LabelSet", "01 Styles", "Alignment label-set style", First(labelSets), "Civil 3D alignment label set for generated fallback alignments.", labelSets);
            model.AddText("Layer", "02 Output", "Alignment layer", "CE-SW-ALIGNMENT", "Output layer for the fallback alignments.");
            model.AddText("Prefix", "02 Output", "Alignment name prefix", "SW-SAFE", "Names are created as Prefix-001, Prefix-002, and so on.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selected = editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect stormwater source 2D/3D polylines for SAFE alignment creation: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (selected.Status != PromptStatus.OK || selected.Value == null) return;

            int created = 0;
            int skipped = 0;
            var messages = new List<string>();
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    ObjectId layerId = GetOrCreateLayer(document.Database, transaction, model.Text("Layer"), "CE-SW-ALIGNMENT");
                    ObjectId alignmentStyleId = ResolveStyleId(civil.Styles.AlignmentStyles, model.Text("AlignmentStyle"), transaction);
                    ObjectId labelSetId = ResolveStyleId(civil.Styles.LabelSetStyles.AlignmentLabelSetStyles, model.Text("LabelSet"), transaction);
                    BlockTableRecord modelSpace = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForWrite, false) as BlockTableRecord;
                    if (modelSpace == null) return;
                    HashSet<string> names = ReadAlignmentNames(civil, transaction);

                    foreach (ObjectId sourceId in selected.Value.GetObjectIds())
                    {
                        List<Point2d> points = ReadSanitizedPlanPoints(sourceId, transaction);
                        if (points.Count < 2 || TotalLength(points) <= PointTolerance)
                        {
                            skipped++;
                            continue;
                        }
                        var clean = new Polyline(points.Count);
                        clean.SetDatabaseDefaults(document.Database);
                        clean.LayerId = layerId;
                        for (int index = 0; index < points.Count; index++)
                            clean.AddVertexAt(index, points[index], 0.0, 0.0, 0.0);
                        clean.Closed = false;
                        modelSpace.AppendEntity(clean);
                        transaction.AddNewlyCreatedDBObject(clean, true);

                        var options = new CivilPolylineOptions
                        {
                            AddCurvesBetweenTangents = false,
                            EraseExistingEntities = true,
                            PlineId = clean.ObjectId
                        };
                        string baseName = (string.IsNullOrWhiteSpace(model.Text("Prefix")) ? "SW-SAFE" : model.Text("Prefix").Trim()) + "-" + (created + 1).ToString("000", CultureInfo.InvariantCulture);
                        string name = UniqueName(baseName, names);
                        try
                        {
                            ObjectId alignmentId = CivilAlignment.Create(
                                civil,
                                options,
                                name,
                                ObjectId.Null,
                                layerId,
                                alignmentStyleId,
                                labelSetId);
                            CivilAlignment alignment = transaction.GetObject(alignmentId, OpenMode.ForWrite, false) as CivilAlignment;
                            if (alignment != null)
                                alignment.Description = "CE safe stormwater alignment | source=" + sourceId.Handle.ToString();
                            created++;
                        }
                        catch (System.Exception exception)
                        {
                            if (!clean.IsErased) clean.Erase();
                            skipped++;
                            messages.Add(sourceId.Handle + ": " + exception.Message);
                        }
                    }
                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_SWALIGNSAFE cancelled. No fallback alignment transaction was committed. " + exception.Message);
                return;
            }

            editor.Regen();
            editor.WriteMessage("\nCE_SWALIGNSAFE complete. Alignments created={0}; skipped={1}. This fallback intentionally omits CE branch sequencing/link metadata; use it only for source strings rejected by the normal SW alignment command.", created, skipped);
            foreach (string message in messages.Take(5)) editor.WriteMessage("\n  " + message);
        }

        [CommandMethod("CE_TOOLS", "CE_WATERPROFILESAFE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SafeWaterProfiles()
        {
            Document document = Active();
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;
            Editor editor = document.Editor;

            PromptSelectionResult selected = editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect WATER/BULK-WATER alignments for safe profile creation: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (selected.Status != PromptStatus.OK || selected.Value == null) return;
            List<ObjectId> alignmentIds = FilterAlignments(document.Database, selected.Value.GetObjectIds());
            if (alignmentIds.Count == 0)
            {
                editor.WriteMessage("\nCE_WATERPROFILESAFE: no Civil 3D alignments were selected.");
                return;
            }

            List<SurfaceChoice> surfaces = WorkflowRepairCommands.ReadSurfaceChoices(document);
            if (surfaces.Count == 0)
            {
                editor.WriteMessage("\nCE_WATERPROFILESAFE cancelled. The drawing contains no Civil 3D surfaces.");
                return;
            }
            var surfaceWindow = new SurfaceSelectionWindow(surfaces, "Select the existing-ground surface for the SAFE water/bulk-water profiles.");
            AcApplication.ShowModalWindow(surfaceWindow);
            SurfaceChoice surface = surfaceWindow.SelectedSurface;
            if (surface == null) return;

            string[] profileStyles = ProductionStyleCatalog.ReadNames(document.Database, civil.Styles.ProfileStyles, "Profile Style");
            string[] profileLabelSets = ProductionStyleCatalog.ReadNames(document.Database, civil.Styles.LabelSetStyles.ProfileLabelSetStyles, "Profile Label Set Style");
            string[] viewStyles = ProductionStyleCatalog.ReadNames(document.Database, civil.Styles.ProfileViewStyles, "Profile View Style");
            string[] bandSets = ProductionStyleCatalog.ReadNames(document.Database, civil.Styles.ProfileViewBandSetStyles, "Profile View Band Set Style");
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Safe Water Profile Views",
                "Fallback for fatal/internal pressure-profile cases. It creates only a surface profile and a profile view through Civil 3D's direct Profile.CreateFromSurface and ProfileView.Create APIs. It deliberately skips pressure-part projection and CE band binding.");
            model.AddChoice("ProfileStyle", "01 Styles", "Profile style", First(profileStyles), "Style for the surface profile.", profileStyles);
            model.AddChoice("ProfileLabelSet", "01 Styles", "Profile label-set style", First(profileLabelSets), "Label set for the surface profile.", profileLabelSets);
            model.AddChoice("ViewStyle", "01 Styles", "Profile-view style", First(viewStyles), "Style for the profile view.", viewStyles);
            model.AddChoice("BandSet", "01 Styles", "Profile-view band-set style", First(bandSets), "Band set imported to each profile view.", bandSets);
            model.AddText("Layer", "02 Layout", "Profile layer", "CE-WATER-PROFILE-SAFE", "Layer for generated profiles/profile views where supported by the Civil API.");
            model.AddPositiveInteger("Columns", "02 Layout", "Views per row", 2, "Number of profile views before wrapping to a new row.");
            model.AddPositiveDouble("Horizontal", "02 Layout", "Horizontal spacing", 300.0, "Drawing-unit spacing between profile-view insertion points.");
            model.AddPositiveDouble("Vertical", "02 Layout", "Vertical spacing", 200.0, "Drawing-unit spacing between profile-view rows.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptPointResult insertion = editor.GetPoint("\nSpecify the upper-left insertion point for safe profile views: ");
            if (insertion.Status != PromptStatus.OK) return;

            int profiles = 0;
            int views = 0;
            int skipped = 0;
            var failures = new List<string>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId layerId = GetOrCreateLayer(document.Database, transaction, model.Text("Layer"), "CE-WATER-PROFILE-SAFE");
                ObjectId profileStyleId = ResolveStyleId(civil.Styles.ProfileStyles, model.Text("ProfileStyle"), transaction);
                ObjectId profileLabelSetId = ResolveStyleId(civil.Styles.LabelSetStyles.ProfileLabelSetStyles, model.Text("ProfileLabelSet"), transaction);
                ObjectId viewStyleId = ResolveStyleId(civil.Styles.ProfileViewStyles, model.Text("ViewStyle"), transaction);
                ObjectId bandSetId = ResolveStyleId(civil.Styles.ProfileViewBandSetStyles, model.Text("BandSet"), transaction);
                HashSet<string> profileNames = ReadObjectNames(document.Database, transaction, "Profile");
                HashSet<string> viewNames = ReadObjectNames(document.Database, transaction, "ProfileView");
                int columns = Math.Max(1, model.Integer("Columns", 2));
                double horizontal = model.Double("Horizontal", 300.0);
                double vertical = model.Double("Vertical", 200.0);

                for (int index = 0; index < alignmentIds.Count; index++)
                {
                    CivilAlignment alignment = transaction.GetObject(alignmentIds[index], OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null) { skipped++; continue; }
                    string profileName = UniqueName(alignment.Name + " - SAFE EG", profileNames);
                    string viewName = UniqueName(alignment.Name + " - SAFE PROFILE VIEW", viewNames);
                    try
                    {
                        ObjectId profileId = CivilProfile.CreateFromSurface(
                            profileName,
                            alignment.ObjectId,
                            surface.ObjectId,
                            layerId,
                            profileStyleId,
                            profileLabelSetId);
                        transaction.GetObject(profileId, OpenMode.ForRead, false);
                        profiles++;

                        int column = index % columns;
                        int row = index / columns;
                        Point3d point = insertion.Value + new Vector3d(column * horizontal, -row * vertical, 0.0);
                        ObjectId viewId = CivilProfileView.Create(
                            alignment.ObjectId,
                            point,
                            viewName,
                            bandSetId,
                            viewStyleId);
                        transaction.GetObject(viewId, OpenMode.ForRead, false);
                        views++;
                    }
                    catch (System.Exception exception)
                    {
                        skipped++;
                        failures.Add(alignment.Name + ": " + exception.Message);
                    }
                }
                transaction.Commit();
            }

            editor.Regen();
            editor.WriteMessage("\nCE_WATERPROFILESAFE complete. Surface profiles={0}; profile views={1}; skipped={2}. No pressure parts were projected into these fallback views.", profiles, views, skipped);
            foreach (string failure in failures.Take(5)) editor.WriteMessage("\n  " + failure);
        }

        private static List<ObjectId> FilterAlignments(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    try
                    {
                        if (transaction.GetObject(id, OpenMode.ForRead, false) is CivilAlignment) result.Add(id);
                    }
                    catch { }
                }
            }
            return result.Distinct().ToList();
        }

        private static List<Point2d> ReadSanitizedPlanPoints(ObjectId id, Transaction transaction)
        {
            var result = new List<Point2d>();
            DBObject value;
            try { value = transaction.GetObject(id, OpenMode.ForRead, false); }
            catch { return result; }
            Polyline polyline = value as Polyline;
            if (polyline != null)
            {
                for (int index = 0; index < polyline.NumberOfVertices; index++)
                    AddPlanPoint(result, polyline.GetPoint2dAt(index));
                return result;
            }
            Polyline3d poly3d = value as Polyline3d;
            if (poly3d != null)
            {
                foreach (ObjectId vertexId in poly3d)
                {
                    PolylineVertex3d vertex = transaction.GetObject(vertexId, OpenMode.ForRead, false) as PolylineVertex3d;
                    if (vertex != null) AddPlanPoint(result, new Point2d(vertex.Position.X, vertex.Position.Y));
                }
            }
            return result;
        }

        private static void AddPlanPoint(IList<Point2d> points, Point2d point)
        {
            if (points.Count == 0 || points[points.Count - 1].GetDistanceTo(point) > PointTolerance) points.Add(point);
        }

        private static double TotalLength(IList<Point2d> points)
        {
            double length = 0.0;
            for (int index = 1; index < points.Count; index++) length += points[index - 1].GetDistanceTo(points[index]);
            return length;
        }

        private static ObjectId ResolveStyleId(IEnumerable<ObjectId> ids, string requested, Transaction transaction)
        {
            ObjectId first = ObjectId.Null;
            foreach (ObjectId id in ids)
            {
                if (first.IsNull) first = id;
                try
                {
                    DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                    string name = Convert.ToString(ReadProperty(value, "Name"), CultureInfo.CurrentCulture);
                    if (!string.IsNullOrWhiteSpace(requested) && string.Equals(name, requested, StringComparison.OrdinalIgnoreCase)) return id;
                }
                catch { }
            }
            if (!first.IsNull) return first;
            throw new InvalidOperationException("The current drawing contains no compatible Civil 3D style for this fallback operation.");
        }

        private static HashSet<string> ReadAlignmentNames(CivilDocument civil, Transaction transaction)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in civil.GetAlignmentIds())
            {
                CivilAlignment alignment = transaction.GetObject(id, OpenMode.ForRead, false) as CivilAlignment;
                if (alignment != null && !string.IsNullOrWhiteSpace(alignment.Name)) result.Add(alignment.Name);
            }
            return result;
        }

        private static HashSet<string> ReadObjectNames(Database database, Transaction transaction, string typeName)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            BlockTable blocks = transaction.GetObject(database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
            if (blocks == null) return result;
            foreach (ObjectId blockId in blocks)
            {
                BlockTableRecord block = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord;
                if (block == null) continue;
                foreach (ObjectId id in block)
                {
                    DBObject value;
                    try { value = transaction.GetObject(id, OpenMode.ForRead, false); }
                    catch { continue; }
                    if (value == null || !string.Equals(value.GetType().Name, typeName, StringComparison.OrdinalIgnoreCase)) continue;
                    string name = Convert.ToString(ReadProperty(value, "Name"), CultureInfo.CurrentCulture);
                    if (!string.IsNullOrWhiteSpace(name)) result.Add(name);
                }
            }
            return result;
        }

        private static ObjectId GetOrCreateLayer(Database database, Transaction transaction, string requested, string fallback)
        {
            string name = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers == null) return ObjectId.Null;
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static string UniqueName(string baseName, ISet<string> names)
        {
            string root = string.IsNullOrWhiteSpace(baseName) ? "CE-SAFE" : baseName.Trim();
            string candidate = root;
            int suffix = 2;
            while (!names.Add(candidate)) candidate = root + " (" + suffix++.ToString(CultureInfo.InvariantCulture) + ")";
            return candidate;
        }

        private static string First(string[] values)
        {
            return values != null && values.Length > 0 ? values[0] : "<Use drawing default>";
        }

        private static object ReadProperty(object value, string name)
        {
            if (value == null) return null;
            try
            {
                var property = value.GetType().GetProperty(name);
                return property == null ? null : property.GetValue(value, null);
            }
            catch { return null; }
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }
}
