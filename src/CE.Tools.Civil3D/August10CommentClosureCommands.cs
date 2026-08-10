using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilCogoPoint = Autodesk.Civil.DatabaseServices.CogoPoint;
using WinForms = System.Windows.Forms;

[assembly: CommandClass(typeof(CETools.Civil3D.August10CommentClosureCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Final user-comment closure surface. These commands reuse the existing CE
    /// dynamic engines and add the missing common runtime behaviour: global
    /// shortcuts, selective overlap/restore, draw-order controls, source zoom,
    /// interoperability, feature-line refresh and explicit shared-setting modes.
    /// </summary>
    public sealed class August10CommentClosureCommands
    {
        [CommandMethod("CE_TOOLS", "CE_COMMENTCLOSURE", CommandFlags.Modal)]
        public void ClosureCentre()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Final Comment Closure Centre",
                "Common runtime fixes and interoperability requested during the final Civil 3D 2023 review.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Resolve annotation overlaps", "CE_OVERLAPSMART", "Resolve COGO/MText/MLeader/Text/Dimension/Table overlaps with All/Selected scope. Source point coordinates never move.", "01 Annotation"),
                    new DisciplineWorkflowAction("Restore annotation positions", "CE_ANNOTATIONRESTORE", "Restore all or selected annotations to their stored pre-overlap locations.", "01 Annotation"),
                    new DisciplineWorkflowAction("Annotation background masks", "CE_ANNOTATIONMASK", "Turn MText and MLeader background masks on or off for all/selected annotations.", "01 Annotation"),
                    new DisciplineWorkflowAction("Bring/send design labels", "CE_ANNOTATIONDRAWORDER", "Bring supported design annotations to front or send them behind linework; All/Selected.", "01 Annotation"),
                    new DisciplineWorkflowAction("Zoom from linked table", "CE_TABLESOURCEZOOM", "Select a linked CE table and zoom/select one or all discoverable source objects.", "02 Tables"),
                    new DisciplineWorkflowAction("Refresh feature-line annotations/tables", "CE_FLANNOTREFRESH", "Refresh linked vertex setting-out and feature-line report tables; All or Selected source feature lines.", "02 Tables"),
                    new DisciplineWorkflowAction("Namibia LO / WGS84 conversion", "CE_NAMIBIALO", "Correct Schwarzeck Lo22/zone conversion including decimal/DMS WGS84 values.", "03 Survey"),
                    new DisciplineWorkflowAction("Pick point coordinate review", "CE_COORDPICKMAP", "Pick any drawing point and calculate drawing XY, Namibia LO and WGS84 values.", "03 Survey"),
                    new DisciplineWorkflowAction("LandXML tools", "CE_LANDXMLTOOLS", "Open native Civil 3D LandXML import/export workflows.", "03 Survey"),
                    new DisciplineWorkflowAction("Export Civil design to CAD copy", "CE_EXPORTCADCOPY", "Create a separate AutoCAD-compatible copy while retaining current CAD objects in the active design drawing.", "04 Drawing"),
                    new DisciplineWorkflowAction("Network multi-object tools", "CE_NETWORKMULTI", "Open multiple pipe/structure creation, connect and schedule workflows.", "05 Networks"),
                    new DisciplineWorkflowAction("Production settings mode", "CE_SETTINGSMODE", "Choose Keep existing drawing settings or Use saved project settings for subsequent popup workflows.", "06 Settings"),
                    new DisciplineWorkflowAction("Safe profile/band batch", "CE_PROFILEBATCHSAFE", "Run style/band refresh steps separately so a failed profile view does not hide the remaining repair commands.", "07 Profiles"),
                    new DisciplineWorkflowAction("Refresh all linked CE outputs", "CE_COMMENTREFRESHALL", "Run the universal linked refresh, vertex/feature-line/platform/road-layout refresh sequence.", "08 Refresh")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_OVERLAPSMART", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ResolveOverlaps()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Resolve Annotation Overlaps",
                "Only annotations that actually collide are moved. COGO source coordinates remain fixed; moved items stay close to their original/reference position and can be restored later.");
            settings.AddChoice("Scope", "01 Selection", "Scope", "All", "Process all supported annotations or only selected annotations.", new[] { "All", "Selected" });
            settings.AddChoice("Types", "01 Selection", "Annotation types", "All supported", "Restrict the resolver to one annotation family.", new[] { "All supported", "COGO labels", "MText / MLeader / Text", "Dimensions / Tables" });
            settings.AddPositiveDouble("Maximum", "02 Placement", "Maximum movement (paper mm)", 6.0, "Maximum permitted displacement from the stored original/reference annotation position.");
            settings.AddChoice("Mask", "03 Presentation", "MText/MLeader background mask", "Keep current", "Optionally enable a background mask while resolving text overlaps.", new[] { "Keep current", "On", "Off" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            HashSet<ObjectId> selected = null;
            if (string.Equals(settings.Text("Scope"), "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect annotations whose labels may move: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
                if (selection.Status != PromptStatus.OK || selection.Value == null) return;
                selected = new HashSet<ObjectId>(selection.Value.GetObjectIds());
            }

            string types = settings.Text("Types");
            int moved = 0;
            int cogoMoved = 0;
            if (string.Equals(types, "All supported", StringComparison.OrdinalIgnoreCase) || string.Equals(types, "COGO labels", StringComparison.OrdinalIgnoreCase))
            {
                CogoPointProjectStyleCommands.ApplySelectedStyles(document, false);
                ISet<ObjectId> restricted = selected == null ? null : new HashSet<ObjectId>(selected.Where(id => IsCogoPoint(document.Database, id)));
                if (selected == null || restricted.Count > 0)
                    cogoMoved = CogoPointProjectStyleCommands.ResolveOverlaps(document, restricted);
            }
            if (!string.Equals(types, "COGO labels", StringComparison.OrdinalIgnoreCase))
                moved = SmartAnnotationRuntime.Resolve(document, selected, types, settings.Double("Maximum", 6.0), settings.Text("Mask"));

            RuntimeAnnotationLinkManager.ClampLinkedAnnotations(document, true);
            UniversalDynamicRefreshManager.Queue();
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_OVERLAPSMART complete. COGO labels moved={0}; other annotations moved={1}; source point coordinates unchanged.", cogoMoved, moved);
        }

        [CommandMethod("CE_TOOLS", "CE_ANNOTATIONRESTORE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RestoreAnnotations()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Restore Annotation Positions",
                "Restore annotations moved by CE Tools overlap resolution. Restoring a COGO label changes only the label location, never the COGO point coordinate.");
            settings.AddChoice("Scope", "Restore", "Scope", "All", "Restore all stored annotation positions or only selected items.", new[] { "All", "Selected" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            HashSet<ObjectId> selected = null;
            if (string.Equals(settings.Text("Scope"), "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect annotations to restore: ", AllowDuplicates = false });
                if (selection.Status != PromptStatus.OK || selection.Value == null) return;
                selected = new HashSet<ObjectId>(selection.Value.GetObjectIds());
            }
            int restored = SmartAnnotationRuntime.Restore(document, selected);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ANNOTATIONRESTORE complete. Restored annotations={0}.", restored);
        }

        [CommandMethod("CE_TOOLS", "CE_ANNOTATIONMASK", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void AnnotationMasks()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Annotation Background Masks",
                "Apply background masks to MText and MLeader content without changing source geometry.");
            settings.AddChoice("Scope", "Mask", "Scope", "Selected", "Apply to selected text/leaders or all supported annotations.", new[] { "Selected", "All" });
            settings.AddChoice("Mask", "Mask", "Background mask", "On", "Turn background masking on or off.", new[] { "On", "Off" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            HashSet<ObjectId> selected = ResolveSelection(document, settings.Text("Scope"), "\nSelect MText/MLeader annotations: ");
            int changed = SmartAnnotationRuntime.ApplyMask(document, selected, string.Equals(settings.Text("Mask"), "On", StringComparison.OrdinalIgnoreCase));
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ANNOTATIONMASK complete. Changed={0}.", changed);
        }

        [CommandMethod("CE_TOOLS", "CE_ANNOTATIONDRAWORDER", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void AnnotationDrawOrder()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Design Annotation Draw Order",
                "Move design labels, dimensions, leaders, COGO points and tables in draw order without changing their geometry.");
            settings.AddChoice("Scope", "Draw Order", "Scope", "Selected", "Use selected supported annotations or all supported annotations in current space.", new[] { "Selected", "All" });
            settings.AddChoice("Order", "Draw Order", "Action", "Bring to front", "Bring design annotations to front or send them to back.", new[] { "Bring to front", "Send to back" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            HashSet<ObjectId> selected = ResolveSelection(document, settings.Text("Scope"), "\nSelect design annotations: ");
            int changed = SmartAnnotationRuntime.ApplyDrawOrder(document, selected, settings.Text("Order").StartsWith("Bring", StringComparison.OrdinalIgnoreCase));
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ANNOTATIONDRAWORDER complete. Objects reordered={0}.", changed);
        }

        [CommandMethod("CE_TOOLS", "CE_TABLESOURCEZOOM", CommandFlags.Modal | CommandFlags.Redraw)]
        public void TableSourceZoom()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityOptions options = new PromptEntityOptions("\nSelect a linked CE table: ");
            options.SetRejectMessage("\nSelect a Table object.");
            options.AddAllowedClass(typeof(Table), false);
            PromptEntityResult picked = document.Editor.GetEntity(options);
            if (picked.Status != PromptStatus.OK) return;
            List<ObjectId> sources = LinkedTableSourceNavigator.Discover(document.Database, picked.ObjectId);
            if (sources.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_TABLESOURCEZOOM: no live source handles were found in this table link.");
                return;
            }
            int index = 0;
            if (sources.Count > 1)
            {
                PromptIntegerOptions prompt = new PromptIntegerOptions("\nSource number to select/zoom <0 = all>: ") { AllowNegative = false, DefaultValue = 0, UseDefaultValue = true, LowerLimit = 0, UpperLimit = sources.Count };
                PromptIntegerResult result = document.Editor.GetInteger(prompt);
                if (result.Status != PromptStatus.OK) return;
                index = result.Value;
            }
            ObjectId[] target = index <= 0 ? sources.ToArray() : new[] { sources[index - 1] };
            document.Editor.SetImpliedSelection(target);
            ZoomToObjects(document, target);
            document.Editor.WriteMessage("\nCE_TABLESOURCEZOOM selected {0} linked source object(s).", target.Length);
        }

        [CommandMethod("CE_TOOLS", "CE_FLANNOTREFRESH", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FeatureLineRefresh()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Refresh Feature Line Annotation and Tables",
                "Refresh dynamic vertex setting-out and feature-line report output. Use All to refresh the complete drawing or Selected to first restrict the source selection for downstream CE commands.");
            settings.AddChoice("Scope", "Refresh", "Scope", "All", "Refresh all links or select source feature lines first.", new[] { "All", "Selected" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            if (string.Equals(settings.Text("Scope"), "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect source feature lines: ", AllowDuplicates = false });
                if (selection.Status != PromptStatus.OK || selection.Value == null) return;
                document.Editor.SetImpliedSelection(selection.Value.GetObjectIds());
            }
            int vertex = 0;
            int reports = 0;
            try { vertex = VertexSettingOutCommands.RefreshAll(document); } catch { }
            try { reports = FinalFeatureLineReportCommands.RefreshAll(document); } catch { }
            try { FeatureLineRelativeCommands.RefreshAll(document); } catch { }
            try { PlatformProductionCommands.RefreshAll(document); } catch { }
            UniversalDynamicRefreshManager.Queue();
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_FLANNOTREFRESH complete. Vertex groups/tables={0}; feature-line report tables={1}.", vertex, reports);
        }

        [CommandMethod("CE_TOOLS", "CE_LANDXMLTOOLS", CommandFlags.Modal)]
        public void LandXmlTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(document, "CE Tools - LandXML", "Use Civil 3D's native LandXML import/export so supported surfaces, alignments, parcels, pipe-network data and points retain their native definitions.", new List<DisciplineWorkflowAction>
            {
                new DisciplineWorkflowAction("Import LandXML", "CE_LANDXMLIMPORT", "Open Civil 3D native LandXML import/file selection.", "01 Import"),
                new DisciplineWorkflowAction("Export LandXML", "CE_LANDXMLEXPORT", "Open Civil 3D native LandXML export workflow.", "02 Export")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_LANDXMLIMPORT", CommandFlags.Modal)]
        public void LandXmlImport() { ActiveDocument()?.SendStringToExecute("_LANDXMLIN ", true, false, true); }

        [CommandMethod("CE_TOOLS", "CE_LANDXMLEXPORT", CommandFlags.Modal)]
        public void LandXmlExport() { ActiveDocument()?.SendStringToExecute("_LANDXMLOUT ", true, false, true); }

        [CommandMethod("CE_TOOLS", "CE_EXPORTCADCOPY", CommandFlags.Modal)]
        public void ExportCadCopy()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            document.Editor.WriteMessage("\nCE_EXPORTCADCOPY: Civil 3D's Export Civil 3D Drawing workflow will create a separate AutoCAD-compatible copy. The active design drawing and its existing CAD objects remain unchanged.");
            document.SendStringToExecute("_EXPORTC3DDRAWING ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_NETWORKMULTI", CommandFlags.Modal)]
        public void NetworkMulti()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(document, "CE Tools - Multiple Pipe / Structure Tools", "Create or connect multiple network objects, then refresh labels, schedules and profiles.", new List<DisciplineWorkflowAction>
            {
                new DisciplineWorkflowAction("Create network from multiple objects", "CE_NETWORKFROMPOLYLINES", "Create a Sewer/SW/Water/Bulk Water network from selected lines, polylines or feature lines.", "01 Create"),
                new DisciplineWorkflowAction("Auto-connect open pipe ends", "CE_NETWORKCONNECTALL", "Connect multiple open gravity-pipe ends to nearby structures.", "02 Connect"),
                new DisciplineWorkflowAction("Connect selected pipe / structure", "CE_NETWORKCONNECT", "Use Civil 3D native part connection for selected parts.", "02 Connect"),
                new DisciplineWorkflowAction("Network schedule", "CE_NETWORKSCHEDULETOOLS", "Create/refresh multiple pipe and structure schedule data.", "03 Report"),
                new DisciplineWorkflowAction("Sewer production", "CE_SEWTOOLS", "Sequence, label, align and profile the resulting sewer network.", "04 Production"),
                new DisciplineWorkflowAction("Stormwater production", "CE_SWTOOLS", "Sequence, label, align and profile the resulting stormwater network.", "04 Production"),
                new DisciplineWorkflowAction("Water production", "CE_WATERTOOLS", "Sequence, label, align and profile the resulting water network.", "04 Production")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_SETTINGSMODE", CommandFlags.Modal)]
        public void SettingsMode()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Saved vs Drawing Settings",
                "Choose which stored values have priority when a production popup opens in this and subsequent drawings.");
            model.AddChoice("Mode", "Settings", "Popup settings source", CrossDrawingSettingsPreference.UseSavedProjectSettings ? "Use saved project settings" : "Keep existing drawing settings", "Keep existing loads project defaults then applies this DWG's overrides. Use saved project settings ignores DWG overrides when opening the next popup.", new[] { "Keep existing drawing settings", "Use saved project settings" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            CrossDrawingSettingsPreference.UseSavedProjectSettings = string.Equals(model.Text("Mode"), "Use saved project settings", StringComparison.OrdinalIgnoreCase);
            document.Editor.WriteMessage("\nCE_SETTINGSMODE: {0}.", CrossDrawingSettingsPreference.UseSavedProjectSettings ? "saved project settings now have priority" : "existing drawing settings now have priority");
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILEBATCHSAFE", CommandFlags.Modal)]
        public void ProfileBatchSafe()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(document, "CE Tools - Safe Profile / Band Batch", "Run each profile repair stage independently. If one Civil 3D object is incompatible, the other stages remain available instead of the complete workflow ending with an internal error.", new List<DisciplineWorkflowAction>
            {
                new DisciplineWorkflowAction("Import project profile/band styles", "CE_PROJECTSTYLEIMPORT", "Import the approved project Civil 3D style source first.", "01 Styles"),
                new DisciplineWorkflowAction("Batch profile-view styles / bands", "CE_PROFILEVIEWBATCHTOOLS", "Apply profile-view style, band set and automatic fit to multiple profile views.", "02 Views"),
                new DisciplineWorkflowAction("Apply data-linked band sets", "CE_PROFILEBANDSBATCH", "Link Primary/Secondary profiles and pipe-network data to selected profile views.", "02 Views"),
                new DisciplineWorkflowAction("Refresh profile bands", "CE_PROFILEBANDREFRESH", "Refresh all linked profile-band data while skipping unsupported individual items.", "03 Refresh"),
                new DisciplineWorkflowAction("Sewer profiles", "CE_SEWPROFILE", "Create/refresh sewer profiles after styles are available.", "04 Discipline"),
                new DisciplineWorkflowAction("Stormwater profiles", "CE_SWPROFILE", "Create/refresh stormwater profiles after styles are available.", "04 Discipline"),
                new DisciplineWorkflowAction("Water profiles", "CE_WATERPROFILE", "Create/refresh water profiles after styles are available.", "04 Discipline")
            });
        }

        [CommandMethod("CE_TOOLS", "CE_COMMENTREFRESHALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshAll()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            int warnings = 0;
            try { UniversalDynamicRefreshManager.RefreshNow(document); } catch { warnings++; }
            try { VertexSettingOutCommands.RefreshAll(document); } catch { warnings++; }
            try { FinalFeatureLineReportCommands.RefreshAll(document); } catch { warnings++; }
            try { FeatureLineRelativeCommands.RefreshAll(document); } catch { warnings++; }
            try { PlatformProductionCommands.RefreshAll(document); } catch { warnings++; }
            try { new RoadLayoutProductionCommands().RoadLayoutRefresh(); } catch { warnings++; }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_COMMENTREFRESHALL complete. Warnings={0}.", warnings);
        }

        private static HashSet<ObjectId> ResolveSelection(Document document, string scope, string prompt)
        {
            if (!string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase)) return null;
            PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = prompt, AllowDuplicates = false });
            return selection.Status == PromptStatus.OK && selection.Value != null ? new HashSet<ObjectId>(selection.Value.GetObjectIds()) : new HashSet<ObjectId>();
        }

        private static bool IsCogoPoint(Database database, ObjectId id)
        {
            if (id.IsNull || id.IsErased) return false;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                try { return transaction.GetObject(id, OpenMode.ForRead, false) is CivilCogoPoint; } catch { return false; }
            }
        }

        private static void ZoomToObjects(Document document, IEnumerable<ObjectId> ids)
        {
            Extents3d? total = null;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { continue; }
                    if (entity == null) continue;
                    try
                    {
                        Extents3d ext = entity.GeometricExtents;
                        if (!total.HasValue) total = ext;
                        else
                        {
                            Extents3d value = total.Value;
                            value.AddExtents(ext);
                            total = value;
                        }
                    }
                    catch { }
                }
            }
            if (!total.HasValue) return;
            Extents3d bounds = total.Value;
            Point3d centre = new Point3d((bounds.MinPoint.X + bounds.MaxPoint.X) * 0.5, (bounds.MinPoint.Y + bounds.MaxPoint.Y) * 0.5, (bounds.MinPoint.Z + bounds.MaxPoint.Z) * 0.5);
            double width = Math.Max(bounds.MaxPoint.X - bounds.MinPoint.X, 1.0) * 1.25;
            double height = Math.Max(bounds.MaxPoint.Y - bounds.MinPoint.Y, 1.0) * 1.25;
            using (ViewTableRecord view = document.Editor.GetCurrentView())
            {
                view.CenterPoint = new Point2d(centre.X, centre.Y);
                view.Width = Math.Max(width, height * Math.Max(1.0, view.Width / Math.Max(view.Height, 1e-9)));
                view.Height = Math.Max(height, width / Math.Max(1.0, view.Width / Math.Max(view.Height, 1e-9)));
                document.Editor.SetCurrentView(view);
            }
        }

        private static Document ActiveDocument() { return AcApplication.DocumentManager.MdiActiveDocument; }
    }

    internal static class CrossDrawingSettingsPreference
    {
        private static bool _useSaved;
        internal static bool UseSavedProjectSettings { get { return _useSaved; } set { _useSaved = value; } }
    }

    internal static class AugustGlobalShortcutManager
    {
        private static bool _initialized;
        private static CeMessageFilter _filter;

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                _filter = new CeMessageFilter();
                WinForms.Application.AddMessageFilter(_filter);
            }
            catch { }
        }

        internal static void Terminate()
        {
            if (!_initialized) return;
            _initialized = false;
            try { if (_filter != null) WinForms.Application.RemoveMessageFilter(_filter); } catch { }
            _filter = null;
        }

        private sealed class CeMessageFilter : WinForms.IMessageFilter
        {
            private const int WmKeyDown = 0x0100;
            private DateTime _last = DateTime.MinValue;
            public bool PreFilterMessage(ref WinForms.Message message)
            {
                if (message.Msg != WmKeyDown) return false;
                WinForms.Keys modifiers = WinForms.Control.ModifierKeys;
                bool control = (modifiers & WinForms.Keys.Control) == WinForms.Keys.Control;
                if (!control) return false;
                int key = message.WParam.ToInt32();
                string command = null;
                if (key == (int)WinForms.Keys.F) command = "CE_TOOLSPALETTE ";
                else if (key == (int)WinForms.Keys.M && (modifiers & WinForms.Keys.Shift) == WinForms.Keys.Shift) command = "CE_MOSTUSEDOVERALL ";
                if (command == null) return false;
                if ((DateTime.UtcNow - _last).TotalMilliseconds < 250.0) return true;
                _last = DateTime.UtcNow;
                Document document = AcApplication.DocumentManager.MdiActiveDocument;
                if (document != null) document.SendStringToExecute(command, true, false, true);
                return true;
            }
        }
    }

    internal static class SmartAnnotationRuntime
    {
        private const string OriginalKey = "CE_OVERLAP_ORIGINAL";

        internal static int Resolve(Document document, ISet<ObjectId> restricted, string types, double maximumPaperMm, string maskMode)
        {
            if (document == null) return 0;
            double step = Math.Max(PaperAnnotationScale.ModelDistance(document.Database, 2.0), 0.001);
            double maximum = Math.Max(PaperAnnotationScale.ModelDistance(document.Database, Math.Max(1.0, maximumPaperMm)), step);
            int moved = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return 0;
                List<AnnotationItem> items = new List<AnnotationItem>();
                foreach (ObjectId id in space)
                {
                    if (restricted != null && !restricted.Contains(id)) continue;
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; } catch { continue; }
                    if (entity == null || entity is CivilCogoPoint || !Matches(entity, types)) continue;
                    Point3d location;
                    if (!TryGetLocation(entity, out location)) continue;
                    SaveOriginal(entity, transaction, location);
                    ApplyMask(entity, maskMode);
                    Extents3d extents;
                    try { extents = entity.GeometricExtents; }
                    catch { extents = new Extents3d(location - new Vector3d(step, step * 0.5, 0.0), location + new Vector3d(step * 2.0, step * 0.5, 0.0)); }
                    items.Add(new AnnotationItem(id, location, extents));
                }

                for (int i = 0; i < items.Count; i++)
                {
                    AnnotationItem current = items[i];
                    bool collision = items.Where((item, index) => index != i).Any(item => Intersects(current.Extents, item.Extents));
                    if (!collision) continue;
                    Point3d best = current.Location;
                    Extents3d bestExtents = current.Extents;
                    bool found = false;
                    for (int ring = 1; ring <= 4 && !found; ring++)
                    {
                        double radius = Math.Min(maximum, step * ring);
                        for (int sector = 0; sector < 8; sector++)
                        {
                            double angle = sector * Math.PI / 4.0;
                            Point3d candidate = current.Location + new Vector3d(Math.Cos(angle) * radius, Math.Sin(angle) * radius, 0.0);
                            if (candidate.DistanceTo(current.Location) > maximum + 1e-8) continue;
                            Extents3d translated = Translate(current.Extents, candidate - current.Location);
                            bool blocked = items.Where((item, index) => index != i).Any(item => Intersects(translated, item.Extents));
                            if (blocked) continue;
                            best = candidate;
                            bestExtents = translated;
                            found = true;
                            break;
                        }
                    }
                    if (!found || best.DistanceTo(current.Location) <= 1e-8) continue;
                    Entity entity = transaction.GetObject(current.Id, OpenMode.ForWrite, false) as Entity;
                    if (entity == null || !TrySetLocation(entity, best)) continue;
                    current.Location = best;
                    current.Extents = bestExtents;
                    moved++;
                }
                transaction.Commit();
            }
            return moved;
        }

        internal static int Restore(Document document, ISet<ObjectId> restricted)
        {
            int restored = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return 0;
                foreach (ObjectId id in space)
                {
                    if (restricted != null && !restricted.Contains(id)) continue;
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; } catch { continue; }
                    if (entity == null) continue;
                    Point3d original;
                    if (!TryReadOriginal(entity, transaction, out original)) continue;
                    if (entity is CivilCogoPoint)
                    {
                        try { ((CivilCogoPoint)entity).LabelLocation = original; restored++; } catch { }
                    }
                    else if (TrySetLocation(entity, original)) restored++;
                }
                transaction.Commit();
            }
            return restored;
        }

        internal static int ApplyMask(Document document, ISet<ObjectId> restricted, bool enabled)
        {
            int changed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return 0;
                foreach (ObjectId id in space)
                {
                    if (restricted != null && !restricted.Contains(id)) continue;
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity; } catch { continue; }
                    MText text = entity as MText;
                    if (text != null)
                    {
                        text.BackgroundFill = enabled;
                        text.UseBackgroundColor = enabled;
                        changed++;
                        continue;
                    }
                    MLeader leader = entity as MLeader;
                    if (leader != null)
                    {
                        try
                        {
                            MText content = leader.MText;
                            if (content != null)
                            {
                                content.BackgroundFill = enabled;
                                content.UseBackgroundColor = enabled;
                                leader.MText = content;
                                changed++;
                            }
                        }
                        catch { }
                    }
                }
                transaction.Commit();
            }
            return changed;
        }

        internal static int ApplyDrawOrder(Document document, ISet<ObjectId> restricted, bool front)
        {
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return 0;
                ObjectIdCollection ids = new ObjectIdCollection();
                foreach (ObjectId id in space)
                {
                    if (restricted != null && !restricted.Contains(id)) continue;
                    Entity entity;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { continue; }
                    if (entity != null && IsSupportedAnnotation(entity)) ids.Add(id);
                }
                if (ids.Count == 0) return 0;
                DrawOrderTable order = transaction.GetObject(space.DrawOrderTableId, OpenMode.ForWrite, false) as DrawOrderTable;
                if (order == null) return 0;
                if (front) order.MoveToTop(ids); else order.MoveToBottom(ids);
                transaction.Commit();
                return ids.Count;
            }
        }

        private static bool Matches(Entity entity, string types)
        {
            if (string.Equals(types, "All supported", StringComparison.OrdinalIgnoreCase)) return IsSupportedAnnotation(entity);
            if (string.Equals(types, "MText / MLeader / Text", StringComparison.OrdinalIgnoreCase)) return entity is MText || entity is MLeader || entity is DBText;
            if (string.Equals(types, "Dimensions / Tables", StringComparison.OrdinalIgnoreCase)) return entity is Dimension || entity is Table;
            return false;
        }

        private static bool IsSupportedAnnotation(Entity entity)
        {
            if (entity is CivilCogoPoint || entity is MText || entity is MLeader || entity is DBText || entity is Dimension || entity is Table) return true;
            string type = entity.GetType().Name;
            return type.IndexOf("Label", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ApplyMask(Entity entity, string mode)
        {
            if (string.Equals(mode, "Keep current", StringComparison.OrdinalIgnoreCase)) return;
            bool enabled = string.Equals(mode, "On", StringComparison.OrdinalIgnoreCase);
            MText text = entity as MText;
            if (text != null) { text.BackgroundFill = enabled; text.UseBackgroundColor = enabled; return; }
            MLeader leader = entity as MLeader;
            if (leader != null)
            {
                try { MText content = leader.MText; if (content != null) { content.BackgroundFill = enabled; content.UseBackgroundColor = enabled; leader.MText = content; } } catch { }
            }
        }

        private static bool TryGetLocation(Entity entity, out Point3d location)
        {
            location = Point3d.Origin;
            MText mtext = entity as MText; if (mtext != null) { location = mtext.Location; return true; }
            DBText text = entity as DBText; if (text != null) { location = text.Position; return true; }
            Dimension dimension = entity as Dimension; if (dimension != null) { location = dimension.TextPosition; return true; }
            Table table = entity as Table; if (table != null) { location = table.Position; return true; }
            return TryPointProperty(entity, new[] { "TextLocation", "LabelLocation", "Location", "Position", "TextPosition" }, false, ref location);
        }

        private static bool TrySetLocation(Entity entity, Point3d location)
        {
            MText mtext = entity as MText; if (mtext != null) { mtext.Location = location; return true; }
            DBText text = entity as DBText; if (text != null) { text.Position = location; return true; }
            Dimension dimension = entity as Dimension; if (dimension != null) { dimension.TextPosition = location; return true; }
            Table table = entity as Table; if (table != null) { table.Position = location; return true; }
            Point3d dummy = location;
            return TryPointProperty(entity, new[] { "TextLocation", "LabelLocation", "Location", "Position", "TextPosition" }, true, ref dummy);
        }

        private static bool TryPointProperty(object target, IEnumerable<string> names, bool set, ref Point3d value)
        {
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || property.PropertyType != typeof(Point3d)) continue;
                    if (set)
                    {
                        if (!property.CanWrite) continue;
                        property.SetValue(target, value, null);
                    }
                    else value = (Point3d)property.GetValue(target, null);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static void SaveOriginal(Entity entity, Transaction transaction, Point3d point)
        {
            Point3d existing;
            if (TryReadOriginal(entity, transaction, out existing)) return;
            if (entity.ExtensionDictionary.IsNull) entity.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null) return;
            var record = new Xrecord { Data = new ResultBuffer(new TypedValue((int)DxfCode.Real, point.X), new TypedValue((int)DxfCode.Real, point.Y), new TypedValue((int)DxfCode.Real, point.Z)) };
            dictionary.SetAt(OriginalKey, record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static bool TryReadOriginal(Entity entity, Transaction transaction, out Point3d point)
        {
            point = Point3d.Origin;
            if (entity == null || entity.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary;
            try { dictionary = transaction.GetObject(entity.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary; } catch { return false; }
            if (dictionary == null || !dictionary.Contains(OriginalKey)) return false;
            Xrecord record = transaction.GetObject(dictionary.GetAt(OriginalKey), OpenMode.ForRead, false) as Xrecord;
            TypedValue[] values = record == null || record.Data == null ? null : record.Data.AsArray();
            if (values == null || values.Length < 3) return false;
            try { point = new Point3d(Convert.ToDouble(values[0].Value, CultureInfo.InvariantCulture), Convert.ToDouble(values[1].Value, CultureInfo.InvariantCulture), Convert.ToDouble(values[2].Value, CultureInfo.InvariantCulture)); return true; }
            catch { return false; }
        }

        private static bool Intersects(Extents3d a, Extents3d b)
        {
            return a.MinPoint.X <= b.MaxPoint.X && a.MaxPoint.X >= b.MinPoint.X && a.MinPoint.Y <= b.MaxPoint.Y && a.MaxPoint.Y >= b.MinPoint.Y;
        }

        private static Extents3d Translate(Extents3d value, Vector3d movement)
        {
            return new Extents3d(value.MinPoint + movement, value.MaxPoint + movement);
        }

        private sealed class AnnotationItem
        {
            internal AnnotationItem(ObjectId id, Point3d location, Extents3d extents) { Id = id; Location = location; Extents = extents; }
            internal ObjectId Id { get; private set; }
            internal Point3d Location { get; set; }
            internal Extents3d Extents { get; set; }
        }
    }

    internal static class LinkedTableSourceNavigator
    {
        internal static List<ObjectId> Discover(Database database, ObjectId tableId)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                Table table;
                try { table = transaction.GetObject(tableId, OpenMode.ForRead, false) as Table; } catch { return result; }
                if (table == null || table.ExtensionDictionary.IsNull) return result;
                DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                if (dictionary == null) return result;
                foreach (DBDictionaryEntry entry in dictionary)
                {
                    Xrecord record;
                    try { record = transaction.GetObject(entry.Value, OpenMode.ForRead, false) as Xrecord; } catch { continue; }
                    if (record == null || record.Data == null) continue;
                    foreach (TypedValue value in record.Data)
                    {
                        string text = Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                        foreach (string token in text.Split(new[] { ',', ';', '|', ' ', '\t', '\r', '\n', '=' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string candidate = token.Trim();
                            long handle;
                            if (candidate.Length == 0 || !long.TryParse(candidate, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out handle)) continue;
                            try
                            {
                                ObjectId id = database.GetObjectId(false, new Handle(handle), 0);
                                if (!id.IsNull && !id.IsErased && !result.Contains(id)) result.Add(id);
                            }
                            catch { }
                        }
                    }
                }
            }
            return result;
        }
    }
}
