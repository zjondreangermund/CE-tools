from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]

def read(rel): return (ROOT / rel).read_text(encoding='utf-8')
def write(rel, text): (ROOT / rel).write_text(text, encoding='utf-8')
def rep(text, old, new, label):
    if old not in text:
        raise SystemExit(f'Missing patch marker: {label}')
    return text.replace(old, new, 1)

# 1) WGS84 + NE/YX convention tools.
p = 'src/CE.Tools.Civil3D/ProjectCoordinationCommands.cs'
t = read(p)
old = '''            model.AddText("Latitude", "01 WGS84", "Latitude", "-22.5609", "Decimal degrees; south is negative. Values from -90 to 90 are accepted.");
            model.AddText("Longitude", "01 WGS84", "Longitude", "17.0658", "Decimal degrees; west is negative and east is positive. Values from -180 to 180 are accepted.");
            model.AddChoice("Provider", "02 Map", "Open in", "Google Maps", "Choose the web map opened after confirmation.", new[] { "Google Maps", "Google Earth" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            double latitude;
            double longitude;
            if (!TryParseCoordinate(model.Text("Latitude"), -90.0, 90.0, out latitude) ||
                !TryParseCoordinate(model.Text("Longitude"), -180.0, 180.0, out longitude))
            {
                document.Editor.WriteMessage("\\nCE_MAPLOCATION stopped. Enter valid signed WGS84 latitude (-90 to 90) and longitude (-180 to 180).");
                return;
            }
            string lat = latitude.ToString("0.########", CultureInfo.InvariantCulture);
            string lon = longitude.ToString("0.########", CultureInfo.InvariantCulture);
            string url = string.Equals(model.Text("Provider"), "Google Earth", StringComparison.OrdinalIgnoreCase)
                ? "https://earth.google.com/web/@" + lat + "," + lon + ",1000a,1000d,35y,0h,0t,0r"
                : "https://www.google.com/maps/search/?api=1&query=" + lat + "%2C" + lon;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                document.Editor.WriteMessage("\\nCE_MAPLOCATION opened {0}: {1}, {2}.", model.Text("Provider"), lat, lon);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\\nCE_MAPLOCATION could not open the browser. {0}", exception.Message);
            }'''
new = '''            model.AddText("Latitude", "01 WGS84", "Latitude", "-22.5609", "Decimal degrees; south is negative. Values from -90 to 90 are accepted.");
            model.AddText("Longitude", "01 WGS84", "Longitude", "17.0658", "Decimal degrees; west is negative and east is positive. Values from -180 to 180 are accepted.");
            model.AddText("Northing", "02 NE / YX", "Northing (N / Y)", "0.000", "Survey Northing maps directly to drawing Y. Signed values are accepted.");
            model.AddText("Easting", "02 NE / YX", "Easting (E / X)", "0.000", "Survey Easting maps directly to drawing X. Signed values are accepted.");
            model.AddText("X", "02 NE / YX", "Drawing X / Easting", "0.000", "Drawing X is the Easting convention used by CE Tools.");
            model.AddText("Y", "02 NE / YX", "Drawing Y / Northing", "0.000", "Drawing Y is the Northing convention used by CE Tools.");
            model.AddChoice("Action", "03 Action", "Action", "Open WGS84 in map", "Open the WGS84 point or convert the survey/drawing coordinate labels without changing geometry.", new[] { "Open WGS84 in map", "Northing / Easting -> Y / X", "Y / X -> Northing / Easting" });
            model.AddChoice("Provider", "03 Action", "Open in", "Google Maps", "Choose the web map opened for the WGS84 action.", new[] { "Google Maps", "Google Earth" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            string action = model.Text("Action");
            if (!string.Equals(action, "Open WGS84 in map", StringComparison.OrdinalIgnoreCase))
            {
                double first;
                double second;
                bool neToYx = action.StartsWith("Northing", StringComparison.OrdinalIgnoreCase);
                string firstKey = neToYx ? "Northing" : "Y";
                string secondKey = neToYx ? "Easting" : "X";
                if (!TryParseSignedNumber(model.Text(firstKey), out first) || !TryParseSignedNumber(model.Text(secondKey), out second))
                {
                    document.Editor.WriteMessage("\\nCE_MAPLOCATION stopped. Enter valid signed coordinate values for the selected conversion.");
                    return;
                }
                string result = neToYx
                    ? string.Format(CultureInfo.CurrentCulture, "Northing {0:N3} -> Y {0:N3}\\nEasting {1:N3} -> X {1:N3}", first, second)
                    : string.Format(CultureInfo.CurrentCulture, "Y {0:N3} -> Northing {0:N3}\\nX {1:N3} -> Easting {1:N3}", first, second);
                MessageBox.Show(result, "CE Tools - NE / YX Conversion", MessageBoxButton.OK, MessageBoxImage.Information);
                document.Editor.WriteMessage("\\nCE_MAPLOCATION coordinate conversion: {0}", result.Replace("\\n", "; "));
                return;
            }
            double latitude;
            double longitude;
            if (!TryParseCoordinate(model.Text("Latitude"), -90.0, 90.0, out latitude) ||
                !TryParseCoordinate(model.Text("Longitude"), -180.0, 180.0, out longitude))
            {
                document.Editor.WriteMessage("\\nCE_MAPLOCATION stopped. Enter valid signed WGS84 latitude (-90 to 90) and longitude (-180 to 180).");
                return;
            }
            string lat = latitude.ToString("0.########", CultureInfo.InvariantCulture);
            string lon = longitude.ToString("0.########", CultureInfo.InvariantCulture);
            string url = string.Equals(model.Text("Provider"), "Google Earth", StringComparison.OrdinalIgnoreCase)
                ? "https://earth.google.com/web/@" + lat + "," + lon + ",1000a,1000d,35y,0h,0t,0r"
                : "https://www.google.com/maps/search/?api=1&query=" + lat + "%2C" + lon;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                document.Editor.WriteMessage("\\nCE_MAPLOCATION opened {0}: {1}, {2}.", model.Text("Provider"), lat, lon);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\\nCE_MAPLOCATION could not open the browser. {0}", exception.Message);
            }'''
t = rep(t, old, new, 'map popup conversion fields')
marker = '''        private static bool TryParseCoordinate(string text, double minimum, double maximum, out double value)
        {'''
