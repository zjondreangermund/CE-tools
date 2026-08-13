using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August13PolylineSelectionCommands))]

namespace CETools.Civil3D
{
    public sealed class August13PolylineSelectionCommands
    {
        private const double MinimumTolerance = 1e-9;

        [CommandMethod("CE_TOOLS", "CE_POLYLINESELECTIONTOOLS", CommandFlags.Modal)]
        public void PolylineSelectionTools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Polyline Selection Tools",
                "Quickly collect drawing geometry by measured polyline length.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Select polylines shorter than a length", "CE_SELECTPOLYLINESHORTER", "Enter a maximum length and select all shorter 2D/3D polylines in the current space.", "01 Length selection"),
                    new DisciplineWorkflowAction("Select polylines with the same length", "CE_SELECTPOLYLINESAMELENGTH", "Pick one reference polyline, set a tolerance and select every matching 2D/3D polyline in the current space.", "01 Length selection")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SELECTPOLYLINESHORTER", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SelectPolylinesShorterThan()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            Editor editor = document.Editor;
            var options = new PromptDoubleOptions("\nSelect polylines shorter than length <10.000>: ")
            {
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = 10.0,
                UseDefaultValue = true
            };
            PromptDoubleResult result = editor.GetDouble(options);
            if (result.Status != PromptStatus.OK) return;

            List<PolylineLengthItem> items = ReadPolylineLengths(document.Database);
            ObjectId[] matches = items.Where(item => item.Length < result.Value).Select(item => item.Id).ToArray();
            editor.SetImpliedSelection(matches);
            editor.WriteMessage("\nCE_SELECTPOLYLINESHORTER complete. Limit={0:N3}; selected={1}; scanned={2}.", result.Value, matches.Length, items.Count);
        }

        [CommandMethod("CE_TOOLS", "CE_SELECTPOLYLINESAMELENGTH", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SelectPolylinesWithSameLength()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            Editor editor = document.Editor;
            PromptEntityResult reference = editor.GetEntity("\nSelect the reference polyline whose length must be matched: ");
            if (reference.Status != PromptStatus.OK) return;

            double referenceLength;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Entity entity = transaction.GetObject(reference.ObjectId, OpenMode.ForRead, false) as Entity;
                if (!IsSupportedPolyline(entity) || !TryReadCurveLength(entity as Curve, out referenceLength))
                {
                    editor.WriteMessage("\nCE_SELECTPOLYLINESAMELENGTH cancelled. Select a measurable 2D/3D polyline.");
                    return;
                }
            }

            double suggested = Math.Max(0.001, Math.Abs(referenceLength) * 1e-6);
            var toleranceOptions = new PromptDoubleOptions(string.Format(CultureInfo.CurrentCulture, "\nLength matching tolerance <{0:N3}>: ", suggested))
            {
                AllowNegative = false,
                AllowZero = true,
                DefaultValue = suggested,
                UseDefaultValue = true
            };
            PromptDoubleResult toleranceResult = editor.GetDouble(toleranceOptions);
            if (toleranceResult.Status != PromptStatus.OK) return;
            double tolerance = Math.Max(toleranceResult.Value, MinimumTolerance);

            List<PolylineLengthItem> items = ReadPolylineLengths(document.Database);
            ObjectId[] matches = items.Where(item => Math.Abs(item.Length - referenceLength) <= tolerance).Select(item => item.Id).ToArray();
            editor.SetImpliedSelection(matches);
            editor.WriteMessage("\nCE_SELECTPOLYLINESAMELENGTH complete. Reference={0:N3}; tolerance={1:N6}; selected={2}; scanned={3}.", referenceLength, tolerance, matches.Length, items.Count);
        }

        private static List<PolylineLengthItem> ReadPolylineLengths(Database database)
        {
            var result = new List<PolylineLengthItem>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return result;
                foreach (ObjectId id in space)
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                    catch { continue; }
                    if (!IsSupportedPolyline(entity)) continue;
                    double length;
                    if (TryReadCurveLength(entity as Curve, out length)) result.Add(new PolylineLengthItem(id, length));
                }
            }
            return result;
        }

        private static bool IsSupportedPolyline(Entity entity)
        {
            return entity is Polyline || entity is Polyline2d || entity is Polyline3d;
        }

        private static bool TryReadCurveLength(Curve curve, out double length)
        {
            length = 0.0;
            if (curve == null) return false;
            try
            {
                double start = curve.GetDistanceAtParameter(curve.StartParam);
                double end = curve.GetDistanceAtParameter(curve.EndParam);
                length = Math.Abs(end - start);
                return !double.IsNaN(length) && !double.IsInfinity(length);
            }
            catch { return false; }
        }

        private sealed class PolylineLengthItem
        {
            internal PolylineLengthItem(ObjectId id, double length) { Id = id; Length = length; }
            internal ObjectId Id { get; private set; }
            internal double Length { get; private set; }
        }
    }
}
