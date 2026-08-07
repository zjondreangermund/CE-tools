using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.RoadCorridorCompletionCommands))]

namespace CETools.Civil3D
{
    public sealed class RoadCorridorCompletionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ROADPROFILEFULL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateFullProfiles()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            document.SendStringToExecute("CE_ROADPROFILES CE_ROADDESIGNPROFILE ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADDESIGNPROFILE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateDesignProfiles()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Final Road Design Profiles",
                "Create one final/layout profile for every CE road alignment from the current NGL profile. The profile is added to existing profile views and band data.");
            model.AddDouble("Offset", "01 Vertical Design", "Initial elevation above NGL", 0.15, "Starting design level above the current NGL profile.");
            model.AddDouble("MinGrade", "01 Vertical Design", "Minimum grade (%)", 0.5, "Absolute minimum grade used between sampled PVIs.");
            model.AddDouble("MaxGrade", "01 Vertical Design", "Maximum grade (%)", 8.0, "Absolute maximum grade used between sampled PVIs.");
            model.AddPositiveInteger("Intervals", "02 Sampling", "Design intervals", 8, "Number of equal station intervals used to seed editable design PVIs.");
            model.AddText("Suffix", "03 Naming", "Design profile suffix", "FG", "Final profiles are named Road-FG by default.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            double offset = model.Double("Offset", 0.15);
            double minimumGrade = Math.Abs(model.Double("MinGrade", 0.5)) / 100.0;
            double maximumGrade = Math.Max(Math.Abs(model.Double("MaxGrade", 8.0)) / 100.0, minimumGrade);
            int intervals = Math.Max(model.Integer("Intervals", 8), 2);
            string suffix = string.IsNullOrWhiteSpace(model.Text("Suffix")) ? "FG" : model.Text("Suffix").Trim();
            ProjectStyleSelection project = ProjectStyleCenterCommands.ReadSelection(document.Database);
            RoadProductionSettings road = RoadProductionSettings.Read(document.Database);
            int created = 0;
            int viewsUpdated = 0;
            var rows = new List<IList<string>>();

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    string actualStyle;
                    ObjectId styleId = CivilStyleCatalogV2.ResolveStyleId(
                        document.Database, civilDocument, "Profile Style",
                        RoadStyle(road, project, "Profile Style"), transaction, out actualStyle);
                    string actualLabels;
                    ObjectId labelSetId = CivilStyleCatalogV2.ResolveStyleId(
                        document.Database, civilDocument, "Profile Label Set Style",
                        RoadStyle(road, project, "Profile Label Set Style"), transaction, out actualLabels);
                    ObjectId layerId = GetOrCreateLayer(document.Database, transaction,
                        string.IsNullOrWhiteSpace(road.ProfileLayer) ? "CE-ROAD-DESIGN-PROFILE" : road.ProfileLayer);

                    foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
                    {
                        CivilAlignment alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment;
                        if (alignment == null || !IsCeRoadAlignment(alignment)) continue;
                        CivilProfile ngl = FindNglProfile(alignment, transaction);
                        if (ngl == null) continue;
                        string name = UniqueProfileName(alignment, alignment.Name + "-" + suffix, transaction);
                        ObjectId profileId = CreateLayoutProfile(name, alignmentId, layerId, styleId, labelSetId);
                        CivilProfile design = transaction.GetObject(profileId, OpenMode.ForWrite, false) as CivilProfile;
                        if (design == null) continue;
                        AddDesignPvis(design, ngl, alignment, offset, minimumGrade, maximumGrade, intervals);
                        design.Description = string.Format(
                            CultureInfo.InvariantCulture,
                            "CE final road profile | NGL={0} | offset={1:R} | grade-range={2:R}-{3:R}",
                            ngl.Name, offset, minimumGrade, maximumGrade);
                        created++;
                        int bound = BindDesignToProfileViews(document.Database, alignmentId, ngl.ObjectId, profileId, transaction);
                        viewsUpdated += bound;
                        rows.Add(new List<string>
                        {
                            alignment.Name, ngl.Name, name,
                            offset.ToString("N3", CultureInfo.CurrentCulture),
                            (minimumGrade * 100.0).ToString("N2", CultureInfo.CurrentCulture),
                            (maximumGrade * 100.0).ToString("N2", CultureInfo.CurrentCulture),
                            bound.ToString(CultureInfo.CurrentCulture)
                        });
                    }
                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_ROADDESIGNPROFILE failed. {0}", exception.Message);
                return;
            }

