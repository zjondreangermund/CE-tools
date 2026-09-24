using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.September24FieldCompletionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Field Completion batch controls added on 24 September 2026.
    /// These controls deliberately use the native Civil 3D objects and expose
    /// one transaction/report per batch so a failed object does not hide the
    /// result for the other selected objects.
    /// </summary>
    public sealed class September24FieldCompletionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_PROFILEMOVEVERTICAL", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void MoveSelectedProfilesVertically()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Move Multiple Design Profiles Vertically",
                "Move the editable PVIs of every selected design profile by one signed vertical distance.");
            settings.AddPositiveDouble(
                "Distance",
                "01 Vertical move",
                "Vertical distance",
                0.100,
                "Positive drawing-unit distance applied to every editable PVI.");
            settings.AddChoice(
                "Direction",
                "01 Vertical move",
                "Direction",
                "Up",
                "Move all selected profile PVIs up or down.",
                new[] { "Up", "Down" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            double distance = Math.Max(0.0, settings.Double("Distance", 0.100));
            double delta = string.Equals(settings.Text("Direction"), "Down", StringComparison.OrdinalIgnoreCase)
                ? -distance
                : distance;
            PromptSelectionResult selection = SelectImpliedOrPrompt(
                document.Editor,
                "\nSelect multiple design profiles to move vertically: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            int profiles = 0;
            int pvis = 0;
            int skipped = 0;
            using (DocumentLock lockDocument = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    CivilProfile profile = null;
                    try { profile = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilProfile; }
                    catch { }
                    if (profile == null)
                    {
                        skipped++;
                        continue;
                    }

                    int changed = MoveProfilePvis(profile, delta);
                    if (changed == 0)
                    {
                        skipped++;
                        continue;
                    }
                    profiles++;
                    pvis += changed;
                    TryInvokeNoArguments(profile, "Update");
                    TryInvokeNoArguments(profile, "Rebuild");
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PROFILEMOVEVERTICAL complete. Profiles changed={0}; PVIs moved={1}; skipped={2}; delta={3:0.###}.",
                profiles, pvis, skipped, delta);
        }

        [CommandMethod("CE_TOOLS", "CE_SURFACEBATCHCONTROL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void BatchSurfaceControl()
        {
            Document document = ActiveDocument();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            List<CivilChoice> surfaces = FieldCompletionBatchUi.ReadSurfaceChoices(document, civilDocument);
            if (surfaces.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_SURFACEBATCHCONTROL cancelled. No Civil 3D surfaces were found.");
                return;
            }

            IList<CivilChoice> selected = FieldCompletionBatchUi.PickMultiple(
                "CE Tools - Select Surfaces",
                "Select one or more surfaces for rebuild and automatic-rebuild control.",
                surfaces);
            if (selected == null || selected.Count == 0) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Multiple Surface Rebuild / Automatic Control",
                "Apply a native rebuild and/or the Civil 3D automatic-rebuild switch to every selected surface.");
            settings.AddChoice(
                "Action",
                "01 Surface action",
                "Action",
                "Rebuild only",
                "Choose whether to rebuild, switch automatic rebuilding, or do both.",
                new[]
                {
                    "Rebuild only",
                    "Automatic rebuild On",
                    "Automatic rebuild Off",
                    "Rebuild + automatic rebuild On",
                    "Rebuild + automatic rebuild Off"
                });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            string action = settings.Text("Action");
            bool rebuild = action.StartsWith("Rebuild", StringComparison.OrdinalIgnoreCase);
            bool setAutomatic = action.IndexOf("automatic rebuild", StringComparison.OrdinalIgnoreCase) >= 0;
            bool automatic = action.EndsWith("On", StringComparison.OrdinalIgnoreCase);
            int changed = 0;
            int rebuilt = 0;
            int failed = 0;

            using (DocumentLock lockDocument = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (CivilChoice choice in selected)
                {
                    CivilSurface surface = null;
                    try { surface = transaction.GetObject(choice.Id, OpenMode.ForWrite, false) as CivilSurface; }
                    catch { }
                    if (surface == null)
                    {
                        failed++;
                        continue;
                    }

                    bool localChanged = false;
                    if (setAutomatic &&
                        FieldCompletionBatchUi.TrySetBoolean(
                            surface,
                            automatic,
                            "RebuildAutomatic",
                            "AutomaticRebuild",
                            "IsRebuildAutomatic"))
                    {
                        localChanged = true;
                    }

                    if (rebuild &&
                        FieldCompletionBatchUi.TryInvokeNoArguments(surface, "Rebuild"))
                    {
                        rebuilt++;
                        localChanged = true;
                    }

                    if (localChanged) changed++;
                    else failed++;
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_SURFACEBATCHCONTROL complete. Selected={0}; changed={1}; rebuilt={2}; unsupported/failed={3}.",
                selected.Count, changed, rebuilt, failed);
        }

        [CommandMethod("CE_TOOLS", "CE_CORRIDORBATCHCONTROL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void BatchCorridorControl()
        {
            Document document = ActiveDocument();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            List<CivilChoice> corridors = FieldCompletionBatchUi.ReadCorridorChoices(document, civilDocument);
            if (corridors.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_CORRIDORBATCHCONTROL cancelled. No Civil 3D corridors were found.");
                return;
            }

            IList<CivilChoice> selected = FieldCompletionBatchUi.PickMultiple(
                "CE Tools - Select Corridors",
                "Select one or more corridors. The batch action is applied only to the selected corridors.",
                corridors);
            if (selected == null || selected.Count == 0) return;

            IList<string> profileStyles = FieldCompletionBatchUi.ReadStyleChoices(
                document.Database,
                civilDocument,
                "Profile Style",
                "<Use drawing default>");
            IList<string> slopeStyles = FieldCompletionBatchUi.ReadStyleChoices(
                document.Database,
                civilDocument,
                "Slope Pattern Style",
                "<Use current>");

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Multiple Corridor Control",
                "Set rebuild behavior, create/apply an output layer, apply a profile style, and assign cut/fill slope-pattern styles for both sides.");
            settings.AddChoice(
                "Action",
                "01 Display and rebuild",
                "Action",
                "Rebuild only",
                "Choose whether to rebuild, switch automatic rebuilding, or do both.",
                new[]
                {
                    "Rebuild only",
                    "Automatic rebuild On",
                    "Automatic rebuild Off",
                    "Rebuild + automatic rebuild On",
                    "Rebuild + automatic rebuild Off"
                });
            settings.AddText(
                "Layer",
                "02 Output",
                "Corridor layer",
                "<Keep current>",
                "Enter a layer name to create/use and assign to every selected corridor. Keep current leaves layers unchanged.");
            settings.AddChoice(
                "ProfileStyle",
                "03 Profile style",
                "Design profile style",
                profileStyles[0],
                "Apply the selected Civil 3D profile style to non-ground profiles on every selected corridor baseline.",
                profileStyles);
            settings.AddChoice(
                "LeftCut",
                "04 Slope patterns",
                "Left cut style",
                slopeStyles[0],
                "Style for the left-side cut slope-pattern condition. Use current leaves it unchanged.",
                slopeStyles);
            settings.AddChoice(
                "LeftFill",
                "04 Slope patterns",
                "Left fill style",
                slopeStyles[0],
                "Style for the left-side fill slope-pattern condition. Use current leaves it unchanged.",
                slopeStyles);
            settings.AddChoice(
                "RightCut",
                "04 Slope patterns",
                "Right cut style",
                slopeStyles[0],
                "Style for the right-side cut slope-pattern condition. Use current leaves it unchanged.",
                slopeStyles);
            settings.AddChoice(
                "RightFill",
                "04 Slope patterns",
                "Right fill style",
                slopeStyles[0],
                "Style for the right-side fill slope-pattern condition. Use current leaves it unchanged.",
                slopeStyles);
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            string action = settings.Text("Action");
            bool rebuild = action.StartsWith("Rebuild", StringComparison.OrdinalIgnoreCase);
            bool setAutomatic = action.IndexOf("automatic rebuild", StringComparison.OrdinalIgnoreCase) >= 0;
            bool automatic = action.EndsWith("On", StringComparison.OrdinalIgnoreCase);
            string layerName = settings.Text("Layer");
            int changed = 0;
            int rebuilt = 0;
            int profileStylesApplied = 0;
            int slopeStylesApplied = 0;
            int failed = 0;

            using (DocumentLock lockDocument = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId layerId = ObjectId.Null;
                if (!string.IsNullOrWhiteSpace(layerName) &&
                    !string.Equals(layerName, "<Keep current>", StringComparison.OrdinalIgnoreCase))
                {
                    layerId = FieldCompletionBatchUi.GetOrCreateLayer(
                        document.Database,
                        transaction,
                        layerName.Trim());
                }

                ObjectId profileStyleId = FieldCompletionBatchUi.ResolveStyleId(
                    document.Database,
                    civilDocument,
                    "Profile Style",
                    settings.Text("ProfileStyle"),
                    transaction);

                Dictionary<string, ObjectId> slopeStyleIds = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                slopeStyleIds["LeftCut"] = FieldCompletionBatchUi.ResolveStyleId(document.Database, civilDocument, "Slope Pattern Style", settings.Text("LeftCut"), transaction);
                slopeStyleIds["LeftFill"] = FieldCompletionBatchUi.ResolveStyleId(document.Database, civilDocument, "Slope Pattern Style", settings.Text("LeftFill"), transaction);
                slopeStyleIds["RightCut"] = FieldCompletionBatchUi.ResolveStyleId(document.Database, civilDocument, "Slope Pattern Style", settings.Text("RightCut"), transaction);
                slopeStyleIds["RightFill"] = FieldCompletionBatchUi.ResolveStyleId(document.Database, civilDocument, "Slope Pattern Style", settings.Text("RightFill"), transaction);

                foreach (CivilChoice choice in selected)
                {
                    DBObject corridor = null;
                    try { corridor = transaction.GetObject(choice.Id, OpenMode.ForWrite, false); }
                    catch { }
                    if (corridor == null ||
                        corridor.GetType().Name.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        failed++;
                        continue;
                    }

                    bool localChanged = false;
                    if (setAutomatic &&
                        FieldCompletionBatchUi.TrySetBoolean(
                            corridor,
                            automatic,
                            "RebuildAutomatic",
                            "AutomaticRebuild"))
                    {
                        localChanged = true;
                    }
                    if (!layerId.IsNull &&
                        FieldCompletionBatchUi.TrySetObjectId(corridor, layerId, "LayerId"))
                    {
                        localChanged = true;
                    }

                    profileStylesApplied += ApplyProfileStyle(
                        corridor,
                        profileStyleId,
                        transaction);
                    slopeStylesApplied += ApplySlopeStyles(
                        corridor,
                        slopeStyleIds,
                        settings,
                        transaction);

                    if (rebuild && FieldCompletionBatchUi.TryInvokeNoArguments(corridor, "Rebuild"))
                    {
                        rebuilt++;
                        localChanged = true;
                    }

                    if (localChanged ||
                        profileStylesApplied > 0 ||
                        slopeStylesApplied > 0)
                    {
                        changed++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_CORRIDORBATCHCONTROL complete. Selected={0}; changed={1}; rebuilt={2}; profile styles={3}; slope styles={4}; unsupported/failed={5}.",
                selected.Count, changed, rebuilt, profileStylesApplied, slopeStylesApplied, failed);
        }

        private static int ApplyProfileStyle(
            DBObject corridor,
            ObjectId styleId,
            Transaction transaction)
        {
            if (corridor == null || styleId.IsNull) return 0;
            int changed = 0;
            object baselines = FieldCompletionBatchUi.ReadProperty(corridor, "Baselines");
            foreach (object baseline in CivilStyleDiscovery.Enumerate(baselines))
            {
                ObjectId alignmentId = FieldCompletionBatchUi.ReadObjectId(baseline, "AlignmentId");
                if (alignmentId.IsNull)
                    alignmentId = FieldCompletionBatchUi.ReadObjectId(baseline, "AlignmentObjectId");
                if (alignmentId.IsNull) continue;

                CivilAlignment alignment = null;
                try { alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment; }
                catch { }
                if (alignment == null) continue;

                foreach (ObjectId profileId in alignment.GetProfileIds())
                {
                    CivilProfile profile = null;
                    try { profile = transaction.GetObject(profileId, OpenMode.ForWrite, false) as CivilProfile; }
                    catch { }
                    if (profile == null) continue;
                    string identity = ((profile.Name ?? string.Empty) + " " +
                        (profile.Description ?? string.Empty)).ToUpperInvariant();
                    if (identity.Contains("NGL") ||
                        identity.Contains("EG") ||
                        identity.Contains("EXIST") ||
                        identity.Contains("GROUND"))
                        continue;
                    if (FieldCompletionBatchUi.TrySetObjectId(profile, styleId, "StyleId", "ProfileStyleId"))
                        changed++;
                }
            }
            return changed;
        }

        private static int ApplySlopeStyles(
            DBObject corridor,
            IDictionary<string, ObjectId> styleIds,
            ProductionSettingsDialogModel settings,
            Transaction transaction)
        {
            if (corridor == null || styleIds == null) return 0;
            object patterns = FieldCompletionBatchUi.ReadProperty(corridor, "SlopePatterns");
            List<object> values = CivilStyleDiscovery.Enumerate(patterns).Where(item => item != null).ToList();
            if (values.Count == 0) return 0;

            int changed = 0;
            foreach (object pattern in values)
            {
                string identity = FieldCompletionBatchUi.Identity(pattern);
                string key = FieldCompletionBatchUi.SlopeStyleKey(identity);
                if (string.IsNullOrWhiteSpace(key))
                {
                    string[] requested = { settings.Text("LeftCut"), settings.Text("LeftFill"), settings.Text("RightCut"), settings.Text("RightFill") };
                    if (requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                        key = "LeftCut";
                }
                if (string.IsNullOrWhiteSpace(key)) continue;

                ObjectId styleId;
                if (!styleIds.TryGetValue(key, out styleId) || styleId.IsNull) continue;
                string styleName = settings.Text(key);
                if (FieldCompletionBatchUi.TrySetObjectId(
                        pattern,
                        styleId,
                        "StyleId",
                        "SlopePatternStyleId",
                        "SlopeStyleId") ||
                    FieldCompletionBatchUi.TrySetString(
                        pattern,
                        styleName,
                        "StyleName",
                        "SlopePatternStyleName",
                        "SlopeStyleName"))
                {
                    changed++;
                }
                FieldCompletionBatchUi.TrySetBoolean(pattern, true, "Visible", "IsVisible", "Enabled");
                FieldCompletionBatchUi.TryInvokeNoArguments(pattern, "Rebuild");
            }
            return changed;
        }

        private static int MoveProfilePvis(CivilProfile profile, double delta)
        {
            object collection = FieldCompletionBatchUi.ReadProperty(profile, "PVIs");
            if (collection == null) return 0;
            int changed = 0;
            foreach (object pvi in CivilStyleDiscovery.Enumerate(collection))
            {
                if (pvi == null) continue;
                PropertyInfo elevation = pvi.GetType().GetProperty(
                    "Elevation",
                    BindingFlags.Public | BindingFlags.Instance);
                if (elevation == null || !elevation.CanRead || !elevation.CanWrite) continue;
                try
                {
                    double oldValue = Convert.ToDouble(elevation.GetValue(pvi, null), CultureInfo.InvariantCulture);
                    elevation.SetValue(pvi, oldValue + delta, null);
                    changed++;
                }
                catch { }
            }
            return changed;
        }

        private static PromptSelectionResult SelectImpliedOrPrompt(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK &&
                implied.Value != null &&
                implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
        }

        private static bool TryInvokeNoArguments(object target, string name)
        {
            return FieldCompletionBatchUi.TryInvokeNoArguments(target, name);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal static class FieldCompletionBatchUi
    {
        internal static List<CivilChoice> ReadSurfaceChoices(
            Document document,
            CivilDocument civilDocument)
        {
            var result = new List<CivilChoice>();
            if (document == null || civilDocument == null) return result;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civilDocument.GetSurfaceIds())
                {
                    CivilSurface surface = null;
                    try { surface = transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface; }
                    catch { }
                    if (surface != null)
                        result.Add(new CivilChoice(id, surface.Name));
                }
            }
            return result
                .GroupBy(item => item.Id)
                .Select(group => group.First())
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        internal static List<CivilChoice> ReadCorridorChoices(
            Document document,
            CivilDocument civilDocument)
        {
            var result = new Dictionary<ObjectId, string>();
            if (document == null || civilDocument == null) return new List<CivilChoice>();

            object collection = ReadProperty(civilDocument, "CorridorCollection");
            foreach (object item in CivilStyleDiscovery.Enumerate(collection))
            {
                ObjectId id = item is ObjectId
                    ? (ObjectId)item
                    : item is DBObject ? ((DBObject)item).ObjectId : ObjectId.Null;
                if (id.IsNull || id.IsErased) continue;
                try
                {
                    using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                    {
                        DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                        if (value != null &&
                            value.GetType().Name.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            string name = Convert.ToString(ReadProperty(value, "Name"), CultureInfo.CurrentCulture);
                            if (!string.IsNullOrWhiteSpace(name)) result[id] = name;
                        }
                    }
                }
                catch { }
            }

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord model = transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (model != null)
                    {
                        foreach (ObjectId id in model)
                        {
                            string className = id.ObjectClass == null ? string.Empty : id.ObjectClass.Name;
                            if (className.IndexOf("CORRIDOR", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                            string name = Convert.ToString(ReadProperty(value, "Name"), CultureInfo.CurrentCulture);
                            if (!string.IsNullOrWhiteSpace(name)) result[id] = name;
                        }
                    }
                }
            }
            catch { }

            return result
                .Select(pair => new CivilChoice(pair.Key, pair.Value))
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        internal static IList<CivilChoice> PickMultiple(
            string title,
            string message,
            IEnumerable<CivilChoice> choices)
        {
            var window = new FieldCompletionMultiChoiceWindow(title, message, choices);
            AcApplication.ShowModalWindow(window);
            return window.Accepted ? window.Selected : null;
        }

        internal static IList<string> ReadStyleChoices(
            Database database,
            CivilDocument civilDocument,
            string category,
            string emptyChoice)
        {
            var result = new List<string>();
            try
            {
                result.AddRange(CivilStyleCatalogV2.ReadNames(database, civilDocument, category));
            }
            catch { }
            result = result
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (result.Count == 0 || !string.Equals(result[0], emptyChoice, StringComparison.OrdinalIgnoreCase))
                result.Insert(0, emptyChoice);
            return result;
        }

        internal static ObjectId ResolveStyleId(
            Database database,
            CivilDocument civilDocument,
            string category,
            string requested,
            Transaction transaction)
        {
            if (string.IsNullOrWhiteSpace(requested) ||
                requested.StartsWith("<", StringComparison.OrdinalIgnoreCase))
                return ObjectId.Null;
            try
            {
                string actual;
                return CivilStyleCatalogV2.ResolveStyleId(
                    database, civilDocument, category, requested, transaction, out actual);
            }
            catch { return ObjectId.Null; }
        }

        internal static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string name)
        {
            LayerTable layers = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (layers == null || string.IsNullOrWhiteSpace(name)) return ObjectId.Null;
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        internal static object ReadProperty(object target, string name)
        {
            if (target == null) return null;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);
                return property == null || property.GetGetMethod() == null
                    ? null
                    : property.GetValue(target, null);
            }
            catch { return null; }
        }

        internal static ObjectId ReadObjectId(object target, string name)
        {
            object value = ReadProperty(target, name);
            return value is ObjectId ? (ObjectId)value : ObjectId.Null;
        }

        internal static bool TrySetObjectId(object target, ObjectId value, params string[] names)
        {
            if (target == null || value.IsNull) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property != null && property.CanWrite &&
                        property.PropertyType == typeof(ObjectId))
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        internal static bool TrySetString(object target, string value, params string[] names)
        {
            if (target == null || string.IsNullOrWhiteSpace(value) ||
                value.StartsWith("<", StringComparison.OrdinalIgnoreCase))
                return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property != null && property.CanWrite &&
                        property.PropertyType == typeof(string))
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        internal static bool TrySetBoolean(object target, bool value, params string[] names)
        {
            if (target == null) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(
                        name,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property != null && property.CanWrite &&
                        property.PropertyType == typeof(bool))
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        internal static bool TryInvokeNoArguments(object target, string name)
        {
            if (target == null) return false;
            foreach (MethodInfo method in target.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance)
                .Where(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    item.GetParameters().Length == 0))
            {
                try
                {
                    method.Invoke(target, null);
                    return true;
                }
                catch { }
            }
            return false;
        }

        internal static string Identity(object value)
        {
            if (value == null) return string.Empty;
            var pieces = new List<string>();
            foreach (string name in new[] { "Name", "Description", "Side", "SlopeType", "Condition", "Type" })
            {
                object item = ReadProperty(value, name);
                if (item != null) pieces.Add(Convert.ToString(item, CultureInfo.CurrentCulture));
            }
            return (value.GetType().Name + " " + string.Join(" ", pieces)).ToUpperInvariant();
        }

        internal static string SlopeStyleKey(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity)) return string.Empty;
            bool left = identity.Contains("LEFT") || identity.Contains("LHS");
            bool right = identity.Contains("RIGHT") || identity.Contains("RHS");
            bool cut = identity.Contains("CUT") || identity.Contains("EXCAV");
            bool fill = identity.Contains("FILL") || identity.Contains("EMBANK");
            if (left && cut) return "LeftCut";
            if (left && fill) return "LeftFill";
            if (right && cut) return "RightCut";
            if (right && fill) return "RightFill";
            return string.Empty;
        }

    }

    internal sealed class FieldCompletionMultiChoiceWindow : System.Windows.Window
    {
        private readonly System.Windows.Controls.ListBox _list;

        internal FieldCompletionMultiChoiceWindow(
            string title,
            string message,
            IEnumerable<CivilChoice> choices)
        {
            Title = title;
            Width = 700;
            Height = 560;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;

            var root = new System.Windows.Controls.DockPanel
            {
                Margin = new System.Windows.Thickness(16)
            };
            Content = root;

            var buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new System.Windows.Thickness(0, 10, 0, 0)
            };
            System.Windows.Controls.DockPanel.SetDock(
                buttons,
                System.Windows.Controls.Dock.Bottom);
            root.Children.Add(buttons);

            var selectAll = new System.Windows.Controls.Button
            {
                Content = "Select all",
                MinWidth = 90,
                Padding = new System.Windows.Thickness(10, 5, 10, 5)
            };
            selectAll.Click += delegate
            {
                _list.SelectAll();
            };
            buttons.Children.Add(selectAll);

            var ok = new System.Windows.Controls.Button
            {
                Content = "Continue",
                MinWidth = 100,
                Padding = new System.Windows.Thickness(10, 5, 10, 5),
                IsDefault = true,
                Margin = new System.Windows.Thickness(8, 0, 0, 0)
            };
            ok.Click += delegate
            {
                Selected = _list.SelectedItems.Cast<CivilChoice>().ToList();
                if (Selected.Count > 0)
                {
                    Accepted = true;
                    DialogResult = true;
                }
            };
            buttons.Children.Add(ok);

            var cancel = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                MinWidth = 90,
                Padding = new System.Windows.Thickness(10, 5, 10, 5),
                IsCancel = true,
                Margin = new System.Windows.Thickness(8, 0, 0, 0)
            };
            buttons.Children.Add(cancel);

            var heading = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 10)
            };
            System.Windows.Controls.DockPanel.SetDock(
                heading,
                System.Windows.Controls.Dock.Top);
            root.Children.Add(heading);

            _list = new System.Windows.Controls.ListBox
            {
                ItemsSource = choices == null
                    ? new List<CivilChoice>()
                    : choices.ToList(),
                DisplayMemberPath = "Name",
                SelectionMode = System.Windows.Controls.SelectionMode.Extended
            };
            root.Children.Add(_list);
        }

        internal bool Accepted { get; private set; }
        internal IList<CivilChoice> Selected { get; private set; } = new List<CivilChoice>();
    }
}