insert = '''        private static bool TryParseSignedNumber(string text, out double value)
        {
            return (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value) ||
                    double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value)) &&
                   !double.IsNaN(value) && !double.IsInfinity(value);
        }

'''
t = rep(t, marker, insert + marker, 'signed number helper')
write(p, t)

# 2) Vertex records carry NG/design level data.
p = 'src/CE.Tools.Civil3D/VertexSettingOutGeometry.cs'
t = read(p)
old = '''        public double? Radius { get; set; }
        public string PointName { get; set; }'''
new = '''        public double? Radius { get; set; }
        public double? NgLevel { get; set; }
        public double? DesignLevel { get; set; }
        public string PointName { get; set; }'''
t = rep(t, old, new, 'vertex level properties')
write(p, t)

# 3) Multi-source vertex setting out: NG surface + design/difference table + anchored MText.
p = 'src/CE.Tools.Civil3D/VertexSettingOutCommands.cs'
t = read(p)
old = '''            List<string> surfaceChoices = ReadSurfaceNames(document.Database, civilDocument);
            surfaceChoices.Insert(0, "<Pick surface in drawing>");'''
new = '''            List<string> surfaceChoices = ReadSurfaceNames(document.Database, civilDocument);
            surfaceChoices.Insert(0, "<Pick surface in drawing>");
            var ngSurfaceChoices = new List<string> { "<None>" };
            ngSurfaceChoices.AddRange(surfaceChoices);'''
t = rep(t, old, new, 'ng surface choices')
old = '''            settings.AddChoice(
                "ElevationSurface", "01 Output", "Civil 3D elevation surface", "<Pick surface in drawing>",
                "Choose an existing surface by name or keep the pick option to select it in the drawing after saving the popup.",
                surfaceChoices);'''
new = old + '''
            settings.AddChoice(
                "NGSurface", "01 Output", "Existing / NG level surface", "<None>",
                "Optional existing-ground surface used for NG Level and Difference columns. It does not change X/Y or the design point elevation.",
                ngSurfaceChoices);'''
t = rep(t, old, new, 'ng surface popup')
old = '''            string elevationSurface = settings.Text("ElevationSurface");
            string coordinateOrder = settings.Text("CoordinateOrder");'''
new = '''            string elevationSurface = settings.Text("ElevationSurface");
            string ngSurface = settings.Text("NGSurface");
            string coordinateOrder = settings.Text("CoordinateOrder");'''
t = rep(t, old, new, 'read ng surface')
old = '''            ObjectId elevationSourceId;
            if (!PromptElevationSource(
                    document,
                    civilDocument,
                    elevationMode,
                    elevationSurface,
                    out elevationSourceId)) return;'''
new = old + '''
            ObjectId ngSurfaceId = ObjectId.Null;
            if (!string.IsNullOrWhiteSpace(ngSurface) && !string.Equals(ngSurface, "<None>", StringComparison.OrdinalIgnoreCase))
            {
                if (!PromptElevationSource(
                        document,
                        civilDocument,
                        "Select Civil 3D surface",
                        ngSurface,
                        out ngSurfaceId)) return;
            }'''
t = rep(t, old, new, 'resolve ng surface')
old = '''            ApplyElevationReference(
                document.Database,
                sources,
                elevationMode,
                elevationSourceId);'''
new = old + '''
            ApplyLevelReferences(document.Database, sources, ngSurfaceId);'''
t = rep(t, old, new, 'apply ng levels initial')
old = '''                ElevationSourceHandle = elevationSourceId.IsNull
                    ? string.Empty
                    : elevationSourceId.Handle.ToString(),
                SourceHandles = linkedHandles'''
new = '''                ElevationSourceHandle = elevationSourceId.IsNull
                    ? string.Empty
                    : elevationSourceId.Handle.ToString(),
                NgSurfaceHandle = ngSurfaceId.IsNull
                    ? string.Empty
                    : ngSurfaceId.Handle.ToString(),
                SourceHandles = linkedHandles'''
t = rep(t, old, new, 'persist ng handle initial')
old = '''                ApplyElevationReference(
                    document.Database,
                    sources,
                    link.ElevationMode,
                    ResolveHandle(document.Database, link.ElevationSourceHandle));'''
new = old + '''
                ApplyLevelReferences(
                    document.Database,
                    sources,
                    ResolveHandle(document.Database, link.NgSurfaceHandle));'''
t = rep(t, old, new, 'apply ng on refresh')
# MText location/reference point exactly at source; attachment determines visual side.
old = '''            var mtext = new MText();
            mtext.SetDatabaseDefaults(database);
            mtext.Location = OutputLocation(record, link.LabelOffset);
            mtext.Attachment = AttachmentPoint.BottomLeft;
            mtext.TextHeight = textHeight;
            mtext.Contents = LabelText(record, link);'''
new = '''            var mtext = new MText();
            mtext.SetDatabaseDefaults(database);
            mtext.Location = record.Point;
            mtext.Attachment = AnchoredAttachment(record, link.LabelOffset);
            mtext.TextHeight = textHeight;
            mtext.Contents = AnchoredMText(LabelText(record, link));'''
t = rep(t, old, new, 'vertex anchored mtext create')
old = '''                CaptureCurrentAnnotationOffset(transaction, id, record);
                mtext.Location = OutputLocation(record, link.LabelOffset);
                mtext.TextHeight = textHeight;
                mtext.Contents = LabelText(record, link);'''
new = '''                CaptureCurrentAnnotationOffset(transaction, id, record);
                mtext.Location = record.Point;
                mtext.Attachment = AnchoredAttachment(record, link.LabelOffset);
                mtext.TextHeight = textHeight;
                mtext.Contents = AnchoredMText(LabelText(record, link));'''
