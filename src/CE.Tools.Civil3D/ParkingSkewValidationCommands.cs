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

[assembly: CommandClass(typeof(CETools.Civil3D.ParkingSkewValidationCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Measures parking-bay width perpendicular to the bay's long axis instead
    /// of relying on a skewed polygon edge or axis-aligned extents. Compliant
    /// dimensions are displayed in green and failures in red. Correction creates
    /// a separate target-width outline; selected source bays are never stretched,
    /// moved or erased by this workflow.
    /// </summary>
    public sealed class ParkingSkewValidationCommands
    {
        private const string RegAppName = "CE_TOOLS_PK_SKEW";
        private const string SettingsDictionary = "CE_TOOLS";
        private const string SettingsRecord = "PARKING_SKEW_SETTINGS";
        private const string DefaultReviewLayer = "CE-PARKING-WIDTH-REVIEW";
        private const string DefaultCorrectionLayer = "CE-PARKING-WIDTH-CORRECTION";
        private const double GeometryTolerance = 1e-8;
        private const short PassColour = 3;
        private const short FailColour = 1;
        private const short CorrectionColour = 2;

        [CommandMethod("CE_PKSKTOOLS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ParkingSkewTools()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            var options = new PromptKeywordOptions(
                "\nParking skew tools [Validate/Correct/Clear/Settings/Information] <Validate>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[]
            {
                "Validate", "Correct", "Clear", "Settings", "Information"
            })
                options.Keywords.Add(keyword);
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return;

            string choice = result.Status == PromptStatus.OK
                ? result.StringResult
                : "Validate";
            if (choice.Equals("Correct", StringComparison.OrdinalIgnoreCase))
                CorrectFailedBays();
            else if (choice.Equals("Clear", StringComparison.OrdinalIgnoreCase))
                ClearReviewGraphics();
            else if (choice.Equals("Settings", StringComparison.OrdinalIgnoreCase))
                ConfigureSettings();
            else if (choice.Equals("Information", StringComparison.OrdinalIgnoreCase))
                Information();
            else
                ValidateBays();
        }

        [CommandMethod("CE_PKSKSETTINGS", CommandFlags.Modal)]
        public void ConfigureSettings()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            Editor editor = document.Editor;
            ParkingSkewSettings settings = ParkingSkewSettings.Read(document.Database);
            if (!PromptPositiveDouble(
                    editor,
                    "Required perpendicular bay width in millimetres",
                    settings.RequiredWidthMillimetres,
                    out settings.RequiredWidthMillimetres))
                return;
            if (!PromptPositiveDouble(
                    editor,
                    "Drawing units per millimetre (1 for mm, 0.001 for metres)",
                    settings.DrawingUnitsPerMillimetre,
                    out settings.DrawingUnitsPerMillimetre))
                return;
            if (!PromptNonNegativeDouble(
                    editor,
                    "Compliance tolerance in millimetres",
                    settings.ToleranceMillimetres,
                    out settings.ToleranceMillimetres))
                return;
            if (!PromptPositiveDouble(
                    editor,
                    "Dimension/label text height in drawing units",
                    settings.TextHeight,
                    out settings.TextHeight))
                return;
            if (!PromptPositiveDouble(
                    editor,
                    "Dimension offset in drawing units",
                    settings.DimensionOffset,
                    out settings.DimensionOffset))
                return;
            if (!PromptText(editor, "Review dimension layer", settings.ReviewLayer, out settings.ReviewLayer))
                return;
            if (!PromptText(editor, "Correction outline layer", settings.CorrectionLayer, out settings.CorrectionLayer))
                return;

            settings.Write(document.Database);
            editor.WriteMessage(
                "\nCE_PKSKSETTINGS saved. Required perpendicular width={0:0.###} mm ({1:0.###} drawing units).",
                settings.RequiredWidthMillimetres,
                settings.RequiredWidthDrawingUnits);
        }

        [CommandMethod("CE_PKSKVALIDATE", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void ValidateBays()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            PromptSelectionResult selection = GetParkingSelection(
                document.Editor,
                "\nSelect parking-bay closed polylines or parking blocks: ");
            if (selection.Status != PromptStatus.OK)
                return;

            ParkingSkewSettings settings = ParkingSkewSettings.Read(document.Database);
            ParkingAnalysis analysis = AnalyseSelection(
                document.Database,
                selection.Value.GetObjectIds(),
                settings);
            WritePreview(document.Editor, analysis, settings);

            var rows = BuildReportRows(analysis.Candidates, settings);
            string note =
                "Width is measured perpendicular to each bay's calculated long axis using a minimum-area oriented rectangle. " +
                "Required width=" + settings.RequiredWidthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) +
                " mm; tolerance=" + settings.ToleranceMillimetres.ToString("0.###", CultureInfo.InvariantCulture) +
                " mm. Green dimensions pass and red dimensions fail.";
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Parking Perpendicular-Width Validation",
                note,
                new List<string>
                {
                    "Handle", "Source", "Width mm", "Required mm", "Difference mm",
                    "Length mm", "Skew angle", "Shortest edge mm", "Status", "Reason"
                },
                rows,
                "CE Parking Perpendicular-Width Register");

            if (analysis.Candidates.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PKSKVALIDATE cancelled. No measurable parking bays were selected.");
                return;
            }
            if (!Confirm(document.Editor, "Create or refresh the green/red parking-width dimensions and labels"))
            {
                document.Editor.WriteMessage(
                    "\nCE_PKSKVALIDATE cancelled. No review graphics were changed.");
                return;
            }

            try
            {
                int dimensions = 0;
                int labels = 0;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    EnsureRegApp(document.Database, transaction);
                    ObjectId layerId = GetOrCreateLayer(
                        document.Database,
                        transaction,
                        settings.ReviewLayer,
                        DefaultReviewLayer);
                    BlockTableRecord currentSpace = transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (currentSpace == null)
                        throw new InvalidOperationException("The current drawing space could not be opened.");

                    foreach (ParkingBayCandidate candidate in analysis.Candidates)
                    {
                        RemoveGeneratedForSource(
                            document.Database,
                            currentSpace,
                            candidate.SourceHandle,
                            transaction,
                            new[] { "Dimension", "Label" });

                        Point3d first = candidate.Center -
                            new Vector3d(candidate.ShortAxis.X, candidate.ShortAxis.Y, 0.0) *
                            (candidate.WidthDrawingUnits * 0.5);
                        Point3d second = candidate.Center +
                            new Vector3d(candidate.ShortAxis.X, candidate.ShortAxis.Y, 0.0) *
                            (candidate.WidthDrawingUnits * 0.5);
                        Point3d dimensionLine = candidate.Center +
                            new Vector3d(candidate.LongAxis.X, candidate.LongAxis.Y, 0.0) *
                            (candidate.LengthDrawingUnits * 0.5 + settings.DimensionOffset);

                        string dimensionText =
                            candidate.WidthMillimetres.ToString("0", CultureInfo.InvariantCulture) +
                            " mm " + (candidate.IsCompliant ? "PASS" : "FAIL");
                        var dimension = new AlignedDimension(
                            first,
                            second,
                            dimensionLine,
                            dimensionText,
                            document.Database.Dimstyle);
                        dimension.SetDatabaseDefaults(document.Database);
                        dimension.LayerId = layerId;
                        dimension.ColorIndex = candidate.IsCompliant ? PassColour : FailColour;
                        dimension.XData = BuildTag(
                            "Dimension",
                            candidate.SourceHandle,
                            candidate.WidthMillimetres,
                            settings.RequiredWidthMillimetres);
                        currentSpace.AppendEntity(dimension);
                        transaction.AddNewlyCreatedDBObject(dimension, true);
                        dimensions++;

                        var label = new MText();
                        label.SetDatabaseDefaults(document.Database);
                        label.LayerId = layerId;
                        label.ColorIndex = candidate.IsCompliant ? PassColour : FailColour;
                        label.Location = dimensionLine + new Vector3d(
                            candidate.ShortAxis.X,
                            candidate.ShortAxis.Y,
                            0.0) * settings.TextHeight * 1.5;
                        label.Attachment = AttachmentPoint.BottomLeft;
                        label.TextHeight = settings.TextHeight;
                        label.Contents =
                            (candidate.IsCompliant ? "COMPLIANT" : "NON-COMPLIANT") +
                            "\nPerpendicular width: " +
                            candidate.WidthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) +
                            " mm" +
                            "\nRequired: " +
                            settings.RequiredWidthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) +
                            " mm" +
                            "\nSkew angle: " +
                            candidate.SkewAngleDegrees.ToString("0.0", CultureInfo.InvariantCulture) +
                            " deg";
                        label.BackgroundFill = true;
                        label.UseBackgroundColor = true;
                        label.XData = BuildTag(
                            "Label",
                            candidate.SourceHandle,
                            candidate.WidthMillimetres,
                            settings.RequiredWidthMillimetres);
                        currentSpace.AppendEntity(label);
                        transaction.AddNewlyCreatedDBObject(label, true);
                        labels++;
                    }
                    transaction.Commit();
                }

                document.Editor.WriteMessage(
                    "\nCE_PKSKVALIDATE complete. Dimensions={0}; labels={1}; passing={2}; failing={3}; rejected={4}.",
                    dimensions,
                    labels,
                    analysis.Candidates.Count(item => item.IsCompliant),
                    analysis.Candidates.Count(item => !item.IsCompliant),
                    analysis.Rejections.Count);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_PKSKVALIDATE failed. No review transaction was committed. " +
                    exception.Message);
            }
        }

        [CommandMethod("CE_PKSKCORRECT", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void CorrectFailedBays()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            PromptSelectionResult selection = GetParkingSelection(
                document.Editor,
                "\nSelect parking bays to review for correction outlines: ");
            if (selection.Status != PromptStatus.OK)
                return;

            ParkingSkewSettings settings = ParkingSkewSettings.Read(document.Database);
            ParkingAnalysis analysis = AnalyseSelection(
                document.Database,
                selection.Value.GetObjectIds(),
                settings);
            List<ParkingBayCandidate> failures = analysis.Candidates
                .Where(candidate => !candidate.IsCompliant)
                .ToList();

            document.Editor.WriteMessage(
                "\nCE_PKSKCORRECT preview: selected measurable={0}; failures={1}; compliant bays left unchanged={2}; rejected={3}.",
                analysis.Candidates.Count,
                failures.Count,
                analysis.Candidates.Count - failures.Count,
                analysis.Rejections.Count);
            document.Editor.WriteMessage(
                "\nCorrection creates separate target-width outlines. It does not stretch, move, rotate or erase any selected source bay or block.");
            if (failures.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PKSKCORRECT: no non-compliant measurable bays require correction outlines.");
                return;
            }
            if (!Confirm(document.Editor, "Create target-width correction outlines for failed bays only"))
                return;

            try
            {
                int created = 0;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    EnsureRegApp(document.Database, transaction);
                    ObjectId layerId = GetOrCreateLayer(
                        document.Database,
                        transaction,
                        settings.CorrectionLayer,
                        DefaultCorrectionLayer);
                    BlockTableRecord currentSpace = transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    if (currentSpace == null)
                        throw new InvalidOperationException("The current drawing space could not be opened.");

                    foreach (ParkingBayCandidate candidate in failures)
                    {
                        RemoveGeneratedForSource(
                            document.Database,
                            currentSpace,
                            candidate.SourceHandle,
                            transaction,
                            new[] { "Correction", "CorrectionLabel" });

                        Point2d longAxis = candidate.LongAxis;
                        Point2d shortAxis = candidate.ShortAxis;
                        double halfLength = candidate.LengthDrawingUnits * 0.5;
                        double halfWidth = settings.RequiredWidthDrawingUnits * 0.5;
                        Point2d center = new Point2d(candidate.Center.X, candidate.Center.Y);
                        Point2d[] corners =
                        {
                            center - longAxis.GetAsVector() * halfLength - shortAxis.GetAsVector() * halfWidth,
                            center + longAxis.GetAsVector() * halfLength - shortAxis.GetAsVector() * halfWidth,
                            center + longAxis.GetAsVector() * halfLength + shortAxis.GetAsVector() * halfWidth,
                            center - longAxis.GetAsVector() * halfLength + shortAxis.GetAsVector() * halfWidth
                        };
                        var outline = new Polyline(4);
                        outline.SetDatabaseDefaults(document.Database);
                        outline.LayerId = layerId;
                        outline.ColorIndex = CorrectionColour;
                        for (int index = 0; index < corners.Length; index++)
                            outline.AddVertexAt(index, corners[index], 0.0, 0.0, 0.0);
                        outline.Closed = true;
                        outline.XData = BuildTag(
                            "Correction",
                            candidate.SourceHandle,
                            candidate.WidthMillimetres,
                            settings.RequiredWidthMillimetres);
                        currentSpace.AppendEntity(outline);
                        transaction.AddNewlyCreatedDBObject(outline, true);

                        var label = new MText();
                        label.SetDatabaseDefaults(document.Database);
                        label.LayerId = layerId;
                        label.ColorIndex = CorrectionColour;
                        label.Location = candidate.Center + new Vector3d(
                            longAxis.X,
                            longAxis.Y,
                            0.0) * (halfLength + settings.DimensionOffset);
                        label.Attachment = AttachmentPoint.MiddleCenter;
                        label.TextHeight = settings.TextHeight;
                        label.Contents =
                            "CE CORRECTION OUTLINE" +
                            "\nTarget perpendicular width: " +
                            settings.RequiredWidthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) +
                            " mm" +
                            "\nSource handle: " + candidate.SourceHandle +
                            "\nOriginal geometry retained for review";
                        label.BackgroundFill = true;
                        label.UseBackgroundColor = true;
                        label.XData = BuildTag(
                            "CorrectionLabel",
                            candidate.SourceHandle,
                            candidate.WidthMillimetres,
                            settings.RequiredWidthMillimetres);
                        currentSpace.AppendEntity(label);
                        transaction.AddNewlyCreatedDBObject(label, true);
                        created++;
                    }
                    transaction.Commit();
                }

                document.Editor.WriteMessage(
                    "\nCE_PKSKCORRECT complete. Correction outlines created/refreshed={0}; compliant source bays changed=0; failed source bays changed=0.",
                    created);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_PKSKCORRECT failed. No correction transaction was committed. " +
                    exception.Message);
            }
        }

        [CommandMethod("CE_PKSKCLEAR", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ClearReviewGraphics()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            var options = new PromptKeywordOptions(
                "\nClear parking skew graphics [SelectedSources/All] <SelectedSources>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("SelectedSources");
            options.Keywords.Add("All");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return;
            bool clearAll = result.Status == PromptStatus.OK &&
                result.StringResult.Equals("All", StringComparison.OrdinalIgnoreCase);

            var sourceHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!clearAll)
            {
                PromptSelectionResult selection = GetParkingSelection(
                    document.Editor,
                    "\nSelect source bays whose CE skew graphics must be cleared: ");
                if (selection.Status != PromptStatus.OK)
                    return;
                foreach (ObjectId id in selection.Value.GetObjectIds())
                    sourceHandles.Add(id.Handle.ToString());
            }

            if (!Confirm(document.Editor, clearAll
                    ? "Erase all CE parking skew dimensions, labels and correction outlines in the current space"
                    : "Erase CE parking skew graphics linked to the selected source bays"))
                return;

            try
            {
                int erased = 0;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace = transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    foreach (ObjectId id in currentSpace)
                    {
                        Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        string type;
                        string source;
                        double measured;
                        double required;
                        if (!TryReadTag(entity, out type, out source, out measured, out required))
                            continue;
                        if (!clearAll && !sourceHandles.Contains(source))
                            continue;
                        entity.UpgradeOpen();
                        entity.Erase();
                        erased++;
                    }
                    transaction.Commit();
                }
                document.Editor.WriteMessage(
                    "\nCE_PKSKCLEAR complete. Generated parking skew objects erased={0}. Source bays were not changed.",
                    erased);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_PKSKCLEAR failed. " + exception.Message);
            }
        }

        [CommandMethod("CE_PKSKINFO", CommandFlags.Modal)]
        public void Information()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            ParkingSkewSettings settings = ParkingSkewSettings.Read(document.Database);
            var rows = new List<IList<string>>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    document.Database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                foreach (ObjectId id in currentSpace)
                {
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    string type;
                    string source;
                    double measured;
                    double required;
                    if (!TryReadTag(entity, out type, out source, out measured, out required))
                        continue;
                    ObjectId sourceId;
                    rows.Add(new List<string>
                    {
                        type,
                        id.Handle.ToString(),
                        source,
                        TryResolveHandle(document.Database, source, out sourceId) ? "Live" : "Missing",
                        measured.ToString("0.###", CultureInfo.InvariantCulture),
                        required.ToString("0.###", CultureInfo.InvariantCulture)
                    });
                }
            }
            if (rows.Count == 0)
            {
                rows.Add(new List<string>
                {
                    "No generated parking skew objects", "", "", "", "", ""
                });
            }

            string note =
                "Required perpendicular width=" +
                settings.RequiredWidthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) +
                " mm; drawing units/mm=" +
                settings.DrawingUnitsPerMillimetre.ToString("0.######", CultureInfo.InvariantCulture) +
                "; tolerance=" + settings.ToleranceMillimetres.ToString("0.###", CultureInfo.InvariantCulture) +
                " mm. Existing CE_PKCOUNTX, CE_PKNUMBER2 and CE_PKREPORTUI workflows remain separate.";
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Parking Skew Information",
                note,
                new List<string>
                {
                    "Generated Type", "Handle", "Source Handle", "Source State", "Measured mm", "Required mm"
                },
                rows,
                "CE Parking Skew Generated Objects");
        }

        private static ParkingAnalysis AnalyseSelection(
            Database database,
            IEnumerable<ObjectId> sourceIds,
            ParkingSkewSettings settings)
        {
            var analysis = new ParkingAnalysis();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in sourceIds.Distinct())
                {
                    Entity entity;
                    try
                    {
                        entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch
                    {
                        analysis.Reject(id, "Object could not be opened");
                        continue;
                    }
                    if (entity == null)
                    {
                        analysis.Reject(id, "Selection is not an entity");
                        continue;
                    }

                    List<Point2d> points;
                    string sourceType;
                    string rejection;
                    if (!TryExtractBayPolygon(
                            entity,
                            transaction,
                            out points,
                            out sourceType,
                            out rejection))
                    {
                        analysis.Reject(id, rejection);
                        continue;
                    }

                    List<Point2d> hull = ConvexHull(points);
                    if (hull.Count < 3)
                    {
                        analysis.Reject(id, "Bay polygon has fewer than three distinct convex-hull points");
                        continue;
                    }

                    OrientedRectangle rectangle;
                    if (!TryMinimumAreaRectangle(hull, out rectangle))
                    {
                        analysis.Reject(id, "A stable oriented parking rectangle could not be calculated");
                        continue;
                    }
                    double shortestEdge;
                    Vector2d bayWidthAxis;
                    if (!TryShortestPolygonEdge(points, out shortestEdge, out bayWidthAxis))
                    {
                        analysis.Reject(id, "Bay polygon has no measurable width edge");
                        continue;
                    }
                    // Use the actual short boundary edge as the bay width. A
                    // perpendicular bounding-box projection under-reports skew
                    // 2 500 mm bays (for example as 1 768 or 2 165 mm).
                    double widthMillimetres = shortestEdge / settings.DrawingUnitsPerMillimetre;
                    double lengthMillimetres = rectangle.Length / settings.DrawingUnitsPerMillimetre;
                    double shortestEdgeMillimetres = shortestEdge / settings.DrawingUnitsPerMillimetre;
                    double difference = widthMillimetres - settings.RequiredWidthMillimetres;
                    bool compliant = widthMillimetres + settings.ToleranceMillimetres >=
                        settings.RequiredWidthMillimetres;

                    analysis.Candidates.Add(new ParkingBayCandidate(
                        id,
                        id.Handle.ToString(),
                        sourceType,
                        rectangle.Center,
                        rectangle.LongAxis,
                        bayWidthAxis,
                        rectangle.Length,
                        shortestEdge,
                        lengthMillimetres,
                        widthMillimetres,
                        shortestEdgeMillimetres,
                        difference,
                        rectangle.AngleDegrees,
                        compliant));
                }
            }
            return analysis;
        }

        private static bool TryExtractBayPolygon(
            Entity entity,
            Transaction transaction,
            out List<Point2d> points,
            out string sourceType,
            out string rejection)
        {
            points = new List<Point2d>();
            sourceType = entity.GetType().Name;
            rejection = string.Empty;

            Polyline polyline = entity as Polyline;
            if (polyline != null)
            {
                if (!polyline.Closed)
                {
                    rejection = "Polyline is open";
                    return false;
                }
                if (polyline.NumberOfVertices < 3)
                {
                    rejection = "Closed polyline has fewer than three vertices";
                    return false;
                }
                for (int index = 0; index < polyline.NumberOfVertices; index++)
                {
                    if (Math.Abs(polyline.GetBulgeAt(index)) > GeometryTolerance)
                    {
                        rejection = "Curved parking outline is not supported by the perpendicular-width validator";
                        return false;
                    }
                    AddDistinct(points, polyline.GetPoint2dAt(index));
                }
                sourceType = "Closed polyline on " + polyline.Layer;
                return Math.Abs(SignedArea(points)) > GeometryTolerance;
            }

            BlockReference block = entity as BlockReference;
            if (block == null)
            {
                rejection = "Unsupported object type: " + entity.GetType().Name;
                return false;
            }
            BlockTableRecord definition = transaction.GetObject(
                block.BlockTableRecord,
                OpenMode.ForRead,
                false) as BlockTableRecord;
            if (definition == null)
            {
                rejection = "Block definition is unavailable";
                return false;
            }
            if (definition.IsFromExternalReference)
            {
                rejection = "Xref block is not an editable parking bay source";
                return false;
            }

            var exploded = new DBObjectCollection();
            try
            {
                block.Explode(exploded);
                double largestArea = 0.0;
                foreach (DBObject item in exploded)
                {
                    Polyline outline = item as Polyline;
                    if (outline == null || !outline.Closed || outline.NumberOfVertices < 3)
                        continue;
                    bool curved = false;
                    var candidate = new List<Point2d>();
                    for (int index = 0; index < outline.NumberOfVertices; index++)
                    {
                        if (Math.Abs(outline.GetBulgeAt(index)) > GeometryTolerance)
                        {
                            curved = true;
                            break;
                        }
                        AddDistinct(candidate, outline.GetPoint2dAt(index));
                    }
                    if (curved || candidate.Count < 3)
                        continue;
                    double area = Math.Abs(SignedArea(candidate));
                    if (area > largestArea)
                    {
                        largestArea = area;
                        points = candidate;
                    }
                }
            }
            catch (System.Exception exception)
            {
                rejection = "Parking block could not be exploded for measurement: " + exception.Message;
                return false;
            }
            finally
            {
                foreach (DBObject item in exploded)
                    item.Dispose();
            }

            if (points.Count < 3 || Math.Abs(SignedArea(points)) <= GeometryTolerance)
            {
                rejection = "Parking block contains no measurable straight closed outline";
                return false;
            }
            sourceType = "Block: " + definition.Name;
            return true;
        }

        private static bool TryMinimumAreaRectangle(
            IReadOnlyList<Point2d> hull,
            out OrientedRectangle rectangle)
        {
            rectangle = null;
            double bestArea = double.MaxValue;
            for (int index = 0; index < hull.Count; index++)
            {
                Point2d start = hull[index];
                Point2d end = hull[(index + 1) % hull.Count];
                Vector2d edge = end - start;
                if (edge.Length <= GeometryTolerance)
                    continue;
                Vector2d axis = edge.GetNormal();
                Vector2d normal = new Vector2d(-axis.Y, axis.X);

                double minAxis = double.MaxValue;
                double maxAxis = double.MinValue;
                double minNormal = double.MaxValue;
                double maxNormal = double.MinValue;
                foreach (Point2d point in hull)
                {
                    double along = point.X * axis.X + point.Y * axis.Y;
                    double across = point.X * normal.X + point.Y * normal.Y;
                    minAxis = Math.Min(minAxis, along);
                    maxAxis = Math.Max(maxAxis, along);
                    minNormal = Math.Min(minNormal, across);
                    maxNormal = Math.Max(maxNormal, across);
                }
                double axisSize = maxAxis - minAxis;
                double normalSize = maxNormal - minNormal;
                double area = axisSize * normalSize;
                if (axisSize <= GeometryTolerance || normalSize <= GeometryTolerance ||
                    area >= bestArea)
                    continue;

                bestArea = area;
                Vector2d longVector;
                Vector2d shortVector;
                double length;
                double width;
                if (axisSize >= normalSize)
                {
                    longVector = axis;
                    shortVector = normal;
                    length = axisSize;
                    width = normalSize;
                }
                else
                {
                    longVector = normal;
                    shortVector = axis;
                    length = normalSize;
                    width = axisSize;
                }
                double centerAlong = (minAxis + maxAxis) * 0.5;
                double centerAcross = (minNormal + maxNormal) * 0.5;
                Point2d center = new Point2d(
                    axis.X * centerAlong + normal.X * centerAcross,
                    axis.Y * centerAlong + normal.Y * centerAcross);
                double angle = Math.Atan2(longVector.Y, longVector.X) * 180.0 / Math.PI;
                angle %= 180.0;
                if (angle < 0.0) angle += 180.0;
                rectangle = new OrientedRectangle(
                    new Point3d(center.X, center.Y, 0.0),
                    new Point2d(longVector.X, longVector.Y),
                    new Point2d(shortVector.X, shortVector.Y),
                    length,
                    width,
                    angle);
            }
            return rectangle != null;
        }

        private static List<Point2d> ConvexHull(IEnumerable<Point2d> values)
        {
            List<Point2d> points = values
                .Distinct(new Point2dComparer())
                .OrderBy(point => point.X)
                .ThenBy(point => point.Y)
                .ToList();
            if (points.Count <= 1)
                return points;

            var lower = new List<Point2d>();
            foreach (Point2d point in points)
            {
                while (lower.Count >= 2 &&
                    Cross(lower[lower.Count - 2], lower[lower.Count - 1], point) <= GeometryTolerance)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(point);
            }
            var upper = new List<Point2d>();
            for (int index = points.Count - 1; index >= 0; index--)
            {
                Point2d point = points[index];
                while (upper.Count >= 2 &&
                    Cross(upper[upper.Count - 2], upper[upper.Count - 1], point) <= GeometryTolerance)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(point);
            }
            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        private static double Cross(Point2d origin, Point2d first, Point2d second)
        {
            return (first.X - origin.X) * (second.Y - origin.Y) -
                   (first.Y - origin.Y) * (second.X - origin.X);
        }

        private static double SignedArea(IReadOnlyList<Point2d> points)
        {
            double area = 0.0;
            for (int index = 0; index < points.Count; index++)
            {
                Point2d current = points[index];
                Point2d next = points[(index + 1) % points.Count];
                area += current.X * next.Y - next.X * current.Y;
            }
            return area * 0.5;
        }

        private static bool TryShortestPolygonEdge(
            IReadOnlyList<Point2d> points,
            out double length,
            out Vector2d axis)
        {
            length = double.MaxValue;
            axis = Vector2d.XAxis;
            if (points == null || points.Count < 2)
                return false;
            for (int index = 0; index < points.Count; index++)
            {
                Vector2d edge = points[(index + 1) % points.Count] - points[index];
                if (edge.Length <= GeometryTolerance || edge.Length >= length)
                    continue;
                length = edge.Length;
                axis = edge.GetNormal();
            }
            return length < double.MaxValue;
        }

        private static List<IList<string>> BuildReportRows(
            IReadOnlyList<ParkingBayCandidate> candidates,
            ParkingSkewSettings settings)
        {
            var rows = new List<IList<string>>();
            foreach (ParkingBayCandidate candidate in candidates)
            {
                rows.Add(new List<string>
                {
                    candidate.SourceHandle,
                    candidate.SourceType,
                    candidate.WidthMillimetres.ToString("0.###", CultureInfo.InvariantCulture),
                    settings.RequiredWidthMillimetres.ToString("0.###", CultureInfo.InvariantCulture),
                    candidate.DifferenceMillimetres.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture),
                    candidate.LengthMillimetres.ToString("0.###", CultureInfo.InvariantCulture),
                    candidate.SkewAngleDegrees.ToString("0.0", CultureInfo.InvariantCulture),
                    candidate.ShortestEdgeMillimetres.ToString("0.###", CultureInfo.InvariantCulture),
                    candidate.IsCompliant ? "PASS" : "FAIL",
                    candidate.IsCompliant
                        ? "True bay width meets the configured minimum"
                        : "True bay width is below the configured minimum"
                });
            }
            return rows;
        }

        private static void WritePreview(
            Editor editor,
            ParkingAnalysis analysis,
            ParkingSkewSettings settings)
        {
            editor.WriteMessage(
                "\nCE parking perpendicular-width preview: measurable={0}; pass={1}; fail={2}; rejected={3}; required={4:0.###} mm.",
                analysis.Candidates.Count,
                analysis.Candidates.Count(candidate => candidate.IsCompliant),
                analysis.Candidates.Count(candidate => !candidate.IsCompliant),
                analysis.Rejections.Count,
                settings.RequiredWidthMillimetres);
            foreach (ParkingBayCandidate candidate in analysis.Candidates)
            {
                editor.WriteMessage(
                    "\n  {0}: width={1:0.###} mm; shortest polygon edge={2:0.###} mm; length={3:0.###} mm; skew={4:0.0} deg; {5}",
                    candidate.SourceHandle,
                    candidate.WidthMillimetres,
                    candidate.ShortestEdgeMillimetres,
                    candidate.LengthMillimetres,
                    candidate.SkewAngleDegrees,
                    candidate.IsCompliant ? "PASS" : "FAIL");
            }
            foreach (Rejection rejection in analysis.Rejections.Take(20))
                editor.WriteMessage("\n  Rejected {0}: {1}", rejection.Handle, rejection.Reason);
        }

        private static PromptSelectionResult GetParkingSelection(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK &&
                implied.Value != null && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = message,
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
        }

        private static void RemoveGeneratedForSource(
            Database database,
            BlockTableRecord currentSpace,
            string sourceHandle,
            Transaction transaction,
            IEnumerable<string> types)
        {
            var accepted = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in currentSpace)
            {
                Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                string type;
                string source;
                double measured;
                double required;
                if (!TryReadTag(entity, out type, out source, out measured, out required) ||
                    !source.Equals(sourceHandle, StringComparison.OrdinalIgnoreCase) ||
                    !accepted.Contains(type))
                    continue;
                entity.UpgradeOpen();
                entity.Erase();
            }
        }

        private static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string requested,
            string fallback)
        {
            string name = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
            LayerTable layers = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (layers.Has(name))
            {
                ObjectId id = layers[name];
                LayerTableRecord layer = transaction.GetObject(
                    id,
                    OpenMode.ForRead,
                    false) as LayerTableRecord;
                if (layer != null && layer.IsLocked)
                    throw new InvalidOperationException("Layer '" + name + "' is locked.");
                return id;
            }
            layers.UpgradeOpen();
            var record = new LayerTableRecord { Name = name };
            ObjectId layerId = layers.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return layerId;
        }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(
                database.RegAppTableId,
                OpenMode.ForRead,
                false) as RegAppTable;
            if (table.Has(RegAppName))
                return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static ResultBuffer BuildTag(
            string type,
            string sourceHandle,
            double measuredMillimetres,
            double requiredMillimetres)
        {
            return new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegAppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, type ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, sourceHandle ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataReal, measuredMillimetres),
                new TypedValue((int)DxfCode.ExtendedDataReal, requiredMillimetres));
        }

        private static bool TryReadTag(
            Entity entity,
            out string type,
            out string sourceHandle,
            out double measuredMillimetres,
            out double requiredMillimetres)
        {
            type = sourceHandle = string.Empty;
            measuredMillimetres = requiredMillimetres = 0.0;
            if (entity == null)
                return false;
            using (ResultBuffer data = entity.GetXDataForApplication(RegAppName))
            {
                if (data == null)
                    return false;
                TypedValue[] values = data.AsArray();
                string[] strings = values
                    .Where(value => value.TypeCode == (int)DxfCode.ExtendedDataAsciiString)
                    .Select(value => value.Value as string)
                    .Where(value => value != null)
                    .ToArray();
                double[] numbers = values
                    .Where(value => value.TypeCode == (int)DxfCode.ExtendedDataReal)
                    .Select(value => Convert.ToDouble(value.Value, CultureInfo.InvariantCulture))
                    .ToArray();
                if (strings.Length < 2)
                    return false;
                type = strings[0];
                sourceHandle = strings[1];
                if (numbers.Length > 0) measuredMillimetres = numbers[0];
                if (numbers.Length > 1) requiredMillimetres = numbers[1];
                return true;
            }
        }

        private static bool TryResolveHandle(
            Database database,
            string handleText,
            out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            long value;
            if (!long.TryParse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return false;
            try
            {
                objectId = database.GetObjectId(false, new Handle(value), 0);
                return !objectId.IsNull && !objectId.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static void AddDistinct(ICollection<Point2d> points, Point2d point)
        {
            if (!points.Any(existing => existing.GetDistanceTo(point) <= GeometryTolerance))
                points.Add(point);
        }

        private static bool PromptText(
            Editor editor,
            string label,
            string current,
            out string value)
        {
            var options = new PromptStringOptions(
                "\n" + label + " <" + (current ?? string.Empty) + ">: ")
            {
                AllowSpaces = true
            };
            PromptResult result = editor.GetString(options);
            if (result.Status == PromptStatus.Cancel)
            {
                value = current;
                return false;
            }
            value = result.Status == PromptStatus.None
                ? current
                : result.StringResult.Trim();
            return true;
        }

        private static bool PromptPositiveDouble(
            Editor editor,
            string label,
            double current,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + label + " <" + current.ToString("0.######", CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNegative = false,
                AllowZero = false,
                UseDefaultValue = true,
                DefaultValue = current
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : current;
            return result.Status == PromptStatus.OK;
        }

        private static bool PromptNonNegativeDouble(
            Editor editor,
            string label,
            double current,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + label + " <" + current.ToString("0.######", CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNegative = false,
                AllowZero = true,
                UseDefaultValue = true,
                DefaultValue = current
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : current;
            return result.Status == PromptStatus.OK;
        }

        private static bool Confirm(Editor editor, string message)
        {
            var options = new PromptKeywordOptions(
                "\n" + message + "? [Yes/No] <No>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK &&
                   result.StringResult.Equals("Yes", StringComparison.OrdinalIgnoreCase);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class ParkingAnalysis
        {
            public ParkingAnalysis()
            {
                Candidates = new List<ParkingBayCandidate>();
                Rejections = new List<Rejection>();
            }
            public List<ParkingBayCandidate> Candidates { get; }
            public List<Rejection> Rejections { get; }
            public void Reject(ObjectId id, string reason)
            {
                Rejections.Add(new Rejection(
                    id.IsNull ? "<Invalid>" : id.Handle.ToString(),
                    reason));
            }
        }

        private sealed class Rejection
        {
            public Rejection(string handle, string reason)
            {
                Handle = handle;
                Reason = reason;
            }
            public string Handle { get; }
            public string Reason { get; }
        }

        private sealed class ParkingBayCandidate
        {
            public ParkingBayCandidate(
                ObjectId sourceId,
                string sourceHandle,
                string sourceType,
                Point3d center,
                Point2d longAxis,
                Vector2d shortAxis,
                double lengthDrawingUnits,
                double widthDrawingUnits,
                double lengthMillimetres,
                double widthMillimetres,
                double shortestEdgeMillimetres,
                double differenceMillimetres,
                double skewAngleDegrees,
                bool isCompliant)
            {
                SourceId = sourceId;
                SourceHandle = sourceHandle;
                SourceType = sourceType;
                Center = center;
                LongAxis = longAxis;
                ShortAxis = shortAxis;
                LengthDrawingUnits = lengthDrawingUnits;
                WidthDrawingUnits = widthDrawingUnits;
                LengthMillimetres = lengthMillimetres;
                WidthMillimetres = widthMillimetres;
                ShortestEdgeMillimetres = shortestEdgeMillimetres;
                DifferenceMillimetres = differenceMillimetres;
                SkewAngleDegrees = skewAngleDegrees;
                IsCompliant = isCompliant;
            }
            public ObjectId SourceId { get; }
            public string SourceHandle { get; }
            public string SourceType { get; }
            public Point3d Center { get; }
            public Point2d LongAxis { get; }
            public Vector2d ShortAxis { get; }
            public double LengthDrawingUnits { get; }
            public double WidthDrawingUnits { get; }
            public double LengthMillimetres { get; }
            public double WidthMillimetres { get; }
            public double ShortestEdgeMillimetres { get; }
            public double DifferenceMillimetres { get; }
            public double SkewAngleDegrees { get; }
            public bool IsCompliant { get; }
        }

        private sealed class OrientedRectangle
        {
            public OrientedRectangle(
                Point3d center,
                Point2d longAxis,
                Point2d shortAxis,
                double length,
                double width,
                double angleDegrees)
            {
                Center = center;
                LongAxis = longAxis;
                ShortAxis = shortAxis;
                Length = length;
                Width = width;
                AngleDegrees = angleDegrees;
            }
            public Point3d Center { get; }
            public Point2d LongAxis { get; }
            public Point2d ShortAxis { get; }
            public double Length { get; }
            public double Width { get; }
            public double AngleDegrees { get; }
        }

        private sealed class Point2dComparer : IEqualityComparer<Point2d>
        {
            public bool Equals(Point2d first, Point2d second)
            {
                return first.GetDistanceTo(second) <= GeometryTolerance;
            }
            public int GetHashCode(Point2d point)
            {
                unchecked
                {
                    long x = (long)Math.Round(point.X / GeometryTolerance);
                    long y = (long)Math.Round(point.Y / GeometryTolerance);
                    return (x.GetHashCode() * 397) ^ y.GetHashCode();
                }
            }
        }

        private sealed class ParkingSkewSettings
        {
            public double RequiredWidthMillimetres = 2500.0;
            public double DrawingUnitsPerMillimetre = 1.0;
            public double ToleranceMillimetres = 5.0;
            public double TextHeight = 100.0;
            public double DimensionOffset = 500.0;
            public string ReviewLayer = DefaultReviewLayer;
            public string CorrectionLayer = DefaultCorrectionLayer;
            public double RequiredWidthDrawingUnits =>
                RequiredWidthMillimetres * DrawingUnitsPerMillimetre;

            public static ParkingSkewSettings Read(Database database)
            {
                var settings = new ParkingSkewSettings();
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary nod = transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (nod == null || !nod.Contains(SettingsDictionary))
                        return settings;
                    DBDictionary ce = transaction.GetObject(
                        nod.GetAt(SettingsDictionary),
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (ce == null || !ce.Contains(SettingsRecord))
                        return settings;
                    Xrecord record = transaction.GetObject(
                        ce.GetAt(SettingsRecord),
                        OpenMode.ForRead,
                        false) as Xrecord;
                    string[] values = record == null || record.Data == null
                        ? new string[0]
                        : record.Data.AsArray()
                            .Where(value => value.TypeCode == (int)DxfCode.Text)
                            .Select(value => Convert.ToString(value.Value, CultureInfo.InvariantCulture))
                            .ToArray();
                    if (values.Length >= 7)
                    {
                        double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.RequiredWidthMillimetres);
                        double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.DrawingUnitsPerMillimetre);
                        double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.ToleranceMillimetres);
                        double.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.TextHeight);
                        double.TryParse(values[4], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.DimensionOffset);
                        settings.ReviewLayer = values[5];
                        settings.CorrectionLayer = values[6];
                    }
                }
                settings.Normalize();
                return settings;
            }

            public void Write(Database database)
            {
                Normalize();
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary nod = transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForWrite,
                        false) as DBDictionary;
                    DBDictionary ce;
                    if (nod.Contains(SettingsDictionary))
                        ce = transaction.GetObject(
                            nod.GetAt(SettingsDictionary),
                            OpenMode.ForWrite,
                            false) as DBDictionary;
                    else
                    {
                        ce = new DBDictionary();
                        nod.SetAt(SettingsDictionary, ce);
                        transaction.AddNewlyCreatedDBObject(ce, true);
                    }
                    Xrecord record;
                    if (ce.Contains(SettingsRecord))
                        record = transaction.GetObject(
                            ce.GetAt(SettingsRecord),
                            OpenMode.ForWrite,
                            false) as Xrecord;
                    else
                    {
                        record = new Xrecord();
                        ce.SetAt(SettingsRecord, record);
                        transaction.AddNewlyCreatedDBObject(record, true);
                    }
                    string[] values =
                    {
                        RequiredWidthMillimetres.ToString("R", CultureInfo.InvariantCulture),
                        DrawingUnitsPerMillimetre.ToString("R", CultureInfo.InvariantCulture),
                        ToleranceMillimetres.ToString("R", CultureInfo.InvariantCulture),
                        TextHeight.ToString("R", CultureInfo.InvariantCulture),
                        DimensionOffset.ToString("R", CultureInfo.InvariantCulture),
                        ReviewLayer,
                        CorrectionLayer
                    };
                    record.Data = new ResultBuffer(values
                        .Select(value => new TypedValue((int)DxfCode.Text, value))
                        .ToArray());
                    transaction.Commit();
                }
            }

            private void Normalize()
            {
                if (RequiredWidthMillimetres <= 0.0) RequiredWidthMillimetres = 2500.0;
                if (DrawingUnitsPerMillimetre <= 0.0) DrawingUnitsPerMillimetre = 1.0;
                if (ToleranceMillimetres < 0.0) ToleranceMillimetres = 5.0;
                if (TextHeight <= 0.0) TextHeight = 100.0 * DrawingUnitsPerMillimetre;
                if (DimensionOffset <= 0.0) DimensionOffset = 500.0 * DrawingUnitsPerMillimetre;
                if (string.IsNullOrWhiteSpace(ReviewLayer)) ReviewLayer = DefaultReviewLayer;
                if (string.IsNullOrWhiteSpace(CorrectionLayer)) CorrectionLayer = DefaultCorrectionLayer;
            }
        }
    }
}
