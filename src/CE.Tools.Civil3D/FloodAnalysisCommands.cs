using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CETools.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.FloodAnalysisCommands))]

namespace CETools.Civil3D
{
    public sealed class FloodAnalysisCommands
    {
        [CommandMethod("CE_FLOODQUICK", CommandFlags.Modal)]
        public void QuickFloodAnalysis()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            Editor editor = document.Editor;

            PromptEntityOptions boundaryOptions = new PromptEntityOptions("\nSelect a closed catchment boundary: ");
            boundaryOptions.SetRejectMessage("\nSelect a closed planar curve.");
            boundaryOptions.AddAllowedClass(typeof(Curve), false);
            PromptEntityResult boundaryResult = editor.GetEntity(boundaryOptions);
            if (boundaryResult.Status != PromptStatus.OK) return;

            double drawingArea;
            using (Transaction transaction = document.TransactionManager.StartTransaction())
            {
                Curve boundary = transaction.GetObject(boundaryResult.ObjectId, OpenMode.ForRead, false) as Curve;
                if (boundary == null || !boundary.Closed)
                {
                    editor.WriteMessage("\nCE_FLOODQUICK: the selected curve is not closed.");
                    return;
                }

                try { drawingArea = boundary.Area; }
                catch
                {
                    editor.WriteMessage("\nCE_FLOODQUICK: the selected boundary has no valid planar area.");
                    return;
                }
            }

            double squareUnitsPerHectare;
            if (!GetPositiveDouble(editor, "\nSquare drawing units per hectare <10000>: ", 10000.0, out squareUnitsPerHectare)) return;
            double areaHectares = drawingArea / squareUnitsPerHectare;
            if (areaHectares <= 0.0)
            {
                editor.WriteMessage("\nCE_FLOODQUICK: catchment area must be greater than zero.");
                return;
            }

            double preCoefficient;
            double postCoefficient;
            if (!GetCoefficient(editor, "\nPre-development runoff coefficient <0.30>: ", 0.30, out preCoefficient)) return;
            if (!GetCoefficient(editor, "\nPost-development runoff coefficient <0.70>: ", 0.70, out postCoefficient)) return;

            var intensities = new Dictionary<int, double>();
            foreach (int period in FloodAnalysisCalculator.StandardReturnPeriods)
            {
                double intensity;
                if (!GetPositiveDouble(editor, $"\n{period}-year rainfall intensity in mm/h: ", 0.0, out intensity, false)) return;
                intensities.Add(period, intensity);
            }

            double slope;
            double manningsN;
            if (!GetPositiveDouble(editor, "\nPreliminary culvert slope m/m <0.01>: ", 0.01, out slope)) return;
            if (!GetPositiveDouble(editor, "\nManning n <0.013>: ", 0.013, out manningsN)) return;

            IReadOnlyList<FloodScenarioResult> results;
            CulvertRecommendation culvert;
            try
            {
                results = FloodAnalysisCalculator.CompareScenarios(areaHectares, preCoefficient, postCoefficient, intensities);
                culvert = FloodAnalysisCalculator.RecommendCircularCulvert(
                    results[results.Count - 1].PostDevelopmentFlowCubicMetresPerSecond,
                    slope,
                    manningsN);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_FLOODQUICK: " + exception.Message);
                return;
            }

            editor.WriteMessage(
                $"\nCE Flood Phase 1 preview: area={areaHectares:N3} ha; pre C={preCoefficient:N2}; post C={postCoefficient:N2}; " +
                $"100-year post flow={results[results.Count - 1].PostDevelopmentFlowCubicMetresPerSecond:N3} m3/s; " +
                $"preliminary culvert={(culvert.CapacityAvailable ? culvert.DiameterMillimetres.ToString("N0") + " mm" : "> 2400 mm / multi-cell study required")}.");

            PromptKeywordOptions confirm = new PromptKeywordOptions("\nPlace the flood-analysis table? [Yes/No] <No>: ") { AllowNone = true };
            confirm.Keywords.Add("Yes");
            confirm.Keywords.Add("No");
            PromptResult confirmation = editor.GetKeywords(confirm);
            if (confirmation.Status != PromptStatus.OK || !confirmation.StringResult.Equals("Yes", StringComparison.OrdinalIgnoreCase)) return;