t = rep(t, old, new, 'vertex anchored mtext refresh')
# Replace table with 12 columns.
old = '''            table.SetSize(records.Count + 2, 9);
            table.SetRowHeight(Math.Max(textHeight * 1.8, 0.001));
            table.SetColumnWidth(Math.Max(textHeight * 8.0, 0.001));
            table.Columns[0].Width = Math.Max(textHeight * 9.0, 0.001);
            table.Columns[1].Width = Math.Max(textHeight * 18.0, 0.001);
            table.Columns[2].Width = Math.Max(textHeight * 14.0, 0.001);
            table.Columns[8].Width = Math.Max(textHeight * 12.0, 0.001);
            table.Cells[0, 0].TextString = "CE VERTEX SETTING-OUT - " + (link.OutputType ?? string.Empty).ToUpperInvariant();
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 8));'''
new = '''            table.SetSize(records.Count + 2, 12);
            table.SetRowHeight(Math.Max(textHeight * 1.8, 0.001));
            table.SetColumnWidth(Math.Max(textHeight * 8.0, 0.001));
            table.Columns[0].Width = Math.Max(textHeight * 9.0, 0.001);
            table.Columns[1].Width = Math.Max(textHeight * 18.0, 0.001);
            table.Columns[2].Width = Math.Max(textHeight * 14.0, 0.001);
            table.Columns[9].Width = Math.Max(textHeight * 11.0, 0.001);
            table.Columns[11].Width = Math.Max(textHeight * 12.0, 0.001);
            table.Cells[0, 0].TextString = "CE VERTEX SETTING-OUT - " + (link.OutputType ?? string.Empty).ToUpperInvariant();
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 11));'''
t = rep(t, old, new, 'vertex table size')
old = '''                yFirst ? "Y" : "X",
                yFirst ? "X" : "Y",
                "Z", "RADIUS", "SEGMENT LENGTH"
            };'''
new = '''                yFirst ? "Y" : "X",
                yFirst ? "X" : "Y",
                "Z", "NG LEVEL", "DESIGN LEVEL", "DIFFERENCE", "RADIUS", "SEGMENT LENGTH"
            };'''
t = rep(t, old, new, 'vertex table headings')
old = '''                table.Cells[row, 6].TextString = record.Point.Z.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 7].TextString = record.Radius.HasValue
                    ? record.Radius.Value.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 8].TextString = record.SegmentLength > 0.0
                    ? record.SegmentLength.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;'''
new = '''                table.Cells[row, 6].TextString = record.Point.Z.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 7].TextString = record.NgLevel.HasValue
                    ? record.NgLevel.Value.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 8].TextString = record.DesignLevel.HasValue
                    ? record.DesignLevel.Value.ToString("N3", CultureInfo.CurrentCulture)
                    : record.Point.Z.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[row, 9].TextString = record.NgLevel.HasValue
                    ? ((record.DesignLevel ?? record.Point.Z) - record.NgLevel.Value).ToString("+N3;-N3;0.000", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 10].TextString = record.Radius.HasValue
                    ? record.Radius.Value.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;
                table.Cells[row, 11].TextString = record.SegmentLength > 0.0
                    ? record.SegmentLength.ToString("N3", CultureInfo.CurrentCulture)
                    : string.Empty;'''
t = rep(t, old, new, 'vertex level values')
# Persist NG handle.
old = '''            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "ELEVHANDLE=" + (link.ElevationSourceHandle ?? string.Empty)));'''
new = old + '''
            values.Add(new TypedValue(
                (int)DxfCode.ExtendedDataAsciiString,
                "NGHANDLE=" + (link.NgSurfaceHandle ?? string.Empty)));'''
t = rep(t, old, new, 'vertex ng xdata write')
old = '''                ElevationSourceHandle = string.Empty,
                CoordinateOrder = "X then Y",'''
new = '''                ElevationSourceHandle = string.Empty,
                NgSurfaceHandle = string.Empty,
                CoordinateOrder = "X then Y",'''
t = rep(t, old, new, 'vertex ng default read')
old = '''                else if (value.StartsWith("ORDER=", StringComparison.OrdinalIgnoreCase))
                    link.CoordinateOrder = value.Substring(6);'''
new = '''                else if (value.StartsWith("NGHANDLE=", StringComparison.OrdinalIgnoreCase))
                    link.NgSurfaceHandle = value.Substring(9);
                else if (value.StartsWith("ORDER=", StringComparison.OrdinalIgnoreCase))
                    link.CoordinateOrder = value.Substring(6);'''
t = rep(t, old, new, 'vertex ng xdata read')
old = '''            public string ElevationSourceHandle { get; set; }
            public string CoordinateOrder { get; set; }'''
new = '''            public string ElevationSourceHandle { get; set; }
            public string NgSurfaceHandle { get; set; }
            public string CoordinateOrder { get; set; }'''
t = rep(t, old, new, 'vertex ng link property')
# Add helpers before InventoryGroup.
marker = '''        private static void InventoryGroup(
            BlockTableRecord modelSpace,'''
helpers = '''        private static void ApplyLevelReferences(
            Database database,
            IList<VertexSettingSource> sources,
            ObjectId ngSurfaceId)
        {
            if (sources == null) return;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Autodesk.Civil.DatabaseServices.Surface ng = null;
                if (!ngSurfaceId.IsNull && !ngSurfaceId.IsErased)
                {
                    try { ng = transaction.GetObject(ngSurfaceId, OpenMode.ForRead, false) as Autodesk.Civil.DatabaseServices.Surface; }
                    catch { ng = null; }
                }
                foreach (VertexSettingRecord record in sources.SelectMany(item => item.Records))
                {
                    record.DesignLevel = record.Point.Z;
                    record.NgLevel = null;
                    if (ng == null) continue;
                    try
                    {
                        double value = ng.FindElevationAtXY(record.Point.X, record.Point.Y);
                        if (!double.IsNaN(value) && !double.IsInfinity(value)) record.NgLevel = value;
                    }
                    catch { }
                }
            }
        }

        private static AttachmentPoint AnchoredAttachment(VertexSettingRecord record, double offset)
        {
            Vector3d direction = record != null && record.AnnotationOffset.HasValue
                ? record.AnnotationOffset.Value
                : new Vector3d(offset, offset, 0.0);
            if (direction.X < 0.0 && direction.Y >= 0.0) return AttachmentPoint.BottomRight;
            if (direction.X < 0.0 && direction.Y < 0.0) return AttachmentPoint.TopRight;
            if (direction.X >= 0.0 && direction.Y < 0.0) return AttachmentPoint.TopLeft;
            return AttachmentPoint.BottomLeft;
        }

        private static string AnchoredMText(string contents)
        {
            string pad = "\\\\~\\\\~";
            return pad + (contents ?? string.Empty).Replace("\\\\P", "\\\\P" + pad);
        }

'''
t = rep(t, marker, helpers + marker, 'vertex level/anchor helpers')
write(p, t)

