using System;
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
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;

[assembly: CommandClass(typeof(CETools.Civil3D.September14FeatureLineCompletionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Focused completion front doors for the remaining feature-line field comments.
    /// Existing CE_FLRELLINKEXISTING remains registered in the August 24 field pack
    /// and is surfaced through CE_FIELDCOMPLETION. New stepped offsets are delegated
    /// to the August 21 candidate-first fatal-safety boundary, and direct surface
    /// links are delegated to the August 23 dynamic drape implementation.
    /// </summary>
    public sealed class September14FeatureLineCompletionCommands
    {
        [CommandMethod(
            "CE_TOOLS",
            "CE_PROFILELABELSETMULTI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ApplyProfileLabelSetToMultipleFinalProfiles()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            IList<string> names = CivilStyleCatalogV2.ReadNames(
                document.Database,
                civilDocument,
                "Profile Label Set Style");
            if (names.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROFILELABELSETMULTI cancelled. No Profile Label Set Style exists in the drawing.");
                return;
            }

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Profile Label Sets",
                "Apply one installed Profile Label Set Style to selected final/design road profiles. Existing-ground/NGL/EG profiles are skipped.");
            settings.AddChoice(
                "Style",
                "01 Style",
                "Profile label set",
                names[0],
                "Choose the Civil 3D profile label set to assign.",
                names.ToArray());
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            List<ObjectId> ids = SelectCompletionObjects(
                document,
                "\nSelect final/design profiles to receive the label set: ");
            if (ids.Count == 0) return;

            int applied = 0;
            int skipped = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                string actualName;
                ObjectId styleId = CivilStyleCatalogV2.ResolveStyleId(
                    document.Database,
                    civilDocument,
                    "Profile Label Set Style",
                    settings.Text("Style"),
                    transaction,
                    out actualName);

                foreach (ObjectId id in ids)
                {
                    try
                    {
                        CivilProfile profile = transaction.GetObject(
                            id,
                            OpenMode.ForWrite,
                            false) as CivilProfile;
                        if (profile == null || !IsFinalDesignProfile(profile))
                        {
                            skipped++;
                            continue;
                        }

                        bool changed =
                            TrySetObjectId(profile, "LabelSetId", styleId) ||
                            TrySetObjectId(profile, "ProfileLabelSetStyleId", styleId) ||
                            TryInvoke(profile, "ImportLabelSet", styleId);
                        if (!changed)
                        {
                            skipped++;
                            continue;
                        }
                        TryInvoke(profile, "Rebuild");
                        TryInvoke(profile, "Update");
                        Entity entity = profile as Entity;
                        if (entity != null) entity.RecordGraphicsModified(true);
                        applied++;
                    }
                    catch
                    {
                        skipped++;
                    }
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PROFILELABELSETMULTI complete. Label set='{0}'; final/design profiles updated={1}; skipped={2}.",
                settings.Text("Style"),
                applied,
                skipped);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PROFILEVIEWSTYLEMULTI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ApplyProfileViewStyleToMultipleViews()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            IList<string> names = CivilStyleCatalogV2.ReadNames(
                document.Database,
                civilDocument,
                "Profile View Style");
            if (names.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROFILEVIEWSTYLEMULTI cancelled. No Profile View Style exists in the drawing.");
                return;
            }

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Profile View Styles",
                "Apply one installed Civil 3D Profile View Style to multiple selected profile views and refresh their graphics.");
            settings.AddChoice(
                "Style",
                "01 Style",
                "Profile view style",
                names[0],
                "Choose the Civil 3D profile view style to assign.",
                names.ToArray());
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            List<ObjectId> ids = SelectCompletionObjects(
                document,
                "\nSelect profile views to receive the style: ");
            if (ids.Count == 0) return;

            int applied = 0;
            int skipped = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                string actualName;
                ObjectId styleId = CivilStyleCatalogV2.ResolveStyleId(
                    document.Database,
                    civilDocument,
                    "Profile View Style",
                    settings.Text("Style"),
                    transaction,
                    out actualName);

                foreach (ObjectId id in ids)
                {
                    try
                    {
                        ProfileView view = transaction.GetObject(
                            id,
                            OpenMode.ForWrite,
                            false) as ProfileView;
                        if (view == null)
                        {
                            skipped++;
                            continue;
                        }

                        bool changed =
                            TrySetObjectId(view, "StyleId", styleId) ||
                            TrySetObjectId(view, "ProfileViewStyleId", styleId);
                        if (!changed)
                        {
                            skipped++;
                            continue;
                        }
                        TryInvoke(view, "Rebuild");
                        TryInvoke(view, "Update");
                        TryInvoke(view, "UpdateDisplay");
                        view.RecordGraphicsModified(true);
                        applied++;
                    }
                    catch
                    {
                        skipped++;
                    }
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PROFILEVIEWSTYLEMULTI complete. Profile-view style='{0}'; views updated={1}; skipped={2}.",
                settings.Text("Style"),
                applied,
                skipped);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLAPPEARANCE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ApplyFeatureLineAppearanceAndSite()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            List<string> siteChoices = ReadSiteChoices(document, civilDocument);
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Feature Line Colour and Site",
                "Set an explicit entity colour, optional output layer and Civil 3D Site for multiple feature lines. A new site name is created and assigned in the same transaction.");
            settings.AddPositiveInteger(
                "Color",
                "01 Appearance",
                "AutoCAD colour index (1-255)",
                7,
                "The explicit entity colour is written so the layout reflects the Properties-panel value.");
            settings.AddText(
                "Layer",
                "01 Appearance",
                "Feature-line layer (optional)",
                string.Empty,
                "Leave blank to keep each feature line's current layer.");
            settings.AddChoice(
                "Site",
                "02 Site",
                "Civil 3D site",
                siteChoices[0],
                "Choose an existing site or leave the feature lines site-less.",
                siteChoices.ToArray());
            settings.AddText(
                "NewSite",
                "02 Site",
                "New site name (optional)",
                string.Empty,
                "If entered, this name takes precedence, is created if necessary and is assigned to every selected feature line.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            List<ObjectId> ids = SelectCompletionObjects(
                document,
                "\nSelect feature lines for colour/layer/site assignment: ");
            if (ids.Count == 0) return;

            int colorIndex = Math.Max(1, Math.Min(255, settings.Integer("Color", 7)));
            int applied = 0;
            int skipped = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId siteId = ResolveSite(
                    civilDocument,
                    transaction,
                    settings.Text("Site"),
                    settings.Text("NewSite"));
                ObjectId layerId = ObjectId.Null;
                if (!string.IsNullOrWhiteSpace(settings.Text("Layer")))
                    layerId = GetOrCreateLayer(
                        document.Database,
                        transaction,
                        settings.Text("Layer").Trim());

                foreach (ObjectId id in ids)
                {
                    try
                    {
                        CivilFeatureLine line = transaction.GetObject(
                            id,
                            OpenMode.ForWrite,
                            false) as CivilFeatureLine;
                        if (line == null)
                        {
                            skipped++;
                            continue;
                        }

                        bool changed = ApplyFeatureLineColor(line, colorIndex);
                        if (!layerId.IsNull)
                        {
                            line.LayerId = layerId;
                            changed = true;
                        }
                        if (!siteId.IsNull ||
                            settings.Text("Site").IndexOf("sitel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            !string.IsNullOrWhiteSpace(settings.Text("NewSite")))
                        {
                            changed = TrySetObjectId(line, "SiteId", siteId) || changed;
                        }
                        line.RecordGraphicsModified(true);
                        if (changed) applied++; else skipped++;
                    }
                    catch
                    {
                        skipped++;
                    }
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_FLAPPEARANCE complete. Feature lines updated={0}; skipped={1}; colour index={2}; site='{3}'; layer='{4}'.",
                applied,
                skipped,
                colorIndex,
                string.IsNullOrWhiteSpace(settings.Text("NewSite"))
                    ? settings.Text("Site")
                    : settings.Text("NewSite").Trim(),
                string.IsNullOrWhiteSpace(settings.Text("Layer"))
                    ? "<unchanged>"
                    : settings.Text("Layer").Trim());
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLSTEPSSAFE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateSafeSteppedOffsets()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Safe Multiple Feature-Line Steps",
                "Create stepped offsets from multiple source feature lines. Each candidate is committed and verified before it becomes a linked CE_FLREL child.");
            settings.AddPositiveDouble("Horizontal", "Steps", "Horizontal step", 1.0, "Horizontal offset per step.");
            settings.AddText("Vertical", "Steps", "Vertical step", "-0.500", "Signed elevation difference per step.");
            settings.AddPositiveInteger("Count", "Steps", "Step count", 1, "Number of linked children per selected source.");
            settings.AddText("Suffix", "Naming", "Child suffix", "STEP", "Suffix used for generated feature-line names.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            double vertical;
            if (!ProductionSettingsDialogModel.TryDouble(settings.Text("Vertical"), out vertical))
            {
                document.Editor.WriteMessage("\nCE_FLSTEPSSAFE cancelled. Vertical step must be a number.");
                return;
            }

            var selectionOptions = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect multiple source feature lines for stepped offsets: "
            };
            PromptSelectionResult selection = document.Editor.GetSelection(selectionOptions);
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            PlatformDynamicRefreshManager.EnsureInitialized();
            August21PlatformRelativeFatalSafety.PlatformStepResult result =
                August21PlatformRelativeFatalSafety.CreatePlatformSteps(
                    document,
                    selection.Value.GetObjectIds().Distinct(),
                    Math.Max(0.001, settings.Double("Horizontal", 1.0)),
                    vertical,
                    Math.Max(1, settings.Integer("Count", 1)),
                    string.IsNullOrWhiteSpace(settings.Text("Suffix")) ? "STEP" : settings.Text("Suffix").Trim());

            if (result.Created > 0) PlatformDynamicRefreshManager.Queue();
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_FLSTEPSSAFE complete. Linked steps={0}; skipped={1}. Existing source feature lines were kept.",
                result.Created,
                result.Skipped);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLSURFACELINKEXISTING",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void LinkExistingFeatureLinesToSurface()
        {
            // Reuse the established August 23 multi-feature-line dynamic drape. It
            // samples through August21SurfaceSafety and stores the persistent direct
            // drape link without rebuilding/deleting the selected source feature line.
            new August23PlatformDynamicGradingCommands().DrapeMultipleFeatureLines();
        }
        private static List<ObjectId> SelectCompletionObjects(
            Document document,
            string prompt)
        {
            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
            {
                selection = document.Editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = prompt,
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
            }
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            return selection.Status == PromptStatus.OK && selection.Value != null
                ? selection.Value.GetObjectIds().Distinct().ToList()
                : new List<ObjectId>();
        }

        private static bool IsFinalDesignProfile(CivilProfile profile)
        {
            string identity = ((profile.Name ?? string.Empty) + " " +
                (profile.Description ?? string.Empty)).ToUpperInvariant();
            return !IsGroundProfile(identity) &&
                (identity.Contains("FINAL") ||
                 identity.Contains("DESIGN") ||
                 identity.Contains("PROPOSED") ||
                 identity.Contains("ROAD") ||
                 ContainsProfileToken(identity, "FG"));
        }

        private static bool IsGroundProfile(string identity)
        {
            string value = identity ?? string.Empty;
            return value.Contains("NGL") ||
                   value.Contains("NATURAL") ||
                   value.Contains("EXIST") ||
                   value.Contains("GROUND") ||
                   value.Contains("SURFACE") ||
                   ContainsProfileToken(value, "EG");
        }

        private static bool ContainsProfileToken(string identity, string token)
        {
            string value = (identity ?? string.Empty)
                .Replace("-", " ")
                .Replace("_", " ")
                .Replace("/", " ")
                .Replace(".", " ");
            return (" " + value + " ").IndexOf(
                " " + (token ?? string.Empty).Trim() + " ",
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<string> ReadSiteChoices(
            Document document,
            CivilDocument civilDocument)
        {
            var result = new List<string> { "<Sitelss> - Do not assign to a Civil 3D site" };
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civilDocument.GetSiteIds())
                {
                    try
                    {
                        Site site = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Site;
                        if (site != null && !string.IsNullOrWhiteSpace(site.Name))
                            result.Add(site.Name);
                    }
                    catch { }
                }
            }
            return result.Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static ObjectId ResolveSite(
            CivilDocument civilDocument,
            Transaction transaction,
            string selected,
            string newName)
        {
            string requested = string.IsNullOrWhiteSpace(newName)
                ? selected
                : newName.Trim();
            if (string.IsNullOrWhiteSpace(requested) ||
                requested.IndexOf("sitel", StringComparison.OrdinalIgnoreCase) >= 0)
                return ObjectId.Null;

            foreach (ObjectId id in civilDocument.GetSiteIds())
            {
                try
                {
                    Site site = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as Site;
                    if (site != null &&
                        string.Equals(site.Name, requested, StringComparison.OrdinalIgnoreCase))
                        return id;
                }
                catch { }
            }

            try
            {
                MethodInfo create = typeof(Site).GetMethod(
                    "Create",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(CivilDocument), typeof(string) },
                    null);
                if (create == null) return ObjectId.Null;
                object result = create.Invoke(
                    null,
                    new object[] { civilDocument, requested });
                if (result is ObjectId) return (ObjectId)result;
                Site created = result as Site;
                return created == null ? ObjectId.Null : created.ObjectId;
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        private static bool ApplyFeatureLineColor(
            CivilFeatureLine line,
            int colorIndex)
        {
            if (line == null) return false;
            bool changed = false;
            try
            {
                line.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci,
                    (short)colorIndex);
                changed = true;
            }
            catch { }

            try
            {
                PropertyInfo property = line.GetType().GetProperty(
                    "ColorIndex",
                    BindingFlags.Public | BindingFlags.Instance);
                if (property != null && property.CanWrite)
                {
                    object value = property.PropertyType == typeof(short)
                        ? (object)(short)colorIndex
                        : property.PropertyType == typeof(int)
                            ? (object)colorIndex
                            : Convert.ChangeType(
                                colorIndex,
                                property.PropertyType,
                                CultureInfo.InvariantCulture);
                    property.SetValue(line, value, null);
                    changed = true;
                }
            }
            catch { }
            return changed;
        }

        private static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string name)
        {
            LayerTable table = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (table == null) return ObjectId.Null;
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            LayerTableRecord layer = new LayerTableRecord { Name = name };
            ObjectId id = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static bool TrySetObjectId(
            object target,
            string propertyName,
            ObjectId value)
        {
            if (target == null) return false;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (property == null ||
                    !property.CanWrite ||
                    property.PropertyType != typeof(ObjectId))
                    return false;
                property.SetValue(target, value, null);
                return true;
            }
            catch { return false; }
        }

        private static bool TryInvoke(
            object target,
            string methodName,
            params object[] arguments)
        {
            if (target == null) return false;
            foreach (MethodInfo method in target.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(
                        method.Name,
                        methodName,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != (arguments == null ? 0 : arguments.Length))
                    continue;
                bool compatible = true;
                object[] converted = arguments == null
                    ? new object[0]
                    : (object[])arguments.Clone();
                for (int index = 0; index < parameters.Length; index++)
                {
                    object argument = converted[index];
                    if (argument == null)
                    {
                        if (parameters[index].ParameterType.IsValueType)
                        {
                            compatible = false;
                            break;
                        }
                    }
                    else if (!parameters[index].ParameterType.IsAssignableFrom(argument.GetType()))
                    {
                        try
                        {
                            converted[index] = Convert.ChangeType(
                                argument,
                                parameters[index].ParameterType,
                                CultureInfo.InvariantCulture);
                        }
                        catch
                        {
                            compatible = false;
                            break;
                        }
                    }
                }
                if (!compatible) continue;
                try
                {
                    method.Invoke(target, converted);
                    return true;
                }
                catch { }
            }
            return false;
        }

    }
}
