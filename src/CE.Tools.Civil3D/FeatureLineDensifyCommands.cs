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
            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions { MessageForAdding = "\nSelect feature lines to densify: " });
            if (selection.Status != PromptStatus.OK) return;

            PromptDoubleResult intervalResult = editor.GetDouble(new PromptDoubleOptions(
                string.Format("\nMaximum vertex interval in drawing units <{0:G}>: ", _lastInterval))
            {
                AllowNegative = false,
                AllowZero = false,
                AllowNone = true,
                DefaultValue = _lastInterval,
                UseDefaultValue = true
            });
            if (intervalResult.Status != PromptStatus.OK && intervalResult.Status != PromptStatus.None) return;
            double interval = intervalResult.Status == PromptStatus.None ? _lastInterval : intervalResult.Value;
            if (interval <= 0.0)
            {
                editor.WriteMessage("\nInterval must be greater than zero.");
                return;
            }
            _lastInterval = interval;

            int eligible = 0, skipped = 0, planned = 0;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject item in selection.Value)
                {
                    CivilFeatureLine fl = OpenFeatureLine(tr, item);
                    if (!IsEditable(tr, fl)) { skipped++; continue; }
                    eligible++;
                    planned += CountInsertions(fl, interval);
                }
            }

            if (planned == 0)
            {
                editor.WriteMessage("\nCE_DENSIFYFEATURELINES: no new vertices are required. Feature lines checked: {0}; skipped: {1}.", eligible, skipped);
                return;
            }

            PromptKeywordOptions confirmOptions = new PromptKeywordOptions(
                string.Format("\nAdd approximately {0} interpolated vertices to {1} feature lines? [Yes/No] <No>: ", planned, eligible))
            { AllowNone = true };
            confirmOptions.Keywords.Add("Yes");
            confirmOptions.Keywords.Add("No");
            PromptResult confirm = editor.GetKeywords(confirmOptions);
            if (confirm.Status != PromptStatus.OK || !string.Equals(confirm.StringResult, "Yes", StringComparison.OrdinalIgnoreCase))
            {
                editor.WriteMessage("\nCE_DENSIFYFEATURELINES cancelled. No changes were made.");
                return;
            }

            int changedLines = 0, added = 0, applySkipped = 0;
            try
            {
                using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject item in selection.Value)
                    {
                        CivilFeatureLine fl = OpenFeatureLine(tr, item);
                        if (!IsEditable(tr, fl)) { applySkipped++; continue; }

                        double length = fl.Length2D;
                        if (length <= interval + PointTolerance) continue;

                        // Work from the end toward the start so inserted points do not
                        // affect the remaining chainage targets on implementations that
                        // recalculate internal point indexing after each insertion.
                        var targets = new List<double>();
                        for (double station = interval; station < length - PointTolerance; station += interval)
                            targets.Add(station);

                        Point3dCollection existing = ReadAllPoints(fl);
                        bool changed = false;
                        fl.UpgradeOpen();
                        for (int i = targets.Count - 1; i >= 0; i--)
                        {
                            Point3d p = fl.GetPointAtDist(targets[i]);
                            if (HasNearbyPoint(existing, p)) continue;
                            fl.InsertElevationPoint(p);
                            added++;
                            changed = true;
                            existing.Add(p);
                        }
                        if (changed) changedLines++;
                    }
                    tr.Commit();
                }
                editor.WriteMessage(
                    "\nCE_DENSIFYFEATURELINES complete. Feature lines densified: {0}; vertices added: {1}; skipped: {2}. Interval: {3:G} drawing units.",
                    changedLines, added, skipped + applySkipped, interval);
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage("\nCE_DENSIFYFEATURELINES failed; transaction was not committed. {0}", ex.Message);
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
