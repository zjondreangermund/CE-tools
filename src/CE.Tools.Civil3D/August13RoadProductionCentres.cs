using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
[assembly: CommandClass(typeof(CETools.Civil3D.August13RoadProductionCentres))]
namespace CETools.Civil3D
{
 public sealed class August13RoadProductionCentres
 {
  [CommandMethod("CE_TOOLS","CE_ROADPRODUCTIONV2",CommandFlags.Modal)]
  public void RoadProduction()
  {
   Document d=AcApplication.DocumentManager.MdiActiveDocument;
   if(d==null)return;
   August11DisciplineStylePresetManager.ActivateForProduction(d.Database,"Roads");
   Run("CE-ROAD PRODUCTION","Choose Road Settings, Road Layout Production or Road Design Production.",new List<DisciplineWorkflowAction>
   {
    A("CE-Road Settings","CE_ROADSETTINGSCENTRE","Road-only project and Civil 3D settings.","01 Road Production"),
    A("CE-Road Layout Production","CE_ROADLAYOUTPRODUCTIONCENTRE","Road reserve geometry, junctions, annotation and setting-out.","01 Road Production"),
    A("CE-Georeferenced JPEG Tiles","CE_GEOIMAGETILES","Create high-quality JPEG tiles from licensed georeferenced imagery and insert correctly scaled and oriented using JGW files.","01 Road Production"),
    A("CE-Insert Georeferenced JPEGs","CE_GEOIMAGEINSERT","Attach existing georeferenced JPEG tiles to model space using their six-parameter world files.","01 Road Production"),
    A("Vehicle Tracking (Preliminary)","CE_VEHICLETRACKING","Review vehicle edge and wheel guides, offsets, junction-path intent and clearance/sight inputs.","01 Road Production"),
    A("CE-Road Signs - Linked Image Placement","CE_ROADSIGNS","Place externally linked sign images on multiple road alignments with start/end station rules and setting-out markers.","01 Road Production"),
    A("CE-Road Design Production","CE_ROADDESIGNPRODUCTIONCENTRE","Alignments, profiles, corridors, junction design, BOQ and drawings.","01 Road Production"),
    A("FINAL ROAD PRODUCTION","CE_FINALROADPRODUCTION","Run the approved, ordered Final Road Production workflow.","01 Road Production")
   });
  }
  [CommandMethod("CE_TOOLS","CE_ROADSETTINGSCENTRE",CommandFlags.Modal)]
  public void RoadSettings()
  {
   Run("CE-Road Settings","Road-only production settings.",new List<DisciplineWorkflowAction>
   {
    A("CE-Road Settings","CE_ROADSETTINGS","Road alignment/profile/profile-view/band/corridor/assembly settings.","01 Settings"),
    A("CE-Road Styles","CE_ROADSTYLES","Civil 3D styles used only by Road production.","01 Settings"),
    A("CE-Vertex / Setting-Out Options","CE_VERTEXSETTINGOUTTOOLS","Point, table, elevation and coordinate display settings.","02 Setting-Out"),
    A("CE-Swap X/Y Values","CE_SETTINGOUTSWAPXY","Exchange displayed X and Y numeric values in selected setting-out tables.","02 Setting-Out")
   });
  }
  [CommandMethod("CE_TOOLS","CE_ROADLAYOUTPRODUCTIONCENTRE",CommandFlags.Modal)]
  public void RoadLayout()
  {
   Run("CE-Road Layout Production","Complete road layout before Civil 3D design.",new List<DisciplineWorkflowAction>
   {
    A("CE-Closed Polylines for All Plots","CE_ROADRESERVECLOSE","Close selected/all open plot or reserve polylines with controlled endpoint-gap handling.","01 Source"),
    A("CE-Road Reserve Centrelines","CE_ROADRESERVECENTERLINES","Create road reserve centrelines from opposing closed cadastral/reserve boundaries.","02 Centrelines"),
    A("CE-Join Continuous Road Reserve Centrelines","CE_ROADCONTINUITYFIX","Join continuous centreline strings.","02 Centrelines"),
    A("CE-Multiple Horizontal Centreline Curves","CE_ROUTEHORIZONTALCURVES","Apply specified tangent curve radii to multiple road/route centreline polylines.","03 Geometry"),
    A("CE-Road Offsets","CE_ROADOFFSET","Create road offsets.","03 Geometry"),
    A("CE-Batch Polyline Offsets","CE_POLYLINEOFFSET","Offset all model-space or multiple selected 2D polylines left, right or both ways without changing originals.","03 Geometry"),
    A("CE-Georeferenced JPEG Tiles","CE_GEOIMAGETILES","Split authorized georeferenced JPEGs into image tiles with accurately calculated JGW world files, then attach them.","03 Geometry"),
    A("CE-Insert Georeferenced JPEGs","CE_GEOIMAGEINSERT","Insert one or more already georeferenced JPEGs or orthophoto tiles onto CE-AERIAL-IMAGERY.","03 Geometry"),
    A("CE-Scale Objects Individually","CE_SCALEEACH","Scale multiple selected drawing objects around their own bottom-centre or top-centre point.","03 Geometry"),
    A("CE-Batch Cross Sections","CE_BATCHCROSSSECTIONS","Create and group sections for multiple road alignments with selected sources, styles, band sets and grid placement.","06 Reports"),
    A("CE-Cross Section View Match","CE_CROSSSECTIONVIEWMATCH","Copy section display style and compatible band data from one source section view.","06 Reports"),
    A("CE-Cross Section View Arrange","CE_CROSSSECTIONVIEWARRANGE","Arrange all or multiple selected cross-section views into per-road grids.","06 Reports"),
    A("CE-Cross Section View Fit","CE_CROSSSECTIONVIEWFIT","Fit elevation/station/offset ranges on all or multiple selected cross-section views when supported.","06 Reports"),
    A("CE-Multiple T/Cross Junction Bellmouths","CE_ROADJUNCTIONBULK","Create T/cross-junction bellmouths.","04 Junctions"),
    A("CE-Multiple Junction Trim","CE_ROADJUNCTIONTRIM","Trim multiple junctions.","04 Junctions"),
    A("CE-Bellmouth Tangent Trim","CE_BELLMOUTHTRIMEDGES","Trim road and sidewalk/shoulder edges exactly to generated bellmouth tangent stations.","04 Junctions"),
    A("CE-Road Names","CE_ROADNAMES","Create/update linked road names.","05 Annotation"),
    A("CE-Synchronize Road Names","CE_ROADNAMESYNC","Synchronize ROAD-n names across alignments, profiles, corridors, sections and assemblies.","05 Annotation"),
    A("CE-Annotation Presentation","CE_ROUTEANNOTATIONSTYLE","Apply annotation presentation settings.","05 Annotation"),
    A("CE-Shift Annotations","CE_ROUTESHIFTANNOTATION","Shift selected annotations together.","05 Annotation"),
    A("CE-Road Dimensions","CE_ROADDIMENSIONS","Create/update linked road dimensions.","05 Annotation"),
    A("CE-Multiple Junction Bellmouth Setting-out","CE_JUNCTIONSETTINGOUT4","Complete four return curves per junction before continuing.","06 Setting-Out"),
    A("CE-Select Polylines Shorter Than Length","CE_SELECTPOLYSHORTER","Select current-space polylines shorter than a specified length.","07 Selection"),
    A("CE-Select Polylines With Same Length","CE_SELECTPOLYSAMELENGTH","Select polylines matching a reference length.","07 Selection"),
    A("CE-Refresh Linked Road Layout","CE_ROADLAYOUTREFRESH","Refresh linked road layout data.","08 Refresh")
   });
  }
  [CommandMethod("CE_TOOLS","CE_ROADDESIGNPRODUCTIONCENTRE",CommandFlags.Modal)]
  public void RoadDesign()
  {
   Run("CE-Road Design Production","Civil 3D road design production after the layout is complete.",new List<DisciplineWorkflowAction>
   {
    A("CE-Create Road Alignments","CE_ROADALIGN","Create linked road alignments.","01 Alignment"),
    A("CE-NGL and Final Road Profiles","CE_ROADPROFILEFULL","Create NGL and final road profiles, vertical curves and final profile-view styling.","02 Profiles"),
    A("CE-Split Road Profile Views","CE_ROADPROFILEVIEWSPLIT","Create 750 m station-section profile views or a specified section length.","02 Profiles"),
    A("CE-Road Assemblies","CE_ASSEMBLYTOOLS","Create/select road assemblies.","03 Corridors"),
    A("CE-Road Corridors","CE_ROADCORRIDORFULL","Create/complete road corridors and final CE-TOP/CE-BOTTOM outputs.","03 Corridors"),
    A("CE-Road Surface Outputs","CE_ROADSURFACEMERGE","Merge all road TOP surfaces to CE Top All, all road BOTTOM surfaces to CE Bottom All, or selected natural ground plus road TOP/grading surfaces to CE Final Surface.","03 Corridors"),
    A("CE-Merge All Junction Surfaces", "CE_JUNCTIONSURFACEMERGE", "Create CE Junctions All from all CE junction grading and kerb/gutter infill TIN surfaces.", "04 DESIGN"),
    A("CE-Corridor Feature Lines","CE_CORRIDORFEATURELINES","Create individual feature lines from selected corridor centreline/edge/kerb/sidewalk/shoulder/toe codes or all corridor feature lines.","03 Corridors"),
    A("CE-Split Corridors","CE_CORSPLIT","Split corridor regions at specified stations.","03 Corridors"),
    A("CE-Automatic Rebuild ON / OFF","CE_AUTOMATICREBUILD","Set all corridor and surface automatic rebuild flags independently.","03 Corridors"),
    A("CE-Name Viewport Scales","CE_NAMESVIEWPORTSCALES","Apply current drawing viewport scales to existing Road/Sewer/Water names.","05 Annotation"),
    A("CE-Rebuild Corridors","CE_CORREBUILD","Rebuild corridor model data.","03 Corridors"),
    A("CE-Road Junction Design","CE_ROADJUNCTIONCONSTRUCTIONTOOLS","Create/finalize junction geometry, corridor regions and outputs.","04 Junction Design"),
    A("CE-Junction Stepped Offset Fallback","CE_JUNCTIONSTEPPEDOFFSETWORKFLOW","Use feature-line stepped offsets when corridor junction design is unreliable.","04 Junction Design"),
    A("CE-Refresh All Linked Model Data","CE_REFRESHALL","Refresh linked model data.","05 Refresh"),
    A("CE-Road Production Information","CE_ROADPRODUCTIONINFO","Open road production information.","06 Reports"),
    A("CE-Road Profile Report","CE_ROADPROFILEREPORT","Report road profile views and station ranges.","06 Reports"),
    A("CE-Road Corridor Report","CE_CORREPORT","Generate corridor report.","06 Reports"),
    A("CE-Road Construction Bill of Quantities","CE_ROADBOQCONSTRUCTION","Create/update live corridor construction quantities.","07 Deliver"),
    A("CE-Automatic Road Plan / Profile Sheets","CE_ROADAUTOSHEETS","Split long roads by chainage, split curved plan ranges into adjacent independently rotated viewports, clone A0/A1 layouts and stack matching plan/profile sets.","07 Deliver"),
    A("CE-Detail Road Design Report","CE_REPORTROAD","Generate detailed road design report.","07 Deliver"),
    A("CE-Road Drawing Production","CE_DRAWINGBOOKROAD","Create road drawing production output.","07 Deliver")
   });
  }
  [CommandMethod("CE_TOOLS","CE_FINALROADPRODUCTION",CommandFlags.Modal)]
  public void FinalRoadProduction()
  {
   Run("FINAL ROAD PRODUCTION",
       "Approved road production sequence. Choose each step in order and review the drawing between stages.",
       new List<DisciplineWorkflowAction>
       {
                A("01. CE-Project Configuration","CE_ROADSETTINGS","Open project-specific road settings for geometry, profiles, corridor assemblies, drawing scales and saved defaults.","01 Final Road Production"),
        A("02. CE-Road Design Standards","CE_ROADSTYLES","Activate road-specific drawing styles and design presentation defaults.","02 Final Road Production"),
        A("03. CE-Road Reserve Centreline Generation","CE_ROADRESERVECENTRELINES","Generate road centreline paths from road reserve boundaries and connected centreline elements.","03 Final Road Production"),
        A("04. CE-Road Centreline Geometry Cleanup","CE_ROADCENTRECLEANSTRICT","Clean centreline geometry and check start, curve tangent and end vertices before alignment creation.","04 Final Road Production"),
        A("05. CE-Road Alignment Generation","CE_ROADALIGN","Create Civil 3D road alignments from prepared centreline geometry.","05 Final Road Production"),
        A("06. CE-Alignment Label Management","CE_ALIGNLABELSETMULTI","Apply an alignment label set to the selected road alignments without altering their geometry.","06 Final Road Production"),
        A("07. CE-Road Name Placement","CE_ROADNAMES","Place or update linked road name annotations and junction-midpoint labels.","07 Final Road Production"),
        A("08. CE-Alignment Sorting","CE_ALIGNMENTSORT","Sort and number road alignments in the chosen drawing order.","08 Final Road Production"),
        A("09. CE-Road Name Synchronisation","CE_ROADNAMESYNC","Synchronise road alignment names and existing linked road-name annotations.","09 Final Road Production"),
        A("10. CE-Road Name Validation","CE_ROADNAMEAUDIT","Review road naming conflicts, unlabelled roads and incorrect alignment references.","10 Final Road Production"),
        A("11. CE-Assembly Generation","CE_ASSEMBLYCREATE","Generate or select approved Civil 3D road assemblies and widths for corridor design.","11 Final Road Production"),
        A("12. CE-Profile Generation","CE_ROADPROFILEFULL","Create and update natural-ground and final-road profiles and their profile views.","12 Final Road Production"),
        A("13. CE-Profile View Standards","CE_PROFILEVIEWSTYLEMULTI","Apply consistent profile view styles to multiple selected road profile views.","13 Final Road Production"),
        A("14. CE-Profile Label Management","CE_PROFILELABELSETMULTI","Apply profile label sets to selected design profiles while retaining vertical geometry.","14 Final Road Production"),
        A("15. CE-Label Collision Review","CE_LABELCOLLISIONREVIEW","Review road, water and sewer CE text-name clashes and adjust spacing where supported.","15 Final Road Production"),
        A("16. CE-Profile Band Management","CE_PROFILEVIEWDATASOURCES","Assign profile band sets and repair their source profile and surface bindings.","16 Final Road Production"),
        A("17. CE-Profile View Property Matching","CE_PROFILEVIEWMATCH","Match profile view display properties from a selected reference view.","17 Final Road Production"),
        A("18. CE-Profile View Fitting","CE_PROFILEVIEWFITALL","Fit vertical and horizontal profile view extents to the available design geometry.","18 Final Road Production"),
        A("19. CE-Profile View Arrangement","CE_PROFILEVIEWARRANGE","Arrange multiple profile views in a consistent layout with controlled spacing.","19 Final Road Production"),
        A("20. CE-Corridor Generation","CE_ROADCORRIDORFULL","Create road corridors and assembly regions from the approved alignments and FG profiles.","20 Final Road Production"),
        A("21. CE-Profile Surface Management","CE_ROADTOPBOTTOMPROFILE","Generate or update corridor TOP/BOTTOM surfaces and add linked profile surface displays.","21 Final Road Production"),
        A("22. CE-Crossing PVI Creation","CE_ROADCROSSINGPVIS","Create design profile PVIs at road crossing points for connected-road grading.","22 Final Road Production"),
        A("23. CE-Crossing PVI Review","CE_REMOVEROADCROSSINGPVIS","Review or remove crossing-created PVIs before recalculating vertical curves.","23 Final Road Production"),
        A("24. CE-Vertical Curve Management","CE_ROADVERTICALCURVES","Create or repair vertical curves between road design profile tangents.","24 Final Road Production"),
        A("25. CE-Profile View Splitting","CE_ROADPROFILEVIEWSPLIT","Split long road profile views into manageable drawing sections.","25 Final Road Production"),
        A("26. CE-Junction Geometry Generation","CE_ROADJUNCTIONBATCH","Create T-junction and cross-junction bellmouth geometry using feature lines.","26 Final Road Production"),
        A("27. CE-Junction Assembly Preparation","CE_ROADTJUNCTIONASSEMBLYLIMITS","Shorten and clean corridor assembly regions at T-junction returns.","27 Final Road Production"),
        A("28. CE-Corridor Junction Splitting","CE_ROADJUNCTIONCONSTRUCTION","Split corridors at junctions to avoid duplicate overlapping design regions.","28 Final Road Production"),
        A("29. CE-Feature Line Constant Grade","CE_FLCONSTGRADE","Apply the requested constant grade to selected feature lines after junction corridor splitting.","29 Final Road Production"),
        A("30. CE-Junction Offset Management","CE_JUNCTIONSTEPPEDOFFSETWORKFLOW","Create the offset lines and associated junction grading geometry.","30 Final Road Production"),
        A("31. CE-Endpoint Connection","CE_CONNECTENDPOINTS","Connect selected endpoints of road and junction feature-line segments.","31 Final Road Production"),
        A("32. CE-Junction Grading","CE_JUNCTIONGRADETOSURFACE","Grade connected junction feature lines toward their design surfaces.","32 Final Road Production"),
        A("33. CE-Road Surface Merge","CE_ROADSURFACEMERGE","Merge road corridor and related design surfaces for final road production.","33 Final Road Production"),
        A("34. CE-Junction Surface Merge","CE_JUNCTIONSURFACEMERGE","Merge junction grading surfaces into the combined junction final surface.","34 Final Road Production"),
        A("35. CE-Feature Line Elevation Transfer","CE_PLATFORMDRAPEMULTI","Transfer surface elevations to multiple selected road or junction feature lines.","35 Final Road Production"),
        A("36. CE-Close Stepped Feature-Line Gaps","CE_PLATFORMCLOSEGAPS","Join stepped feature lines or polylines and optionally preserve or remove selected source geometry.","36 Final Road Production"),
        A("37. CE-Densify Feature Lines","CE_DENSIFYFEATURELINES","Add controlled intermediate elevation points to selected feature lines.","37 Final Road Production"),
        A("38. CE-Road Edge Elevation Management","CE_ROADEDGELEVELS","Update left/right road edge levels against the final road design surfaces.","38 Final Road Production"),
        A("39. CE-Profile View Surface Assignment","CE_SURFACEADDTOVIEWS","Assign selected surface profiles to multiple Civil 3D profile views.","39 Final Road Production"),
        A("40. CE-Corridor Completion","CE_ROADCORRIDORCOMPLETE","Complete and refresh corridor links, regions and design surfaces.","40 Final Road Production"),
        A("41. CE-Surface Style Management","CE_SURFACESTYLEMULTI","Apply the selected surface display style to multiple existing surfaces.","41 Final Road Production"),
        A("42. CE-Alignment and Profile Reversal","CE_ROADALIGNREVERSEMULTI","Reverse selected road alignments and their associated profiles where directed.","42 Final Road Production"),
        A("43. CE-Road Width Dimensioning","CE_ROADDIMENSIONS","Place controlled road width and junction dimension annotations in millimetres.","43 Final Road Production"),
        A("44. CE-Junction Setting-Out","CE_JUNCTIONSETTINGOUT4","Generate junction setting-out points and tables with verified coordinates and elevations.","44 Final Road Production"),
        A("45. CE-Road Hatch Generation","CE_ROADHATCHSIDES","Add road surface hatching for drawing presentation.","45 Final Road Production"),
        A("46. CE-Selected Hatch Repair","CE_HATCHFIXOVERLAP","Repair overlapping hatches in the selected road drawing area.","46 Final Road Production"),
        A("47. CE-Global Hatch Repair","CE_HATCHFIXOVERLAPALL","Review and repair generated road hatch overlaps throughout the current drawing.","47 Final Road Production"),
        A("48. CE-Batch Cross Sections","CE_BATCHCROSSSECTIONS","Create cross sections for all or selected roads using chosen sample-line, section-view and band styles.","48 Final Road Production"),
        A("49. CE-Cross Section View Match","CE_CROSSSECTIONVIEWMATCH","Match source cross-section view styles, bands and vertical/horizontal ranges to all or selected views.","49 Final Road Production"),
        A("50. CE-Cross Section View Fit","CE_CROSSSECTIONVIEWFIT","Fit all or selected cross-section views vertically with configurable row and label clearances.","50 Final Road Production"),
        A("51. CE-Cross Section View Arrange","CE_CROSSSECTIONVIEWARRANGE","Arrange cross-section views from top-left to right grouped by road.","51 Final Road Production"),
        A("52. CE-Vehicle Tracking","CE_VEHICLETRACKING","Review vehicle swept paths, junction turns, wheel edges, clearances and sight-distance checks.","52 Final Road Production"),
        A("53. CE-Road Signs","CE_ROADSIGNS","Place approved linked road signs by alignment, chainage and junction approach with setting-out markers.","53 Final Road Production"),
        A("54. CE-Corridor Region Activation","CE_CORRIDORREGIONSALLON","Activate all corridor baseline regions so they contribute to design and quantities.","54 Final Road Production"),
        A("55. CE-Corridor Region Restoration","CE_CORRIDORREGIONSRESTORE","Restore previously saved corridor-region enabled states.","55 Final Road Production"),
        A("56. CE-Road Sheet Generation","CE_ROADAUTOSHEETS","Generate and assemble final road drawing sheets using the selected sheet template.","56 Final Road Production"),
        A("57. CE-Design Data Export","CE_DESIGNDATAEXPORT","Export road-only or complete design levels, alignment coordinates and profile data to Excel.","57 Final Road Production"),
        A("58. CE-Construction BOQ","CE_ROADBOQCONSTRUCTION","Calculate road construction quantities including earthworks, surfaces, corridors and junction grading.","58 Final Road Production")

});
  }
  private static void Run(string title,string note,IList<DisciplineWorkflowAction> actions){Document d=AcApplication.DocumentManager.MdiActiveDocument;if(d!=null)DisciplineWorkflowDialogs.SelectAndRun(d,title,note,actions);}
  private static DisciplineWorkflowAction A(string t,string c,string d,string g){return new DisciplineWorkflowAction(t,c,d,g);}
 }
}
