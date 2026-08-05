using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.DrawingResetCommands))]

namespace CETools.Civil3D
{
    public sealed class DrawingResetCommands
    {
        [CommandMethod("CE_DRAWINGRESETALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ResetDrawing()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            Database database = document.Database;
            DrawingResetPreview preview = ReadPreview(database);
            string backupPath = BuildBackupPath(database);

            var window = new DrawingResetConfirmationWindow(preview, backupPath);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted)
            {
                document.Editor.WriteMessage("\nCE_DRAWINGRESETALL cancelled. Nothing was changed.");
                return;
            }

            try
            {
                CreateBackup(database, backupPath);
            }
            catch (System.Exception exception)
            {
                MessageBox.Show(
                    "CE Tools could not create the mandatory backup. The drawing was not changed.\n\n" +
                    exception.Message,
                    "CE Tools - Reset Drawing",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            int detached = DetachAllXrefs(database);
            int erased = 0;
            int skipped = 0;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTable blocks = (BlockTable)transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false);
                foreach (ObjectId blockId in blocks)
                {
                    BlockTableRecord block = transaction.GetObject(
                        blockId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (block == null || block.IsFromExternalReference ||
                        block.IsFromOverlayReference)
                        continue;

                    foreach (ObjectId entityId in block.Cast<ObjectId>().ToArray())
                    {
                        DBObject value;
                        try
                        {
                            value = transaction.GetObject(
                                entityId,
                                OpenMode.ForWrite,
                                false);
                        }
                        catch
                        {
                            skipped++;
                            continue;
                        }
                        if (value == null || value.IsErased)
                            continue;
                        try
                        {
                            value.Erase(true);
                            erased++;
                        }
                        catch
                        {
                            skipped++;
                        }
                    }
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_DRAWINGRESETALL complete. Objects erased={0}; XREFs detached={1}; skipped={2}. Backup: {3}",
                erased,
                detached,
                skipped,
                backupPath);
        }

        private static DrawingResetPreview ReadPreview(Database database)
        {
            var preview = new DrawingResetPreview();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTable blocks = (BlockTable)transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false);
                foreach (ObjectId blockId in blocks)
                {
                    BlockTableRecord block = transaction.GetObject(
                        blockId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (block == null) continue;
                    if (block.IsFromExternalReference || block.IsFromOverlayReference)
                        preview.Xrefs++;
                    int count = block.Cast<ObjectId>().Count();
                    preview.TotalObjects += count;
                    if (block.IsLayout)
                    {
                        Layout layout = transaction.GetObject(
                            block.LayoutId,
                            OpenMode.ForRead,
                            false) as Layout;
                        if (layout != null && layout.ModelType)
                            preview.ModelObjects += count;
                        else
                            preview.PaperObjects += count;
                    }
                    else
                    {
                        preview.BlockDefinitionObjects += count;
                    }
                }
            }
            return preview;
        }

        private static string BuildBackupPath(Database database)
        {
            string current = database == null ? string.Empty : database.Filename;
            string folder = !string.IsNullOrWhiteSpace(current)
                ? Path.GetDirectoryName(current)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string name = !string.IsNullOrWhiteSpace(current)
                ? Path.GetFileNameWithoutExtension(current)
                : "Unsaved-Drawing";
            return Path.Combine(
                folder,
                name + "-CE-RESET-BACKUP-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                ".dwg");
        }

        private static void CreateBackup(Database database, string path)
        {
            MethodInfo method = database.GetType().GetMethod(
                "Wblock",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            if (method == null)
                throw new InvalidOperationException(
                    "The installed AutoCAD API does not expose the full-drawing Wblock backup method.");
            using (Database backup = method.Invoke(database, null) as Database)
            {
                if (backup == null)
                    throw new InvalidOperationException(
                        "AutoCAD did not create the backup database.");
                backup.SaveAs(path, DwgVersion.Current);
            }
            if (!File.Exists(path))
                throw new IOException("The backup DWG was not created.");
        }

        private static int DetachAllXrefs(Database database)
        {
            var xrefs = new List<ObjectId>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTable blocks = (BlockTable)transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false);
                foreach (ObjectId id in blocks)
                {
                    BlockTableRecord block = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (block != null &&
                        (block.IsFromExternalReference || block.IsFromOverlayReference))
                        xrefs.Add(id);
                }
            }

            MethodInfo detach = database.GetType().GetMethod(
                "DetachXref",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(ObjectId) },
                null);
            if (detach == null) return 0;
            int detached = 0;
            foreach (ObjectId id in xrefs)
            {
                try
                {
                    detach.Invoke(database, new object[] { id });
                    detached++;
                }
                catch
                {
                    // Any remaining XREF references are erased with the other
                    // drawing entities in the following transaction.
                }
            }
            return detached;
        }
    }

