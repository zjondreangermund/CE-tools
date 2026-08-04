using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.SurveyCorrectionComparisonCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Read-only comparison between an original survey surface and a corrected
    /// surface. The source surfaces are never edited. Results may be reviewed in
    /// a popup, placed as a drawing table, or exported to a dependency-free XLSX.
    /// </summary>
    public sealed class SurveyCorrectionComparisonCommands
    {
        private const int MaximumSamplePoints = 250000;
        private const int MaximumPopupRows = 10000;

        [CommandMethod("CE_TOOLS", "CE_SURVEYCOMPARETOOLS", CommandFlags.Modal)]
        public void Tools()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Survey Correction Comparison",
                "Compare an original survey surface with a corrected surface without modifying either source.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Survey-change report", "CE_SURVEYCHANGES", "Review sampled elevation changes in a popup and optional drawing table.", "01 Review"),
                    new DisciplineWorkflowAction("Export survey changes", "CE_SURVEYCHANGEEXPORT", "Export the comparison to a dependency-free Excel workbook.", "02 Export")
                });
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SURVEYCHANGES",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ShowChanges()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            ComparisonRequest request;
            if (!PromptRequest(document, out request)) return;

            ComparisonResult comparison;
            try
            {
                comparison = Compare(document.Database, request);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SURVEYCHANGES stopped. {0}",
                    exception.Message);
                return;
            }

            ShowComparison(document, comparison, request);
            WriteSummary(document.Editor, comparison, request);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SURVEYCHANGEEXPORT",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ExportChanges()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            ComparisonRequest request;
            if (!PromptRequest(document, out request)) return;

            ComparisonResult comparison;
            try
            {
                comparison = Compare(document.Database, request);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SURVEYCHANGEEXPORT stopped. {0}",
                    exception.Message);
                return;
            }

            var options = new PromptSaveFileOptions(
                "\nSelect survey correction comparison workbook path: ")
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DialogCaption = "Export CE Tools Survey Correction Comparison",
                InitialFileName = "CE-Tools-Survey-Correction-Comparison.xlsx"
            };
            PromptFileNameResult pathResult =
                document.Editor.GetFileNameForSave(options);
            if (pathResult.Status != PromptStatus.OK) return;

            string path = pathResult.StringResult;
            if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                path += ".xlsx";

            try
            {
                SimpleXlsxWriter.Write(
                    path,
                    "Survey Changes",
                    BuildExportRows(comparison, request));
                document.Editor.WriteMessage(
                    "\nCE_SURVEYCHANGEEXPORT complete. Rows={0}; changed={1}; workbook={2}",
                    comparison.Rows.Count,
                    comparison.ChangedCount,
                    path);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SURVEYCHANGEEXPORT failed. {0}",
                    exception.Message);
            }
        }

        private static bool PromptRequest(
            Document document,
            out ComparisonRequest request)
        {
            request = null;
            ObjectId originalId;
            ObjectId correctedId;
            if (!PromptSurface(
                    document.Editor,
                    "\nSelect ORIGINAL survey surface: ",
                    out originalId))
                return false;
            if (!PromptSurface(
                    document.Editor,
                    "\nSelect CORRECTED survey surface: ",
                    out correctedId))
                return false;
            if (originalId == correctedId)
            {
                document.Editor.WriteMessage(
                    "\nOriginal and corrected surfaces must be different.");
                return false;
            }

            var toleranceOptions = new PromptDoubleOptions(
                "\nMinimum absolute elevation change to flag <0.001>: ")
            {
                AllowNegative = false,
                AllowZero = true,
                UseDefaultValue = true,
                DefaultValue = 0.001
            };
            PromptDoubleResult toleranceResult =
                document.Editor.GetDouble(toleranceOptions);
            if (toleranceResult.Status != PromptStatus.OK) return false;

            var modeOptions = new PromptKeywordOptions(
                "\nReport rows [ChangedOnly/AllSampled] <ChangedOnly>: ")
            {
                AllowNone = true
            };
            modeOptions.Keywords.Add("ChangedOnly");
            modeOptions.Keywords.Add("AllSampled");
            PromptResult modeResult = document.Editor.GetKeywords(modeOptions);
            if (modeResult.Status == PromptStatus.Cancel) return false;
            bool changedOnly = modeResult.Status != PromptStatus.OK ||
                !string.Equals(
                    modeResult.StringResult,
                    "AllSampled",
                    StringComparison.OrdinalIgnoreCase);

            request = new ComparisonRequest(
                originalId,
                correctedId,
                toleranceResult.Value,
                changedOnly);
            return true;
        }

        private static ComparisonResult Compare(
            Database database,
            ComparisonRequest request)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                CivilSurface original = transaction.GetObject(
                    request.OriginalSurfaceId,
                    OpenMode.ForRead,
                    false) as CivilSurface;
                CivilSurface corrected = transaction.GetObject(
                    request.CorrectedSurfaceId,
                    OpenMode.ForRead,
                    false) as CivilSurface;
                if (original == null || corrected == null)
                    throw new InvalidOperationException(
                        "Both selected objects must be Civil 3D surfaces.");

                List<Point3d> sourcePoints = ReadSurfaceVertices(original);
                int sourceCount = sourcePoints.Count;
                if (sourceCount == 0)
                    throw new InvalidOperationException(
                        "The original surface exposes no readable vertices.");
                if (sourceCount > MaximumSamplePoints)
                {
                    int step = Math.Max(
                        1,
                        (int)Math.Ceiling(
                            sourceCount / (double)MaximumSamplePoints));
                    sourcePoints = sourcePoints
                        .Where((point, index) => index % step == 0)
                        .Take(MaximumSamplePoints)
                        .ToList();
                }

                var rows = new List<ComparisonRow>();
                int outside = 0;
                for (int index = 0; index < sourcePoints.Count; index++)
                {
                    Point3d originalPoint = sourcePoints[index];
                    double correctedElevation;
                    if (!TryFindElevation(
                            corrected,
                            originalPoint.X,
                            originalPoint.Y,
                            out correctedElevation))
                    {
                        outside++;
                        rows.Add(ComparisonRow.Outside(
                            index + 1,
                            originalPoint));
                        continue;
                    }

                    double delta = correctedElevation - originalPoint.Z;
                    bool changed = Math.Abs(delta) >= request.ChangeTolerance;
                    rows.Add(new ComparisonRow(
                        index + 1,
                        originalPoint.X,
                        originalPoint.Y,
                        originalPoint.Z,
                        correctedElevation,
                        delta,
                        changed ? ChangeType(delta) : "Unchanged"));
                }

                return new ComparisonResult(
                    ReadName(original),
                    ReadName(corrected),
                    sourceCount,
                    rows,
                    outside);
            }
        }

        private static List<Point3d> ReadSurfaceVertices(object surface)
        {
            object raw = InvokeNoArgument(surface, "GetVertices") ??
                         ReadProperty(surface, "Vertices");
            IEnumerable values = raw as IEnumerable;
            var points = new List<Point3d>();
            if (values == null) return points;

            foreach (object value in values)
            {
                Point3d point;
                if (TryReadPoint(value, out point))
                    points.Add(point);
            }
            return points;
        }

        private static bool TryFindElevation(
            object surface,
            double x,
            double y,
            out double elevation)
        {
            elevation = double.NaN;
            foreach (string name in new[]
            {
                "FindElevationAtXY",
                "GetElevationAtXY",
                "ElevationAtXY"
            })
            {
                MethodInfo method = surface.GetType().GetMethod(
                    name,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(double), typeof(double) },
                    null);
                if (method == null) continue;
                try
                {
                    object value = method.Invoke(surface, new object[] { x, y });
                    elevation = Convert.ToDouble(
                        value,
                        CultureInfo.InvariantCulture);
                    return !double.IsNaN(elevation) &&
                           !double.IsInfinity(elevation);
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        private static void ShowComparison(
            Document document,
            ComparisonResult comparison,
            ComparisonRequest request)
        {
            IEnumerable<ComparisonRow> selected = request.ChangedOnly
                ? comparison.Rows.Where(row => row.IsChanged || row.IsOutside)
                : comparison.Rows;
            List<IList<string>> rows = selected
                .Take(MaximumPopupRows)
                .Select(ToDisplayRow)
                .Cast<IList<string>>()
                .ToList();
            if (rows.Count == 0)
            {
                rows.Add(new List<string>
                {
                    "-", "-", "-", "-", "-", "0.000", "No change above tolerance"
                });
            }

            string note = string.Format(
                CultureInfo.CurrentCulture,
                "Original: {0} | corrected: {1} | source vertices={2} | sampled={3} | changed={4} | outside corrected surface={5} | tolerance={6:0.###}. The comparison is read-only and must be checked against survey control and project datum.",
                comparison.OriginalName,
                comparison.CorrectedName,
                comparison.OriginalSourceCount,
                comparison.Rows.Count,
                comparison.ChangedCount,
                comparison.OutsideCount,
                request.ChangeTolerance);

            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Survey Correction Changes",
                note,
                new List<string>
                {
                    "Point", "X", "Y", "Original Z", "Corrected Z", "Delta Z", "Change"
                },
                rows,
                "CE TOOLS SURVEY CORRECTION CHANGES");
        }

        private static List<IList<string>> BuildExportRows(
            ComparisonResult comparison,
            ComparisonRequest request)
        {
            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    "Original Surface", comparison.OriginalName,
                    "Corrected Surface", comparison.CorrectedName
                },
                new List<string>
                {
                    "Original Vertex Count", comparison.OriginalSourceCount.ToString(CultureInfo.InvariantCulture),
                    "Sampled", comparison.Rows.Count.ToString(CultureInfo.InvariantCulture),
                    "Tolerance", request.ChangeTolerance.ToString("0.######", CultureInfo.InvariantCulture)
                },
                new List<string>
                {
                    "Point", "X", "Y", "Original Z", "Corrected Z", "Delta Z", "Change"
                }
            };
            IEnumerable<ComparisonRow> selected = request.ChangedOnly
                ? comparison.Rows.Where(row => row.IsChanged || row.IsOutside)
                : comparison.Rows;
            foreach (ComparisonRow row in selected)
                rows.Add(ToDisplayRow(row));
            return rows;
        }

        private static List<string> ToDisplayRow(ComparisonRow row)
        {
            return new List<string>
            {
                row.Index.ToString(CultureInfo.InvariantCulture),
                row.X.ToString("0.###", CultureInfo.InvariantCulture),
                row.Y.ToString("0.###", CultureInfo.InvariantCulture),
                row.OriginalElevation.ToString("0.###", CultureInfo.InvariantCulture),
                row.IsOutside
                    ? "Outside"
                    : row.CorrectedElevation.ToString("0.###", CultureInfo.InvariantCulture),
                row.IsOutside
                    ? "-"
                    : row.Delta.ToString("+0.###;-0.###;0.000", CultureInfo.InvariantCulture),
                row.Change
            };
        }

        private static void WriteSummary(
            Editor editor,
            ComparisonResult comparison,
            ComparisonRequest request)
        {
            editor.WriteMessage(
                "\nCE_SURVEYCHANGES complete. Original={0}; corrected={1}; sampled={2}; changed={3}; outside={4}; tolerance={5:0.###}.",
                comparison.OriginalName,
                comparison.CorrectedName,
                comparison.Rows.Count,
                comparison.ChangedCount,
                comparison.OutsideCount,
                request.ChangeTolerance);
        }

        private static bool PromptSurface(
            Editor editor,
            string message,
            out ObjectId surfaceId)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect a Civil 3D surface.");
            options.AddAllowedClass(typeof(CivilSurface), false);
            PromptEntityResult result = editor.GetEntity(options);
            surfaceId = result.Status == PromptStatus.OK
                ? result.ObjectId
                : ObjectId.Null;
            return result.Status == PromptStatus.OK;
        }

        private static object InvokeNoArgument(object owner, string methodName)
        {
            MethodInfo method = owner.GetType().GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            if (method == null) return null;
            try { return method.Invoke(owner, null); }
            catch { return null; }
        }

        private static object ReadProperty(object owner, string propertyName)
        {
            PropertyInfo property = owner.GetType().GetProperty(
                propertyName,
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.IgnoreCase);
            if (property == null || !property.CanRead) return null;
            try { return property.GetValue(owner, null); }
            catch { return null; }
        }

        private static bool TryReadPoint(object value, out Point3d point)
        {
            if (value is Point3d)
            {
                point = (Point3d)value;
                return true;
            }
            foreach (string name in new[] { "Location", "Position", "Point" })
            {
                object raw = ReadProperty(value, name);
                if (raw is Point3d)
                {
                    point = (Point3d)raw;
                    return true;
                }
            }
            point = Point3d.Origin;
            return false;
        }

        private static string ReadName(DBObject item)
        {
            string name = Convert.ToString(
                ReadProperty(item, "Name"),
                CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(name)
                ? item.GetType().Name + " " + item.ObjectId.Handle
                : name;
        }

        private static string ChangeType(double delta)
        {
            return delta > 0.0 ? "Raised" : "Lowered";
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class ComparisonRequest
        {
            public ComparisonRequest(
                ObjectId originalSurfaceId,
                ObjectId correctedSurfaceId,
                double changeTolerance,
                bool changedOnly)
            {
                OriginalSurfaceId = originalSurfaceId;
                CorrectedSurfaceId = correctedSurfaceId;
                ChangeTolerance = changeTolerance;
                ChangedOnly = changedOnly;
            }

            public ObjectId OriginalSurfaceId { get; }
            public ObjectId CorrectedSurfaceId { get; }
            public double ChangeTolerance { get; }
            public bool ChangedOnly { get; }
        }

        private sealed class ComparisonResult
        {
            public ComparisonResult(
                string originalName,
                string correctedName,
                int originalSourceCount,
                List<ComparisonRow> rows,
                int outsideCount)
            {
                OriginalName = originalName;
                CorrectedName = correctedName;
                OriginalSourceCount = originalSourceCount;
                Rows = rows;
                OutsideCount = outsideCount;
            }

            public string OriginalName { get; }
            public string CorrectedName { get; }
            public int OriginalSourceCount { get; }
            public List<ComparisonRow> Rows { get; }
            public int OutsideCount { get; }
            public int ChangedCount => Rows.Count(row => row.IsChanged);
        }

        private sealed class ComparisonRow
        {
            public ComparisonRow(
                int index,
                double x,
                double y,
                double originalElevation,
                double correctedElevation,
                double delta,
                string change)
            {
                Index = index;
                X = x;
                Y = y;
                OriginalElevation = originalElevation;
                CorrectedElevation = correctedElevation;
                Delta = delta;
                Change = change;
            }

            public int Index { get; }
            public double X { get; }
            public double Y { get; }
            public double OriginalElevation { get; }
            public double CorrectedElevation { get; }
            public double Delta { get; }
            public string Change { get; }
            public bool IsOutside => double.IsNaN(CorrectedElevation);
            public bool IsChanged => !IsOutside &&
                !string.Equals(Change, "Unchanged", StringComparison.Ordinal);

            public static ComparisonRow Outside(int index, Point3d point)
            {
                return new ComparisonRow(
                    index,
                    point.X,
                    point.Y,
                    point.Z,
                    double.NaN,
                    double.NaN,
                    "Outside corrected surface");
            }
        }
    }
}