# 4) Coordinate workflow tables get NG/design/difference; MText insertion stays at CAD point.
p = 'src/CE.Tools.Civil3D/SurveyCoordinateWorkflowCommands.cs'
t = read(p)
old = '''                if (settings.Output == AnnotationOutput.MText)
                {
                    var text = new MText();
                    text.SetDatabaseDefaults(database);
                    text.Location = labelPoint;
                    text.Attachment = AttachmentPoint.MiddleLeft;
                    text.TextHeight = PaperAnnotationScale.AnnotativeTextHeight(
                        database,
                        settings.TextHeight);
                    text.Contents = contents;'''
new = '''                if (settings.Output == AnnotationOutput.MText)
                {
                    var text = new MText();
                    text.SetDatabaseDefaults(database);
                    text.Location = target;
                    text.Attachment = CoordinateAttachment(target, labelPoint);
                    text.TextHeight = PaperAnnotationScale.AnnotativeTextHeight(
                        database,
                        settings.TextHeight);
                    text.Contents = AnchoredCoordinateText(contents);'''
t = rep(t, old, new, 'coordinate mtext anchor')
# New linked level table overload.
old = '''        internal static ObjectId CreateLinkedTable(
            Database database,
            Point3d insertionPoint,
            IList<ObjectId> sourceIds,
            double textHeight,
            string title)
        {'''
new = '''        internal static ObjectId CreateLinkedTable(
            Database database,
            Point3d insertionPoint,
            IList<ObjectId> sourceIds,
            double textHeight,
            string title)
        {
            return CreateLinkedLevelTable(database, insertionPoint, sourceIds, textHeight, title, ObjectId.Null);
        }

        internal static ObjectId CreateLinkedLevelTable(
            Database database,
            Point3d insertionPoint,
            IList<ObjectId> sourceIds,
            double textHeight,
            string title,
            ObjectId ngSurfaceId)
        {'''
t = rep(t, old, new, 'coordinate level table overload')
old = '''                WriteLinkRecord(table, transaction, sourceIds);
                int missing;
                List<CoordinateRow> rows = ReadRows(transaction, sourceIds, out missing);'''
new = '''                WriteLinkRecord(table, transaction, sourceIds, ngSurfaceId);
                int missing;
                List<CoordinateRow> rows = ReadRows(transaction, sourceIds, ngSurfaceId, out missing);'''
t = rep(t, old, new, 'coordinate create level rows')
# Append/refresh preserve NG surface.
old = '''                WriteLinkRecord(table, transaction, links);
                int missing;
                List<CoordinateRow> rows = ReadRows(transaction, links, out missing);'''
new = '''                ObjectId ngSurfaceId = ReadNgSurfaceId(database, table, transaction);
                WriteLinkRecord(table, transaction, links, ngSurfaceId);
                int missing;
                List<CoordinateRow> rows = ReadRows(transaction, links, ngSurfaceId, out missing);'''
t = rep(t, old, new, 'coordinate append preserves ng')
old = '''                List<CoordinateRow> rows = ReadRows(transaction, links, out missing);
                active = rows.Count;'''
new = '''                ObjectId ngSurfaceId = ReadNgSurfaceId(database, table, transaction);
                List<CoordinateRow> rows = ReadRows(transaction, links, ngSurfaceId, out missing);
                active = rows.Count;'''
t = rep(t, old, new, 'coordinate refresh ng')
# ReadRows signature and surface sampling.
old = '''        private static List<CoordinateRow> ReadRows(
            Transaction transaction,
            IList<ObjectId> sourceIds,
            out int missing)
        {
            var rows = new List<CoordinateRow>();'''
new = '''        private static List<CoordinateRow> ReadRows(
            Transaction transaction,
            IList<ObjectId> sourceIds,
            ObjectId ngSurfaceId,
            out int missing)
        {
            var rows = new List<CoordinateRow>();
            CivilSurface ngSurface = null;
            if (!ngSurfaceId.IsNull && !ngSurfaceId.IsErased)
            {
                try { ngSurface = transaction.GetObject(ngSurfaceId, OpenMode.ForRead, false) as CivilSurface; }
                catch { ngSurface = null; }
            }'''
t = rep(t, old, new, 'coordinate rows signature')
old = '''                    rows.Add(new CoordinateRow(
                        cogo.PointNumber.ToString(CultureInfo.InvariantCulture),
                        pointName,
                        cogo.Northing,
                        cogo.Easting,
                        cogo.Elevation));'''
new = '''                    double? ng = SampleLevel(ngSurface, cogo.Easting, cogo.Northing);
                    rows.Add(new CoordinateRow(
                        cogo.PointNumber.ToString(CultureInfo.InvariantCulture),
                        pointName,
                        cogo.Northing,
                        cogo.Easting,
                        cogo.Elevation,
                        ng));'''
t = rep(t, old, new, 'coordinate cogo ng')
old = '''                    rows.Add(new CoordinateRow(
                        fallbackNumber.ToString(CultureInfo.InvariantCulture),
                        pointName,
                        point.Position.Y,
                        point.Position.X,
                        point.Position.Z));'''
new = '''                    double? ng = SampleLevel(ngSurface, point.Position.X, point.Position.Y);
                    rows.Add(new CoordinateRow(
                        fallbackNumber.ToString(CultureInfo.InvariantCulture),
                        pointName,
                        point.Position.Y,
                        point.Position.X,
                        point.Position.Z,
                        ng));'''
t = rep(t, old, new, 'coordinate dbpoint ng')
# 7-column table.
old = '''            const int columns = 4;'''
new = '''            const int columns = 7;'''
t = rep(t, old, new, 'coordinate table column count')
old = '''                "POINT NAME",
                "X",
                "Y",
                "Z"
            };'''
new = '''                "POINT NAME",
                "X",
                "Y",
                "Z",
                "NG LEVEL",
                "DESIGN LEVEL",
                "DIFFERENCE"
            };'''
