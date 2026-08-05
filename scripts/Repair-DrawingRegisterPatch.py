#!/usr/bin/env python3
from pathlib import Path
import re

path = Path(__file__).with_name("Apply-SewerProjectProductionPopupFix.py")
text = path.read_text(encoding="utf-8")
pattern = re.compile(
    r"production = replace_regex\(\n    production,\n    r'''            List<LayoutSnapshot> layouts = snapshot\.Layouts.*?    \"linked on-sheet drawing register\",\n    flags=re\.S\)\n",
    re.S,
)
replacement = r"""production = replace_regex(
    production,
    r'''        private static Table BuildBookRegister\(
            Database database,
            Point3d position,
            BookPackage package,
            ProjectSnapshot snapshot,
            ProductionDrawingRegisterData drawingRegister,
            double textHeight\)
        \{.*?
        \}

        private static void AddBookGenerated''',
    r'''        private static Table BuildBookRegister(
            Database database,
            Point3d position,
            BookPackage package,
            ProjectSnapshot snapshot,
            ProductionDrawingRegisterData drawingRegister,
            double textHeight)
        {
            List<ProductionDrawingRegisterRow> rows = drawingRegister.Rows
                .Take(package.PaperName == "A4" ? 10 : 24)
                .ToList();
            if (rows.Count == 0)
            {
                rows.Add(new ProductionDrawingRegisterRow
                {
                    DrawingNumber = "-",
                    Layout = package.LayoutName,
                    Title = "No drawings registered",
                    Purpose = package.Purpose,
                    Revision = drawingRegister.Header("Revision")
                });
            }

            var table = new Table();
            table.SetDatabaseDefaults(database);
            table.TableStyle = database.Tablestyle;
            table.Position = position;
            table.SetSize(rows.Count + 2, 5);
            table.SetRowHeight(textHeight * 2.0);
            double available = package.Width * 0.82;
            table.Columns[0].Width = available * 0.14;
            table.Columns[1].Width = available * 0.24;
            table.Columns[2].Width = available * 0.38;
            table.Columns[3].Width = available * 0.12;
            table.Columns[4].Width = available * 0.12;
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 4));
            table.Cells[0, 0].TextString = "DRAWING BOOK REGISTER";
            string[] headings =
            {
                "DRAWING NO.", "LAYOUT", "TITLE", "SCALE", "REV"
            };
            for (int column = 0; column < headings.Length; column++)
                table.Cells[1, column].TextString = headings[column];
            for (int index = 0; index < rows.Count; index++)
            {
                int rowIndex = index + 2;
                ProductionDrawingRegisterRow item = rows[index];
                table.Cells[rowIndex, 0].TextString = item.DrawingNumber;
                table.Cells[rowIndex, 1].TextString = item.Layout;
                table.Cells[rowIndex, 2].TextString = item.Title;
                table.Cells[rowIndex, 3].TextString = item.Scale;
                table.Cells[rowIndex, 4].TextString = item.Revision;
            }
            for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                for (int column = 0; column < table.Columns.Count; column++)
                    table.Cells[rowIndex, column].TextHeight = textHeight;
            return table;
        }

        private static void AddBookGenerated''',
    "linked on-sheet drawing register",
    flags=re.S)
"""
updated, count = pattern.subn(lambda match: replacement, text, count=1)
if count != 1:
    raise SystemExit(f"Could not narrow drawing-register patch; matches={count}")
path.write_text(updated, encoding="utf-8")
print("Narrowed drawing-register patch to BuildBookRegister only.")
