using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.August11SurveyRuntimeCommands))]

namespace CETools.Civil3D
{
    public sealed class August11SurveyRuntimeCommands
    {
        private const string InitialOffsetKey = "CE_COGO_LABEL_INITIAL_OFFSET";
        private const string MultiSurfaceKey = "CE_COORD_MULTI_SURFACE_TABLE";

        [CommandMethod("CE_TOOLS", "CE_COGOLABELRESTOREINITIAL", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RestoreInitialCogoLabels()
        {
            Document document = Active();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Restore COGO Initial Label Positions",
                "Return COGO labels to the first CE-stored label offset. Survey point coordinates are never changed.");
            model.AddChoice("Scope", "Restore", "COGO points", "All", "Restore all stored COGO label positions or only selected points.", new[] { "All", "Selected" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            HashSet<ObjectId> selected = null;
            if (string.Equals(model.Text("Scope"), "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect COGO points whose labels should return to their initial positions: ", AllowDuplicates = false });
                if (selection.Status != PromptStatus.OK || selection.Value == null) return;
                selected = new HashSet<ObjectId>(selection.Value.GetObjectIds());
            }
            int restored = RestoreCogoLabels(document, selected);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_COGOLABELRESTOREINITIAL complete. COGO labels restored={0}; point coordinates unchanged.", restored);
        }

        [CommandMethod("CE_TOOLS", "CE_COORDMULTISURFACETABLE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void MultiSurfaceCoordinateTable()
        {
            Document document = Active();
            if (document == null) return;
            PromptSelectionResult surfaceSelection = document.Editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect MULTIPLE Civil 3D surfaces for the coordinate comparison table: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (surfaceSelection.Status != PromptStatus.OK || surfaceSelection.Value == null) return;
            List<ObjectId> surfaces = FilterSurfaces(document.Database, surfaceSelection.Value.GetObjectIds());
            if (surfaces.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_COORDMULTISURFACETABLE: no Civil 3D surfaces were selected.");
                return;
            }

            PromptSelectionResult pointSelection = document.Editor.SelectImplied();
            if (pointSelection.Status != PromptStatus.OK || pointSelection.Value == null || pointSelection.Value.Count == 0)
            {
                pointSelection = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect COGO/AutoCAD points to compare against the selected surfaces: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            }
            if (pointSelection.Status != PromptStatus.OK || pointSelection.Value == null) return;
            List<ObjectId> points = FilterPoints(document.Database, pointSelection.Value.GetObjectIds());
            if (points.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_COORDMULTISURFACETABLE: no COGO/DBPoint sources were selected.");
                return;
            }

            PromptPointResult insertion = document.Editor.GetPoint("\nInsertion point for linked multi-surface coordinate table: ");
            if (insertion.Status != PromptStatus.OK) return;
            ObjectId tableId = ObjectId.Null;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (space == null) return;
                Table table = BuildMultiSurfaceTable(document.Database, transaction, points, surfaces, insertion.Value);
                if (table == null) return;
                space.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                WriteMultiSurfaceLink(table, transaction, points, surfaces);
                tableId = table.ObjectId;
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_COORDMULTISURFACETABLE complete. Points={0}; surfaces={1}; linked table={2}.", points.Count, surfaces.Count, tableId.Handle.ToString());
        }

        [CommandMethod("CE_TOOLS", "CE_COORDMULTISURFACEREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshMultiSurfaceTables()
        {
            Document document = Active();
            if (document == null) return;
            int count = RefreshMultiSurfaceTables(document);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_COORDMULTISURFACEREFRESH complete. Linked tables refreshed={0}.", count);
        }

        internal static void SyncProjectLocation(Document document, string town, string coordinateSystem)
        {
            if (document == null) return;
            try
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(town)) values["Town"] = town.Trim();
                if (!string.IsNullOrWhiteSpace(coordinateSystem)) values["Coordinate System"] = coordinateSystem.Trim();
                if (values.Count == 0) return;
                ProjectSetupCommands.MergeSharedProjectMetadata(document.Database, values);
                ProjectSetupCommands.RefreshInformationTables(document);
                ProductionMetadataDynamicManager.Refresh(document);
            }
            catch { }
        }

        internal static void CaptureCogoInitialOffsets(Document document)
        {
            if (document == null) return;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in ReadCogoIds(civilDocument))
                    {
                        CivilCogoPoint point;
                        try { point = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilCogoPoint; }
                        catch { continue; }
                        if (point == null || HasRecord(point, transaction, InitialOffsetKey)) continue;
                        Point3d anchor = PointLocation(point);
                        Point3d label;
                        try { label = point.LabelLocation; }
                        catch { continue; }
                        Vector3d offset = label - anchor;
                        WriteVectorRecord(point, transaction, InitialOffsetKey, offset);
                    }
                    transaction.Commit();
                }
            }
            catch { }
        }

        internal static int RestoreCogoLabels(Document document, ISet<ObjectId> selected)
        {
            if (document == null) return 0;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return 0;
            int restored = 0;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in ReadCogoIds(civilDocument))
                    {
                        if (selected != null && !selected.Contains(id)) continue;
                        CivilCogoPoint point;
                        try { point = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilCogoPoint; }
                        catch { continue; }
                        if (point == null) continue;
                        Vector3d offset;
                        if (!TryReadVectorRecord(point, transaction, InitialOffsetKey, out offset)) continue;
                        try
                        {
                            point.LabelLocation = PointLocation(point) + offset;
                            restored++;
                        }
                        catch { }
                    }
                    transaction.Commit();
                }
            }
            catch { }
            return restored;
        }

        internal static int RefreshMultiSurfaceTables(Document document)
        {
            if (document == null) return 0;
            int refreshed = 0;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                    if (space == null) return 0;
                    foreach (ObjectId id in space)
                    {
                        Table table;
                        try { table = transaction.GetObject(id, OpenMode.ForWrite, false) as Table; }
                        catch { continue; }
                        if (table == null) continue;
                        List<ObjectId> points;
                        List<ObjectId> surfaces;
                        if (!TryReadMultiSurfaceLink(table, transaction, out points, out surfaces)) continue;
                        UpdateMultiSurfaceTable(document.Database, transaction, table, points, surfaces);
                        refreshed++;
                    }
                    transaction.Commit();
                }
            }
            catch { }
            return refreshed;
        }

        private static Table BuildMultiSurfaceTable(Database database, Transaction transaction, IList<ObjectId> points, IList<ObjectId> surfaces, Point3d insertion)
        {
            var table = new Table();
            table.SetDatabaseDefaults(database);
            table.Position = insertion;
            table.SetSize(points.Count + 2, surfaces.Count + 4);
            double text = Math.Max(PaperAnnotationScale.ModelTextHeight(database, 2.0), 0.001);
            double rowHeight = Math.Max(text * 1.8, 0.001);
            table.Cells[0, 0].TextString = "LINKED MULTI-SURFACE COORDINATE REGISTER";
            table.MergeCells(CellRange.Create(table, 0, 0, 0, surfaces.Count + 3));
            string[] headers = { "POINT", "X / EASTING", "Y / NORTHING", "POINT Z" };
            for (int col = 0; col < headers.Length; col++) table.Cells[1, col].TextString = headers[col];
            for (int surfaceIndex = 0; surfaceIndex < surfaces.Count; surfaceIndex++)
            {
                CivilSurface surface;
                try { surface = transaction.GetObject(surfaces[surfaceIndex], OpenMode.ForRead, false) as CivilSurface; }
                catch { surface = null; }
                table.Cells[1, 4 + surfaceIndex].TextString = surface == null ? "SURFACE " + (surfaceIndex + 1).ToString(CultureInfo.InvariantCulture) : surface.Name;
            }
            for (int row = 0; row < table.Rows.Count; row++)
            {
                table.Rows[row].Height = rowHeight;
                for (int col = 0; col < table.Columns.Count; col++)
                {
                    table.Cells[row, col].Alignment = CellAlignment.MiddleCenter;
                    try { table.Cells[row, col].TextHeight = text; } catch { }
                }
            }
            for (int col = 0; col < table.Columns.Count; col++) table.Columns[col].Width = Math.Max(text * (col == 0 ? 8.0 : 9.5), 1.0);
            UpdateMultiSurfaceTable(database, transaction, table, points, surfaces);
            return table;
        }

        private static void UpdateMultiSurfaceTable(Database database, Transaction transaction, Table table, IList<ObjectId> points, IList<ObjectId> surfaces)
        {
            if (table == null) return;
            int requiredRows = points.Count + 2;
            int requiredColumns = surfaces.Count + 4;
            if (table.Rows.Count != requiredRows || table.Columns.Count != requiredColumns)
            {
                // Do not resize a user-edited table destructively. The command can
                // be rerun if its linked source set intentionally changes.
                return;
            }
            for (int rowIndex = 0; rowIndex < points.Count; rowIndex++)
            {
                PointSource source;
                if (!TryReadPoint(transaction, points[rowIndex], out source)) continue;
                int row = rowIndex + 2;
                table.Cells[row, 0].TextString = source.Name;
                table.Cells[row, 1].TextString = source.Location.X.ToString("0.000", CultureInfo.InvariantCulture);
                table.Cells[row, 2].TextString = source.Location.Y.ToString("0.000", CultureInfo.InvariantCulture);
                table.Cells[row, 3].TextString = source.Location.Z.ToString("0.000", CultureInfo.InvariantCulture);
                for (int surfaceIndex = 0; surfaceIndex < surfaces.Count; surfaceIndex++)
                {
                    CivilSurface surface;
                    try { surface = transaction.GetObject(surfaces[surfaceIndex], OpenMode.ForRead, false) as CivilSurface; }
                    catch { surface = null; }
                    double elevation = double.NaN;
                    if (surface != null)
                    {
                        try { elevation = surface.FindElevationAtXY(source.Location.X, source.Location.Y); }
                        catch { }
                    }
                    table.Cells[row, 4 + surfaceIndex].TextString = double.IsNaN(elevation) || double.IsInfinity(elevation) ? "N/A" : elevation.ToString("0.000", CultureInfo.InvariantCulture);
                }
            }
            try { table.GenerateLayout(); } catch { }
            try { table.RecordGraphicsModified(true); } catch { }
        }

        private static void WriteMultiSurfaceLink(Table table, Transaction transaction, IEnumerable<ObjectId> points, IEnumerable<ObjectId> surfaces)
        {
            if (table.ExtensionDictionary.IsNull) table.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            var values = new List<TypedValue>();
            values.Add(new TypedValue((int)DxfCode.Text, "POINTS"));
            foreach (ObjectId id in points) values.Add(new TypedValue((int)DxfCode.Text, id.Handle.ToString()));
            values.Add(new TypedValue((int)DxfCode.Text, "SURFACES"));
            foreach (ObjectId id in surfaces) values.Add(new TypedValue((int)DxfCode.Text, id.Handle.ToString()));
            var record = new Xrecord { Data = new ResultBuffer(values.ToArray()) };
            dictionary.SetAt(MultiSurfaceKey, record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static bool TryReadMultiSurfaceLink(Table table, Transaction transaction, out List<ObjectId> points, out List<ObjectId> surfaces)
        {
            points = new List<ObjectId>();
            surfaces = new List<ObjectId>();
            if (table == null || table.ExtensionDictionary.IsNull) return false;
            try
            {
                DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                if (dictionary == null || !dictionary.Contains(MultiSurfaceKey)) return false;
                Xrecord record = transaction.GetObject(dictionary.GetAt(MultiSurfaceKey), OpenMode.ForRead, false) as Xrecord;
                if (record == null || record.Data == null) return false;
                string mode = string.Empty;
                foreach (TypedValue value in record.Data)
                {
                    string text = Convert.ToString(value.Value, CultureInfo.InvariantCulture);
                    if (string.Equals(text, "POINTS", StringComparison.OrdinalIgnoreCase) || string.Equals(text, "SURFACES", StringComparison.OrdinalIgnoreCase))
                    {
                        mode = text;
                        continue;
                    }
                    ObjectId id;
                    if (!TryResolveHandle(table.Database, text, out id)) continue;
                    if (string.Equals(mode, "POINTS", StringComparison.OrdinalIgnoreCase)) points.Add(id);
                    else if (string.Equals(mode, "SURFACES", StringComparison.OrdinalIgnoreCase)) surfaces.Add(id);
                }
                return points.Count > 0 && surfaces.Count > 0;
            }
            catch { return false; }
        }

        private static bool TryResolveHandle(Database database, string text, out ObjectId id)
        {
            id = ObjectId.Null;
            if (database == null || string.IsNullOrWhiteSpace(text)) return false;
            long value;
            if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return false;
            try
            {
                id = database.GetObjectId(false, new Handle(value), 0);
                return !id.IsNull && !id.IsErased;
            }
            catch { return false; }
        }

        private static List<ObjectId> FilterSurfaces(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids.Where(value => !value.IsNull && !value.IsErased).Distinct())
                {
                    try { if (transaction.GetObject(id, OpenMode.ForRead, false) is CivilSurface) result.Add(id); }
                    catch { }
                }
            }
            return result;
        }

        private static List<ObjectId> FilterPoints(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids.Where(value => !value.IsNull && !value.IsErased).Distinct())
                {
                    DBObject value;
                    try { value = transaction.GetObject(id, OpenMode.ForRead, false); }
                    catch { continue; }
                    if (value is CivilCogoPoint || value is DBPoint) result.Add(id);
                }
            }
            return result;
        }

        private static bool TryReadPoint(Transaction transaction, ObjectId id, out PointSource source)
        {
            source = new PointSource();
            DBObject value;
            try { value = transaction.GetObject(id, OpenMode.ForRead, false); }
            catch { return false; }
            CivilCogoPoint cogo = value as CivilCogoPoint;
            if (cogo != null)
            {
                source.Name = string.IsNullOrWhiteSpace(cogo.PointName) ? "P" + cogo.PointNumber.ToString(CultureInfo.InvariantCulture) : cogo.PointName;
                source.Location = PointLocation(cogo);
                return true;
            }
            DBPoint point = value as DBPoint;
            if (point != null)
            {
                source.Name = "DBPOINT-" + id.Handle.ToString();
                source.Location = point.Position;
                return true;
            }
            return false;
        }

        private static List<ObjectId> ReadCogoIds(CivilDocument civilDocument)
        {
            var result = new List<ObjectId>();
            if (civilDocument == null) return result;
            foreach (object value in CivilStyleDiscovery.Enumerate(civilDocument.CogoPoints))
            {
                if (value is ObjectId)
                {
                    ObjectId id = (ObjectId)value;
                    if (!id.IsNull && !id.IsErased) result.Add(id);
                }
                else
                {
                    DBObject dbObject = value as DBObject;
                    if (dbObject != null && !dbObject.ObjectId.IsNull) result.Add(dbObject.ObjectId);
                }
            }
            return result.Distinct().ToList();
        }

        private static Point3d PointLocation(CivilCogoPoint point)
        {
            return new Point3d(point.Easting, point.Northing, point.Elevation);
        }

        private static bool HasRecord(DBObject value, Transaction transaction, string key)
        {
            if (value == null || value.ExtensionDictionary.IsNull) return false;
            try
            {
                DBDictionary dictionary = transaction.GetObject(value.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                return dictionary != null && dictionary.Contains(key);
            }
            catch { return false; }
        }

        private static void WriteVectorRecord(DBObject value, Transaction transaction, string key, Vector3d vector)
        {
            if (value.ExtensionDictionary.IsNull) value.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(value.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            Xrecord record;
            if (dictionary.Contains(key)) record = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForWrite, false) as Xrecord;
            else
            {
                record = new Xrecord();
                dictionary.SetAt(key, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            if (record != null) record.Data = new ResultBuffer(new TypedValue((int)DxfCode.Real, vector.X), new TypedValue((int)DxfCode.Real, vector.Y), new TypedValue((int)DxfCode.Real, vector.Z));
        }

        private static bool TryReadVectorRecord(DBObject value, Transaction transaction, string key, out Vector3d vector)
        {
            vector = Vector3d.Zero;
            if (!HasRecord(value, transaction, key)) return false;
            try
            {
                DBDictionary dictionary = transaction.GetObject(value.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                Xrecord record = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForRead, false) as Xrecord;
                if (record == null || record.Data == null) return false;
                TypedValue[] values = record.Data.AsArray();
                if (values.Length < 2) return false;
                double x = Convert.ToDouble(values[0].Value, CultureInfo.InvariantCulture);
                double y = Convert.ToDouble(values[1].Value, CultureInfo.InvariantCulture);
                double z = values.Length > 2 ? Convert.ToDouble(values[2].Value, CultureInfo.InvariantCulture) : 0.0;
                vector = new Vector3d(x, y, z);
                return true;
            }
            catch { return false; }
        }

        private static Document Active() { return AcApplication.DocumentManager.MdiActiveDocument; }

        private struct PointSource
        {
            internal string Name;
            internal Point3d Location;
        }
    }

    /// <summary>
    /// Runs the immediate post-command repairs requested during field testing.
    /// The work is deferred to Application.Idle so Civil 3D has committed the
    /// command transaction before CE Tools styles/refreshes the generated output.
    /// </summary>
    internal static class August11SurveyRuntimeManager
    {
        private static Document _document;
        private static bool _initialised;
        private static bool _pendingSettingOutRefresh;
        private static bool _pendingMultiSurfaceRefresh;
        private static bool _busy;

        internal static void Initialize()
        {
            if (_initialised) return;
            _initialised = true;
            AcApplication.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcApplication.DocumentManager.DocumentCreated += OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed += OnDocumentDestroyed;
            AcApplication.Idle += OnIdle;
            Attach(AcApplication.DocumentManager.MdiActiveDocument);
        }

        internal static void Terminate()
        {
            if (!_initialised) return;
            _initialised = false;
            AcApplication.DocumentManager.DocumentActivated -= OnDocumentActivated;
            AcApplication.DocumentManager.DocumentCreated -= OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed -= OnDocumentDestroyed;
            AcApplication.Idle -= OnIdle;
            Detach();
        }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e) { Attach(e == null ? null : e.Document); }
        private static void OnDocumentDestroyed(object sender, DocumentCollectionEventArgs e) { if (e != null && ReferenceEquals(e.Document, _document)) Detach(); }

        private static void Attach(Document document)
        {
            if (ReferenceEquals(document, _document)) return;
            Detach();
            _document = document;
            if (_document == null) return;
            _document.CommandWillStart += OnCommandWillStart;
            _document.CommandEnded += OnCommandEnded;
        }

        private static void Detach()
        {
            if (_document != null)
            {
                _document.CommandWillStart -= OnCommandWillStart;
                _document.CommandEnded -= OnCommandEnded;
            }
            _document = null;
            _pendingSettingOutRefresh = false;
            _pendingMultiSurfaceRefresh = false;
        }

        private static void OnCommandWillStart(object sender, CommandEventArgs e)
        {
            if (_busy || _document == null || e == null) return;
            string command = Normalize(e.GlobalCommandName);
            if (command == "CE_OVERLAPSMART" || command == "CE_COGOOVERLAPFIX")
                August11SurveyRuntimeCommands.CaptureCogoInitialOffsets(_document);
        }

        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            if (_busy || e == null) return;
            string command = Normalize(e.GlobalCommandName);
            if (command.IndexOf("VERTEXSETTINGOUT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                command.IndexOf("JUNCTIONSETTINGOUT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                command == "CE_GRIDSETTINGOUT" || command == "CE_PLATFORMSETTINGOUT")
                _pendingSettingOutRefresh = true;
            if (command.StartsWith("CE_", StringComparison.OrdinalIgnoreCase) ||
                command.IndexOf("MOVE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                command.IndexOf("STRETCH", StringComparison.OrdinalIgnoreCase) >= 0 ||
                command.IndexOf("GRIP", StringComparison.OrdinalIgnoreCase) >= 0)
                _pendingMultiSurfaceRefresh = true;
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            if (_busy || _document == null || (!_pendingSettingOutRefresh && !_pendingMultiSurfaceRefresh)) return;
            if (!ReferenceEquals(AcApplication.DocumentManager.MdiActiveDocument, _document)) return;
            string commands = Convert.ToString(AcApplication.GetSystemVariable("CMDNAMES"), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commands)) return;
            _busy = true;
            try
            {
                if (_pendingSettingOutRefresh)
                {
                    _pendingSettingOutRefresh = false;
                    try { CogoPointProjectStyleCommands.ApplySelectedStyles(_document, false); } catch { }
                    try { VertexSettingOutCommands.RefreshAll(_document); } catch { }
                    try { FinalFeatureLineReportCommands.RefreshAll(_document); } catch { }
                    try { _document.Editor.Regen(); } catch { }
                }
                if (_pendingMultiSurfaceRefresh)
                {
                    _pendingMultiSurfaceRefresh = false;
                    try { August11SurveyRuntimeCommands.RefreshMultiSurfaceTables(_document); } catch { }
                }
            }
            finally { _busy = false; }
        }

        private static string Normalize(string value) { return (value ?? string.Empty).Trim().TrimStart('.', '_').ToUpperInvariant(); }
    }
}
