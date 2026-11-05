using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.SectionViewManagementCommands))]
namespace CETools.Civil3D
{
    /// <summary>Cross-section view operations, separate from profile view commands.</summary>
    public sealed class SectionViewManagementCommands
    {
        private sealed class ViewInfo
        {
            internal ObjectId Id;
            internal string Name;
            internal string Road;
            internal Point3d Lower;
            internal Point3d Upper;
            internal double Width { get { return Math.Max(1, Upper.X - Lower.X); } }
            internal double Height { get { return Math.Max(1, Upper.Y - Lower.Y); } }
        }

        private static List<ViewInfo> FindViews(Document document)
        {
            var result = new List<ViewInfo>();
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = (BlockTable)tr.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                BlockTableRecord model = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in model)
                {
                    SectionView view = tr.GetObject(id, OpenMode.ForRead, false) as SectionView;
                    if (view == null || view.IsErased) continue;
                    Extents3d bounds;
                    try { bounds = view.GeometricExtents; }
                    catch { continue; }
                    string name = view.Name ?? string.Empty;
                    string road = RoadName(name);
                    string sampleName = ResolveSampleLineName(tr, view);
                    if (!string.IsNullOrEmpty(sampleName)) road = RoadName(sampleName);
                    result.Add(new ViewInfo { Id = id, Name = name, Road = road,
                        Lower = bounds.MinPoint, Upper = bounds.MaxPoint });
                }
            }
            return result;
        }

        internal static HashSet<ObjectId> CaptureViewIds(Document doc)
        {
            return new HashSet<ObjectId>(FindViews(doc).Select(x => x.Id));
        }

        internal static int ApplyCreatedSectionViewStyles(Document doc,
            HashSet<ObjectId> previous, CivilDocument civil, string sectionStyle, string bandSet)
        {
            ObjectId sectionStyleId = ResolveStyleId(doc.Database,
                civil.Styles.SectionViewStyles, sectionStyle);
            ObjectId bandStyleId = ResolveStyleId(doc.Database,
                civil.Styles.SectionViewBandSetStyles, bandSet);
            int updated = 0;
            List<ViewInfo> created = FindViews(doc).Where(x => !previous.Contains(x.Id)).ToList();
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ViewInfo item in created)
                {
                    try
                    {
                        SectionView view = tr.GetObject(item.Id, OpenMode.ForWrite, false) as SectionView;
                        if (view == null) continue;
                        bool applied = false;
                        if (!sectionStyleId.IsNull)
                        {
                            view.StyleId = sectionStyleId;
                            applied = true;
                        }
                        if (!bandStyleId.IsNull)
                            applied = TrySetProperty(view, "BandSetStyleId", bandStyleId) || applied;
                        if (applied) updated++;
                    }
                    catch { }
                }
                tr.Commit();
            }
            return updated;
        }

        private static ObjectId ResolveStyleId(Database database, object styleCollection, string desired)
        {
            if (styleCollection == null || string.IsNullOrWhiteSpace(desired)) return ObjectId.Null;
            var entries = new List<object>();
            var enumerable = styleCollection as System.Collections.IEnumerable;
            if (enumerable != null) foreach (object item in enumerable) entries.Add(item);
            if (entries.Count == 0)
            {
                try
                {
                    var countProperty = styleCollection.GetType().GetProperty("Count");
                    var indexer = styleCollection.GetType().GetProperty("Item");
                    int count = countProperty == null ? 0 : Convert.ToInt32(countProperty.GetValue(styleCollection, null));
                    for (int i = 0; i < count; i++)
                        if (indexer != null) entries.Add(indexer.GetValue(styleCollection, new object[] { i }));
                }
                catch { }
            }
            using (Transaction tr = database.TransactionManager.StartTransaction())
            {
                foreach (object entry in entries)
                {
                    try
                    {
                        ObjectId id;
                        if (entry is ObjectId) id = (ObjectId)entry;
                        else
                        {
                            var property = entry == null ? null : entry.GetType().GetProperty("ObjectId");
                            if (property == null) continue;
                            object raw = property.GetValue(entry, null);
                            if (!(raw is ObjectId)) continue;
                            id = (ObjectId)raw;
                        }
                        DBObject style = tr.GetObject(id, OpenMode.ForRead, false);
                        PropertyInfo nameProperty = style.GetType().GetProperty("Name");
                        if (nameProperty != null &&
                            string.Equals(nameProperty.GetValue(style, null) as string,
                                desired, StringComparison.OrdinalIgnoreCase))
                            return id;
                    }
                    catch { }
                }
            }
            return ObjectId.Null;
        }

        internal static int ArrangeGeneratedByRoad(Document doc, HashSet<ObjectId> previous,
            Point3d start, int sectionsPerRow, int roadGroupsPerRow,
            double hGap, double vGap, bool titles)
        {
            List<ViewInfo> created = FindViews(doc).Where(x => !previous.Contains(x.Id)).ToList();
            if (created.Count == 0) return 0;
            var groups = created.GroupBy(x => x.Road)
                .OrderBy(x => RoadOrdinal(x.Key)).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new { Road = x.Key,
                    Items = x.OrderBy(y => y.Name, StringComparer.OrdinalIgnoreCase).ToList() }).ToList();
            var planned = new List<Tuple<ViewInfo, double, double>>();
            var titlePositions = new List<Tuple<string, double, double>>();
            double groupRowTop = start.Y;
            int groupColumns = Math.Max(1, roadGroupsPerRow);
            int sectionColumns = Math.Max(1, sectionsPerRow);
            for (int g = 0; g < groups.Count;)
            {
                var groupRow = groups.Skip(g).Take(groupColumns).ToList();
                double x = start.X, maximumHeight = 0;
                foreach (var group in groupRow)
                {
                    int index = 0;
                    double localY = groupRowTop - (titles ? 8.0 : 0.0);
                    double maxWidth = 0;
                    titlePositions.Add(Tuple.Create(group.Road, x, groupRowTop));
                    while (index < group.Items.Count)
                    {
                        var row = group.Items.Skip(index).Take(sectionColumns).ToList();
                        double rowX = x, maxSectionHeight = row.Max(v => v.Height);
                        foreach (ViewInfo item in row)
                        {
                            planned.Add(Tuple.Create(item, rowX, localY - item.Height));
                            rowX += item.Width + hGap;
                        }
                        maxWidth = Math.Max(maxWidth, rowX - x);
                        localY -= maxSectionHeight + vGap;
                        index += row.Count;
                    }
                    maximumHeight = Math.Max(maximumHeight, groupRowTop - localY);
                    x += maxWidth + hGap;
                }
                groupRowTop -= maximumHeight + vGap;
                g += groupRow.Count;
            }
            int moved = 0;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var placement in planned)
                {
                    try
                    {
                        SectionView view = tr.GetObject(placement.Item1.Id, OpenMode.ForWrite, false) as SectionView;
                        if (view != null && MoveTo(view,
                            new Point3d(placement.Item2, placement.Item3, placement.Item1.Lower.Z))) moved++;
                    }
                    catch { }
                }
                if (titles)
                {
                    BlockTable table = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord model = (BlockTableRecord)tr.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    foreach (var title in titlePositions)
                    {
                        var text = new MText
                        {
                            Contents = title.Item1,
                            TextHeight = 3.0,
                            Location = new Point3d(title.Item2, title.Item3, 0),
                            Attachment = AttachmentPoint.TopLeft
                        };
                        model.AppendEntity(text);
                        tr.AddNewlyCreatedDBObject(text, true);
                    }
                }
                tr.Commit();
            }
            return moved;
        }

        private static string ResolveSampleLineName(Transaction tr, SectionView view)
        {
            foreach (string propertyName in new[] { "SampleLineId", "ParentEntityId", "SampleLineObjectId" })
            {
                try
                {
                    PropertyInfo prop = view.GetType().GetProperty(propertyName);
                    if (prop == null) continue;
                    object raw = prop.GetValue(view, null);
                    if (!(raw is ObjectId)) continue;
                    ObjectId id = (ObjectId)raw;
                    if (id.IsNull) continue;
                    DBObject source = tr.GetObject(id, OpenMode.ForRead, false);
                    PropertyInfo name = source.GetType().GetProperty("Name");
                    if (name != null)
                    {
                        string value = name.GetValue(source, null) as string;
                        if (!string.IsNullOrWhiteSpace(value)) return value;
                    }
                }
                catch { }
            }
            return string.Empty;
        }

        private static string RoadName(string name)
        {
            Match match = Regex.Match(name ?? string.Empty, @"(?:ROAD|RD)[-_ ]*0*([0-9]+)", RegexOptions.IgnoreCase);
            return match.Success ? "ROAD-" + int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture).ToString("00", CultureInfo.InvariantCulture)
                : (name ?? "UNASSIGNED");
        }

        private static List<ViewInfo> ChooseViews(Document document, string scope, List<ViewInfo> all)
        {
            if (scope == "All") return all;
            PromptSelectionOptions prompt = new PromptSelectionOptions { MessageForAdding =
                "\nSelect multiple cross-section views (Ctrl/Shift or window selection): " };
            PromptSelectionResult selected = document.Editor.GetSelection(prompt);
            if (selected.Status != PromptStatus.OK || selected.Value == null) return new List<ViewInfo>();
            var ids = new HashSet<ObjectId>(selected.Value.GetObjectIds());
            return all.Where(item => ids.Contains(item.Id)).ToList();
        }

        private static void AddScope(ProductionSettingsDialogModel config)
        {
            config.AddChoice("Scope", "01 Selection", "Cross-section views", "All",
                "Process all views or select multiple cross-section views in the drawing.",
                new[] { "All", "Multiple selected" });
        }

        private static bool TrySetProperty(object target, string name, object value)
        {
            try
            {
                PropertyInfo property = target.GetType().GetProperty(name);
                if (property == null || !property.CanWrite) return false;
                if (value != null && !property.PropertyType.IsAssignableFrom(value.GetType())) return false;
                property.SetValue(target, value, null);
                return true;
            }
            catch { return false; }
        }

        private static bool MoveTo(SectionView view, Point3d desiredLower)
        {
            Extents3d bounds;
            try { bounds = view.GeometricExtents; }
            catch { return false; }
            Vector3d delta = desiredLower - bounds.MinPoint;
            // Civil 3D section views may expose Location as read-only on
            // particular releases. Never claim success if neither operation works.
            try
            {
                PropertyInfo location = view.GetType().GetProperty("Location");
                if (location != null && location.CanRead && location.CanWrite &&
                    location.PropertyType == typeof(Point3d))
                {
                    Point3d current = (Point3d)location.GetValue(view, null);
                    location.SetValue(view, current + delta, null);
                    return true;
                }
            }
            catch { }
            try { view.TransformBy(Matrix3d.Displacement(delta)); return true; }
            catch { return false; }
        }

        [CommandMethod("CE_TOOLS", "CE_CROSSSECTIONVIEWMATCH", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void Match()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var views = FindViews(doc);
            if (views.Count < 2) { doc.Editor.WriteMessage("\nAt least two section views are required."); return; }
            var settings = new ProductionSettingsDialogModel("CE Tools - Match Cross Section Views",
                "Copy section-view style and compatible band-set properties from one source view to all or multiple selected destination views.");
            AddScope(settings);
            settings.AddChoice("MatchStyle", "02 Properties", "Match view style", "ON",
                "Copy the source SectionView.StyleId.", new[] { "ON", "OFF" });
            settings.AddChoice("MatchBands", "02 Properties", "Match band set where supported", "ON",
                "Copy BandSetStyleId only if this Civil 3D version exposes a writable property.",
                new[] { "ON", "OFF" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            PromptEntityOptions opt = new PromptEntityOptions("\nSelect SOURCE cross-section view: ");
            opt.AddAllowedClass(typeof(SectionView), true);
            opt.SetRejectMessage("\nSelect a Civil 3D section view.");
            PromptEntityResult sourcePick = doc.Editor.GetEntity(opt);
            if (sourcePick.Status != PromptStatus.OK) return;
            List<ViewInfo> targets = ChooseViews(doc, settings.Text("Scope"), views)
                .Where(x => x.Id != sourcePick.ObjectId).ToList();
            if (targets.Count == 0) return;
            int changed = 0, skipped = 0;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                SectionView source = tr.GetObject(sourcePick.ObjectId, OpenMode.ForRead, false) as SectionView;
                if (source == null) return;
                ObjectId style = source.StyleId;
                object bandStyle = null;
                try { PropertyInfo property = source.GetType().GetProperty("BandSetStyleId");
                    if (property != null) bandStyle = property.GetValue(source, null); } catch { }
                foreach (ViewInfo item in targets)
                {
                    try
                    {
                        SectionView dest = tr.GetObject(item.Id, OpenMode.ForWrite, false) as SectionView;
                        if (dest == null) { skipped++; continue; }
                        bool applied = false;
                        if (settings.Text("MatchStyle") == "ON") { dest.StyleId = style; applied = true; }
                        if (settings.Text("MatchBands") == "ON" && bandStyle != null)
                            applied = TrySetProperty(dest, "BandSetStyleId", bandStyle) || applied;
                        if (applied) changed++; else skipped++;
                    }
                    catch { skipped++; }
                }
                tr.Commit();
            }
            doc.Editor.Regen();
            doc.Editor.WriteMessage("\nCE_CROSSSECTIONVIEWMATCH: updated {0}, unsupported/skipped {1}.", changed, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_CROSSSECTIONVIEWARRANGE", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void Arrange()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            List<ViewInfo> views = FindViews(doc);
            if (views.Count == 0) { doc.Editor.WriteMessage("\nNo Civil 3D section views found."); return; }
            var settings = new ProductionSettingsDialogModel("CE Tools - Arrange Cross Section Views",
                "Group cross sections per road, then arrange top-left to right in rows. Grid positions are based on actual view extents.");
            AddScope(settings);
            settings.AddChoice("Grouping", "02 Road groups", "Road grouping", "Group per road",
                "Keep each ROAD-xx together instead of mixing sections from different alignments.",
                new[] { "Group per road", "All views together" });
            settings.AddChoice("Layout", "03 Layout", "Layout pattern", "Grid",
                "Arrange in a grid, single row or single column.",
                new[] { "Grid", "Horizontal", "Vertical" });
            settings.AddPositiveInteger("Columns", "03 Layout", "Views per row", 3,
                "Number of cross sections per row within each road group.");
            settings.AddPositiveDouble("HorizontalGap", "03 Layout", "Horizontal gap (m)", 10,
                "Free space between adjacent section view extents.");
            settings.AddPositiveDouble("VerticalGap", "03 Layout", "Vertical gap (m)", 10,
                "Free space between successive rows.");
            settings.AddPositiveDouble("RoadGroupGap", "03 Layout", "Gap between road groups (m)", 25,
                "Vertical space between ROAD-01, ROAD-02 and later groups.");
            settings.AddChoice("Base", "04 Position", "Top-left anchor", "Pick in drawing",
                "Pick the upper-left starting point, or preserve the first cross-section position.",
                new[] { "Pick in drawing", "First view existing position" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            List<ViewInfo> selected = ChooseViews(doc, settings.Text("Scope"), views);
            if (selected.Count == 0) return;
            Point3d topLeft;
            if (settings.Text("Base") == "Pick in drawing")
            {
                PromptPointResult point = doc.Editor.GetPoint("\nPick top-left of arranged cross sections: ");
                if (point.Status != PromptStatus.OK) return;
                topLeft = point.Value.TransformBy(doc.Editor.CurrentUserCoordinateSystem);
            }
            else
            {
                ViewInfo first = selected.OrderBy(x => x.Road).ThenBy(x => x.Lower.X).First();
                topLeft = new Point3d(first.Lower.X, first.Upper.Y, first.Lower.Z);
            }
            int columns = Math.Max(1, settings.Integer("Columns", 3));
            if (settings.Text("Layout") == "Horizontal") columns = int.MaxValue;
            if (settings.Text("Layout") == "Vertical") columns = 1;
            double hGap = settings.Double("HorizontalGap", 10), vGap = settings.Double("VerticalGap", 10),
                groupGap = settings.Double("RoadGroupGap", 25);
            var grouped = settings.Text("Grouping") == "All views together"
                ? new[] { selected.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList() }
                : selected.GroupBy(x => x.Road).OrderBy(x => RoadOrdinal(x.Key))
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.OrderBy(y => y.Name, StringComparer.OrdinalIgnoreCase).ToList()).ToArray();
            int moved = 0, failed = 0;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                double groupTop = topLeft.Y;
                foreach (List<ViewInfo> roadGroup in grouped)
                {
                    double rowTop = groupTop;
                    int index = 0;
                    while (index < roadGroup.Count)
                    {
                        var row = roadGroup.Skip(index).Take(columns).ToList();
                        double x = topLeft.X, rowHeight = row.Max(v => v.Height);
                        foreach (ViewInfo item in row)
                        {
                            SectionView view = tr.GetObject(item.Id, OpenMode.ForWrite, false) as SectionView;
                            if (view != null && MoveTo(view,
                                new Point3d(x, rowTop - item.Height, item.Lower.Z))) moved++;
                            else failed++;
                            x += item.Width + hGap;
                        }
                        rowTop -= rowHeight + vGap;
                        index += row.Count;
                    }
                    groupTop = rowTop - groupGap;
                }
                tr.Commit();
            }
            doc.Editor.Regen();
            doc.Editor.WriteMessage("\nCE_CROSSSECTIONVIEWARRANGE: arranged {0}, failed {1}. Road grouping={2}.",
                moved, failed, settings.Text("Grouping"));
        }

        private static int RoadOrdinal(string name)
        {
            Match match = Regex.Match(name ?? "", @"[0-9]+");
            int number;
            return match.Success && int.TryParse(match.Value, out number) ? number : int.MaxValue;
        }

        [CommandMethod("CE_TOOLS", "CE_CROSSSECTIONVIEWFIT", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void Fit()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            List<ViewInfo> views = FindViews(doc);
            if (views.Count == 0) return;
            var settings = new ProductionSettingsDialogModel("CE Tools - Fit Cross Section Views",
                "Apply automatic offsets/elevation range where the installed Civil 3D SectionView API supports it. Existing view geometry and labels are preserved.");
            AddScope(settings);
            settings.AddPositiveDouble("Clearance", "02 Display", "Vertical elevation clearance (m)", 1.0,
                "Only applied when manual section elevation limits are exposed.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            List<ViewInfo> selected = ChooseViews(doc, settings.Text("Scope"), views);
            int fitted = 0, unsupported = 0;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ViewInfo item in selected)
                {
                    try
                    {
                        SectionView view = tr.GetObject(item.Id, OpenMode.ForWrite, false) as SectionView;
                        if (view == null) { unsupported++; continue; }
                        bool updated = false;
                        updated = TrySetProperty(view, "IsElevationRangeAutomatic", true) || updated;
                        updated = TrySetProperty(view, "IsOffsetRangeAutomatic", true) || updated;
                        updated = TrySetProperty(view, "IsStationRangeAutomatic", true) || updated;
                        if (updated) fitted++; else unsupported++;
                    }
                    catch { unsupported++; }
                }
                tr.Commit();
            }
            doc.Editor.Regen();
            doc.Editor.WriteMessage("\nCE_CROSSSECTIONVIEWFIT: automatic fit enabled on {0} views; unsupported {1}.",
                fitted, unsupported);
        }
    }
}
