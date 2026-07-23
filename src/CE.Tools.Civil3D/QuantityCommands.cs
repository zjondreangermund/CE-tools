using System;
using System.Collections.Generic;
using System.Linq;
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

            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect linework to total: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });

            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            double total = 0.0;
            int counted = 0;
            int skipped = 0;
            var byLayer = new SortedDictionary<string, LayerQuantity>(StringComparer.OrdinalIgnoreCase);

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
                        skipped++;
                        continue;
                    }

                    total += length;
                    counted++;
                    AddLayerQuantity(byLayer, curve.Layer, length);
                }
            }

            WriteLengthReport(editor, counted, skipped, total, byLayer);
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

            PromptSelectionResult selection = editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect closed boundaries, hatches or regions to total: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });

            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            double total = 0.0;
            int counted = 0;
            int skipped = 0;
            var byLayer = new SortedDictionary<string, LayerQuantity>(StringComparer.OrdinalIgnoreCase);

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
                        skipped++;
                        continue;
                    }

                    total += area;
                    counted++;
                    AddLayerQuantity(byLayer, entity.Layer, area);
                }
            }

            WriteAreaReport(editor, counted, skipped, total, byLayer);
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

                var existingRegion = entity as Region;
                if (existingRegion != null)
                {
                    area = Math.Abs(existingRegion.Area);
                    return IsFinitePositive(area);
                }

                var curve = entity as Curve;
                if (curve == null || !curve.Closed)
                {
                    return false;
                }

                var sourceCurves = new DBObjectCollection();
                Curve curveClone = null;
                DBObjectCollection generatedRegions = null;

                try
                {
                    curveClone = curve.Clone() as Curve;
                    if (curveClone == null)
                    {
                        return false;
                    }

                    sourceCurves.Add(curveClone);
                    generatedRegions = Region.CreateFromCurves(sourceCurves);

                    double sum = 0.0;
                    foreach (DBObject databaseObject in generatedRegions)
                    {
                        var region = databaseObject as Region;
                        if (region != null)
                        {
                            sum += Math.Abs(region.Area);
                        }
                    }

                    area = sum;
                    return IsFinitePositive(area);
                }
                finally
                {
                    if (generatedRegions != null)
                    {
                        foreach (DBObject databaseObject in generatedRegions)
                        {
                            databaseObject.Dispose();
                        }
                    }

                    curveClone?.Dispose();
                }
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

        private static void AddLayerQuantity(
            IDictionary<string, LayerQuantity> quantities,
            string layer,
            double value)
        {
            LayerQuantity existing;
            if (!quantities.TryGetValue(layer, out existing))
            {
                existing = new LayerQuantity();
                quantities[layer] = existing;
            }

            existing.Count++;
            existing.Value += value;
        }

        private static void WriteLengthReport(
            Editor editor,
            int counted,
            int skipped,
            double total,
            IEnumerable<KeyValuePair<string, LayerQuantity>> byLayer)
        {
            editor.WriteMessage(
                $"\nCE_TLENGTH complete. Counted: {counted}; skipped: {skipped}; " +
                $"TOTAL LENGTH = {total:N3} drawing units.");

            foreach (KeyValuePair<string, LayerQuantity> item in byLayer)
            {
                editor.WriteMessage(
                    $"\n  Layer {item.Key}: {item.Value.Value:N3} " +
                    $"({item.Value.Count} object{(item.Value.Count == 1 ? string.Empty : "s")})");
            }
        }

        private static void WriteAreaReport(
            Editor editor,
            int counted,
            int skipped,
            double total,
            IEnumerable<KeyValuePair<string, LayerQuantity>> byLayer)
        {
            editor.WriteMessage(
                $"\nCE_TAREA complete. Counted: {counted}; skipped: {skipped}; " +
                $"TOTAL AREA = {total:N3} square drawing units.");

            foreach (KeyValuePair<string, LayerQuantity> item in byLayer)
            {
                editor.WriteMessage(
                    $"\n  Layer {item.Key}: {item.Value.Value:N3} " +
                    $"({item.Value.Count} object{(item.Value.Count == 1 ? string.Empty : "s")})");
            }
        }

        private sealed class LayerQuantity
        {
            public int Count { get; set; }

            public double Value { get; set; }
        }
    }
}
