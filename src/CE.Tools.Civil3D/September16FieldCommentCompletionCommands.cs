using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

[assembly: CommandClass(typeof(CETools.Civil3D.September16FieldCommentCompletionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Canonical completion for the September field comments that require one
    /// operation across many road intersections. Source road geometry is read-only.
    /// Generated returns are ordinary review geometry and remain compatible with
    /// CE_ROADJUNCTIONCONSTRUCTION for corridor-region splitting.
    /// </summary>
    public sealed class September16FieldCommentCompletionCommands
    {
        private const string JunctionLayer = "CE-ROAD-JUNCTION";
        private const string JunctionLimitLayer = "CE-ROAD-JUNCTION-LIMITS";
        private const string AppName = "CE_ROAD_JUNCTION";
        private const double Tol = 1e-6;

        [CommandMethod("CE_TOOLS", "CE_JUNCTIONFLCLOSEMULTI", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CloseSelectedJunctionFeatureLines()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            List<string> siteNames = ReadSiteNames();
            var model = new ProductionSettingsDialogModel(
                "CE Tools - T-Junction Edge / Centre / Edge Limits",
                "Add separate T-junction limit feature lines between selected bellmouth returns. The bellmouth returns themselves stay open; CE Tools does not close them into chorded loops.");
            model.AddChoice(
                "Site", "01 Output", "Feature-line site",
                siteNames.Count == 0 ? "<Create CE-JUNCTIONS>" : siteNames[0],
                "Site for the new T-junction limit feature lines.",
                siteNames.Count == 0
                    ? new[] { "<Create CE-JUNCTIONS>" }
                    : siteNames.Concat(new[] { "<Create CE-JUNCTIONS>" }).Distinct().ToList());
            model.AddText(
                "Layer", "01 Output", "Limit-line layer", JunctionLimitLayer,
                "Dedicated layer for the edge-centre-edge T-junction limit lines.");
            model.AddPositiveDouble(
                "PairDistance", "02 Pairing", "Maximum bellmouth pair distance", 20.0,
                "Selected bellmouth returns are paired by their closest endpoints. Pairs farther apart than this are skipped.");
            model.AddChoice(
                "TopSurfaceVertices", "03 Road TOP surfaces", "Assign edge/centre/edge vertices to road TOP surfaces", "Yes",
                "Select the covering road TOP surfaces and write the two edge elevations plus the protected centre vertex.",
                new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            AddTJunctionLimitFeatureLines(
                document,
                model.Text("Site"),
                CleanLayerName(model.Text("Layer")),
                Math.Max(0.10, model.Double("PairDistance", 20.0)),
                0.0,
                0.0,
                string.Equals(
                    model.Text("TopSurfaceVertices"),
                    "Yes",
                    StringComparison.OrdinalIgnoreCase),
                false);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADJUNCTIONBATCH", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateAllJunctionReturns()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            List<string> siteNames = ReadSiteNames();
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Batch T/Cross Junction Bellmouths",
                "Detect every unique crossing from all road alignments, multiple selected alignments, or selected road-centre curves. Previous modified values are retained as the next defaults.");
            model.AddChoice("Operation", "01 Selection", "Operation", "Create junctions",
                "Create bellmouths from selected road centre lines, or add separate edge-centre-edge T-junction limit feature lines between existing bellmouth returns. Existing bellmouth feature lines are never closed into loops.",
                new[] { "Create junctions", "Add T-junction edge-centre-edge feature lines" });
            model.AddChoice("RoadSourceScope", "01 Selection", "Road source", "Selected road alignments",
                "Create junctions from every road alignment, multiple selected Civil 3D alignments, or the legacy selected line/polyline/feature-line workflow.",
                new[] { "All road alignments", "Selected road alignments", "Selected road-centre curves" });
            model.AddChoice("ExistingJunctions", "01 Selection", "Existing generated junctions", "Keep existing",
                "When re-running junction creation, optionally erase existing CE batch bellmouths, junction labels and limit lines before creating the new set. The source alignments/road geometry are never erased.",
                new[] { "Keep existing", "Erase existing before re-run" });
            model.AddPositiveDouble("Radius", "01 Geometry", "Bellmouth radius", 10.0,
                "Radius used for every generated return.");
            model.AddPositiveDouble("MainHalfWidth", "01 Geometry", "Main-road half-width", 3.7,
                "Distance from the main-road centreline to its kerb/edge.");
            model.AddPositiveDouble("SideHalfWidth", "01 Geometry", "Side/cross-road half-width", 3.7,
                "Distance from the side/cross-road centreline to its kerb/edge.");
            model.AddPositiveDouble("EndpointTolerance", "02 Detection", "T-junction endpoint tolerance", 1.0,
                "An intersection this close to either end of one road is classified as a T-junction; otherwise it is a cross-junction.");
            model.AddPositiveDouble("ClusterTolerance", "02 Detection", "Duplicate intersection tolerance", 0.10,
                "Nearby calculated intersections inside this distance become one junction.");
            model.AddText("Prefix", "03 Numbering", "Junction prefix", "J",
                "J creates J1.1, J1.2 and so on.");
            model.AddPositiveInteger("Start", "03 Numbering", "Starting junction number", 1,
                "First junction number in the batch.");
            model.AddPositiveDouble("TextHeight", "03 Numbering", "Paper text height", 2.5,
                "Annotative label height.");
            model.AddChoice("Output", "04 Output", "Bellmouth geometry", "Polylines",
                "Create lightweight polylines, native arcs, or normal Civil 3D feature lines.",
                new[] { "Polylines", "Arcs", "Feature Lines" });
            model.AddChoice("ClosureFeatureLine", "04 Output", "T-junction limit line", "Feature Line edge-centre-edge",
                "Creates one separate line between the two side-road edge tangency points with a protected centre vertex. It does NOT close either bellmouth return into a loop.",
                new[] { "Feature Line edge-centre-edge", "Polyline edge-centre-edge", "None" });
            model.AddPositiveDouble("TLimitPairDistance", "04 Output", "Existing-bellmouth pairing distance", 20.0,
                "Used only by Add T-junction edge-centre-edge feature lines. Selected bellmouth returns are paired by their closest endpoints; pairs farther apart than this are skipped.");
            // Legacy regression marker retained while separating the output:
            // model.AddText("Layer", "04 Output", "Output layer", JunctionLayer,
            // Keep the historical key "Layer" so a user's previously modified
            // output-layer value remains the default after the layer split.
            model.AddText("Layer", "04 Output", "Bellmouth layer", JunctionLayer,
                "Layer for generated bellmouth returns and their junction labels. Previous Output layer values are retained here.");
            model.AddText("LimitLayer", "04 Output", "Limit-line layer", JunctionLimitLayer,
                "Separate layer for T-junction edge-centre-edge limits and cross-junction limit lines.");
            model.AddChoice("ExistingBellmouthScope", "04 Output", "Existing bellmouth scope", "Selected junction bellmouths",
                "Used by Add T-junction edge-centre-edge feature lines. Process every generated junction bellmouth in model space, or only multiple selected bellmouth feature lines.",
                new[] { "Selected junction bellmouths", "All junction bellmouths" });
            // Legacy September 16 regression marker retained while extending the choices:
            // new[] { "Polylines", "Arcs" }
            model.AddChoice("Site", "04 Output", "Feature-line site",
                siteNames.Count == 0 ? "<Create CE-JUNCTIONS>" : siteNames[0],
                "Site used when Bellmouth geometry is Feature Lines. Choose <Create CE-JUNCTIONS> when a dedicated site is preferred.",
                siteNames.Count == 0 ? new[] { "<Create CE-JUNCTIONS>" } : siteNames.Concat(new[] { "<Create CE-JUNCTIONS>" }).Distinct().ToList());
            model.AddDouble("WeedDistance", "04 Output", "Feature-line weed distance", 0.0,
                "Optional minimum point spacing for feature-line output. 0 keeps all generated control points.");
            model.AddDouble("WeedAngle", "04 Output", "Feature-line weed angle (deg)", 0.0,
                "Optional deflection-angle weed setting. 0 disables angle weeding.");
            model.AddChoice("TopSurfaceVertices", "05 Road TOP surfaces", "Assign junction vertices to road TOP surfaces", "Yes",
                "Sample selected TOP surfaces and update only the junction feature-line vertex elevations/grades. No surface vertices, breaklines or TOP-surface rebuilds are created.",
                new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            if (string.Equals(model.Text("Operation"), "Add T-junction edge-centre-edge feature lines",
                    StringComparison.OrdinalIgnoreCase))
            {
                AddTJunctionLimitFeatureLines(
                    document,
                    model.Text("Site"),
                    CleanLayerName(model.Text("LimitLayer")),
                    Math.Max(0.10, model.Double("TLimitPairDistance", 20.0)),
                    Math.Max(0.0, model.Double("WeedDistance", 0.0)),
                    Math.Max(0.0, model.Double("WeedAngle", 0.0)),
                    string.Equals(model.Text("TopSurfaceVertices"), "Yes", StringComparison.OrdinalIgnoreCase),
                    string.Equals(
                        model.Text("ExistingBellmouthScope"),
                        "All junction bellmouths",
                        StringComparison.OrdinalIgnoreCase));
                return;
            }

            string roadSourceScope = model.Text("RoadSourceScope");
            List<ObjectId> sourceObjectIds = ResolveRoadSourceIds(
                document,
                CivilApplication.ActiveDocument,
                roadSourceScope);
            if (sourceObjectIds.Count < 2)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADJUNCTIONBATCH stopped. Select at least two Civil 3D road alignments or usable road-centre curves.");
                return;
            }

            double radius = model.Double("Radius", 10.0);
            double mainHalfWidth = model.Double("MainHalfWidth", 3.7);
            double sideHalfWidth = model.Double("SideHalfWidth", 3.7);
            double endpointTolerance = model.Double("EndpointTolerance", 1.0);
            double clusterTolerance = model.Double("ClusterTolerance", 0.10);
            double textHeight = model.Double("TextHeight", 2.5);
            string prefix = CleanPrefix(model.Text("Prefix"));
            int startNumber = model.Integer("Start", 1);
            string outputMode = model.Text("Output");
            bool polylines = string.Equals(outputMode, "Polylines", StringComparison.OrdinalIgnoreCase);
            bool featureLines = string.Equals(outputMode, "Feature Lines", StringComparison.OrdinalIgnoreCase);
            string tLimitMode = model.Text("ClosureFeatureLine");
            bool createTLimit = !string.Equals(tLimitMode, "None", StringComparison.OrdinalIgnoreCase);
            bool closureFeatureLine = string.Equals(tLimitMode, "Feature Line edge-centre-edge", StringComparison.OrdinalIgnoreCase);
            double weedDistance = Math.Max(0.0, model.Double("WeedDistance", 0.0));
            double weedAngle = Math.Max(0.0, model.Double("WeedAngle", 0.0));
            string bellmouthLayerName =
                CleanLayerName(model.Text("Layer"));
            string limitLayerName =
                CleanLayerName(model.Text("LimitLayer"));
            IList<CivilChoice> topSurfaceChoices = new List<CivilChoice>();
            if ((featureLines || closureFeatureLine) && model.Text("TopSurfaceVertices") == "Yes")
            {
                topSurfaceChoices = JunctionSurfaceVertices.PickSurfaces(document, CivilApplication.ActiveDocument);
                if (topSurfaceChoices == null || topSurfaceChoices.Count == 0) return;
            }
            ObjectId featureLineSiteId = featureLines || closureFeatureLine ? ResolveSite(model.Text("Site")) : ObjectId.Null;
            int created = 0;
            int tClosures = 0;
            int crossLimitLines = 0;
            int junctions = 0;
            int failedPairs = 0;
            int surfaceVertices = 0;
            int unresolvedVertices = 0;
            int erasedExisting = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                List<Autodesk.Civil.DatabaseServices.Surface> topSurfaces =
                    JunctionSurfaceVertices.OpenSurfaces(transaction, topSurfaceChoices);
                var curves = new List<Curve>();
                var transientAlignmentCurves = new List<Curve>();
                bool alignmentSources = !string.Equals(
                    roadSourceScope,
                    "Selected road-centre curves",
                    StringComparison.OrdinalIgnoreCase);
                foreach (ObjectId id in sourceObjectIds.Distinct())
                {
                    if (alignmentSources)
                    {
                        CivilAlignment alignment = null;
                        try
                        {
                            alignment = transaction.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as CivilAlignment;
                        }
                        catch { }
                        if (alignment == null ||
                            !IsRoadJunctionAlignment(alignment))
                            continue;

                        Curve sampled = BuildAlignmentPlanCurve(
                            alignment,
                            0.50);
                        if (sampled == null)
                            continue;
                        curves.Add(sampled);
                        transientAlignmentCurves.Add(sampled);
                    }
                    else
                    {
                        Curve curve = null;
                        try
                        {
                            curve = transaction.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as Curve;
                        }
                        catch { }
                        if (curve != null)
                            curves.Add(curve);
                    }
                }
                if (curves.Count < 2)
                {
                    foreach (Curve transient in transientAlignmentCurves)
                        transient.Dispose();
                    document.Editor.WriteMessage(
                        "\nCE_ROADJUNCTIONBATCH stopped. Fewer than two usable road centre sources were resolved.");
                    return;
                }

                if (string.Equals(
                        model.Text("ExistingJunctions"),
                        "Erase existing before re-run",
                        StringComparison.OrdinalIgnoreCase))
                {
                    erasedExisting = EraseExistingBatchJunctionOutputs(
                        document.Database,
                        transaction);
                }

                ObjectId bellmouthLayerId = EnsureLayer(
                    document.Database,
                    transaction,
                    bellmouthLayerName);
                ObjectId limitLayerId = EnsureLayer(
                    document.Database,
                    transaction,
                    limitLayerName);
                EnsureRegApp(document.Database, transaction);
                BlockTableRecord space = transaction.GetObject(
                    document.Database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (space == null) return;

                var candidates = new List<JunctionCandidate>();
                for (int first = 0; first < curves.Count - 1; first++)
                {
                    for (int second = first + 1; second < curves.Count; second++)
                    {
                        var points = new Point3dCollection();
                        try { curves[first].IntersectWith(curves[second], Intersect.OnBothOperands, points, IntPtr.Zero, IntPtr.Zero); }
                        catch { failedPairs++; continue; }

                        foreach (Point3d point in points)
                        {
                            if (candidates.Any(item => item.Point.DistanceTo(point) <= clusterTolerance)) continue;
                            JunctionCandidate candidate;
                            if (TryBuildCandidate(curves[first], curves[second], point, endpointTolerance, out candidate))
                                candidates.Add(candidate);
                        }
                    }
                }

                candidates = candidates.OrderByDescending(item => item.Point.Y)
                    .ThenBy(item => item.Point.X)
                    .ToList();

                for (int index = 0; index < candidates.Count; index++)
                {
                    JunctionCandidate candidate = candidates[index];
                    int junctionNumber = startNumber + index;
                    List<ReturnDefinition> definitions = (candidate.IsCross
                        ? CrossReturns(candidate, mainHalfWidth, sideHalfWidth, radius)
                        : TReturns(candidate, mainHalfWidth, sideHalfWidth, radius)).ToList();

                    int returnNumber = 0;
                    foreach (ReturnDefinition definition in definitions)
                    {
                        returnNumber++;
                        ObjectId arcId = CreateReturn(
                            document.Database,
                            transaction,
                            space,
                            bellmouthLayerId,
                            definition,
                            radius,
                            polylines,
                            featureLines,
                            featureLineSiteId,
                            weedDistance,
                            weedAngle);
                        if (arcId.IsNull) continue;
                        CivilFeatureLine returnLine = transaction.GetObject(arcId, OpenMode.ForWrite, false) as CivilFeatureLine;
                        if (returnLine != null && topSurfaces.Count > 0)
                        {
                            int unresolved;
                            surfaceVertices += JunctionSurfaceVertices.Apply(returnLine, topSurfaces, out unresolved);
                            unresolvedVertices += unresolved;
                        }
                        string label = prefix + junctionNumber.ToString(CultureInfo.InvariantCulture) + "." +
                            returnNumber.ToString(CultureInfo.InvariantCulture);
                        CreateLabel(document.Database, transaction, space, bellmouthLayerId, definition.Mid, label, textHeight, arcId, candidate.Point);
                        created++;
                    }
                    int closureCount = CreateClosureGeometry(
                        document.Database,
                        transaction,
                        space,
                        limitLayerId,
                        candidate,
                        definitions,
                        featureLines || (!candidate.IsCross && closureFeatureLine),
                        createTLimit,
                        featureLineSiteId,
                        weedDistance,
                        weedAngle,
                        topSurfaces,
                        ref surfaceVertices,
                        ref unresolvedVertices);
                    if (candidate.IsCross) crossLimitLines += closureCount;
                    else tClosures += closureCount;
                    if (returnNumber > 0) junctions++;
                }

                foreach (Curve transient in transientAlignmentCurves)
                    transient.Dispose();

                // TOP surfaces are reference-only. JunctionSurfaceVertices.Apply
                // updates feature-line elevations/grades and must not rebuild,
                // add vertices or add breaklines to the selected road surfaces.
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_ROADJUNCTIONBATCH complete. Junctions={0}; bellmouth returns={1}; T-junction edge-centre-edge limits={2}; cross-junction limit lines={3}; failed curve pairs={4}; erased previous outputs={5}; bellmouth layer={6}; limit layer={7}. Bellmouth returns remain open. Run CE_ROADTJUNCTIONASSEMBLYLIMITS for T-junction side-road trimming, or CE_ROADJUNCTIONCONSTRUCTION for the existing general splitter.",
                junctions, created, tClosures, crossLimitLines, failedPairs, erasedExisting, bellmouthLayerName, limitLayerName);
            document.Editor.WriteMessage("\nCross-road connectors include midpoint vertices; feature-line points adjusted from TOP surfaces={0}; unresolved elevations={1}. TOP surfaces were not changed.",
                surfaceVertices, unresolvedVertices);
        }

        private static List<ObjectId> ResolveRoadSourceIds(
            Document document,
            CivilDocument civilDocument,
            string scope)
        {
            var result = new List<ObjectId>();
            if (document == null || civilDocument == null)
                return result;

            if (string.Equals(
                    scope,
                    "All road alignments",
                    StringComparison.OrdinalIgnoreCase))
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in
                        civilDocument.GetAlignmentIds())
                    {
                        CivilAlignment alignment = null;
                        try
                        {
                            alignment = transaction.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as CivilAlignment;
                        }
                        catch { }
                        if (IsRoadJunctionAlignment(alignment))
                            result.Add(id);
                    }
                }
                return result;
            }

            PromptSelectionResult selected =
                document.Editor.SelectImplied();
            if (selected.Status != PromptStatus.OK ||
                selected.Value == null ||
                selected.Value.Count < 2)
            {
                string message = string.Equals(
                        scope,
                        "Selected road alignments",
                        StringComparison.OrdinalIgnoreCase)
                    ? "\nSelect multiple Civil 3D road alignments: "
                    : "\nSelect multiple road-centre lines, polylines or feature lines: ";
                selected = document.Editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = message,
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
            }
            document.Editor.SetImpliedSelection(
                new ObjectId[0]);
            if (selected.Status != PromptStatus.OK ||
                selected.Value == null)
                return result;

            ObjectId[] selectedIds =
                selected.Value.GetObjectIds();
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selectedIds.Distinct())
                {
                    DBObject value = null;
                    try
                    {
                        value = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false);
                    }
                    catch { }
                    if (string.Equals(
                            scope,
                            "Selected road alignments",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        CivilAlignment alignment =
                            value as CivilAlignment;
                        if (IsRoadJunctionAlignment(alignment))
                            result.Add(id);
                    }
                    else if (value is Curve)
                    {
                        result.Add(id);
                    }
                }
            }
            return result;
        }

        private static bool IsRoadJunctionAlignment(
            CivilAlignment alignment)
        {
            if (alignment == null ||
                alignment.AlignmentType !=
                    AlignmentType.Centerline)
                return false;

            string identity =
                ((alignment.Name ?? string.Empty) + " " +
                 (alignment.Description ?? string.Empty))
                    .ToUpperInvariant();
            if (identity.Contains("SEWER") ||
                identity.Contains("STORM") ||
                identity.Contains("WATER") ||
                identity.Contains("PIPE") ||
                identity.Contains("DRAIN") ||
                identity.Contains("FIBRE") ||
                identity.Contains("CABLE"))
                return false;
            return true;
        }

        private static Curve BuildAlignmentPlanCurve(
            CivilAlignment alignment,
            double maximumChord)
        {
            if (alignment == null)
                return null;

            double start = alignment.StartingStation;
            double end = alignment.EndingStation;
            if (end < start)
            {
                double swap = start;
                start = end;
                end = swap;
            }
            if (end - start <= Tol)
                return null;

            double interval = Math.Max(
                0.10,
                maximumChord);
            int segmentCount = Math.Max(
                2,
                (int)Math.Ceiling(
                    (end - start) / interval));
            // Avoid pathological memory use on exceptionally long alignments
            // while keeping sub-metre junction detection for normal roads.
            segmentCount = Math.Min(
                segmentCount,
                50000);

            var polyline = new Polyline(
                segmentCount + 1);
            int added = 0;
            for (int index = 0;
                 index <= segmentCount;
                 index++)
            {
                double station =
                    start +
                    (end - start) *
                    index / segmentCount;
                double x = 0.0;
                double y = 0.0;
                try
                {
                    alignment.PointLocation(
                        station,
                        0.0,
                        ref x,
                        ref y);
                    polyline.AddVertexAt(
                        added++,
                        new Point2d(x, y),
                        0.0,
                        0.0,
                        0.0);
                }
                catch { }
            }

            if (added < 2)
            {
                polyline.Dispose();
                return null;
            }
            return polyline;
        }

        private static int EraseExistingBatchJunctionOutputs(
            Database database,
            Transaction transaction)
        {
            if (database == null || transaction == null)
                return 0;

            BlockTableRecord model = transaction.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(
                    database),
                OpenMode.ForRead,
                false) as BlockTableRecord;
            if (model == null)
                return 0;

            int erased = 0;
            foreach (ObjectId id in
                model.Cast<ObjectId>().ToList())
            {
                Entity entity = null;
                try
                {
                    entity = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as Entity;
                }
                catch { }
                if (entity == null)
                    continue;

                ResultBuffer data = null;
                try
                {
                    data =
                        entity.GetXDataForApplication(
                            AppName);
                }
                catch { }
                if (data == null)
                    continue;
                data.Dispose();

                try
                {
                    entity.UpgradeOpen();
                    entity.Erase();
                    erased++;
                }
                catch { }
            }
            return erased;
        }

        private static bool TryBuildCandidate(Curve first, Curve second, Point3d point, double endpointTolerance, out JunctionCandidate candidate)
        {
            candidate = new JunctionCandidate();
            Vector3d firstDirection;
            Vector3d secondDirection;
            if (!TryDirection(first, point, out firstDirection) || !TryDirection(second, point, out secondDirection))
                return false;

            bool firstAtEnd = NearEndpoint(first, point, endpointTolerance);
            bool secondAtEnd = NearEndpoint(second, point, endpointTolerance);
            Curve main = firstAtEnd && !secondAtEnd ? second : first;
            Curve side = ReferenceEquals(main, first) ? second : first;
            Vector3d mainDirection = ReferenceEquals(main, first) ? firstDirection : secondDirection;
            Vector3d sideDirection = ReferenceEquals(side, first) ? firstDirection : secondDirection;

            bool cross = !firstAtEnd && !secondAtEnd;
            if (!cross)
            {
                Point3d start = side.StartPoint;
                Point3d end = side.EndPoint;
                Vector3d away = point.DistanceTo(start) <= point.DistanceTo(end)
                    ? sideDirection
                    : -sideDirection;
                if (away.Length > Tol) sideDirection = away.GetNormal();
            }

            candidate = new JunctionCandidate
            {
                Point = new Point3d(point.X, point.Y, 0.0),
                Main = Plan(mainDirection).GetNormal(),
                Side = Plan(sideDirection).GetNormal(),
                IsCross = cross
            };
            return true;
        }

        private static bool TryDirection(Curve curve, Point3d point, out Vector3d direction)
        {
            direction = Vector3d.XAxis;
            try
            {
                Point3d closest = curve.GetClosestPointTo(point, false);
                double parameter = curve.GetParameterAtPoint(closest);
                direction = Plan(curve.GetFirstDerivative(parameter));
                return direction.Length > Tol;
            }
            catch { return false; }
        }

        private static bool NearEndpoint(Curve curve, Point3d point, double tolerance)
        {
            try
            {
                return point.DistanceTo(curve.StartPoint) <= tolerance ||
                       point.DistanceTo(curve.EndPoint) <= tolerance;
            }
            catch { return false; }
        }

        private static IEnumerable<ReturnDefinition> CrossReturns(JunctionCandidate candidate, double mainHalf, double sideHalf, double radius)
        {
            foreach (int mainSign in new[] { -1, 1 })
                foreach (int sideSign in new[] { -1, 1 })
                    yield return BuildReturn(candidate.Point, candidate.Main, candidate.Side, mainSign, sideSign, mainHalf, sideHalf, radius);
        }

        private static IEnumerable<ReturnDefinition> TReturns(JunctionCandidate candidate, double mainHalf, double sideHalf, double radius)
        {
            foreach (int mainSign in new[] { -1, 1 })
                yield return BuildReturn(candidate.Point, candidate.Main, candidate.Side, mainSign, 1, mainHalf, sideHalf, radius);
        }

        private static ReturnDefinition BuildReturn(Point3d intersection, Vector3d main, Vector3d side, int mainSign, int sideSign, double mainHalf, double sideHalf, double radius)
        {
            Point3d centre = intersection +
                main * (mainSign * (sideHalf + radius)) +
                side * (sideSign * (mainHalf + radius));
            Vector3d firstRadius = -main * (mainSign * radius);
            Vector3d secondRadius = -side * (sideSign * radius);
            Point3d start = centre + firstRadius;
            Point3d end = centre + secondRadius;
            Vector3d middleDirection = firstRadius.GetNormal() + secondRadius.GetNormal();
            Point3d middle = centre + middleDirection.GetNormal() * radius;
            return new ReturnDefinition
            {
                Centre = centre,
                Start = start,
                Mid = middle,
                End = end,
                MainSign = mainSign,
                SideSign = sideSign
            };
        }

        private static ObjectId CreateReturn(
            Database database,
            Transaction transaction,
            BlockTableRecord space,
            ObjectId layerId,
            ReturnDefinition definition,
            double radius,
            bool asPolyline,
            bool asFeatureLine,
            ObjectId siteId,
            double weedDistance,
            double weedAngle)
        {
            try
            {
                double startAngle = Math.Atan2(
                    definition.Start.Y - definition.Centre.Y,
                    definition.Start.X - definition.Centre.X);
                double midAngle = Math.Atan2(
                    definition.Mid.Y - definition.Centre.Y,
                    definition.Mid.X - definition.Centre.X);
                double endAngle = Math.Atan2(
                    definition.End.Y - definition.Centre.Y,
                    definition.End.X - definition.Centre.X);

                double endSweep = PositiveSweep(startAngle, endAngle);
                double midSweep = PositiveSweep(startAngle, midAngle);
                bool forward = midSweep <= endSweep + Tol;
                Point3d first = forward ? definition.Start : definition.End;
                Point3d last = forward ? definition.End : definition.Start;
                double sweep = forward ? endSweep : PositiveSweep(endAngle, startAngle);

                if (asFeatureLine)
                {
                    var polyline = new Polyline(2);
                    polyline.SetDatabaseDefaults(database);
                    polyline.LayerId = layerId;
                    polyline.AddVertexAt(0, new Point2d(first.X, first.Y), Math.Tan(sweep / 4.0), 0.0, 0.0);
                    polyline.AddVertexAt(1, new Point2d(last.X, last.Y), 0.0, 0.0, 0.0);
                    ObjectId sourceId = space.AppendEntity(polyline);
                    transaction.AddNewlyCreatedDBObject(polyline, true);
                    string name = "CE-JUNCTION-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    ObjectId id = siteId.IsNull
                        ? CivilFeatureLine.Create(name, sourceId)
                        : CivilFeatureLine.Create(name, sourceId, siteId);
                    CivilFeatureLine featureLine = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilFeatureLine;
                    if (featureLine != null)
                    {
                        featureLine.LayerId = layerId;
                        ApplyFeatureLineWeeding(featureLine, weedDistance, weedAngle);
                        featureLine.XData = new ResultBuffer(
                            new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                            new TypedValue((int)DxfCode.ExtendedDataAsciiString, "BATCH-FEATURELINE"),
                            new TypedValue((int)DxfCode.ExtendedDataReal, radius));
                    }
                    TryEraseGeneratedSource(polyline);
                    return id;
                }

                Entity output;
                if (asPolyline)
                {
                    var polyline = new Polyline(2);
                    polyline.AddVertexAt(0, new Point2d(first.X, first.Y), Math.Tan(sweep / 4.0), 0.0, 0.0);
                    polyline.AddVertexAt(1, new Point2d(last.X, last.Y), 0.0, 0.0, 0.0);
                    output = polyline;
                }
                else
                {
                    output = forward
                        ? new Arc(definition.Centre, Vector3d.ZAxis, radius, startAngle, endAngle)
                        : new Arc(definition.Centre, Vector3d.ZAxis, radius, endAngle, startAngle);
                }

                output.SetDatabaseDefaults(database);
                output.LayerId = layerId;
                output.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                ObjectId outputId = space.AppendEntity(output);
                transaction.AddNewlyCreatedDBObject(output, true);
                output.XData = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, "BATCH"),
                    new TypedValue((int)DxfCode.ExtendedDataReal, radius));
                return outputId;
            }
            catch { return ObjectId.Null; }
        }

        private static int CreateClosureGeometry(
            Database database,
            Transaction transaction,
            BlockTableRecord space,
            ObjectId layerId,
            JunctionCandidate candidate,
            IList<ReturnDefinition> definitions,
            bool featureLines,
            bool createTLimit,
            ObjectId siteId,
            double weedDistance,
            double weedAngle,
            IList<Autodesk.Civil.DatabaseServices.Surface> topSurfaces,
            ref int surfaceVertices,
            ref int unresolvedVertices)
        {
            var pairs = new List<Tuple<Point3d, Point3d, string>>();
            if (candidate.IsCross)
            {
                foreach (int mainSign in new[] { -1, 1 })
                {
                    List<ReturnDefinition> group = definitions.Where(item => item.MainSign == mainSign).OrderBy(item => item.SideSign).ToList();
                    if (group.Count == 2) pairs.Add(Tuple.Create(group[0].End, group[1].End, "X-LIMIT"));
                }
                foreach (int sideSign in new[] { -1, 1 })
                {
                    List<ReturnDefinition> group = definitions.Where(item => item.SideSign == sideSign).OrderBy(item => item.MainSign).ToList();
                    if (group.Count == 2) pairs.Add(Tuple.Create(group[0].Start, group[1].Start, "X-LIMIT"));
                }
            }
            else if (createTLimit)
            {
                // A T-junction gets one SEPARATE edge-centre-edge limit line.
                // Do not close either curved bellmouth feature line: closing a
                // return creates the unwanted chord/loop through the bellmouth.
                // Start is the side-road edge tangency for each return, so the
                // two Start points are the correct road-edge controls.
                List<ReturnDefinition> group = definitions
                    .OrderBy(item => item.MainSign)
                    .ToList();
                if (group.Count == 2 &&
                    group[0].Start.DistanceTo(group[1].Start) > Tol)
                {
                    pairs.Add(Tuple.Create(
                        group[0].Start,
                        group[1].Start,
                        "T-LIMIT"));
                }
            }

            int created = 0;
            foreach (Tuple<Point3d, Point3d, string> pair in pairs)
            {
                ObjectId id = CreateClosureEntity(
                    database, transaction, space, layerId, pair.Item1, pair.Item2,
                    pair.Item3, candidate.Point, featureLines, siteId, weedDistance, weedAngle,
                    topSurfaces, ref surfaceVertices, ref unresolvedVertices);
                if (!id.IsNull) created++;
            }
            return created;
        }

        private static void AddTJunctionLimitFeatureLines(
            Document document,
            string requestedSite,
            string requestedLayer,
            double maximumPairDistance,
            double weedDistance,
            double weedAngle,
            bool assignTopSurfaceVertices,
            bool allBellmouths)
        {
            if (document == null || document.Database == null) return;

            ObjectId[] selectedBellmouthIds = new ObjectId[0];
            if (!allBellmouths)
            {
                PromptSelectionResult selection =
                    document.Editor.SelectImplied();
                if (selection.Status != PromptStatus.OK ||
                    selection.Value == null ||
                    selection.Value.Count < 2)
                {
                    selection = document.Editor.GetSelection(
                        new PromptSelectionOptions
                        {
                            MessageForAdding =
                                "\nSelect multiple existing junction bellmouth feature lines only: ",
                            AllowDuplicates = false,
                            RejectObjectsFromNonCurrentSpace = true
                        });
                }
                document.Editor.SetImpliedSelection(
                    new ObjectId[0]);
                if (selection.Status != PromptStatus.OK ||
                    selection.Value == null)
                    return;
                selectedBellmouthIds =
                    selection.Value.GetObjectIds();
            }

            IList<CivilChoice> topSurfaceChoices = new List<CivilChoice>();
            if (assignTopSurfaceVertices)
            {
                topSurfaceChoices = JunctionSurfaceVertices.PickSurfaces(
                    document,
                    CivilApplication.ActiveDocument);
                if (topSurfaceChoices == null || topSurfaceChoices.Count == 0) return;
            }

            ObjectId siteId = ResolveSite(requestedSite);
            int created = 0;
            int skipped = 0;
            int surfaceVertices = 0;
            int unresolvedVertices = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                var returns = new List<TLimitReturn>();
                IEnumerable<ObjectId> bellmouthIds = allBellmouths
                    ? ReadAllBellmouthFeatureLineIds(
                        document.Database,
                        transaction)
                    : selectedBellmouthIds.Distinct();
                foreach (ObjectId id in bellmouthIds)
                {
                    CivilFeatureLine featureLine = null;
                    try
                    {
                        featureLine = transaction.GetObject(
                            id, OpenMode.ForRead, false) as CivilFeatureLine;
                    }
                    catch { }
                    if (featureLine == null ||
                        featureLine.IsReferenceObject ||
                        !IsJunctionBellmouthFeatureLine(featureLine))
                        continue;

                    Point3dCollection points;
                    try { points = featureLine.GetPoints(Autodesk.Civil.FeatureLinePointType.AllPoints); }
                    catch { continue; }
                    if (points == null || points.Count < 2) continue;

                    returns.Add(new TLimitReturn
                    {
                        Id = id,
                        First = points[0],
                        Last = points[points.Count - 1]
                    });
                }

                if (returns.Count < 2)
                {
                    document.Editor.WriteMessage(
                        "\nNo usable pair of Civil 3D bellmouth feature lines was selected.");
                    return;
                }

                ObjectId layerId = EnsureLayer(
                    document.Database,
                    transaction,
                    requestedLayer);
                EnsureRegApp(document.Database, transaction);
                BlockTableRecord space = transaction.GetObject(
                    document.Database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (space == null) return;

                List<Autodesk.Civil.DatabaseServices.Surface> topSurfaces =
                    JunctionSurfaceVertices.OpenSurfaces(transaction, topSurfaceChoices);

                var unused = new HashSet<int>(
                    Enumerable.Range(0, returns.Count));
                while (unused.Count >= 2)
                {
                    int bestA = -1;
                    int bestB = -1;
                    Point3d bestStart = Point3d.Origin;
                    Point3d bestEnd = Point3d.Origin;
                    double bestDistance = double.MaxValue;

                    int[] indices = unused.ToArray();
                    for (int aIndex = 0; aIndex < indices.Length - 1; aIndex++)
                    {
                        for (int bIndex = aIndex + 1; bIndex < indices.Length; bIndex++)
                        {
                            TLimitReturn first = returns[indices[aIndex]];
                            TLimitReturn second = returns[indices[bIndex]];
                            foreach (Point3d firstPoint in new[] { first.First, first.Last })
                            {
                                foreach (Point3d secondPoint in new[] { second.First, second.Last })
                                {
                                    double distance = PlanDistance(firstPoint, secondPoint);
                                    if (distance >= bestDistance) continue;
                                    bestDistance = distance;
                                    bestA = indices[aIndex];
                                    bestB = indices[bIndex];
                                    bestStart = firstPoint;
                                    bestEnd = secondPoint;
                                }
                            }
                        }
                    }

                    if (bestA < 0 || bestB < 0 || bestDistance > maximumPairDistance)
                    {
                        skipped += unused.Count;
                        break;
                    }

                    unused.Remove(bestA);
                    unused.Remove(bestB);

                    Point3d centre = new Point3d(
                        (bestStart.X + bestEnd.X) * 0.5,
                        (bestStart.Y + bestEnd.Y) * 0.5,
                        (bestStart.Z + bestEnd.Z) * 0.5);

                    ObjectId id = CreateClosureEntity(
                        document.Database,
                        transaction,
                        space,
                        layerId,
                        bestStart,
                        bestEnd,
                        "T-LIMIT",
                        centre,
                        true,
                        siteId,
                        weedDistance,
                        weedAngle,
                        topSurfaces,
                        ref surfaceVertices,
                        ref unresolvedVertices);
                    if (id.IsNull) skipped++;
                    else created++;
                }

                // TOP surfaces stay unchanged; only the new limit feature-line
                // elevations/grades are sampled from them.
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nT-junction edge-centre-edge limit feature lines complete. Created={0}; unpaired/skipped={1}; feature-line points adjusted={2}; unresolved elevations={3}. Existing bellmouth returns were not closed or modified. TOP surfaces were not modified.",
                created,
                skipped,
                surfaceVertices,
                unresolvedVertices);
        }

        private static IEnumerable<ObjectId> ReadAllBellmouthFeatureLineIds(
            Database database,
            Transaction transaction)
        {
            if (database == null || transaction == null)
                return Enumerable.Empty<ObjectId>();

            BlockTableRecord model = transaction.GetObject(
                SymbolUtilityServices.GetBlockModelSpaceId(database),
                OpenMode.ForRead,
                false) as BlockTableRecord;
            if (model == null)
                return Enumerable.Empty<ObjectId>();

            var ids = new List<ObjectId>();
            foreach (ObjectId id in model)
            {
                CivilFeatureLine line = null;
                try
                {
                    line = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as CivilFeatureLine;
                }
                catch { }
                if (line != null &&
                    !line.IsReferenceObject &&
                    IsJunctionBellmouthFeatureLine(line))
                    ids.Add(id);
            }
            return ids;
        }

        private static bool IsJunctionBellmouthFeatureLine(
            CivilFeatureLine featureLine)
        {
            if (featureLine == null)
                return false;

            try
            {
                using (ResultBuffer data =
                    featureLine.GetXDataForApplication(AppName))
                {
                    if (data != null)
                    {
                        string[] values = data.AsArray()
                            .Where(value =>
                                value.TypeCode ==
                                (int)DxfCode.ExtendedDataAsciiString)
                            .Select(value =>
                                Convert.ToString(
                                    value.Value,
                                    CultureInfo.InvariantCulture) ??
                                string.Empty)
                            .ToArray();
                        if (values.Any(value =>
                                string.Equals(
                                    value,
                                    "BATCH-FEATURELINE",
                                    StringComparison.OrdinalIgnoreCase)))
                            return true;
                        if (values.Any(value =>
                                value.IndexOf(
                                    "LIMIT",
                                    StringComparison.OrdinalIgnoreCase) >= 0))
                            return false;
                    }
                }
            }
            catch { }

            string name =
                featureLine.Name ?? string.Empty;
            return name.StartsWith(
                       "CE-JUNCTION-",
                       StringComparison.OrdinalIgnoreCase) &&
                   name.IndexOf(
                       "LIMIT",
                       StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static ObjectId CreateClosureEntity(
            Database database,
            Transaction transaction,
            BlockTableRecord space,
            ObjectId layerId,
            Point3d start,
            Point3d end,
            string marker,
            Point3d junction,
            bool featureLine,
            ObjectId siteId,
            double weedDistance,
            double weedAngle,
            IList<Autodesk.Civil.DatabaseServices.Surface> topSurfaces,
            ref int surfaceVertices,
            ref int unresolvedVertices)
        {
            try
            {
                var polyline = new Polyline(3);
                polyline.SetDatabaseDefaults(database);
                polyline.LayerId = layerId;
                polyline.Color = Color.FromColorIndex(ColorMethod.ByAci, 6);
                polyline.AddVertexAt(0, new Point2d(start.X, start.Y), 0.0, 0.0, 0.0);
                polyline.AddVertexAt(1, new Point2d((start.X + end.X) * 0.5, (start.Y + end.Y) * 0.5), 0.0, 0.0, 0.0);
                polyline.AddVertexAt(2, new Point2d(end.X, end.Y), 0.0, 0.0, 0.0);
                ObjectId sourceId = space.AppendEntity(polyline);
                transaction.AddNewlyCreatedDBObject(polyline, true);

                Entity output = polyline;
                ObjectId outputId = sourceId;
                if (featureLine)
                {
                    string name = "CE-" + marker + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    outputId = siteId.IsNull
                        ? CivilFeatureLine.Create(name, sourceId)
                        : CivilFeatureLine.Create(name, sourceId, siteId);
                    CivilFeatureLine created = transaction.GetObject(outputId, OpenMode.ForWrite, false) as CivilFeatureLine;
                    if (created != null)
                    {
                        created.LayerId = layerId;
                        created.Color = Color.FromColorIndex(ColorMethod.ByAci, 6);
                        // The centre crown and both edge controls are mandatory;
                        // straight-line weeding would remove the midpoint.
                        if (topSurfaces.Count > 0)
                        {
                            int unresolved;
                            surfaceVertices += JunctionSurfaceVertices.Apply(created, topSurfaces, out unresolved);
                            unresolvedVertices += unresolved;
                        }
                        output = created;
                    }
                    TryEraseGeneratedSource(polyline);
                }

                output.XData = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, marker),
                    new TypedValue((int)DxfCode.ExtendedDataReal, junction.X),
                    new TypedValue((int)DxfCode.ExtendedDataReal, junction.Y),
                    new TypedValue((int)DxfCode.ExtendedDataReal, junction.Z));
                return outputId;
            }
            catch { return ObjectId.Null; }
        }

        private static void TryEraseGeneratedSource(DBObject source)
        {
            if (source == null || source.IsErased) return;
            try
            {
                MethodInfo method = source.GetType().GetMethod("Erase", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method != null) method.Invoke(source, null);
            }
            catch { }
        }

        private static void ApplyFeatureLineWeeding(CivilFeatureLine featureLine, double distance, double angleDegrees)
        {
            if (featureLine == null || (distance <= 0.0 && angleDegrees <= 0.0)) return;
            foreach (string name in new[] { "WeedPoints", "Weed" })
            {
                foreach (MethodInfo method in featureLine.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(item => item.Name == name))
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    try
                    {
                        if (parameters.Length == 2 &&
                            parameters[0].ParameterType == typeof(double) &&
                            parameters[1].ParameterType == typeof(double))
                        {
                            method.Invoke(featureLine, new object[] { distance, angleDegrees * Math.PI / 180.0 });
                            return;
                        }
                        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(double) && distance > 0.0)
                        {
                            method.Invoke(featureLine, new object[] { distance });
                            return;
                        }
                    }
                    catch { }
                }
            }
        }

        private static List<string> ReadSiteNames()
        {
            var result = new List<string>();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (civilDocument == null || document == null) return result;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civilDocument.GetSiteIds())
                {
                    Site site = null;
                    try { site = transaction.GetObject(id, OpenMode.ForRead, false) as Site; } catch { }
                    if (site != null && !string.IsNullOrWhiteSpace(site.Name)) result.Add(site.Name);
                }
            }
            return result.OrderBy(item => item, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static ObjectId ResolveSite(string requested)
        {
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (civilDocument == null || document == null) return ObjectId.Null;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civilDocument.GetSiteIds())
                {
                    Site site = null;
                    try { site = transaction.GetObject(id, OpenMode.ForRead, false) as Site; } catch { }
                    if (site != null && string.Equals(site.Name, requested, StringComparison.OrdinalIgnoreCase))
                        return id;
                }
            }

            if (!string.Equals(requested, "<Create CE-JUNCTIONS>", StringComparison.OrdinalIgnoreCase))
                return ObjectId.Null;

            try
            {
                MethodInfo create = typeof(Site).GetMethod(
                    "Create",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(CivilDocument), typeof(string) },
                    null);
                if (create == null) return ObjectId.Null;
                object result = create.Invoke(null, new object[] { civilDocument, "CE-JUNCTIONS" });
                return result is ObjectId ? (ObjectId)result : ObjectId.Null;
            }
            catch { return ObjectId.Null; }
        }

        private static double PositiveSweep(double fromAngle, double toAngle)
        {
            double sweep = toAngle - fromAngle;
            double fullTurn = Math.PI * 2.0;
            while (sweep < 0.0) sweep += fullTurn;
            while (sweep >= fullTurn) sweep -= fullTurn;
            return sweep;
        }

        private static void CreateLabel(Database database, Transaction transaction, BlockTableRecord space, ObjectId layerId, Point3d position, string value, double paperHeight, ObjectId arcId, Point3d junction)
        {
            var text = new MText
            {
                Location = position,
                Contents = value,
                Attachment = AttachmentPoint.MiddleCenter,
                LayerId = layerId,
                Color = Color.FromColorIndex(ColorMethod.ByLayer, 256),
                TextHeight = PaperAnnotationScale.ModelTextHeight(database, paperHeight)
            };
            text.SetDatabaseDefaults(database);
            PaperAnnotationScale.SetAnnotative(text);
            space.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
            text.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, arcId.Handle.ToString()),
                new TypedValue((int)DxfCode.ExtendedDataReal, junction.X),
                new TypedValue((int)DxfCode.ExtendedDataReal, junction.Y),
                new TypedValue((int)DxfCode.ExtendedDataReal, junction.Z));
        }

        private static ObjectId EnsureLayer(
            Database database,
            Transaction transaction,
            string requestedName)
        {
            string name = CleanLayerName(requestedName);
            LayerTable table = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (table.Has(name)) return table[name];

            table.UpgradeOpen();
            var record = new LayerTableRecord
            {
                Name = name,
                IsPlottable = true
            };
            record.Color = Color.FromColorIndex(ColorMethod.ByAci, 6);
            ObjectId id = table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return id;
        }

        private static string CleanLayerName(string value)
        {
            string name = (value ?? string.Empty).Trim();
            foreach (char invalid in new[] { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', '=' })
                name = name.Replace(invalid.ToString(), string.Empty);
            return string.IsNullOrWhiteSpace(name) ? JunctionLayer : name;
        }

        private static void EnsureRegApp(Database database, Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(database.RegAppTableId, OpenMode.ForRead, false) as RegAppTable;
            if (table.Has(AppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = AppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static string CleanPrefix(string value)
        {
            string cleaned = new string((value ?? "J").Where(character => char.IsLetterOrDigit(character) || character == '-' || character == '_').ToArray());
            return string.IsNullOrWhiteSpace(cleaned) ? "J" : cleaned;
        }

        private static Vector3d Plan(Vector3d value)
        {
            return new Vector3d(value.X, value.Y, 0.0);
        }

        private sealed class TLimitReturn
        {
            internal ObjectId Id;
            internal Point3d First;
            internal Point3d Last;
        }

        private sealed class JunctionCandidate
        {
            internal Point3d Point;
            internal Vector3d Main;
            internal Vector3d Side;
            internal bool IsCross;
        }

        private struct ReturnDefinition
        {
            internal Point3d Centre;
            internal Point3d Start;
            internal Point3d Mid;
            internal Point3d End;
            internal int MainSign;
            internal int SideSign;
        }
    }
}