            PromptPointResult insertion = editor.GetPoint("\nSpecify flood-analysis table insertion point: ");
            if (insertion.Status != PromptStatus.OK) return;

            using (document.LockDocument())
            using (Transaction transaction = document.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = (BlockTable)transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                BlockTableRecord modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                Table table = BuildTable(insertion.Value, areaHectares, preCoefficient, postCoefficient, results, culvert, slope, manningsN);
                modelSpace.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                transaction.Commit();
            }

            editor.WriteMessage("\nCE_FLOODQUICK complete. Verify rainfall inputs and hydraulic assumptions before design issue.");
        }

        private static Table BuildTable(Point3d position, double area, double preC, double postC,
            IReadOnlyList<FloodScenarioResult> results, CulvertRecommendation culvert, double slope, double manningsN)
        {
            var table = new Table { Position = position };
            table.SetSize(results.Count + 5, 6);
            table.SetRowHeight(4.0);
            table.SetColumnWidth(24.0);
            table.Cells[0, 0].TextString = "CE TOOLS — FLOOD ANALYSIS PHASE 1";
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 5));
            table.Cells[1, 0].TextString = $"Catchment: {area:N3} ha";
            table.Cells[1, 1].TextString = $"Pre C: {preC:N2}";
            table.Cells[1, 2].TextString = $"Post C: {postC:N2}";
            table.Cells[1, 3].TextString = $"Slope: {slope:N4}";
            table.Cells[1, 4].TextString = $"Manning n: {manningsN:N3}";
            table.Cells[1, 5].TextString = "Preliminary only";

            string[] headings = { "Return period", "Intensity mm/h", "Pre Q m3/s", "Post Q m3/s", "Increase m3/s", "Increase %" };
            for (int column = 0; column < headings.Length; column++) table.Cells[2, column].TextString = headings[column];

            for (int index = 0; index < results.Count; index++)
            {
                FloodScenarioResult result = results[index];
                int row = index + 3;
                table.Cells[row, 0].TextString = $"{result.ReturnPeriodYears} year";
                table.Cells[row, 1].TextString = result.IntensityMillimetresPerHour.ToString("N2");
                table.Cells[row, 2].TextString = result.PreDevelopmentFlowCubicMetresPerSecond.ToString("N3");
                table.Cells[row, 3].TextString = result.PostDevelopmentFlowCubicMetresPerSecond.ToString("N3");
                table.Cells[row, 4].TextString = result.IncreaseCubicMetresPerSecond.ToString("N3");
                table.Cells[row, 5].TextString = result.IncreasePercent.ToString("N1");
            }

            int culvertRow = results.Count + 3;
            table.Cells[culvertRow, 0].TextString = "Preliminary circular culvert";
            table.MergeCells(CellRange.Create(table, culvertRow, 0, culvertRow, 2));
            table.Cells[culvertRow, 3].TextString = culvert.CapacityAvailable ? $"{culvert.DiameterMillimetres:N0} mm" : "Larger / multi-cell study";
            table.Cells[culvertRow, 4].TextString = "Capacity";
            table.Cells[culvertRow, 5].TextString = $"{culvert.FullFlowCapacityCubicMetresPerSecond:N3} m3/s";

            int noteRow = results.Count + 4;
            table.Cells[noteRow, 0].TextString = "Screening result only. Confirm IDF data, time of concentration, inlet/outlet control, headwater, tailwater, blockage and authority requirements.";
            table.MergeCells(CellRange.Create(table, noteRow, 0, noteRow, 5));
            table.GenerateLayout();
            return table;
        }

        private static bool GetCoefficient(Editor editor, string message, double defaultValue, out double value)
        {
            if (!GetPositiveDouble(editor, message, defaultValue, out value)) return false;
            if (value > 1.0)
            {
                editor.WriteMessage("\nRunoff coefficient may not exceed 1.0.");
                return false;
            }
            return true;
        }

        private static bool GetPositiveDouble(Editor editor, string message, double defaultValue, out double value, bool useDefault = true)
        {
            var options = new PromptDoubleOptions(message)
            {
                AllowNegative = false,
                AllowZero = false,
                UseDefaultValue = useDefault,
                DefaultValue = defaultValue
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Value;
            return result.Status == PromptStatus.OK;
        }
    }
}
