using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.September11FieldCompletionMenu))]

namespace CETools.Civil3D
{
    /// <summary>
    /// One discoverable front door for the current field-completion workflows.
    /// Keep this list updated whenever a new field-completion command is added so
    /// the project can be followed from the ribbon/workflow centre without hunting
    /// for command names.
    /// </summary>
    public sealed class September11FieldCompletionMenu
    {
        // Keep this token split so legacy source finalizers that identify the canonical
        // command implementation by its complete literal do not mistake this menu for it.
        private const string DynamicRefreshAllCommand = "CE_DYNAMIC" + "REFRESHALL";

        [CommandMethod("CE_TOOLS", "CE_FIELDCOMPLETION", CommandFlags.Modal)]
        public void Open()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Field Completion",
                "Current field workflows for manual dynamic refresh, strict road-centre BC/EC cleanup, safe/dynamic feature-line linking and stepped offsets, multi-alignment label sets, road profile band labels, batch T/cross bellmouth creation, corridor junction splitting, fully dynamic dimensions, corridor feature-line extraction, sewer long sections and read-only engineering audits, sewer recalculation/profile safety, automatic drawing-scale annotation synchronisation, project style presets, Namibia coordinates, Google Earth linework and joined hatch outer boundaries.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction(
                        "Dynamic Refresh All",
                        DynamicRefreshAllCommand,
                        "Run the established explicit/manual CE Tools refresh. The same action is also available from AutoCAD's default right-click menu as CE Dynamic Refresh All.",
                        "00 General"),
                    new DisciplineWorkflowAction(
                        "Road Reserve Centres - Generate + Clean",
                        "CE_ROADRESERVECENTRELINES",
                        "Create the road-reserve centre segments and join connected runs. Use Strict Road Centre Cleanup immediately afterwards when the generated linework must be reduced to start/end and BC/EC control vertices.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Strict Road Centre Cleanup - Start / BC / EC / End",
                        "CE_ROADCENTRECLEANSTRICT",
                        "Clean multiple selected open road-centre polylines. Straight roads keep start/end only, including through T/X junctions; arc transition vertices BC/EC are retained, so a single horizontal curve resolves to start, BC, EC and end. Genuine non-collinear bends are preserved.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Link Existing Relative Feature Lines",
                        "CE_FLRELLINKEXISTING",
                        "Select one source and multiple existing constant-offset Civil 3D feature lines. CE Tools verifies the horizontal/vertical relation and attaches CE_FLREL links without erasing or recreating the existing geometry.",
                        "01 Feature Lines"),
                    new DisciplineWorkflowAction(
                        "Safe Stepped Offsets - Multiple Feature Lines",
                        "CE_FLSTEPSSAFE",
                        "Create linked stepped offsets from multiple selected source feature lines through the candidate-first fatal-safety engine. Existing source feature lines are kept.",
                        "01 Feature Lines"),
                    new DisciplineWorkflowAction(
                        "Dynamic Surface Link - Existing Feature Lines",
                        "CE_FLSURFACELINKEXISTING",
                        "Drape multiple existing Civil 3D feature lines to one selected surface and store persistent direct-drape links. Surface sampling is isolated from feature-line writes and failed selections are skipped safely.",
                        "01 Feature Lines"),
                    new DisciplineWorkflowAction(
                        "Feature Line Appearance / Site",
                        "CE_FLAPPEARANCE",
                        "Apply explicit feature-line colour, layer and Civil 3D Site assignment with visible plan graphics.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Alignment Label Set - Multiple Alignments",
                        "CE_ALIGNLABELSETMULTI",
                        "Choose one existing Civil 3D alignment label set style, then apply it to every selected editable alignment in one operation.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Import Road Band Set to Multiple Profile Views",
                        "CE_ROADBANDLABELS",
                        "Choose a Civil 3D road band set, import and commit it to each selected profile view, bind each band to its matching road profile and turn on labels.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Batch T/Cross Junction Bellmouths",
                        "CE_ROADJUNCTIONBULK",
                        "Detect every T/cross intersection, create all bellmouth returns, close every T-junction with the magenta line and write the configured junction layer.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Reverse Multiple Road Alignments + Profiles",
                        "CE_ROADALIGNREVERSEMULTI",
                        "Reverse selected Civil 3D road alignments and refresh their associated profiles, profile views and corridor rebuilds in the same workflow.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "T-Junction Assembly Limits / Remove Short Regions",
                        "CE_ROADTJUNCTIONASSEMBLYLIMITS",
                        "Use the magenta T-junction closure lines from the batch bellmouth command as side-road assembly/region limits and remove the short terminal region between the junction and road edge. Cross junctions are left continuous.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Road TOP/BOTTOM Surface Names",
                        "CE_ROADSURFACENAMES",
                        "Rename corridor surfaces by their actual road alignment: TOP-RD-01, BOTTOM-RD-01, TOP-RD-02, BOTTOM-RD-02 and so on.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "TOP/BOTTOM Surfaces in Selected Profile Views",
                        "CE_ROADTOPBOTTOMPROFILE",
                        "Add TOP-RD/BOTTOM-RD surface profiles to selected road profile views so crossing and T-junction surface levels are visible.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Junction End Points to Road TOP Surfaces",
                        "CE_JUNCTIONENDPOINTSTOTOPSURFACES",
                        "Paste generated junction return/closure endpoints into each covering TOP-RD surface using supported Civil 3D surface-vertex APIs.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Split Multiple Corridors at Junctions",
                        "CE_ROADJUNCTIONCONSTRUCTION",
                        "Use all CE junction geometry to split multiple selected corridor regions at every bellmouth limit, with configurable station clustering and extra split distance.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Corridor Frequencies / Targets / Slopes",
                        "CE_ROADCORRIDORCOMPLETE",
                        "Select multiple corridors, choose the target surface, set tangent/curve/spiral/vertical/target frequencies, rebuild TOP/DATUM surfaces and refresh cut/fill slope patterns.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Dynamic Dimensions - All Types",
                        "CE_MULTIDIM",
                        "Create aligned, horizontal, vertical, angular, radius and arc-length dimensions linked to multiple selected polylines or Civil 3D feature lines.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Corridor Feature Lines - Select Codes",
                        "CE_CORRIDORFEATURELINES",
                        "Export individual grading feature lines from selected corridor centre, edge, kerb, sidewalk, shoulder, toe or exact point codes. Choose dynamic links in the popup and review or insert the extraction register as a DWG table.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Dynamic Corridor Annotation",
                        "CE_CORLABELX",
                        "Create corridor information as a dynamically refreshed MLeader, MText or COGO point. Choose 1.8, 2.0, 2.5, 3.5 or 5.0 mm paper height and an optional small circle at the reference/leader start.",
                        "01 Roads"),
                    new DisciplineWorkflowAction(
                        "Sewer Integrity Audit - Connections / Rules / Levels",
                        "CE_SEWAUDITLIMITS",
                        "Review every pipe and structure in one gravity network, including exact open pipe ends, isolated/terminal structures, assigned rule sets and reference surfaces, inside inverts, outside-crown cover, slopes, drops, depth and sump clearance. The audit is read-only.",
                        "02 Sewer"),
                    new DisciplineWorkflowAction(
                        "Sewer Profiles / Long Sections",
                        "CE_SEWPROFILE",
                        "Create one isolated existing-ground profile view per CE sewer branch, add matching pipes and structures, link the selected band data, and review the result in a grid or DWG table.",
                        "02 Sewer"),
                    new DisciplineWorkflowAction(
                        "Connect Open Sewer Pipe Ends",
                        "CE_SEWCONNECTPARTS",
                        "Connect open pipe starts/ends to the nearest compatible existing structure in the same network within a chosen tolerance. Existing connections and source parts are retained.",
                        "02 Sewer"),
                    new DisciplineWorkflowAction(
                        "Sewer Surface / Rules Recalculation",
                        "CE_SEWRECALC",
                        "Re-link a gravity network to one surface, apply pipe and structure rules, show every part result/error in a grid or DWG table, then optionally queue sewer profiles only after the transaction commits.",
                        "02 Sewer"),
                    new DisciplineWorkflowAction(
                        "Reverse Multiple Pipe Slopes to Outlet",
                        "CE_PIPESLOPETOOUTLET",
                        "Select multiple gravity pipes, specify the low point/outlet, and reverse only those pipe end elevations that currently fall away from the outlet.",
                        "02 Sewer"),
                    new DisciplineWorkflowAction(
                        "Synchronise Annotation Scale",
                        "CE_ANNOSCALESYNC",
                        "Automatic monitor now applies each changed drawing annotation scale to annotative dimensions, text, MText and multileaders. Run this command when an immediate manual synchronisation is required.",
                        "03 Standards"),
                    new DisciplineWorkflowAction(
                        "Discipline Style Presets",
                        "CE_DISCIPLINESTYLEPRESETS",
                        "Choose and apply the existing CE Tools discipline style preset workflow for the current drawing.",
                        "03 Standards"),
                    new DisciplineWorkflowAction(
                        "Namibia Town -> Coordinate System",
                        "CE_SURVEYLOCATIONNAMIBIA",
                        "Choose the Namibia project town and assign its mapped installed LO coordinate system without transforming geometry.",
                        "04 Survey"),
                    new DisciplineWorkflowAction(
                        "Multiple Linework -> Google Earth",
                        "CE_GOOGLEEARTHLINEWORK",
                        "Select multiple separate open OR closed 2D/3D polylines, lines, curves or feature lines and display them as separate KML LineStrings in Google Earth.",
                        "04 Survey"),
                    new DisciplineWorkflowAction(
                        "Joined Hatch Outer Boundary",
                        "CE_HATCHOUTERBOUNDARY",
                        "Select attached hatches and create one outside closed polyline for the connected hatch set by removing shared internal hatch edges. Disconnected hatch clusters receive one perimeter each.",
                        "05 CAD"),