    internal sealed class DrawingResetPreview
    {
        public int TotalObjects { get; set; }
        public int ModelObjects { get; set; }
        public int PaperObjects { get; set; }
        public int BlockDefinitionObjects { get; set; }
        public int Xrefs { get; set; }
    }

    internal sealed class DrawingResetConfirmationWindow : Window
    {
        private readonly TextBox _confirmation;

        public DrawingResetConfirmationWindow(
            DrawingResetPreview preview,
            string backupPath)
        {
            Accepted = false;
            Title = "CE Tools - Delete All Drawing and Design Objects";
            Width = 660;
            Height = 500;
            MinWidth = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var root = new StackPanel { Margin = new Thickness(22) };
            Content = root;
            root.Children.Add(new TextBlock
            {
                Text = "Reset this drawing",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            root.Children.Add(new TextBlock
            {
                Text = "This command deletes all model-space, paper-space, Civil design and nested block geometry and detaches every XREF. Drawing settings and installed Civil 3D styles remain available.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            });

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddRow(grid, 0, "All drawing/design objects", preview.TotalObjects.ToString(CultureInfo.CurrentCulture));
            AddRow(grid, 1, "Model-space objects", preview.ModelObjects.ToString(CultureInfo.CurrentCulture));
            AddRow(grid, 2, "Paper-space objects", preview.PaperObjects.ToString(CultureInfo.CurrentCulture));
            AddRow(grid, 3, "Nested block-definition objects", preview.BlockDefinitionObjects.ToString(CultureInfo.CurrentCulture));
            AddRow(grid, 4, "XREF definitions", preview.Xrefs.ToString(CultureInfo.CurrentCulture));
            root.Children.Add(grid);

            root.Children.Add(new TextBlock
            {
                Text = "Mandatory backup:",
                FontWeight = FontWeights.SemiBold
            });
            root.Children.Add(new TextBlock
            {
                Text = backupPath,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 14)
            });
            root.Children.Add(new TextBlock
            {
                Text = "Type DELETE ALL to continue:",
                FontWeight = FontWeights.SemiBold
            });
            _confirmation = new TextBox
            {
                Margin = new Thickness(0, 6, 0, 16),
                MinHeight = 30,
                FontSize = 15
            };
            root.Children.Add(_confirmation);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var reset = new Button
            {
                Content = "Create Backup and Delete All",
                MinWidth = 205,
                Padding = new Thickness(12, 7, 12, 7),
                Margin = new Thickness(0, 0, 8, 0)
            };
            reset.Click += delegate
            {
                if (!string.Equals(
                        _confirmation.Text.Trim(),
                        "DELETE ALL",
                        StringComparison.Ordinal))
                {
                    MessageBox.Show(
                        "Enter DELETE ALL exactly before continuing.",
                        "CE Tools - Reset Drawing",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                Accepted = true;
                DialogResult = true;
                Close();
            };
            buttons.Children.Add(reset);
            var cancel = new Button
            {
                Content = "Cancel",
                MinWidth = 90,
                Padding = new Thickness(12, 7, 12, 7),
                IsCancel = true
            };
            cancel.Click += delegate { Close(); };
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
        }

        public bool Accepted { get; private set; }

        private static void AddRow(
            Grid grid,
            int row,
            string label,
            string value)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var left = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 3, 10, 3)
            };
            Grid.SetRow(left, row);
            grid.Children.Add(left);
            var right = new TextBlock
            {
                Text = value,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 3, 0, 3)
            };
            Grid.SetRow(right, row);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
    }
}
