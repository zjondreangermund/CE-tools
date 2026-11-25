using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Wpf = System.Windows;
using WpfControls = System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.DrawingSheetImportCommands))]

namespace CETools.Civil3D
{
    /// <summary>Imports Paper Space layouts, floating viewports and optional Model Space content.</summary>
    public sealed class DrawingSheetImportCommands
    {
        [CommandMethod("CE_TOOLS", "CE_IMPORTSHEETS", CommandFlags.Modal)]
        public void ImportSheets()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            try
            {
                string path;
                using (var file = new OpenFileDialog
                {
                    Title = "CE Tools - Select source drawing",
                    Filter = "AutoCAD drawings (*.dwg)|*.dwg",
                    CheckFileExists = true,
                    Multiselect = false,
                    RestoreDirectory = true,
                    FileName = File.Exists(ReadTransferSetting("SourcePath")) ? ReadTransferSetting("SourcePath") : string.Empty
                })
                {
                    if (file.ShowDialog() != DialogResult.OK) return;
                    path = Path.GetFullPath(file.FileName);
                    WriteTransferSetting("SourcePath", path);
                }
                if (!string.IsNullOrEmpty(document.Database.Filename) &&
                    string.Equals(path, Path.GetFullPath(document.Database.Filename), StringComparison.OrdinalIgnoreCase))
                {
                    Show("Choose a different drawing. The source cannot be the active drawing.");
                    return;
                }

                // A side database reads the saved file only. It never creates an MDI document.
                using (var source = new Database(false, true))
                {
                    source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
                    source.CloseInput(true);
                    List<Sheet> sheets = ReadSheets(source);
                    if (sheets.Count == 0)
                    {
                        Show("This drawing contains no paper-space layouts.");
                        return;
                    }
                    var picker = new SheetPicker(path, sheets, ReadTransferSetting("ImportedLayouts"), ReadTransferSetting("TransferMode"));
                    {
                        if (AcApplication.ShowModalWindow(picker) != true) return;
                        List<Sheet> selected = picker.SelectedSheets;
                        if (selected == null || selected.Count == 0) return;
                        WriteTransferSetting("ImportedLayouts", string.Join("|", selected.Select(x => x.Name)));
                        WriteTransferSetting("TransferMode", picker.SelectedTransferMode);

                        ImportResult result;
                        using (document.LockDocument())
                        {
                            if (string.Equals(picker.SelectedTransferMode, "MODEL", StringComparison.OrdinalIgnoreCase))
                            {
                                // Import source tabs and every source viewport, plus Model Space once.
                                // Do not import title blocks or other non-viewport Paper Space objects.
                                result = CopySheets(source, document.Database, selected,
                                    includeModelSpace: true, includePaperSpaceEntities: false, includeViewports: true);
                            }
                            else if (string.Equals(picker.SelectedTransferMode, "TITLE", StringComparison.OrdinalIgnoreCase))
                            {
                                // Import source tabs directly even when their names do not exist in the
                                // destination. Bring across all Paper Space objects and viewports, but
                                // do not clone source Model Space content.
                                result = CopySheets(source, document.Database, selected,
                                    includeModelSpace: false, includePaperSpaceEntities: true, includeViewports: false);
                            }
                            else
                            {
                                // Complete transfer: layouts, all Paper Space entities, all viewports,
                                // and Model Space at the source drawing's original coordinates.
                                result = CopySheets(source, document.Database, selected,
                                    includeModelSpace: true, includePaperSpaceEntities: true, includeViewports: true);
                            }
                        }

                        try { document.Editor.Regen(); } catch { }
                        ZoomExtentsInImportedLayouts(document, result.ImportedLayoutNames);

                        string modeDescription =
                            string.Equals(picker.SelectedTransferMode, "MODEL", StringComparison.OrdinalIgnoreCase)
                                ? "Model Space plus source viewport frames (no title blocks or other Paper Space objects)"
                                : string.Equals(picker.SelectedTransferMode, "TITLE", StringComparison.OrdinalIgnoreCase)
                                    ? "Source Paper Space tabs and title-block/Paper Space entities only (no source Model Space or viewports)"
                                    : "Complete Paper Space and Model Space transfer";

                        Show(modeDescription + " completed.\\n\\n" +
                            "Layouts imported: " + result.ImportedLayoutNames.Count +
                            "\\nPaper Space objects copied: " + result.PaperCount +
                            "\\nSource floating viewports found: " + result.ViewportCount +
                            "\\nFloating viewports created: " + result.ViewportCreatedCount +
                            "\\nModel Space objects copied: " + result.ModelCount +
                            "\\n\\n" + string.Join("\\n", result.Names) +
                            (result.ViewportErrors.Count > 0
                                ? "\\n\\nViewport creation issues:\\n" + string.Join("\\n", result.ViewportErrors)
                                : string.Empty) +
                            "\\n\\nFor complete/model-only transfers, source viewport positions, sizes, view centres, directions, scales, twist angles and lock states are retained. " +
                            "Title-block-only transfer creates the source tabs directly and copies only their non-viewport Paper Space entities. " +
                            "Destination layout names are made unique if a source tab name already exists.");
                    }
                }
            }
            catch (System.Exception error)
            {
                Show("The sheets could not be imported. No import transaction was committed.\n\n" +
                    error.GetBaseException().Message, MessageBoxIcon.Error);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_COPYPAPERSPACETABS", CommandFlags.Modal)]
        public void CopyPaperSpaceTabs()
        {
            ImportSheets();
        }


        [CommandMethod("CE_TOOLS", "CE_COPYVIEWPORTCONTENT", CommandFlags.Modal)]
        public void CopyViewportContentToModelSpace()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            try
            {
                string path = ReadTransferSetting("SourcePath");
                using (var file = new OpenFileDialog
                {
                    Title = "CE Tools - Select source drawing",
                    Filter = "AutoCAD drawings (*.dwg)|*.dwg",
                    CheckFileExists = true, Multiselect = false, RestoreDirectory = true,
                    FileName = File.Exists(path) ? path : string.Empty
                })
                {
                    if (file.ShowDialog() != DialogResult.OK) return;
                    path = Path.GetFullPath(file.FileName);
                }
                if (!string.IsNullOrEmpty(document.Database.Filename) &&
                    string.Equals(path, Path.GetFullPath(document.Database.Filename), StringComparison.OrdinalIgnoreCase))
                {
                    Show("Choose a different source drawing.");
                    return;
                }

                using (var source = new Database(false, true))
                {
                    source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
                    source.CloseInput(true);
                    List<Sheet> sheets = ReadSheets(source);
                    if (sheets.Count == 0) { Show("The source drawing contains no Paper Space tabs."); return; }
                    var picker = new SheetPicker(path, sheets, ReadTransferSetting("ViewportLayouts"), "MODEL");
                    if (AcApplication.ShowModalWindow(picker) != true) return;
                    List<Sheet> selected = picker.SelectedSheets;
                    if (selected == null || selected.Count == 0) return;
                    WriteTransferSetting("SourcePath", path);
                    WriteTransferSetting("ViewportLayouts", string.Join("|", selected.Select(x => x.Name)));

                    int copied;
                    using (document.LockDocument())
                        copied = CopyVisibleViewportModelContent(source, document.Database, selected);
                    try { document.Editor.Regen(); } catch { }
                    Show("Autodesk.AutoCAD.DatabaseServices.Viewport-content transfer completed.\n\nSource layouts: " + selected.Count +
                        "\nViewports examined: every active viewport on the selected layouts" +
                        "\nModel-space objects copied: " + copied +
                        "\n\nObjects are copied at their original Model Space coordinates. Paper Space objects, title blocks and viewport frames are not copied. Entities are copied whole when their extents intersect a viewport view area; custom clipping boundaries and per-viewport frozen-layer visibility are not used as trimming operations.");
                }
            }
            catch (System.Exception ex)
            {
                Show("Autodesk.AutoCAD.DatabaseServices.Viewport-content transfer failed. " + ex.GetBaseException().Message, MessageBoxIcon.Error);
            }
        }

        private static int CopyVisibleViewportModelContent(Database source, Database destination, IList<Sheet> sheets)
        {
            if (sheets == null || sheets.Count == 0)
                throw new InvalidOperationException("Select at least one source Paper Space tab.");

            ObjectIdCollection modelEntityIds = new ObjectIdCollection();
            int viewportCount = 0;
            using (Transaction read = source.TransactionManager.StartTransaction())
            {
                BlockTable sourceBlocks = (BlockTable)read.GetObject(source.BlockTableId, OpenMode.ForRead);
                ObjectId sourceModelId = sourceBlocks[BlockTableRecord.ModelSpace];
                BlockTableRecord sourceModel = (BlockTableRecord)read.GetObject(sourceModelId, OpenMode.ForRead);

                foreach (ObjectId id in sourceModel)
                {
                    if (id.IsNull || id.IsErased) continue;
                    Entity entity = read.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity != null && !entity.IsErased)
                        modelEntityIds.Add(id);
                }

                // Validate that selected layouts really contain floating viewports.
                // Do not use a calculated viewport polygon to filter Model Space entities:
                // DCS/WCS transforms and clipping can incorrectly reject every entity.
                foreach (Sheet sheet in sheets)
                {
                    Layout layout = read.GetObject(sheet.Id, OpenMode.ForRead, false) as Layout;
                    if (layout == null) continue;
                    BlockTableRecord paper = read.GetObject(layout.BlockTableRecordId, OpenMode.ForRead, false) as BlockTableRecord;
                    if (paper == null) continue;

                    bool skippedOverallViewport = false;
                    foreach (ObjectId paperId in paper)
                    {
                        Autodesk.AutoCAD.DatabaseServices.Viewport viewport =
                            read.GetObject(paperId, OpenMode.ForRead, false) as Autodesk.AutoCAD.DatabaseServices.Viewport;
                        if (viewport == null) continue;

                        // Match the import routine: skip only the first overall viewport (#1).
                        // This also handles side-database files that report Number=1 for
                        // additional viewport entities.
                        bool isOverallViewport = viewport.Number == 1 && !skippedOverallViewport;
                        if (viewport.Number == 1)
                            skippedOverallViewport = true;

                        if (!isOverallViewport)
                            viewportCount++;
                    }
                }
                read.Commit();
            }

