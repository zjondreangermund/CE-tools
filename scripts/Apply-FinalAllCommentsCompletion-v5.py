from pathlib import Path

source = Path(__file__).resolve().parent / "Apply-FinalAllCommentsCompletion.py"
text = source.read_text(encoding="utf-8")

text = text.replace("IEnumerable<VertexSettingSource> sources", "IList<VertexSettingSource> sources")

old_block = '''# Add DesignSurfaceHandle property next to NgSurfaceHandle.
text = replace_once(text,
''' + "'''" + '''        public string NgSurfaceHandle { get; set; }
        public IList<string> SourceHandles''' + "'''" + ''',
''' + "'''" + '''        public string NgSurfaceHandle { get; set; }
        public string DesignSurfaceHandle { get; set; }
        public IList<string> SourceHandles''' + "'''" + ''',
"design surface link property")

# Serialize/deserialize design handle using the same key/value Xrecord contract.
old = ''' + "'''" + '''                "NGSURFACE=" + (link.NgSurfaceHandle ?? string.Empty),''' + "'''" + '''
new = ''' + "'''" + '''                "NGSURFACE=" + (link.NgSurfaceHandle ?? string.Empty),
                "DESIGNSURFACE=" + (link.DesignSurfaceHandle ?? string.Empty),''' + "'''" + '''
text = replace_once(text, old, new, "write design surface link")
old = ''' + "'''" + '''                NgSurfaceHandle = ReadLinkValue(values, "NGSURFACE"),''' + "'''" + '''
new = ''' + "'''" + '''                NgSurfaceHandle = ReadLinkValue(values, "NGSURFACE"),
                DesignSurfaceHandle = ReadLinkValue(values, "DESIGNSURFACE"),''' + "'''" + '''
text = replace_once(text, old, new, "read design surface link")'''
new_block = '''# Add DesignSurfaceHandle property next to NgSurfaceHandle.
text = replace_once(text,
''' + "'''" + '''            public string NgSurfaceHandle { get; set; }
            public string CoordinateOrder''' + "'''" + ''',
''' + "'''" + '''            public string NgSurfaceHandle { get; set; }
            public string DesignSurfaceHandle { get; set; }
            public string CoordinateOrder''' + "'''" + ''',
"design surface link property")
text = replace_once(text,
''' + "'''" + '''            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "NGHANDLE=" + (link.NgSurfaceHandle ?? string.Empty)));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "ORDER="''' + "'''" + ''',
''' + "'''" + '''            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "NGHANDLE=" + (link.NgSurfaceHandle ?? string.Empty)));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "DESIGNHANDLE=" + (link.DesignSurfaceHandle ?? string.Empty)));
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "ORDER="''' + "'''" + ''',
"write design surface link")
text = replace_once(text,
''' + "'''" + '''                NgSurfaceHandle = string.Empty,
                CoordinateOrder = "X then Y",''' + "'''" + ''',
''' + "'''" + '''                NgSurfaceHandle = string.Empty,
                DesignSurfaceHandle = string.Empty,
                CoordinateOrder = "X then Y",''' + "'''" + ''',
"initialize design surface link")
text = replace_once(text,
''' + "'''" + '''                else if (value.StartsWith("NGHANDLE=", StringComparison.OrdinalIgnoreCase))
                    link.NgSurfaceHandle = value.Substring(9);
                else if (value.StartsWith("ORDER=",''' + "'''" + ''',
''' + "'''" + '''                else if (value.StartsWith("NGHANDLE=", StringComparison.OrdinalIgnoreCase))
                    link.NgSurfaceHandle = value.Substring(9);
                else if (value.StartsWith("DESIGNHANDLE=", StringComparison.OrdinalIgnoreCase))
                    link.DesignSurfaceHandle = value.Substring(13);
                else if (value.StartsWith("ORDER=",''' + "'''" + ''',
"read design surface link")'''
if old_block not in text:
    raise RuntimeError("Could not align live vertex link patch block.")
text = text.replace(old_block, new_block, 1)

# Replace the obsolete table-array patcher with the exact current cell-based table structure.
start = text.index("# Remove Z column from linked setting-out table while keeping NG/Design/Difference.")
end = text.index("write(name, text)\n\n# 5. Sewer excavation", start)
current_table_patch = '''# Remove redundant Z from the current cell-based linked setting-out table.
text = replace_once(text, ''' + "'''" + '''            table.SetSize(records.Count + 2, 12);''' + "'''" + ''', ''' + "'''" + '''            table.SetSize(records.Count + 2, 11);''' + "'''" + ''', "vertex table column count")
text = replace_once(text, ''' + "'''" + '''            table.Columns[9].Width = Math.Max(textHeight * 11.0, 0.001);
            table.Columns[11].Width = Math.Max(textHeight * 12.0, 0.001);''' + "'''" + ''', ''' + "'''" + '''            table.Columns[8].Width = Math.Max(textHeight * 11.0, 0.001);
            table.Columns[10].Width = Math.Max(textHeight * 12.0, 0.001);''' + "'''" + ''', "vertex table widths")
text = replace_once(text, ''' + "'''" + '''            table.MergeCells(CellRange.Create(table, 0, 0, 0, 11));''' + "'''" + ''', ''' + "'''" + '''            table.MergeCells(CellRange.Create(table, 0, 0, 0, 10));''' + "'''" + ''', "vertex table title merge")
text = replace_once(text, ''' + "'''" + '''                "Z", "NG LEVEL", "DESIGN LEVEL", "DIFFERENCE", "RADIUS", "SEGMENT LENGTH"''' + "'''" + ''', ''' + "'''" + '''                "NG LEVEL", "DESIGN LEVEL", "DIFFERENCE", "RADIUS", "SEGMENT LENGTH"''' + "'''" + ''', "vertex table headings")
text = replace_once(text, ''' + "'''" + '''                table.Cells[row, 6].TextString = record.Point.Z.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 7].TextString = record.NgLevel.HasValue
                    ? record.NgLevel.Value.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 8].TextString = record.DesignLevel.HasValue
                    ? record.DesignLevel.Value.ToString("N3", CultureInfo.CurrentCulture)
                    : record.Point.Z.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 9].TextString = record.NgLevel.HasValue
                    ? ((record.DesignLevel ?? record.Point.Z) - record.NgLevel.Value).ToString("+0.000;-0.000;0.000", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 10].TextString = record.Radius.HasValue
                    ? record.Radius.Value.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 11].TextString = record.SegmentLength > 0.0
                    ? record.SegmentLength.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;''' + "'''" + ''', ''' + "'''" + '''                table.Cells[row, 6].TextString = record.NgLevel.HasValue
                    ? record.NgLevel.Value.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 7].TextString = record.DesignLevel.HasValue
                    ? record.DesignLevel.Value.ToString("N3", CultureInfo.CurrentCulture)
                    : record.Point.Z.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 8].TextString = record.NgLevel.HasValue
                    ? ((record.DesignLevel ?? record.Point.Z) - record.NgLevel.Value).ToString("+0.000;-0.000;0.000", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 9].TextString = record.Radius.HasValue
                    ? record.Radius.Value.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 10].TextString = record.SegmentLength > 0.0
                    ? record.SegmentLength.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;''' + "'''" + ''', "vertex table row values")
'''
text = text[:start] + current_table_patch + text[end:]

exec(compile(text, str(source), "exec"), {"__name__": "__main__", "__file__": str(source)})
