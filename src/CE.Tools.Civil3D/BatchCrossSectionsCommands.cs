using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.BatchCrossSectionsCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Batch sample-line and cross-section production. All configuration is gathered
    /// in one popup before any drawing objects are created.
    /// </summary>
    public sealed class BatchCrossSectionsCommands
    {
        [CommandMethod("CE_TOOLS", "CE_BATCHCROSSSECTIONS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateBatchCrossSections()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            List<AlignmentChoice> alignments = ReadAlignments(doc.Database);
            if (alignments.Count == 0)
            {
                doc.Editor.WriteMessage("\nCE_BATCHCROSSSECTIONS: no road alignments found in this drawing.");
                return;
            }

            CivilDocument civilDoc = CivilApplication.ActiveDocument;
            var window = new BatchCrossSectionsWindow(
                alignments,
                ReadNames(doc.Database, civilDoc.Styles.SampleLineStyles),
                ReadNames(doc.Database, GetStyleCollection(civilDoc.Styles, "SampleLineLabelStyles")),
                ReadNames(doc.Database, civilDoc.Styles.SectionViewStyles),
                ReadNames(doc.Database, civilDoc.Styles.SectionViewBandSetStyles),
                ReadSourceChoices(doc.Database, civilDoc, false),
                ReadSourceChoices(doc.Database, civilDoc, true), doc);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted) return;

            BatchCrossSectionSettings s = window.Settings;
            Point3d basePoint = new Point3d(s.InsertX, s.InsertY, 0.0);
            int groupsCreated = 0, linesCreated = 0, sectionGroupsCreated = 0;
            var failures = new List<string>();

            for (int alignmentIndex = 0; alignmentIndex < s.Alignments.Count; alignmentIndex++)
            {
                AlignmentChoice choice = s.Alignments[alignmentIndex];
                try
                {
                    ObjectId groupId;
                    double start, end;
                    using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        Alignment alignment = tr.GetObject(choice.Id, OpenMode.ForRead, false) as Alignment;
                        if (alignment == null) { failures.Add(choice.Name + ": alignment could not be opened."); continue; }
                        start = s.UseFullRange ? alignment.StartingStation : Math.Max(alignment.StartingStation, s.StartStation);
                        end = s.UseFullRange ? alignment.EndingStation : Math.Min(alignment.EndingStation, s.EndStation);
                        if (end < start) { failures.Add(choice.Name + ": station range is outside alignment extents."); continue; }

                        if (s.RemoveExisting)
                        {
                            // Erase existing sample-line groups for this alignment only when explicitly requested.
                            foreach (ObjectId oldGroupId in alignment.GetSampleLineGroupIds())
                            {
                                try
                                {
                                    DBObject oldObject = tr.GetObject(oldGroupId, OpenMode.ForWrite, false);
                                    if (oldObject != null && !oldObject.IsErased) oldObject.Erase();
                                }
                                catch { /* Keep processing if an existing group cannot be erased. */ }
                            }
                        }
                        string groupName = UniqueGroupName(alignment, s.GroupNamePrefix);
                        groupId = SampleLineGroup.Create(groupName, choice.Id);
                        SampleLineGroup group = tr.GetObject(groupId, OpenMode.ForWrite, false) as SampleLineGroup;
                        if (group != null)
                        {
                            TrySetStyleByName(group, "DefaultSamplineStyleId", civilDoc.Styles.SampleLineStyles, s.SampleLineStyle);
                            
                            if (!string.IsNullOrWhiteSpace(s.SampleLineLayer))
                                TrySetLayer(group, doc.Database, s.SampleLineLayer);
                        }
                        tr.Commit();
                    }
                    groupsCreated++;

                    // Station-based SampleLine.Create uses the drawing's default swath widths.
                    // Use the Point2dCollection overload so each line has the requested left/right width.
                    var stations = BuildStations(start, end, s.Interval, s.IncludeStart, s.IncludeEnd);
                    foreach (double station in stations)
                    {
                        try
                        {
                            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                            {
                                Alignment alignment = tr.GetObject(choice.Id, OpenMode.ForRead, false) as Alignment;
                                Point2dCollection points = new Point2dCollection();
                                double ex = 0, ny = 0;
                                alignment.PointLocation(station, -s.LeftWidth, ref ex, ref ny);
                                points.Add(new Point2d(ex, ny));
                                alignment.PointLocation(station, 0.0, ref ex, ref ny);
                                points.Add(new Point2d(ex, ny));
                                alignment.PointLocation(station, s.RightWidth, ref ex, ref ny);
                                points.Add(new Point2d(ex, ny));

                                string sampleName = MakeSampleLineName(choice.Name, station);
                                ObjectId sampleId = SampleLine.Create(sampleName, groupId, points);
                                SampleLine sample = tr.GetObject(sampleId, OpenMode.ForWrite, false) as SampleLine;
                                if (sample != null)
                                {
                                    if (!string.IsNullOrWhiteSpace(s.SampleLineStyle))
                                        TrySetNamedStyle(sample, s.SampleLineStyle);
                                    if (!string.IsNullOrWhiteSpace(s.SampleLineLayer))
                                        TrySetLayer(sample, doc.Database, s.SampleLineLayer);
                                }
                                tr.Commit();
                                linesCreated++;
                            }
                        }
                        catch (System.Exception ex)
                        {
                            failures.Add(choice.Name + " station " + station.ToString("0.###", CultureInfo.InvariantCulture) + ": " + ex.Message);
                        }
                    }

                    if (s.CreateSectionViews)
                    {
                        try
                        {
                            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                            {
                                SampleLineGroup group = tr.GetObject(groupId, OpenMode.ForWrite, false) as SampleLineGroup;
                                if (group != null && group.GetSampleLineIds().Count > 0)
                                {
                                    // Sampled-source flags are configured via the group source collection when exposed by this Civil 3D build.
                                    TryEnableAvailableSources(group, s.SampleAllAvailableSources);
                                    ApplySelectedSources(group, s.Surfaces, s.Corridors, s.SampleAllAvailableSources);
                                    // Place each alignment's section-view group in a predictable grid:
                                    // start at the top-left, progress left-to-right, then top-to-bottom.
                                    int column = alignmentIndex % s.GroupsPerRow;
                                    int row = alignmentIndex / s.GroupsPerRow;
                                    Point3d placement = new Point3d(
                                        basePoint.X + column * s.ColumnSpacing,
                                        basePoint.Y - row * s.RowSpacing,
                                        basePoint.Z);
                                    SectionViewGroup svg = group.SectionViewGroups.Add(placement);
                                    if (svg != null)
                                    {
                                        sectionGroupsCreated++;
                                        TryApplySectionViewStyle(svg, civilDoc, s.SectionViewStyle, s.BandSetStyle);
                                    }
                                }
                                tr.Commit();
                            }
                        }
                        catch (System.Exception ex)
                        {
                            failures.Add(choice.Name + ": sample lines created, but section views failed: " + ex.Message);
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    failures.Add(choice.Name + ": " + ex.Message);
                }
            }

            doc.Editor.Regen();
            doc.Editor.WriteMessage(
                "\nCE_BATCHCROSSSECTIONS complete. Alignments selected={0}; groups created={1}; sample lines created={2}; section-view groups created={3}; run cross sections={4}; issues={5}.",
                s.Alignments.Count, groupsCreated, linesCreated, sectionGroupsCreated, s.CreateSectionViews ? "Yes" : "No", failures.Count);
            foreach (string failure in failures.Take(15))
                doc.Editor.WriteMessage("\n  " + failure);
            if (failures.Count > 15)
                doc.Editor.WriteMessage("\n  ... plus {0} additional issues.", failures.Count - 15);
        }

        private static List<AlignmentChoice> ReadAlignments(Database db)
        {
            var result = new List<AlignmentChoice>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                CivilDocument civilDoc = CivilApplication.ActiveDocument;
                foreach (ObjectId id in civilDoc.GetAlignmentIds())
                {
                    Alignment alignment = tr.GetObject(id, OpenMode.ForRead, false) as Alignment;
                    if (alignment != null) result.Add(new AlignmentChoice(id, alignment.Name));
                }
                tr.Commit();
            }
            return result.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Resolve optional Civil 3D style collections by reflection so this source remains
        // compatible with API versions where a collection is not exposed on StylesRoot.
        private static object GetStyleCollection(object stylesRoot, string propertyName)
        {
            if (stylesRoot == null || string.IsNullOrWhiteSpace(propertyName)) return null;
            try
            {
                var property = stylesRoot.GetType().GetProperty(propertyName);
                return property == null ? null : property.GetValue(stylesRoot, null);
            }
            catch { return null; }
        }

        // Resolve ObjectIds yielded by Civil 3D style collections to their DBObjects.
        // This ensures the popup lists the names of available styles and band sets.
        private static List<string> ReadNames(Database db, object collection)
        {
            var names = new List<string>();
            try
            {
                var enumerable = collection as System.Collections.IEnumerable;
                if (enumerable == null) return names;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (object item in enumerable)
                    {
                        if (item == null) continue;
                        object candidate = item;
                        if (item is ObjectId)
                        {
                            ObjectId id = (ObjectId)item;
                            if (id.IsNull || !id.IsValid) continue;
                            try { candidate = tr.GetObject(id, OpenMode.ForRead, false); }
                            catch { continue; }
                        }
                        var property = candidate.GetType().GetProperty("Name");
                        string value = property == null ? null : property.GetValue(candidate, null) as string;
                        if (!string.IsNullOrWhiteSpace(value)) names.Add(value);
                    }
                    tr.Commit();
                }
            }
            catch { }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        }

        private static List<SectionSourceChoice> ReadSourceChoices(Database db, CivilDocument civilDoc, bool corridors)
        {
            var result = new List<SectionSourceChoice>();
            var candidateIds = new HashSet<ObjectId>();
            // Civil surfaces and corridors are generally stored in Civil 3D dictionaries/collections,
            // not as entities in ModelSpace. Discover the public ID collections exposed by this API.
            try
            {
                foreach (var member in civilDoc.GetType().GetMembers(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                {
                    string memberName = member.Name ?? string.Empty;
                    bool relevant = corridors
                        ? memberName.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) >= 0
                        : memberName.IndexOf("Surface", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!relevant || !(memberName.IndexOf("Id", StringComparison.OrdinalIgnoreCase) >= 0 || memberName.IndexOf("Collection", StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    object value = null;
                    try
                    {
                        var property = member as System.Reflection.PropertyInfo;
                        if (property != null && property.GetIndexParameters().Length == 0) value = property.GetValue(civilDoc, null);
                        var method = member as System.Reflection.MethodInfo;
                        if (method != null && method.GetParameters().Length == 0) value = method.Invoke(civilDoc, null);
                    }
                    catch { }
                    var enumerable = value as System.Collections.IEnumerable;
                    if (enumerable == null) continue;
                    foreach (object entry in enumerable)
                    {
                        if (entry is ObjectId && !((ObjectId)entry).IsNull) candidateIds.Add((ObjectId)entry);
                        else
                        {
                            ObjectId nested = ReadObjectId(entry, "ObjectId", "Id", "SurfaceId", "CorridorId");
                            if (!nested.IsNull) candidateIds.Add(nested);
                        }
                    }
                }
            }
            catch { }
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                BlockTable bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord model = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in model) candidateIds.Add(id);
                foreach (ObjectId id in candidateIds)
                {
                    try
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead, false);
                        if (obj == null) continue;
                        string typeName = obj.GetType().Name;
                        bool isCorridor = typeName.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool isSurface = typeName.IndexOf("Surface", StringComparison.OrdinalIgnoreCase) >= 0
                            && typeName.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) < 0;
                        if ((corridors && !isCorridor) || (!corridors && !isSurface)) continue;
                        var nameProp = obj.GetType().GetProperty("Name");
                        string name = nameProp == null ? null : nameProp.GetValue(obj, null) as string;
                        if (string.IsNullOrWhiteSpace(name)) name = typeName + " - " + id.Handle.ToString();
                        result.Add(new SectionSourceChoice(id, name, typeName));
                    }
                    catch { }
                }
                tr.Commit();
            }
            return result.GroupBy(x => x.Id).Select(g => g.First()).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void ApplySelectedSources(SampleLineGroup group, List<SectionSourceChoice> surfaces, List<SectionSourceChoice> corridors, bool sampleAll)
        {
            // Civil 3D's section-source collection differs across API versions. Use reflection
            // so the source archive does not require a runtime-binder reference.
            try
            {
                object collection = group.GetSectionSources();
                if (collection == null) return;
                var methods = collection.GetType().GetMethods();
                var selected = new List<SectionSourceChoice>();
                if (surfaces != null) selected.AddRange(surfaces);
                if (corridors != null) selected.AddRange(corridors);
                foreach (SectionSourceChoice choice in selected)
                {
                    bool found = false;
                    foreach (object source in (collection as System.Collections.IEnumerable) ?? new object[0])
                    {
                        if (source == null) continue;
                        ObjectId existing = ReadObjectId(source, "SourceId", "ObjectId", "Id");
                        if (existing == choice.Id) { SetSampled(source, true); found = true; break; }
                    }
                    if (found) continue;
                    foreach (var method in methods.Where(m => string.Equals(m.Name, "Add", StringComparison.OrdinalIgnoreCase)))
                    {
                        var ps = method.GetParameters();
                        if (ps.Length != 1 || ps[0].ParameterType != typeof(ObjectId)) continue;
                        try
                        {
                            object source = method.Invoke(collection, new object[] { choice.Id });
                            if (source != null) SetSampled(source, true);
                            found = true; break;
                        }
                        catch { }
                    }
                }
                if (sampleAll)
                {
                    var enumerable = collection as System.Collections.IEnumerable;
                    if (enumerable != null) foreach (object source in enumerable) SetSampled(source, true);
                }
            }
            catch { }
        }

        private static ObjectId ReadObjectId(object obj, params string[] properties)
        {
            if (obj == null) return ObjectId.Null;
            foreach (string name in properties)
            {
                try { var p = obj.GetType().GetProperty(name); if (p != null) { object value = p.GetValue(obj, null); if (value is ObjectId) return (ObjectId)value; } } catch { }
            }
            return ObjectId.Null;
        }

        private static void SetSampled(object source, bool value)
        {
            if (source == null) return;
            foreach (string name in new[] { "IsSampled", "IsSampledForSection", "Sampled" })
            {
                try { var p = source.GetType().GetProperty(name); if (p != null && p.CanWrite && p.PropertyType == typeof(bool)) { p.SetValue(source, value, null); return; } } catch { }
            }
        }

        private static List<double> BuildStations(double start, double end, double interval, bool includeStart, bool includeEnd)
        {
            var stations = new List<double>();
            if (includeStart) stations.Add(start);
            // Build stations from this alignment's actual selected start station. Do not
            // anchor the interval to station zero: station equations / non-zero starts
            // otherwise make different alignments appear to have inconsistent sampling.
            for (double station = start + interval; station < end - 1e-7; station += interval)
                stations.Add(station);
            if (includeEnd && (stations.Count == 0 || Math.Abs(stations[stations.Count - 1] - end) > 1e-7))
                stations.Add(end);
            return stations.Distinct().OrderBy(v => v).ToList();
        }

        private static string UniqueGroupName(Alignment alignment, string prefix)
        {
            // Every selected road receives its own explicitly named group. Keep the
            // alignment name in the group name so group ownership is immediately clear.
            string baseName = (string.IsNullOrWhiteSpace(prefix) ? "SL Collection" : prefix.Trim()) + " - " + alignment.Name;
            var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in alignment.GetSampleLineGroupIds())
            {
                try
                {
                    using (Transaction tr = alignment.Database.TransactionManager.StartTransaction())
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead, false);
                        var nameProperty = obj?.GetType().GetProperty("Name");
                        string existing = nameProperty?.GetValue(obj, null) as string;
                        if (!string.IsNullOrWhiteSpace(existing)) existingNames.Add(existing);
                        tr.Commit();
                    }
                }
                catch { }
            }
            if (!existingNames.Contains(baseName)) return baseName;
            for (int suffix = 2; suffix < 10000; suffix++)
            {
                string candidate = baseName + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")";
                if (!existingNames.Contains(candidate)) return candidate;
            }
            return baseName + " - " + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static string MakeSampleLineName(string alignmentName, double station)
        {
            // Sample-line names must be unique beyond a single alignment/group in some
            // Civil 3D releases. Including the road name prevents RD-02+ from failing
            // with "Sample line name should not duplicate" after RD-01 was created.
            string safeRoadName = string.IsNullOrWhiteSpace(alignmentName) ? "ROAD" : alignmentName.Trim();
            foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
                safeRoadName = safeRoadName.Replace(invalid, '_');
            safeRoadName = safeRoadName.Replace(' ', '_');
            return safeRoadName + "-SL-" + station.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static void TrySetStyleByName(object target, string propertyName, object collection, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                var enumerable = collection as System.Collections.IEnumerable;
                if (enumerable == null) return;
                foreach (object style in enumerable)
                {
                    if (style == null) continue;
                    var type = style.GetType();
                    string styleName = type.GetProperty("Name")?.GetValue(style, null) as string;
                    object id = type.GetProperty("ObjectId")?.GetValue(style, null);
                    if (id is ObjectId && string.Equals(styleName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        target.GetType().GetProperty(propertyName)?.SetValue(target, id, null);
                        return;
                    }
                }
            }
            catch { }
        }

        private static void TrySetNamedStyle(object target, string name)
        {
            try
            {
                var p = target.GetType().GetProperty("StyleName");
                if (p != null && p.CanWrite) p.SetValue(target, name, null);
            }
            catch { }
        }

        private static void TrySetLayer(Entity entity, Database db, string layerName)
        {
            try
            {
                ObjectId layerId = ObjectId.Null;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    LayerTable lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (lt.Has(layerName)) layerId = lt[layerName];
                    else
                    {
                        lt.UpgradeOpen();
                        var record = new LayerTableRecord { Name = layerName };
                        layerId = lt.Add(record);
                        tr.AddNewlyCreatedDBObject(record, true);
                    }
                    tr.Commit();
                }
                if (!layerId.IsNull) entity.LayerId = layerId;
            }
            catch { }
        }

        private static void TryEnableAvailableSources(SampleLineGroup group, bool enable)
        {
            if (!enable) return;
            try
            {
                object sources = group.GetSectionSources();
                if (sources == null) return;
                var enumerable = sources as System.Collections.IEnumerable;
                if (enumerable == null) return;
                foreach (object source in enumerable)
                {
                    try
                    {
                        var prop = source.GetType().GetProperty("IsSampled");
                        if (prop != null && prop.CanWrite) prop.SetValue(source, true, null);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void TryApplySectionViewStyle(object group, CivilDocument civilDoc, string styleName, string bandName)
        {
            // SectionViewGroup creation overloads vary between Civil 3D releases.
            // Apply style/band properties when exposed by the target API.
            try
            {
                var p = group.GetType().GetProperty("StyleId");
                if (p != null && p.CanWrite)
                {
                    foreach (object style in (System.Collections.IEnumerable)civilDoc.Styles.SectionViewStyles)
                        if (style != null && string.Equals(style.GetType().GetProperty("Name")?.GetValue(style, null) as string, styleName, StringComparison.OrdinalIgnoreCase))
                            p.SetValue(group, (ObjectId)style.GetType().GetProperty("ObjectId").GetValue(style, null), null);
                }
            }
            catch { }
            try
            {
                var p = group.GetType().GetProperty("BandSetStyleId");
                if (p != null && p.CanWrite && !string.IsNullOrWhiteSpace(bandName))
                    foreach (object style in (System.Collections.IEnumerable)civilDoc.Styles.SectionViewBandSetStyles)
                        if (style != null)
                    {
                        var nameProperty = style.GetType().GetProperty("Name");
                        var idProperty = style.GetType().GetProperty("ObjectId");
                        string candidateName = nameProperty == null ? null : nameProperty.GetValue(style, null) as string;
                        if (idProperty != null && string.Equals(candidateName, bandName, StringComparison.OrdinalIgnoreCase))
                            p.SetValue(group, (ObjectId)idProperty.GetValue(style, null), null);
                    }
            }
            catch { }
        }

        private static bool TryReadPositive(TextBox box, out double value)
        {
            return double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                && value > 0.0;
        }

        private sealed class AlignmentChoice
        {
            public ObjectId Id { get; private set; }
            public string Name { get; private set; }
            public AlignmentChoice(ObjectId id, string name) { Id = id; Name = name; }
            public override string ToString() { return Name; }
        }

        private sealed class SectionSourceChoice
        {
            public ObjectId Id { get; private set; }
            public string Name { get; private set; }
            public string TypeName { get; private set; }
            public SectionSourceChoice(ObjectId id, string name, string typeName) { Id = id; Name = name; TypeName = typeName; }
            public override string ToString() { return Name; }
        }

        private sealed class BatchCrossSectionSettings
        {
            public List<AlignmentChoice> Alignments;
            public List<SectionSourceChoice> Surfaces, Corridors;
            public string GroupNamePrefix, SampleLineLayer, SampleLineStyle, SampleLineLabelStyle, SectionViewStyle, BandSetStyle;
            public double Interval, LeftWidth, RightWidth, StartStation, EndStation, InsertX, InsertY, RowSpacing, ColumnSpacing;
            public int GroupsPerRow;
            public bool UseFullRange, IncludeStart, IncludeEnd, SampleAllAvailableSources, CreateSectionViews, RemoveExisting;
        }

        private sealed class BatchCrossSectionsWindow : Window
        {
            private readonly ListBox _alignments = new ListBox { SelectionMode = SelectionMode.Extended, MinHeight = 130 };
            private readonly TextBox _groupPrefix = Field("SL Collection");
            private readonly TextBox _interval = Field("10");
            private readonly TextBox _left = Field("10");
            private readonly TextBox _right = Field("10");
            private readonly TextBox _start = Field("0");
            private readonly TextBox _end = Field("0");
            private readonly TextBox _layer = Field("C-ROAD-SAMP");
            private readonly TextBox _x = Field("0");
            private readonly TextBox _y = Field("0");
            private readonly Document _document;
            private readonly TextBox _rowSpacing = Field("100");
            private readonly TextBox _columnSpacing = Field("200");
            private readonly TextBox _groupsPerRow = Field("3");
            private readonly ComboBox _sampleStyle = new ComboBox { MinWidth = 220 };
            private readonly ComboBox _labelStyle = new ComboBox { MinWidth = 220 };
            private readonly ComboBox _sectionStyle = new ComboBox { MinWidth = 220 };
            private readonly ComboBox _bandSet = new ComboBox { MinWidth = 220 };
            private readonly CheckBox _fullRange = new CheckBox { Content = "Use each alignment's full station range", IsChecked = true };
            private readonly CheckBox _includeStart = new CheckBox { Content = "Include range start", IsChecked = true };
            private readonly CheckBox _includeEnd = new CheckBox { Content = "Include range end", IsChecked = true };
            private readonly CheckBox _sampleSources = new CheckBox { Content = "Enable all available section sources", IsChecked = true };
            private readonly ListBox _surfaces = new ListBox { SelectionMode = SelectionMode.Extended, MinHeight = 75 };
            private readonly ListBox _corridors = new ListBox { SelectionMode = SelectionMode.Extended, MinHeight = 75 };
            private readonly CheckBox _createSectionViews = new CheckBox { Content = "Run cross sections (create section views)", IsChecked = true };
            private readonly CheckBox _removeExisting = new CheckBox { Content = "Remove existing sample-line groups / cross sections for selected alignments before creating new ones", IsChecked = false };
            public bool Accepted { get; private set; }
            public BatchCrossSectionSettings Settings { get; private set; }

            public BatchCrossSectionsWindow(List<AlignmentChoice> alignments, List<string> sampleStyles, List<string> labelStyles, List<string> sectionStyles, List<string> bandSets, List<SectionSourceChoice> surfaces, List<SectionSourceChoice> corridors, Document document)
            {
                _document = document;
                Title = "CE Tools - Batch Cross Sections";
                Width = 760; Height = 820; WindowStartupLocation = WindowStartupLocation.CenterScreen;
                ResizeMode = ResizeMode.CanResize;
                var root = new DockPanel { Margin = new Thickness(14) };
                Content = root;
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
                var cancel = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(4) };
                cancel.Click += (s, e) => { Accepted = false; Close(); };
                var run = new Button { Content = "Create Sample Lines + Section Views", Width = 245, Margin = new Thickness(4), IsDefault = true };
                run.Click += OnAccept;
                buttons.Children.Add(cancel); buttons.Children.Add(run);
                DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

                var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                DockPanel.SetDock(scroll, Dock.Top); root.Children.Add(scroll);
                var panel = new StackPanel();
                scroll.Content = panel;
                panel.Children.Add(new TextBlock { Text = "Select multiple road alignments (Ctrl/Shift for multi-select)", FontWeight = FontWeights.Bold, Margin = new Thickness(0,0,0,5) });
                foreach (var a in alignments) _alignments.Items.Add(a);
                if (_alignments.Items.Count > 0) _alignments.SelectAll();
                panel.Children.Add(_alignments);

                AddHeader(panel, "01  Sample line group and sampling");
                AddRow(panel, "Sample-line group name prefix", _groupPrefix);
                AddRow(panel, "Sample-line layer (created if missing)", _layer);
                AddRow(panel, "Sample-line style", _sampleStyle, sampleStyles);
                AddRow(panel, "Sampling interval along alignment (m)", _interval);
                AddRow(panel, "Left swath width (m)", _left);
                AddRow(panel, "Right swath width (m)", _right);
                panel.Children.Add(_fullRange);
                var range = new StackPanel { Orientation = Orientation.Horizontal };
                range.Children.Add(new TextBlock { Text = "Start station", Width = 120, VerticalAlignment = VerticalAlignment.Center }); range.Children.Add(_start);
                range.Children.Add(new TextBlock { Text = "End station", Width = 100, Margin = new Thickness(10,0,0,0), VerticalAlignment = VerticalAlignment.Center }); range.Children.Add(_end);
                panel.Children.Add(range);
                panel.Children.Add(_includeStart); panel.Children.Add(_includeEnd);
                panel.Children.Add(_sampleSources);
                AddHeader(panel, "01A  Surfaces and corridors to sample");
                panel.Children.Add(new TextBlock { Text = "Select one or multiple surfaces (Ctrl/Shift for multi-select). Leave empty to use the checkbox above.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,2,0,2) });
                foreach (SectionSourceChoice item in surfaces) _surfaces.Items.Add(item);
                panel.Children.Add(_surfaces);
                panel.Children.Add(new TextBlock { Text = "Select one or multiple corridors / corridor surfaces to sample.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,6,0,2) });
                foreach (SectionSourceChoice item in corridors) _corridors.Items.Add(item);
                panel.Children.Add(_corridors);

                AddHeader(panel, "02  Section view style and bands");
                AddRow(panel, "Section-view style", _sectionStyle, sectionStyles);
                AddRow(panel, "Section-view band set", _bandSet, bandSets);

                AddHeader(panel, "03  Cross-section placement and output");
                panel.Children.Add(_createSectionViews);
                panel.Children.Add(_removeExisting);
                panel.Children.Add(new TextBlock { Text = "Leave removal unchecked to keep existing sample lines and cross sections. When checked, existing sample-line groups for the selected alignments are erased before new groups are created.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,2,0,4) });
                panel.Children.Add(new TextBlock { Text = "Start point is the TOP-LEFT insertion point. Alignment groups are placed left-to-right, then top-to-bottom in the order selected above.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,2,0,4) });
                var pickPoint = new Button { Content = "Pick insertion point in drawing...", Margin = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left };
                pickPoint.Click += (sender, args) =>
                {
                    Hide();
                    try
                    {
                        var prompt = new Autodesk.AutoCAD.EditorInput.PromptPointOptions("\nSpecify top-left cross-section insertion point: ");
                        var picked = _document.Editor.GetPoint(prompt);
                        if (picked.Status == Autodesk.AutoCAD.EditorInput.PromptStatus.OK)
                        {
                            _x.Text = picked.Value.X.ToString("0.###", CultureInfo.CurrentCulture);
                            _y.Text = picked.Value.Y.ToString("0.###", CultureInfo.CurrentCulture);
                        }
                    }
                    finally { Show(); Activate(); }
                };
                panel.Children.Add(pickPoint);
                AddRow(panel, "Top-left start point X", _x); AddRow(panel, "Top-left start point Y", _y);
                AddRow(panel, "Alignment groups per row", _groupsPerRow);
                AddRow(panel, "Horizontal spacing between alignment groups", _columnSpacing);
                AddRow(panel, "Vertical spacing between rows", _rowSpacing);

                _sampleStyle.SelectedIndex = FindIndex(_sampleStyle, "Road Sample Line");
                _labelStyle.SelectedIndex = FindIndex(_labelStyle, "Section Name");
                _sectionStyle.SelectedIndex = FindIndex(_sectionStyle, "Road Section");
                _bandSet.SelectedIndex = FindIndex(_bandSet, "Major Stations Offset and Elevations");
                _fullRange.Checked += (s,e) => { _start.IsEnabled = false; _end.IsEnabled = false; };
                _fullRange.Unchecked += (s,e) => { _start.IsEnabled = true; _end.IsEnabled = true; };
                _start.IsEnabled = _end.IsEnabled = false;
            }

            private static TextBox Field(string value) => new TextBox { Text = value, MinWidth = 80, Margin = new Thickness(3) };
            private static void AddHeader(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.Bold, FontSize = 15, Margin = new Thickness(0, 14, 0, 5) });
            private static int FindIndex(ComboBox box, string desired)
            {
                for (int i=0;i<box.Items.Count;i++) if (string.Equals(box.Items[i] as string, desired, StringComparison.OrdinalIgnoreCase)) return i;
                return box.Items.Count > 0 ? 0 : -1;
            }
            private static void AddRow(Panel panel, string label, Control box)
            {
                var grid = new Grid { Margin = new Thickness(0,2,0,2) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.Children.Add(new TextBlock { Text=label, VerticalAlignment=VerticalAlignment.Center });
                Grid.SetColumn(box,1); grid.Children.Add(box); panel.Children.Add(grid);
            }
            private static void AddRow(Panel panel, string label, ComboBox box, List<string> choices)
            {
                foreach (string name in choices) box.Items.Add(name);
                AddRow(panel,label,(Control)box);
            }
            private void OnAccept(object sender, RoutedEventArgs e)
            {
                double interval, left, right, start, end, x, y, spacing, columnSpacing;
                int groupsPerRow;
                if (_alignments.SelectedItems.Count == 0) { MessageBox.Show(this, "Select at least one alignment."); return; }
                if (!TryReadPositive(_interval,out interval) || !TryReadPositive(_left,out left) || !TryReadPositive(_right,out right) ||
                    !TryReadPositive(_rowSpacing,out spacing) ||
                    !TryReadPositive(_columnSpacing,out columnSpacing) ||
                    !int.TryParse(_groupsPerRow.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out groupsPerRow) || groupsPerRow < 1 ||
                    !double.TryParse(_x.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out x) ||
                    !double.TryParse(_y.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out y))
                { MessageBox.Show(this, "Interval, swath widths and horizontal/vertical spacing must be positive; groups per row must be a positive whole number; insertion coordinates must be numeric."); return; }
                start = 0; end = 0;
                if (_fullRange.IsChecked != true &&
                    (!double.TryParse(_start.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out start) ||
                     !double.TryParse(_end.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out end) || end <= start))
                { MessageBox.Show(this, "Enter a valid start and end station."); return; }

                Settings = new BatchCrossSectionSettings
                {
                    Alignments = _alignments.SelectedItems.Cast<AlignmentChoice>().ToList(),
                    Surfaces = _surfaces.SelectedItems.Cast<SectionSourceChoice>().ToList(),
                    Corridors = _corridors.SelectedItems.Cast<SectionSourceChoice>().ToList(),
                    GroupNamePrefix = _groupPrefix.Text,
                    SampleLineLayer = _layer.Text,
                    SampleLineStyle = _sampleStyle.SelectedItem as string,
                    SampleLineLabelStyle = _labelStyle.SelectedItem as string,
                    SectionViewStyle = _sectionStyle.SelectedItem as string,
                    BandSetStyle = _bandSet.SelectedItem as string,
                    Interval = interval, LeftWidth = left, RightWidth = right,
                    StartStation = start, EndStation = end,
                    UseFullRange = _fullRange.IsChecked == true,
                    IncludeStart = _includeStart.IsChecked == true, IncludeEnd = _includeEnd.IsChecked == true,
                    SampleAllAvailableSources = _sampleSources.IsChecked == true,
                    CreateSectionViews = _createSectionViews.IsChecked == true,
                    RemoveExisting = _removeExisting.IsChecked == true,
                    InsertX = x, InsertY = y, RowSpacing = spacing,
                    ColumnSpacing = columnSpacing, GroupsPerRow = groupsPerRow
                };
                Accepted = true; Close();
            }
        }
    }
}
