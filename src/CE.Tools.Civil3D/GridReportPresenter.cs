using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Presents multi-column CE Tools reports in a readable WPF grid and can place
    /// the same report as an AutoCAD table in the current drawing.
    /// </summary>
    internal static class GridReportPresenter
    {
        public static void ShowReportAndOfferTable(
            Document document,
            string title,
            string note,
            IList<string> columns,
            IList<IList<string>> rows,
            string tableTitle)
        {
            if (document == null)
            {
                return;
            }

            var window = new GridReportWindow(title, note, columns, rows);
            AcApplication.ShowModalWindow(window);
            if (window.PlaceTableRequested)
            {
                PlaceTable(document, tableTitle, columns, rows);
            }
        }

        private static void PlaceTable(
            Document document,
            string tableTitle,
            IList<string> columns,
            IList<IList<string>> rows)
        {
            Editor editor = document.Editor;
            if (columns == null || columns.Count == 0)
            {
                editor.WriteMessage("\nCE Tools table creation cancelled. The report has no columns.");
                return;
            }

            PromptPointResult pointResult = editor.GetPoint(
                "\nSelect insertion point for the CE Tools report table: ");
            if (pointResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nCE Tools report table placement cancelled.");
                return;
            }

            int dataCount = rows == null ? 0 : rows.Count;
            double textHeight = ResolveTextHeight(document.Database);

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

                    table.SetSize(dataCount + 2, columns.Count);
                    table.SetRowHeight(textHeight * 2.4);

                    for (int columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                    {
                        int maximumLength = columns[columnIndex] == null
                            ? 0
                            : columns[columnIndex].Length;

                        for (int rowIndex = 0; rowIndex < dataCount; rowIndex++)
                        {
                            string value = GetValue(rows[rowIndex], columnIndex);
                            maximumLength = Math.Max(maximumLength, value.Length);
                        }

                        double width = textHeight * Math.Max(
                            10.0,
                            Math.Min(32.0, (maximumLength + 4.0) * 0.82));
                        table.Columns[columnIndex].Width = width;
                    }

                    table.MergeCells(CellRange.Create(
                        table,
                        0,
                        0,
                        0,
                        columns.Count - 1));
                    table.Cells[0, 0].TextString = string.IsNullOrWhiteSpace(tableTitle)
                        ? "CE Tools Report"
                        : tableTitle;
                    table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
                    table.Cells[0, 0].TextHeight = textHeight * 1.15;

                    for (int columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                    {
                        table.Cells[1, columnIndex].TextString = columns[columnIndex] ?? string.Empty;
                        table.Cells[1, columnIndex].Alignment = CellAlignment.MiddleCenter;
                        table.Cells[1, columnIndex].TextHeight = textHeight;
                    }

                    for (int dataIndex = 0; dataIndex < dataCount; dataIndex++)
                    {
                        int tableRow = dataIndex + 2;
                        for (int columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                        {
                            table.Cells[tableRow, columnIndex].TextString =
                                GetValue(rows[dataIndex], columnIndex);
                            table.Cells[tableRow, columnIndex].Alignment =
                                CellAlignment.MiddleLeft;
                            table.Cells[tableRow, columnIndex].TextHeight = textHeight;
                        }
                    }

                    table.GenerateLayout();
                    currentSpace.AppendEntity(table);
                    transaction.AddNewlyCreatedDBObject(table, true);
                    transaction.Commit();
                }

                editor.WriteMessage("\nCE Tools report table created.");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE Tools report table creation failed. No table was committed. {0}",
                    exception.Message);
            }
        }

        private static string GetValue(IList<string> row, int columnIndex)
        {
            if (row == null || columnIndex < 0 || columnIndex >= row.Count)
            {
                return string.Empty;
            }

            return row[columnIndex] ?? string.Empty;
        }

        private static double ResolveTextHeight(Database database)
        {
            double height = database == null ? 0.0 : database.Textsize;
            if (height > 0.0 && !double.IsNaN(height) && !double.IsInfinity(height))
            {
                return Math.Max(1.8, Math.Min(5.0, height));
            }

            return 2.0;
        }

        private sealed class GridReportWindow : Window
        {
            public GridReportWindow(
                string title,
                string note,
                IList<string> columns,
                IList<IList<string>> rows)
            {
                Title = title ?? "CE Tools Report";
                Width = 1080;
                Height = 620;
                MinWidth = 720;
                MinHeight = 420;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                ResizeMode = ResizeMode.CanResizeWithGrip;
                ShowInTaskbar = false;

                var root = new Grid
                {
                    Margin = new Thickness(14)
                };
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition
                {
                    Height = new GridLength(1.0, GridUnitType.Star)
                });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var heading = new TextBlock
                {
                    Text = title ?? "CE Tools Report",
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

                var dataGrid = new DataGrid
                {
                    IsReadOnly = true,
                    AutoGenerateColumns = false,
                    CanUserAddRows = false,
                    CanUserDeleteRows = false,
                    CanUserReorderColumns = true,
                    HeadersVisibility = DataGridHeadersVisibility.Column,
                    GridLinesVisibility = DataGridGridLinesVisibility.All,
                    SelectionMode = DataGridSelectionMode.Single,
                    FrozenColumnCount = columns == null || columns.Count == 0 ? 0 : 1,
                    ItemsSource = BuildItems(rows)
                };

                if (columns != null)
                {
                    for (int index = 0; index < columns.Count; index++)
                    {
                        dataGrid.Columns.Add(new DataGridTextColumn
                        {
                            Header = columns[index] ?? string.Empty,
                            Binding = new Binding("Values[" + index + "]"),
                            Width = DataGridLength.SizeToCells,
                            MinWidth = 90
                        });
                    }
                }

                Grid.SetRow(dataGrid, 2);
                root.Children.Add(dataGrid);

                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 12, 0, 0)
                };

                var tableButton = CreateButton("Place Table", 110);
                tableButton.Click += delegate
                {
                    PlaceTableRequested = true;
                    DialogResult = true;
                    Close();
                };
                buttons.Children.Add(tableButton);

                var closeButton = CreateButton("Close", 90);
                closeButton.IsCancel = true;
                closeButton.Click += delegate
                {
                    PlaceTableRequested = false;
                    DialogResult = false;
                    Close();
                };
                buttons.Children.Add(closeButton);

                Grid.SetRow(buttons, 3);
                root.Children.Add(buttons);
                Content = root;
            }

            public bool PlaceTableRequested { get; private set; }

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

            private static IList<GridReportRow> BuildItems(IList<IList<string>> rows)
            {
                var items = new List<GridReportRow>();
                if (rows == null)
                {
                    return items;
                }

                foreach (IList<string> row in rows)
                {
                    items.Add(new GridReportRow(row));
                }

                return items;
            }
        }

        private sealed class GridReportRow
        {
            public GridReportRow(IList<string> values)
            {
                Values = values ?? new List<string>();
            }

            public IList<string> Values { get; }
        }
    }
}
