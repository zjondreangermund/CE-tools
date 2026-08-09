from pathlib import Path

source = Path(__file__).resolve().parent / "Apply-FinalAllCommentsCompletion.py"
text = source.read_text(encoding="utf-8")

# Live source uses IList for ApplyLevelReferences.
text = text.replace("IEnumerable<VertexSettingSource> sources", "IList<VertexSettingSource> sources")

# Live VertexSettingLink keeps coordinate display properties immediately after NG handle.
text = text.replace(
'''# Add DesignSurfaceHandle property next to NgSurfaceHandle.
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
text = replace_once(text, old, new, "read design surface link")''',
'''# Add DesignSurfaceHandle property next to NgSurfaceHandle.
text = replace_once(text,
''' + "'''" + '''            public string NgSurfaceHandle { get; set; }
            public string CoordinateOrder''' + "'''" + ''',
''' + "'''" + '''            public string NgSurfaceHandle { get; set; }
            public string DesignSurfaceHandle { get; set; }
            public string CoordinateOrder''' + "'''" + ''',
"design surface link property")

# Persist the independent design surface in the current XData schema.
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
"read design surface link")''')

exec(compile(text, str(source), "exec"), {"__name__": "__main__", "__file__": str(source)})
