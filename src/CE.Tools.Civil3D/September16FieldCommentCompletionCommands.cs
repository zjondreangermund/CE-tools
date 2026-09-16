using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

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
        private const string AppName = "CE_ROAD_JUNCTION";
        private const double Tol = 1e-6;

        [CommandMethod("CE_TOOLS", "CE_ROADJUNCTIONBATCH", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateAllJunctionReturns()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Batch T/Cross Junction Bellmouths",
                "Detect every unique crossing between selected road-centre curves, create all T/cross bellmouth returns in one transaction and leave the source geometry unchanged.");
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
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selected = document.Editor.SelectImplied();
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count < 2)
            {
                var options = new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect all road-centre lines, polylines or feature lines: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                };
                selected = document.Editor.GetSelection(options);
            }
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count < 2) return;

            double radius = model.Double("Radius", 10.0);
            double mainHalfWidth = model.Double("MainHalfWidth", 3.7);
            double sideHalfWidth = model.Double("SideHalfWidth", 3.7);
            double endpointTolerance = model.Double("EndpointTolerance", 1.0);
            double clusterTolerance = model.Double("ClusterTolerance", 0.10);
            double textHeight = model.Double("TextHeight", 2.5);
            string prefix = CleanPrefix(model.Text("Prefix"));
            int startNumber = model.Integer("Start", 1);
            int created = 0;
            int junctions = 0;
            int failedPairs = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                var curves = new List<Curve>();
                foreach (ObjectId id in selected.Value.GetObjectIds().Distinct())
                {
                    Curve curve = null;
                    try { curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve; }
                    catch { }
                    if (curve != null) curves.Add(curve);
                }
                if (curves.Count < 2)
                {
                    document.Editor.WriteMessage("\nCE_ROADJUNCTIONBATCH stopped. Select at least two usable road-centre curves.");
                    return;
                }

                ObjectId layerId = EnsureLayer(document.Database, transaction);
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
                    IEnumerable<ReturnDefinition> definitions = candidate.IsCross
                        ? CrossReturns(candidate, mainHalfWidth, sideHalfWidth, radius)
                        : TReturns(candidate, mainHalfWidth, sideHalfWidth, radius);

                    int returnNumber = 0;
                    foreach (ReturnDefinition definition in definitions)
                    {
                        returnNumber++;
                        ObjectId arcId = CreateReturn(document.Database, transaction, space, layerId, definition, radius);
                        if (arcId.IsNull) continue;
                        string label = prefix + junctionNumber.ToString(CultureInfo.InvariantCulture) + "." +
                            returnNumber.ToString(CultureInfo.InvariantCulture);
                        CreateLabel(document.Database, transaction, space, layerId, definition.Mid, label, textHeight, arcId, candidate.Point);
                        created++;
                    }
                    if (returnNumber > 0) junctions++;
                }

                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_ROADJUNCTIONBATCH complete. Junctions={0}; bellmouth returns={1}; failed curve pairs={2}. Run CE_ROADJUNCTIONCONSTRUCTION and choose All CE junction geometry to split all selected corridors at the new limits.",
                junctions, created, failedPairs);
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
            return new ReturnDefinition { Centre = centre, Start = start, Mid = middle, End = end };
        }

        private static ObjectId CreateReturn(Database database, Transaction transaction, BlockTableRecord space, ObjectId layerId, ReturnDefinition definition, double radius)
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

                // AutoCAD 2023 Arc has no three-point constructor. Choose the
                // counter-clockwise endpoint order whose sweep contains Mid so the
                // generated geometry is the intended bellmouth quarter-circle.
                double endSweep = PositiveSweep(startAngle, endAngle);
                double midSweep = PositiveSweep(startAngle, midAngle);
                Arc arc = midSweep <= endSweep + Tol
                    ? new Arc(definition.Centre, Vector3d.ZAxis, radius, startAngle, endAngle)
                    : new Arc(definition.Centre, Vector3d.ZAxis, radius, endAngle, startAngle);

                arc.SetDatabaseDefaults(database);
                arc.LayerId = layerId;
                arc.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
                ObjectId id = space.AppendEntity(arc);
                transaction.AddNewlyCreatedDBObject(arc, true);
                arc.XData = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, "BATCH"),
                    new TypedValue((int)DxfCode.ExtendedDataReal, radius));
                return id;
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

        private static ObjectId EnsureLayer(Database database, Transaction transaction)
        {
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table.Has(JunctionLayer)) return table[JunctionLayer];
            table.UpgradeOpen();
            var record = new LayerTableRecord { Name = JunctionLayer, IsPlottable = true };
            record.Color = Color.FromColorIndex(ColorMethod.ByAci, 3);
            ObjectId id = table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return id;
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
        }
    }
}
