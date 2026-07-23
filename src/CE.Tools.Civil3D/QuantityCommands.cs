using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.QuantityCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Quick quantity commands for general AutoCAD and Civil 3D geometry.
    /// </summary>
    public sealed class QuantityCommands
    {
        [CommandMethod(
            "CE_TOOLS",
            "CE_TLENGTH",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void TotalLength()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            Editor editor = document.Editor;
            Database database = document.Database;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect linework to total: ");

            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            var report = new QuantityReport();

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    if (selectedObject == null)
                    {
                        continue;
                    }

                    var curve = transaction.GetObject(
                        selectedObject.ObjectId,
                        OpenMode.ForRead,
                        false) as Curve;

                    double length;
                    if (curve == null || !TryGetFiniteLength(curve, out length))
                    {
                        report.Skipped++;
                        continue;
                    }

                    report.Add(curve.Layer, length);
                }
            }

            WriteReport(editor, "CE_TLENGTH", "LENGTH", "drawing units", report);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_TAREA",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void TotalArea()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            Editor editor = document.Editor;
            Database database = document.Database;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect closed boundaries, hatches or regions to total: ");

            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            var report = new QuantityReport();

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    if (selectedObject == null)
                    {
                        continue;
                    }

                    var entity = transaction.GetObject(
                        selectedObject.ObjectId,
                        OpenMode.ForRead,
                        false) as Entity;

                    double area;
                    if (entity == null || !TryGetFiniteArea(entity, out area))
                    {
                        report.Skipped++;
                        continue;
                    }

                    report.Add(entity.Layer, area);
                }
            }

            WriteReport(editor, "CE_TAREA", "AREA", "square drawing units", report);
        }

        private static PromptSelectionResult GetSelection(Editor editor, string message)
        {
            return editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = message,
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
        }

        private static bool TryGetFiniteLength(Curve curve, out double length)
        {
            length = 0.0;

            try
            {
                double startDistance = curve.GetDistanceAtParameter(curve.StartParam);
                double endDistance = curve.GetDistanceAtParameter(curve.EndParam);
                length = Math.Abs(endDistance - startDistance);
                return IsFinitePositive(length);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return false;
            }
        }

        private static bool TryGetFiniteArea(Entity entity, out double area)
        {
            area = 0.0;

            try
            {
                var hatch = entity as Hatch;
                if (hatch != null)
                {
                    area = Math.Abs(hatch.Area);
                    return IsFinitePositive(area);
                }

                var region = entity as Region;
                if (region != null)
                {
                    area = Math.Abs(region.Area);
                    return IsFinitePositive(area);
                }

                var curve = entity as Curve;
                if (curve == null || !curve.Closed)
                {
                    return false;
                }

                area = Math.Abs(curve.Area);
                return IsFinitePositive(area);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return false;
            }
        }

        private static bool IsFinitePositive(double value)
        {
            return !double.IsNaN(value) &&
                   !double.IsInfinity(value) &&
                   value > 0.0;
        }

        private static void WriteReport(
            Editor editor,
            string commandName,
            string quantityName,
            string unitText,
            QuantityReport report)
        {
            editor.WriteMessage(
                $"\n{commandName} complete. Counted: {report.Counted}; " +
                $"skipped: {report.Skipped}; TOTAL {quantityName} = " +
                $"{report.Total:N3} {unitText}.");

            foreach (KeyValuePair<string, LayerQuantity> item in report.ByLayer)
            {
                editor.WriteMessage(
                    $"\n  Layer {item.Key}: {item.Value.Value:N3} " +
                    $"({item.Value.Count} object{(item.Value.Count == 1 ? string.Empty : "s")})");
            }
        }

        private sealed class QuantityReport
        {
            public QuantityReport()
            {
                ByLayer = new SortedDictionary<string, LayerQuantity>(
                    StringComparer.OrdinalIgnoreCase);
            }

            public int Counted { get; private set; }

            public int Skipped { get; set; }

            public double Total { get; private set; }

            public SortedDictionary<string, LayerQuantity> ByLayer { get; }

            public void Add(string layer, double value)
            {
                LayerQuantity layerQuantity;
                if (!ByLayer.TryGetValue(layer, out layerQuantity))
                {
                    layerQuantity = new LayerQuantity();
                    ByLayer[layer] = layerQuantity;
                }

                Counted++;
                Total += value;
                layerQuantity.Count++;
                layerQuantity.Value += value;
            }
        }

        private sealed class LayerQuantity
        {
            public int Count { get; set; }

            public double Value { get; set; }
        }
    }
}