t = rep(t, old, new, 'coordinate table level headings')
old = '''                    row.PointName,
                    row.X.ToString("N3", CultureInfo.CurrentCulture),
                    row.Y.ToString("N3", CultureInfo.CurrentCulture),
                    row.Z.ToString("N3", CultureInfo.CurrentCulture)
                };'''
new = '''                    row.PointName,
                    row.X.ToString("N3", CultureInfo.CurrentCulture),
                    row.Y.ToString("N3", CultureInfo.CurrentCulture),
                    row.Z.ToString("N3", CultureInfo.CurrentCulture),
                    row.NgLevel.HasValue ? row.NgLevel.Value.ToString("N3", CultureInfo.CurrentCulture) : string.Empty,
                    row.Z.ToString("N3", CultureInfo.CurrentCulture),
                    row.NgLevel.HasValue ? (row.Z - row.NgLevel.Value).ToString("+N3;-N3;0.000", CultureInfo.CurrentCulture) : string.Empty
                };'''
t = rep(t, old, new, 'coordinate table level values')
# Link record overload NG surface.
old = '''        private static void WriteLinkRecord(
            Table table,
            Transaction transaction,
            IList<ObjectId> sourceIds)
        {'''
new = '''        private static void WriteLinkRecord(
            Table table,
            Transaction transaction,
            IList<ObjectId> sourceIds)
        {
            WriteLinkRecord(table, transaction, sourceIds, ObjectId.Null);
        }

        private static void WriteLinkRecord(
            Table table,
            Transaction transaction,
            IList<ObjectId> sourceIds,
            ObjectId ngSurfaceId)
        {'''
t = rep(t, old, new, 'coordinate link ng overload')
old = '''            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion)
            };'''
new = '''            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.Text, "Schema=" + SchemaVersion)
            };
            if (!ngSurfaceId.IsNull && !ngSurfaceId.IsErased)
                values.Add(new TypedValue((int)DxfCode.Text, "NgSurface=" + ngSurfaceId.Handle));'''
t = rep(t, old, new, 'coordinate ng xrecord write')
# Helpers before ReadLinkRecord.
marker = '''        private static List<ObjectId> ReadLinkRecord(
            Database database,'''
helpers = '''        private static ObjectId ReadNgSurfaceId(Database database, Table table, Transaction transaction)
        {
            if (table == null || table.ExtensionDictionary.IsNull) return ObjectId.Null;
            DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkRecordName)) return ObjectId.Null;
            Xrecord record = transaction.GetObject(dictionary.GetAt(LinkRecordName), OpenMode.ForRead, false) as Xrecord;
            if (record == null || record.Data == null) return ObjectId.Null;
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("NgSurface=", StringComparison.OrdinalIgnoreCase)) continue;
                long handle;
                if (!long.TryParse(text.Substring(10), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out handle)) return ObjectId.Null;
                try { return database.GetObjectId(false, new Handle(handle), 0); }
                catch { return ObjectId.Null; }
            }
            return ObjectId.Null;
        }

        private static double? SampleLevel(CivilSurface surface, double x, double y)
        {
            if (surface == null) return null;
            try
            {
                double value = surface.FindElevationAtXY(x, y);
                return double.IsNaN(value) || double.IsInfinity(value) ? (double?)null : value;
            }
            catch { return null; }
        }

'''
t = rep(t, marker, helpers + marker, 'coordinate ng read helper')
# Expose optional NG surface selection using existing UI.
old = '''        private static bool PromptOptionalSurface(
            Document document,
            out ObjectId surfaceId)
        {'''
new = '''        internal static bool PromptOptionalNgSurface(Document document, out ObjectId surfaceId)
        {
            return PromptOptionalSurface(document, out surfaceId, "Select an existing-ground surface for NG Level and Difference columns");
        }

        private static bool PromptOptionalSurface(
            Document document,
            out ObjectId surfaceId)
        {
            return PromptOptionalSurface(document, out surfaceId, "Link point Z values dynamically to a Civil 3D surface");
        }

        private static bool PromptOptionalSurface(
            Document document,
            out ObjectId surfaceId,
            string question)
        {'''
t = rep(t, old, new, 'coordinate surface question overload')
old = '''                "Link point Z values dynamically to a Civil 3D surface",
                true))'''
new = '''                question,
                true))'''
t = rep(t, old, new, 'coordinate surface custom question')
# Coordinate row level property.
old = '''            public CoordinateRow(string point, string pointName, double y, double x, double z)
            {
                Point = point;
                PointName = pointName;
                Y = y;
                X = x;
                Z = z;
            }'''
new = '''            public CoordinateRow(string point, string pointName, double y, double x, double z, double? ngLevel)
            {
                Point = point;
                PointName = pointName;
                Y = y;
                X = x;
                Z = z;
                NgLevel = ngLevel;
            }'''
t = rep(t, old, new, 'coordinate row constructor')
old = '''            public double Z { get; }
        }'''
new = '''            public double Z { get; }
            public double? NgLevel { get; }
        }'''
t = rep(t, old, new, 'coordinate row ng property')
# Anchored MText helpers near BuildMTextCoordinate.
marker = '''        private static string BuildMTextCoordinate(Point3d point)
        {'''
helpers = '''        private static AttachmentPoint CoordinateAttachment(Point3d target, Point3d labelPoint)
        {
            double dx = labelPoint.X - target.X;
            double dy = labelPoint.Y - target.Y;
            if (dx < 0.0 && dy >= 0.0) return AttachmentPoint.BottomRight;
            if (dx < 0.0 && dy < 0.0) return AttachmentPoint.TopRight;
            if (dx >= 0.0 && dy < 0.0) return AttachmentPoint.TopLeft;
            return AttachmentPoint.BottomLeft;
        }

        private static string AnchoredCoordinateText(string contents)
        {
            string pad = "\\\\~\\\\~";
            return pad + (contents ?? string.Empty).Replace("\\\\P", "\\\\P" + pad);
        }

'''
t = rep(t, marker, helpers + marker, 'coordinate anchored text helpers')
write(p, t)

# 5) Feature-line table gets optional NG surface and level columns through shared linked table.
p = 'src/CE.Tools.Civil3D/FeatureProfileSurfaceCommentCommands.cs'
t = read(p)
old = '''            int created = 0;
            int rejected = 0;
            var work = new List<FeatureVertexWork>();'''
