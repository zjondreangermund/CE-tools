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

[assembly: CommandClass(typeof(CETools.Civil3D.September14FeatureLineCompletionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Focused completion front doors for the remaining feature-line field comments.
    /// Existing Civil 3D feature-line geometry is never erased by the link command;
    /// only the CE_FLREL relationship record is attached after the offset is verified.
    /// New stepped offsets are delegated to the August 21 candidate-first fatal-safety
    /// boundary, and direct surface links are delegated to the August 23 dynamic drape.
    /// </summary>
    public sealed class September14FeatureLineCompletionCommands
    {
        private const string RelationKey = "CE_FLREL";
        private const double Tolerance = 1e-7;

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLRELLINKEXISTING",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void LinkExistingRelativeFeatureLines()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            Editor editor = document.Editor;
            var sourceOptions = new PromptEntityOptions("\nSelect SOURCE feature line: ");
            sourceOptions.SetRejectMessage("\nSelect an ordinary editable Civil 3D feature line.");
            sourceOptions.AddAllowedClass(typeof(CivilFeatureLine), false);
            PromptEntityResult sourceResult = editor.GetEntity(sourceOptions);
            if (sourceResult.Status != PromptStatus.OK) return;

            var selectionOptions = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect EXISTING offset feature lines to link to the source: "
            };
            PromptSelectionResult selection = editor.GetSelection(selectionOptions);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            var candidates = new List<RelativeCandidate>();
            int skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine source = OpenFeatureLine(transaction, sourceResult.ObjectId, OpenMode.ForRead);
                string sourceError;
                if (!Editable(source, transaction, out sourceError))
                {
                    editor.WriteMessage("\nCE_FLRELLINKEXISTING cancelled. " + sourceError);
                    return;
                }

                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    if (id == sourceResult.ObjectId)
                    {
                        skipped++;
                        continue;
                    }

                    CivilFeatureLine child = OpenFeatureLine(transaction, id, OpenMode.ForRead);
                    string error;
                    if (!Editable(child, transaction, out error))
                    {
                        skipped++;
                        continue;
                    }
                    if (HasRelation(child, transaction))
                    {
                        skipped++;
                        editor.WriteMessage("\n'{0}' is already linked by CE_FLREL; it was kept unchanged.", SafeName(child));
                        continue;
                    }

                    double horizontal;
                    double vertical;
                    if (!TryMeasureConstantOffset(source, child, out horizontal, out vertical, out error))
                    {
                        skipped++;
                        editor.WriteMessage("\n'{0}' was not linked. {1}", SafeName(child), error);
                        continue;
                    }

                    candidates.Add(new RelativeCandidate(id, SafeName(child), horizontal, vertical));
                }
            }

            int linked = 0;
            int sequence = 1;
            foreach (RelativeCandidate candidate in candidates
                .OrderBy(item => Math.Abs(item.HorizontalOffset))
                .ThenBy(item => item.HorizontalOffset))
            {
                try
                {
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        CivilFeatureLine child = OpenFeatureLine(transaction, candidate.ObjectId, OpenMode.ForWrite);
                        string error;
                        if (!Editable(child, transaction, out error) || HasRelation(child, transaction))
                        {
                            skipped++;
                            continue;
                        }

                        WriteRelation(
                            child,
                            sourceResult.ObjectId.Handle.ToString(),
                            candidate.HorizontalOffset,
                            candidate.VerticalOffset,
                            sequence,
                            transaction);
                        transaction.Commit();
                    }
                    linked++;
                    sequence++;
                }
                catch (System.Exception exception)
                {
                    skipped++;
                    editor.WriteMessage("\n'{0}' was kept unchanged after a safe link failure. {1}", candidate.Name, exception.Message);
                }
            }

            if (linked > 0)
            {
                PlatformDynamicRefreshManager.EnsureInitialized();
                PlatformDynamicRefreshManager.Queue();
            }
            editor.Regen();
            editor.WriteMessage(
                "\nCE_FLRELLINKEXISTING complete. Existing feature lines linked={0}; skipped={1}. Geometry was not recreated or erased.",
                linked,
                skipped);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLSTEPSSAFE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateSafeSteppedOffsets()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Safe Multiple Feature-Line Steps",
                "Create stepped offsets from multiple source feature lines. Each candidate is committed and verified before it becomes a linked CE_FLREL child.");
            settings.AddPositiveDouble("Horizontal", "Steps", "Horizontal step", 1.0, "Horizontal offset per step.");
            settings.AddText("Vertical", "Steps", "Vertical step", "-0.500", "Signed elevation difference per step.");
            settings.AddPositiveInteger("Count", "Steps", "Step count", 1, "Number of linked children per selected source.");
            settings.AddText("Suffix", "Naming", "Child suffix", "STEP", "Suffix used for generated feature-line names.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            double vertical;
            if (!ProductionSettingsDialogModel.TryDouble(settings.Text("Vertical"), out vertical))
            {
                document.Editor.WriteMessage("\nCE_FLSTEPSSAFE cancelled. Vertical step must be a number.");
                return;
            }

            var selectionOptions = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect multiple source feature lines for stepped offsets: "
            };
            PromptSelectionResult selection = document.Editor.GetSelection(selectionOptions);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            PlatformDynamicRefreshManager.EnsureInitialized();
            August21PlatformRelativeFatalSafety.PlatformStepResult result =
                August21PlatformRelativeFatalSafety.CreatePlatformSteps(
                    document,
                    selection.Value.GetObjectIds().Distinct(),
                    Math.Max(0.001, settings.Double("Horizontal", 1.0)),
                    vertical,
                    Math.Max(1, settings.Integer("Count", 1)),
                    string.IsNullOrWhiteSpace(settings.Text("Suffix")) ? "STEP" : settings.Text("Suffix").Trim());

            if (result.Created > 0) PlatformDynamicRefreshManager.Queue();
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_FLSTEPSSAFE complete. Linked steps={0}; skipped={1}. Existing source feature lines were kept.",
                result.Created,
                result.Skipped);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLSURFACELINKEXISTING",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void LinkExistingFeatureLinesToSurface()
        {
            // Reuse the established August 23 multi-feature-line dynamic drape. It
            // samples through August21SurfaceSafety and stores the persistent direct
            // drape link without rebuilding/deleting the selected source feature line.
            new August23PlatformDynamicGradingCommands().DrapeMultipleFeatureLines();
        }

        private static bool TryMeasureConstantOffset(
            CivilFeatureLine source,
            CivilFeatureLine child,
            out double horizontalOffset,
            out double verticalOffset,
            out string error)
        {
            horizontalOffset = 0.0;
            verticalOffset = 0.0;
            error = string.Empty;

            Point3dCollection points = child.GetPoints(FeatureLinePointType.PIPoint);
            if (points == null || points.Count < 2)
                points = child.GetPoints(FeatureLinePointType.AllPoints);
            if (points == null || points.Count < 2)
            {
                error = "At least two feature-line points are required.";
                return false;
            }

            var horizontal = new List<double>();
            var vertical = new List<double>();
            int sign = 0;
            foreach (Point3d point in points.Cast<Point3d>())
            {
                Point3d projected = new Point3d(point.X, point.Y, 0.0);
                Point3d sourcePoint;
                try
                {
                    sourcePoint = source.GetClosestPointTo(projected, Vector3d.ZAxis, false);
                }
                catch (System.Exception exception)
                {
                    error = "The source/child offset could not be measured. " + exception.Message;
                    return false;
                }

                double dx = point.X - sourcePoint.X;
                double dy = point.Y - sourcePoint.Y;
                double distance = Math.Sqrt((dx * dx) + (dy * dy));
                int currentSign = sign;
                if (distance > Tolerance)
                {
                    try
                    {
                        double parameter = source.GetParameterAtPoint(sourcePoint);
                        Vector3d tangent = source.GetFirstDerivative(parameter);
                        double cross = (tangent.X * dy) - (tangent.Y * dx);
                        if (Math.Abs(cross) > Tolerance) currentSign = cross > 0.0 ? 1 : -1;
                    }
                    catch
                    {
                        // Keep the previously established side when a derivative is
                        // unavailable at an endpoint.
                    }
                }
                if (currentSign == 0) currentSign = sign == 0 ? 1 : sign;
                if (sign == 0 && distance > Tolerance) sign = currentSign;
                if (distance > Tolerance && sign != 0 && currentSign != sign)
                {
                    error = "The selected child crosses the source and is not one constant-side offset.";
                    return false;
                }

                horizontal.Add(distance * currentSign);
                vertical.Add(point.Z - sourcePoint.Z);
            }

            horizontalOffset = horizontal.Average();
            verticalOffset = vertical.Average();
            double horizontalTolerance = Math.Max(0.010, Math.Abs(horizontalOffset) * 0.03);
            double verticalTolerance = Math.Max(0.010, Math.Abs(verticalOffset) * 0.02);
            if (horizontal.Any(value => Math.Abs(value - horizontalOffset) > horizontalTolerance))
            {
                error = "Plan offsets are not constant enough to rebuild safely as a CE_FLREL child.";
                return false;
            }
            if (vertical.Any(value => Math.Abs(value - verticalOffset) > verticalTolerance))
            {
                error = "Elevation differences are not constant enough to rebuild safely as a CE_FLREL child.";
                return false;
            }
            return true;
        }

        private static CivilFeatureLine OpenFeatureLine(Transaction transaction, ObjectId id, OpenMode mode)
        {
            return id.IsNull ? null : transaction.GetObject(id, mode, false) as CivilFeatureLine;
        }

        private static bool Editable(CivilFeatureLine featureLine, Transaction transaction, out string error)
        {
            error = string.Empty;
            if (featureLine == null)
            {
                error = "Selection is not a Civil 3D feature line.";
                return false;
            }
            if (featureLine.IsReferenceObject)
            {
                error = "Referenced feature lines are read-only.";
                return false;
            }
            LayerTableRecord layer = transaction.GetObject(featureLine.LayerId, OpenMode.ForRead, false) as LayerTableRecord;
            if (layer != null && layer.IsLocked)
            {
                error = "The feature-line layer is locked.";
                return false;
            }
            return true;
        }

        private static bool HasRelation(CivilFeatureLine child, Transaction transaction)
        {
            if (child == null || child.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = transaction.GetObject(child.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            return dictionary != null && dictionary.Contains(RelationKey);
        }

        private static void WriteRelation(
            CivilFeatureLine child,
            string sourceHandle,
            double horizontalOffset,
            double verticalOffset,
            int sequence,
            Transaction transaction)
        {
            if (child.ExtensionDictionary.IsNull) child.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(child.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) throw new InvalidOperationException("Feature-line extension dictionary is unavailable.");

            Xrecord record;
            if (dictionary.Contains(RelationKey))
            {
                record = transaction.GetObject(dictionary.GetAt(RelationKey), OpenMode.ForWrite, false) as Xrecord;
                if (record == null) throw new InvalidOperationException("Existing CE_FLREL relationship record is invalid.");
            }
            else
            {
                record = new Xrecord();
                dictionary.SetAt(RelationKey, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }

            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, sourceHandle),
                new TypedValue((int)DxfCode.Real, horizontalOffset),
                new TypedValue((int)DxfCode.Real, verticalOffset),
                new TypedValue((int)DxfCode.Int32, sequence));
        }

        private static string SafeName(CivilFeatureLine featureLine)
        {
            return featureLine == null || string.IsNullOrWhiteSpace(featureLine.Name)
                ? "FeatureLine"
                : featureLine.Name;
        }

        private sealed class RelativeCandidate
        {
            internal RelativeCandidate(ObjectId objectId, string name, double horizontalOffset, double verticalOffset)
            {
                ObjectId = objectId;
                Name = name;
                HorizontalOffset = horizontalOffset;
                VerticalOffset = verticalOffset;
            }

            internal ObjectId ObjectId { get; private set; }
            internal string Name { get; private set; }
            internal double HorizontalOffset { get; private set; }
            internal double VerticalOffset { get; private set; }
        }
    }
}
