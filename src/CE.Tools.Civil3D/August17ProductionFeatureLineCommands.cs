using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

[assembly: CommandClass(typeof(CETools.Civil3D.August17ProductionFeatureLineCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// August 17 production additions requested from the field-test workflow:
    /// selective corridor feature-line extraction and multi-platform slope feature lines.
    /// </summary>
    public sealed class August17ProductionFeatureLineCommands
    {
        [CommandMethod("CE_TOOLS", "CE_CORRIDORFEATURELINES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CorridorFeatureLines()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Corridor Feature Lines",
                "Create individual grading feature lines from selected corridor feature-line codes. Choose All or the required road components; exported lines can remain dynamically linked to the corridor.");
            settings.AddChoice("Scope", "01 Selection", "Feature-line selection", "Selected groups",
                "Export every available corridor feature line or only the required engineering groups/codes.",
                new[] { "All", "Selected groups" });
            settings.AddChoice("Center", "02 Groups", "Corridor centreline / crown", "Yes", "Include centre/crown/baseline-related corridor point codes.", new[] { "Yes", "No" });
            settings.AddChoice("RoadEdge", "02 Groups", "Corridor road edges", "Yes", "Include edge-of-pavement / edge-of-travel-way codes.", new[] { "Yes", "No" });
            settings.AddChoice("BottomKerb", "02 Groups", "Bottom of kerbs / gutters", "Yes", "Include bottom-kerb, gutter and flow-line codes.", new[] { "Yes", "No" });
            settings.AddChoice("TopKerb", "02 Groups", "Top of kerbs", "Yes", "Include top-of-kerb / top-of-curb codes.", new[] { "Yes", "No" });
            settings.AddChoice("BackKerb", "02 Groups", "Back of kerbs", "Yes", "Include back-of-kerb / back-of-curb codes.", new[] { "Yes", "No" });
            settings.AddChoice("Sidewalk", "02 Groups", "Sidewalk / shoulder outer edges", "Yes", "Include sidewalk, walk, shoulder and hinge codes.", new[] { "Yes", "No" });
            settings.AddChoice("Toe", "02 Groups", "Toe / daylight lines", "Yes", "Include toe, daylight, cut and fill codes.", new[] { "Yes", "No" });
            settings.AddChoice("Other", "02 Groups", "Other / exact corridor codes", "No", "Include unclassified feature lines and optionally enter exact Civil 3D point codes.", new[] { "Yes", "No" });
            settings.AddChoice("Dynamic", "03 Output", "Link exported feature lines to corridor", "Yes", "Yes keeps Civil 3D's dynamic corridor relationship; No creates independent grading feature lines.", new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            HashSet<string> exactCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (IsYes(settings.Text("Other")) && !string.Equals(settings.Text("Scope"), "All", StringComparison.OrdinalIgnoreCase))
            {
                var exact = new PromptStringOptions("\nOptional exact corridor point codes, comma-separated <Enter for none>: ")
                {
                    AllowSpaces = true
                };
                PromptResult exactResult = document.Editor.GetString(exact);
                if (exactResult.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(exactResult.StringResult))
                {
                    foreach (string value in exactResult.StringResult.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string code = value.Trim();
                        if (code.Length > 0) exactCodes.Add(code);
                    }
                }
            }

            List<ObjectId> corridorIds = SelectCorridors(document);
            if (corridorIds.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_CORRIDORFEATURELINES cancelled. No Civil 3D corridors were selected.");
                return;
            }

            bool all = string.Equals(settings.Text("Scope"), "All", StringComparison.OrdinalIgnoreCase);
            bool dynamic = IsYes(settings.Text("Dynamic"));
            var seen = new HashSet<int>();
            int scanned = 0;
            int matched = 0;
            int created = 0;
            int failed = 0;

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId corridorId in corridorIds.Distinct())
                {
                    Corridor corridor;
                    try { corridor = transaction.GetObject(corridorId, OpenMode.ForRead, false) as Corridor; }
                    catch { corridor = null; }
                    if (corridor == null) continue;

                    foreach (Baseline baseline in corridor.Baselines)
                    {
                        foreach (CorridorFeatureLine line in EnumerateBaselineFeatureLines(baseline))
                        {
                            if (line == null) continue;
                            int identity = RuntimeHelpers.GetHashCode(line);
                            if (!seen.Add(identity)) continue;
                            scanned++;

                            string code = line.CodeName ?? string.Empty;
                            if (!all && !MatchesRequestedGroup(code, settings, exactCodes)) continue;
                            matched++;
                            try
                            {
                                ObjectId id = line.ExportAsGradingFeatureLine(ObjectId.Null, dynamic);
                                if (!id.IsNull) created++;
                                else failed++;
                            }
                            catch
                            {
                                failed++;
                            }
                        }
                    }
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_CORRIDORFEATURELINES complete. Corridors={0}; scanned={1}; matched={2}; feature lines created={3}; failed={4}; dynamic={5}.",
                corridorIds.Distinct().Count(), scanned, matched, created, failed, dynamic ? "Yes" : "No");
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMFEATURELINESLOPE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void PlatformFeatureLinesAtSlope()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var referenceOptions = new PromptEntityOptions("\nSelect the reference feature line for platform slope control: ");
            referenceOptions.SetRejectMessage("\nSelect a Civil 3D feature line.");
            referenceOptions.AddAllowedClass(typeof(CivilFeatureLine), false);
            PromptEntityResult referenceResult = document.Editor.GetEntity(referenceOptions);
            if (referenceResult.Status != PromptStatus.OK) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Platform Feature Lines at Slope",
                "Convert multiple platform polylines to individual feature lines whose vertex elevations satisfy a fixed or minimum slope relative to the selected reference feature line.");
            settings.AddChoice("Mode", "01 Grade", "Slope rule", "Fixed slope",
                "Fixed slope forces the calculated grade. Minimum slope keeps an existing vertex only when it is already at least as steep in the requested direction.",
                new[] { "Fixed slope", "Minimum slope" });
            settings.AddDouble("Slope", "01 Grade", "Slope (%)", 2.0, "Positive design slope magnitude in percent.");
            settings.AddChoice("Direction", "02 Direction", "Slope direction", "Fall away from reference",
                "Fall away lowers the target with distance; Fall toward raises the target with distance toward the platform polyline.",
                new[] { "Fall away from reference", "Fall toward reference" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            List<ObjectId> targetIds = SelectPlatformPolylines(document, referenceResult.ObjectId);
            if (targetIds.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_PLATFORMFEATURELINESLOPE cancelled. No platform polylines were selected.");
                return;
            }

            double slope = Math.Abs(settings.Double("Slope", 2.0)) / 100.0;
            bool minimum = string.Equals(settings.Text("Mode"), "Minimum slope", StringComparison.OrdinalIgnoreCase);
            bool away = string.Equals(settings.Text("Direction"), "Fall away from reference", StringComparison.OrdinalIgnoreCase);
            int created = 0;
            int failed = 0;

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine reference = transaction.GetObject(referenceResult.ObjectId, OpenMode.ForRead, false) as CivilFeatureLine;
                if (reference == null) return;
                Point3dCollection referencePoints = reference.GetPoints(FeatureLinePointType.AllPoints);
                List<Point3d> controls = referencePoints.Cast<Point3d>().ToList();
                if (controls.Count < 2)
                {
                    document.Editor.WriteMessage("\nThe selected reference feature line does not contain enough control points.");
                    return;
                }

                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return;

                foreach (ObjectId id in targetIds.Distinct())
                {
                    Polyline source;
                    try { source = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch { source = null; }
                    if (source == null || source.NumberOfVertices < 2) { failed++; continue; }

                    var outputPoints = new Point3dCollection();
                    try
                    {
                        for (int index = 0; index < source.NumberOfVertices; index++)
                        {
                            Point3d sourcePoint = source.GetPoint3dAt(index);
                            double referenceZ;
                            double planDistance;
                            ClosestReference(sourcePoint, controls, out referenceZ, out planDistance);
                            double requiredZ = away
                                ? referenceZ - (planDistance * slope)
                                : referenceZ + (planDistance * slope);
                            double z = requiredZ;
                            if (minimum)
                            {
                                z = away
                                    ? (sourcePoint.Z <= requiredZ ? sourcePoint.Z : requiredZ)
                                    : (sourcePoint.Z >= requiredZ ? sourcePoint.Z : requiredZ);
                            }
                            outputPoints.Add(new Point3d(sourcePoint.X, sourcePoint.Y, z));
                        }

                        var temporary = new Polyline3d(Poly3dType.SimplePoly, outputPoints, source.Closed);
                        temporary.SetDatabaseDefaults(document.Database);
                        temporary.LayerId = source.LayerId;
                        ObjectId temporaryId = space.AppendEntity(temporary);
                        transaction.AddNewlyCreatedDBObject(temporary, true);
                        ObjectId featureLineId = CivilFeatureLine.Create(string.Empty, temporaryId);
                        if (!featureLineId.IsNull) created++;
                        else failed++;
                        temporary.Erase();
                    }
                    catch
                    {
                        failed++;
                    }
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PLATFORMFEATURELINESLOPE complete. Feature lines created={0}; failed={1}; rule={2}; slope={3:0.###}%; direction={4}.",
                created, failed, minimum ? "Minimum" : "Fixed", slope * 100.0, away ? "Away" : "Toward");
        }

        private static bool IsYes(string value)
        {
            return string.Equals(value, "Yes", StringComparison.OrdinalIgnoreCase);
        }

        private static List<ObjectId> SelectCorridors(Document document)
        {
            PromptSelectionResult result = document.Editor.SelectImplied();
            if (result.Status != PromptStatus.OK || result.Value == null || result.Value.Count == 0)
            {
                var options = new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect one or more Civil 3D corridors: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                };
                result = document.Editor.GetSelection(options);
            }
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            if (result.Status != PromptStatus.OK || result.Value == null) return new List<ObjectId>();

            var valid = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in result.Value.GetObjectIds())
                {
                    try
                    {
                        if (transaction.GetObject(id, OpenMode.ForRead, false) is Corridor) valid.Add(id);
                    }
                    catch { }
                }
            }
            return valid;
        }

        private static IEnumerable<CorridorFeatureLine> EnumerateBaselineFeatureLines(Baseline baseline)
        {
            if (baseline == null) yield break;
            foreach (CorridorFeatureLine line in EnumerateFeatureContainer(baseline.MainBaselineFeatureLines))
                yield return line;

            PropertyInfo offsetProperty = baseline.GetType().GetProperty("OffsetBaselineFeatureLinesCol", BindingFlags.Public | BindingFlags.Instance);
            object offsets = offsetProperty == null ? null : offsetProperty.GetValue(baseline, null);
            foreach (object value in AsObjects(offsets))
                foreach (CorridorFeatureLine line in EnumerateFeatureContainer(value))
                    yield return line;
        }

        private static IEnumerable<CorridorFeatureLine> EnumerateFeatureContainer(object container)
        {
            if (container == null) yield break;
            PropertyInfo mapProperty = container.GetType().GetProperty("FeatureLineCollectionMap", BindingFlags.Public | BindingFlags.Instance);
            object map = mapProperty == null ? null : mapProperty.GetValue(container, null);
            foreach (object collection in AsObjects(map))
            {
                foreach (CorridorFeatureLine line in FindCorridorFeatureLines(collection))
                    yield return line;
            }
        }

        private static IEnumerable<CorridorFeatureLine> FindCorridorFeatureLines(object value)
        {
            if (value == null) yield break;
            CorridorFeatureLine direct = value as CorridorFeatureLine;
            if (direct != null) { yield return direct; yield break; }

            PropertyInfo valueProperty = value.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (valueProperty != null)
            {
                object nested = valueProperty.GetValue(value, null);
                foreach (CorridorFeatureLine line in FindCorridorFeatureLines(nested)) yield return line;
                yield break;
            }

            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null || value is string) yield break;
            foreach (object nested in enumerable)
                foreach (CorridorFeatureLine line in FindCorridorFeatureLines(nested)) yield return line;
        }

        private static IEnumerable<object> AsObjects(object value)
        {
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null || value is string) yield break;
            foreach (object item in enumerable) yield return item;
        }

        private static bool MatchesRequestedGroup(string code, ProductionSettingsDialogModel settings, HashSet<string> exactCodes)
        {
            string normalized = Regex.Replace((code ?? string.Empty).ToUpperInvariant(), "[^A-Z0-9]", string.Empty);
            if (exactCodes.Contains(code ?? string.Empty) || exactCodes.Any(item => string.Equals(Regex.Replace(item.ToUpperInvariant(), "[^A-Z0-9]", string.Empty), normalized, StringComparison.OrdinalIgnoreCase))) return true;
            if (IsYes(settings.Text("Center")) && ContainsAny(normalized, "CENTER", "CENTRE", "CROWN", "BASELINE", "CL")) return true;
            if (IsYes(settings.Text("RoadEdge")) && ContainsAny(normalized, "ETW", "EOP", "EDGE", "PAVEEDGE", "EDGEPAVE")) return true;
            if (IsYes(settings.Text("BottomKerb")) && ContainsAny(normalized, "BOTTOMKERB", "BOTTOMCURB", "BOK", "GUTTER", "FLOWLINE")) return true;
            if (IsYes(settings.Text("TopKerb")) && ContainsAny(normalized, "TOPKERB", "TOPCURB", "TOK", "TOC")) return true;
            if (IsYes(settings.Text("BackKerb")) && ContainsAny(normalized, "BACKKERB", "BACKCURB", "BCK")) return true;
            if (IsYes(settings.Text("Sidewalk")) && ContainsAny(normalized, "SIDEWALK", "WALK", "SHOULDER", "SHLDR", "HINGE")) return true;
            if (IsYes(settings.Text("Toe")) && ContainsAny(normalized, "TOE", "DAYLIGHT", "CUT", "FILL")) return true;
            if (IsYes(settings.Text("Other")) && !IsKnownGroup(normalized)) return true;
            return false;
        }

        private static bool IsKnownGroup(string normalized)
        {
            return ContainsAny(normalized,
                "CENTER", "CENTRE", "CROWN", "BASELINE", "CL", "ETW", "EOP", "EDGE", "PAVEEDGE", "EDGEPAVE",
                "BOTTOMKERB", "BOTTOMCURB", "BOK", "GUTTER", "FLOWLINE", "TOPKERB", "TOPCURB", "TOK", "TOC",
                "BACKKERB", "BACKCURB", "BCK", "SIDEWALK", "WALK", "SHOULDER", "SHLDR", "HINGE", "TOE", "DAYLIGHT", "CUT", "FILL");
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            foreach (string term in terms)
                if (value.Contains(term)) return true;
            return false;
        }

        private static List<ObjectId> SelectPlatformPolylines(Document document, ObjectId referenceId)
        {
            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                var options = new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect multiple platform polylines to convert to slope-controlled feature lines: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                };
                selection = document.Editor.GetSelection(options,
                    new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") }));
            }
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return new List<ObjectId>();
            return selection.Value.GetObjectIds().Where(id => !id.IsNull && !id.IsErased && id != referenceId).ToList();
        }

        private static void ClosestReference(Point3d point, IList<Point3d> controls, out double z, out double distance)
        {
            z = controls[0].Z;
            distance = double.MaxValue;
            for (int index = 0; index < controls.Count - 1; index++)
            {
                Point3d a = controls[index];
                Point3d b = controls[index + 1];
                double vx = b.X - a.X;
                double vy = b.Y - a.Y;
                double length2 = (vx * vx) + (vy * vy);
                double t = length2 <= 1e-12 ? 0.0 : (((point.X - a.X) * vx) + ((point.Y - a.Y) * vy)) / length2;
                t = Math.Max(0.0, Math.Min(1.0, t));
                double x = a.X + (vx * t);
                double y = a.Y + (vy * t);
                double dx = point.X - x;
                double dy = point.Y - y;
                double candidate = Math.Sqrt((dx * dx) + (dy * dy));
                if (candidate < distance)
                {
                    distance = candidate;
                    z = a.Z + ((b.Z - a.Z) * t);
                }
            }
            if (double.IsInfinity(distance) || distance == double.MaxValue) distance = 0.0;
        }
    }

    internal static class August17ProjectRuntime
    {
        internal static int PreferredLoCentralMeridian(Document document)
        {
            if (document != null)
            {
                try
                {
                    IDictionary<string, string> project = ProjectSetupCommands.ReadSharedProjectMetadata(document.Database);
                    string coordinateSystem;
                    if (project != null && project.TryGetValue("Coordinate System", out coordinateSystem))
                    {
                        int parsed = ParseLo(coordinateSystem);
                        if (parsed > 0) return parsed;
                    }
                    string town;
                    if (project != null && project.TryGetValue("Town", out town) && !string.IsNullOrWhiteSpace(town))
                    {
                        int parsed = ParseLo(NamibiaCoordinateSystemCatalog.PreferredLoName(town));
                        if (parsed > 0) return parsed;
                    }
                }
                catch { }
            }

            int inferred;
            try { NamibiaCoordinateRuntime.TryInferLoZone(out inferred); }
            catch { inferred = 0; }
            return inferred > 0 ? inferred : 17;
        }

        internal static bool TryInsertRegisteredClientBookTitleBlock(
            Database database,
            Transaction transaction,
            BlockTableRecord paperSpace,
            ICollection<string> generated,
            string paperName,
            string layoutName,
            string pageNumber,
            string pageTitle,
            string stage,
            string revision,
            out string diagnostic)
        {
            diagnostic = string.Empty;
            if (database == null || transaction == null || paperSpace == null) return false;
            try
            {
                ProductionDrawingRegisterData register = ProductionDrawingRegisterStore.Read(database);
                register.ApplyProjectDefaults(ProjectSetupCommands.ReadSharedProjectMetadata(database));
                string sourcePath = register.Header("Title Block Source");
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    diagnostic = "No Title Block Source is saved in the Drawing Register.";
                    return false;
                }

                string prefix = register.Header("Drawing Number Prefix");
                if (string.IsNullOrWhiteSpace(prefix)) prefix = "CE";
                var row = new ProductionDrawingRegisterRow
                {
                    Layout = layoutName ?? string.Empty,
                    DrawingNumber = prefix + "-CB-" + (string.IsNullOrWhiteSpace(pageNumber) ? "001" : pageNumber),
                    Title = pageTitle ?? string.Empty,
                    Purpose = "Client Book",
                    Paper = paperName ?? string.Empty,
                    Scale = "As shown",
                    Stage = stage ?? register.Header("Project Stage"),
                    Revision = revision ?? register.Header("Revision"),
                    IssueDate = register.Header("Issue Date")
                };

                ObjectId inserted = ProductionTitleBlockManager.TryInsert(
                    database,
                    transaction,
                    paperSpace,
                    sourcePath,
                    paperName,
                    Point3d.Origin,
                    register,
                    row,
                    out diagnostic);
                if (inserted.IsNull) return false;
                if (generated != null) generated.Add(inserted.Handle.ToString());
                return true;
            }
            catch (Exception ex)
            {
                diagnostic = ex.Message;
                return false;
            }
        }

        private static int ParseLo(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            Match match = Regex.Match(value, @"(?i)\bLO\s*[-/]?\s*(\d{1,2})\b");
            if (!match.Success) match = Regex.Match(value, @"(?i)\bLo(\d{1,2})\b");
            int result;
            return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
                ? result
                : 0;
        }
    }
}
