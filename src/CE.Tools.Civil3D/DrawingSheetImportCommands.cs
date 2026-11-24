using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <summary>Imports sheets and their model without opening an editor document.</summary>
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

                        if (string.Equals(picker.SelectedTransferMode, "MODEL", StringComparison.OrdinalIgnoreCase))
                        {
                            int copied;
                            using (document.LockDocument())
                                copied = CopyVisibleViewportModelContent(source, document.Database, selected);
                            try { document.Editor.Regen(); } catch { }
                            Show("Model Space viewport-content transfer completed.\\n\\nPaper Space tabs selected: " +
                                selected.Count + "\\nEvery active viewport examined.\\nModel Space objects copied: " + copied +
                                "\\nObjects retain their original Model Space coordinates. Paper Space objects and title blocks were not copied.");
                        }
                        else if (string.Equals(picker.SelectedTransferMode, "TITLE", StringComparison.OrdinalIgnoreCase))
                        {
                            List<Sheet> destinationSheets = ReadSheets(document.Database);
                            var destinationByName = destinationSheets.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
                            int copied = 0;
                            var reports = new List<string>();
                            using (document.LockDocument())
                            {
                                foreach (Sheet sourceSheet in selected)
                                {
                                    Sheet destinationSheet;
                                    if (!destinationByName.TryGetValue(sourceSheet.Name, out destinationSheet))
                                    {
                                        reports.Add(sourceSheet.Name + ": no destination layout with the same name; skipped.");
                                        continue;
                                    }
                                    string imageReport;
                                    int count = CopyTitleBlock(source, document.Database, sourceSheet,
                                        new Vector3d(0, 0, 0), path, destinationSheet.Name, out imageReport);
                                    copied += count;
                                    reports.Add(sourceSheet.Name + ": " + count + " title-block objects copied.");
                                }
                            }
                            try { document.Editor.Regen(); } catch { }
                            Show("Title-block-only transfer completed. Objects copied: " + copied + ".\\n\\n" +
                                string.Join("\\n", reports) +
                                "\\n\\nOnly non-viewport Paper Space entities were copied, at their original Paper Space coordinates.");
                        }
                        else
                        {
                            ImportResult result;
                            using (document.LockDocument())
                                result = CopySheets(source, document.Database, selected);
                            try { document.Editor.Regen(); }
                            catch { /* The import is already committed; report it as successful. */ }
                            Show("Imported " + result.Names.Count + " layout(s), " + result.PaperCount +
                                " paper-space objects (including " + result.ViewportCount + " viewports), and " +
                                result.ModelCount + " model-space objects.\\n\\n" + string.Join("\\n", result.Names) +
                                "\\n\\nThe model was copied once at its original coordinates. Existing model geometry may also appear in these views." +
                                "\\nExternal references, images and data links still require their supporting files. Printer configurations and fonts must be installed locally.");
                        }
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
            var selectedIds = new HashSet<ObjectId>();
            using (Transaction tr = source.TransactionManager.StartTransaction())
            {
                BlockTable blocks = (BlockTable)tr.GetObject(source.BlockTableId, OpenMode.ForRead);
                ObjectId modelId = blocks[BlockTableRecord.ModelSpace];
                BlockTableRecord model = (BlockTableRecord)tr.GetObject(modelId, OpenMode.ForRead);
                var modelEntities = new List<Entity>();
                foreach (ObjectId id in model)
                {
                    Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity != null && !entity.IsErased) modelEntities.Add(entity);
                }

                foreach (Sheet sheet in sheets)
                {
                    Layout layout = tr.GetObject(sheet.Id, OpenMode.ForRead) as Layout;
                    if (layout == null) continue;
                    BlockTableRecord paper = tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead) as BlockTableRecord;
                    if (paper == null) continue;
                    foreach (ObjectId paperId in paper)
                    {
                        Autodesk.AutoCAD.DatabaseServices.Viewport viewport = tr.GetObject(paperId, OpenMode.ForRead, false) as Autodesk.AutoCAD.DatabaseServices.Viewport;
                        if (viewport == null || viewport.Number <= 1 || !viewport.On || viewport.PerspectiveOn ||
                            viewport.Width <= 0 || viewport.Height <= 0 || viewport.ViewHeight <= 0) continue;
                        List<Point3d> polygon = GetViewportModelPolygon(viewport);
                        if (polygon.Count < 3) continue;
                        foreach (Entity entity in modelEntities)
                        {
                            try
                            {
                                Extents3d ext = entity.GeometricExtents;
                                if (ExtentsIntersectsViewport(ext, polygon)) selectedIds.Add(entity.ObjectId);
                            }
                            catch { /* Non-extents entities cannot be spatially tested reliably. */ }
                        }
                    }
                }
                tr.Commit();
            }

            if (selectedIds.Count == 0) return 0;
            var ids = new ObjectIdCollection();
            foreach (ObjectId id in selectedIds) ids.Add(id);
            using (Transaction write = destination.TransactionManager.StartTransaction())
            {
                BlockTable targetBlocks = (BlockTable)write.GetObject(destination.BlockTableId, OpenMode.ForRead);
                ObjectId targetModel = targetBlocks[BlockTableRecord.ModelSpace];
                using (var mapping = new IdMapping())
                {
                    source.WblockCloneObjects(ids, targetModel, mapping, DuplicateRecordCloning.MangleName, false);
                    int count = 0;
                    foreach (ObjectId id in ids)
                    {
                        IdPair pair = mapping[id];
                        if (!pair.IsCloned || pair.Value.IsNull || pair.Value.IsErased)
                            throw new InvalidOperationException("A visible viewport object could not be copied: " + id.Handle);
                        count++;
                    }
                    write.Commit();
                    return count;
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
                    if (sourceSheets.Count == 0 || destinationSheets.Count == 0)
                    {
                        Show("The source or current drawing does not contain paper-space layouts.");
                        return;
                    }

                    var picker = new TitleBlockTransferWindow(path, sourceSheets, destinationSheets);
                    if (AcApplication.ShowModalWindow(picker) != true) return;
                    Sheet sourceSheet = picker.SelectedSourceSheet;
                    List<string> targets = picker.SelectedDestinationLayouts;
                    WriteTransferSetting("TitleBlockSourceLayout", sourceSheet == null ? string.Empty : sourceSheet.Name);
                    WriteTransferSetting("TitleBlockDestinationLayouts", string.Join("|", targets ?? new List<string>()));
                    if (sourceSheet == null || targets.Count == 0) return;

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

                    if (document.Database.TileMode)
                    {
                        Show("Activate a paper-space layout in the destination drawing first.");
                        return;
                    }

                    int totalCopied = 0;
                    var reports = new List<string>();
                    using (document.LockDocument())
                    {
                        foreach (string layoutName in targets)
                        {
                            string imageReport;
                            int copied = CopyTitleBlock(source, document.Database, sourceSheet,
                                displacement, path, layoutName, out imageReport);
                            totalCopied += copied;
                            reports.Add(layoutName + ": " + copied + " objects copied.");
                            if (!string.IsNullOrWhiteSpace(imageReport)) reports.Add(imageReport);
                        }
                    }
                    document.Editor.Regen();
                    Show("Copied title block from '" + sourceSheet.Name + "' to " + targets.Count +
                        " destination layout(s). Total objects copied: " + totalCopied + ".\n\n" +
                        string.Join("\n", reports) +
                        "\n\nSource base point: (" + sourceBase.X.ToString("0.###") + ", " +
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
                        int count;
                        using (var clonedLayers = new SheetCloneLayerScope(read, write, mapping))
                        {
                        Matrix3d shift = Matrix3d.Displacement(displacement);
                        count = 0;
                        foreach (ObjectId id in ids)
                        {
                            IdPair pair = mapping[id];
                            if (!pair.IsCloned || pair.Value.IsNull || pair.Value.IsErased)
                                throw new InvalidOperationException("Title-block entity could not be cloned: " + id.Handle);
                            Entity clone = write.GetObject(pair.Value, OpenMode.ForWrite, false, true) as Entity;
                            if (clone == null) continue;
                            if (displacement.Length > 1e-9) clone.TransformBy(shift);
                            count++;
                        }
                        imageReport = RelinkRasterLogos(source, destination, read, write,
                            ids, mapping, sourceDwgPath);
                        } // restore layer flags before committing
                        write.Commit();
                        return count;
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
            CeMessageDialog.Show("Import Sheets", message);
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

        private static ImportResult CopySheets(Database source, Database destination, IList<Sheet> sheets)
        {
            var result = new ImportResult();
            using (Transaction read = source.TransactionManager.StartTransaction())
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
                var jobs = new List<CloneJob>();
                ObjectIdCollection modelIds = EntityIds(read, sourceBlocks[BlockTableRecord.ModelSpace]);
                // Exactly one model-space clone, shared by every imported viewport.
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
                    // Copy layout settings before registering it. Registering afterwards binds
                    // the imported layout to the destination-owned Paper Space BTR, not the source BTR.
                    imported.CopyFrom(original);
                    imported.LayoutName = name;
                    imported.AddToLayoutDictionary(destination, paper.ObjectId);
                    write.AddNewlyCreatedDBObject(imported, true);
                    imported.TabOrder = ++tab;
                    CopyPageSetup(source, destination, read, write, original, imported);
                    ObjectIdCollection paperIds = EntityIds(read, original.BlockTableRecordId);
                    if (paperIds.Count == 0)
                        throw new InvalidOperationException("Source layout '" + sheet.Name +
                            "' contains no cloneable Paper Space entities. Check that the source DWG is saved and that the layout contains objects.");
                    result.PaperCount += paperIds.Count;
                    int layoutViewportCount = 0;
                    foreach (ObjectId id in paperIds)
                        if (read.GetObject(id, OpenMode.ForRead) is Autodesk.AutoCAD.DatabaseServices.Viewport) { result.ViewportCount++; layoutViewportCount++; }
                    jobs.Add(new CloneJob { Ids = paperIds, Owner = paper.ObjectId });
                    result.Names.Add(name + " [Paper Space objects: " + paperIds.Count +
                        ", floating viewports: " + layoutViewportCount + "]");
                }

                // Clone each owner batch with its own IdMapping. Reusing one mapping across
                // Model Space and several Paper Space owners can leave AutoCAD's clone context
                // in an invalid state when multiple layouts are selected.
                foreach (CloneJob job in jobs.Where(j => j.Ids.Count > 0))
                {
                    using (var mapping = new IdMapping())
                    {
                        using (var sourceLayers = new SheetCloneLayerScope(source, read))
                            source.WblockCloneObjects(job.Ids, job.Owner, mapping,
                                DuplicateRecordCloning.MangleName, false);
                        using (var clonedLayers = new SheetCloneLayerScope(read, write, mapping))
                        {
                        foreach (ObjectId id in job.Ids)
                        {
                            IdPair pair = mapping[id];
                            if (!pair.IsCloned || pair.Value.IsNull || pair.Value.IsErased)
                                throw new InvalidOperationException("A source object could not be copied: " + id.Handle);
                            Autodesk.AutoCAD.DatabaseServices.Viewport sourceViewport = read.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Viewport;
                            if (sourceViewport == null || sourceViewport.Number == 1) continue;
                            Autodesk.AutoCAD.DatabaseServices.Viewport clonedViewport = write.GetObject(pair.Value, OpenMode.ForWrite, false, true) as Autodesk.AutoCAD.DatabaseServices.Viewport;
                            if (clonedViewport == null) continue;
                            clonedViewport.ViewCenter = sourceViewport.ViewCenter;
                            clonedViewport.ViewTarget = sourceViewport.ViewTarget;
                            clonedViewport.ViewDirection = sourceViewport.ViewDirection;
                            clonedViewport.ViewHeight = sourceViewport.ViewHeight;
                            clonedViewport.TwistAngle = sourceViewport.TwistAngle;
                            clonedViewport.CustomScale = sourceViewport.CustomScale;
                            clonedViewport.PerspectiveOn = sourceViewport.PerspectiveOn;
                            clonedViewport.Locked = sourceViewport.Locked;
                            clonedViewport.On = sourceViewport.On;
                        }
                        } // restore cloned layer flags
                    }
                }
                // Layouts, model, viewports and dependent definitions commit together or roll back together.
                write.Commit();
            }
            return result;
        }

        private static ObjectIdCollection EntityIds(Transaction tr, ObjectId blockId)
        {
            var result = new ObjectIdCollection();
            var block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
            foreach (ObjectId id in block)
            {
                if (id.IsErased) continue;
                Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (entity == null) continue;
                Autodesk.AutoCAD.DatabaseServices.Viewport viewport = entity as Autodesk.AutoCAD.DatabaseServices.Viewport;
                // Number 1 is the special overall paper-space viewport, not a floating viewport to import.
                if (viewport != null && viewport.Number <= 1) continue;
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

        private sealed class ImportResult
        {
            internal readonly List<string> Names = new List<string>();
            internal int ModelCount, PaperCount, ViewportCount;
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
                Width = 650; Height = 650; MinWidth = 540; MinHeight = 500;
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

                var proceed = new WpfControls.Button { Content = "Continue", Width = 105, Margin = new Wpf.Thickness(5), IsDefault = true };
                proceed.Click += (s, e) =>
                {
                    SelectedSourceSheet = _sourceLayout.SelectedItem as Sheet;
                    SelectedDestinationLayouts = _targets.Where(c => c.IsChecked == true)
                        .Select(c => Convert.ToString(c.Tag)).ToList();
                    double x, y;
                    if (SelectedSourceSheet == null || SelectedDestinationLayouts.Count == 0)
                    {
                        CeMessageDialog.Show(Title, "Select one source layout and at least one destination layout.", false, this);
                        return;
                    }
                    if (!double.TryParse(_baseX.Text, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out x) ||
                        !double.TryParse(_baseY.Text, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out y))
                    {
                        CeMessageDialog.Show(Title, "Enter valid source base-point X and Y coordinates using decimal points.", false, this);
                        return;
                    }
                    SourceBaseX = x; SourceBaseY = y;
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
                body.Children.Add(new WpfControls.TextBlock { Text = "Source Paper Space tab (title block to copy)", Margin = new Wpf.Thickness(0, 2, 0, 5) });
                _sourceLayout = new WpfControls.ComboBox { Height = 30, Margin = new Wpf.Thickness(0, 0, 0, 10) };
                foreach (Sheet sheet in sourceSheets) _sourceLayout.Items.Add(sheet);
                string rememberedSourceLayout = ReadTransferSetting("TitleBlockSourceLayout");
                if (_sourceLayout.Items.Count > 0)
                {
                    int rememberedIndex = -1;
                    for (int i = 0; i < _sourceLayout.Items.Count; i++)
                        if (string.Equals(((_sourceLayout.Items[i] as Sheet)?.Name), rememberedSourceLayout, StringComparison.OrdinalIgnoreCase)) rememberedIndex = i;
                    _sourceLayout.SelectedIndex = rememberedIndex >= 0 ? rememberedIndex : 0;
                }
                body.Children.Add(_sourceLayout);

                body.Children.Add(new WpfControls.TextBlock
                {
                    Text = "Source title-block base point (coordinates in source drawing)",
                    Margin = new Wpf.Thickness(0, 4, 0, 5)
                });
                var baseGrid = new WpfControls.Grid { Margin = new Wpf.Thickness(0, 0, 0, 10) };
                baseGrid.ColumnDefinitions.Add(new WpfControls.ColumnDefinition());
                baseGrid.ColumnDefinitions.Add(new WpfControls.ColumnDefinition());
                baseGrid.ColumnDefinitions.Add(new WpfControls.ColumnDefinition());
                var xLabel = new WpfControls.TextBlock { Text = "X", VerticalAlignment = Wpf.VerticalAlignment.Center };
                var yLabel = new WpfControls.TextBlock { Text = "Y", VerticalAlignment = Wpf.VerticalAlignment.Center };
                _baseX = new WpfControls.TextBox { Text = string.IsNullOrWhiteSpace(ReadTransferSetting("TitleBlockBaseX")) ? "0" : ReadTransferSetting("TitleBlockBaseX"), Height = 28, Margin = new Wpf.Thickness(4, 0, 12, 0) };
                _baseY = new WpfControls.TextBox { Text = string.IsNullOrWhiteSpace(ReadTransferSetting("TitleBlockBaseY")) ? "0" : ReadTransferSetting("TitleBlockBaseY"), Height = 28, Margin = new Wpf.Thickness(4, 0, 0, 0) };
                WpfControls.Grid.SetColumn(xLabel, 0); WpfControls.Grid.SetColumn(_baseX, 0);
                WpfControls.Grid.SetColumn(yLabel, 1); WpfControls.Grid.SetColumn(_baseY, 1);
                baseGrid.Children.Add(_baseX); baseGrid.Children.Add(_baseY);
                var labels = new WpfControls.Grid();
                labels.ColumnDefinitions.Add(new WpfControls.ColumnDefinition());
                labels.ColumnDefinitions.Add(new WpfControls.ColumnDefinition());
                WpfControls.Grid.SetColumn(xLabel, 0); WpfControls.Grid.SetColumn(yLabel, 1);
                labels.Children.Add(xLabel); labels.Children.Add(yLabel);
                body.Children.Add(labels);
                body.Children.Add(baseGrid);

                body.Children.Add(new WpfControls.TextBlock
                {
                    Text = "Destination Paper Space tabs in the current drawing",
                    Margin = new Wpf.Thickness(0, 5, 0, 5)
                });
                var selectAll = new WpfControls.CheckBox { Content = "Select all destination tabs", FontWeight = Wpf.FontWeights.Bold, Margin = new Wpf.Thickness(0, 2, 0, 6) };
                selectAll.Checked += (s, e) => { if (!_updatingAll) SetTargets(true); };
                selectAll.Unchecked += (s, e) => { if (!_updatingAll) SetTargets(false); };
                body.Children.Add(selectAll);

                var rememberedTargets = new HashSet<string>((ReadTransferSetting("TitleBlockDestinationLayouts") ?? string.Empty).Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
                var list = new WpfControls.StackPanel();
                foreach (Sheet sheet in destinationSheets)
                {
                    var check = new WpfControls.CheckBox
                    {
                        Content = sheet.Name, Tag = sheet.Name,
                        IsChecked = rememberedTargets.Contains(sheet.Name),
                        Margin = new Wpf.Thickness(3, 4, 3, 4)
                    };
                    _targets.Add(check);
                    check.Checked += (s, e) => UpdateAll(selectAll);
                    check.Unchecked += (s, e) => UpdateAll(selectAll);
                    list.Children.Add(check);
                }
                selectAll.IsChecked = _targets.Count > 0 && _targets.All(x => x.IsChecked == true);
                body.Children.Add(new WpfControls.Border
                {
                    BorderThickness = new Wpf.Thickness(1),
                    Padding = new Wpf.Thickness(8),
                    Child = new WpfControls.ScrollViewer
                    {
                        VerticalScrollBarVisibility = WpfControls.ScrollBarVisibility.Auto,
                        Height = 190,
                        Content = list
                    }
                });
                body.Children.Add(new WpfControls.TextBlock
                {
                    Text = "Continue, then pick the insertion point on screen. The same title block will be copied to every checked destination layout.",
                    TextWrapping = Wpf.TextWrapping.Wrap,
                    Margin = new Wpf.Thickness(0, 10, 0, 0)
                });
                Loaded += (s, e) => CeInterfaceTheme.Apply(this);
            }

            private void SetTargets(bool value)
            {
                _updatingAll = true;
                foreach (var item in _targets) item.IsChecked = value;
                _updatingAll = false;
            }

            private void UpdateAll(WpfControls.CheckBox selectAll)
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
                        CeMessageDialog.Show(Title, "Select at least one Paper Space tab.", false, this);
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
                    Content = "Model Space content only (every viewport)",
                    Margin = new Wpf.Thickness(3, 4, 3, 4),
                    IsChecked = _selectedMode == "MODEL",
                    IsThreeState = false, IsEnabled = true, IsHitTestVisible = true, Focusable = true
                };
                var titleMode = new WpfControls.CheckBox
                {
                    Content = "Title blocks only (matching destination tab names)",
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
