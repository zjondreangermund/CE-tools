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
                        {
                            // SectionView band sets are imported into the Bands
                            // collection, not assigned as a graph property.
                            view.Bands.ImportBandSetStyle(bandStyleId);
                            applied = true;
                        }
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
            double hGap, double vGap, double roadGroupGap, bool titles)
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
                    x += maxWidth + roadGroupGap;
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

        // Civil 3D section band collections are tied to their owning SectionView.
        // Never call destination.Bands.SetBottomBandItems(source.Bands.GetBottomBandItems()):
        // the API rejects a collection from a different view and, more seriously,
        // Section1/Section2 source IDs are unique to each sample line.
        private static bool RepairSectionBandSources(SectionView source, SectionView destination,
            Transaction tr, out int repaired, out int unresolved)
        {
            repaired = 0;
            unresolved = 0;
            SampleLine targetSample = tr.GetObject(destination.SampleLineId, OpenMode.ForRead, false) as SampleLine;
            if (targetSample == null) return false;
            var available = new List<Section>();
            foreach (ObjectId sectionId in targetSample.GetSectionIds())
            {
                try
                {
                    Section section = tr.GetObject(sectionId, OpenMode.ForRead, false) as Section;
                    if (section != null && !section.IsErased && section.SampleLineId == destination.SampleLineId)
                        available.Add(section);
                }
                catch { }
            }
            if (available.Count == 0) return false;
            bool changed = false;
            foreach (bool top in new[] { true, false })
            {
                SectionViewBandItemCollection sourceItems = top
                    ? source.Bands.GetTopBandItems() : source.Bands.GetBottomBandItems();
                SectionViewBandItemCollection destinationItems = top
                    ? destination.Bands.GetTopBandItems() : destination.Bands.GetBottomBandItems();
                if (sourceItems == null || destinationItems == null) continue;
                int count = Math.Min(sourceItems.Count, destinationItems.Count);
                bool collectionChanged = false;
                for (int index = 0; index < count; index++)
                {
                    SectionViewBandItem sourceBand = sourceItems[index];
                    SectionViewBandItem destinationBand = destinationItems[index];
                    // If a view has a different band layout, do not bind a
                    // surface/elevation row to an unrelated type of data band.
                    if (sourceBand.BandType != destinationBand.BandType)
                    {
                        unresolved += 2;
                        continue;
                    }
                    // Section data bands have two section source selectors. Other
                    // band kinds can throw on these properties, so leave them alone.
                    foreach (int slot in new[] { 1, 2 })
                    {
                        try
                        {
                            ObjectId sourceId = slot == 1 ? sourceBand.Section1Id : sourceBand.Section2Id;
                            ObjectId currentId = slot == 1 ? destinationBand.Section1Id : destinationBand.Section2Id;
                            if (sourceId.IsNull || !sourceId.IsValid) continue;
                            Section reference = tr.GetObject(sourceId, OpenMode.ForRead, false) as Section;
                            if (reference == null || reference.SampleLineId != source.SampleLineId)
                            { unresolved++; continue; }
                            Section match = FindMatchingLocalSection(reference, available, currentId);
                            if (match == null)
                            {
                                unresolved++;
                                continue; // Never substitute the source road's section.
                            }
                            if (currentId == match.ObjectId) continue;
                            if (slot == 1) destinationBand.Section1Id = match.ObjectId;
                            else destinationBand.Section2Id = match.ObjectId;
                            repaired++;
                            changed = true;
                            collectionChanged = true;
                        }
                        catch { unresolved++; }
                    }
                }
                // Commit only the destination's OWN band collection. Civil 3D
                // explicitly disallows passing band collections across views.
                if (collectionChanged)
                {
                    if (top) destination.Bands.SetTopBandItems(destinationItems);
                    else destination.Bands.SetBottomBandItems(destinationItems);
                }
                if (sourceItems.Count != destinationItems.Count)
                    unresolved += Math.Abs(sourceItems.Count - destinationItems.Count);
            }
            return changed;
        }

        private static Section FindMatchingLocalSection(Section reference,
            IList<Section> candidates, ObjectId currentId)
        {
            string referenceKey = NormalizeSectionSourceName(reference.SourceName);
            if (string.IsNullOrEmpty(referenceKey))
                referenceKey = NormalizeSectionSourceName(reference.Name);
            if (string.IsNullOrEmpty(referenceKey)) return null;

            // The available sections belong to the destination sample line.
            // Match the source role (e.g. NG or CORRIDOR TOP) while allowing
            // ROAD-01/RD-01 to become ROAD-02/RD-02 on the next road.
            var matches = candidates.Where(section =>
            {
                string name = NormalizeSectionSourceName(section.SourceName);
                if (string.IsNullOrEmpty(name)) name = NormalizeSectionSourceName(section.Name);
                return name == referenceKey;
            }).ToList();

            if (matches.Count == 1) return matches[0];
            // If multiple sections have the same role, retain an existing
            // matching local binding. Otherwise report ambiguity, don't guess.
            return matches.FirstOrDefault(section => section.ObjectId == currentId);
        }

        private static string NormalizeSectionSourceName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            string normalized = Regex.Replace(name.Trim().ToUpperInvariant(),
                @"(?:ROAD|RD)[-_ ]*0*[0-9]+", "ROAD-#", RegexOptions.IgnoreCase);
            return Regex.Replace(normalized, @"\\s+", " ");
        }

        [CommandMethod("CE_TOOLS", "CE_CROSSSECTIONVIEWMATCH", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void Match()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var views = FindViews(doc);
            if (views.Count < 2) { doc.Editor.WriteMessage("\nAt least two section views are required."); return; }
            var settings = new ProductionSettingsDialogModel("CE Tools - Match Cross Section Views",
                "Match section-view presentation, and repair each destination's Section1/Section2 band sources from its own sampled sections. Source-road section IDs are never copied.");
            AddScope(settings);
            settings.AddChoice("MatchStyle", "02 Properties", "Match view style", "ON",
                "Copy the source SectionView.StyleId.", new[] { "ON", "OFF" });
            settings.AddChoice("RepairBandSources", "02 Properties", "Repair Section1/Section2 band data sources", "ON",
                "For each destination sample line, resolve Section1 and Section2 to local NG and road corridor sections by corresponding source names. Never copy the reference view section IDs.",
                new[] { "ON", "OFF" });
            settings.AddChoice("MatchElevation", "02 Properties", "Match vertical elevation range", "ON",
                "Match source automatic/manual elevation mode and the actual minimum/maximum displayed elevations. Unlike styles alone, this can visibly change the destination section grid.",
                new[] { "ON", "OFF" });
            settings.AddChoice("MatchOffset", "02 Properties", "Match horizontal offset range", "ON",
                "Match the source offset range's automatic/manual setting and limits where supported.",
                new[] { "ON", "OFF" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            PromptEntityOptions opt = new PromptEntityOptions("\nSelect SOURCE cross-section view: ");
            // AutoCAD requires SetRejectMessage to precede AddAllowedClass.
            // Reversing these calls throws InvalidOperationException before
            // the SOURCE view can be selected (SetRejectMessage first).
            opt.SetRejectMessage("\nSelect a Civil 3D section view.");
            opt.AddAllowedClass(typeof(SectionView), true);
            PromptEntityResult sourcePick = doc.Editor.GetEntity(opt);
            if (sourcePick.Status != PromptStatus.OK) return;
            List<ViewInfo> targets = ChooseViews(doc, settings.Text("Scope"), views)
                .Where(x => x.Id != sourcePick.ObjectId).ToList();
            if (targets.Count == 0) return;
            int changed = 0, alreadySame = 0, skipped = 0, styleUpdates = 0,
                elevationUpdates = 0, offsetUpdates = 0, bandUpdates = 0, unresolvedBandSources = 0;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                SectionView source = tr.GetObject(sourcePick.ObjectId, OpenMode.ForRead, false) as SectionView;
                if (source == null) return;
                ObjectId style = source.StyleId;
                bool sourceAutoElevation = source.IsElevationRangeAutomatic;
                double sourceMin = source.ElevationMin, sourceMax = source.ElevationMax;
                foreach (ViewInfo item in targets)
                {
                    try
                    {
                        SectionView dest = tr.GetObject(item.Id, OpenMode.ForWrite, false) as SectionView;
                        if (dest == null) { skipped++; continue; }
                        bool changedThis = false;
                        if (settings.Text("MatchStyle") == "ON" && dest.StyleId != style)
                        {
                            dest.StyleId = style;
                            changedThis = true;
                            styleUpdates++;
                        }
                        if (settings.Text("MatchElevation") == "ON")
                        {
                            bool differs = dest.IsElevationRangeAutomatic != sourceAutoElevation ||
                                Math.Abs(dest.ElevationMin - sourceMin) > 0.001 ||
                                Math.Abs(dest.ElevationMax - sourceMax) > 0.001;
                            if (differs)
                            {
                                // In manual mode set the upper limit first, then lower.
                                dest.IsElevationRangeAutomatic = sourceAutoElevation;
                                if (!sourceAutoElevation && sourceMax >= sourceMin)
                                {
                                    dest.ElevationMax = sourceMax;
                                    dest.ElevationMin = sourceMin;
                                }
                                changedThis = true;
                                elevationUpdates++;
                            }
                        }
                        if (settings.Text("MatchOffset") == "ON")
                        {
                            bool copied = CopySectionOffsetRange(source, dest);
                            if (copied) { changedThis = true; offsetUpdates++; }
                        }
                        if (settings.Text("RepairBandSources") == "ON")
                        {
                            int fixedSources, missingSources;
                            bool fixedBands = RepairSectionBandSources(
                                source, dest, tr, out fixedSources, out missingSources);
                            unresolvedBandSources += missingSources;
                            if (fixedBands) { changedThis = true; bandUpdates += fixedSources; }
                            if (missingSources > 0)
                                doc.Editor.WriteMessage(
                                    "\\nCE_CROSSSECTIONVIEWMATCH: {0}: {1} band sources could not be matched uniquely to this sample line.",
                                    item.Name, missingSources);
                        }
                        if (changedThis) changed++;
                        else alreadySame++;
                    }
                    catch (System.Exception error)
                    {
                        skipped++;
                        doc.Editor.WriteMessage("\\nCE_CROSSSECTIONVIEWMATCH skipped {0}: {1}", item.Name, error.Message);
                    }
                }
                tr.Commit();
            }
            doc.Editor.Regen();
            doc.Editor.WriteMessage(
                "\\nCE_CROSSSECTIONVIEWMATCH: changed {0}, no differences/unsupported {1}, errors {2}. Styles {3}, vertical ranges {4}, horizontal ranges {5}, corrected Section1/Section2 references {6}, unresolved references {7}.",
                changed, alreadySame, skipped, styleUpdates, elevationUpdates, offsetUpdates, bandUpdates, unresolvedBandSources);
        }

        private static bool CopySectionOffsetRange(SectionView source, SectionView dest)
        {
            // Reflectively handle release-specific Civil 3D section view range
            // properties. Unsupported properties are NOT counted as a change.
            var automatic = source.GetType().GetProperty("IsOffsetRangeAutomatic");
            if (automatic == null || !automatic.CanRead || automatic.PropertyType != typeof(bool))
                return false;
            bool isAutomatic = (bool)automatic.GetValue(source, null);
            bool sourceMode = false, destinationMode = false;
            try
            {
                sourceMode = isAutomatic;
                destinationMode = (bool)automatic.GetValue(dest, null);
            }
            catch { return false; }
            if (sourceMode && destinationMode) return false;
            if (!TrySetProperty(dest, "IsOffsetRangeAutomatic", sourceMode)) return false;
            if (!sourceMode)
            {
                foreach (string key in new[] { "OffsetLeft", "OffsetRight" })
                {
                    var property = source.GetType().GetProperty(key);
                    if (property != null && property.CanRead)
                        TrySetProperty(dest, key, property.GetValue(source, null));
                }
            }
            return true;
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
            settings.AddPositiveInteger("RoadColumns", "03 Layout", "Road groups per row", 2,
                "Arrange ROAD-01, ROAD-02 etc left-to-right across this many road-group columns.");
            settings.AddChoice("RoadTitles", "03 Layout", "Add ROAD-xx headings", "OFF",
                "Create road name text above each arranged road group. Use OFF when headings already exist.",
                new[] { "OFF", "ON" });
            settings.AddPositiveDouble("HorizontalGap", "03 Layout", "Horizontal gap (m)", 10,
                "Free space between adjacent section view extents.");
            settings.AddPositiveDouble("VerticalGap", "03 Layout", "Vertical gap (m)", 10,
                "Free space between successive rows.");
            settings.AddPositiveDouble("RoadGroupGap", "03 Layout", "Gap between road groups (m)", 25,
                "Horizontal space between neighboring road groups.");
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
            if (settings.Text("Grouping") == "Group per road")
            {
                var keep = new HashSet<ObjectId>(views.Select(x => x.Id));
                foreach (ViewInfo chosen in selected) keep.Remove(chosen.Id);
                int movedRoad = ArrangeGeneratedByRoad(doc, keep, topLeft, columns,
                    Math.Max(1, settings.Integer("RoadColumns", 2)),
                    settings.Double("HorizontalGap", 10),
                    settings.Double("VerticalGap", 10),
                    settings.Double("RoadGroupGap", 25),
                    settings.Text("RoadTitles") == "ON");
                doc.Editor.Regen();
                doc.Editor.WriteMessage("\nCE_CROSSSECTIONVIEWARRANGE: grouped/arranged {0} selected section views by road.", movedRoad);
                return;
            }
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
            if (views.Count == 0)
            {
                doc.Editor.WriteMessage("\\nCE_CROSSSECTIONVIEWFIT: no Civil 3D section views found.");
                return;
            }
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Fit Cross Section Views and Label Clearance",
                "Fit all or multiple selected cross section views. Use Civil 3D's automatic section elevation range as the data envelope, then reserve grid-row clearances around it.");
            AddScope(settings);
            settings.AddChoice("Elevation", "01 Vertical fit", "Elevation range", "Fit with row clearances",
                "Manual elevation limits are computed from the current automatic data envelope and the reserved rows. Choose Automatic to use Civil 3D's default range only.",
                new[] { "Fit with row clearances", "Automatic" });
            settings.AddPositiveDouble("RowHeight", "01 Vertical fit", "Cross-section-view row height", 1.0,
                "Elevation represented by one horizontal section-view row (m).");
            settings.AddPositiveInteger("BottomRows", "01 Vertical fit", "Rows below lowest section", 5,
                "Clear rows between the lowest displayed section and bottom frame.");
            settings.AddPositiveInteger("SectionToTopLabelRows", "01 Vertical fit",
                "Rows from highest section to top labels", 4,
                "Rows reserved between highest displayed section and its top labels.");
            settings.AddPositiveDouble("TopLabelToFrameRows", "01 Vertical fit",
                "Rows from top labels to top frame", 1.0,
                "Additional clear rows above the top text before the section-view frame.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            List<ViewInfo> selected = ChooseViews(doc, settings.Text("Scope"), views);
            if (selected.Count == 0) return;

            bool withClearance = settings.Text("Elevation") == "Fit with row clearances";
            double rowHeight = settings.Double("RowHeight", 1.0);
            double bottom = settings.Integer("BottomRows", 5) * rowHeight;
            double top = (settings.Integer("SectionToTopLabelRows", 4) +
                settings.Double("TopLabelToFrameRows", 1.0)) * rowHeight;
            int fitted = 0, unsupported = 0, unchanged = 0;
            double greatestChange = 0.0;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ViewInfo item in selected)
                {
                    try
                    {
                        SectionView view = tr.GetObject(item.Id, OpenMode.ForWrite, false) as SectionView;
                        if (view == null) { unsupported++; continue; }

                        // Start from a fresh automatic elevation envelope, so
                        // re-running the fit does not compound earlier padding.
                        view.IsElevationRangeAutomatic = true;
                        // Force a read of the drawing's current sampled section
                        // envelope before switching to explicit elevations.
                        double dataMin = view.ElevationMin;
                        double dataMax = view.ElevationMax;
                        if (double.IsNaN(dataMin) || double.IsNaN(dataMax) ||
                            double.IsInfinity(dataMin) || double.IsInfinity(dataMax) ||
                            dataMax < dataMin)
                        {
                            unsupported++;
                            continue;
                        }
                        if (withClearance)
                        {
                            double expectedMin = dataMin - bottom;
                            double expectedMax = dataMax + top;
                            // Autodesk requires manual mode before setting limits.
                            view.IsElevationRangeAutomatic = false;
                            // Expand top first, then bottom, avoiding a transient
                            // inverted min/max pair in Civil 3D.
                            view.ElevationMax = expectedMax;
                            view.ElevationMin = expectedMin;
                            // Verify the actual Civil 3D property values instead
                            // of reporting success without a visible change.
                            double actualMin = view.ElevationMin;
                            double actualMax = view.ElevationMax;
                            double discrepancy = Math.Max(Math.Abs(actualMin - expectedMin),
                                Math.Abs(actualMax - expectedMax));
                            if (discrepancy > 0.01)
                            {
                                unsupported++;
                                doc.Editor.WriteMessage("\\nCE_CROSSSECTIONVIEWFIT: {0} elevation range did not accept requested limits. Wanted {1:0.###}..{2:0.###}, got {3:0.###}..{4:0.###}.",
                                    item.Name, expectedMin, expectedMax, actualMin, actualMax);
                                continue;
                            }
                            greatestChange = Math.Max(greatestChange, expectedMax - expectedMin);
                            if (Math.Abs(bottom) + Math.Abs(top) < 0.0001)
                                unchanged++;
                        }
                        TrySetProperty(view, "IsOffsetRangeAutomatic", true);
                        fitted++;
                    }
                    catch (System.Exception error)
                    {
                        unsupported++;
                        doc.Editor.WriteMessage("\\nCE_CROSSSECTIONVIEWFIT: skipped section '{0}': {1}",
                            item.Name, error.Message);
                    }
                }
                tr.Commit();
            }
            doc.Editor.Regen();
            doc.Editor.WriteMessage(
                "\\nCE_CROSSSECTIONVIEWFIT: fitted {0} section views, skipped {1}; range-width maximum {2:0.###} m. Mode: {3}. Row height {4:0.###} m, below {5:0.###} m, above {6:0.###} m.",
                fitted, unsupported, greatestChange, settings.Text("Elevation"), rowHeight, bottom, top);
            if (rowHeight >= 20.0 || bottom >= 100.0 || top >= 100.0)
                doc.Editor.WriteMessage("\\nWarning: very large cross-section elevation clearances may produce extremely tall views. Row height is in drawing elevation units (usually metres), not text height.");
        }
    }
}