new = '''            ObjectId ngSurfaceId;
            if (!SurveyCoordinateWorkflowCommands.PromptOptionalNgSurface(document, out ngSurfaceId)) return;

            int created = 0;
            int rejected = 0;
            var work = new List<FeatureVertexWork>();'''
t = rep(t, old, new, 'feature ng surface selection')
old = '''                    SurveyCoordinateWorkflowCommands.CreateLinkedTable(
                        document.Database,
                        insertion.Value.TransformBy(
                            document.Editor.CurrentUserCoordinateSystem),
                        linkedPointIds,
                        settings.TextHeight,
                        "FEATURE LINE POINTS");'''
new = '''                    SurveyCoordinateWorkflowCommands.CreateLinkedLevelTable(
                        document.Database,
                        insertion.Value.TransformBy(
                            document.Editor.CurrentUserCoordinateSystem),
                        linkedPointIds,
                        settings.TextHeight,
                        "FEATURE LINE POINTS",
                        ngSurfaceId);'''
t = rep(t, old, new, 'feature level table')
write(p, t)

# 6) Vertex-linked MText must retain exact anchor; overlap solver must not move its insertion grip.
p = 'src/CE.Tools.Civil3D/PreBuildRuntimeCompletionCommands.cs'
t = read(p)
old = '''                    Point3d location;
                    if (!TryReadAnnotationLocation(entity, out location)) continue;
                    Vector3d offset = location - anchor;'''
new = '''                    Point3d location;
                    if (!TryReadAnnotationLocation(entity, out location)) continue;
                    MText anchoredText = entity as MText;
                    if (anchoredText != null)
                    {
                        if (anchoredText.Location.DistanceTo(anchor) > 1e-8)
                            anchoredText.Location = anchor;
                        EnsureVisible(anchoredText);
                        continue;
                    }
                    Vector3d offset = location - anchor;'''
t = rep(t, old, new, 'keep vertex mtext anchor fixed')
write(p, t)

# 7) BOQ: geometric pipe endpoints first, nominal diameter snap, all cells centered.
p = 'src/CE.Tools.Civil3D/BillOfQuantitiesCommands.cs'
t = read(p)
t = t.replace('TryGetLength(databaseObject, out raw)', 'TryGetLength(databaseObject, transaction, out raw)')
old = '''                    table.Cells[row, column].Alignment =
                        column == 3 || column == 9
                            ? CellAlignment.MiddleLeft
                            : CellAlignment.MiddleCenter;'''
new = '''                    table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;'''
t = rep(t, old, new, 'boq centered cells')
old = '''        private static bool TryGetLength(DBObject databaseObject, out double length)
        {
            length = 0.0;

            var curve = databaseObject as Curve;
            if (curve != null)
            {
                try
                {
                    double start = curve.GetDistanceAtParameter(curve.StartParam);
                    double end = curve.GetDistanceAtParameter(curve.EndParam);
                    length = Math.Abs(end - start);
                    if (IsFinitePositive(length)) return true;
                }
                catch
                {
                    // Continue to reflection-based Civil 3D properties.
                }
            }

            return TryReadDoubleProperty(
                databaseObject,
                out length,
                "Length3DCenterToCenter",
                "Length2DCenterToCenter",
                "Length3D",
                "Length2D",
                "Length");
        }'''
new = '''        private static bool TryGetLength(DBObject databaseObject, Transaction transaction, out double length)
        {
            length = 0.0;
            Point3d startPoint;
            Point3d endPoint;
            if (TryReadPointProperty(databaseObject, "StartPoint", out startPoint) &&
                TryReadPointProperty(databaseObject, "EndPoint", out endPoint))
            {
                length = startPoint.DistanceTo(endPoint);
                if (IsFinitePositive(length)) return true;
            }
            if (TryReadConnectedStructureLength(databaseObject, transaction, out length) && IsFinitePositive(length))
                return true;

            var curve = databaseObject as Curve;
            if (curve != null)
            {
                try
                {
                    double start = curve.GetDistanceAtParameter(curve.StartParam);
                    double end = curve.GetDistanceAtParameter(curve.EndParam);
                    length = Math.Abs(end - start);
                    if (IsFinitePositive(length)) return true;
                }
                catch { }
            }

            if (TryReadDoubleProperty(
                    databaseObject,
                    out length,
                    "Length3DCenterToCenter",
                    "Length2DCenterToCenter",
                    "Length3D",
                    "Length2D",
                    "Length") &&
                IsFinitePositive(length))
                return true;
            return false;
        }

        private static bool TryReadConnectedStructureLength(DBObject value, Transaction transaction, out double length)
        {
            length = 0.0;
            if (value == null || transaction == null) return false;
            ObjectId startId;
            ObjectId endId;
            if (!TryReadObjectIdProperty(value, "StartStructureId", out startId) ||
                !TryReadObjectIdProperty(value, "EndStructureId", out endId) ||
                startId.IsNull || endId.IsNull) return false;
            try
            {
                DBObject start = transaction.GetObject(startId, OpenMode.ForRead, false);
                DBObject end = transaction.GetObject(endId, OpenMode.ForRead, false);
                Point3d a;
                Point3d b;
                if (!TryReadPointProperty(start, "Position", out a) && !TryReadPointProperty(start, "Location", out a)) return false;
                if (!TryReadPointProperty(end, "Position", out b) && !TryReadPointProperty(end, "Location", out b)) return false;
                length = a.DistanceTo(b);
                return IsFinitePositive(length);
            }
            catch { return false; }
        }

        private static bool TryReadObjectIdProperty(object value, string name, out ObjectId id)
        {
            id = ObjectId.Null;
            if (value == null) return false;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                object raw = property == null ? null : property.GetValue(value, null);
                if (raw is ObjectId) { id = (ObjectId)raw; return !id.IsNull; }
            }
            catch { }
            return false;
        }

        private static bool TryReadPointProperty(object value, string name, out Point3d point)
        {
            point = Point3d.Origin;
            if (value == null) return false;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                object raw = property == null ? null : property.GetValue(value, null);
                if (raw is Point3d) { point = (Point3d)raw; return true; }
            }
            catch { }
            return false;
        }'''
