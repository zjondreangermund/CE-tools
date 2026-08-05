using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ProductionCommentCommands))]

namespace CETools.Civil3D
{
    public sealed class ProductionCommentCommands
    {
        [CommandMethod("CE_TOOLS", "CE_BOQCENTER", CommandFlags.Modal | CommandFlags.Redraw)]
        public void BoqCentre()
        {
            RunChoiceWindow(
                "CE Tools - Dynamic BOQ and Quantity Centre",
                "Build, refresh or export linked quantities. Matching rates are preserved by the existing CE_BOQREFRESH workflow.",
                new List<ProductionChoice>
                {
                    new ProductionChoice("Build a linked BOQ from selected design objects", "CE_BOQBUILD "),
                    new ProductionChoice("Refresh one linked BOQ from current design geometry", "CE_BOQREFRESH "),
                    new ProductionChoice("Review linked BOQ source and stale-handle information", "CE_BOQINFO "),
                    new ProductionChoice("Refresh and export one linked BOQ to Excel", "CE_BOQEXPORT "),
                    new ProductionChoice("Export Road BOQ to Excel", "CE_BOQROAD "),
                    new ProductionChoice("Export Platform BOQ to Excel", "CE_BOQPLATFORM "),
                    new ProductionChoice("Export Stormwater BOQ to Excel", "CE_BOQSTORM "),
                    new ProductionChoice("Export Sewer BOQ to Excel", "CE_BOQSEWER "),
                    new ProductionChoice("Export Water BOQ to Excel", "CE_BOQWATER "),
                    new ProductionChoice("Export Bulk-water BOQ to Excel", "CE_BOQBULKWATER "),
                    new ProductionChoice("Refresh all linked coordinates, BOQs, surfaces and corridors", "CE_REFRESHALL "),
                    new ProductionChoice("Show automatic refresh status", "CE_REFRESHSTATUS "),
                    new ProductionChoice("Total selected curve length", "CE_TLENGTH "),
                    new ProductionChoice("Total selected area", "CE_TAREA ")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_REPORTCENTER", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ReportCentre()
        {
            RunChoiceWindow(
                "CE Tools - Design Report Centre",
                "Generate current model reports, optional drawing tables and Excel output by discipline.",
                new List<ProductionChoice>
                {
                    new ProductionChoice("Full design model report", "CE_REPORTFULL "),
                    new ProductionChoice("Choose a discipline report", "CE_REPORTDISC "),
                    new ProductionChoice("Road design report", "CE_REPORTROAD "),
                    new ProductionChoice("Platform and grading report", "CE_REPORTPLATFORM "),
                    new ProductionChoice("Stormwater report", "CE_REPORTSTORM "),
                    new ProductionChoice("Sewer report", "CE_REPORTSEWER "),
                    new ProductionChoice("Water report", "CE_REPORTWATER "),
                    new ProductionChoice("Bulk-water report", "CE_REPORTBULKWATER "),
                    new ProductionChoice("Export design report to Excel", "CE_REPORTEXPORT "),
                    new ProductionChoice("Network summary popup and optional table", "CE_NETWORKREPORT2 "),
                    new ProductionChoice("Selected network pipe/structure data", "CE_NETWORKPARTREPORT2 "),
                    new ProductionChoice("Feature-line popup report", "CE_FLREPORT2 "),
                    new ProductionChoice("Profile popup report", "CE_PROFILEREPORT2 "),
                    new ProductionChoice("Surface popup report", "CE_SURFACEREPORT2 "),
                    new ProductionChoice("Refresh all linked outputs before reporting", "CE_REFRESHALL ")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PRODUCTIONCENTER", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProductionCentre()
        {
            RunChoiceWindow(
                "CE Tools - Plan Production and Project Books",
                "Create or refresh client/construction books, summaries, registers and PDFs from one production window.",
                new List<ProductionChoice>
                {
                    new ProductionChoice("Refresh all dynamic model data first", "CE_REFRESHALL "),
                    new ProductionChoice("Create or refresh project summary sheet", "CE_SUMMARYSHEET "),
                    new ProductionChoice("Refresh existing project summary", "CE_SUMMARYREFRESH "),
                    new ProductionChoice("Review project summary links", "CE_SUMMARYINFO "),
                    new ProductionChoice("Project closeout - create A4 and A3 client books", "CE_PROJECTCLOSEOUT "),
                    new ProductionChoice("Create A4, A3 or both client summary books", "CE_CLIENTBOOK "),
                    new ProductionChoice("Refresh all linked client-book pages", "CE_CLIENTBOOKREFRESH "),
                    new ProductionChoice("Review client-book link and revision information", "CE_CLIENTBOOKINFO "),
                    new ProductionChoice("Export client-book register to Excel", "CE_CLIENTBOOKINDEX "),
                    new ProductionChoice("Edit drawing titles and drawing register", "CE_DRAWINGREGISTEREDIT "),
                    new ProductionChoice("Create A4/A3 client and A1/A0 construction layouts", "CE_DRAWINGBOOK "),
                    new ProductionChoice("Export drawing-book layout register to Excel", "CE_BOOKINDEX "),
                    new ProductionChoice("Open AutoCAD Publish for batch PDF output", "CE_BATCHPUBLISH "),
                    new ProductionChoice("Show where CE books and exports are stored", "CE_OUTPUTLOCATION ")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PRINTCENTER", CommandFlags.Modal | CommandFlags.Redraw)]
        public void PrintCentre()
        {
            RunChoiceWindow(
                "CE Tools - Print and Publish Centre",
                "Prepare linked books first, then use AutoCAD's native plot or publish workflows for PDF or hard-copy output.",
                new List<ProductionChoice>
                {
                    new ProductionChoice("Edit drawing titles and drawing register", "CE_DRAWINGREGISTEREDIT "),
                    new ProductionChoice("Create/refresh A-series drawing-book layouts", "CE_DRAWINGBOOK "),
                    new ProductionChoice("Create/refresh A4/A3 client books", "CE_CLIENTBOOK "),
                    new ProductionChoice("Refresh client-book pages", "CE_CLIENTBOOKREFRESH "),
                    new ProductionChoice("Open AutoCAD Publish for batch PDF", "CE_BATCHPUBLISH "),
                    new ProductionChoice("Open AutoCAD Plot for current sheet", "_.PLOT "),
                    new ProductionChoice("Export drawing-book index", "CE_BOOKINDEX "),
                    new ProductionChoice("Export client-book index", "CE_CLIENTBOOKINDEX "),
                    new ProductionChoice("Show output locations", "CE_OUTPUTLOCATION ")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_BATCHPUBLISH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void BatchPublish()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            document.Editor.WriteMessage("\nCE_BATCHPUBLISH is opening AutoCAD Publish. Select the generated A1/A0 construction layouts or A4/A3 client-book layouts and choose a PDF publish setup.");
            document.SendStringToExecute("_.PUBLISH ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_OUTPUTLOCATION", CommandFlags.Modal | CommandFlags.Redraw)]
        public void OutputLocation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            string drawingPath = document.Database.Filename;
            string folder = string.IsNullOrWhiteSpace(drawingPath) ? "<Drawing has not been saved>" : Path.GetDirectoryName(drawingPath);
            int layouts = CountLayouts(document.Database);
            var rows = new List<IList<string>>
            {
                new List<string> { "Current DWG", string.IsNullOrWhiteSpace(drawingPath) ? "<Unsaved drawing>" : drawingPath },
                new List<string> { "Drawing folder", string.IsNullOrWhiteSpace(folder) ? "<Unavailable>" : folder },
                new List<string> { "A-series drawing books", "Stored as layouts inside the current DWG until plotted or published" },
                new List<string> { "A4/A3 client books", "Stored as linked layouts/pages inside the current DWG" },
                new List<string> { "Current layout count", layouts.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                new List<string> { "BOQ and report Excel files", "Saved to the location selected in the export dialog" },
                new List<string> { "Published PDFs", "Saved to the path selected in AutoCAD Publish/Plot" },
                new List<string> { "Recommended project output folder", string.IsNullOrWhiteSpace(folder) ? "Save the DWG first" : Path.Combine(folder, "CE Tools Outputs") }
            };
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Output Locations",
                "CE drawing and client books are linked DWG layouts. Excel and PDF paths are selected when exporting or publishing.",
                new List<string> { "Output", "Location / Behaviour" },
                rows,
                "CE TOOLS OUTPUT LOCATIONS");
        }

        private static int CountLayouts(Database database)
        {
            int count = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary layouts = transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead, false) as DBDictionary;
                if (layouts != null)
                {
                    foreach (DBDictionaryEntry entry in layouts)
                    {
                        Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead, false) as Layout;
                        if (layout != null && !layout.ModelType) count++;
                    }
                }
            }
            return count;
        }

        private static void RunChoiceWindow(string title, string subtitle, IList<ProductionChoice> choices)
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var window = new ProductionChoiceWindow(title, subtitle, choices);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted || window.Selected == null) return;
            document.SendStringToExecute(window.Selected.Command, true, false, true);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class ProductionChoice
    {
        public ProductionChoice(string label, string command)
        {
            Label = label;
            Command = command;
        }
        public string Label { get; }
        public string Command { get; }
        public override string ToString() { return Label; }
    }

    internal sealed class ProductionChoiceWindow : Window
    {
        private readonly ListBox _choices;
        public ProductionChoiceWindow(string title, string subtitle, IEnumerable<ProductionChoice> choices)
        {
            Title = title;
            Width = 720;
            Height = 580;
            MinWidth = 520;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            var cancel = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            cancel.Click += delegate { Close(); };
            buttons.Children.Add(cancel);
            var run = new Button { Content = "Run Selected", Width = 120, IsDefault = true };
            run.Click += delegate
            {
                Selected = _choices.SelectedItem as ProductionChoice;
                if (Selected == null) return;
                Accepted = true;
                Close();
            };
            buttons.Children.Add(run);

            var header = new TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            _choices = new ListBox { ItemsSource = choices };
            if (_choices.Items.Count > 0) _choices.SelectedIndex = 0;
            _choices.MouseDoubleClick += delegate { run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
            root.Children.Add(_choices);
        }

        public bool Accepted { get; private set; }
        public ProductionChoice Selected { get; private set; }
    }
}