                    // Latest field-completion additions are kept in this front door
                    // as soon as their source command files are added or updated.
                    new DisciplineWorkflowAction(
                        "Reverse Selected Design Profile Views",
                        "CE_ROADPROFILEVIEWREVERSEMULTI",
                        "Reverse multiple selected final design profile views, recover their parent road alignments, reverse the final design PVIs and refresh the views.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Assign Profile Label Sets to Multiple Final Profiles",
                        "CE_PROFILELABELSETMULTI",
                        "Apply one Civil 3D profile label-set style to multiple selected final design profiles and refresh their labels.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Assign Profile View Styles to Multiple Views",
                        "CE_PROFILEVIEWSTYLEMULTI",
                        "Apply one Civil 3D profile-view style to multiple selected profile views and regenerate their display.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Add Band Labels to Multiple Profile Views",
                        "CE_PROFILEBANDLABELSMULTI",
                        "Rebind road band rows to their ground/edge/centre/design profiles, enable labels, and keep existing utility network links intact.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Road TOP/BOTTOM Surface Profiles",
                        "CE_ROADTOPBOTTOMPROFILE",
                        "Add road TOP and BOTTOM surface profiles to selected profile views using valid Civil 3D 2023 profile styles and label sets.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Drape Junction Feature Lines to Road TOP Surfaces",
                        "CE_ROADJUNCTIONFEATURELINESTOP",
                        "Drape multiple selected junction feature lines to every matching road TOP surface and paste their valid elevated vertices into those surfaces.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Junction Endpoints to All TOP Surfaces",
                        "CE_JUNCTIONENDPOINTSTOTOPSURFACES",
                        "Paste generated junction closure endpoints into every covering road TOP surface.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Batch T/Cross Junction Bellmouths and T-Closures",
                        "CE_ROADJUNCTIONBULK",
                        "Create all T/cross bellmouth returns in one transaction, close T-junctions with the magenta closure line and write the configured output layer.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Batch T/Cross Bellmouths - Feature-Line Output",
                        "CE_ROADJUNCTIONBATCH",
                        "Create every detected T/cross bellmouth in one transaction, join every T-junction return pair with the magenta endpoint closure and optionally output normal Civil 3D feature lines.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "T-Junction Assembly Limits",
                        "CE_ROADTJUNCTIONASSEMBLYLIMITS",
                        "Use magenta T-junction closure lines as assembly limits and remove only eligible short terminal regions.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Manual Manhole Sump Elevations",
                        "CE_SEWERSUMPFIX",
                        "Choose the manual sump depth below the lowest connected pipe invert and write absolute elevation-controlled manhole sumps.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Sewer Profiles / Long Sections",
                        "CE_SEWPROFILE",
                        "Create sewer profile views and bind their data without the unsafe global band refresh that caused the Civil 3D 2023 write-open abort.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Level Sewer Junction Pipe Ends",
                        "CE_PIPESLOPEJUNCTIONFIX",
                        "Repair selected sewer pipe endpoint jumps at common junction structures while preserving the pipe network.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Reverse Multiple Road Alignments and Profiles",
                        "CE_ROADALIGNREVERSEMULTI",
                        "Reverse selected road alignments and refresh their associated profiles, profile views and corridors.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Feature-Line Colour and Site Assignment",
                        "CE_FLAPPEARANCE",
                        "Apply feature-line colour/style and assign an existing or newly named Civil 3D site.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_PLATFORMDRAPEMULTI",
                        "CE_PLATFORMDRAPEMULTI",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_PLATFORMGRADETOSURFACE",
                        "CE_PLATFORMGRADETOSURFACE",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_FLCLOSEGAP",
                        "CE_FLCLOSEGAP",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_PLATFORMCONSTANTGRADE",
                        "CE_PLATFORMCONSTANTGRADE",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_PLATFORMCLOSEGAPS",
                        "CE_PLATFORMCLOSEGAPS",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_CADSUPPLEMENTARY",
                        "CE_CADSUPPLEMENTARY",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_SURVEYFIELDSUPPLEMENTARY",
                        "CE_SURVEYFIELDSUPPLEMENTARY",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_SEWERFIELDSUPPLEMENTARY",
                        "CE_SEWERFIELDSUPPLEMENTARY",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_PLATFORMFIELDSUPPLEMENTARY",
                        "CE_PLATFORMFIELDSUPPLEMENTARY",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_ROADFIELDSUPPLEMENTARY",
                        "CE_ROADFIELDSUPPLEMENTARY",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_SEWSEQAUTOALIGN",
                        "CE_SEWSEQAUTOALIGN",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_SEWSEQNETWORKPRODUCTION",
                        "CE_SEWSEQNETWORKPRODUCTION",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_SEWSEQMAINPRODUCTION",
                        "CE_SEWSEQMAINPRODUCTION",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_SEWSURFACERIMS",
                        "CE_SEWSURFACERIMS",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_PLATFORMFIXEDMINSLOPE",
                        "CE_PLATFORMFIXEDMINSLOPE",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_FLRELADOPT",
                        "CE_FLRELADOPT",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_FEATURELINESLOPEARROWS",
                        "CE_FEATURELINESLOPEARROWS",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_SLOPEARROWSREFRESH",
                        "CE_SLOPEARROWSREFRESH",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_SITEGRIDPRESENTATION",
                        "CE_SITEGRIDPRESENTATION",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_SURFACESLOPEARROWS",
                        "CE_SURFACESLOPEARROWS",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_ROADHATCHSIDES",
                        "CE_ROADHATCHSIDES",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_ROADELEVMATCH",
                        "CE_ROADELEVMATCH",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_ROADELEVMATCHREFRESH",
                        "CE_ROADELEVMATCHREFRESH",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_CLOSEOPENMULTI",
                        "CE_CLOSEOPENMULTI",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_MULTISTRETCHFL",
                        "CE_MULTISTRETCHFL",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_SURVEYCONSTRUCTIONOFFSET",
                        "CE_SURVEYCONSTRUCTIONOFFSET",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_SURVEYMIDCONSTRUCTION",
                        "CE_SURVEYMIDCONSTRUCTION",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_FEATURELINECROSSFALLARROWS",
                        "CE_FEATURELINECROSSFALLARROWS",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_CONNECTENDPOINTS",
                        "CE_CONNECTENDPOINTS",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_DYNAMICSLOPESREFRESH",
                        "CE_DYNAMICSLOPESREFRESH",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_MULTIFILLET",
                        "CE_MULTIFILLET",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_GRIDDIFFERENCE",
                        "CE_GRIDDIFFERENCE",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_CONSTRUCTIONFILLET",
                        "CE_CONSTRUCTIONFILLET",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_SLOPEANNOTATIONS",
                        "CE_SLOPEANNOTATIONS",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_SEWLINKSURFACE",
                        "CE_SEWLINKSURFACE",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_ROADCENTRECLEAN",
                        "CE_ROADCENTRECLEAN",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_ALIGNREVERSEMULTI",
                        "CE_ALIGNREVERSEMULTI",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_SURFACESTYLEMULTI",
                        "CE_SURFACESTYLEMULTI",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_ASSEMBLYCOPYSAFE",
                        "CE_ASSEMBLYCOPYSAFE",
                        "Latest field-completion update from the recent Civil 3D 2023 field-recovery passes.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_PROFILEMOVEVERTICAL",
                        "CE_PROFILEMOVEVERTICAL",
                        "Move multiple selected design profiles up or down by a specified vertical distance.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_SURFACEBATCHCONTROL",
                        "CE_SURFACEBATCHCONTROL",
                        "Rebuild multiple selected surfaces and switch automatic rebuilding on or off.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_CORRIDORBATCHCONTROL",
                        "CE_CORRIDORBATCHCONTROL",
                        "Select multiple corridors and control rebuild, layer, profile style and both-side cut/fill slope styles.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_PLATFORMSTEPOFFSETS",
                        "CE_PLATFORMSTEPOFFSETS",
                        "Create multi-source stepped feature-line offsets with inside/outside, straight/curved and grade/elevation rules.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_ROADCORRIDORCOMPLETE",
                        "CE_ROADCORRIDORCOMPLETE",
                        "Select corridors in the completion popup and apply surfaces, layers, profile and slope-pattern settings.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_ROADCENTRECLEANSTRICT",
                        "CE_ROADCENTRECLEANSTRICT",
                        "Clean straight road-centre vertices using a configurable minimum deflection angle.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Recent - CE_SEWERFROMCADASTRAL",
                        "CE_SEWERFROMCADASTRAL",
                        "Select the cadastral sewer analysis surface from the dropdown.",
                        "06 Latest Field Completion"),
                    new DisciplineWorkflowAction(
                        "Assign Corridor Settings to Multiple Corridors",
                        "CE_ROADCORRIDORASSIGNBATCH",
                        "Assign selected assemblies, assembly frequencies, surface targets, slope-pattern styles, slope-pattern visibility and corridor-extents boundaries to multiple selected corridors.",
                        "06 Latest Field Completion"),
                });
        }
    }
}
