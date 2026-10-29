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
    A("CE-Road Design Production","CE_ROADDESIGNPRODUCTIONCENTRE","Alignments, profiles, corridors, junction design, BOQ and drawings.","01 Road Production"),
    A("FINAL ROAD PRODUCTION","CE_FINALROADPRODUCTION","Run the approved road-production sequence from ROAD SEQUENCE.docx.","01 Road Production")
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
    A("CE-Road Surface Outputs","CE_ROADSURFACEMERGE","Merge all road TOP surfaces to CE Top All, all road BOTTOM surfaces to CE Bottom All, or selected natural ground plus road TOP/grading surfaces to CE Final Surface.","03 Corridors"),
    A("CE-Merge All Junction Surfaces", "CE_JUNCTIONSURFACEMERGE", "Create CE Junctions All from all CE junction grading and kerb/gutter infill TIN surfaces.", "04 DESIGN"),
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
    A("CE-Automatic Road Plan / Profile Sheets","CE_ROADAUTOSHEETS","Split long roads by chainage, split curved plan ranges into adjacent independently rotated viewports, clone A0/A1 layouts and stack matching plan/profile sets.","07 Deliver"),
    A("CE-Detail Road Design Report","CE_REPORTROAD","Generate detailed road design report.","07 Deliver"),
    A("CE-Road Drawing Production","CE_DRAWINGBOOKROAD","Create road drawing production output.","07 Deliver")
   });
  }
  [CommandMethod("CE_TOOLS","CE_FINALROADPRODUCTION",CommandFlags.Modal)]
  public void FinalRoadProduction()
  {
   Run("FINAL ROAD PRODUCTION",
       "Approved sequence from ROAD SEQUENCE.docx. Choose each step in order; commands run individually so you can review the drawing between stages.",
       new List<DisciplineWorkflowAction>
       {
        A("01. CE-Road Settings","CE_ROADSETTINGS","Road production sequence step 1.","01 Road Sequence"),
        A("02. CE-Road Styles","CE_ROADSTYLES","Road production sequence step 2.","02 Road Sequence"),
        A("03. Road Reserve Centres - Generate + Clean","CE_ROADRESERVECENTRELINES","Road production sequence step 3.","03 Road Sequence"),
        A("04. Strict Road Centre Cleanup - Start / BC / EC / End","CE_ROADCENTRECLEANSTRICT","Road production sequence step 4.","04 Road Sequence"),
        A("05. CE-Create Road Alignments","CE_ROADALIGN","Road production sequence step 5.","05 Road Sequence"),
        A("06. Alignment Label Set - Multiple Alignments","CE_ALIGNLABELSETMULTI","Road production sequence step 6.","06 Road Sequence"),
        A("07. Road Names - Layers / Junction Midpoints","CE_ROADNAMES","Road production sequence step 7.","07 Road Sequence"),
        A("08. CE-Sort Road Alignments","CE_ALIGNMENTSORT","Road production sequence step 8.","08 Road Sequence"),
        A("09. CE-Road Name Sync","CE_ROADNAMESYNC","Road production sequence step 9.","09 Road Sequence"),
        A("10. Road Name Audit","CE_ROADNAMEAUDIT","Road production sequence step 10.","10 Road Sequence"),
        A("11. Create CE road assembly","CE_ASSEMBLYCREATE","Road production sequence step 11.","11 Road Sequence"),
        A("12. CE-NGL and Final Road Profiles","CE_ROADPROFILEFULL","Road production sequence step 12.","12 Road Sequence"),
        A("13. Assign Profile View Styles to Multiple Views","CE_PROFILEVIEWSTYLEMULTI","Road production sequence step 13.","13 Road Sequence"),
        A("14. Import Band Set / Repair Road Band Sources","CE_PROFILEVIEWDATASOURCES","Road production sequence step 14.","14 Road Sequence"),
        A("15. CE-Match Profile View Properties","CE_PROFILEVIEWMATCH","Road production sequence step 15.","15 Road Sequence"),
        A("16. CE-Fit All Profile Views","CE_PROFILEVIEWFITALL","Road production sequence step 16.","16 Road Sequence"),
        A("17. Arrange Multiple Profile Views","CE_PROFILEVIEWARRANGE","Road production sequence step 17.","17 Road Sequence"),
        A("18. CE-Road Corridors","CE_ROADCORRIDORFULL","Road production sequence step 18.","18 Road Sequence"),
        A("19. CE-Split Road Profile Views","CE_ROADPROFILEVIEWSPLIT","Road production sequence step 19.","19 Road Sequence"),
        A("20. Batch T/Cross Bellmouths - Feature-Line Output","CE_ROADJUNCTIONBATCH","Road production sequence step 20.","20 Road Sequence"),
        A("21. T-Junction Assembly Limits / Remove Short Regions","CE_ROADTJUNCTIONASSEMBLYLIMITS","Road production sequence step 21.","21 Road Sequence"),
        A("22. Split Multiple Corridors at Junctions","CE_ROADJUNCTIONCONSTRUCTION","Road production sequence step 22.","22 Road Sequence"),
        A("23. Junction Stepped Offset Workflow","CE_JUNCTIONSTEPPEDOFFSETWORKFLOW","Road production sequence step 23.","23 Road Sequence"),
        A("24. Connect Endpoints","CE_CONNECTENDPOINTS","Road production sequence step 24.","24 Road Sequence"),
        A("25. Junction Grading Batch","CE_JUNCTIONGRADETOSURFACE","Road production sequence step 25.","25 Road Sequence"),
        A("26. Elevation from Surface - Multiple Feature Lines","CE_PLATFORMDRAPEMULTI","Road production sequence step 26.","26 Road Sequence"),
        A("27. Left / Right Road Edge Levels","CE_ROADEDGELEVELS","Road production sequence step 27.","27 Road Sequence"),
        A("28. Add Surfaces to Profile Views","CE_SURFACEADDTOVIEWS","Road production sequence step 28.","28 Road Sequence"),
        A("29. TOP/BOTTOM Surfaces in Selected Profile Views","CE_ROADTOPBOTTOMPROFILE","Road production sequence step 29.","29 Road Sequence"),
        A("30. Add Road Crossing PVIs","CE_ROADCROSSINGPVIS","Create cross-road FG profile PVIs after TOP/BOTTOM surfaces are available.","30 Road Sequence"),
        A("31. Remove Road Crossing PVIs","CE_REMOVEROADCROSSINGPVIS","Review/remove eligible crossing PVIs before reshaping curves.","31 Road Sequence"),
        A("32. Add / Repair Road Vertical Curves","CE_ROADVERTICALCURVES","Construct/review required vertical curves after crossing PVI work.","32 Road Sequence"),
        A("33. CE-Complete Road Corridors","CE_ROADCORRIDORCOMPLETE","Road production sequence step 33.","33 Road Sequence"),
        A("34. Change Multiple Surface Styles","CE_SURFACESTYLEMULTI","Road production sequence step 34.","34 Road Sequence"),
        A("35. Reverse Multiple Road Alignments + Profiles","CE_ROADALIGNREVERSEMULTI","Road production sequence step 35.","35 Road Sequence"),
        A("36. Road Width Dimensions - Junction Midpoints","CE_ROADDIMENSIONS","Road production sequence step 36.","36 Road Sequence"),
        A("37. Junction Setting-Out - Road Number / Clockwise","CE_JUNCTIONSETTINGOUT4","Road production sequence step 37.","37 Road Sequence"),
        A("38. CE-Road Hatch Sides","CE_ROADHATCHSIDES","Road production sequence step 38.","38 Road Sequence"),
        A("39. Repair Selected Hatch Overlaps","CE_HATCHFIXOVERLAP","Road production sequence step 39.","39 Road Sequence"),
        A("40. Repair All Hatch Overlaps","CE_HATCHFIXOVERLAPALL","Road production sequence step 40.","40 Road Sequence"),
        A("41. CE-All Corridor Regions On","CE_CORRIDORREGIONSALLON","Road production sequence step 41.","41 Road Sequence"),
        A("42. CE-Restore Corridor Regions","CE_CORRIDORREGIONSRESTORE","Road production sequence step 42.","42 Road Sequence"),
        A("43. CE-Road Construction BOQ","CE_ROADBOQCONSTRUCTION","Road production sequence step 43.","43 Road Sequence"),
        A("44. CE-Create Road Sheets","CE_ROADAUTOSHEETS","Road production sequence step 44.","44 Road Sequence")
       });
  }
  private static void Run(string title,string note,IList<DisciplineWorkflowAction> actions){Document d=AcApplication.DocumentManager.MdiActiveDocument;if(d!=null)DisciplineWorkflowDialogs.SelectAndRun(d,title,note,actions);}
  private static DisciplineWorkflowAction A(string t,string c,string d,string g){return new DisciplineWorkflowAction(t,c,d,g);}
 }
}
