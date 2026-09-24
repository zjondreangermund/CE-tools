using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.September24CorridorAssignmentCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Assigns selected Civil 3D corridor production settings to multiple
    /// existing CE corridors without creating replacement corridors.
    /// </summary>
    public sealed class September24CorridorAssignmentCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ROADCORRIDORASSIGNBATCH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void AssignCorridorSettings()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return;

            List<CivilChoice> corridorChoices =
                FieldCompletionBatchUi.ReadCorridorChoices(document, civilDocument);
            if (corridorChoices.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADCORRIDORASSIGNBATCH: no CE corridors were found.");
                return;
            }

            IList<CivilChoice> selectedCorridors = FieldCompletionBatchUi.PickMultiple(
                "CE Tools - Assign Corridor Settings",
                "Select every existing corridor that must receive the same assembly, frequencies, targets, slope-pattern settings and boundary extents.",
                corridorChoices);
            if (selectedCorridors == null || selectedCorridors.Count == 0) return;

            List<CivilChoice> assemblies = ReadAssemblyChoices(document, civilDocument);
            if (assemblies.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADCORRIDORASSIGNBATCH: no Civil 3D assemblies were found.");
                return;
            }

            List<CivilChoice> surfaces = FieldCompletionBatchUi.ReadSurfaceChoices(
                document,
                civilDocument);
            surfaces.Insert(0, new CivilChoice(ObjectId.Null, "<Keep current>"));

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

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Assign Corridor Settings",
                "Apply one controlled production configuration to multiple selected corridors. Source alignments, profiles and corridor objects are retained.");
            model.AddChoice(
                "AssemblyAction",
                "01 Assembly",
                "Assign selected assembly to existing regions",
                "Enabled",
                "When enabled, the selected assembly is assigned to every existing baseline region in every selected corridor.",
                new[] { "Enabled", "Disabled" });
            model.AddChoice(
                "Assembly",
                "01 Assembly",
                "Assembly",
                assemblies[0].Name,
                "Select the Civil 3D assembly assigned to existing baseline regions.",
                assemblies.Select(item => item.Name));
            model.AddPositiveDouble(
                "TangentFrequency",
                "02 Assembly Frequencies",
                "Along tangents (m)",
                10.0,
                "Maximum spacing between applied assemblies along tangent geometry.");
            model.AddPositiveDouble(
                "CurveFrequency",
                "02 Assembly Frequencies",
                "Along horizontal curves (m)",
                5.0,
                "Maximum spacing between applied assemblies along horizontal curves and spirals.");
            model.AddPositiveDouble(
                "VerticalFrequency",
                "02 Assembly Frequencies",
                "Along vertical curves (m)",
                5.0,
                "Maximum spacing between applied assemblies along profile curves.");
            model.AddChoice(
                "TargetAction",
                "03 Targets",
                "Assign selected surface target",
                "Enabled",
                "Assign the selected Civil 3D surface to compatible surface/elevation/daylight targets in every selected region.",
                new[] { "Enabled", "Disabled" });
            model.AddChoice(
                "TargetSurface",
                "03 Targets",
                "Target surface",
                surfaces[0].Name,
                "Select the Civil 3D surface used by the corridor region targets.",
                surfaces.Select(item => item.Name));
            model.AddText(
                "CorridorLayer",
                "04 Output",
                "Corridor layer",
                "<Keep current>",
                "Optional layer to create/use and assign to every selected corridor.");
            model.AddChoice(
                "BoundaryMode",
                "04 Output",
                "Corridor boundary extents",
                "Corridor extents",
                "Choose corridor-extents boundaries, preserve existing boundaries, or remove the corridor surface boundaries.",
                new[] { "Corridor extents", "Keep current", "No boundary" });
            model.AddText(
                "TopCodes",
                "04 Output",
                "Top surface link codes",
                "Top,Pave",
                "Comma-separated corridor link codes written to the road TOP surface.");
            model.AddText(
                "BottomCodes",
                "04 Output",
                "Bottom surface link codes",
                "Datum,Subgrade",
                "Comma-separated corridor link codes written to the road BOTTOM surface.");
            model.AddChoice(
                "SlopePatternMode",
                "05 Slope Patterns",
                "Slope patterns",
                "Enabled",
                "Enable, disable or preserve each corridor's slope-pattern collection.",
                new[] { "Enabled", "Disabled", "Keep current" });
            model.AddChoice(
                "LeftCut",
                "05 Slope Patterns",
                "Left cut slope-pattern style",
                slopeStyles[0],
                "Style applied to the left cut condition.",
                slopeStyles);
            model.AddChoice(
                "LeftFill",
                "05 Slope Patterns",
                "Left fill slope-pattern style",
                slopeStyles[0],
                "Style applied to the left fill condition.",
                slopeStyles);
            model.AddChoice(
                "RightCut",
                "05 Slope Patterns",
                "Right cut slope-pattern style",
                slopeStyles[0],
                "Style applied to the right cut condition.",
                slopeStyles);
            model.AddChoice(
                "RightFill",
                "05 Slope Patterns",
                "Right fill slope-pattern style",
                slopeStyles[0],
                "Style applied to the right fill condition.",
                slopeStyles);
            model.AddChoice(
                "AutomaticRebuild",
                "06 Rebuild",
                "Automatic rebuild",
                "Keep current",
                "Switch Civil 3D automatic corridor rebuilding on or off for every selected corridor.",
                new[] { "Enabled", "Disabled", "Keep current" });
            model.AddChoice(
                "Rebuild",
                "06 Rebuild",
                "Rebuild after applying settings",
                "Enabled",
                "Rebuild each selected corridor after the assignments are committed.",
                new[] { "Enabled", "Disabled" });

            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            CivilChoice selectedAssembly = assemblies.FirstOrDefault(
                item => string.Equals(
                    item.Name,
                    model.Text("Assembly"),
                    StringComparison.OrdinalIgnoreCase));
            CivilChoice selectedSurface = surfaces.FirstOrDefault(
                item => string.Equals(
                    item.Name,
                    model.Text("TargetSurface"),
                    StringComparison.OrdinalIgnoreCase));
            bool assignAssembly = string.Equals(
                model.Text("AssemblyAction"),
                "Enabled",
                StringComparison.OrdinalIgnoreCase);
            bool applyTargets = string.Equals(
                model.Text("TargetAction"),
                "Enabled",
                StringComparison.OrdinalIgnoreCase) &&
                selectedSurface != null &&
                !selectedSurface.Id.IsNull;
            string boundaryMode = model.Text("BoundaryMode");
            string slopeMode = model.Text("SlopePatternMode");

            var options = new RoadCorridorCompletionOptions
            {
                CorridorIds = selectedCorridors.Select(item => item.Id).ToList(),
                CorridorLayerName = model.Text("CorridorLayer"),
                ProfileStyleName = "<Use drawing default>",
                LeftCutSlopeStyle = model.Text("LeftCut"),
                LeftFillSlopeStyle = model.Text("LeftFill"),
                RightCutSlopeStyle = model.Text("RightCut"),
                RightFillSlopeStyle = model.Text("RightFill"),
                SlopePatternMode = slopeMode,
                AutomaticRebuildMode = model.Text("AutomaticRebuild"),
                TargetSurfaceId = applyTargets ? selectedSurface.Id : ObjectId.Null,
                TargetSurfaceName = applyTargets ? selectedSurface.Name : string.Empty,
                AssemblyName = assignAssembly && selectedAssembly != null
                    ? selectedAssembly.Name
                    : string.Empty,
                AssemblyId = assignAssembly && selectedAssembly != null
                    ? selectedAssembly.Id
                    : ObjectId.Null,
                AssignAssemblyToExistingRegions = assignAssembly,
                TopSurfaceName = "TOP-RD-01",
                BottomSurfaceName = "BOTTOM-RD-01",
                UseRoadNumberedSurfaceNames = true,
                TopCodes = SplitCodes(model.Text("TopCodes"), new[] { "Top", "Pave" }),
                BottomCodes = SplitCodes(model.Text("BottomCodes"), new[] { "Datum", "Subgrade" }),
                BoundaryMode = boundaryMode,
                AddBoundary = string.Equals(
                    boundaryMode,
                    "Corridor extents",
                    StringComparison.OrdinalIgnoreCase),
                ManageCorridorSurfaces = !string.Equals(
                    boundaryMode,
                    "Keep current",
                    StringComparison.OrdinalIgnoreCase),
                ApplyTargets = applyTargets,
                TangentFrequency = model.Double("TangentFrequency", 10.0),
                CurveFrequency = model.Double("CurveFrequency", 5.0),
                VerticalFrequency = model.Double("VerticalFrequency", 5.0),
                EnsureVisible = false,
                EnableAutomaticRebuild = false,
                EnableSlopePatterns = string.Equals(
                    slopeMode,
                    "Enabled",
                    StringComparison.OrdinalIgnoreCase),
                RebuildCorridors = string.Equals(
                    model.Text("Rebuild"),
                    "Enabled",
                    StringComparison.OrdinalIgnoreCase),
                RefreshProfileViews = false
            };

            RoadCorridorCompletionResult result =
                RoadCorridorCompletionCommands.CompleteAll(
                    document,
                    civilDocument,
                    options);

            document.Editor.Regen();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Corridor Assignment Result",
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Selected={0}; corridors={1}; assembly assignments={2}; frequency values={3}; targets={4}; boundaries={5}; slope-pattern changes={6}; rebuilt={7}; warnings={8}.",
                    selectedCorridors.Count,
                    result.Corridors,
                    result.AssemblyAssignments,
                    result.FrequencySettings,
                    result.Targets,
                    result.Boundaries,
                    result.SlopePatterns,
                    result.Rebuilt,
                    result.Warnings),
                new List<string> { "Selected", "Processed", "Assembly assignments", "Frequency values", "Targets", "Boundaries", "Slope patterns", "Rebuilt", "Warnings" },
                new List<IList<string>>
                {
                    new List<string>
                    {
                        selectedCorridors.Count.ToString(CultureInfo.CurrentCulture),
                        result.Corridors.ToString(CultureInfo.CurrentCulture),
                        result.AssemblyAssignments.ToString(CultureInfo.CurrentCulture),
                        result.FrequencySettings.ToString(CultureInfo.CurrentCulture),
                        result.Targets.ToString(CultureInfo.CurrentCulture),
                        result.Boundaries.ToString(CultureInfo.CurrentCulture),
                        result.SlopePatterns.ToString(CultureInfo.CurrentCulture),
                        result.Rebuilt.ToString(CultureInfo.CurrentCulture),
                        result.Warnings.ToString(CultureInfo.CurrentCulture)
                    }
                },
                "CE TOOLS CORRIDOR ASSIGNMENT REGISTER");
        }

        private static List<CivilChoice> ReadAssemblyChoices(
            Document document,
            CivilDocument civilDocument)
        {
            var result = new List<CivilChoice>();
            if (document == null || civilDocument == null) return result;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in CivilAssemblyResolver.GetAssemblyIds(
                    civilDocument,
                    document.Database))
                {
                    if (id.IsNull || id.IsErased) continue;
                    try
                    {
                        DBObject assembly =
                            transaction.GetObject(id, OpenMode.ForRead, false);
                        string name = Convert.ToString(
                            FieldCompletionBatchUi.ReadProperty(
                                assembly,
                                "Name"),
                            CultureInfo.CurrentCulture);
                        if (!string.IsNullOrWhiteSpace(name))
                            result.Add(new CivilChoice(id, name));
                    }
                    catch { }
                }
            }
            return result
                .GroupBy(item => item.Id)
                .Select(group => group.First())
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static List<string> SplitCodes(
            string value,
            IEnumerable<string> fallback)
        {
            var result = (value ?? string.Empty)
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (result.Count == 0)
                result.AddRange(fallback ?? Enumerable.Empty<string>());
            return result;
        }
    }
}