t = rep(t, old, new, 'boq geometric length')
old = '''            double metres = raw / unitsPerMetre;
            if (!IsFinitePositive(metres)) return string.Empty;
            return metres < 2.0
                ? (metres * 1000.0).ToString("N0", CultureInfo.CurrentCulture) + " mm"
                : metres.ToString("N3", CultureInfo.CurrentCulture) + " m";'''
new = '''            double metres = raw / unitsPerMetre;
            if (!IsFinitePositive(metres)) return string.Empty;
            if (metres < 2.0)
            {
                double millimetres = SnapNominalDiameter(metres * 1000.0);
                return millimetres.ToString("N0", CultureInfo.CurrentCulture) + " mm";
            }
            return metres.ToString("N3", CultureInfo.CurrentCulture) + " m";'''
t = rep(t, old, new, 'boq nominal diameter')
marker = '''        private static string GetObjectName(DBObject databaseObject, Transaction transaction)
        {'''
helper = '''        private static double SnapNominalDiameter(double millimetres)
        {
            double value = Math.Abs(millimetres);
            double[] nominal = { 20, 25, 32, 40, 50, 63, 75, 90, 110, 125, 140, 160, 180, 200, 225, 250, 280, 300, 315, 355, 400, 450, 500, 560, 600, 630, 710, 800, 900, 1000, 1200, 1500, 1800, 2000 };
            foreach (double candidate in nominal)
                if (value <= candidate + 0.5) return candidate;
            return Math.Round(value, 0);
        }

'''
t = rep(t, marker, helper + marker, 'boq nominal helper')
write(p, t)

# 8) Network schedules use same robust display rules.
p = 'src/CE.Tools.Civil3D/NetworkAssetScheduleCommands.cs'
t = read(p)
old = '''            double? geometricLength = ReadGeometricLength(value);'''
new = '''            double? geometricLength = ReadGeometricLength(value, transaction);'''
t = rep(t, old, new, 'schedule geometric length call')
old = '''        private static double ToNominalMillimetres(double value)
        {
            double absolute = Math.Abs(value);
            return absolute > 0.0 && absolute < 10.0 ? value * 1000.0 : value;
        }

        private static double? ReadGeometricLength(object value)
        {
            if (value == null) return null;
            try
            {
                MethodInfo method = value.GetType().GetMethod(
                    "GetPointAtParam",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(double) },
                    null);
                if (method == null) return null;
                object first = method.Invoke(value, new object[] { 0.0 });
                object second = method.Invoke(value, new object[] { 1.0 });
                if (!(first is Autodesk.AutoCAD.Geometry.Point3d) ||
                    !(second is Autodesk.AutoCAD.Geometry.Point3d)) return null;
                return ((Autodesk.AutoCAD.Geometry.Point3d)first).DistanceTo(
                    (Autodesk.AutoCAD.Geometry.Point3d)second);
            }
            catch
            {
                return null;
            }
        }'''
new = '''        private static double ToNominalMillimetres(double value)
        {
            double absolute = Math.Abs(value);
            double millimetres = absolute > 0.0 && absolute < 10.0 ? absolute * 1000.0 : absolute;
            double[] nominal = { 20, 25, 32, 40, 50, 63, 75, 90, 110, 125, 140, 160, 180, 200, 225, 250, 280, 300, 315, 355, 400, 450, 500, 560, 600, 630, 710, 800, 900, 1000, 1200, 1500, 1800, 2000 };
            foreach (double candidate in nominal)
                if (millimetres <= candidate + 0.5) return candidate;
            return Math.Round(millimetres, 0);
        }

        private static double? ReadGeometricLength(object value, Transaction transaction)
        {
            if (value == null) return null;
            Autodesk.AutoCAD.Geometry.Point3d firstPoint;
            Autodesk.AutoCAD.Geometry.Point3d secondPoint;
            if (TryReadPointProperty(value, "StartPoint", out firstPoint) &&
                TryReadPointProperty(value, "EndPoint", out secondPoint))
                return firstPoint.DistanceTo(secondPoint);
            try
            {
                PropertyInfo startProperty = value.GetType().GetProperty("StartStructureId", BindingFlags.Public | BindingFlags.Instance);
                PropertyInfo endProperty = value.GetType().GetProperty("EndStructureId", BindingFlags.Public | BindingFlags.Instance);
                if (transaction != null && startProperty != null && endProperty != null)
                {
                    object startRaw = startProperty.GetValue(value, null);
                    object endRaw = endProperty.GetValue(value, null);
                    if (startRaw is ObjectId && endRaw is ObjectId)
                    {
                        DBObject start = transaction.GetObject((ObjectId)startRaw, OpenMode.ForRead, false);
                        DBObject end = transaction.GetObject((ObjectId)endRaw, OpenMode.ForRead, false);
                        if ((TryReadPointProperty(start, "Position", out firstPoint) || TryReadPointProperty(start, "Location", out firstPoint)) &&
                            (TryReadPointProperty(end, "Position", out secondPoint) || TryReadPointProperty(end, "Location", out secondPoint)))
                            return firstPoint.DistanceTo(secondPoint);
                    }
                }
            }
            catch { }
            try
            {
                MethodInfo method = value.GetType().GetMethod(
                    "GetPointAtParam",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(double) },
                    null);
                if (method == null) return null;
                object first = method.Invoke(value, new object[] { 0.0 });
                object second = method.Invoke(value, new object[] { 1.0 });
                if (!(first is Autodesk.AutoCAD.Geometry.Point3d) || !(second is Autodesk.AutoCAD.Geometry.Point3d)) return null;
                return ((Autodesk.AutoCAD.Geometry.Point3d)first).DistanceTo((Autodesk.AutoCAD.Geometry.Point3d)second);
            }
            catch { return null; }
        }

        private static bool TryReadPointProperty(object value, string name, out Autodesk.AutoCAD.Geometry.Point3d point)
        {
            point = Autodesk.AutoCAD.Geometry.Point3d.Origin;
            if (value == null) return false;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                object raw = property == null ? null : property.GetValue(value, null);
                if (raw is Autodesk.AutoCAD.Geometry.Point3d) { point = (Autodesk.AutoCAD.Geometry.Point3d)raw; return true; }
            }
            catch { }
            return false;
        }'''
t = rep(t, old, new, 'schedule nominal and endpoints')
write(p, t)