            if (viewportCount == 0)
                throw new InvalidOperationException(
                    "No active floating viewports were found on the selected source Paper Space tabs. " +
                    "Choose layouts that contain viewports, or use Complete Paper Space transfer.");

            if (modelEntityIds.Count == 0)
                throw new InvalidOperationException("The source drawing's Model Space contains no entities to copy.");

            using (Transaction write = destination.TransactionManager.StartTransaction())
            {
                BlockTable targetBlocks = (BlockTable)write.GetObject(destination.BlockTableId, OpenMode.ForRead);
                ObjectId targetModel = targetBlocks[BlockTableRecord.ModelSpace];

                using (var mapping = new IdMapping())
                {
                    source.WblockCloneObjects(
                        modelEntityIds,
                        targetModel,
                        mapping,
                        DuplicateRecordCloning.MangleName,
                        false);

                    int copied = 0;
                    var failures = new List<string>();
                    foreach (ObjectId id in modelEntityIds)
                    {
                        IdPair pair = mapping[id];
                        if (!pair.IsCloned || pair.Value.IsNull || pair.Value.IsErased)
                            failures.Add(id.Handle.ToString());
                        else
                            copied++;
                    }

                    if (failures.Count > 0)
                        throw new InvalidOperationException(
                            "Only " + copied + " of " + modelEntityIds.Count +
                            " Model Space entities were copied. Failed source handles: " +
                            string.Join(", ", failures.Take(20)));

                    write.Commit();
                    return copied;
                }
            }
        }

        private static List<Point3d> GetViewportModelPolygon(Autodesk.AutoCAD.DatabaseServices.Viewport viewport)
        {
            double halfWidth = viewport.Width * 0.5;
            double halfHeight = viewport.Height * 0.5;
            var paperCorners = new[]
            {
                new Point2d(viewport.CenterPoint.X-halfWidth, viewport.CenterPoint.Y-halfHeight),
                new Point2d(viewport.CenterPoint.X+halfWidth, viewport.CenterPoint.Y-halfHeight),
                new Point2d(viewport.CenterPoint.X+halfWidth, viewport.CenterPoint.Y+halfHeight),
                new Point2d(viewport.CenterPoint.X-halfWidth, viewport.CenterPoint.Y+halfHeight)
            };
            var result = new List<Point3d>();
            foreach (Point2d paperPoint in paperCorners)
            {
                double scale = Math.Abs(viewport.CustomScale) < 1e-12 ? 1.0 : viewport.CustomScale;
                double dcsX = viewport.ViewCenter.X + (paperPoint.X - viewport.CenterPoint.X) / scale;
                double dcsY = viewport.ViewCenter.Y + (paperPoint.Y - viewport.CenterPoint.Y) / scale;
                Point3d dcs = new Point3d(dcsX, dcsY, 0.0);
                Matrix3d dcsToWcs = Matrix3d.PlaneToWorld(viewport.ViewDirection);
                dcsToWcs = Matrix3d.Displacement(viewport.ViewTarget - Point3d.Origin) * dcsToWcs;
                dcsToWcs = Matrix3d.Rotation(-viewport.TwistAngle, viewport.ViewDirection, viewport.ViewTarget) * dcsToWcs;
                result.Add(dcs.TransformBy(dcsToWcs));
            }
            return result;
        }

        private static bool ExtentsIntersectsViewport(Extents3d ext, IList<Point3d> polygon)
        {
            double minX = Math.Min(ext.MinPoint.X, ext.MaxPoint.X), maxX = Math.Max(ext.MinPoint.X, ext.MaxPoint.X);
            double minY = Math.Min(ext.MinPoint.Y, ext.MaxPoint.Y), maxY = Math.Max(ext.MinPoint.Y, ext.MaxPoint.Y);
            double pMinX = polygon.Min(p => p.X), pMaxX = polygon.Max(p => p.X);
            double pMinY = polygon.Min(p => p.Y), pMaxY = polygon.Max(p => p.Y);
            if (maxX < pMinX || minX > pMaxX || maxY < pMinY || minY > pMaxY) return false;
            Point2d[] box = { new Point2d(minX,minY), new Point2d(maxX,minY), new Point2d(maxX,maxY), new Point2d(minX,maxY) };
            if (box.Any(p => PointInPolygon(p, polygon))) return true;
            if (polygon.Any(p => p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY)) return true;
            for (int i=0;i<4;i++)
                for (int j=0;j<4;j++)
                    if (SegmentsIntersect(box[i], box[(i+1)%4],
                        new Point2d(polygon[j].X,polygon[j].Y), new Point2d(polygon[(j+1)%4].X,polygon[(j+1)%4].Y))) return true;
            return false;
        }

        private static bool PointInPolygon(Point2d point, IList<Point3d> polygon)
        {
            bool inside = false;
            for (int i=0,j=polygon.Count-1;i<polygon.Count;j=i++)
            {
                Point3d a=polygon[i], b=polygon[j];
                if (((a.Y>point.Y)!=(b.Y>point.Y)) &&
                    point.X < (b.X-a.X)*(point.Y-a.Y)/(b.Y-a.Y+1e-30)+a.X) inside=!inside;
            }
            return inside;
        }

        private static bool SegmentsIntersect(Point2d a, Point2d b, Point2d c, Point2d d)
        {
            double o1=(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
            double o2=(b.X-a.X)*(d.Y-a.Y)-(b.Y-a.Y)*(d.X-a.X);
            double o3=(d.X-c.X)*(a.Y-c.Y)-(d.Y-c.Y)*(a.X-c.X);
            double o4=(d.X-c.X)*(b.Y-c.Y)-(d.Y-c.Y)*(b.X-c.X);
            return (o1==0 || o2==0 || Math.Sign(o1)!=Math.Sign(o2)) &&
                   (o3==0 || o4==0 || Math.Sign(o3)!=Math.Sign(o4));
        }

        private static string TransferSettingsPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CE Tools", "DrawingSheetTransfer.settings"); }
        }

