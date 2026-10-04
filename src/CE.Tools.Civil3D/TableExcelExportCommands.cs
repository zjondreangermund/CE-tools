using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using WinForms = System.Windows.Forms;

[assembly: CommandClass(typeof(CETools.Civil3D.TableExcelExportCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Exports one or more selected AutoCAD/CE tables directly to a real .xlsx
    /// workbook. The command is intentionally UsePickSet-friendly so it can be
    /// launched from the CE Tools right-click menu while a table is selected.
    /// </summary>
    public sealed class TableExcelExportCommands
    {
        [CommandMethod(
            "CE_TOOLS",
            "CE_TABLEEXPORTEXCEL",
            CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void ExportSelectedTables()
        {
            Document document =
                AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            List<ObjectId> tableIds =
                ReadImpliedTableIds(
                    document.Database,
                    editor);

            if (tableIds.Count == 0)
            {
                PromptEntityOptions options =
                    new PromptEntityOptions(
                        "\nSelect a table to export to Excel: ");
                options.SetRejectMessage(
                    "\nSelect an AutoCAD/CE table.");
                options.AddAllowedClass(
                    typeof(Table),
                    true);

                PromptEntityResult selected =
                    editor.GetEntity(options);
                if (selected.Status != PromptStatus.OK)
                    return;

                tableIds.Add(selected.ObjectId);
            }

            List<TableSheet> sheets =
                ReadSheets(
                    document.Database,
                    tableIds);
            if (sheets.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_TABLEEXPORTEXCEL: no readable tables were selected.");
                return;
            }

            string drawingFolder =
                ResolveDrawingFolder(
                    document.Database);
            string defaultName =
                BuildDefaultFileName(
                    document.Database,
                    sheets[0].Name);

            using (var dialog =
                new WinForms.SaveFileDialog())
            {
                dialog.Title =
                    "Export CE Tools Table(s) to Excel";
                dialog.Filter =
                    "Excel Workbook (*.xlsx)|*.xlsx";
                dialog.DefaultExt = "xlsx";
                dialog.AddExtension = true;
                dialog.OverwritePrompt = true;
                dialog.FileName = defaultName;
                if (!string.IsNullOrWhiteSpace(
                        drawingFolder) &&
                    Directory.Exists(
                        drawingFolder))
                    dialog.InitialDirectory =
                        drawingFolder;

                if (dialog.ShowDialog() !=
                    WinForms.DialogResult.OK)
                    return;

                try
                {
                    WriteWorkbook(
                        dialog.FileName,
                        sheets);
                    editor.WriteMessage(
                        "\nCE_TABLEEXPORTEXCEL complete. Tables={0}; workbook={1}",
                        sheets.Count,
                        dialog.FileName);

                    try
                    {
                        Process.Start(
                            new ProcessStartInfo
                            {
                                FileName =
                                    dialog.FileName,
                                UseShellExecute = true
                            });
                    }
                    catch
                    {
                        // Export is complete even when Windows cannot launch
                        // the workbook automatically.
                    }
                }
                catch (System.Exception exception)
                {
                    editor.WriteMessage(
                        "\nCE_TABLEEXPORTEXCEL failed. No workbook was written. {0}",
                        exception.Message);
                }
            }
        }

        private static List<ObjectId> ReadImpliedTableIds(
            Database database,
            Editor editor)
        {
            var result =
                new List<ObjectId>();
            if (database == null ||
                editor == null)
                return result;

            PromptSelectionResult implied =
                editor.SelectImplied();
            if (implied.Status != PromptStatus.OK ||
                implied.Value == null)
                return result;

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in
                    implied.Value.GetObjectIds()
                        .Distinct())
                {
                    try
                    {
                        Table table =
                            transaction.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as Table;
                        if (table != null)
                            result.Add(id);
                    }
                    catch { }
                }
            }

            return result;
        }

        private static List<TableSheet> ReadSheets(
            Database database,
            IEnumerable<ObjectId> ids)
        {
            var result =
                new List<TableSheet>();
            if (database == null ||
                ids == null)
                return result;

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                int sequence = 1;
                var usedSheetNames =
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId id in
                    ids.Distinct())
                {
                    Table table = null;
                    try
                    {
                        table =
                            transaction.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as Table;
                    }
                    catch { }

                    if (table == null ||
                        table.Rows.Count <= 0 ||
                        table.Columns.Count <= 0)
                        continue;

                    var values =
                        new List<IList<string>>();
                    for (int row = 0;
                         row < table.Rows.Count;
                         row++)
                    {
                        var cells =
                            new List<string>();
                        for (int column = 0;
                             column < table.Columns.Count;
                             column++)
                        {
                            string value =
                                string.Empty;
                            try
                            {
                                value =
                                    table.Cells[
                                        row,
                                        column]
                                        .TextString ??
                                    string.Empty;
                            }
                            catch
                            {
                                try
                                {
                                    object raw =
                                        table.Cells[
                                            row,
                                            column]
                                            .Value;
                                    value =
                                        Convert.ToString(
                                            raw,
                                            CultureInfo.CurrentCulture) ??
                                        string.Empty;
                                }
                                catch { }
                            }
                            cells.Add(
                                NormalizeCellText(
                                    value));
                        }
                        values.Add(cells);
                    }

                    string title =
                        values.Count > 0 &&
                        values[0].Count > 0
                            ? values[0][0]
                            : string.Empty;
                    string name =
                        UniqueSheetName(
                            BuildSheetName(
                                title,
                                sequence++),
                            usedSheetNames);
                    result.Add(
                        new TableSheet(
                            name,
                            values));
                }
            }

            return result;
        }

        private static string NormalizeCellText(
            string value)
        {
            return (value ?? string.Empty)
                .Replace("\\P", Environment.NewLine)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Trim();
        }

        private static void WriteWorkbook(
            string fileName,
            IList<TableSheet> sheets)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                throw new ArgumentException(
                    "The Excel file name is empty.");
            if (sheets == null ||
                sheets.Count == 0)
                throw new InvalidOperationException(
                    "There are no table sheets to export.");

            string folder =
                Path.GetDirectoryName(fileName);
            if (!string.IsNullOrWhiteSpace(folder))
                Directory.CreateDirectory(folder);

            if (File.Exists(fileName))
                File.Delete(fileName);

            using (FileStream stream =
                new FileStream(
                    fileName,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None))
            using (ZipArchive archive =
                new ZipArchive(
                    stream,
                    ZipArchiveMode.Create,
                    false))
            {
                WriteEntry(
                    archive,
                    "[Content_Types].xml",
                    ContentTypesXml(
                        sheets.Count));
                WriteEntry(
                    archive,
                    "_rels/.rels",
                    RootRelationshipsXml());
                WriteEntry(
                    archive,
                    "xl/workbook.xml",
                    WorkbookXml(sheets));
                WriteEntry(
                    archive,
                    "xl/_rels/workbook.xml.rels",
                    WorkbookRelationshipsXml(
                        sheets.Count));

                for (int index = 0;
                     index < sheets.Count;
                     index++)
                {
                    WriteEntry(
                        archive,
                        "xl/worksheets/sheet" +
                        (index + 1)
                            .ToString(
                                CultureInfo.InvariantCulture) +
                        ".xml",
                        WorksheetXml(
                            sheets[index]));
                }
            }
        }

        private static void WriteEntry(
            ZipArchive archive,
            string name,
            string text)
        {
            ZipArchiveEntry entry =
                archive.CreateEntry(
                    name,
                    CompressionLevel.Optimal);
            using (Stream stream =
                entry.Open())
            using (var writer =
                new StreamWriter(
                    stream,
                    new UTF8Encoding(false)))
            {
                writer.Write(text ?? string.Empty);
            }
        }

        private static string ContentTypesXml(
            int sheetCount)
        {
            var builder =
                new StringBuilder();
            builder.Append(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            builder.Append(
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            builder.Append(
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            builder.Append(
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            builder.Append(
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            for (int index = 1;
                 index <= sheetCount;
                 index++)
            {
                builder.Append(
                    "<Override PartName=\"/xl/worksheets/sheet");
                builder.Append(
                    index.ToString(
                        CultureInfo.InvariantCulture));
                builder.Append(
                    ".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            }
            builder.Append("</Types>");
            return builder.ToString();
        }

        private static string RootRelationshipsXml()
        {
            return
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>";
        }

        private static string WorkbookXml(
            IList<TableSheet> sheets)
        {
            var builder =
                new StringBuilder();
            builder.Append(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            builder.Append(
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");

            for (int index = 0;
                 index < sheets.Count;
                 index++)
            {
                builder.Append("<sheet name=\"");
                builder.Append(
                    XmlEscape(
                        sheets[index].Name));
                builder.Append("\" sheetId=\"");
                builder.Append(
                    (index + 1)
                        .ToString(
                            CultureInfo.InvariantCulture));
                builder.Append("\" r:id=\"rId");
                builder.Append(
                    (index + 1)
                        .ToString(
                            CultureInfo.InvariantCulture));
                builder.Append("\"/>");
            }

            builder.Append(
                "</sheets></workbook>");
            return builder.ToString();
        }

        private static string WorkbookRelationshipsXml(
            int sheetCount)
        {
            var builder =
                new StringBuilder();
            builder.Append(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            builder.Append(
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int index = 1;
                 index <= sheetCount;
                 index++)
            {
                builder.Append(
                    "<Relationship Id=\"rId");
                builder.Append(
                    index.ToString(
                        CultureInfo.InvariantCulture));
                builder.Append(
                    "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet");
                builder.Append(
                    index.ToString(
                        CultureInfo.InvariantCulture));
                builder.Append(".xml\"/>");
            }
            builder.Append("</Relationships>");
            return builder.ToString();
        }

        private static string WorksheetXml(
            TableSheet sheet)
        {
            var builder =
                new StringBuilder();
            builder.Append(
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            builder.Append(
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

            int columnCount =
                sheet.Rows.Count == 0
                    ? 0
                    : sheet.Rows.Max(row =>
                        row == null ? 0 : row.Count);
            if (columnCount > 0)
            {
                builder.Append("<cols>");
                for (int column = 0;
                     column < columnCount;
                     column++)
                {
                    int maximum =
                        8;
                    foreach (IList<string> row in
                        sheet.Rows)
                    {
                        if (row == null ||
                            column >= row.Count)
                            continue;
                        maximum =
                            Math.Max(
                                maximum,
                                (row[column] ??
                                 string.Empty)
                                    .Replace(
                                        "\n",
                                        " ")
                                    .Length);
                    }
                    double width =
                        Math.Max(
                            8.0,
                            Math.Min(
                                60.0,
                                maximum + 2.0));
                    builder.Append(
                        "<col min=\"");
                    builder.Append(
                        (column + 1)
                            .ToString(
                                CultureInfo.InvariantCulture));
                    builder.Append(
                        "\" max=\"");
                    builder.Append(
                        (column + 1)
                            .ToString(
                                CultureInfo.InvariantCulture));
                    builder.Append(
                        "\" width=\"");
                    builder.Append(
                        width.ToString(
                            "0.##",
                            CultureInfo.InvariantCulture));
                    builder.Append(
                        "\" customWidth=\"1\"/>");
                }
                builder.Append("</cols>");
            }

            builder.Append("<sheetData>");
            for (int rowIndex = 0;
                 rowIndex < sheet.Rows.Count;
                 rowIndex++)
            {
                builder.Append("<row r=\"");
                builder.Append(
                    (rowIndex + 1)
                        .ToString(
                            CultureInfo.InvariantCulture));
                builder.Append("\">");

                IList<string> row =
                    sheet.Rows[rowIndex];
                if (row != null)
                {
                    for (int column = 0;
                         column < row.Count;
                         column++)
                    {
                        string reference =
                            ExcelColumnName(
                                column + 1) +
                            (rowIndex + 1)
                                .ToString(
                                    CultureInfo.InvariantCulture);
                        string value =
                            row[column] ??
                            string.Empty;
                        builder.Append("<c r=\"");
                        builder.Append(reference);
                        builder.Append(
                            "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
                        builder.Append(
                            XmlEscape(value));
                        builder.Append(
                            "</t></is></c>");
                    }
                }

                builder.Append("</row>");
            }
            builder.Append(
                "</sheetData></worksheet>");
            return builder.ToString();
        }

        private static string ExcelColumnName(
            int index)
        {
            var builder =
                new StringBuilder();
            int value =
                Math.Max(
                    1,
                    index);
            while (value > 0)
            {
                value--;
                builder.Insert(
                    0,
                    (char)('A' +
                           (value % 26)));
                value /= 26;
            }
            return builder.ToString();
        }

        private static string XmlEscape(
            string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private static string BuildSheetName(
            string value,
            int sequence)
        {
            string name =
                string.IsNullOrWhiteSpace(value)
                    ? "Table " +
                      sequence.ToString(
                          CultureInfo.InvariantCulture)
                    : value.Trim();
            foreach (char invalid in
                new[]
                {
                    ':',
                    '\\',
                    '/',
                    '?',
                    '*',
                    '[',
                    ']'
                })
                name =
                    name.Replace(
                        invalid,
                        '-');
            if (name.Length > 31)
                name =
                    name.Substring(
                        0,
                        31);
            if (string.IsNullOrWhiteSpace(name))
                name =
                    "Table " +
                    sequence.ToString(
                        CultureInfo.InvariantCulture);
            return name;
        }

        private static string UniqueSheetName(
            string requested,
            ISet<string> used)
        {
            string root =
                string.IsNullOrWhiteSpace(requested)
                    ? "Table"
                    : requested;
            if (used == null)
                return root;

            string candidate = root;
            int suffix = 2;
            while (used.Contains(candidate))
            {
                string suffixText =
                    " (" +
                    suffix.ToString(
                        CultureInfo.InvariantCulture) +
                    ")";
                int maxRoot =
                    Math.Max(
                        1,
                        31 - suffixText.Length);
                candidate =
                    (root.Length > maxRoot
                        ? root.Substring(
                            0,
                            maxRoot)
                        : root) +
                    suffixText;
                suffix++;
            }
            used.Add(candidate);
            return candidate;
        }

        private static string ResolveDrawingFolder(
            Database database)
        {
            if (database == null ||
                string.IsNullOrWhiteSpace(
                    database.Filename))
                return string.Empty;
            try
            {
                return Path.GetDirectoryName(
                           database.Filename) ??
                       string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string BuildDefaultFileName(
            Database database,
            string sheetName)
        {
            string drawing =
                database == null ||
                string.IsNullOrWhiteSpace(
                    database.Filename)
                    ? "CE-Tools"
                    : Path.GetFileNameWithoutExtension(
                        database.Filename);
            string title =
                string.IsNullOrWhiteSpace(sheetName)
                    ? "Table"
                    : sheetName;
            string combined =
                drawing +
                " - " +
                title +
                ".xlsx";
            foreach (char invalid in
                Path.GetInvalidFileNameChars())
                combined =
                    combined.Replace(
                        invalid,
                        '-');
            return combined;
        }

        private sealed class TableSheet
        {
            internal TableSheet(
                string name,
                IList<IList<string>> rows)
            {
                Name =
                    string.IsNullOrWhiteSpace(name)
                        ? "Table"
                        : name;
                Rows =
                    rows ??
                    new List<IList<string>>();
            }

            internal string Name { get; private set; }
            internal IList<IList<string>> Rows { get; private set; }
        }
    }
}
