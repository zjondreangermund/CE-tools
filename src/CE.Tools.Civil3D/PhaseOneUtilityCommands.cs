using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.PhaseOneUtilityCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Completes the original Phase 1 utility surface with a single hub plus
    /// dedicated viewport, layer, Excel, label and survey-cleanup launchers.
    /// The launchers reuse existing commands; viewport and layer reports are
    /// implemented here without altering Civil 3D design objects.
    /// </summary>
    public sealed class PhaseOneUtilityCommands
    {
        [CommandMethod("CE_TOOLS", "CE_PHASE1", CommandFlags.Modal)]
        public void PhaseOneHub()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Phase 1 Utilities",
                "All original CE Tools utility families are available here. Drawing picks remain in the Civil 3D canvas.",
                new List<DisciplineWorkflowAction>
                {
                    Action("Feature Line Utilities", "CE_FLTOOLS", "Create, inspect and edit feature lines.", "01 Geometry"),
                    Action("Alignment Utilities", "CE_ALTOOLS", "Inspect alignments and station/offset data.", "01 Geometry"),
                    Action("Drawing Cleanup", "CE_DRAWCLEAN", "Run controlled OVERKILL, AUDIT and PURGE stages.", "02 Drawing"),
                    Action("Survey Cleanup", "CE_SURVEYCLEANUP", "Review survey corrections, surfaces and linked coordinates.", "02 Drawing"),
                    Action("Background Utilities", "CE_BACKGROUNDTOOLS", "Review, prepare, split and back up backgrounds/XREFs.", "02 Drawing"),
                    Action("Viewport Tools", "CE_VIEWPORTTOOLS", "Report and control paper-space viewport locking.", "02 Drawing"),
                    Action("Hatch Utilities", "CE_HATCHTOOLS", "Create and maintain controlled civil hatches.", "02 Drawing"),
                    Action("Layer Manager", "CE_LAYERTOOLS", "Review drawing layers or open the native Layer palette.", "02 Drawing"),
                    Action("Excel Tools", "CE_EXCELTOOLS", "Open linked schedule, BOQ and report exports.", "03 Data"),
                    Action("Coordinate Utilities", "CE_COORDINATE", "Create coordinate labels, crosses and tables.", "03 Data"),
                    Action("Label Utilities", "CE_LABELTOOLS", "Open shared dynamic annotation workflows.", "03 Data"),
                    Action("Parking Utilities", "CE_PKTOOLS", "Create, count and number parking bays.", "04 Site"),
                });
        }

        [CommandMethod("CE_TOOLS", "CE_VIEWPORTTOOLS", CommandFlags.Modal)]
        public void ViewportTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Viewport Tools",
                "Review all paper-space viewports and control display locking across layouts.",
                new List<DisciplineWorkflowAction>
                {
                    Action("Viewport report", "CE_VIEWPORTREPORT", "Report layout, viewport number, scale, size, layer and lock state.", "01 Review"),
                    Action("Lock all viewports", "CE_VIEWPORTLOCKALL", "Lock every floating paper-space viewport.", "02 Control"),
                    Action("Unlock all viewports", "CE_VIEWPORTUNLOCKALL", "Unlock every floating paper-space viewport.", "02 Control")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_VIEWPORTREPORT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ViewportReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<ViewportRecord> records = ReadViewports(document.Database);
            var rows = records.Select(item => (IList<string>)new List<string>
            {
                item.Layout,
                item.Number.ToString(CultureInfo.InvariantCulture),
                item.On ? "On" : "Off",
                item.Locked ? "Locked" : "Unlocked",
                item.CustomScale.ToString("0.######", CultureInfo.CurrentCulture),
                item.Width.ToString("0.###", CultureInfo.CurrentCulture),
                item.Height.ToString("0.###", CultureInfo.CurrentCulture),
                item.Layer
            }).ToList();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Paper-Space Viewport Report",
                records.Count == 0
                    ? "No floating paper-space viewports were found."
                    : records.Count.ToString(CultureInfo.CurrentCulture) + " floating paper-space viewport(s) found across all layouts.",
                new List<string> { "LAYOUT", "NO.", "DISPLAY", "LOCK", "CUSTOM SCALE", "WIDTH", "HEIGHT", "LAYER" },
                rows,
                "CE TOOLS VIEWPORT REPORT");
        }

        [CommandMethod("CE_TOOLS", "CE_VIEWPORTLOCKALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void LockAllViewports()
        {
            SetViewportLock(true);
        }

        [CommandMethod("CE_TOOLS", "CE_VIEWPORTUNLOCKALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void UnlockAllViewports()
        {
            SetViewportLock(false);
        }

        [CommandMethod("CE_TOOLS", "CE_LAYERTOOLS", CommandFlags.Modal)]
        public void LayerTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Layer Manager",
                "Audit the current drawing layer register or continue in AutoCAD's native Layer Properties Manager.",
                new List<DisciplineWorkflowAction>
                {
                    Action("Layer report", "CE_LAYERREPORT", "Review state, colour, linetype and plottability for every drawing layer.", "01 Review"),
                    Action("Open Layer Properties", "CE_LAYERPALETTE", "Open AutoCAD's native layer-management interface.", "02 Manage")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_LAYERREPORT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void LayerReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var rows = new List<IList<string>>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                LayerTable layers = (LayerTable)transaction.GetObject(
                    document.Database.LayerTableId,
                    OpenMode.ForRead,
                    false);
                foreach (ObjectId id in layers)
                {
                    LayerTableRecord layer = transaction.GetObject(id, OpenMode.ForRead, false) as LayerTableRecord;
                    if (layer == null) continue;
                    LinetypeTableRecord linetype = transaction.GetObject(
                        layer.LinetypeObjectId,
                        OpenMode.ForRead,
                        false) as LinetypeTableRecord;
                    rows.Add(new List<string>
                    {
                        layer.Name,
                        layer.IsOff ? "Off" : "On",
                        layer.IsFrozen ? "Frozen" : "Thawed",
                        layer.IsLocked ? "Locked" : "Unlocked",
                        layer.IsPlottable ? "Yes" : "No",
                        layer.Color == null
                            ? string.Empty
                            : layer.Color.ColorIndex.ToString(CultureInfo.InvariantCulture),
                        linetype == null ? string.Empty : linetype.Name
                    });
                }
            }
            rows = rows.OrderBy(row => row[0], StringComparer.OrdinalIgnoreCase).ToList();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Layer Register",
                rows.Count.ToString(CultureInfo.CurrentCulture) + " layer(s) found. The report is read-only; use Layer Properties to make changes.",
                new List<string> { "LAYER", "ON/OFF", "FREEZE", "LOCK", "PLOT", "ACI", "LINETYPE" },
                rows,
                "CE TOOLS LAYER REGISTER");
        }

        [CommandMethod("CE_TOOLS", "CE_LAYERPALETTE", CommandFlags.Modal)]
        public void OpenLayerPalette()
        {
            Document document = ActiveDocument();
            if (document != null)
                document.SendStringToExecute("_.LAYER ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_EXCELTOOLS", CommandFlags.Modal)]
        public void ExcelTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Excel Tools",
                "Open dependency-free Excel exports linked to current drawing data.",
                new List<DisciplineWorkflowAction>
                {
                    Action("Export linked BOQ", "CE_BOQEXPORT", "Refresh and export a linked bill of quantities.", "01 Quantities"),
                    Action("Export setting-out schedule", "CE_SETTINGOUTEXPORT", "Export linked COGO/AutoCAD point coordinates and levels.", "02 Survey"),
                    Action("Export survey changes", "CE_SURVEYCHANGEEXPORT", "Export original-versus-corrected surface comparison results.", "02 Survey"),
                    Action("Export project report", "CE_REPORTEXPORT", "Export a current model-derived engineering report.", "03 Reports"),
                    Action("Export drawing-book index", "CE_BOOKINDEX", "Export the standard layout and drawing-book register.", "03 Reports"),
                    Action("Export client-book index", "CE_CLIENTBOOKINDEX", "Export the linked client drawing-book register.", "03 Reports")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_LABELTOOLS", CommandFlags.Modal)]
        public void LabelTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Label Utilities",
                "Create drawing-linked annotations using shared paper heights, output types and overlap controls.",
                new List<DisciplineWorkflowAction>
                {
                    Action("Annotation settings", "CE_ANNOTSETTINGS", "Set 1.8/2.0/2.5/3.5/5.0 mm paper height, marker and output.", "01 Settings"),
                    Action("Coordinate label", "CE_COORDPICK2", "Create a linked XYZ coordinate annotation.", "02 Survey"),
                    Action("Coordinate cross", "CE_COORDCROSS2", "Create a linked coordinate cross and optional register entry.", "02 Survey"),
                    Action("Alignment label", "CE_ALLABELX", "Create a station/offset annotation.", "03 Civil Objects"),
                    Action("Profile label", "CE_PRLABELX", "Create a station/elevation/grade annotation.", "03 Civil Objects"),
                    Action("Surface label", "CE_SFLABELX", "Create a surface elevation annotation.", "03 Civil Objects"),
                    Action("Feature-line label", "CE_FLLABELX", "Create a feature-line elevation/grade annotation.", "03 Civil Objects"),
                    Action("Corridor label", "CE_CORLABELX", "Create a corridor annotation.", "03 Civil Objects"),
                    Action("Parking numbering", "CE_PKNUMBERX", "Create linked parking-bay numbering annotations.", "04 Parking"),
                    Action("Resolve overlaps", "CE_OVERLAPFIX", "Reposition supported annotations to reduce collisions.", "05 Cleanup")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SURVEYCLEANUP", CommandFlags.Modal)]
        public void SurveyCleanup()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Survey Cleanup",
                "Audit and compare survey data while preserving original source geometry and surfaces.",
                new List<DisciplineWorkflowAction>
                {
                    Action("Survey correction comparison", "CE_SURVEYCOMPARETOOLS", "Compare original and corrected survey surfaces.", "01 Compare"),
                    Action("Surface correction tools", "CE_SURFCTOOLS", "Audit and create reversible corrected/simplified surface copies.", "02 Correct"),
                    Action("Spike and hole repair", "CE_SURFSPIKEHOLEFIX", "Create a repaired copy while keeping the original surface.", "02 Correct"),
                    Action("Coordinate utilities", "CE_COORDINATE", "Review or recreate coordinate labels, crosses and tables.", "03 Coordinates"),
                    Action("Drawing cleanup", "CE_DRAWCLEAN", "Run controlled drawing cleanup after survey review.", "04 Drawing")
                });
        }

        private static DisciplineWorkflowAction Action(
            string title,
            string command,
            string description,
            string group)
        {
            return new DisciplineWorkflowAction(title, command, description, group);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private static List<ViewportRecord> ReadViewports(Database database)
        {
            var records = new List<ViewportRecord>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary layouts = (DBDictionary)transaction.GetObject(
                    database.LayoutDictionaryId,
                    OpenMode.ForRead,
                    false);
                foreach (DictionaryEntry entry in layouts)
                {
                    Layout layout = transaction.GetObject((ObjectId)entry.Value, OpenMode.ForRead, false) as Layout;
                    if (layout == null || layout.ModelType) continue;
                    BlockTableRecord space = transaction.GetObject(
                        layout.BlockTableRecordId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (space == null) continue;
                    foreach (ObjectId id in space)
                    {
                        Viewport viewport = transaction.GetObject(id, OpenMode.ForRead, false) as Viewport;
                        if (viewport == null || viewport.Number <= 1) continue;
                        records.Add(new ViewportRecord(
                            layout.LayoutName,
                            viewport.Number,
                            viewport.On,
                            viewport.Locked,
                            viewport.CustomScale,
                            viewport.Width,
                            viewport.Height,
                            viewport.Layer));
                    }
                }
            }
            return records
                .OrderBy(item => item.Layout, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Number)
                .ToList();
        }

        private static void SetViewportLock(bool locked)
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<ViewportRecord> existing = ReadViewports(document.Database);
            int affected = existing.Count(item => item.Locked != locked);
            if (affected == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE Tools: all {0} floating viewport(s) are already {1}.",
                    existing.Count,
                    locked ? "locked" : "unlocked");
                return;
            }
            if (!DisciplineWorkflowDialogs.Confirm(
                    "CE Tools - Viewport Tools",
                    (locked ? "Lock " : "Unlock ") +
                    affected.ToString(CultureInfo.CurrentCulture) +
                    " floating paper-space viewport(s) across all layouts?"))
                return;

            int changed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                DBDictionary layouts = (DBDictionary)transaction.GetObject(
                    document.Database.LayoutDictionaryId,
                    OpenMode.ForRead,
                    false);
                foreach (DictionaryEntry entry in layouts)
                {
                    Layout layout = transaction.GetObject((ObjectId)entry.Value, OpenMode.ForRead, false) as Layout;
                    if (layout == null || layout.ModelType) continue;
                    BlockTableRecord space = transaction.GetObject(
                        layout.BlockTableRecordId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (space == null) continue;
                    foreach (ObjectId id in space)
                    {
                        Viewport viewport = transaction.GetObject(id, OpenMode.ForRead, false) as Viewport;
                        if (viewport == null || viewport.Number <= 1 || viewport.Locked == locked) continue;
                        viewport.UpgradeOpen();
                        viewport.Locked = locked;
                        changed++;
                    }
                }
                transaction.Commit();
            }
            document.Editor.WriteMessage(
                "\nCE Tools: {0} floating viewport(s) {1}.",
                changed,
                locked ? "locked" : "unlocked");
        }

        private sealed class ViewportRecord
        {
            public ViewportRecord(
                string layout,
                int number,
                bool on,
                bool locked,
                double customScale,
                double width,
                double height,
                string layer)
            {
                Layout = layout ?? string.Empty;
                Number = number;
                On = on;
                Locked = locked;
                CustomScale = customScale;
                Width = width;
                Height = height;
                Layer = layer ?? string.Empty;
            }

            public string Layout { get; private set; }
            public int Number { get; private set; }
            public bool On { get; private set; }
            public bool Locked { get; private set; }
            public double CustomScale { get; private set; }
            public double Width { get; private set; }
            public double Height { get; private set; }
            public string Layer { get; private set; }
        }
    }
}