# 9) Surface repair: direct Civil TIN triangles/vertices and AddVertices API.
p = 'src/CE.Tools.Civil3D/SurfaceSpikeHoleRepairCommands.cs'
t = read(p)
old = '''using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;'''
new = '''using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivilTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;'''
t = rep(t, old, new, 'tin alias')
old = '''                List<Point3d> vertices = ReadVertices(source);
                if (vertices.Count < 3)
                    throw new InvalidOperationException(
                        "The selected surface exposes fewer than three readable vertices.");'''
new = '''                List<TriangleRecord> triangles = ReadTriangles(source);
                List<Point3d> vertices = ReadVertices(source);
                if (vertices.Count < 3 && triangles.Count > 0)
                    vertices = UniqueTriangleVertices(triangles);
                if (vertices.Count < 3)
                    throw new InvalidOperationException(
                        "The selected TIN exposes fewer than three readable vertices. Rebuild the source surface and retry.");'''
t = rep(t, old, new, 'surface derive vertices')
old = '''                List<TriangleRecord> triangles = ReadTriangles(source);
                Dictionary<int, double> replacements = FindSpikeReplacements('''
new = '''                Dictionary<int, double> replacements = FindSpikeReplacements('''
t = rep(t, old, new, 'remove duplicate triangles read')
old = '''        private static List<TriangleRecord> ReadTriangles(object surface)
        {
            object raw = InvokeWithOptionalBoolean(surface, "GetTriangles") ??
                         ReadProperty(surface, "Triangles");'''
new = '''        private static List<TriangleRecord> ReadTriangles(object surface)
        {
            object raw = null;
            CivilTinSurface tin = surface as CivilTinSurface;
            if (tin != null)
            {
                try { raw = tin.GetTriangles(false); }
                catch { raw = null; }
            }
            raw = raw ?? InvokeWithOptionalBoolean(surface, "GetTriangles") ??
                         ReadProperty(surface, "Triangles");'''
t = rep(t, old, new, 'surface direct triangles')
# direct AddVertices first.
old = '''        private static void AddPoints(
            DBObject surface,
            IReadOnlyList<Point3d> points)
        {
            object definition = ReadProperty(surface, "Definition");'''
new = '''        private static void AddPoints(
            DBObject surface,
            IReadOnlyList<Point3d> points)
        {
            CivilTinSurface tin = surface as CivilTinSurface;
            if (tin != null)
            {
                tin.AddVertices(new Point3dCollection(points.ToArray()));
                tin.Rebuild();
                return;
            }
            object definition = ReadProperty(surface, "Definition");'''
t = rep(t, old, new, 'surface direct add vertices')
# helper unique triangle points before CreateTinSurface.
marker = '''        private static ObjectId CreateTinSurface(
            Database database,'''
helper = '''        private static List<Point3d> UniqueTriangleVertices(IEnumerable<TriangleRecord> triangles)
        {
            var result = new List<Point3d>();
            foreach (TriangleRecord triangle in triangles ?? Enumerable.Empty<TriangleRecord>())
            {
                foreach (Point3d point in new[] { triangle.A, triangle.B, triangle.C })
                {
                    if (!result.Any(existing => PlanDistanceSquared(existing, point) <= GeometryTolerance && Math.Abs(existing.Z - point.Z) <= 0.000001))
                        result.Add(point);
                }
            }
            return result;
        }

'''
t = rep(t, marker, helper + marker, 'surface unique triangle vertices')
write(p, t)

# 10) Global CE table centering manager and automatic refresh integration.
p = 'src/CE.Tools.Civil3D/CeTablePresentationCommands.cs'
new_file = '''using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.CeTablePresentationCommands))]

namespace CETools.Civil3D
{
    public sealed class CeTablePresentationCommands
    {
        [CommandMethod("CE_TOOLS", "CE_TABLECENTERALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CenterAll()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            int changed = CeTablePresentationManager.CenterCeTables(document);
            document.Editor.Regen();
            document.Editor.WriteMessage("\\nCE_TABLECENTERALL complete. CE tables centered={0}.", changed);
        }
    }

    internal static class CeTablePresentationManager
    {
        internal static int CenterCeTables(Document document)
        {
            if (document == null) return 0;
            int changed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTable blocks = transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
                if (blocks == null) return 0;
                foreach (ObjectId blockId in blocks)
                {
                    BlockTableRecord space;
                    try { space = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord; }
                    catch { continue; }
                    if (space == null || space.LayoutId.IsNull) continue;
                    foreach (ObjectId id in space)
                    {
                        Table table;
                        try { table = transaction.GetObject(id, OpenMode.ForRead, false) as Table; }
                        catch { continue; }
                        if (table == null || !IsCeTable(table)) continue;
                        table.UpgradeOpen();
                        for (int row = 0; row < table.Rows.Count; row++)
                            for (int column = 0; column < table.Columns.Count; column++)
                                table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;
                        try { table.GenerateLayout(); } catch { }
                        try { table.RecordGraphicsModified(true); } catch { }
                        changed++;
                    }
                }
                transaction.Commit();
            }
            return changed;
        }

        private static bool IsCeTable(Table table)
        {
            if (table == null || table.Rows.Count == 0 || table.Columns.Count == 0) return false;
            string title;
            try { title = table.Cells[0, 0].TextString ?? string.Empty; }
            catch { return false; }
            string upper = title.ToUpperInvariant();
            return upper.StartsWith("CE ") || upper.StartsWith("CE-") || upper.Contains("CE TOOLS") ||
                   upper.StartsWith("LINKED ") || upper.StartsWith("FEATURE LINE") || upper.StartsWith("POLYLINE ");
        }
    }
}
'''
write(p, new_file)

p = 'src/CE.Tools.Civil3D/UniversalDynamicRefreshCommands.cs'
t = read(p)
old = '''                try { result.MetadataAttributes += ProductionMetadataDynamicManager.Refresh(document); }
                catch { result.Warnings++; }'''
new = old + '''
                try { CeTablePresentationManager.CenterCeTables(document); }
                catch { result.Warnings++; }'''
t = rep(t, old, new, 'automatic table centering')
write(p, t)

print('Coordinate/levels/BOQ/surface runtime patch applied.')
