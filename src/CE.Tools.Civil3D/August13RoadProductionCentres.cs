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
    A("CE-Road Design Production","CE_ROADDESIGNPRODUCTIONCENTRE","Alignments, profiles, corridors, junction design, BOQ and drawings.","01 Road Production")
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
    A("CE-Corridor Feature Lines","CE_CORRIDORFEATURELINES","Create individual feature lines from selected corridor centreline/edge/kerb/sidewalk/shoulder/toe codes or all corridor feature lines.","03 Corridors"),
    A("CE-Split Corridors","CE_CORSPLIT","Split corridor regions at specified stations.","03 Corridors"),
    A("CE-Rebuild Corridors","CE_CORREBUILD","Rebuild corridor model data.","03 Corridors"),
    A("CE-Road Junction Design","CE_ROADJUNCTIONCONSTRUCTIONTOOLS","Create/finalize junction geometry, corridor regions and outputs.","04 Junction Design"),
    A("CE-Junction Stepped Offset Fallback","CE_JUNCTIONSTEPPEDOFFSETWORKFLOW","Use feature-line stepped offsets when corridor junction design is unreliable.","04 Junction Design"),
    A("CE-Refresh All Linked Model Data","CE_REFRESHALL","Refresh linked model data.","05 Refresh"),
    A("CE-Road Production Information","CE_ROADPRODUCTIONINFO","Open road production information.","06 Reports"),
    A("CE-Road Profile Report","CE_ROADPROFILEREPORT","Report road profile views and station ranges.","06 Reports"),
    A("CE-Road Corridor Report","CE_CORREPORT","Generate corridor report.","06 Reports"),
    A("CE-Road Construction Bill of Quantities","CE_ROADBOQCONSTRUCTION","Create/update live corridor construction quantities.","07 Deliver"),
    A("CE-Detail Road Design Report","CE_REPORTROAD","Generate detailed road design report.","07 Deliver"),
    A("CE-Road Drawing Production","CE_DRAWINGBOOKROAD","Create road drawing production output.","07 Deliver")
   });
  }
  private static void Run(string title,string note,IList<DisciplineWorkflowAction> actions){Document d=AcApplication.DocumentManager.MdiActiveDocument;if(d!=null)DisciplineWorkflowDialogs.SelectAndRun(d,title,note,actions);}
  private static DisciplineWorkflowAction A(string t,string c,string d,string g){return new DisciplineWorkflowAction(t,c,d,g);}
 }
}
