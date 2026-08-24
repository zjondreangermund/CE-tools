using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using ConnectorPositionType = Autodesk.Civil.DatabaseServices.ConnectorPositionType;

[assembly: CommandClass(typeof(CETools.Civil3D.August24FieldCompletionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// August 24 field-review completion pack.  The commands in this file are small
    /// front doors around the existing production engines plus a few isolated field
    /// utilities that were missing from the discipline centres.
    /// </summary>
    public sealed class August24FieldCompletionCommands
    {
        private const double Tol = 0.000001;
        private const string FeatureRelationKey = "CE_FLREL";
        private const string SlopeLinkKey = "CE_FIELD_SLOPE_LINK";
        private const string RoadHatchLayer = "CE-ROAD-HATCH";
        private const string RoadHatchBoundaryLayer = "CE-ROAD-HATCH-BOUNDARY";

        // -----------------------------------------------------------------
        // Field supplementary centres
        // -----------------------------------------------------------------
        [CommandMethod("CE_TOOLS", "CE_CADSUPPLEMENTARY", CommandFlags.Modal)]
        public void CadSupplementary()
        {
            RunMenu(
                "CE-CAD SUPPLEMENTARY",
                "Recent field utilities grouped away from the core CAD-production pages.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Convert Curves to Polylines - Keep Originals", "CE_CURVECONVERT", "Convert curves with Keep originals as the safe default.", "01 Geometry"),
                    A("CE-Break Routes at Crossings / T-Junctions", "CE_PLBREAKJUNCTIONS", "Split route polylines safely at real crossings and T-junctions.", "01 Geometry"),
                    A("CE-Close Multiple Open Polylines / Feature Lines", "CE_CLOSEOPENMULTI", "Close multiple selected open polylines or feature lines.", "01 Geometry"),
                    A("CE-Stretch Multiple Feature Lines", "CE_MULTISTRETCHFL", "Preselect multiple feature lines for native grip-aware STRETCH.", "01 Geometry"),
                    A("CE-Offset / Construction Offset", "CE_SURVEYCONSTRUCTIONOFFSET", "Normal offsets or per-straight construction offsets with zero-fillet joins.", "01 Geometry"),
                    A("CE-Centre Construction Lines", "CE_SURVEYMIDCONSTRUCTION", "Create centre construction lines for selected curve pairs.", "01 Geometry"),
                    A("CE-Multiple Dimensions", "CE_MULTIDIM", "Dimension multiple open/closed polylines and feature lines.", "02 Annotation"),
                    A("CE-Feature-Line Dynamic Slope Arrows", "CE_FEATURELINESLOPEARROWS", "Create linked slope arrows and percent labels on multiple feature lines.", "02 Annotation"),
                    A("CE-Surface Slope Arrows", "CE_SURFACESLOPEARROWS", "Create sampled downhill arrows and slope labels on a selected surface.", "02 Annotation"),
                    A("CE-Site Grid Presentation", "CE_SITEGRIDPRESENTATION", "Apply colour and annotative paper text to linked Site Grid children.", "02 Annotation"),
                    A("CE-Road Side Hatch", "CE_ROADHATCHSIDES", "Hatch left/right/both sides of multiple road polylines or alignments.", "03 Production"),
                    A("CE-Sewer Field Supplementary", "CE_SEWERFIELDSUPPLEMENTARY", "Open sewer field completion utilities.", "03 Production"),
                    A("CE-Platform Field Supplementary", "CE_PLATFORMFIELDSUPPLEMENTARY", "Open platform field completion utilities.", "03 Production")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SURVEYFIELDSUPPLEMENTARY", CommandFlags.Modal)]
        public void SurveySupplementary()
        {
            RunMenu(
                "CE-SURVEY FIELD SUPPLEMENTARY",
                "Site Grid, construction, slope and setting-out field tools.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Site Grid - Selected / Multiple / Window", "CE_SITEGRIDMULTI", "Create linked full Site Grids from one or more closed boundaries.", "01 Grid"),
                    A("CE-Site Grid Presentation", "CE_SITEGRIDPRESENTATION", "Set grid colour and annotative paper text height.", "01 Grid"),
                    A("CE-Grid Setting-Out", "CE_GRIDSETTINGOUT", "Linked setting-out across multiple source strings.", "01 Grid"),
                    A("CE-Feature-Line Dynamic Slope Arrows", "CE_FEATURELINESLOPEARROWS", "Slope arrows/values follow edited feature-line elevations.", "02 Slopes"),
                    A("CE-Refresh Dynamic Slope Arrows", "CE_SLOPEARROWSREFRESH", "Refresh all linked feature-line slope arrows in the drawing.", "02 Slopes"),
                    A("CE-Surface Slope Arrows", "CE_SURFACESLOPEARROWS", "Sample a surface and show downhill slope arrows/values.", "02 Slopes"),
                    A("CE-Offset / Construction Offset", "CE_SURVEYCONSTRUCTIONOFFSET", "Create normal or construction-line offsets.", "03 Construction"),
                    A("CE-Centre Construction Lines", "CE_SURVEYMIDCONSTRUCTION", "Create centre construction lines between selected curve pairs.", "03 Construction")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SEWERFIELDSUPPLEMENTARY", CommandFlags.Modal)]
        public void SewerSupplementary()
        {
            RunMenu(
                "CE-SEWER FIELD SUPPLEMENTARY",
                "Network preparation, sequencing, surface/rim control, connection checks and production handoff.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Break Source Routes at Crossings / T-Junctions", "CE_PLBREAKJUNCTIONS", "Split source polylines before network creation so crossings become real vertices.", "01 Network preparation"),
                    A("CE-Create Sewer Network from Multiple Sources", "CE_SEWERNETWORKMULTI", "Create one connected gravity network from lines, polylines or feature lines.", "01 Network preparation"),
                    A("CE-Connect Selected / Whole Network", "CE_SEWCONNECTPARTS", "Connect pipe endpoints to the nearest compatible structures.", "01 Network preparation"),
                    A("CE-Rims from Selected Surface", "CE_SEWSURFACERIMS", "Set selected/all structure rims to a surface plus a specified height.", "02 Surface / levels"),
                    A("CE-Sewer Cover / Slope / Drop Audit", "CE_SEWAUDITLIMITS", "Review min/max cover, pipe slope and manhole drop against field limits.", "02 Surface / levels"),
                    A("CE-Sequence + Auto Alignments", "CE_SEWSEQAUTOALIGN", "Sequence first, then safely hand off to alignment production as a separate command step.", "03 Sequence / production"),
                    A("CE-Sequence Network + Production Options", "CE_SEWSEQNETWORKPRODUCTION", "Complete-network sequencing with labels/alignments/profiles as separate production options.", "03 Sequence / production"),
                    A("CE-Sequence Selected Main + Production Options", "CE_SEWSEQMAINPRODUCTION", "Select the main route first, then choose labels/alignments/profiles separately.", "03 Sequence / production"),
                    A("CE-Create Sewer Alignments", "CE_SEWALIGN", "Create branch alignments after sequencing; kept separate from the rename transaction.", "04 Alignments / profiles"),
                    A("CE-Create Sewer Profiles", "CE_SEWPROFILE", "Create surface-linked sewer profiles after alignment review.", "04 Alignments / profiles")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMFIELDSUPPLEMENTARY", CommandFlags.Modal)]
        public void PlatformSupplementary()
        {
            RunMenu(
                "CE-PLATFORM FIELD SUPPLEMENTARY",
                "Feature-line, surface, grading and linked-offset field completion tools.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Create Feature Lines - Site / Surface Options", "CE_FLCREATE", "Create multiple feature lines with source/surface options.", "01 Feature lines"),
                    A("CE-Elevation from Surface - Multiple Feature Lines", "CE_PLATFORMDRAPEMULTI", "Drape multiple feature lines dynamically to one selected surface.", "01 Feature lines"),
                    A("CE-Fixed / Minimum Slope - Feature Lines", "CE_PLATFORMFIXEDMINSLOPE", "Apply fixed/minimum slope to feature lines and optionally add intermediate points.", "02 Levels"),
                    A("CE-Stepped Offsets - Pick Inside / Outside Side", "CE_FLRELCREATE", "Create a linked stepped set and explicitly pick the offset side.", "03 Linked offsets"),
                    A("CE-Link Existing Feature Lines to Source", "CE_FLRELLINKEXISTING", "Link multiple existing feature lines to one source so source edits rebuild them.", "03 Linked offsets"),
                    A("CE-Preserve Changed Linked Relationship", "CE_FLRELADOPT", "Adopt current edited child offsets as the new saved relationship.", "03 Linked offsets"),
                    A("CE-Grade Multiple Feature Lines to Surface", "CE_PLATFORMGRADETOSURFACE", "Dynamic cut/fill daylight grading to a selected surface.", "04 Grading"),
                    A("CE-Platform Site / Surface / Infill", "CE_PLATFORMSURFACE", "Create platform site/surface, add breaklines and attempt separate infills.", "04 Grading"),
                    A("CE-Close Multiple Open Feature Lines / Polylines", "CE_CLOSEOPENMULTI", "Close multiple platform source boundaries.", "05 Cleanup"),
                    A("CE-Stretch Multiple Feature Lines", "CE_MULTISTRETCHFL", "Stretch multiple selected feature lines.", "05 Cleanup")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_ROADFIELDSUPPLEMENTARY", CommandFlags.Modal)]
        public void RoadSupplementary()
        {
            RunMenu(
                "CE-ROAD FIELD SUPPLEMENTARY",
                "Road hatch, construction and elevation-link field utilities.",
                new List<DisciplineWorkflowAction>
                {
                    A("CE-Hatch Road Left / Right / Both", "CE_ROADHATCHSIDES", "Create controlled road-side hatch strips from multiple polylines or alignments.", "01 Presentation"),
                    A("CE-Offset / Construction Offset", "CE_SURVEYCONSTRUCTIONOFFSET", "Normal/construction offsets with zero-fillet joins.", "02 Construction"),
                    A("CE-Centre Construction Lines", "CE_SURVEYMIDCONSTRUCTION", "Create centre construction lines within a specified maximum separation.", "02 Construction"),
                    A("CE-Match Road Junction Elevations", "CE_ROADELEVMATCH", "Link selected crossing feature lines to a master road elevation.", "03 Levels"),
                    A("CE-Refresh Road Junction Elevations", "CE_ROADELEVMATCHREFRESH", "Refresh all stored road elevation matches.", "03 Levels")
                });
        }

        // -----------------------------------------------------------------
        // Sewer production options
        // -----------------------------------------------------------------
        [CommandMethod("CE_TOOLS", "CE_SEWSEQAUTOALIGN", CommandFlags.Modal)]
        public void SewerSequenceAutoAlign()
        {
            Document document = Active();
            if (document == null) return;
            document.Editor.WriteMessage("\nCE-Sequence + Auto Alignments: sequencing and alignment creation are intentionally separate transactions for Civil 3D stability.");
            CeSequentialCommandRunner.Start(
                document,
                new List<string> { "CE_SEWSEQ", "CE_SEWALIGN" },
                "CE Tools - Sewer sequence + auto alignments");
        }

        [CommandMethod("CE_TOOLS", "CE_SEWSEQNETWORKPRODUCTION", CommandFlags.Modal)]
        public void SewerSequenceNetworkProduction()
        {
            RunMenu(
                "CE-Sequence Network + Production Options",
                "Run complete-network sequencing first. Labels, alignments and profiles remain separate Civil transactions.",
                new List<DisciplineWorkflowAction>
                {
                    A("1. Sequence / Rename Complete Network", "CE_SEWSEQ", "High point toward low point network sequence.", "01 Sequence"),
                    A("2. Create / Refresh Pipe and Structure Labels", "CE_SEWLABELS", "Apply sewer plan labels after the sequence completes.", "02 Production"),
                    A("3. Create Sewer Alignments", "CE_SEWALIGN", "Create alignments after reviewing the sequenced network.", "02 Production"),
                    A("4. Create Sewer Profiles", "CE_SEWPROFILE", "Create profiles after alignment production.", "02 Production"),
                    A("Audit Cover / Slope / Drop", "CE_SEWAUDITLIMITS", "Check the final network engineering limits.", "03 Review")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SEWSEQMAINPRODUCTION", CommandFlags.Modal)]
        public void SewerSequenceMainProduction()
        {
            RunMenu(
                "CE-Sequence Selected Main + Production Options",
                "Select Branch-1/main first, then run each Civil production step separately.",
                new List<DisciplineWorkflowAction>
                {
                    A("1. Sequence with Selected Main", "CE_SEWSEQMAIN", "Choose the main route and sequence from the high end toward the low end.", "01 Sequence"),
                    A("2. Create / Refresh Pipe and Structure Labels", "CE_SEWLABELS", "Apply labels after sequence review.", "02 Production"),
                    A("3. Create Sewer Alignments", "CE_SEWALIGN", "Create branch alignments separately.", "02 Production"),
                    A("4. Create Sewer Profiles", "CE_SEWPROFILE", "Create surface/profile views after alignments.", "02 Production"),
                    A("Audit Cover / Slope / Drop", "CE_SEWAUDITLIMITS", "Check engineering limits.", "03 Review")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SEWSURFACERIMS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SewerSurfaceRims()
        {
            Document document = Active();
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;
            List<SurfaceChoice> surfaces = WorkflowRepairCommands.ReadSurfaceChoices(document);
            if (surfaces.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_SEWSURFACERIMS cancelled. No Civil 3D surface was found.");
                return;
            }

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Sewer Rims from Surface",
                "Set selected structures or every structure in one selected network to the chosen surface elevation plus an optional height.");
            settings.AddChoice("Surface", "01 Surface", "Surface", surfaces[0].Name, "Existing ground / design surface controlling the rim elevations.", surfaces.Select(item => item.Name));
            settings.AddDouble("Height", "01 Surface", "Height above surface", 0.0, "Signed height added to the sampled surface elevation.");
            settings.AddChoice("Scope", "02 Structures", "Scope", "Selected structures", "Use selected structures or all structures in the network containing a selected part.", new[] { "Selected structures", "Whole selected network" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            SurfaceChoice surfaceChoice = surfaces.FirstOrDefault(item => string.Equals(item.Name, settings.Text("Surface"), StringComparison.OrdinalIgnoreCase));
            if (surfaceChoice == null) return;
            List<ObjectId> structures = ReadStructureScope(document, settings.Text("Scope"));
            if (structures.Count == 0) return;

            int changed = 0;
            int skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilSurface surface = transaction.GetObject(surfaceChoice.ObjectId, OpenMode.ForRead, false) as CivilSurface;
                if (surface == null) return;
                foreach (ObjectId id in structures.Distinct())
                {
                    CivilStructure structure = null;
                    try { structure = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilStructure; } catch { }
                    if (structure == null || structure.IsReferenceObject) { skipped++; continue; }
                    try
                    {
                        structure.RimElevation = surface.FindElevationAtXY(structure.Position.X, structure.Position.Y) + settings.Double("Height", 0.0);
                        changed++;
                    }
                    catch { skipped++; }
                }
                transaction.Commit();
            }
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage("\nCE_SEWSURFACERIMS complete. Rims updated={0}; skipped={1}.", changed, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_SEWCONNECTPARTS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SewerConnectParts()
        {
            Document document = Active();
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Connect Sewer Pipes and Structures",
                "Connect each open pipe endpoint to the nearest compatible structure in the same network. Existing connections are retained.");
            settings.AddChoice("Scope", "01 Scope", "Scope", "Selected parts", "Use selected pipes/structures or every part in one selected network.", new[] { "Selected parts", "Whole selected network" });
            settings.AddPositiveDouble("Tolerance", "02 Connection", "Maximum connection distance", 2.0, "Maximum plan distance from an open pipe endpoint to a structure centre.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            List<ObjectId> pipeIds;
            List<ObjectId> structureIds;
            if (!ReadPipeStructureScope(document, settings.Text("Scope"), out pipeIds, out structureIds)) return;
            double maxDistance = settings.Double("Tolerance", 2.0);
            int connected = 0;
            int skipped = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                var structures = new List<CivilStructure>();
                foreach (ObjectId id in structureIds.Distinct())
                {
                    try
                    {
                        CivilStructure structure = transaction.GetObject(id, OpenMode.ForRead, false) as CivilStructure;
                        if (structure != null) structures.Add(structure);
                    }
                    catch { }
                }

                foreach (ObjectId id in pipeIds.Distinct())
                {
                    CivilPipe pipe = null;
                    try { pipe = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilPipe; } catch { }
                    if (pipe == null || pipe.IsReferenceObject) { skipped++; continue; }
                    if (pipe.StartStructureId.IsNull)
                    {
                        CivilStructure structure = NearestStructure(pipe.StartPoint, pipe.NetworkId, structures, maxDistance);
                        if (structure != null)
                        {
                            try { pipe.ConnectToStructure(ConnectorPositionType.Start, structure.ObjectId, true); connected++; } catch { skipped++; }
                        }
                    }
                    if (pipe.EndStructureId.IsNull)
                    {
                        CivilStructure structure = NearestStructure(pipe.EndPoint, pipe.NetworkId, structures, maxDistance);
                        if (structure != null)
                        {
                            try { pipe.ConnectToStructure(ConnectorPositionType.End, structure.ObjectId, true); connected++; } catch { skipped++; }
                        }
                    }
                }
                transaction.Commit();
            }
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage("\nCE_SEWCONNECTPARTS complete. Endpoint connections made={0}; skipped/failed={1}.", connected, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_SEWAUDITLIMITS", CommandFlags.Modal)]
        public void SewerAuditLimits()
        {
            Document document = Active();
            if (document == null) return;
            ObjectId networkId = PromptNetwork(document);
            if (networkId.IsNull) return;
            List<SurfaceChoice> surfaces = WorkflowRepairCommands.ReadSurfaceChoices(document);
            string[] surfaceNames = new[] { "<No surface / skip cover>" }.Concat(surfaces.Select(item => item.Name)).ToArray();
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Sewer Engineering Limits Audit",
                "Review full-network cover, pipe slopes and structure drops. The command reports observed ranges and violations without modifying the network.");
            settings.AddChoice("Surface", "01 Cover", "Cover surface", surfaceNames[0], "Surface used to sample cover along pipe lengths.", surfaceNames);
            settings.AddPositiveDouble("MinCover", "01 Cover", "Minimum cover", 0.8, "Minimum permitted cover.");
            settings.AddPositiveDouble("MaxCover", "01 Cover", "Maximum cover", 6.0, "Maximum permitted cover.");
            settings.AddPositiveDouble("MinSlope", "02 Pipe", "Minimum pipe slope (%)", 0.5, "Minimum absolute pipe grade.");
            settings.AddPositiveDouble("MaxSlope", "02 Pipe", "Maximum pipe slope (%)", 15.0, "Maximum absolute pipe grade.");
            settings.AddPositiveDouble("MinDrop", "03 Structures", "Minimum manhole drop", 0.0, "Minimum end-elevation difference between connected pipes at a structure.");
            settings.AddPositiveDouble("MaxDrop", "03 Structures", "Maximum manhole drop", 1.0, "Maximum end-elevation difference between connected pipes at a structure.");
            settings.AddPositiveInteger("Samples", "01 Cover", "Cover samples per pipe", 10, "Number of equally spaced cover samples along each pipe.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            SurfaceChoice surfaceChoice = surfaces.FirstOrDefault(item => string.Equals(item.Name, settings.Text("Surface"), StringComparison.OrdinalIgnoreCase));
            SewerAuditResult audit = AuditNetwork(document, networkId, surfaceChoice == null ? ObjectId.Null : surfaceChoice.ObjectId, settings);
            PopupTablePresenter.ShowReview(
                "CE Tools - Sewer Limits Audit",
                "Observed values are based on the current network geometry. Cover is sampled along each pipe when a surface is selected.",
                new List<KeyValuePair<string, string>>
                {
                    Pair("Pipes checked", audit.Pipes.ToString(CultureInfo.CurrentCulture)),
                    Pair("Structures checked", audit.Structures.ToString(CultureInfo.CurrentCulture)),
                    Pair("Cover range", RangeText(audit.MinCover, audit.MaxCover)),
                    Pair("Cover violations", audit.CoverViolations.ToString(CultureInfo.CurrentCulture)),
                    Pair("Pipe slope range (%)", RangeText(audit.MinSlope, audit.MaxSlope)),
                    Pair("Slope violations", audit.SlopeViolations.ToString(CultureInfo.CurrentCulture)),
                    Pair("Structure drop range", RangeText(audit.MinDrop, audit.MaxDrop)),
                    Pair("Drop violations", audit.DropViolations.ToString(CultureInfo.CurrentCulture)),
                    Pair("Open pipe endpoints", audit.OpenEndpoints.ToString(CultureInfo.CurrentCulture))
                },
                "Close");
        }

        // -----------------------------------------------------------------
        // Platform field additions
        // -----------------------------------------------------------------
        [CommandMethod("CE_TOOLS", "CE_PLATFORMFIXEDMINSLOPE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void PlatformFixedMinimumSlope()
        {
            Document document = Active();
            if (document == null) return;
            var referenceOptions = new PromptEntityOptions("\nSelect reference feature line: ");
            referenceOptions.SetRejectMessage("\nSelect a Civil 3D feature line.");
            referenceOptions.AddAllowedClass(typeof(CivilFeatureLine), false);
            PromptEntityResult referenceResult = document.Editor.GetEntity(referenceOptions);
            if (referenceResult.Status != PromptStatus.OK) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Platform Fixed / Minimum Slope",
                "Apply fixed or minimum slope to multiple feature lines relative to the selected reference. Optional intermediate elevation points are inserted before grading.");
            settings.AddChoice("Mode", "01 Grade", "Slope rule", "Fixed slope", "Fixed forces the calculated slope; Minimum keeps a target already steeper in the requested direction.", new[] { "Fixed slope", "Minimum slope" });
            settings.AddPositiveDouble("Slope", "01 Grade", "Slope (%)", 2.0, "Positive slope magnitude in percent.");
            settings.AddChoice("Direction", "01 Grade", "Direction", "Fall away from reference", "Fall away lowers targets with distance; fall toward raises them.", new[] { "Fall away from reference", "Fall toward reference" });
            settings.AddChoice("Intermediate", "02 Points", "Add intermediate points", "Yes", "Insert elevation points on long target segments before applying the slope.", new[] { "Yes", "No" });
            settings.AddPositiveDouble("Spacing", "02 Points", "Maximum intermediate spacing", 5.0, "Approximate maximum plan spacing between target control/elevation points.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            PromptSelectionResult selection = Select(document.Editor, "\nSelect target feature lines: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            double grade = Math.Abs(settings.Double("Slope", 2.0)) / 100.0;
            bool minimum = string.Equals(settings.Text("Mode"), "Minimum slope", StringComparison.OrdinalIgnoreCase);
            bool away = string.Equals(settings.Text("Direction"), "Fall away from reference", StringComparison.OrdinalIgnoreCase);
            bool addIntermediate = string.Equals(settings.Text("Intermediate"), "Yes", StringComparison.OrdinalIgnoreCase);
            double spacing = Math.Max(settings.Double("Spacing", 5.0), 0.01);
            int changed = 0;
            int skipped = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine reference = transaction.GetObject(referenceResult.ObjectId, OpenMode.ForRead, false) as CivilFeatureLine;
                if (reference == null) return;
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    CivilFeatureLine target = null;
                    try { target = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilFeatureLine; } catch { }
                    if (target == null || target.IsReferenceObject || target.ObjectId == reference.ObjectId) { skipped++; continue; }
                    try
                    {
                        if (addIntermediate) InsertIntermediatePoints(target, spacing);
                        Point3dCollection points = target.GetPoints(FeatureLinePointType.AllPoints);
                        for (int index = 0; index < points.Count; index++)
                        {
                            Point3d point = points[index];
                            Point3d referencePoint = reference.GetClosestPointTo(new Point3d(point.X, point.Y, 0.0), Vector3d.ZAxis, false);
                            double distance = PlanDistance(point, referencePoint);
                            double required = referencePoint.Z + (away ? -1.0 : 1.0) * distance * grade;
                            double z = required;
                            if (minimum)
                                z = away ? Math.Min(point.Z, required) : Math.Max(point.Z, required);
                            target.SetPointElevation(index, z);
                        }
                        changed++;
                    }
                    catch { skipped++; }
                }
                transaction.Commit();
            }
            August21DisplayRefresh.Flush(document);
            PlatformDynamicRefreshManager.Queue();
            document.Editor.WriteMessage("\nCE_PLATFORMFIXEDMINSLOPE complete. Feature lines changed={0}; skipped={1}; intermediate points={2}.", changed, skipped, addIntermediate ? "Yes" : "No");
        }

        [CommandMethod("CE_TOOLS", "CE_FLRELLINKEXISTING", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void LinkExistingFeatureLines()
        {
            Document document = Active();
            if (document == null) return;
            PromptEntityOptions sourceOptions = new PromptEntityOptions("\nSelect SOURCE feature line: ");
            sourceOptions.SetRejectMessage("\nSelect a Civil 3D feature line.");
            sourceOptions.AddAllowedClass(typeof(CivilFeatureLine), false);
            PromptEntityResult sourceResult = document.Editor.GetEntity(sourceOptions);
            if (sourceResult.Status != PromptStatus.OK) return;
            PromptSelectionResult selection = Select(document.Editor, "\nSelect existing feature lines to link to that source: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            int linked = 0;
            int skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine source = transaction.GetObject(sourceResult.ObjectId, OpenMode.ForRead, false) as CivilFeatureLine;
                if (source == null) return;
                int sequence = 1;
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    CivilFeatureLine child = null;
                    try { child = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilFeatureLine; } catch { }
                    if (child == null || child.ObjectId == source.ObjectId || child.IsReferenceObject) { skipped++; continue; }
                    double horizontal;
                    double vertical;
                    if (!MeasureFeatureRelation(source, child, out horizontal, out vertical)) { skipped++; continue; }
                    WriteFeatureRelation(child, transaction, source.Handle.ToString(), horizontal, vertical, sequence++);
                    linked++;
                }
                transaction.Commit();
            }
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage("\nCE_FLRELLINKEXISTING complete. Linked children={0}; skipped={1}. Existing CE linked-feature-line refresh now owns these relationships.", linked, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_FLRELADOPT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void AdoptFeatureRelations()
        {
            Document document = Active();
            if (document == null) return;
            PromptSelectionResult selection = Select(document.Editor, "\nSelect linked stepped feature lines whose current edited relationship must become the new saved relationship: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            int adopted = 0;
            int skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    CivilFeatureLine child = null;
                    try { child = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilFeatureLine; } catch { }
                    string sourceHandle;
                    int sequence;
                    if (child == null || !TryReadFeatureRelation(child, transaction, out sourceHandle, out sequence)) { skipped++; continue; }
                    ObjectId sourceId = ResolveHandle(document.Database, sourceHandle);
                    CivilFeatureLine source = sourceId.IsNull ? null : transaction.GetObject(sourceId, OpenMode.ForRead, false) as CivilFeatureLine;
                    double horizontal;
                    double vertical;
                    if (source == null || !MeasureFeatureRelation(source, child, out horizontal, out vertical)) { skipped++; continue; }
                    WriteFeatureRelation(child, transaction, sourceHandle, horizontal, vertical, sequence);
                    adopted++;
                }
                transaction.Commit();
            }
            document.Editor.WriteMessage("\nCE_FLRELADOPT complete. Changed relationships adopted={0}; skipped={1}. Future source refreshes use the adopted offsets instead of snapping back to the old values.", adopted, skipped);
        }

        // -----------------------------------------------------------------
        // Dynamic feature-line slope arrows / Site Grid presentation
        // -----------------------------------------------------------------
        [CommandMethod("CE_TOOLS", "CE_FEATURELINESLOPEARROWS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FeatureLineSlopeArrows()
        {
            Document document = Active();
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Dynamic Feature-Line Slope Arrows",
                "Create an arrow and slope percentage for every consecutive feature-line point pair. The links refresh after watched source feature lines are edited.");
            settings.AddPaperHeight("TextHeight", "01 Annotation", "Paper text height", 2.5, "Annotative paper text height.");
            settings.AddPositiveDouble("Arrow", "01 Annotation", "Arrow-head length", 1.0, "Drawing-unit arrow-head length.");
            settings.AddPositiveInteger("Colour", "01 Annotation", "Colour index", 2, "AutoCAD colour index 1-255.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            PromptSelectionResult selection = Select(document.Editor, "\nSelect multiple feature lines for linked slope arrows: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            var sourceIds = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    CivilFeatureLine featureLine = null;
                    try { featureLine = transaction.GetObject(id, OpenMode.ForRead, false) as CivilFeatureLine; } catch { }
                    if (featureLine != null) sourceIds.Add(id);
                }
            }
            if (sourceIds.Count == 0) return;
            int created = RebuildSlopeAnnotations(document, sourceIds, settings.Double("TextHeight", 2.5), settings.Double("Arrow", 1.0), ClampColour(settings.Integer("Colour", 2)), true);
            August24SlopeDynamicManager.Watch(document, sourceIds.Select(id => id.Handle.ToString()));
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage("\nCE_FEATURELINESLOPEARROWS complete. Linked annotation entities created={0}. Only edits to watched source feature lines queue this dynamic refresh.", created);
        }

        [CommandMethod("CE_TOOLS", "CE_SLOPEARROWSREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshSlopeArrows()
        {
            Document document = Active();
            if (document == null) return;
            int refreshed = RefreshSlopeAnnotations(document);
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage("\nCE_SLOPEARROWSREFRESH complete. Linked slope annotation entities refreshed={0}.", refreshed);
        }

        [CommandMethod("CE_TOOLS", "CE_SITEGRIDPRESENTATION", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SiteGridPresentation()
        {
            Document document = Active();
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Site Grid Presentation",
                "Apply one colour and true annotative paper text height to linked Site Grid lines, points and labels. Grid geometry remains linked to its boundary.");
            settings.AddPositiveInteger("Colour", "01 Display", "Colour index", 3, "AutoCAD colour index 1-255.");
            settings.AddPaperHeight("TextHeight", "01 Display", "Paper text height", 2.5, "Absolute annotative paper text height.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            PromptSelectionResult selection = Select(document.Editor, "\nSelect one or more linked Site Grid boundary polylines: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    Polyline boundary = null;
                    try { boundary = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; } catch { }
                    if (boundary != null) handles.Add(boundary.Handle.ToString());
                }
            }
            int changed = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                foreach (ObjectId id in model)
                {
                    Entity entity = null;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { }
                    string parent;
                    if (entity == null || !TryReadGridParent(entity, transaction, out parent) || !handles.Contains(parent)) continue;
                    entity.UpgradeOpen();
                    entity.ColorIndex = (short)ClampColour(settings.Integer("Colour", 3));
                    MText text = entity as MText;
                    if (text != null)
                    {
                        text.TextHeight = PaperAnnotationScale.AnnotativeTextHeight(document.Database, settings.Double("TextHeight", 2.5));
                        PaperAnnotationScale.SetAnnotative(text);
                    }
                    try { entity.RecordGraphicsModified(true); } catch { }
                    changed++;
                }
                transaction.Commit();
            }
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage("\nCE_SITEGRIDPRESENTATION complete. Linked Site Grid children changed={0}. Grid lines remain full-frame and labels remain linked to their boundary.", changed);
        }

        [CommandMethod("CE_TOOLS", "CE_SURFACESLOPEARROWS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SurfaceSlopeArrows()
        {
            Document document = Active();
            if (document == null) return;
            PromptEntityOptions surfaceOptions = new PromptEntityOptions("\nSelect Civil 3D surface for slope arrows: ");
            surfaceOptions.SetRejectMessage("\nSelect a Civil 3D surface.");
            surfaceOptions.AddAllowedClass(typeof(CivilSurface), false);
            PromptEntityResult selected = document.Editor.GetEntity(surfaceOptions);
            if (selected.Status != PromptStatus.OK) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Surface Slope Arrows",
                "Sample the selected surface on a regular grid and show the local steepest-downhill slope direction and percentage.");
            settings.AddPositiveDouble("Spacing", "01 Sampling", "Grid spacing", 20.0, "Plan spacing between sampled arrows.");
            settings.AddPositiveDouble("Arrow", "02 Annotation", "Arrow length", 8.0, "Drawing-unit arrow length.");
            settings.AddPaperHeight("TextHeight", "02 Annotation", "Paper text height", 2.5, "Annotative paper height.");
            settings.AddPositiveInteger("Colour", "02 Annotation", "Colour index", 2, "AutoCAD colour index 1-255.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            int created = CreateSurfaceSlopeArrows(document, selected.ObjectId, settings.Double("Spacing", 20.0), settings.Double("Arrow", 8.0), settings.Double("TextHeight", 2.5), ClampColour(settings.Integer("Colour", 2)));
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage("\nCE_SURFACESLOPEARROWS complete. Sampled arrow sets={0}.", created);
        }

        // -----------------------------------------------------------------
        // Road side hatch + elevation matches
        // -----------------------------------------------------------------
        [CommandMethod("CE_TOOLS", "CE_ROADHATCHSIDES", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RoadHatchSides()
        {
            Document document = Active();
            if (document == null) return;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Road Side Hatch",
                "Create hatch strips along multiple road polylines or Civil 3D alignments. Choose side, width, pattern, scale and colour.");
            settings.AddPositiveDouble("Distance", "01 Geometry", "Hatch width / offset distance", 5.0, "Width of the hatch strip from the selected road line.");
            settings.AddChoice("Side", "01 Geometry", "Side", "Both", "Hatch the left side, right side or both sides.", new[] { "Left", "Right", "Both" });
            settings.AddText("Pattern", "02 Hatch", "Hatch pattern", "ANSI31", "Installed AutoCAD hatch pattern name.");
            settings.AddPositiveDouble("Scale", "02 Hatch", "Hatch scale", 1.0, "Hatch pattern scale.");
            settings.AddPositiveInteger("Colour", "02 Hatch", "Colour index", 8, "AutoCAD colour index 1-255.");
            settings.AddPositiveDouble("Sample", "03 Alignments", "Alignment sample interval", 5.0, "Plan sampling interval when an alignment is selected.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            PromptSelectionResult selection = Select(document.Editor, "\nSelect multiple road polylines and/or alignments: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            int created = 0;
            int skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId hatchLayer = GetOrCreateLayer(document.Database, transaction, RoadHatchLayer);
                ObjectId boundaryLayer = GetOrCreateLayer(document.Database, transaction, RoadHatchBoundaryLayer);
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    Entity entity = null;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { }
                    using (Polyline source = BuildRoadPolyline(entity, settings.Double("Sample", 5.0)))
                    {
                        if (source == null || source.NumberOfVertices < 2 || source.Closed) { skipped++; continue; }
                        string side = settings.Text("Side");
                        if (string.Equals(side, "Left", StringComparison.OrdinalIgnoreCase) || string.Equals(side, "Both", StringComparison.OrdinalIgnoreCase))
                            if (CreateHatchStrip(document.Database, transaction, space, source, settings.Double("Distance", 5.0), settings.Text("Pattern"), settings.Double("Scale", 1.0), ClampColour(settings.Integer("Colour", 8)), hatchLayer, boundaryLayer)) created++; else skipped++;
                        if (string.Equals(side, "Right", StringComparison.OrdinalIgnoreCase) || string.Equals(side, "Both", StringComparison.OrdinalIgnoreCase))
                            if (CreateHatchStrip(document.Database, transaction, space, source, -settings.Double("Distance", 5.0), settings.Text("Pattern"), settings.Double("Scale", 1.0), ClampColour(settings.Integer("Colour", 8)), hatchLayer, boundaryLayer)) created++; else skipped++;
                    }
                }
                transaction.Commit();
            }
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage("\nCE_ROADHATCHSIDES complete. Hatch strips created={0}; skipped={1}.", created, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADELEVMATCH", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RoadElevationMatch()
        {
            Document document = Active();
            if (document == null) return;
            PromptEntityOptions masterOptions = new PromptEntityOptions("\nSelect MASTER road feature line whose elevations must control crossings: ");
            masterOptions.SetRejectMessage("\nSelect a Civil 3D feature line.");
            masterOptions.AddAllowedClass(typeof(CivilFeatureLine), false);
            PromptEntityResult masterResult = document.Editor.GetEntity(masterOptions);
            if (masterResult.Status != PromptStatus.OK) return;
            PromptSelectionResult targets = Select(document.Editor, "\nSelect crossing/T-junction road feature lines to match to the master: ");
            if (targets.Status != PromptStatus.OK || targets.Value == null) return;
            int linked = WriteRoadElevationLinks(document, masterResult.ObjectId, targets.Value.GetObjectIds());
            int refreshed = RefreshRoadElevationMatches(document);
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage("\nCE_ROADELEVMATCH complete. Target links stored={0}; crossing elevation points refreshed={1}. Use CE_ROADELEVMATCHREFRESH after vertical edits.", linked, refreshed);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADELEVMATCHREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RoadElevationMatchRefresh()
        {
            Document document = Active();
            if (document == null) return;
            int refreshed = RefreshRoadElevationMatches(document);
            August21DisplayRefresh.Flush(document);
            document.Editor.WriteMessage("\nCE_ROADELEVMATCHREFRESH complete. Crossing elevation points refreshed={0}.", refreshed);
        }

        // -----------------------------------------------------------------
        // Helpers - menus / selections / sewer
        // -----------------------------------------------------------------
        private static Document Active() { return AcApplication.DocumentManager.MdiActiveDocument; }

        private static void RunMenu(string title, string description, List<DisciplineWorkflowAction> actions)
        {
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(document, title, description, actions);
        }

        private static DisciplineWorkflowAction A(string title, string command, string description, string group)
        {
            return new DisciplineWorkflowAction(title, command, description, group);
        }

        private static PromptSelectionResult Select(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
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

        private static List<ObjectId> ReadStructureScope(Document document, string scope)
        {
            var result = new List<ObjectId>();
            if (string.Equals(scope, "Whole selected network", StringComparison.OrdinalIgnoreCase))
            {
                ObjectId networkId = PromptNetwork(document);
                if (networkId.IsNull) return result;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilNetwork network = transaction.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                    if (network != null) result.AddRange(network.GetStructureIds().Cast<ObjectId>());
                }
                return result;
            }
            PromptSelectionResult selection = Select(document.Editor, "\nSelect sewer structures/manholes: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return result;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    try { if (transaction.GetObject(id, OpenMode.ForRead, false) is CivilStructure) result.Add(id); } catch { }
                }
            }
            return result;
        }

        private static bool ReadPipeStructureScope(Document document, string scope, out List<ObjectId> pipes, out List<ObjectId> structures)
        {
            pipes = new List<ObjectId>();
            structures = new List<ObjectId>();
            if (string.Equals(scope, "Whole selected network", StringComparison.OrdinalIgnoreCase))
            {
                ObjectId networkId = PromptNetwork(document);
                if (networkId.IsNull) return false;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilNetwork network = transaction.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null) return false;
                    pipes.AddRange(network.GetPipeIds().Cast<ObjectId>());
                    structures.AddRange(network.GetStructureIds().Cast<ObjectId>());
                }
                return true;
            }
            PromptSelectionResult selection = Select(document.Editor, "\nSelect sewer pipes and structures: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null) return false;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    try
                    {
                        DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                        if (value is CivilPipe) pipes.Add(id);
                        else if (value is CivilStructure) structures.Add(id);
                    }
                    catch { }
                }
            }
            return pipes.Count > 0 && structures.Count > 0;
        }

        private static ObjectId PromptNetwork(Document document)
        {
            PromptEntityOptions options = new PromptEntityOptions("\nSelect one sewer pipe or structure from the required network: ");
            PromptEntityResult selected = document.Editor.GetEntity(options);
            if (selected.Status != PromptStatus.OK) return ObjectId.Null;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                DBObject value = transaction.GetObject(selected.ObjectId, OpenMode.ForRead, false);
                CivilPipe pipe = value as CivilPipe;
                if (pipe != null) return pipe.NetworkId;
                CivilStructure structure = value as CivilStructure;
                return structure == null ? ObjectId.Null : structure.NetworkId;
            }
        }

        private static CivilStructure NearestStructure(Point3d point, ObjectId networkId, IEnumerable<CivilStructure> structures, double maximum)
        {
            CivilStructure best = null;
            double distance = double.PositiveInfinity;
            foreach (CivilStructure structure in structures)
            {
                if (structure == null || structure.NetworkId != networkId) continue;
                double current = PlanDistance(point, structure.Position);
                if (current < distance && current <= maximum) { distance = current; best = structure; }
            }
            return best;
        }

        private static SewerAuditResult AuditNetwork(Document document, ObjectId networkId, ObjectId surfaceId, ProductionSettingsDialogModel settings)
        {
            var result = new SewerAuditResult();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilNetwork network = transaction.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                CivilSurface surface = surfaceId.IsNull ? null : transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                var pipeEndpointElevations = new Dictionary<ObjectId, List<double>>();
                int samples = Math.Max(2, settings.Integer("Samples", 10));
                foreach (ObjectId pipeId in network.GetPipeIds())
                {
                    CivilPipe pipe = transaction.GetObject(pipeId, OpenMode.ForRead, false) as CivilPipe;
                    if (pipe == null) continue;
                    result.Pipes++;
                    double run = PlanDistance(pipe.StartPoint, pipe.EndPoint);
                    if (run > Tol)
                    {
                        double slope = Math.Abs(pipe.EndPoint.Z - pipe.StartPoint.Z) / run * 100.0;
                        result.AddSlope(slope);
                        if (slope < settings.Double("MinSlope", 0.5) || slope > settings.Double("MaxSlope", 15.0)) result.SlopeViolations++;
                    }
                    if (pipe.StartStructureId.IsNull) result.OpenEndpoints++; else AddEndpoint(pipeEndpointElevations, pipe.StartStructureId, pipe.StartPoint.Z);
                    if (pipe.EndStructureId.IsNull) result.OpenEndpoints++; else AddEndpoint(pipeEndpointElevations, pipe.EndStructureId, pipe.EndPoint.Z);
                    if (surface != null)
                    {
                        double radius = ReadPipeRadius(pipe);
                        for (int index = 0; index <= samples; index++)
                        {
                            double fraction = index / (double)samples;
                            try
                            {
                                Point3d point = pipe.GetPointAtParam(fraction);
                                double cover = surface.FindElevationAtXY(point.X, point.Y) - (point.Z + radius);
                                result.AddCover(cover);
                                if (cover < settings.Double("MinCover", 0.8) || cover > settings.Double("MaxCover", 6.0)) result.CoverViolations++;
                            }
                            catch { }
                        }
                    }
                }
                foreach (ObjectId structureId in network.GetStructureIds())
                {
                    result.Structures++;
                    List<double> elevations;
                    if (!pipeEndpointElevations.TryGetValue(structureId, out elevations) || elevations.Count < 2) continue;
                    double drop = elevations.Max() - elevations.Min();
                    result.AddDrop(drop);
                    if (drop < settings.Double("MinDrop", 0.0) || drop > settings.Double("MaxDrop", 1.0)) result.DropViolations++;
                }
            }
            return result;
        }

        private static void AddEndpoint(IDictionary<ObjectId, List<double>> values, ObjectId id, double elevation)
        {
            List<double> list;
            if (!values.TryGetValue(id, out list)) { list = new List<double>(); values[id] = list; }
            list.Add(elevation);
        }

        private static double ReadPipeRadius(CivilPipe pipe)
        {
            foreach (string name in new[] { "InnerDiameterOrWidth", "OuterDiameterOrWidth" })
            {
                try
                {
                    PropertyInfo property = pipe.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                    if (property == null || !property.CanRead) continue;
                    double diameter = Convert.ToDouble(property.GetValue(pipe, null), CultureInfo.InvariantCulture);
                    if (diameter > 0.0) return diameter * 0.5;
                }
                catch { }
            }
            return 0.0;
        }

        // -----------------------------------------------------------------
        // Helpers - feature relationships
        // -----------------------------------------------------------------
        private static void InsertIntermediatePoints(CivilFeatureLine featureLine, double spacing)
        {
            Point3dCollection before = featureLine.GetPoints(FeatureLinePointType.AllPoints);
            var inserts = new List<Point3d>();
            for (int index = 0; index + 1 < before.Count; index++)
            {
                Point3d first = before[index];
                Point3d second = before[index + 1];
                double length = PlanDistance(first, second);
                int count = Math.Max(0, (int)Math.Ceiling(length / spacing) - 1);
                for (int step = 1; step <= count; step++)
                {
                    double f = step / (double)(count + 1);
                    inserts.Add(new Point3d(first.X + (second.X - first.X) * f, first.Y + (second.Y - first.Y) * f, first.Z + (second.Z - first.Z) * f));
                }
            }
            foreach (Point3d point in inserts)
            {
                try { featureLine.InsertElevationPoint(point); } catch { }
            }
        }

        private static bool MeasureFeatureRelation(CivilFeatureLine source, CivilFeatureLine child, out double horizontal, out double vertical)
        {
            horizontal = 0.0;
            vertical = 0.0;
            try
            {
                Point3dCollection childPoints = child.GetPoints(FeatureLinePointType.AllPoints);
                if (childPoints == null || childPoints.Count == 0) return false;
                double horizontalTotal = 0.0;
                double verticalTotal = 0.0;
                int count = 0;
                foreach (Point3d point in childPoints)
                {
                    Point3d closest = source.GetClosestPointTo(new Point3d(point.X, point.Y, 0.0), Vector3d.ZAxis, false);
                    horizontalTotal += PlanDistance(point, closest);
                    verticalTotal += point.Z - closest.Z;
                    count++;
                }
                double magnitude = horizontalTotal / Math.Max(1, count);
                Point3d sample = childPoints[childPoints.Count / 2];
                Point3d sourcePoint = source.GetClosestPointTo(new Point3d(sample.X, sample.Y, 0.0), Vector3d.ZAxis, false);
                Vector3d tangent;
                try { tangent = source.GetFirstDerivative(sourcePoint); } catch { tangent = Vector3d.XAxis; }
                Vector3d delta = sample - sourcePoint;
                double cross = tangent.X * delta.Y - tangent.Y * delta.X;
                horizontal = cross >= 0.0 ? magnitude : -magnitude;
                vertical = verticalTotal / Math.Max(1, count);
                return true;
            }
            catch { return false; }
        }

        private static void WriteFeatureRelation(CivilFeatureLine child, Transaction transaction, string sourceHandle, double horizontal, double vertical, int sequence)
        {
            WriteRecord(child, transaction, FeatureRelationKey, new[]
            {
                new TypedValue((int)DxfCode.Text, sourceHandle),
                new TypedValue((int)DxfCode.Real, horizontal),
                new TypedValue((int)DxfCode.Real, vertical),
                new TypedValue((int)DxfCode.Int32, sequence)
            });
        }

        private static bool TryReadFeatureRelation(CivilFeatureLine child, Transaction transaction, out string sourceHandle, out int sequence)
        {
            sourceHandle = string.Empty;
            sequence = 0;
            TypedValue[] values;
            if (!TryReadRecord(child, transaction, FeatureRelationKey, out values) || values.Length < 4) return false;
            sourceHandle = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture);
            sequence = Convert.ToInt32(values[3].Value, CultureInfo.InvariantCulture);
            return !string.IsNullOrWhiteSpace(sourceHandle);
        }

        // -----------------------------------------------------------------
        // Helpers - slope annotations
        // -----------------------------------------------------------------
        private static int RebuildSlopeAnnotations(Document document, IEnumerable<ObjectId> sourceIds, double paperHeight, double arrowHead, int colour, bool eraseExisting)
        {
            var handles = new HashSet<string>(sourceIds.Select(id => id.Handle.ToString()), StringComparer.OrdinalIgnoreCase);
            int created = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                if (eraseExisting) EraseSlopeChildren(space, transaction, handles);
                foreach (ObjectId sourceId in sourceIds.Distinct())
                {
                    CivilFeatureLine source = null;
                    try { source = transaction.GetObject(sourceId, OpenMode.ForRead, false) as CivilFeatureLine; } catch { }
                    if (source == null) continue;
                    Point3dCollection points = source.GetPoints(FeatureLinePointType.AllPoints);
                    for (int index = 0; index + 1 < points.Count; index++)
                    {
                        SlopeGeometry geometry;
                        if (!TrySlopeGeometry(points[index], points[index + 1], arrowHead, out geometry)) continue;
                        string handle = source.Handle.ToString();
                        created += AppendSlopeEntity(document.Database, transaction, space, new Line(geometry.High, geometry.Low), handle, index, "MAIN", colour);
                        created += AppendSlopeEntity(document.Database, transaction, space, new Line(geometry.Low, geometry.Head1), handle, index, "HEAD1", colour);
                        created += AppendSlopeEntity(document.Database, transaction, space, new Line(geometry.Low, geometry.Head2), handle, index, "HEAD2", colour);
                        var text = new MText();
                        text.SetDatabaseDefaults(document.Database);
                        text.Location = geometry.TextPoint;
                        text.TextHeight = PaperAnnotationScale.AnnotativeTextHeight(document.Database, paperHeight);
                        text.Contents = geometry.Slope.ToString("0.##", CultureInfo.InvariantCulture) + "%";
                        text.Rotation = geometry.Rotation;
                        text.Attachment = AttachmentPoint.MiddleCenter;
                        PaperAnnotationScale.SetAnnotative(text);
                        created += AppendSlopeEntity(document.Database, transaction, space, text, handle, index, "TEXT", colour);
                    }
                }
                transaction.Commit();
            }
            return created;
        }

        private static int RefreshSlopeAnnotations(Document document)
        {
            int refreshed = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                foreach (ObjectId id in model.Cast<ObjectId>().ToList())
                {
                    Entity entity = null;
                    try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { }
                    SlopeLink link;
                    if (entity == null || !TryReadSlopeLink(entity, transaction, out link)) continue;
                    ObjectId sourceId = ResolveHandle(document.Database, link.SourceHandle);
                    CivilFeatureLine source = sourceId.IsNull ? null : transaction.GetObject(sourceId, OpenMode.ForRead, false) as CivilFeatureLine;
                    if (source == null) continue;
                    Point3dCollection points = source.GetPoints(FeatureLinePointType.AllPoints);
                    if (link.Segment < 0 || link.Segment + 1 >= points.Count) continue;
                    SlopeGeometry geometry;
                    if (!TrySlopeGeometry(points[link.Segment], points[link.Segment + 1], Math.Max(0.001, link.ArrowHead), out geometry)) continue;
                    entity.UpgradeOpen();
                    Line line = entity as Line;
                    if (line != null)
                    {
                        if (link.Role == "MAIN") { line.StartPoint = geometry.High; line.EndPoint = geometry.Low; }
                        else if (link.Role == "HEAD1") { line.StartPoint = geometry.Low; line.EndPoint = geometry.Head1; }
                        else if (link.Role == "HEAD2") { line.StartPoint = geometry.Low; line.EndPoint = geometry.Head2; }
                    }
                    MText text = entity as MText;
                    if (text != null)
                    {
                        text.Location = geometry.TextPoint;
                        text.Contents = geometry.Slope.ToString("0.##", CultureInfo.InvariantCulture) + "%";
                        text.Rotation = geometry.Rotation;
                    }
                    try { entity.RecordGraphicsModified(true); } catch { }
                    refreshed++;
                }
                transaction.Commit();
            }
            return refreshed;
        }

        internal static int RefreshSlopeAnnotationsForDynamic(Document document)
        {
            return RefreshSlopeAnnotations(document);
        }

        private static int AppendSlopeEntity(Database database, Transaction transaction, BlockTableRecord space, Entity entity, string sourceHandle, int segment, string role, int colour)
        {
            entity.SetDatabaseDefaults(database);
            entity.ColorIndex = (short)colour;
            space.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
            double arrowHead = 1.0;
            Line line = entity as Line;
            if (line != null && role != "MAIN") arrowHead = line.Length;
            WriteRecord(entity, transaction, SlopeLinkKey, new[]
            {
                new TypedValue((int)DxfCode.Text, sourceHandle),
                new TypedValue((int)DxfCode.Int32, segment),
                new TypedValue((int)DxfCode.Text, role),
                new TypedValue((int)DxfCode.Real, arrowHead)
            });
            return 1;
        }

        private static void EraseSlopeChildren(BlockTableRecord space, Transaction transaction, ISet<string> handles)
        {
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                Entity entity = null;
                try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; } catch { }
                SlopeLink link;
                if (entity == null || !TryReadSlopeLink(entity, transaction, out link) || !handles.Contains(link.SourceHandle)) continue;
                entity.UpgradeOpen();
                entity.Erase();
            }
        }

        private static bool TryReadSlopeLink(Entity entity, Transaction transaction, out SlopeLink link)
        {
            link = new SlopeLink();
            TypedValue[] values;
            if (!TryReadRecord(entity, transaction, SlopeLinkKey, out values) || values.Length < 4) return false;
            try
            {
                link.SourceHandle = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture);
                link.Segment = Convert.ToInt32(values[1].Value, CultureInfo.InvariantCulture);
                link.Role = Convert.ToString(values[2].Value, CultureInfo.InvariantCulture);
                link.ArrowHead = Convert.ToDouble(values[3].Value, CultureInfo.InvariantCulture);
                return !string.IsNullOrWhiteSpace(link.SourceHandle);
            }
            catch { return false; }
        }

        private static bool TrySlopeGeometry(Point3d first, Point3d second, double arrowHead, out SlopeGeometry geometry)
        {
            geometry = new SlopeGeometry();
            double run = PlanDistance(first, second);
            if (run <= Tol) return false;
            Point3d high = first.Z >= second.Z ? first : second;
            Point3d low = first.Z >= second.Z ? second : first;
            Vector3d direction = new Vector3d(low.X - high.X, low.Y - high.Y, 0.0).GetNormal();
            Vector3d back = -direction;
            Vector3d normal = new Vector3d(-direction.Y, direction.X, 0.0);
            double head = Math.Min(Math.Max(arrowHead, 0.001), run * 0.35);
            geometry.High = new Point3d(high.X, high.Y, high.Z);
            geometry.Low = new Point3d(low.X, low.Y, low.Z);
            geometry.Head1 = geometry.Low + back * head + normal * head * 0.45;
            geometry.Head2 = geometry.Low + back * head - normal * head * 0.45;
            geometry.TextPoint = new Point3d((high.X + low.X) * 0.5, (high.Y + low.Y) * 0.5, (high.Z + low.Z) * 0.5);
            geometry.Slope = Math.Abs(high.Z - low.Z) / run * 100.0;
            geometry.Rotation = Math.Atan2(direction.Y, direction.X);
            return true;
        }

        private static int CreateSurfaceSlopeArrows(Document document, ObjectId surfaceId, double spacing, double arrowLength, double paperHeight, int colour)
        {
            int created = 0;
            spacing = Math.Max(spacing, 0.1);
            arrowLength = Math.Max(arrowLength, 0.1);
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                Entity surfaceEntity = surface as Entity;
                if (surface == null || surfaceEntity == null) return 0;
                Extents3d extents;
                try { extents = surfaceEntity.GeometricExtents; } catch { return 0; }
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite, false) as BlockTableRecord;
                double delta = Math.Max(spacing * 0.15, 0.05);
                int guard = 0;
                for (double x = extents.MinPoint.X + spacing * 0.5; x < extents.MaxPoint.X && guard < 500; x += spacing)
                {
                    for (double y = extents.MinPoint.Y + spacing * 0.5; y < extents.MaxPoint.Y && guard < 500; y += spacing)
                    {
                        double z, zx1, zx2, zy1, zy2;
                        try
                        {
                            z = surface.FindElevationAtXY(x, y);
                            zx1 = surface.FindElevationAtXY(x - delta, y);
                            zx2 = surface.FindElevationAtXY(x + delta, y);
                            zy1 = surface.FindElevationAtXY(x, y - delta);
                            zy2 = surface.FindElevationAtXY(x, y + delta);
                        }
                        catch { continue; }
                        double gx = (zx2 - zx1) / (2.0 * delta);
                        double gy = (zy2 - zy1) / (2.0 * delta);
                        double magnitude = Math.Sqrt(gx * gx + gy * gy);
                        if (magnitude <= Tol) continue;
                        Vector3d down = new Vector3d(-gx, -gy, 0.0).GetNormal();
                        Point3d start = new Point3d(x, y, z);
                        Point3d end = start + down * arrowLength;
                        var line = new Line(start, end); line.SetDatabaseDefaults(document.Database); line.ColorIndex = (short)colour; space.AppendEntity(line); transaction.AddNewlyCreatedDBObject(line, true);
                        var text = new MText(); text.SetDatabaseDefaults(document.Database); text.ColorIndex = (short)colour; text.Location = new Point3d((start.X + end.X) * 0.5, (start.Y + end.Y) * 0.5, z); text.TextHeight = PaperAnnotationScale.AnnotativeTextHeight(document.Database, paperHeight); text.Contents = (magnitude * 100.0).ToString("0.##", CultureInfo.InvariantCulture) + "%"; text.Rotation = Math.Atan2(down.Y, down.X); text.Attachment = AttachmentPoint.MiddleCenter; PaperAnnotationScale.SetAnnotative(text); space.AppendEntity(text); transaction.AddNewlyCreatedDBObject(text, true);
                        created++; guard++;
                    }
                }
                transaction.Commit();
            }
            return created;
        }

        // -----------------------------------------------------------------
        // Helpers - road hatch / road elevation links
        // -----------------------------------------------------------------
        private static Polyline BuildRoadPolyline(Entity entity, double sampleInterval)
        {
            Polyline source = entity as Polyline;
            if (source != null)
            {
                var copy = new Polyline(source.NumberOfVertices);
                for (int index = 0; index < source.NumberOfVertices; index++) copy.AddVertexAt(index, source.GetPoint2dAt(index), source.GetBulgeAt(index), 0.0, 0.0);
                copy.Elevation = source.Elevation;
                copy.Closed = source.Closed;
                return copy;
            }
            CivilAlignment alignment = entity as CivilAlignment;
            if (alignment == null) return null;
            double start = ReadDouble(alignment, "StartingStation");
            double end = ReadDouble(alignment, "EndingStation");
            if (!(end > start)) return null;
            sampleInterval = Math.Max(sampleInterval, 0.25);
            int count = Math.Max(2, (int)Math.Ceiling((end - start) / sampleInterval));
            var polyline = new Polyline(count + 1);
            for (int index = 0; index <= count; index++)
            {
                double station = start + (end - start) * index / count;
                Point2d point;
                if (!TryAlignmentPoint(alignment, station, out point)) { polyline.Dispose(); return null; }
                polyline.AddVertexAt(index, point, 0.0, 0.0, 0.0);
            }
            return polyline;
        }

        private static bool TryAlignmentPoint(CivilAlignment alignment, double station, out Point2d point)
        {
            point = Point2d.Origin;
            try
            {
                MethodInfo method = alignment.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(item => item.Name == "PointLocation" && item.GetParameters().Length == 4);
                if (method == null) return false;
                object[] values = { station, 0.0, 0.0, 0.0 };
                method.Invoke(alignment, values);
                point = new Point2d(Convert.ToDouble(values[2], CultureInfo.InvariantCulture), Convert.ToDouble(values[3], CultureInfo.InvariantCulture));
                return true;
            }
            catch { return false; }
        }

        private static bool CreateHatchStrip(Database database, Transaction transaction, BlockTableRecord space, Polyline source, double offset, string pattern, double scale, int colour, ObjectId hatchLayer, ObjectId boundaryLayer)
        {
            DBObjectCollection offsets = null;
            try
            {
                offsets = source.GetOffsetCurves(offset);
                Polyline other = offsets.Cast<DBObject>().OfType<Polyline>().FirstOrDefault();
                if (other == null) return false;
                List<Point3d> first = SampleCurve(source, 60);
                List<Point3d> second = SampleCurve(other, 60);
                if (first.Count < 2 || second.Count < 2) return false;
                second.Reverse();
                var boundary = new Polyline(first.Count + second.Count);
                boundary.SetDatabaseDefaults(database);
                boundary.LayerId = boundaryLayer;
                int vertex = 0;
                foreach (Point3d point in first.Concat(second)) boundary.AddVertexAt(vertex++, new Point2d(point.X, point.Y), 0.0, 0.0, 0.0);
                boundary.Closed = true;
                ObjectId boundaryId = space.AppendEntity(boundary);
                transaction.AddNewlyCreatedDBObject(boundary, true);

                var hatch = new Hatch();
                hatch.SetDatabaseDefaults(database);
                hatch.LayerId = hatchLayer;
                hatch.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)colour);
                space.AppendEntity(hatch);
                transaction.AddNewlyCreatedDBObject(hatch, true);
                hatch.SetHatchPattern(HatchPatternType.PreDefined, string.IsNullOrWhiteSpace(pattern) ? "ANSI31" : pattern.Trim());
                if (!string.Equals(pattern, "SOLID", StringComparison.OrdinalIgnoreCase)) hatch.PatternScale = Math.Max(scale, 0.001);
                hatch.Associative = true;
                var loop = new ObjectIdCollection(); loop.Add(boundaryId); hatch.AppendLoop(HatchLoopTypes.Outermost, loop); hatch.EvaluateHatch(true);
                return true;
            }
            catch { return false; }
            finally
            {
                if (offsets != null)
                    foreach (DBObject value in offsets) try { if (value.Database == null) value.Dispose(); } catch { }
            }
        }

        private static List<Point3d> SampleCurve(Curve curve, int count)
        {
            var result = new List<Point3d>();
            count = Math.Max(2, count);
            for (int index = 0; index <= count; index++)
            {
                double parameter = curve.StartParam + (curve.EndParam - curve.StartParam) * index / count;
                try { result.Add(curve.GetPointAtParameter(parameter)); } catch { }
            }
            return result;
        }

        private const string RoadElevationLinkKey = "CE_ROAD_ELEVATION_LINK";

        private static int WriteRoadElevationLinks(Document document, ObjectId masterId, IEnumerable<ObjectId> targetIds)
        {
            int linked = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine master = transaction.GetObject(masterId, OpenMode.ForRead, false) as CivilFeatureLine;
                if (master == null) return 0;
                foreach (ObjectId id in targetIds.Distinct())
                {
                    CivilFeatureLine target = null;
                    try { target = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilFeatureLine; } catch { }
                    if (target == null || target.ObjectId == master.ObjectId || target.IsReferenceObject) continue;
                    WriteRecord(target, transaction, RoadElevationLinkKey, new[] { new TypedValue((int)DxfCode.Text, master.Handle.ToString()) });
                    linked++;
                }
                transaction.Commit();
            }
            return linked;
        }

        private static int RefreshRoadElevationMatches(Document document)
        {
            int refreshed = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                foreach (ObjectId id in model.Cast<ObjectId>().ToList())
                {
                    CivilFeatureLine target = null;
                    try { target = transaction.GetObject(id, OpenMode.ForRead, false) as CivilFeatureLine; } catch { }
                    TypedValue[] values;
                    if (target == null || !TryReadRecord(target, transaction, RoadElevationLinkKey, out values) || values.Length == 0) continue;
                    ObjectId masterId = ResolveHandle(document.Database, Convert.ToString(values[0].Value, CultureInfo.InvariantCulture));
                    CivilFeatureLine master = masterId.IsNull ? null : transaction.GetObject(masterId, OpenMode.ForRead, false) as CivilFeatureLine;
                    if (master == null) continue;
                    Point3dCollection intersections = new Point3dCollection();
                    try { ((Entity)master).IntersectWith((Entity)target, Intersect.OnBothOperands, intersections, IntPtr.Zero, IntPtr.Zero); } catch { }
                    if (intersections.Count == 0) continue;
                    target.UpgradeOpen();
                    foreach (Point3d intersection in intersections)
                    {
                        try
                        {
                            Point3d masterPoint = master.GetClosestPointTo(new Point3d(intersection.X, intersection.Y, 0.0), Vector3d.ZAxis, false);
                            Point3d targetPoint = target.GetClosestPointTo(new Point3d(intersection.X, intersection.Y, 0.0), Vector3d.ZAxis, false);
                            try { target.InsertElevationPoint(targetPoint); } catch { }
                            Point3dCollection points = target.GetPoints(FeatureLinePointType.AllPoints);
                            int nearest = ClosestPointIndex(points, targetPoint);
                            target.SetPointElevation(nearest, masterPoint.Z);
                            refreshed++;
                        }
                        catch { }
                    }
                }
                transaction.Commit();
            }
            return refreshed;
        }

        // -----------------------------------------------------------------
        // Generic helpers / records
        // -----------------------------------------------------------------
        private static int ClosestPointIndex(Point3dCollection points, Point3d target)
        {
            int best = 0; double distance = double.PositiveInfinity;
            for (int index = 0; index < points.Count; index++)
            {
                double current = PlanDistance(points[index], target);
                if (current < distance) { distance = current; best = index; }
            }
            return best;
        }

        private static ObjectId GetOrCreateLayer(Database database, Transaction transaction, string name)
        {
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId id = table.Add(layer); transaction.AddNewlyCreatedDBObject(layer, true); return id;
        }

        private static bool TryReadGridParent(Entity entity, Transaction transaction, out string parent)
        {
            parent = string.Empty;
            TypedValue[] values;
            if (!TryReadRecord(entity, transaction, August12SurveySiteGridCommands.ChildKey, out values) || values.Length == 0) return false;
            parent = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture);
            return !string.IsNullOrWhiteSpace(parent);
        }

        private static void WriteRecord(DBObject owner, Transaction transaction, string key, IEnumerable<TypedValue> values)
        {
            if (owner.ExtensionDictionary.IsNull) owner.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(owner.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            Xrecord record;
            if (dictionary.Contains(key)) record = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForWrite, false) as Xrecord;
            else { record = new Xrecord(); dictionary.SetAt(key, record); transaction.AddNewlyCreatedDBObject(record, true); }
            record.Data = new ResultBuffer(values.ToArray());
        }

        private static bool TryReadRecord(DBObject owner, Transaction transaction, string key, out TypedValue[] values)
        {
            values = new TypedValue[0];
            if (owner == null || owner.ExtensionDictionary.IsNull) return false;
            try
            {
                DBDictionary dictionary = transaction.GetObject(owner.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                if (dictionary == null || !dictionary.Contains(key)) return false;
                Xrecord record = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForRead, false) as Xrecord;
                values = record == null || record.Data == null ? new TypedValue[0] : record.Data.AsArray();
                return values.Length > 0;
            }
            catch { return false; }
        }

        private static ObjectId ResolveHandle(Database database, string text)
        {
            long value;
            if (string.IsNullOrWhiteSpace(text) || !long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return ObjectId.Null;
            try { return database.GetObjectId(false, new Handle(value), 0); } catch { return ObjectId.Null; }
        }

        private static double ReadDouble(object value, string propertyName)
        {
            try
            {
                PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                return property == null ? 0.0 : Convert.ToDouble(property.GetValue(value, null), CultureInfo.InvariantCulture);
            }
            catch { return 0.0; }
        }

        private static int ClampColour(int value) { return Math.Max(1, Math.Min(255, value)); }
        private static double PlanDistance(Point3d first, Point3d second) { double dx = first.X - second.X; double dy = first.Y - second.Y; return Math.Sqrt(dx * dx + dy * dy); }
        private static KeyValuePair<string, string> Pair(string key, string value) { return new KeyValuePair<string, string>(key, value); }
        private static string RangeText(double minimum, double maximum) { return double.IsInfinity(minimum) || double.IsInfinity(maximum) ? "No values" : minimum.ToString("0.###", CultureInfo.CurrentCulture) + " to " + maximum.ToString("0.###", CultureInfo.CurrentCulture); }

        private sealed class SewerAuditResult
        {
            public int Pipes; public int Structures; public int CoverViolations; public int SlopeViolations; public int DropViolations; public int OpenEndpoints;
            public double MinCover = double.PositiveInfinity, MaxCover = double.NegativeInfinity;
            public double MinSlope = double.PositiveInfinity, MaxSlope = double.NegativeInfinity;
            public double MinDrop = double.PositiveInfinity, MaxDrop = double.NegativeInfinity;
            public void AddCover(double value) { MinCover = Math.Min(MinCover, value); MaxCover = Math.Max(MaxCover, value); }
            public void AddSlope(double value) { MinSlope = Math.Min(MinSlope, value); MaxSlope = Math.Max(MaxSlope, value); }
            public void AddDrop(double value) { MinDrop = Math.Min(MinDrop, value); MaxDrop = Math.Max(MaxDrop, value); }
        }

        private sealed class SlopeLink { public string SourceHandle; public int Segment; public string Role; public double ArrowHead; }
        private sealed class SlopeGeometry { public Point3d High, Low, Head1, Head2, TextPoint; public double Slope, Rotation; }
    }

    /// <summary>
    /// Lightweight dynamic watcher used only after feature-line slope annotations
    /// are created.  Unlike the old blanket CE refresh, unrelated commands do not
    /// queue work: ObjectModified must first identify a watched source handle.
    /// </summary>
    internal static class August24SlopeDynamicManager
    {
        private sealed class State
        {
            public readonly HashSet<string> Handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool Dirty;
            public bool Busy;
        }

        private static readonly Dictionary<Document, State> States = new Dictionary<Document, State>();

        internal static void Watch(Document document, IEnumerable<string> handles)
        {
            if (document == null || document.Database == null) return;
            State state;
            if (!States.TryGetValue(document, out state))
            {
                state = new State();
                States.Add(document, state);
                try { document.Database.ObjectModified += OnObjectModified; } catch { }
                try { document.CommandEnded += OnCommandEnded; } catch { }
            }
            foreach (string handle in handles) if (!string.IsNullOrWhiteSpace(handle)) state.Handles.Add(handle);
        }

        private static void OnObjectModified(object sender, ObjectEventArgs args)
        {
            if (args == null || args.DBObject == null) return;
            string handle;
            try { handle = args.DBObject.Handle.ToString(); } catch { return; }
            foreach (KeyValuePair<Document, State> pair in States)
            {
                if (pair.Value.Busy) continue;
                if (pair.Key.Database == sender && pair.Value.Handles.Contains(handle)) pair.Value.Dirty = true;
            }
        }

        private static void OnCommandEnded(object sender, CommandEventArgs args)
        {
            Document document = sender as Document;
            State state;
            if (document == null || !States.TryGetValue(document, out state) || !state.Dirty || state.Busy) return;
            state.Busy = true;
            try
            {
                August24FieldCompletionCommands.RefreshSlopeAnnotationsForDynamic(document);
                state.Dirty = false;
                August21DisplayRefresh.Flush(document);
            }
            catch { }
            finally { state.Busy = false; }
        }
    }
}
