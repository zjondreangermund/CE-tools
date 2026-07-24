using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Small shared WPF report/review window used by CE Tools commands.
    /// It keeps command-line output available while also giving users a readable
    /// pop-up and, where appropriate, an option to place the same information as
    /// an AutoCAD table in the current drawing.
    /// </summary>
    internal static class PopupTablePresenter
    {
        public static void ShowReportAndOfferTable(
            Document document,
            string title,
            string note,
            IList<KeyValuePair<string, string>> rows,
            string tableTitle)
        {
            if (document == null)
            {
                return;
            }

            var window = new KeyValuePopupWindow(
                title,
                note,
                rows,
                "Close",
                allowAccept: false,
                allowTable: true);

            AcApplication.ShowModalWindow(window);
            if (window.Action == PopupWindowAction.PlaceTable)
            {
                PlaceKeyValueTable(document, tableTitle, rows);
            }
        }

        public static bool ShowReview(
            string title,
            string note,
            IList<KeyValuePair<string, string>> rows,
            string acceptText)
        {
            var window = new KeyValuePopupWindow(
                title,
                note,
                rows,
                acceptText,
                allowAccept: true,
                allowTable: false);

            AcApplication.ShowModalWindow(window);
            return window.Action == PopupWindowAction.Accept;
        }

        public static IList<KeyValuePair<string, string>> BuildRows(
            IEnumerable<string> fieldOrder,
            Func<string, string> valueProvider)
        {
            var rows = new List<KeyValuePair<string, string>>();
            if (fieldOrder == null || valueProvider == null)
            {
                return rows;
            }

            foreach (string field in fieldOrder)
            {
                string value = valueProvider(field);
                rows.Add(new KeyValuePair<string, string>(
                    field,
                    string.IsNullOrWhiteSpace(value) ? "<Not set>" : value));
            }

            return rows;
        }

        private static void PlaceKeyValueTable(
            Document document,
            string tableTitle,
            IList<KeyValuePair<string, string>> rows)
        {
            Editor editor = document.Editor;
            PromptPointResult pointResult = editor.GetPoint(
                "\nSelect insertion point for the CE Tools information table: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nCE Tools table placement cancelled.");
                return;
            }

            double textHeight = ResolveTextHeight();
            int dataCount = rows == null ? 0 : rows.Count;

            try
            {
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false);

                    var table = new Table
                    {
                        TableStyle = document.Database.Tablestyle,
                        Position = pointResult.Value
                    };

                    table.SetSize(dataCount + 2, 2);
                    table.SetRowHeight(textHeight * 2.4);
                    table.Columns[0].Width = textHeight * 20.0;
                    table.Columns[1].Width = textHeight * 45.0;

                    table.MergeCells(CellRange.Create(table, 0, 0, 0, 1));
                    table.Cells[0, 0].TextString = string.IsNullOrWhiteSpace(tableTitle)
                        ? "CE Tools Information"
                        : tableTitle;
                    table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
                    table.Cells[0, 0].TextHeight = textHeight * 1.15;

                    table.Cells[1, 0].TextString = "Field";
                    table.Cells[1, 1].TextString = "Value";
                    table.Cells[1, 0].Alignment = CellAlignment.MiddleCenter;
                    table.Cells[1, 1].Alignment = CellAlignment.MiddleCenter;
                    table.Cells[1, 0].TextHeight = textHeight;
                    table.Cells[1, 1].TextHeight = textHeight;

                    for (int index = 0; index < dataCount; index++)
                    {
                        int rowIndex = index + 2;
                        table.Cells[rowIndex, 0].TextString = rows[index].Key ?? string.Empty;
                        table.Cells[rowIndex, 1].TextString = rows[index].Value ?? string.Empty;
                        table.Cells[rowIndex, 0].Alignment = CellAlignment.MiddleLeft;
                        table.Cells[rowIndex, 1].Alignment = CellAlignment.MiddleLeft;
                        table.Cells[rowIndex, 0].TextHeight = textHeight;
                        table.Cells[rowIndex, 1].TextHeight = textHeight;
                    }

                    table.GenerateLayout();
                    currentSpace.AppendEntity(table);
                    transaction.AddNewlyCreatedDBObject(table, true);
                    transaction.Commit();
                }

                editor.WriteMessage("\nCE Tools information table created.");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE Tools table creation failed. No table was committed. {0}",
                    exception.Message);
            }
        }

        private static double ResolveTextHeight()
        {
            try
            {
                object value = AcApplication.GetSystemVariable("TEXTSIZE");
                double height = Convert.ToDouble(value);
                if (height > 0.0)
                {
                    return Math.Max(1.8, Math.Min(5.0, height));
                }
            }
            catch
            {
                // Use the CE Tools default below.
            }

            return 2.0;
        }

        private enum PopupWindowAction
        {
            Close,
            Accept,
            PlaceTable
        }

        private sealed class KeyValuePopupWindow : Window
        {
            public KeyValuePopupWindow(
                string title,
                string note,
                IList<KeyValuePair<string, string>> rows,
                string acceptText,
                bool allowAccept,
                bool allowTable)
            {
                Title = title ?? "CE Tools";
                Width = 720;
                Height = 520;
                MinWidth = 560;
                MinHeight = 360;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                ResizeMode = ResizeMode.CanResizeWithGrip;
                ShowInTaskbar = false;

                var root = new Grid
                {
                    Margin = new Thickness(14)
                };
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var heading = new TextBlock
                {
                    Text = title ?? "CE Tools",
                    FontSize = 20,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                Grid.SetRow(heading, 0);
                root.Children.Add(heading);

                var noteBlock = new TextBlock
                {
                    Text = note ?? string.Empty,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10)
                };
                Grid.SetRow(noteBlock, 1);
                root.Children.Add(noteBlock);

                var grid = new DataGrid
                {
                    IsReadOnly = true,
                    AutoGenerateColumns = false,
                    CanUserAddRows = false,
                    CanUserDeleteRows = false,
                    HeadersVisibility = DataGridHeadersVisibility.Column,
                    GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                    SelectionMode = DataGridSelectionMode.Single,
                    ItemsSource = BuildItems(rows)
                };
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = "Field",
                    Binding = new System.Windows.Data.Binding("Field"),
                    Width = new DataGridLength(0.35, DataGridLengthUnitType.Star)
                });
                grid.Columns.Add(new DataGridTextColumn
                {
                    Header = "Value",
                    Binding = new System.Windows.Data.Binding("Value"),
                    Width = new DataGridLength(0.65, DataGridLengthUnitType.Star)
                });
                Grid.SetRow(grid, 2);
                root.Children.Add(grid);

                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 12, 0, 0)
                };

                if (allowTable)
                {
                    var tableButton = CreateButton("Place Table", 105);
                    tableButton.Click += delegate
                    {
                        Action = PopupWindowAction.PlaceTable;
                        DialogResult = true;
                        Close();
                    };
                    buttons.Children.Add(tableButton);
                }

                if (allowAccept)
                {
                    var acceptButton = CreateButton(
                        string.IsNullOrWhiteSpace(acceptText) ? "Accept" : acceptText,
                        95);
                    acceptButton.IsDefault = true;
                    acceptButton.Click += delegate
                    {
                        Action = PopupWindowAction.Accept;
                        DialogResult = true;
                        Close();
                    };
                    buttons.Children.Add(acceptButton);
                }

                var closeButton = CreateButton(allowAccept ? "Cancel" : "Close", 90);
                closeButton.IsCancel = true;
                closeButton.Click += delegate
                {
                    Action = PopupWindowAction.Close;
                    DialogResult = false;
                    Close();
                };
                buttons.Children.Add(closeButton);

                Grid.SetRow(buttons, 3);
                root.Children.Add(buttons);
                Content = root;
            }

            public PopupWindowAction Action { get; private set; }

            private static Button CreateButton(string text, double width)
            {
                return new Button
                {
                    Content = text,
                    Width = width,
                    MinHeight = 30,
                    Margin = new Thickness(8, 0, 0, 0),
                    Padding = new Thickness(8, 3, 8, 3)
                };
            }

            private static IList<ReportItem> BuildItems(
                IList<KeyValuePair<string, string>> rows)
            {
                var items = new List<ReportItem>();
                if (rows == null)
                {
                    return items;
                }

                foreach (KeyValuePair<string, string> row in rows)
                {
                    items.Add(new ReportItem
                    {
                        Field = row.Key ?? string.Empty,
                        Value = row.Value ?? string.Empty
                    });
                }

                return items;
            }
        }

        private sealed class ReportItem
        {
            public string Field { get; set; }

            public string Value { get; set; }
        }
    }
}