        private static string ReadTransferSetting(string key)
        {
            try
            {
                if (!File.Exists(TransferSettingsPath)) return string.Empty;
                foreach (string line in File.ReadAllLines(TransferSettingsPath))
                {
                    int split = line.IndexOf('=');
                    if (split > 0 && string.Equals(line.Substring(0, split), key, StringComparison.Ordinal))
                        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(split + 1)));
                }
            }
            catch { }
            return string.Empty;
        }

        private static void WriteTransferSetting(string key, string value)
        {
            try
            {
                string path = TransferSettingsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var values = new Dictionary<string,string>(StringComparer.Ordinal);
                if (File.Exists(path))
                    foreach (string line in File.ReadAllLines(path))
                    {
                        int split=line.IndexOf('=');
                        if(split>0) values[line.Substring(0,split)]=line.Substring(split+1);
                    }
                values[key]=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty));
                File.WriteAllLines(path, values.Select(kv => kv.Key + "=" + kv.Value));
            }
            catch { }
        }

        // Copy title-block entities from one selected source layout to all checked destination layouts.
        [CommandMethod("CE_TOOLS", "CE_IMPORTTITLEBLOCK", CommandFlags.Modal)]
        public void ImportTitleBlock()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            try
            {
                string path;
                using (var file = new OpenFileDialog
                {
                    Title = "CE Tools - Select source drawing",
                    Filter = "AutoCAD drawings (*.dwg)|*.dwg",
                    CheckFileExists = true,
                    Multiselect = false,
                    RestoreDirectory = true,
                    FileName = File.Exists(ReadTransferSetting("SourcePath")) ? ReadTransferSetting("SourcePath") : string.Empty
                })
                {
                    if (file.ShowDialog() != DialogResult.OK) return;
                    path = Path.GetFullPath(file.FileName);
                    WriteTransferSetting("SourcePath", path);
                }
                if (!string.IsNullOrEmpty(document.Database.Filename) &&
                    string.Equals(path, Path.GetFullPath(document.Database.Filename), StringComparison.OrdinalIgnoreCase))
                {
                    Show("Choose a different source drawing.");
                    return;
                }

                using (var source = new Database(false, true))
                {
                    source.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
                    source.CloseInput(true);
                    List<Sheet> sourceSheets = ReadSheets(source);
                    List<Sheet> destinationSheets = ReadSheets(document.Database);
                    if (sourceSheets.Count == 0)
                    {
                        Show("The source drawing does not contain any Paper Space layouts.");
                        return;
                    }
                    if (destinationSheets.Count == 0)
                    {
                        Show("The destination drawing does not contain any Paper Space layouts.");
                        return;
                    }

                    var picker = new TitleBlockTransferWindow(path, sourceSheets, destinationSheets);
                    if (AcApplication.ShowModalWindow(picker) != true) return;
                    Sheet sourceSheet = picker.SelectedSourceSheet;
                    List<string> destinationLayouts = picker.SelectedDestinationLayouts ?? new List<string>();
                    WriteTransferSetting("TitleBlockSourceLayout", sourceSheet == null ? string.Empty : sourceSheet.Name);
                    WriteTransferSetting("TitleBlockDestinationLayouts", string.Join("|", destinationLayouts));
                    if (sourceSheet == null || destinationLayouts.Count == 0) return;

                    Point3d sourceBase = new Point3d(picker.SourceBaseX, picker.SourceBaseY, 0.0);
                    Point3d insertionPoint;
                    List<Entity> previewEntities = ReadTitleBlockPreviewEntities(source, sourceSheet);
                    using (var jig = new TitleBlockInsertionJig(previewEntities, sourceBase, ReadLastInsertionPoint()))
                    {
                        PromptResult jigResult = document.Editor.Drag(jig);
                        if (jigResult.Status != PromptStatus.OK) return;
                        insertionPoint = jig.InsertionPoint;
                    }
                    WriteTransferSetting("TitleBlockInsertionX", insertionPoint.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    WriteTransferSetting("TitleBlockInsertionY", insertionPoint.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    WriteTransferSetting("TitleBlockInsertionZ", insertionPoint.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    Vector3d displacement = insertionPoint - sourceBase;

                    int totalCopied = 0;
                    var reports = new List<string>();
                    using (document.LockDocument())
                    {
                        foreach (string destinationLayoutName in destinationLayouts)
                        {
                            string imageReport;
                            int copied = CopyTitleBlock(source, document.Database, sourceSheet,
                                displacement, path, destinationLayoutName, out imageReport);
                            totalCopied += copied;
                            reports.Add(destinationLayoutName + ": " + copied + " title-block objects copied.");
                            if (!string.IsNullOrWhiteSpace(imageReport)) reports.Add(imageReport);
                        }
                    }

                    document.Editor.Regen();
                    Show("Copied title block from source tab '" + sourceSheet.Name +
                        "' into " + destinationLayouts.Count + " selected destination Paper Space tab(s)." +
                        "\\nTotal objects copied: " + totalCopied + "." +
                        "\\n\\n" + string.Join("\\n", reports) +
                        "\\n\\nSource base point: (" + sourceBase.X.ToString("0.###") + ", " +
                        sourceBase.Y.ToString("0.###") + "). Destination insertion point was picked on screen.");
                }
            }
            catch (System.Exception ex)
            {
                Show("Title-block transfer failed. " + ex.GetBaseException().Message, MessageBoxIcon.Error);
            }
        }

        private static Point3d ReadLastInsertionPoint()
        {
            double x, y, z;
            double.TryParse(ReadTransferSetting("TitleBlockInsertionX"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x);
            double.TryParse(ReadTransferSetting("TitleBlockInsertionY"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y);
            double.TryParse(ReadTransferSetting("TitleBlockInsertionZ"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z);
            return new Point3d(x, y, z);
        }

        private static List<Entity> ReadTitleBlockPreviewEntities(Database source, Sheet sheet)
        {
            var previews = new List<Entity>();
            using (Transaction tr = source.TransactionManager.StartTransaction())
            {
                Layout layout = tr.GetObject(sheet.Id, OpenMode.ForRead) as Layout;
                if (layout == null) return previews;
                BlockTableRecord paper = tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead) as BlockTableRecord;
                if (paper == null) return previews;
                foreach (ObjectId id in paper)
                {
                    Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || entity is Autodesk.AutoCAD.DatabaseServices.Viewport || entity.IsErased) continue;
                    Entity clone = entity.Clone() as Entity;
                    if (clone != null) previews.Add(clone);
                }
                tr.Commit();
            }
            return previews;
        }

        private sealed class TitleBlockInsertionJig : DrawJig, IDisposable
        {
            private readonly IList<Entity> _sourceEntities;
            private readonly Point3d _sourceBase;
            private Point3d _currentPoint;

            internal Point3d InsertionPoint { get { return _currentPoint; } }

            internal TitleBlockInsertionJig(IList<Entity> sourceEntities, Point3d sourceBase, Point3d initialPoint)
            {
                _sourceEntities = sourceEntities ?? new List<Entity>();
                _sourceBase = sourceBase;
                _currentPoint = initialPoint;
            }

            protected override SamplerStatus Sampler(JigPrompts prompts)
            {
                var options = new JigPromptPointOptions("\\nMove title-block preview and specify insertion point: ")
                {
                    UserInputControls = UserInputControls.Accept3dCoordinates
                };
                PromptPointResult result = prompts.AcquirePoint(options);
                if (result.Status != PromptStatus.OK) return SamplerStatus.Cancel;
                if (result.Value.DistanceTo(_currentPoint) < Tolerance.Global.EqualPoint)
                    return SamplerStatus.NoChange;
                _currentPoint = result.Value;
                return SamplerStatus.OK;
            }

            protected override bool WorldDraw(WorldDraw draw)
            {
                Vector3d offset = _currentPoint - _sourceBase;
                Matrix3d transform = Matrix3d.Displacement(offset);
                foreach (Entity sourceEntity in _sourceEntities)
                {
                    Entity ghost = null;
                    try
                    {
                        ghost = sourceEntity.Clone() as Entity;
                        if (ghost == null) continue;
                        ghost.TransformBy(transform);
                        draw.Geometry.Draw(ghost);
                    }
                    catch { }
                    finally
                    {
                        if (ghost != null) ghost.Dispose();
                    }
                }
                return true;
            }

            public void Dispose()
            {
                foreach (Entity entity in _sourceEntities)
                    if (entity != null) entity.Dispose();
            }
        }

        private static string CreateTitleBlockDestinationLayout(Database source, Database destination, Sheet sourceSheet)
        {
            using (Transaction read = source.TransactionManager.StartTransaction())
            using (Transaction write = destination.TransactionManager.StartTransaction())
            {
                Layout original = (Layout)read.GetObject(sourceSheet.Id, OpenMode.ForRead);
                DBDictionary layouts = (DBDictionary)write.GetObject(destination.LayoutDictionaryId, OpenMode.ForWrite);
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int tabOrder = 0;
                foreach (DBDictionaryEntry entry in layouts)
                {
                    Layout existing = write.GetObject(entry.Value, OpenMode.ForRead, false) as Layout;
                    if (existing == null) continue;
                    usedNames.Add(existing.LayoutName);
                    tabOrder = Math.Max(tabOrder, existing.TabOrder);
                }

                string newName = UniqueName(sourceSheet.Name, usedNames);
                BlockTable blocks = (BlockTable)write.GetObject(destination.BlockTableId, OpenMode.ForWrite);
                int index = 0;
                string paperName;
                do { paperName = "*Paper_Space" + index++; } while (blocks.Has(paperName));
                var paper = new BlockTableRecord { Name = paperName };
                blocks.Add(paper);
                write.AddNewlyCreatedDBObject(paper, true);

                var imported = new Layout { LayoutName = newName };
                imported.CopyFrom(original);
                imported.LayoutName = newName;
                imported.AddToLayoutDictionary(destination, paper.ObjectId);
                write.AddNewlyCreatedDBObject(imported, true);
                imported.TabOrder = tabOrder + 1;
                CopyPageSetup(source, destination, read, write, original, imported);
                write.Commit();
                return newName;
            }
        }

        private static int CopyTitleBlock(Database source, Database destination, Sheet sheet,
            Vector3d displacement, string sourceDwgPath, string destinationLayoutName, out string imageReport)
        {
            imageReport = string.Empty;
            using (Transaction read = source.TransactionManager.StartTransaction())
            {
                var layout = (Layout)read.GetObject(sheet.Id, OpenMode.ForRead);
                var ids = new ObjectIdCollection();
                var paper = (BlockTableRecord)read.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId id in paper)
                {
                    Entity entity = read.GetObject(id, OpenMode.ForRead) as Entity;
                    if (entity != null && !(entity is Autodesk.AutoCAD.DatabaseServices.Viewport) && !entity.IsErased) ids.Add(id);
                }
                if (ids.Count == 0) return 0;

                using (Transaction write = destination.TransactionManager.StartTransaction())
                {
                    DBDictionary layouts = (DBDictionary)write.GetObject(destination.LayoutDictionaryId, OpenMode.ForRead);
                    Layout destinationLayout = null;
                    foreach (DBDictionaryEntry entry in layouts)
                    {
                        Layout candidate = write.GetObject(entry.Value, OpenMode.ForRead, false) as Layout;
                        if (candidate != null && !candidate.ModelType &&
                            string.Equals(candidate.LayoutName, destinationLayoutName, StringComparison.OrdinalIgnoreCase))
                        {
                            destinationLayout = candidate;
                            break;
                        }
                    }
                    if (destinationLayout == null)
                        throw new InvalidOperationException("Destination layout not found: " + destinationLayoutName);

                    BlockTableRecord destinationPaper = (BlockTableRecord)write.GetObject(
                        destinationLayout.BlockTableRecordId, OpenMode.ForWrite);
                    using (var mapping = new IdMapping())
                    {
                        using (var sourceLayers = new SheetCloneLayerScope(source, read))
                            source.WblockCloneObjects(ids, destinationPaper.ObjectId, mapping,
                            DuplicateRecordCloning.MangleName, false);
                        using (var clonedLayers = new SheetCloneLayerScope(read, write, mapping))
                        {
                        Matrix3d shift = Matrix3d.Displacement(displacement);
                        int count = 0;
                        foreach (ObjectId id in ids)
                        {
                            IdPair pair = mapping[id];
                            if (!pair.IsCloned || pair.Value.IsNull || pair.Value.IsErased)
                                throw new InvalidOperationException("Title-block entity could not be cloned: " + id.Handle);
                            Entity clone = write.GetObject(pair.Value, OpenMode.ForWrite, false) as Entity;
                            if (clone == null) continue;
                            if (displacement.Length > 1e-9) clone.TransformBy(shift);
                            count++;
                        }
                        imageReport = RelinkRasterLogos(source, destination, read, write,
                            ids, mapping, sourceDwgPath);
                        }
                        write.Commit();
                        return ids.Count;
                    }
                }
            }
        }

        private static string RelinkRasterLogos(Database source, Database destination,
            Transaction read, Transaction write, ObjectIdCollection originalIds,
            IdMapping mapping, string sourceDwgPath)
        {
            int resolved = 0, missing = 0;
            var missingNames = new List<string>();
            var processed = new HashSet<ObjectId>();
            foreach (ObjectId originalId in originalIds)
            {
                RasterImage sourceImage = read.GetObject(originalId, OpenMode.ForRead) as RasterImage;
                if (sourceImage == null || sourceImage.ImageDefId.IsNull) continue;
                IdPair pair = mapping[originalId];
                if (!pair.IsCloned || pair.Value.IsNull) continue;
                RasterImage targetImage = write.GetObject(pair.Value, OpenMode.ForRead) as RasterImage;
                if (targetImage == null || targetImage.ImageDefId.IsNull ||
                    !processed.Add(targetImage.ImageDefId)) continue;
                RasterImageDef sourceDef = read.GetObject(sourceImage.ImageDefId, OpenMode.ForRead) as RasterImageDef;
                RasterImageDef targetDef = write.GetObject(targetImage.ImageDefId, OpenMode.ForWrite) as RasterImageDef;
                if (sourceDef == null || targetDef == null) continue;
                string originalPath = sourceDef.SourceFileName;
                string filePath = ResolveLogoPath(originalPath, sourceDwgPath, destination.Filename);
                if (filePath == null)
                {
                    missing++;
                    missingNames.Add(originalPath ?? "(empty image path)");
                    continue;
                }
                targetDef.SourceFileName = filePath;
                resolved++;
                try { targetDef.Load(); }
                catch (System.Exception) { }
            }
            if (resolved == 0 && missing == 0) return string.Empty;
            return "\n\nRaster logos/images: " + resolved + " paths resolved; " + missing + " missing." +
                (missing > 0 ? "\nMissing files: " + string.Join("; ", missingNames.Distinct().Take(8)) +
                "\nPlace missing logo files beside the source DWG or correct their paths." : string.Empty);
        }

        private static string ResolveLogoPath(string storedPath, string sourceDwgPath,
            string destinationDwgPath)
        {
            if (string.IsNullOrWhiteSpace(storedPath)) return null;
            string sourceFolder = Path.GetDirectoryName(sourceDwgPath);
            string destinationFolder = string.IsNullOrWhiteSpace(destinationDwgPath)
                ? null : Path.GetDirectoryName(destinationDwgPath);
            var candidates = new List<string>();
            if (Path.IsPathRooted(storedPath)) candidates.Add(storedPath);
            else
            {
                if (!string.IsNullOrWhiteSpace(sourceFolder))
                    candidates.Add(Path.Combine(sourceFolder, storedPath));
                if (!string.IsNullOrWhiteSpace(destinationFolder))
                    candidates.Add(Path.Combine(destinationFolder, storedPath));
            }
            if (!string.IsNullOrWhiteSpace(sourceFolder))
                candidates.Add(Path.Combine(sourceFolder, Path.GetFileName(storedPath)));
            foreach (string candidate in candidates)
            {
                try
                {
                    string fullPath = Path.GetFullPath(candidate);
                    if (File.Exists(fullPath)) return fullPath;
                }
                catch (System.Exception) { }
            }
            return null;
        }

        private static void Show(string message, MessageBoxIcon icon = MessageBoxIcon.Information)
        {
            try
            {
                var window = new Wpf.Window
                {
                    Title = "CE Tools - Import Sheets",
                    Width = 560,
                    SizeToContent = Wpf.SizeToContent.Height,
                    MinHeight = 170,
                    MaxHeight = 620,
                    ResizeMode = Wpf.ResizeMode.NoResize,
                    WindowStartupLocation = Wpf.WindowStartupLocation.CenterScreen,
                    ShowInTaskbar = false
                };
                var root = new WpfControls.DockPanel { Margin = new Wpf.Thickness(18) };
                window.Content = root;
                var ok = new WpfControls.Button
                {
                    Content = "OK",
                    Width = 92,
                    Height = 30,
                    Margin = new Wpf.Thickness(6, 14, 0, 0),
                    IsDefault = true,
                    IsCancel = true,
                    HorizontalAlignment = Wpf.HorizontalAlignment.Right
                };
                ok.Click += (s, e) => window.Close();
                WpfControls.DockPanel.SetDock(ok, WpfControls.Dock.Bottom);
                root.Children.Add(ok);
                var text = new WpfControls.TextBlock
                {
                    Text = message ?? string.Empty,
                    TextWrapping = Wpf.TextWrapping.Wrap,
                    Margin = new Wpf.Thickness(0, 4, 0, 8),
                    MaxWidth = 510
                };
                root.Children.Add(new WpfControls.ScrollViewer
                {
                    VerticalScrollBarVisibility = WpfControls.ScrollBarVisibility.Auto,
                    MaxHeight = 500,
                    Content = text
                });
                window.Loaded += (s, e) => CeInterfaceTheme.Apply(window);
                AcApplication.ShowModalWindow(window);
            }
            catch
            {
                MessageBox.Show(message, "CE Tools - Import Sheets", MessageBoxButtons.OK, icon);
            }
        }

        private static void ShowThemedPickerMessage(Wpf.Window owner, string message, string title)
        {
            var window = new Wpf.Window
            {
                Title = title ?? "CE Tools",
                Width = 480,
                SizeToContent = Wpf.SizeToContent.Height,
                MinHeight = 150,
                MaxHeight = 520,
                ResizeMode = Wpf.ResizeMode.NoResize,
                WindowStartupLocation = Wpf.WindowStartupLocation.CenterOwner,
                Owner = owner,
                ShowInTaskbar = false
            };
            var root = new WpfControls.DockPanel { Margin = new Wpf.Thickness(18) };
            window.Content = root;
            var ok = new WpfControls.Button
            {
                Content = "OK",
                Width = 86,
                Height = 30,
                Margin = new Wpf.Thickness(6, 14, 0, 0),
                IsDefault = true,
                IsCancel = true,
                HorizontalAlignment = Wpf.HorizontalAlignment.Right
            };
            ok.Click += (s, e) => window.Close();
            WpfControls.DockPanel.SetDock(ok, WpfControls.Dock.Bottom);
            root.Children.Add(ok);
            root.Children.Add(new WpfControls.TextBlock
            {
                Text = message ?? string.Empty,
                TextWrapping = Wpf.TextWrapping.Wrap,
                MaxWidth = 430,
                Margin = new Wpf.Thickness(0, 4, 0, 8)
            });
            window.Loaded += (s, e) => CeInterfaceTheme.Apply(window);
            window.ShowDialog();
        }

        private static List<Sheet> ReadSheets(Database source)
        {
            var sheets = new List<Sheet>();
            using (Transaction tr = source.TransactionManager.StartTransaction())
            {
                var dictionary = (DBDictionary)tr.GetObject(source.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dictionary)
                {
                    var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    if (!layout.ModelType)
                        sheets.Add(new Sheet { Id = entry.Value, Name = layout.LayoutName, Order = layout.TabOrder });
                }
            }
            return sheets.OrderBy(s => s.Order).ThenBy(s => s.Name).ToList();
        }

        private static ImportResult CopySheets(Database source, Database destination, IList<Sheet> sheets,
            bool includeModelSpace = true, bool includePaperSpaceEntities = true, bool includeViewports = true)
        {
            var result = new ImportResult();
            var viewportJobs = new List<ViewportCloneJob>();

            // First create and commit the layouts and their non-viewport contents.
            // Floating viewports are created only after their destination layouts exist
            // and can be made current in the active drawing.
            using (Transaction read = source.TransactionManager.StartTransaction())
            {
                using (Transaction write = destination.TransactionManager.StartTransaction())
                {
                    var layouts = (DBDictionary)write.GetObject(destination.LayoutDictionaryId, OpenMode.ForWrite);
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int tab = 0;
                    foreach (DBDictionaryEntry entry in layouts)
                    {
                        var layout = (Layout)write.GetObject(entry.Value, OpenMode.ForRead);
                        names.Add(layout.LayoutName);
                        tab = Math.Max(tab, layout.TabOrder);
                    }

                    var sourceBlocks = (BlockTable)read.GetObject(source.BlockTableId, OpenMode.ForRead);
                    var targetBlocks = (BlockTable)write.GetObject(destination.BlockTableId, OpenMode.ForWrite);

                    // Import the source drawing's viewport annotation-scale definitions before
                    // cloning annotative Model Space objects. This allows their existing scale
                    // contexts to resolve in the destination instead of falling back to the
                    // destination drawing's current CANNOSCALE.
                    EnsureDestinationViewportAnnotationScales(source, destination, read, write, sheets);

                    var jobs = new List<CloneJob>();
                    ObjectIdCollection modelIds = includeModelSpace
                        ? EntityIds(read, sourceBlocks[BlockTableRecord.ModelSpace])
                        : new ObjectIdCollection();

                    // Exactly one Model Space clone is shared by all imported viewports.
                    if (modelIds.Count > 0)
                        jobs.Add(new CloneJob { Ids = modelIds, Owner = targetBlocks[BlockTableRecord.ModelSpace] });
                    result.ModelCount = modelIds.Count;

                    foreach (Sheet sheet in sheets)
                    {
                        var original = (Layout)read.GetObject(sheet.Id, OpenMode.ForRead);
                        string name = UniqueName(sheet.Name, names);
                        int paperIndex = 0;
                        string paperName;
                        do { paperName = "*Paper_Space" + paperIndex++; } while (targetBlocks.Has(paperName));

                        var paper = new BlockTableRecord { Name = paperName };
                        targetBlocks.Add(paper);
                        write.AddNewlyCreatedDBObject(paper, true);

                        var imported = new Layout { LayoutName = name };
                        imported.CopyFrom(original);
                        imported.LayoutName = name;
                        imported.AddToLayoutDictionary(destination, paper.ObjectId);
                        write.AddNewlyCreatedDBObject(imported, true);
                        imported.TabOrder = ++tab;
                        CopyPageSetup(source, destination, read, write, original, imported);

                        ObjectIdCollection allPaperIds = AllEntityIds(read, original.BlockTableRecordId);
                        var paperIds = new ObjectIdCollection();
                        var viewportIds = new ObjectIdCollection();

                        bool skippedOverallViewport = false;
                        foreach (ObjectId id in allPaperIds)
                        {
                            Autodesk.AutoCAD.DatabaseServices.Viewport sourceViewport =
                                read.GetObject(id, OpenMode.ForRead, false) as Autodesk.AutoCAD.DatabaseServices.Viewport;

                            if (sourceViewport != null)
                            {
                                // Normally AutoCAD assigns Number=1 to the overall Paper Space
                                // viewport and Number>1 to floating viewports. In side-database
                                // reads, however, some drawings expose Number=1 for more than one
                                // viewport. Skip only the first Number=1 viewport in this layout;
                                // retain subsequent viewport entities as floating viewport candidates.
                                bool isOverallViewport = sourceViewport.Number == 1 && !skippedOverallViewport;
                                if (sourceViewport.Number == 1)
                                    skippedOverallViewport = true;

                                // Some source drawings contain an additional floating viewport
                                // whose boundary spans almost the entire sheet/title-block area.
                                // It is not a drawing viewport and creates an unwanted large
                                // selectable/activatable frame around the imported sheet. Skip
                                // only these sheet-enclosing viewports; retain normal detail,
                                // plan and section viewports at their original sizes.
                                if (includeViewports && !isOverallViewport &&
                                    !IsSheetEnclosingViewport(sourceViewport, original))
                                    viewportIds.Add(id);
                            }
                            else if (includePaperSpaceEntities)
                            {
                                paperIds.Add(id);
                            }
                        }

                        result.PaperCount += paperIds.Count + viewportIds.Count;
                        result.ViewportCount += viewportIds.Count;

                        if (paperIds.Count > 0)
                            jobs.Add(new CloneJob { Ids = paperIds, Owner = paper.ObjectId });

                        if (viewportIds.Count > 0)
                        {
                            viewportJobs.Add(new ViewportCloneJob
                            {
                                Ids = viewportIds,
                                Owner = paper.ObjectId,
                                LayoutName = name
                            });
                        }

                        result.ImportedLayoutNames.Add(name);
                        result.Names.Add(name + " [Paper Space objects copied: " +
                            (paperIds.Count + viewportIds.Count) +
                            ", source floating viewports: " + viewportIds.Count + "]");
                    }

                    List<LayerStateSnapshot> sourceLayerStates = CaptureAndUnlockLayers(source, read);
                    List<LayerStateSnapshot> destinationLayerStates = CaptureAndUnlockLayers(destination, write);
                    var originalDestinationLayerIds =
                        new HashSet<ObjectId>(destinationLayerStates.Select(x => x.Id));
                    var clonedLayerStates = new Dictionary<ObjectId, LayerStateSnapshot>();

                    try
                    {
                        // Clone each owner batch with its own IdMapping. Reusing a mapping
                        // across Model Space and multiple Paper Space owners can corrupt
                        // AutoCAD's clone context on multi-layout transfers.
                        foreach (CloneJob job in jobs.Where(j => j.Ids.Count > 0))
                        {
                            using (var mapping = new IdMapping())
                            {
                                source.WblockCloneObjects(job.Ids, job.Owner, mapping,
                                    DuplicateRecordCloning.MangleName, false);

                                foreach (ObjectId id in job.Ids)
                                {
                                    IdPair pair = mapping[id];
                                    if (!pair.IsCloned || pair.Value.IsNull || pair.Value.IsErased)
                                        throw new InvalidOperationException(
                                            "A source object could not be copied: " + id.Handle);
                                }

                                foreach (LayerStateSnapshot sourceLayer in sourceLayerStates)
                                {
                                    try
                                    {
                                        IdPair layerPair = mapping[sourceLayer.Id];
                                        if (layerPair.IsCloned && !layerPair.Value.IsNull &&
                                            !originalDestinationLayerIds.Contains(layerPair.Value))
                                        {
                                            clonedLayerStates[layerPair.Value] = sourceLayer;
                                        }
                                    }
                                    catch { /* This batch did not require that source layer. */ }
                                }
                            }
                        }
                    }
                    finally
                    {
                        RestoreLayerStates(destination, write, destinationLayerStates);
                        RestoreMappedLayerStates(destination, write, clonedLayerStates);
                        RestoreLayerStates(source, read, sourceLayerStates);
                    }

                    // Commit the new layouts and cloned entities before activating layouts
                    // and creating their floating viewports.
                    write.Commit();
                }

                if (includeViewports && viewportJobs.Count > 0)
                    CreateImportedFloatingViewports(source, destination, read, viewportJobs, result);
            }

            return result;
        }

        private static void CreateImportedFloatingViewports(Database source, Database destination,
            Transaction sourceRead, IList<ViewportCloneJob> viewportJobs, ImportResult result)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
                throw new InvalidOperationException("There is no active destination drawing.");

            string originalLayout = null;
            try { originalLayout = LayoutManager.Current.CurrentLayout; } catch { }

            try
            {
                foreach (ViewportCloneJob viewportJob in viewportJobs)
                {
                    try
                    {
                        // A floating viewport must be created while its destination layout
                        // is active in Paper Space. Each candidate is appended to that layout's
                        // Paper Space BTR before it is enabled, allowing AutoCAD to register it.
                        LayoutManager.Current.CurrentLayout = viewportJob.LayoutName;
                        document.Editor.SwitchToPaperSpace();

                        using (Transaction write = destination.TransactionManager.StartTransaction())
                        {
                            BlockTableRecord destinationPaper =
                                (BlockTableRecord)write.GetObject(viewportJob.Owner, OpenMode.ForWrite);
                            List<LayerStateSnapshot> destinationLayerStates =
                                CaptureAndUnlockLayers(destination, write);

                            try
                            {
                                foreach (ObjectId sourceViewportId in viewportJob.Ids)
                                {
                                    Autodesk.AutoCAD.DatabaseServices.Viewport sourceViewport =
                                        sourceRead.GetObject(sourceViewportId, OpenMode.ForRead, false)
                                            as Autodesk.AutoCAD.DatabaseServices.Viewport;
                                    if (sourceViewport == null) continue;

                                    var destinationViewport =
                                        new Autodesk.AutoCAD.DatabaseServices.Viewport();
                                    destinationViewport.SetDatabaseDefaults(destination);

                                    // Append and register the new viewport BEFORE assigning its view
                                    // state. Several Viewport properties (notably per-viewport UCS
                                    // state) require a database-resident entity. Setting those
                                    // properties while the viewport is still transient can throw
                                    // eNotInDatabase, which previously prevented every layout from
                                    // receiving its floating viewports.
                                    destinationPaper.AppendEntity(destinationViewport);
                                    write.AddNewlyCreatedDBObject(destinationViewport, true);
                                    destinationViewport.On = false;

                                    LayerTableRecord sourceLayer =
                                        sourceRead.GetObject(sourceViewport.LayerId, OpenMode.ForRead, false)
                                            as LayerTableRecord;
                                    if (sourceLayer != null)
                                    {
                                        ObjectId destinationLayerId =
                                            FindDestinationLayer(write, destination, sourceLayer.Name);
                                        if (!destinationLayerId.IsNull)
                                            destinationViewport.LayerId = destinationLayerId;
                                    }

                                    // Rebuild the floating viewport from source properties only after
                                    // it has an owner and belongs to the destination database.
                                    destinationViewport.CenterPoint = sourceViewport.CenterPoint;
                                    destinationViewport.Width = sourceViewport.Width;
                                    destinationViewport.Height = sourceViewport.Height;
                                    destinationViewport.ViewCenter = sourceViewport.ViewCenter;
                                    destinationViewport.ViewTarget = sourceViewport.ViewTarget;
                                    destinationViewport.ViewDirection = sourceViewport.ViewDirection;
                                    destinationViewport.ViewHeight = sourceViewport.ViewHeight;
                                    destinationViewport.TwistAngle = sourceViewport.TwistAngle;

                                    // Preserve the named standard scale first, then the exact
                                    // numeric custom scale. CustomScale alone does not preserve
                                    // the viewport's original drawing-scale selection.
                                    CopyViewportProperty(sourceViewport, destinationViewport, "StandardScale");
                                    destinationViewport.CustomScale = sourceViewport.CustomScale;

                                    // Preserve the viewport's annotative scale context (for example
                                    // 1:500 or 1:1000). The context must belong to the destination
                                    // drawing; assigning the source database's context directly is
                                    // not safe across databases. Import does not rewrite any cloned
                                    // DBText/MText/Dimension height or paper-height value.
                                    CopyViewportAnnotationScale(sourceViewport, destinationViewport, destination);

                                    destinationViewport.PerspectiveOn = sourceViewport.PerspectiveOn;
                                    destinationViewport.Locked = sourceViewport.Locked;
                                    destinationViewport.GridOn = sourceViewport.GridOn;
                                    destinationViewport.SnapOn = sourceViewport.SnapOn;
                                    destinationViewport.SnapIncrement = sourceViewport.SnapIncrement;
                                    destinationViewport.SnapAngle = sourceViewport.SnapAngle;
                                    destinationViewport.UcsPerViewport = sourceViewport.UcsPerViewport;

                                    // Imported floating viewports must be active in the destination
                                    // layout. Side-database reads can report sourceViewport.On as false
                                    // even when the source viewport is visibly active. Preserve the
                                    // source view configuration, but explicitly turn every imported
                                    // floating viewport ON so it can display and be activated by
                                    // double-clicking in Paper Space.
                                    destinationViewport.On = true;
                                    result.ViewportCreatedCount++;
                                }
                            }
                            finally
                            {
                                RestoreLayerStates(destination, write, destinationLayerStates);
                            }

                            write.Commit();
                        }

                        document.Editor.Regen();
                    }
                    catch (System.Exception viewportError)
                    {
                        result.ViewportErrors.Add(viewportJob.LayoutName + ": " +
                            viewportError.GetBaseException().Message);
                    }
                }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(originalLayout))
                {
                    try
                    {
                        LayoutManager.Current.CurrentLayout = originalLayout;
                        document.Editor.SwitchToPaperSpace();
                    }
                    catch { }
                }
            }
        }

        private static bool IsSheetEnclosingViewport(
            Autodesk.AutoCAD.DatabaseServices.Viewport viewport, Layout layout)
        {
            if (viewport == null || layout == null) return false;

            try
            {
                double viewportWidth = Math.Abs(viewport.Width);
                double viewportHeight = Math.Abs(viewport.Height);
                if (viewportWidth <= 0.0 || viewportHeight <= 0.0) return false;

                // PlotPaperSize is exposed by PlotSettings on supported AutoCAD releases.
                // Read it reflectively so the importer remains compatible with the
                // Civil 3D 2024 target assemblies without depending on a version-specific
                // compile-time return type.
                object paperSize = ReadObjectProperty(layout, "PlotPaperSize");
                double paperWidth = ReadCoordinate(paperSize, "X");
                double paperHeight = ReadCoordinate(paperSize, "Y");
                if (paperWidth <= 0.0 || paperHeight <= 0.0) return false;

                // Compare both orientations because the layout may be portrait or landscape.
                double directWidthRatio = viewportWidth / paperWidth;
                double directHeightRatio = viewportHeight / paperHeight;
                double swappedWidthRatio = viewportWidth / paperHeight;
                double swappedHeightRatio = viewportHeight / paperWidth;

                bool coversDirectSheet =
                    directWidthRatio >= 0.88 && directHeightRatio >= 0.88;
                bool coversRotatedSheet =
                    swappedWidthRatio >= 0.88 && swappedHeightRatio >= 0.88;

                return coversDirectSheet || coversRotatedSheet;
            }
            catch
            {
                // If paper dimensions cannot be read, do not discard a source viewport.
                return false;
            }
        }

        private static double ReadCoordinate(object value, string propertyName)
        {
            if (value == null) return 0.0;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    propertyName, BindingFlags.Public | BindingFlags.Instance);
                object raw = property == null ? null : property.GetValue(value, null);
                return raw == null ? 0.0 : Convert.ToDouble(
                    raw, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return 0.0; }
        }

        private static void EnsureDestinationViewportAnnotationScales(
            Database source, Database destination, Transaction sourceTransaction,
            Transaction destinationTransaction, IList<Sheet> sheets)
        {
            if (source == null || destination == null ||
                sourceTransaction == null || destinationTransaction == null ||
                sheets == null)
                return;

            // Preserve the source drawing's current annotation scale where the Database API
            // exposes it. The per-viewport contexts below are also imported individually.
            try
            {
                PropertyInfo currentScaleProperty = source.GetType().GetProperty(
                    "Cannoscale", BindingFlags.Public | BindingFlags.Instance);
                object currentScale = currentScaleProperty == null
                    ? null : currentScaleProperty.GetValue(source, null);
                EnsureDestinationAnnotationScaleContext(destination, currentScale);
            }
            catch { }

            foreach (Sheet sheet in sheets)
            {
                try
                {
                    Layout sourceLayout = sourceTransaction.GetObject(
                        sheet.Id, OpenMode.ForRead, false) as Layout;
                    if (sourceLayout == null) continue;

                    BlockTableRecord paper = sourceTransaction.GetObject(
                        sourceLayout.BlockTableRecordId, OpenMode.ForRead, false)
                        as BlockTableRecord;
                    if (paper == null) continue;

                    bool skippedOverallViewport = false;
                    foreach (ObjectId id in paper)
                    {
                        try
                        {
                            Autodesk.AutoCAD.DatabaseServices.Viewport viewport =
                                sourceTransaction.GetObject(id, OpenMode.ForRead, false)
                                    as Autodesk.AutoCAD.DatabaseServices.Viewport;
                            if (viewport == null) continue;

                            bool isOverallViewport =
                                viewport.Number == 1 && !skippedOverallViewport;
                            if (viewport.Number == 1)
                                skippedOverallViewport = true;
                            if (isOverallViewport) continue;

                            object context = ReadViewportAnnotationScale(viewport);
                            EnsureDestinationAnnotationScaleContext(destination, context);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        private static object EnsureDestinationAnnotationScaleContext(
            Database destination, object sourceContext)
        {
            if (destination == null || sourceContext == null) return null;

            try
            {
                Type contextType = sourceContext.GetType();
                string contextName = Convert.ToString(
                    ReadObjectProperty(sourceContext, "Name"),
                    System.Globalization.CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(contextName)) return null;

                object manager = ReadObjectProperty(destination, "ObjectContextManager");
                if (manager == null) return null;

                MethodInfo getCollectionMethod = manager.GetType().GetMethod(
                    "GetContextCollection", new[] { typeof(string) });
                if (getCollectionMethod == null) return null;

                object collection = getCollectionMethod.Invoke(
                    manager, new object[] { "ACDB_ANNOTATIONSCALES" });
                if (collection == null) return null;

                MethodInfo getContextMethod = collection.GetType().GetMethod(
                    "GetContext", new[] { typeof(string) });
                if (getContextMethod == null) return null;

                object existing = getContextMethod.Invoke(
                    collection, new object[] { contextName });
                if (existing != null) return existing;

                object destinationContext = Activator.CreateInstance(contextType);
                if (destinationContext == null) return null;

                // AnnotationScale definitions are portable by name and unit ratio.
                // Copy only their definition properties; never copy source database IDs.
                CopySimpleContextProperty(sourceContext, destinationContext, "Name");
                CopySimpleContextProperty(sourceContext, destinationContext, "PaperUnits");
                CopySimpleContextProperty(sourceContext, destinationContext, "DrawingUnits");

                MethodInfo addContextMethod = collection.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                    {
                        if (!string.Equals(method.Name, "AddContext", StringComparison.Ordinal))
                            return false;
                        ParameterInfo[] parameters = method.GetParameters();
                        return parameters.Length == 1 &&
                            parameters[0].ParameterType.IsAssignableFrom(contextType);
                    });

                if (addContextMethod == null) return null;
                addContextMethod.Invoke(collection, new[] { destinationContext });

                return getContextMethod.Invoke(collection, new object[] { contextName });
            }
            catch
            {
                return null;
            }
        }

        private static void CopySimpleContextProperty(
            object source, object destination, string propertyName)
        {
            try
            {
                PropertyInfo sourceProperty = source.GetType().GetProperty(
                    propertyName, BindingFlags.Public | BindingFlags.Instance);
                PropertyInfo destinationProperty = destination.GetType().GetProperty(
                    propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (sourceProperty == null || destinationProperty == null ||
                    !destinationProperty.CanWrite)
                    return;

                object value = sourceProperty.GetValue(source, null);
                if (value == null) return;

                if (destinationProperty.PropertyType.IsEnum &&
                    value.GetType() != destinationProperty.PropertyType)
                {
                    value = Enum.ToObject(destinationProperty.PropertyType, value);
                }
                else if (!destinationProperty.PropertyType.IsAssignableFrom(value.GetType()))
                {
                    value = Convert.ChangeType(
                        value, destinationProperty.PropertyType,
                        System.Globalization.CultureInfo.InvariantCulture);
                }

                destinationProperty.SetValue(destination, value, null);
            }
            catch { }
        }

        private static object ReadObjectProperty(object instance, string propertyName)
        {
            if (instance == null) return null;
            try
            {
                PropertyInfo property = instance.GetType().GetProperty(
                    propertyName, BindingFlags.Public | BindingFlags.Instance);
                return property == null ? null : property.GetValue(instance, null);
            }
            catch { return null; }
        }

        private static object ReadViewportAnnotationScale(
            Autodesk.AutoCAD.DatabaseServices.Viewport viewport)
        {
            return ReadObjectProperty(viewport, "AnnotationScale");
        }

        private static void CopyViewportProperty(
            Autodesk.AutoCAD.DatabaseServices.Viewport source,
            Autodesk.AutoCAD.DatabaseServices.Viewport destination,
            string propertyName)
        {
            try
            {
                PropertyInfo sourceProperty = source.GetType().GetProperty(
                    propertyName, BindingFlags.Public | BindingFlags.Instance);
                PropertyInfo destinationProperty = destination.GetType().GetProperty(
                    propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (sourceProperty == null || destinationProperty == null ||
                    !destinationProperty.CanWrite)
                    return;

                object value = sourceProperty.GetValue(source, null);
                if (value != null &&
                    destinationProperty.PropertyType.IsAssignableFrom(value.GetType()))
                    destinationProperty.SetValue(destination, value, null);
            }
            catch { }
        }

        private static void CopyViewportAnnotationScale(
            Autodesk.AutoCAD.DatabaseServices.Viewport source,
            Autodesk.AutoCAD.DatabaseServices.Viewport destination,
            Database destinationDatabase)
        {
            object sourceContext = ReadViewportAnnotationScale(source);
            if (sourceContext == null) return;

            object destinationContext = EnsureDestinationAnnotationScaleContext(
                destinationDatabase, sourceContext);
            if (destinationContext == null)
                throw new InvalidOperationException(
                    "Could not import viewport annotation scale '" +
                    Convert.ToString(ReadObjectProperty(sourceContext, "Name")) +
                    "' into the destination drawing.");

            PropertyInfo destinationProperty = destination.GetType().GetProperty(
                "AnnotationScale", BindingFlags.Public | BindingFlags.Instance);
            if (destinationProperty == null || !destinationProperty.CanWrite)
                throw new InvalidOperationException(
                    "The destination viewport does not expose a writable AnnotationScale property.");

            destinationProperty.SetValue(destination, destinationContext, null);
        }

        private static ObjectId FindDestinationLayer(Transaction transaction, Database destination, string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName)) return ObjectId.Null;
            LayerTable table = (LayerTable)transaction.GetObject(destination.LayerTableId, OpenMode.ForRead);
            if (table.Has(layerName)) return table[layerName];

            // If the source layer name collided and was mangled during cloning, choose the
            // destination layer whose name begins with the original layer name.
            foreach (ObjectId id in table)
            {
                LayerTableRecord record = transaction.GetObject(id, OpenMode.ForRead, false) as LayerTableRecord;
                if (record != null &&
                    record.Name.StartsWith(layerName + "$", StringComparison.OrdinalIgnoreCase))
                    return id;
            }
            return ObjectId.Null;
        }

        private static void ZoomExtentsInImportedLayouts(Document document, IEnumerable<string> layoutNames)
        {
            if (document == null || layoutNames == null) return;

            string originalLayout = null;
            try { originalLayout = LayoutManager.Current.CurrentLayout; }
            catch { }

            try
            {
                foreach (string layoutName in layoutNames)
                {
                    if (string.IsNullOrWhiteSpace(layoutName)) continue;
                    try
                    {
                        LayoutManager.Current.CurrentLayout = layoutName;
                        // Ensure the command runs in Paper Space, not inside a floating viewport.
                        document.Editor.Command("_.PSPACE");
                        document.Editor.Command("_.ZOOM", "_E");
                        document.Editor.Regen();
                    }
                    catch
                    {
                        // A single invalid/unavailable layout must not prevent the remaining
                        // imported layouts from being zoomed to extents.
                    }
                }
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(originalLayout))
                {
                    try
                    {
                        LayoutManager.Current.CurrentLayout = originalLayout;
                        document.Editor.Regen();
                    }
                    catch { }
                }
            }
        }

        private static List<LayerStateSnapshot> CaptureAndUnlockLayers(Database database, Transaction transaction)
        {
            var states = new List<LayerStateSnapshot>();
            LayerTable layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId layerId in layerTable)
            {
                LayerTableRecord layer = transaction.GetObject(layerId, OpenMode.ForWrite, false) as LayerTableRecord;
                if (layer == null || layer.IsDependent) continue;
                bool wasLocked = layer.IsLocked;
                bool wasFrozen = layer.IsFrozen;
                if (!wasLocked && !wasFrozen) continue;
                states.Add(new LayerStateSnapshot
                {
                    Id = layerId,
                    Name = layer.Name,
                    IsLocked = wasLocked,
                    IsFrozen = wasFrozen
                });
                if (layer.IsLocked) layer.IsLocked = false;
                if (layer.IsFrozen) layer.IsFrozen = false;
            }
            return states;
        }

        private static void RestoreLayerStates(Database database, Transaction transaction,
            IEnumerable<LayerStateSnapshot> states)
        {
            foreach (LayerStateSnapshot state in states)
            {
                try
                {
                    LayerTableRecord layer = transaction.GetObject(state.Id, OpenMode.ForWrite, false) as LayerTableRecord;
                    if (layer == null || layer.IsDependent) continue;
                    layer.IsFrozen = state.IsFrozen;
                    layer.IsLocked = state.IsLocked;
                }
                catch { /* Preserve best-effort restoration without masking the original clone error. */ }
            }
        }

        private static void RestoreMappedLayerStates(Database database, Transaction transaction,
            IDictionary<ObjectId, LayerStateSnapshot> states)
        {
            foreach (KeyValuePair<ObjectId, LayerStateSnapshot> pair in states)
            {
                try
                {
                    LayerTableRecord layer = transaction.GetObject(pair.Key, OpenMode.ForWrite, false) as LayerTableRecord;
                    if (layer == null || layer.IsDependent) continue;
                    layer.IsFrozen = pair.Value.IsFrozen;
                    layer.IsLocked = pair.Value.IsLocked;
                }
                catch { /* A cloned dependent layer may not permit state changes. */ }
            }
        }

        private static ObjectIdCollection EntityIds(Transaction tr, ObjectId blockId)
        {
            return AllEntityIds(tr, blockId);
        }

        private static ObjectIdCollection AllEntityIds(Transaction tr, ObjectId blockId)
        {
            var result = new ObjectIdCollection();
            var block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
            foreach (ObjectId id in block)
            {
                if (id.IsNull || id.IsErased) continue;
                Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (entity == null || entity.IsErased) continue;
                result.Add(id);
            }
            return result;
        }

        private static string UniqueName(string requested, ISet<string> used)
        {
            string name = requested;
            int suffix = 1;
            while (!used.Add(name))
            {
                string tail = " (Imported " + suffix++ + ")";
                name = requested.Substring(0, Math.Min(requested.Length, 255 - tail.Length)) + tail;
            }
            return name;
        }

        private static void CopyPageSetup(Database source, Database destination, Transaction read,
            Transaction write, Layout original, Layout imported)
        {
            if (string.IsNullOrEmpty(original.PlotSettingsName)) return;
            var sourceSetups = (DBDictionary)read.GetObject(source.PlotSettingsDictionaryId, OpenMode.ForRead);
            if (!sourceSetups.Contains(original.PlotSettingsName))
            {
                imported.PlotSettingsName = string.Empty;
                return; // The layout still retains its own copied plot settings.
            }
            var targetSetups = (DBDictionary)write.GetObject(destination.PlotSettingsDictionaryId, OpenMode.ForWrite);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DBDictionaryEntry entry in targetSetups) names.Add(entry.Key);
            var originalSetup = (PlotSettings)read.GetObject(sourceSetups.GetAt(original.PlotSettingsName), OpenMode.ForRead);
            var setup = new PlotSettings(false);
            setup.CopyFrom(originalSetup);
            setup.PlotSettingsName = UniqueName(original.PlotSettingsName, names);
            setup.AddToPlotSettingsDictionary(destination);
            write.AddNewlyCreatedDBObject(setup, true);
            imported.PlotSettingsName = setup.PlotSettingsName;
        }

        private sealed class Sheet
        {
            internal ObjectId Id;
            internal string Name;
            internal int Order;
            public override string ToString() { return Name; }
        }

        private sealed class CloneJob
        {
            internal ObjectIdCollection Ids;
            internal ObjectId Owner;
        }

        private sealed class ViewportCloneJob
        {
            internal ObjectIdCollection Ids;
            internal ObjectId Owner;
            internal string LayoutName;
        }

        private sealed class LayerStateSnapshot
        {
            internal ObjectId Id;
            internal string Name;
            internal bool IsLocked;
            internal bool IsFrozen;
        }

        private sealed class ImportResult
        {
            internal readonly List<string> Names = new List<string>();
            internal readonly List<string> ImportedLayoutNames = new List<string>();
            internal readonly List<string> ViewportErrors = new List<string>();
            internal int ModelCount, PaperCount, ViewportCount, ViewportCreatedCount;
        }

        private sealed class TitleBlockTransferWindow : Wpf.Window
        {
            private readonly WpfControls.ComboBox _sourceLayout;
            private readonly List<WpfControls.CheckBox> _targets = new List<WpfControls.CheckBox>();
            private readonly WpfControls.TextBox _baseX;
            private readonly WpfControls.TextBox _baseY;
            private bool _updatingAll;

            internal Sheet SelectedSourceSheet { get; private set; }
            internal List<string> SelectedDestinationLayouts { get; private set; }
            internal double SourceBaseX { get; private set; }
            internal double SourceBaseY { get; private set; }

            internal TitleBlockTransferWindow(string path, IList<Sheet> sourceSheets, IList<Sheet> destinationSheets)
            {
                Title = "CE Tools - Transfer Title Block";
                Width = 650;
                Height = 700;
                MinWidth = 540;
                MinHeight = 540;
                WindowStartupLocation = Wpf.WindowStartupLocation.CenterOwner;
                ResizeMode = Wpf.ResizeMode.CanResize;

                var root = new WpfControls.DockPanel { Margin = new Wpf.Thickness(18) };
                Content = root;

                var buttons = new WpfControls.StackPanel
                {
                    Orientation = WpfControls.Orientation.Horizontal,
                    HorizontalAlignment = Wpf.HorizontalAlignment.Right,
                    Margin = new Wpf.Thickness(0, 12, 0, 0)
                };
                WpfControls.DockPanel.SetDock(buttons, WpfControls.Dock.Bottom);
                root.Children.Add(buttons);

                var cancel = new WpfControls.Button
                {
                    Content = "Cancel", Width = 90, Margin = new Wpf.Thickness(5), IsCancel = true
                };
                cancel.Click += (s, e) => { DialogResult = false; Close(); };
                buttons.Children.Add(cancel);

                var proceed = new WpfControls.Button
                {
                    Content = "Continue", Width = 105, Margin = new Wpf.Thickness(5), IsDefault = true
                };
                proceed.Click += (s, e) =>
                {
                    SelectedSourceSheet = _sourceLayout.SelectedItem as Sheet;
                    SelectedDestinationLayouts = _targets.Where(c => c.IsChecked == true)
                        .Select(c => Convert.ToString(c.Tag)).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

                    double x, y;
                    if (SelectedSourceSheet == null || SelectedDestinationLayouts.Count == 0)
                    {
                        ShowThemedPickerMessage(this,
                            "Select one source Paper Space tab and at least one destination Paper Space tab.", Title);
                        return;
                    }
                    if (!double.TryParse(_baseX.Text, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out x) ||
                        !double.TryParse(_baseY.Text, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out y))
                    {
                        ShowThemedPickerMessage(this,
                            "Enter valid source base-point X and Y coordinates using decimal points.", Title);
                        return;
                    }

                    SourceBaseX = x;
                    SourceBaseY = y;
                    WriteTransferSetting("TitleBlockSourceLayout", SelectedSourceSheet.Name);
                    WriteTransferSetting("TitleBlockDestinationLayouts", string.Join("|", SelectedDestinationLayouts));
                    WriteTransferSetting("TitleBlockBaseX", x.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    WriteTransferSetting("TitleBlockBaseY", y.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    DialogResult = true;
                    Close();
                };
                buttons.Children.Add(proceed);

                var body = new WpfControls.StackPanel();
                root.Children.Add(body);

                body.Children.Add(new WpfControls.TextBlock
                {
                    Text = "SOURCE DRAWING: " + System.IO.Path.GetFileName(path),
                    FontWeight = Wpf.FontWeights.Bold,
                    Margin = new Wpf.Thickness(0, 0, 0, 10)
                });
                body.Children.Add(new WpfControls.TextBlock
                {
                    Text = "Source Paper Space tab (title block to copy)",
                    Margin = new Wpf.Thickness(0, 2, 0, 5)
                });

                _sourceLayout = new WpfControls.ComboBox
                {
                    Height = 30,
                    Margin = new Wpf.Thickness(0, 0, 0, 10)
                };
                foreach (Sheet sheet in sourceSheets) _sourceLayout.Items.Add(sheet);
                string rememberedSourceLayout = ReadTransferSetting("TitleBlockSourceLayout");
                int rememberedIndex = -1;
                for (int i = 0; i < _sourceLayout.Items.Count; i++)
                {
                    Sheet item = _sourceLayout.Items[i] as Sheet;
                    if (item != null && string.Equals(item.Name, rememberedSourceLayout,
                        StringComparison.OrdinalIgnoreCase)) rememberedIndex = i;
                }
                _sourceLayout.SelectedIndex = rememberedIndex >= 0
                    ? rememberedIndex : (_sourceLayout.Items.Count > 0 ? 0 : -1);
                body.Children.Add(_sourceLayout);

                body.Children.Add(new WpfControls.TextBlock
                {
                    Text = "Source title-block base point (coordinates in source drawing)",
                    Margin = new Wpf.Thickness(0, 4, 0, 5)
                });

                var labels = new WpfControls.Grid();
                labels.ColumnDefinitions.Add(new WpfControls.ColumnDefinition());
                labels.ColumnDefinitions.Add(new WpfControls.ColumnDefinition());
                var xLabel = new WpfControls.TextBlock { Text = "X" };
                var yLabel = new WpfControls.TextBlock { Text = "Y" };
                WpfControls.Grid.SetColumn(xLabel, 0);
                WpfControls.Grid.SetColumn(yLabel, 1);
                labels.Children.Add(xLabel);
                labels.Children.Add(yLabel);
                body.Children.Add(labels);

                var baseGrid = new WpfControls.Grid { Margin = new Wpf.Thickness(0, 0, 0, 10) };
                baseGrid.ColumnDefinitions.Add(new WpfControls.ColumnDefinition());
                baseGrid.ColumnDefinitions.Add(new WpfControls.ColumnDefinition());
                _baseX = new WpfControls.TextBox
                {
                    Text = string.IsNullOrWhiteSpace(ReadTransferSetting("TitleBlockBaseX")) ? "0" : ReadTransferSetting("TitleBlockBaseX"),
                    Height = 28, Margin = new Wpf.Thickness(0, 0, 10, 0)
                };
                _baseY = new WpfControls.TextBox
                {
                    Text = string.IsNullOrWhiteSpace(ReadTransferSetting("TitleBlockBaseY")) ? "0" : ReadTransferSetting("TitleBlockBaseY"),
                    Height = 28
                };
                WpfControls.Grid.SetColumn(_baseX, 0);
                WpfControls.Grid.SetColumn(_baseY, 1);
                baseGrid.Children.Add(_baseX);
                baseGrid.Children.Add(_baseY);
                body.Children.Add(baseGrid);

                // Keep the section heading and destination-tab checklist. Only the two
                // destination-method mode checkboxes are intentionally omitted.
                body.Children.Add(new WpfControls.TextBlock
                {
                    Text = "DESTINATION METHOD",
                    FontWeight = Wpf.FontWeights.Bold,
                    Margin = new Wpf.Thickness(0, 5, 0, 5)
                });
                body.Children.Add(new WpfControls.TextBlock
                {
                    Text = "Select destination Paper Space tabs",
                    Margin = new Wpf.Thickness(0, 2, 0, 5)
                });

                var rememberedTargets = new HashSet<string>(
                    (ReadTransferSetting("TitleBlockDestinationLayouts") ?? string.Empty)
                        .Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.OrdinalIgnoreCase);

                var selectAll = new WpfControls.CheckBox
                {
                    Content = "Select all destination tabs",
                    FontWeight = Wpf.FontWeights.Bold,
                    Margin = new Wpf.Thickness(0, 2, 0, 6)
                };
                selectAll.Checked += (s, e) => { if (!_updatingAll) SetTargets(true); };
                selectAll.Unchecked += (s, e) => { if (!_updatingAll) SetTargets(false); };
                body.Children.Add(selectAll);

                var targetList = new WpfControls.StackPanel();
                foreach (Sheet sheet in destinationSheets ?? new List<Sheet>())
                {
                    var check = new WpfControls.CheckBox
                    {
                        Content = sheet.Name,
                        Tag = sheet.Name,
                        IsChecked = rememberedTargets.Contains(sheet.Name),
                        Margin = new Wpf.Thickness(3, 4, 3, 4)
                    };
                    _targets.Add(check);
                    check.Checked += (s, e) => UpdateSelectAll(selectAll);
                    check.Unchecked += (s, e) => UpdateSelectAll(selectAll);
                    targetList.Children.Add(check);
                }
                selectAll.IsChecked = _targets.Count > 0 && _targets.All(x => x.IsChecked == true);
                body.Children.Add(new WpfControls.Border
                {
                    BorderThickness = new Wpf.Thickness(1),
                    Padding = new Wpf.Thickness(8),
                    Child = new WpfControls.ScrollViewer
                    {
                        VerticalScrollBarVisibility = WpfControls.ScrollBarVisibility.Auto,
                        Height = 155,
                        Content = targetList
                    }
                });

                body.Children.Add(new WpfControls.TextBlock
                {
                    Text = "Continue, then pick the insertion point on screen. The selected title block will be copied to every checked destination Paper Space tab.",
                    TextWrapping = Wpf.TextWrapping.Wrap,
                    Margin = new Wpf.Thickness(0, 10, 0, 0)
                });

                Loaded += (s, e) => CeInterfaceTheme.Apply(this);
            }

            private void SetTargets(bool value)
            {
                _updatingAll = true;
                foreach (WpfControls.CheckBox item in _targets) item.IsChecked = value;
                _updatingAll = false;
            }

            private void UpdateSelectAll(WpfControls.CheckBox selectAll)
            {
                if (_updatingAll) return;
                _updatingAll = true;
                selectAll.IsChecked = _targets.Count > 0 && _targets.All(x => x.IsChecked == true);
                _updatingAll = false;
            }
        }

        private sealed class SheetPicker : Wpf.Window
        {
            private readonly List<WpfControls.CheckBox> _choices = new List<WpfControls.CheckBox>();
            private bool _updating;
            private string _selectedMode = "COMPLETE";
            internal List<Sheet> SelectedSheets { get; private set; }
            internal string SelectedTransferMode { get; private set; }

            internal SheetPicker(string path, IList<Sheet> sheets, string selectedNames, string selectedMode)
            {
                Title = "CE Tools - Import Paper Space Tabs";
                Width = 650; Height = 570; MinWidth = 520; MinHeight = 420;
                WindowStartupLocation = Wpf.WindowStartupLocation.CenterOwner;
                ResizeMode = Wpf.ResizeMode.CanResize;

                var root = new WpfControls.DockPanel { Margin = new Wpf.Thickness(18) };
                Content = root;
                var buttons = new WpfControls.StackPanel
                {
                    Orientation = WpfControls.Orientation.Horizontal,
                    HorizontalAlignment = Wpf.HorizontalAlignment.Right,
                    Margin = new Wpf.Thickness(0, 12, 0, 0)
                };
                WpfControls.DockPanel.SetDock(buttons, WpfControls.Dock.Bottom);
                root.Children.Add(buttons);

                var cancel = new WpfControls.Button { Content = "Cancel", Width = 90, Margin = new Wpf.Thickness(5), IsCancel = true };
                cancel.Click += (s, e) => { DialogResult = false; Close(); };
                buttons.Children.Add(cancel);
                var import = new WpfControls.Button { Content = "Import selected tabs", Width = 145, Margin = new Wpf.Thickness(5), IsDefault = true };
                import.Click += (s, e) =>
                {
                    SelectedSheets = _choices.Where(c => c.IsChecked == true)
                        .Select(c => c.Tag as Sheet).Where(x => x != null).ToList();
                    SelectedTransferMode = _selectedMode;
                    if (SelectedSheets.Count == 0)
                    {
                        ShowThemedPickerMessage(this, "Select at least one Paper Space tab.", Title);
                        return;
                    }
                    DialogResult = true;
                    Close();
                };
                buttons.Children.Add(import);

                var body = new WpfControls.StackPanel();
                root.Children.Add(body);
                body.Children.Add(new WpfControls.TextBlock
                {
                    Text = "SOURCE DRAWING: " + System.IO.Path.GetFileName(path) +
                        "\nChoose what to transfer, then tick one, several or all source Paper Space tabs.",
                    TextWrapping = Wpf.TextWrapping.Wrap,
                    Margin = new Wpf.Thickness(0, 0, 0, 12)
                });
                body.Children.Add(new WpfControls.TextBlock
                {
                    Text = "TRANSFER MODE",
                    FontSize = 11,
                    Margin = new Wpf.Thickness(0, 2, 0, 6)
                });
                var modePanel = new WpfControls.StackPanel { Margin = new Wpf.Thickness(0, 0, 0, 10) };
                _selectedMode = selectedMode == "MODEL" || selectedMode == "TITLE" ? selectedMode : "COMPLETE";
                var completeMode = new WpfControls.CheckBox
                {
                    Content = "Complete Paper Space transfer",
                    Margin = new Wpf.Thickness(3, 4, 3, 4),
                    IsChecked = _selectedMode == "COMPLETE",
                    IsThreeState = false, IsEnabled = true, IsHitTestVisible = true, Focusable = true
                };
                var modelMode = new WpfControls.CheckBox
                {
                    Content = "Model Space content only (copy source model once for selected viewports)",
                    Margin = new Wpf.Thickness(3, 4, 3, 4),
                    IsChecked = _selectedMode == "MODEL",
                    IsThreeState = false, IsEnabled = true, IsHitTestVisible = true, Focusable = true
                };
                var titleMode = new WpfControls.CheckBox
                {
                    Content = "Title blocks only (import source tabs; no matching names required)",
                    Margin = new Wpf.Thickness(3, 4, 3, 4),
                    IsChecked = _selectedMode == "TITLE",
                    IsThreeState = false, IsEnabled = true, IsHitTestVisible = true, Focusable = true
                };
                bool changingMode = false;
                completeMode.Checked += (sender, args) =>
                {
                    if (changingMode) return;
                    changingMode = true;
                    modelMode.IsChecked = false; titleMode.IsChecked = false;
                    _selectedMode = "COMPLETE";
                    changingMode = false;
                };
                modelMode.Checked += (sender, args) =>
                {
                    if (changingMode) return;
                    changingMode = true;
                    completeMode.IsChecked = false; titleMode.IsChecked = false;
                    _selectedMode = "MODEL";
                    changingMode = false;
                };
                titleMode.Checked += (sender, args) =>
                {
                    if (changingMode) return;
                    changingMode = true;
                    completeMode.IsChecked = false; modelMode.IsChecked = false;
                    _selectedMode = "TITLE";
                    changingMode = false;
                };
                modePanel.Children.Add(completeMode);
                modePanel.Children.Add(modelMode);
                modePanel.Children.Add(titleMode);
                body.Children.Add(modePanel);

                var remembered = new HashSet<string>((selectedNames ?? string.Empty).Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
                var all = new WpfControls.CheckBox { Content = "Select all Paper Space tabs", FontWeight = Wpf.FontWeights.Bold, Margin = new Wpf.Thickness(0, 0, 0, 8) };
                all.Checked += (s, e) => { if (!_updating) SetChecks(true); };
                all.Unchecked += (s, e) => { if (!_updating) SetChecks(false); };
                body.Children.Add(all);
                var list = new WpfControls.StackPanel();
                foreach (Sheet sheet in sheets)
                {
                    var check = new WpfControls.CheckBox { Content = sheet.Name, Tag = sheet, Margin = new Wpf.Thickness(3, 4, 3, 4), IsChecked = remembered.Count == 0 ? true : remembered.Contains(sheet.Name) };
                    _choices.Add(check);
                    check.Checked += (s, e) => UpdateAll(all);
                    check.Unchecked += (s, e) => UpdateAll(all);
                    list.Children.Add(check);
                }
                all.IsChecked = _choices.Count > 0 && _choices.All(x => x.IsChecked == true);
                body.Children.Add(new WpfControls.Border
                {
                    BorderThickness = new Wpf.Thickness(1),
                    Padding = new Wpf.Thickness(8),
                    Child = new WpfControls.ScrollViewer
                    {
                        VerticalScrollBarVisibility = WpfControls.ScrollBarVisibility.Auto,
                        Content = list
                    }
                });
                Loaded += (s, e) => CeInterfaceTheme.Apply(this);
            }

            private void SetChecks(bool value)
            {
                _updating = true;
                foreach (var item in _choices) item.IsChecked = value;
                _updating = false;
            }

            private void UpdateAll(WpfControls.CheckBox all)
            {
                if (_updating) return;
                _updating = true;
                all.IsChecked = _choices.Count > 0 && _choices.All(x => x.IsChecked == true);
                _updating = false;
            }
        }
    }
}