            document.Editor.Regen();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Final Road Profiles",
                string.Format(CultureInfo.CurrentCulture, "Final profiles created={0}; profile views updated={1}.", created, viewsUpdated),
                new List<string> { "Road", "NGL", "Final Profile", "Offset", "Min Grade %", "Max Grade %", "Views" },
                rows,
                "CE TOOLS FINAL ROAD PROFILE REGISTER");
        }

        [CommandMethod("CE_TOOLS", "CE_ROADCORRIDORFULL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateFullCorridors()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            document.SendStringToExecute("CE_ROADCORRIDORS CE_ROADCORRIDORCOMPLETE ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADCORRIDORCOMPLETE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CompleteCorridors()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return;

            List<CivilChoice> surfaces = ReadSurfaces(document, civilDocument);
            if (surfaces.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_ROADCORRIDORCOMPLETE: no Civil 3D surface is available for corridor targets.");
                return;
            }
            var surfacePicker = new CivilChoiceWindow(
                "CE Tools - Corridor Target Surface",
                "Select the existing-ground or target surface used for width/elevation surface targets.",
                surfaces);
            AcApplication.ShowModalWindow(surfacePicker);
            if (!surfacePicker.Accepted || surfacePicker.Selected == null) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Complete Road Corridors",
                "Repair every CE road corridor and add complete production output: baselines, regions, assemblies, targets, TOP/DATUM surfaces, boundaries, slope patterns, styles and rebuild.");
            model.AddText("TopName", "01 Corridor Surfaces", "Top surface name", "CE-TOP", "Corridor top surface name.");
            model.AddText("BottomName", "01 Corridor Surfaces", "Bottom surface name", "CE-BOTTOM", "Corridor bottom/datum surface name.");
            List<string> assemblyNames = ReadAssemblyNames(document, civilDocument);
            model.AddChoice("Assembly", "00 Baseline and Region", "Assembly for missing corridor regions",
                assemblyNames.Count == 0 ? string.Empty : assemblyNames[0],
                "When a CE road corridor has no baseline/region, use this existing Civil 3D assembly to create a full-length region.",
                assemblyNames);
            model.AddText("TopCodes", "01 Corridor Surfaces", "Top link codes", "Top,Pave", "Comma-separated corridor link codes included in the top surface.");
            model.AddText("BottomCodes", "01 Corridor Surfaces", "Bottom link codes", "Datum,Subgrade", "Comma-separated corridor link codes included in the bottom surface.");
            model.AddChoice("Boundary", "02 Boundaries", "Automatic outer boundary", "Enabled", "Add a corridor-extents boundary to each generated corridor surface.", new[] { "Enabled", "Disabled" });
            model.AddChoice("Targets", "03 Targets", "Apply selected surface targets", "Enabled", "Assign the selected surface wherever a region exposes an ObjectId surface target.", new[] { "Enabled", "Disabled" });
            model.AddChoice("Slope", "04 Slope Patterns", "Create/refresh slope patterns", "Enabled", "Enable available corridor slope-pattern collections and rebuild them.", new[] { "Enabled", "Disabled" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            RoadCorridorCompletionOptions options = new RoadCorridorCompletionOptions
            {
                TargetSurfaceId = surfacePicker.Selected.Id,
                TopSurfaceName = SafeName(model.Text("TopName"), "CE-TOP"),
                BottomSurfaceName = SafeName(model.Text("BottomName"), "CE-BOTTOM"),
                AssemblyName = model.Text("Assembly"),
                TopCodes = SplitCodes(model.Text("TopCodes"), new[] { "Top", "Pave" }),
                BottomCodes = SplitCodes(model.Text("BottomCodes"), new[] { "Datum", "Subgrade" }),
                AddBoundary = string.Equals(model.Text("Boundary"), "Enabled", StringComparison.OrdinalIgnoreCase),
                ApplyTargets = string.Equals(model.Text("Targets"), "Enabled", StringComparison.OrdinalIgnoreCase),
                EnableSlopePatterns = string.Equals(model.Text("Slope"), "Enabled", StringComparison.OrdinalIgnoreCase)
            };

            RoadCorridorCompletionResult result = CompleteAll(document, civilDocument, options);
            document.Editor.Regen();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Road Corridor Completion",
                string.Format(CultureInfo.CurrentCulture,
                    "Corridors={0}; baselines={1}; regions={2}; targets={3}; surfaces={4}; boundaries={5}; slope patterns={6}; rebuilt={7}; warnings={8}.",
                    result.Corridors, result.Baselines, result.Regions, result.Targets,
                    result.Surfaces, result.Boundaries, result.SlopePatterns,
                    result.Rebuilt, result.Warnings),
                new List<string> { "Corridor", "Baselines", "Regions", "Targets", "Surfaces", "Boundaries", "Slope Patterns", "Status" },
                result.Rows,
                "CE TOOLS ROAD CORRIDOR COMPLETION REGISTER");
        }

        internal static RoadCorridorCompletionResult CompleteAll(
            Document document,
            CivilDocument civilDocument,
            RoadCorridorCompletionOptions options)
        {
            var result = new RoadCorridorCompletionResult();
            if (document == null || civilDocument == null || options == null) return result;
            object collection = ReadProperty(civilDocument, "CorridorCollection");
            IEnumerable values = collection as IEnumerable;
            if (values == null)
            {
                result.Warnings++;
                return result;
            }
            ProjectStyleSelection project = ProjectStyleCenterCommands.ReadSelection(document.Database);
            RoadProductionSettings road = RoadProductionSettings.Read(document.Database);

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                string corridorStyleName;
                ObjectId corridorStyleId = ResolveOptionalStyle(document.Database, civilDocument, road, project, "Corridor Style", transaction, out corridorStyleName);
                string codeSetName;
                ObjectId codeSetStyleId = ResolveOptionalStyle(document.Database, civilDocument, road, project, "Code Set Style", transaction, out codeSetName);

                foreach (object item in values)
                {
                    ObjectId id = item is ObjectId ? (ObjectId)item : item is DBObject ? ((DBObject)item).ObjectId : ObjectId.Null;
                    if (id.IsNull || id.IsErased) continue;
                    DBObject corridor;
                    try { corridor = transaction.GetObject(id, OpenMode.ForWrite, false); }
                    catch { result.Warnings++; continue; }
                    if (corridor == null || corridor.GetType().Name.IndexOf("Corridor", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string name = Convert.ToString(ReadProperty(corridor, "Name"), CultureInfo.CurrentCulture);
                    if (!IsCeCorridor(corridor, name)) continue;
                    result.Corridors++;
                    int beforeBaseline = result.Baselines;
                    int beforeRegion = result.Regions;
                    int beforeTarget = result.Targets;
                    int beforeSurface = result.Surfaces;
                    int beforeBoundary = result.Boundaries;
                    int beforeSlope = result.SlopePatterns;

                    if (!corridorStyleId.IsNull) TrySetObjectId(corridor, corridorStyleId, "StyleId", "CorridorStyleId");
                    if (!codeSetStyleId.IsNull) TrySetObjectId(corridor, codeSetStyleId, "CodeSetStyleId", "CodeSetStyle");
                    object baselines = ReadProperty(corridor, "Baselines");
                    if (!CivilStyleDiscovery.Enumerate(baselines).Any())
                    {
                        if (!TryCreateMissingBaselineAndRegion(
                                corridor, name, baselines, civilDocument, transaction, options, ref result))
                            result.Warnings++;
                    }
                    foreach (object baseline in CivilStyleDiscovery.Enumerate(baselines))
                    {
                        if (baseline == null) continue;
                        result.Baselines++;
                        object regions = ReadProperty(baseline, "BaselineRegions") ?? ReadProperty(baseline, "Regions");
                        foreach (object region in CivilStyleDiscovery.Enumerate(regions))
                        {
                            if (region == null) continue;
                            result.Regions++;
                            if (!codeSetStyleId.IsNull) TrySetObjectId(region, codeSetStyleId, "CodeSetStyleId", "CodeSetStyle");
                            if (options.ApplyTargets) result.Targets += ApplySurfaceTargets(region, options.TargetSurfaceId);
                            Invoke(region, "Rebuild");
                        }
                    }

                    object corridorSurfaces = ReadProperty(corridor, "CorridorSurfaces") ?? ReadProperty(corridor, "Surfaces");
                    object top = EnsureCorridorSurface(corridorSurfaces, options.TopSurfaceName, options.TopCodes, options.AddBoundary, ref result);
                    object bottom = EnsureCorridorSurface(corridorSurfaces, options.BottomSurfaceName, options.BottomCodes, options.AddBoundary, ref result);
                    if (top != null) Invoke(top, "Rebuild");
                    if (bottom != null) Invoke(bottom, "Rebuild");
                    if (options.EnableSlopePatterns) result.SlopePatterns += EnableSlopePatterns(corridor);
                    if (Invoke(corridor, "Rebuild")) result.Rebuilt++;
                    result.Rows.Add(new List<string>
                    {
                        name,
                        (result.Baselines - beforeBaseline).ToString(CultureInfo.CurrentCulture),
                        (result.Regions - beforeRegion).ToString(CultureInfo.CurrentCulture),
                        (result.Targets - beforeTarget).ToString(CultureInfo.CurrentCulture),
                        (result.Surfaces - beforeSurface).ToString(CultureInfo.CurrentCulture),
                        (result.Boundaries - beforeBoundary).ToString(CultureInfo.CurrentCulture),
                        (result.SlopePatterns - beforeSlope).ToString(CultureInfo.CurrentCulture),
                        "Rebuilt"
                    });
                }
                transaction.Commit();
            }
            return result;
        }

        private static ObjectId CreateLayoutProfile(string name, ObjectId alignmentId, ObjectId layerId, ObjectId styleId, ObjectId labelSetId)
        {
            foreach (MethodInfo method in typeof(CivilProfile).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(candidate => candidate.Name.IndexOf("CreateByLayout", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                ParameterInfo[] parameters = method.GetParameters();
                var args = new object[parameters.Length];
                int objectIndex = 0;
                bool supported = true;
                for (int index = 0; index < parameters.Length; index++)
                {
                    Type type = parameters[index].ParameterType;
                    string parameterName = parameters[index].Name ?? string.Empty;
                    if (type == typeof(string)) args[index] = name;
                    else if (type == typeof(ObjectId))
                    {
                        if (parameterName.IndexOf("alignment", StringComparison.OrdinalIgnoreCase) >= 0) args[index] = alignmentId;
                        else if (parameterName.IndexOf("layer", StringComparison.OrdinalIgnoreCase) >= 0) args[index] = layerId;
                        else if (parameterName.IndexOf("label", StringComparison.OrdinalIgnoreCase) >= 0) args[index] = labelSetId;
                        else if (parameterName.IndexOf("style", StringComparison.OrdinalIgnoreCase) >= 0) args[index] = styleId;
                        else args[index] = objectIndex++ == 0 ? alignmentId : ObjectId.Null;
                    }
                    else if (type == typeof(bool)) args[index] = false;
                    else { supported = false; break; }
                }
                if (!supported) continue;
                try
                {
                    object value = method.Invoke(null, args);
                    if (value is ObjectId && !((ObjectId)value).IsNull) return (ObjectId)value;
                }
                catch { }
            }
            throw new InvalidOperationException("Civil 3D did not expose a compatible Profile.CreateByLayout overload.");
        }

        private static void AddDesignPvis(CivilProfile design, CivilProfile ngl, CivilAlignment alignment, double offset, double minGrade, double maxGrade, int intervals)
        {
            object pvis = ReadProperty(design, "PVIs");
            if (pvis == null) throw new InvalidOperationException("The final profile PVI collection is unavailable.");
            MethodInfo add = pvis.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name.IndexOf("AddPVI", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    method.GetParameters().Length >= 2 &&
                    method.GetParameters()[0].ParameterType == typeof(double) &&
                    method.GetParameters()[1].ParameterType == typeof(double));
            if (add == null) throw new InvalidOperationException("The final profile PVI creation method is unavailable.");

            double start = alignment.StartingStation;
            double end = alignment.EndingStation;
            double previousStation = start;
            double previousElevation = ElevationAt(ngl, start) + offset;
            InvokeAddPvi(add, pvis, start, previousElevation);
            for (int index = 1; index <= intervals; index++)
            {
                double station = start + ((end - start) * index / intervals);
                double desired = ElevationAt(ngl, station) + offset;
                double distance = Math.Max(station - previousStation, 0.001);
                double grade = (desired - previousElevation) / distance;
                double sign = Math.Abs(grade) < 1e-12 ? (index % 2 == 0 ? 1.0 : -1.0) : Math.Sign(grade);
                double absolute = Math.Min(Math.Max(Math.Abs(grade), minGrade), maxGrade);
                double elevation = previousElevation + (sign * absolute * distance);
                InvokeAddPvi(add, pvis, station, elevation);
                previousStation = station;
                previousElevation = elevation;
            }
        }

        private static void InvokeAddPvi(MethodInfo method, object target, double station, double elevation)
        {
            ParameterInfo[] parameters = method.GetParameters();
            var args = new object[parameters.Length];
            args[0] = station;
            args[1] = elevation;
            for (int index = 2; index < parameters.Length; index++)
            {
                Type type = parameters[index].ParameterType;
                args[index] = type == typeof(double) ? 0.0 : type == typeof(bool) ? false : type.IsEnum ? Enum.GetValues(type).GetValue(0) : null;
            }
            method.Invoke(target, args);
        }

        private static double ElevationAt(CivilProfile profile, double station)
        {
            try { return profile.ElevationAt(station); }
            catch
            {
                MethodInfo method = profile.GetType().GetMethod("ElevationAt", new[] { typeof(double) });
                return method == null ? 0.0 : Convert.ToDouble(method.Invoke(profile, new object[] { station }), CultureInfo.InvariantCulture);
            }
        }

        private static CivilProfile FindNglProfile(CivilAlignment alignment, Transaction transaction)
        {
            foreach (ObjectId id in alignment.GetProfileIds())
            {
                CivilProfile profile = transaction.GetObject(id, OpenMode.ForRead, false) as CivilProfile;
                if (profile == null) continue;
                string name = profile.Name ?? string.Empty;
                if (name.EndsWith("-EG", StringComparison.OrdinalIgnoreCase) ||
                    name.IndexOf("NGL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("EXIST", StringComparison.OrdinalIgnoreCase) >= 0)
                    return profile;
            }
            return null;
        }

        private static bool IsCeRoadAlignment(CivilAlignment alignment)
        {
            string description = alignment.Description ?? string.Empty;
            return description.IndexOf("CE road", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (alignment.Name ?? string.Empty).StartsWith("RD", StringComparison.OrdinalIgnoreCase);
        }

        private static string UniqueProfileName(CivilAlignment alignment, string desired, Transaction transaction)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in alignment.GetProfileIds())
            {
                CivilProfile profile = transaction.GetObject(id, OpenMode.ForRead, false) as CivilProfile;
                if (profile != null) existing.Add(profile.Name);
            }
            string result = desired;
            int index = 2;
            while (existing.Contains(result)) result = desired + "-" + index++.ToString(CultureInfo.InvariantCulture);
            return result;
        }

        private static int BindDesignToProfileViews(Database database, ObjectId alignmentId, ObjectId nglId, ObjectId designId, Transaction transaction)
        {
            int updated = 0;
            BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead, false) as BlockTableRecord;
            if (model == null) return 0;
            foreach (ObjectId id in model)
            {
                DBObject value;
                try { value = transaction.GetObject(id, OpenMode.ForWrite, false); }
                catch { continue; }
                if (value == null || value.GetType().Name.IndexOf("ProfileView", StringComparison.OrdinalIgnoreCase) < 0) continue;
                ObjectId linkedAlignment = ReadObjectId(value, "AlignmentId");
                if (!linkedAlignment.IsNull && linkedAlignment != alignmentId) continue;
                ProfileViewBandDataBinder.Bind(value, nglId, designId, ObjectId.Null);
                updated++;
            }
            return updated;
        }

        private static List<string> ReadAssemblyNames(Document document, CivilDocument civilDocument)
        {
            var names = new List<string>();
            if (document == null || civilDocument == null) return names;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in CivilAssemblyResolver.GetAssemblyIds(civilDocument, document.Database))
                {
                    if (id.IsNull || id.IsErased) continue;
                    DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                    string name = Convert.ToString(ReadProperty(value, "Name"), CultureInfo.CurrentCulture);
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                }
            }
            return names.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static bool TryCreateMissingBaselineAndRegion(
            object corridor, string corridorName, object baselines, CivilDocument civilDocument,
            Transaction transaction, RoadCorridorCompletionOptions options, ref RoadCorridorCompletionResult result)
        {
            if (corridor == null || baselines == null || civilDocument == null) return false;
            var candidates = new List<CivilAlignment>();
            foreach (ObjectId id in civilDocument.GetAlignmentIds())
            {
                CivilAlignment alignment = transaction.GetObject(id, OpenMode.ForRead, false) as CivilAlignment;
                if (alignment != null && IsCeRoadAlignment(alignment)) candidates.Add(alignment);
            }
            CivilAlignment selected = candidates.FirstOrDefault(item =>
                !string.IsNullOrWhiteSpace(corridorName) && corridorName.IndexOf(item.Name, StringComparison.OrdinalIgnoreCase) >= 0);
            if (selected == null && candidates.Count == 1) selected = candidates[0];
            if (selected == null) return false;
            CivilProfile profile = null;
            foreach (ObjectId id in selected.GetProfileIds())
            {
                CivilProfile current = transaction.GetObject(id, OpenMode.ForRead, false) as CivilProfile;
                if (current == null) continue;
                if (profile == null) profile = current;
                string n = current.Name ?? string.Empty;
                if (n.EndsWith("-FG", StringComparison.OrdinalIgnoreCase) || n.IndexOf("FINAL", StringComparison.OrdinalIgnoreCase) >= 0)
                { profile = current; break; }
            }
            if (profile == null) return false;
            object baseline = InvokeAddBaseline(baselines, selected.ObjectId, profile.ObjectId, selected.Name);
            if (baseline == null) return false;
            result.Baselines++;
            ObjectId assemblyId = FindAssemblyId(civilDocument, transaction, options.AssemblyName);
            if (assemblyId.IsNull) return true;
            object regions = ReadProperty(baseline, "BaselineRegions") ?? ReadProperty(baseline, "Regions");
            object region = InvokeAddRegion(regions, assemblyId, selected.StartingStation, selected.EndingStation);
            if (region != null) result.Regions++;
            return true;
        }

        private static object InvokeAddBaseline(object collection, ObjectId alignmentId, ObjectId profileId, string name)
        {
            if (collection == null) return null;
            foreach (MethodInfo method in collection.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(item => item.Name == "Add").OrderBy(item => item.GetParameters().Length))
            {
                ParameterInfo[] ps = method.GetParameters(); var args = new object[ps.Length]; bool ok = true; int ids = 0;
                for (int i=0;i<ps.Length;i++)
                {
                    string p=(ps[i].Name ?? string.Empty).ToLowerInvariant(); Type t=ps[i].ParameterType;
                    if (t==typeof(ObjectId)) args[i] = p.Contains("profile") ? profileId : p.Contains("alignment") ? alignmentId : (ids++==0 ? alignmentId : profileId);
                    else if (t==typeof(string)) args[i] = string.IsNullOrWhiteSpace(name) ? "CE Road Baseline" : name;
                    else if (t==typeof(bool)) args[i]=false; else if (ps[i].HasDefaultValue) args[i]=ps[i].DefaultValue; else { ok=false; break; }
                }
                if (!ok) continue;
                try { object value=method.Invoke(collection,args); if (value!=null) return value; } catch { }
            }
            return CivilStyleDiscovery.Enumerate(collection).FirstOrDefault();
        }

        private static object InvokeAddRegion(object regions, ObjectId assemblyId, double start, double end)
        {
            if (regions == null || assemblyId.IsNull) return null;
            foreach (MethodInfo method in regions.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(item => item.Name == "Add").OrderBy(item => item.GetParameters().Length))
            {
                ParameterInfo[] ps=method.GetParameters(); var args=new object[ps.Length]; bool ok=true; int doubles=0;
                for(int i=0;i<ps.Length;i++)
                {
                    string p=(ps[i].Name ?? string.Empty).ToLowerInvariant(); Type t=ps[i].ParameterType;
                    if(t==typeof(ObjectId)) args[i]=assemblyId;
                    else if(t==typeof(double)) args[i]=p.Contains("end") ? end : p.Contains("start") ? start : (doubles++==0 ? start : end);
                    else if(t==typeof(string)) args[i]="CE Road Region";
                    else if(t==typeof(bool)) args[i]=true; else if(ps[i].HasDefaultValue) args[i]=ps[i].DefaultValue; else {ok=false;break;}
                }
                if(!ok) continue;
                try { object value=method.Invoke(regions,args); if(value!=null) return value; } catch { }
            }
            return CivilStyleDiscovery.Enumerate(regions).FirstOrDefault();
        }

        private static ObjectId FindAssemblyId(CivilDocument civilDocument, Transaction transaction, string requested)
        {
            ObjectId first=ObjectId.Null;
            foreach(ObjectId id in CivilAssemblyResolver.GetAssemblyIds(civilDocument, AcApplication.DocumentManager.MdiActiveDocument.Database))
            {
                if(id.IsNull || id.IsErased) continue; if(first.IsNull) first=id;
                DBObject value=transaction.GetObject(id,OpenMode.ForRead,false);
                string name=Convert.ToString(ReadProperty(value,"Name"),CultureInfo.CurrentCulture);
                if(!string.IsNullOrWhiteSpace(requested) && string.Equals(name,requested,StringComparison.OrdinalIgnoreCase)) return id;
            }
            return first;
        }

        private static object EnsureCorridorSurface(object collection, string name, IEnumerable<string> codes, bool boundary, ref RoadCorridorCompletionResult result)
        {
            if (collection == null) { result.Warnings++; return null; }
            object surface = CivilStyleDiscovery.Enumerate(collection)
                .FirstOrDefault(item => string.Equals(Convert.ToString(ReadProperty(item, "Name"), CultureInfo.CurrentCulture), name, StringComparison.OrdinalIgnoreCase));
            if (surface == null)
            {
                surface = InvokeReturning(collection, "Add", name);
                if (surface != null) result.Surfaces++;
            }
            if (surface == null) { result.Warnings++; return null; }
            foreach (string code in codes)
            {
                if (Invoke(surface, "AddLinkCode", code) || Invoke(surface, "AddCode", code)) { }
            }
            if (boundary)
            {
                object boundaries = ReadProperty(surface, "Boundaries");
                if (Invoke(boundaries, "AddCorridorExtentsBoundary", name + "-OUTER") ||
                    Invoke(boundaries, "Add", name + "-OUTER")) result.Boundaries++;
            }
            return surface;
        }

        private static int ApplySurfaceTargets(object region, ObjectId surfaceId)
        {
            if (region == null || surfaceId.IsNull) return 0;
            int changed = 0;
            object targets = InvokeReturning(region, "GetTargets") ?? ReadProperty(region, "Targets");
            foreach (object target in CivilStyleDiscovery.Enumerate(targets))
            {
                if (target == null) continue;
                if (TrySetObjectId(target, surfaceId, "TargetId", "SurfaceId")) changed++;
                object ids = ReadProperty(target, "TargetIds") ?? ReadProperty(target, "ObjectIds");
                ObjectIdCollection collection = ids as ObjectIdCollection;
                if (collection != null)
                {
                    collection.Clear();
                    collection.Add(surfaceId);
                    changed++;
                }
            }
            if (targets != null) Invoke(region, "SetTargets", targets);
            return changed;
        }

        private static int EnableSlopePatterns(object corridor)
        {
            int changed = 0;
            object patterns = ReadProperty(corridor, "SlopePatterns");
            foreach (object pattern in CivilStyleDiscovery.Enumerate(patterns))
            {
                if (pattern == null) continue;
                if (TrySetBoolean(pattern, true, "Visible", "IsVisible", "Enabled")) changed++;
                Invoke(pattern, "Rebuild");
            }
            return changed;
        }

        private static bool IsCeCorridor(object corridor, string name)
        {
            string description = Convert.ToString(ReadProperty(corridor, "Description"), CultureInfo.CurrentCulture);
            return (name ?? string.Empty).IndexOf("CORRIDOR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (description ?? string.Empty).IndexOf("CE road", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ObjectId ResolveOptionalStyle(Database database, CivilDocument civilDocument, RoadProductionSettings road, ProjectStyleSelection project, string category, Transaction transaction, out string actual)
        {
            actual = string.Empty;
            try { return CivilStyleCatalogV2.ResolveStyleId(database, civilDocument, category, RoadStyle(road, project, category), transaction, out actual); }
            catch { return ObjectId.Null; }
        }

        private static string RoadStyle(RoadProductionSettings road, ProjectStyleSelection project, string category)
        {
            string requested = road == null ? string.Empty : road.Value(category);
            return !string.IsNullOrWhiteSpace(requested) &&
                !string.Equals(requested, "<Use drawing default>", StringComparison.OrdinalIgnoreCase)
                ? requested.Trim()
                : ReadStyle(project, category);
        }

        private static string ReadStyle(ProjectStyleSelection project, string category)
        {
            string value;
            return project != null && project.Exists && project.Values.TryGetValue(category, out value) &&
                !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, "<Use drawing default>", StringComparison.OrdinalIgnoreCase)
                ? value.Trim() : string.Empty;
        }

        private static List<CivilChoice> ReadSurfaces(Document document, CivilDocument civilDocument)
        {
            var result = new List<CivilChoice>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civilDocument.GetSurfaceIds())
                {
                    CivilSurface surface = transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
                    if (surface != null) result.Add(new CivilChoice(id, surface.Name));
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static ObjectId GetOrCreateLayer(Database database, Transaction transaction, string name)
        {
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (layers.Has(name)) return layers[name];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        private static string SafeName(string value, string fallback) { return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim(); }
        private static IList<string> SplitCodes(string value, IEnumerable<string> fallback)
        {
            List<string> result = (value ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim()).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return result.Count > 0 ? result : fallback.ToList();
        }
        private static Document ActiveDocument() { return AcApplication.DocumentManager.MdiActiveDocument; }

        private static object ReadProperty(object target, string name)
        {
            if (target == null) return null;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                return property == null || property.GetGetMethod() == null ? null : property.GetValue(target, null);
            }
            catch { return null; }
        }

        private static ObjectId ReadObjectId(object target, string name)
        {
            object value = ReadProperty(target, name);
            return value is ObjectId ? (ObjectId)value : ObjectId.Null;
        }

        private static bool TrySetObjectId(object target, ObjectId value, params string[] names)
        {
            if (target == null || value.IsNull) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (property != null && property.CanWrite && property.PropertyType == typeof(ObjectId))
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static bool TrySetBoolean(object target, bool value, params string[] names)
        {
            if (target == null) return false;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
                    {
                        property.SetValue(target, value, null);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static bool Invoke(object target, string name, params object[] supplied)
        {
            return InvokeReturning(target, name, supplied) != null;
        }

        private static object InvokeReturning(object target, string name, params object[] supplied)
        {
            if (target == null) return null;
            foreach (MethodInfo method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != supplied.Length) continue;
                bool valid = true;
                for (int index = 0; index < parameters.Length; index++)
                {
                    if (supplied[index] == null) continue;
                    if (!parameters[index].ParameterType.IsInstanceOfType(supplied[index]) &&
                        !(parameters[index].ParameterType == typeof(string) && supplied[index] is string))
                    { valid = false; break; }
                }
                if (!valid) continue;
                try
                {
                    object result = method.Invoke(target, supplied);
                    return method.ReturnType == typeof(void) ? target : result;
                }
                catch { }
            }
            return null;
        }
    }

    internal sealed class RoadCorridorCompletionOptions
    {
        internal ObjectId TargetSurfaceId { get; set; }
        internal string AssemblyName { get; set; }
        internal string TopSurfaceName { get; set; }
        internal string BottomSurfaceName { get; set; }
        internal IList<string> TopCodes { get; set; }
        internal IList<string> BottomCodes { get; set; }
        internal bool AddBoundary { get; set; }
        internal bool ApplyTargets { get; set; }
        internal bool EnableSlopePatterns { get; set; }
    }

    internal sealed class RoadCorridorCompletionResult
    {
        internal RoadCorridorCompletionResult() { Rows = new List<IList<string>>(); }
        internal int Corridors { get; set; }
        internal int Baselines { get; set; }
        internal int Regions { get; set; }
        internal int Targets { get; set; }
        internal int Surfaces { get; set; }
        internal int Boundaries { get; set; }
        internal int SlopePatterns { get; set; }
        internal int Rebuilt { get; set; }
        internal int Warnings { get; set; }
        internal List<IList<string>> Rows { get; private set; }
    }

    internal sealed class CivilChoice
    {
        internal CivilChoice(ObjectId id, string name) { Id = id; Name = name ?? string.Empty; }
        internal ObjectId Id { get; private set; }
        internal string Name { get; private set; }
        public override string ToString() { return Name; }
    }

    internal sealed class CivilChoiceWindow : System.Windows.Window
    {
        private readonly System.Windows.Controls.ListBox _list;
        internal CivilChoiceWindow(string title, string message, IEnumerable<CivilChoice> choices)
        {
            Title = title;
            Width = 620;
            Height = 500;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner;
            var root = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(16) };
            Content = root;
            var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 10, 0, 0) };
            System.Windows.Controls.DockPanel.SetDock(buttons, System.Windows.Controls.Dock.Bottom);
            root.Children.Add(buttons);
            var ok = new System.Windows.Controls.Button { Content = "Continue", MinWidth = 100, Padding = new System.Windows.Thickness(10, 5, 10, 5), IsDefault = true };
            ok.Click += delegate { Selected = _list.SelectedItem as CivilChoice; if (Selected != null) { Accepted = true; DialogResult = true; } };
            buttons.Children.Add(ok);
            var cancel = new System.Windows.Controls.Button { Content = "Cancel", MinWidth = 90, Padding = new System.Windows.Thickness(10, 5, 10, 5), Margin = new System.Windows.Thickness(8, 0, 0, 0), IsCancel = true };
            buttons.Children.Add(cancel);
            var heading = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new System.Windows.Thickness(0, 0, 0, 10) };
            System.Windows.Controls.DockPanel.SetDock(heading, System.Windows.Controls.Dock.Top);
            root.Children.Add(heading);
            _list = new System.Windows.Controls.ListBox { ItemsSource = choices == null ? new List<CivilChoice>() : choices.ToList(), DisplayMemberPath = "Name" };
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            root.Children.Add(_list);
        }
        internal bool Accepted { get; private set; }
        internal CivilChoice Selected { get; private set; }
    }
}
