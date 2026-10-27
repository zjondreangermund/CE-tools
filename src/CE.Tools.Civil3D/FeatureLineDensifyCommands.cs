using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.FeatureLineDensifyCommands))]

namespace CETools.Civil3D
{
    /// <summary>Feature-line densification with an explicit edit preview.</summary>
    public sealed class FeatureLineDensifyCommands
    {
        private static double _lastInterval = 0.50;
        private const double PointTolerance = 0.0001;

        [CommandMethod("CE_TOOLS", "CE_DENSIFYFEATURELINES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Execute()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            Editor editor = document.Editor;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Densify Feature Lines",
                "Insert PI geometry vertices or elevation-only points at regular distances " +
                "along multiple selected editable Civil 3D feature lines. Existing points are preserved.");
            settings.AddPositiveDouble("Interval", "01 Spacing", "Vertex interval / distance (m)",
                _lastInterval, "Insert points at this interval measured from the beginning of each feature line.");
            settings.AddChoice("PointType", "02 Point type", "Insert point type",
                "PI vertices",
                "PI vertices divide the feature-line geometry and may affect curved segments. " +
                "Elevation points retain existing plan geometry and add elevation controls only.",
                new[] { "PI vertices", "Elevation points (preserve plan geometry)" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            double interval = settings.Double("Interval", _lastInterval);
            if (interval <= PointTolerance)
            {
                editor.WriteMessage("\\nChoose a distance greater than the point tolerance.");
                return;
            }
            _lastInterval = interval;
            bool piVertices = settings.Text("PointType") == "PI vertices";

            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions {
                    MessageForAdding = "\\nSelect editable Civil 3D feature lines to densify: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            int eligible = 0, skipped = 0, planned = 0;
            try
            {
                using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject item in selection.Value)
                    {
                        CivilFeatureLine line = OpenFeatureLine(tr, item);
                        if (!IsEditable(tr, line)) { skipped++; continue; }
                        eligible++;
                        planned += CountInsertions(line, interval);
                        if (planned > 100000)
                            throw new InvalidOperationException(
                                "This selection exceeds 100,000 requested points. Increase spacing or split the selection.");
                    }
                }
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\\nCE_DENSIFYFEATURELINES preview stopped: " + ex.Message);
                return;
            }

            if (planned == 0)
            {
                editor.WriteMessage("\\nNo additional vertices are required. Eligible feature lines: {0}; skipped: {1}.",
                    eligible, skipped);
                return;
            }

            string mode = piVertices ? "PI vertices (edit feature-line geometry)" :
                                       "elevation points (preserve plan geometry)";
            if (!DisciplineWorkflowDialogs.Confirm("CE Tools - Densify Feature Lines",
                "Selected feature lines: " + eligible + "\\n" +
                "New points: approximately " + planned + "\\n" +
                "Distance: " + interval.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " m\\n" +
                "Insert: " + mode + "\\n\\n" +
                (piVertices ? "PI vertices can change the shape of curved feature lines.\\n" : "") +
                "Continue on the selected feature lines?"))
                return;

            int changed = 0, added = 0, applySkipped = 0;
            try
            {
                using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject item in selection.Value)
                    {
                        CivilFeatureLine line = OpenFeatureLine(tr, item);
                        if (!IsEditable(tr, line)) { applySkipped++; continue; }
                        double length = line.Length2D;
                        if (length <= interval + PointTolerance) continue;
                        var targets = new List<Point3d>();
                        Point3dCollection existing = ReadAllPoints(line);
                        for (double station = interval; station < length - PointTolerance; station += interval)
                        {
                            Point3d point = line.GetPointAtDist(station);
                            if (!HasNearbyPoint(existing, point)) targets.Add(point);
                        }
                        // Capture all target points before editing; reverse order
                        // to avoid moving downstream station targets.
                        if (targets.Count == 0) continue;
                        line.UpgradeOpen();
                        for (int i = targets.Count - 1; i >= 0; i--)
                        {
                            if (piVertices) line.InsertPIPoint(targets[i]);
                            else line.InsertElevationPoint(targets[i]);
                            added++;
                        }
                        changed++;
                    }
                    tr.Commit();
                }
                document.Editor.Regen();
                editor.WriteMessage("\\nCE_DENSIFYFEATURELINES completed. Feature lines changed={0}; " +
                    "new {1}={2}; skipped={3}; distance={4:G} m.",
                    changed, piVertices ? "PI vertices" : "elevation points",
                    added, skipped + applySkipped, interval);
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\\nCE_DENSIFYFEATURELINES failed; transaction was not committed: " + ex.Message);
            }
        }

        private static CivilFeatureLine OpenFeatureLine(Transaction transaction, SelectedObject selected)
        {
            if (selected == null || selected.ObjectId.IsNull || selected.ObjectId.IsErased) return null;
            try { return transaction.GetObject(selected.ObjectId, OpenMode.ForRead, false) as CivilFeatureLine; }
            catch { return null; }
        }
        private static bool IsEditable(Transaction transaction, CivilFeatureLine line)
        {
            if (line == null || line.IsErased || line.IsReferenceObject) return false;
            try
            {
                var layer = transaction.GetObject(line.LayerId, OpenMode.ForRead, false) as LayerTableRecord;
                return layer != null && !layer.IsLocked;
            }
            catch { return false; }
        }
        private static Point3dCollection ReadAllPoints(CivilFeatureLine line)
        {
            // FeatureLinePointType may be unavailable in Civil 3D 2025:
            // resolve the GetPoints enum reflectively to support each host API.
            MethodInfo method = line.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(candidate => candidate.Name == "GetPoints" &&
                    candidate.GetParameters().Length == 1 &&
                    candidate.GetParameters()[0].ParameterType.IsEnum);
            if (method == null) throw new InvalidOperationException("Civil 3D did not expose FeatureLine.GetPoints.");
            Type enumType = method.GetParameters()[0].ParameterType;
            object all = Enum.Parse(enumType, "AllPoints", true);
            var result = method.Invoke(line, new[] { all }) as Point3dCollection;
            if (result == null) throw new InvalidOperationException("Unable to retrieve feature-line points.");
            return result;
        }
        private static bool HasNearbyPoint(Point3dCollection points, Point3d point)
        {
            foreach (Point3d existing in points)
            {
                double x = existing.X - point.X, y = existing.Y - point.Y;
                if (x * x + y * y <= PointTolerance * PointTolerance) return true;
            }
            return false;
        }
        private static int CountInsertions(CivilFeatureLine line, double interval)
        {
            double length = line.Length2D;
            if (interval <= 0 || length <= interval + PointTolerance) return 0;
            if (length / interval > 100000)
                throw new InvalidOperationException("More than 100,000 inserted vertices would be required.");
            Point3dCollection existing = ReadAllPoints(line);
            int total = 0;
            for (double station = interval; station < length - PointTolerance; station += interval)
                if (!HasNearbyPoint(existing, line.GetPointAtDist(station))) total++;
            return total;
        }
    }
}
