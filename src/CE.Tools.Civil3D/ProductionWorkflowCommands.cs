using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ProductionWorkflowCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// One ordered, non-duplicated production page per discipline. The native
    /// commands are intentionally still explicit because Civil 3D commands that
    /// need a pick, surface, insertion point or network cannot be safely guessed;
    /// the page keeps those interactions in the correct start-to-finish order.
    /// </summary>
    public sealed class ProductionWorkflowCommands
    {
        [CommandMethod("CE_TOOLS", "CE_PROJECTWORKFLOW", CommandFlags.Modal)]
        public void Project() { Run("PROJECT", "Project setup → standards/styles → coordination → register/books.", string.Empty, new[]
        {
            Step("Project setup", "CE_PROJECTSETUPCHOICE2", "Load last-saved, company-standard or blank project information.", "01 SETTINGS"),
            Step("Project styles", "CE_PROJECTSTYLES", "Select/import shared Civil 3D styles once for the project.", "01 SETTINGS"),
            Step("Discipline style presets", "CE_DISCIPLINESTYLEPRESETS", "Save or activate independent Roads, Platform, Stormwater, Sewer, Water, Bulk Water, Parking and Flood presets.", "01 SETTINGS"),
            Step("Standards", "CE_STANDARDS", "Record the project design and documentation standards.", "02 PREPARE"),
            Step("Project coordination", "CE_PROJECTCOORDINATION", "Coordinate source drawings, location and page setup.", "02 PREPARE"),
            Step("Metadata refresh", "CE_PROJECTMETADATAREFRESH", "Refresh linked project metadata after coordination changes.", "03 CREATE"),
            Step("Drawing register", "CE_DRAWINGREGISTEREDIT", "Review drawing numbers, titles, revisions and issue data.", "05 COMPLETE"),
            Step("Drawing / client books", "CE_BOOKTOOLS", "Create final drawing books, client books and indexes.", "06 DELIVER")
        }); }

        [CommandMethod("CE_TOOLS", "CE_SURVEYWORKFLOW", CommandFlags.Modal)]
        public void Survey() { Run("SURVEY", "Coordinate system → survey import → existing ground → correction → setting-out/delivery.", "Survey", new[]
        {
            Step("Location / coordinate system", "CE_SURVEYLOCATION", "Assign the project area and installed Namibia LO coordinate system.", "01 SETTINGS"),
            Step("Survey styles", "CE_SURVEYSTYLES", "Choose point, point-label and surface styles.", "01 SETTINGS"),
            Step("LandXML import / export", "CE_LANDXMLTOOLS", "Bring survey interchange data into the drawing or deliver it.", "02 PREPARE"),
            Step("Create surface from survey file", "CE_SURFACEFROMFILE", "Create an existing-ground TIN from TXT/CSV XYZ data.", "03 CREATE"),
            Step("Create surface from drawing objects", "CE_SURFACEFROMOBJECTS", "Create a TIN from COGO/DBPoints, polylines or feature lines when no file is used.", "03 CREATE"),
            Step("Surface tools / point extent", "CE_SURFTOOLS", "Review the existing-ground surface and run surface utilities.", "03 CREATE"),
            Step("Surface correction / review", "CE_SURFCTOOLS", "Audit and create reversible corrected/simplified surface copies.", "04 DESIGN"),
            Step("Vertex setting-out", "CE_VERTEXSETTINGOUT", "Generate linked COGO/MText/MLeader setting-out from the accepted survey geometry.", "05 COMPLETE"),
            Step("Grid setting-out", "CE_GRIDSETTINGOUT", "Generate linked grid/perimeter points where required.", "05 COMPLETE"),
            Step("Base / comparison table", "CE_SURFACECOMPARETABLE", "Compare the accepted survey surface against a selected design/base surface.", "05 COMPLETE"),
            Step("Survey comparison / export", "CE_SURVEYCOMPARETOOLS", "Review differences and export the accepted survey data.", "06 DELIVER"),
            Step("Export selected table", "CE_TABLEEXPORTCSV", "Export the selected CE table to CSV/Excel-compatible output.", "06 DELIVER")
        }); }

        [CommandMethod("CE_TOOLS", "CE_PLATFORMWORKFLOW", CommandFlags.Modal)]
        public void Platform() { Run("PLATFORM", "Source boundaries → feature lines → levels/grading → final surface → setting-out/quantities/drawings.", "Platforms", new[]
        {
            Step("Platform styles", "CE_PLATFORMSTYLES", "Choose feature-line, grading, surface and annotation styles.", "01 SETTINGS"),
            Step("Create feature lines", "CE_FLCREATE", "Create feature lines from selected platform boundaries.", "02 PREPARE"),
            Step("Platform slopes / levels", "CE_PLATFORMSLOPE", "Apply constant/fixed slopes or flatten to the highest elevation.", "03 DESIGN"),
            Step("Grade / daylight to surface", "CE_PLATFORMGRADETOSURFACE", "Create dynamic cut/fill daylight feature lines using the selected natural-ground surface.", "03 DESIGN"),
            Step("Stepped platform offsets", "CE_PLATFORMSTEPOFFSETS", "Create linked stepped offsets for multiple platforms.", "03 DESIGN"),
            Step("Drape / platform surface", "CE_PLATFORMDRAPE", "Drape accepted platform controls to the selected surface.", "03 DESIGN"),
            Step("Merge road / grading outputs", "CE_ROADSURFACEMERGE", "Create CE Final Surface from selected natural ground and all or selected TOP/grading surfaces.", "04 SURFACES"),
            Step("Platform setting-out", "CE_PLATFORMSETTINGOUT", "Generate linked platform vertex/grid setting-out and tables.", "05 COMPLETE"),
            Step("Platform names / register", "CE_PLATFORMTABLE", "Create the linked platform name/elevation register.", "05 COMPLETE"),
            Step("Platform cut / fill", "CE_PLATFORMCUTFILL", "Calculate linked NG-versus-design quantities.", "06 DELIVER"),
            Step("Platform drawings / sections", "CE_PLATFORMDRAWINGS", "Create platform layouts and section source lines.", "06 DELIVER")
        }); }

        [CommandMethod("CE_TOOLS", "CE_ROADWORKFLOW", CommandFlags.Modal)]
        public void Road() { Run("ROAD", "Road settings → reserve geometry/junctions → names/dimensions → alignments/profiles/corridors → merged surfaces → setting-out/BOQ/report.", "Roads", new[]
        {
            Step("Road settings", "CE_ROADSETTINGS", "Set alignment, profile, profile-view, band, corridor and assembly styles.", "01 SETTINGS"),
            Step("Road styles", "CE_ROADSTYLES", "Activate the road-only Civil 3D style preset.", "01 SETTINGS"),
            Step("Close source reserve polylines", "CE_ROADRESERVECLOSE", "Close selected/all open plot or reserve polylines.", "02 PREPARE"),
            Step("Create road-reserve centrelines", "CE_ROADRESERVECENTERLINES", "Create connected centreline strings from opposing reserve boundaries.", "02 PREPARE"),
            Step("Join continuous centrelines", "CE_ROADCONTINUITYFIX", "Join centreline strings that represent one continuous road.", "02 PREPARE"),
            Step("Horizontal curves", "CE_ROUTEHORIZONTALCURVES", "Apply specified tangent radii to multiple road centrelines.", "03 GEOMETRY"),
            Step("Road offsets", "CE_ROADOFFSET", "Create road-edge/offset geometry.", "03 GEOMETRY"),
            Step("Junction bellmouths", "CE_ROADJUNCTIONBULK", "Create T/cross-junction bellmouths.", "04 JUNCTIONS"),
            Step("Junction trim", "CE_ROADJUNCTIONTRIM", "Trim multiple junctions and tangent edges.", "04 JUNCTIONS"),
            Step("Bellmouth tangent trim", "CE_BELLMOUTHTRIMEDGES", "Trim road and sidewalk/shoulder edges to generated tangent stations.", "04 JUNCTIONS"),
            Step("Road names", "CE_ROADNAMES", "Create/update linked road names at cross/T-junction midpoint spans.", "05 ANNOTATION"),
            Step("Synchronize road names", "CE_ROADNAMESYNC", "Synchronize names across alignments, profiles, corridors, sections and assemblies.", "05 ANNOTATION"),
            Step("Road dimensions", "CE_ROADDIMENSIONS", "Create/update linked road dimensions while avoiding label overlap.", "05 ANNOTATION"),
            Step("Road alignments", "CE_ROADALIGN", "Create linked Civil 3D road alignments.", "06 CIVIL 3D"),
            Step("NGL / final profiles", "CE_ROADPROFILEFULL", "Create the NGL and final road profiles with vertical curves and profile-view styling.", "06 CIVIL 3D"),
            Step("Road assemblies", "CE_ASSEMBLYTOOLS", "Create/select the road assemblies used by the corridors.", "06 CIVIL 3D"),
            Step("Road corridors", "CE_ROADCORRIDORFULL", "Build corridors, regions, targets, frequencies, CE-TOP and CE-BOTTOM surfaces.", "06 CIVIL 3D"),
            Step("Road TOP/BOTTOM and final surface outputs", "CE_ROADSURFACEMERGE", "Create CE Top All, CE Bottom All or CE Final Surface without changing numbered source surfaces.", "07 SURFACES"),
            Step("Junction design", "CE_ROADJUNCTIONCONSTRUCTIONTOOLS", "Complete junction geometry, regions and output checks.", "08 JUNCTION DESIGN"),
            Step("Bellmouth setting-out", "CE_JUNCTIONSETTINGOUT4", "Erase/replace the selected junction setting-out groups and number each junction top-left clockwise to bottom-left by owning road.", "09 SETTING-OUT"),
            Step("Refresh linked model data", "CE_REFRESHALL", "Refresh linked road layout, corridor and annotation outputs.", "10 REFRESH"),
            Step("Road construction BOQ", "CE_ROADBOQCONSTRUCTION", "Create/update live corridor construction quantities.", "11 DELIVER"),
            Step("Road design report", "CE_REPORTROAD", "Generate the road design report.", "11 DELIVER"),
            Step("Road drawing book", "CE_DRAWINGBOOKROAD", "Create the road drawing production output.", "11 DELIVER")
        }); }

        [CommandMethod("CE_TOOLS", "CE_STORMWATERWORKFLOW", CommandFlags.Modal)]
        public void Stormwater() { Run("STORMWATER", "Stormwater settings/parts → route/network → sequence → design → profiles/labels/setting-out → BOQ/report.", "Stormwater", new[]
        {
            Step("Stormwater settings", "CE_SWSETTINGS", "Choose stormwater alignment/profile/profile-view/band styles, layers and labels.", "01 SETTINGS"),
            Step("Stormwater styles", "CE_SWSTYLES", "Activate stormwater-specific styles, rules and parts.", "01 SETTINGS"),
            Step("Utility route from road reserve", "CE_UTILITYFROMROADRESERVE", "Create preliminary stormwater route geometry using the Stormwater layer/discipline choice.", "02 PREPARE"),
            Step("Multiple networks from polylines", "CE_NETWORKFROMPOLYLINESBATCH", "Create multiple gravity networks from selected sources with duplicate-source protection.", "03 CREATE"),
            Step("Sequence stormwater network", "CE_SWSEQ", "Build main/branch order and apply stormwater network naming.", "03 CREATE"),
            Step("Network data / levels", "CE_NETWORKDATA", "Review stormwater pipe/structure levels, lengths and slopes.", "04 DESIGN"),
            Step("Create / refresh alignments", "CE_SWALIGN", "Create linked stormwater branch alignments and plan labels.", "05 COMPLETE"),
            Step("Safe alignment fallback", "CE_SWALIGNSAFE", "Use the stormwater-specific direct alignment fallback for duplicate/zero-length source geometry.", "05 COMPLETE"),
            Step("Create / refresh profiles", "CE_SWPROFILE", "Create linked stormwater surface profiles/profile views using the stormwater styles and parts.", "05 COMPLETE"),
            Step("Pipe / structure labels", "CE_SWLABELS", "Apply stormwater-specific plan labels without duplicating existing labels.", "05 COMPLETE"),
            Step("Vertex setting-out", "CE_VERTEXSETTINGOUT", "Generate linked stormwater setting-out and leaders.", "05 COMPLETE"),
            Step("Stormwater BOQ", "CE_BOQSTORMWATER", "Create linked stormwater quantities.", "06 DELIVER"),
            Step("Stormwater report", "CE_REPORTSTORMWATER", "Generate stormwater report/drawing handoff.", "06 DELIVER")
        }); }

        [CommandMethod("CE_TOOLS", "CE_SEWERWORKFLOW", CommandFlags.Modal)]
        public void Sewer() { Run("SEWER", "Sewer settings/parts → cadastral/midblock/road-reserve route → network sequence → rules/levels → profiles/bands/labels → setting-out/BOQ/report.", "Sewer", new[]
        {
            Step("Sewer settings", "CE_SEWSETTINGS", "Choose sewer-specific parts, rule sets, styles, labels, profile and band settings.", "01 SETTINGS"),
            Step("Sewer styles", "CE_SEWERSTYLES", "Activate sewer-specific styles and layer preset.", "01 SETTINGS"),
            Step("Cadastral / midblock route", "CE_MIDBLOCKSEWERPRODUCTION", "Create the continuous selected-side/low-side sewer route and planning manholes.", "02 PREPARE"),
            Step("Road-reserve sewer route", "CE_SEWERFROMROADRESERVE", "Create the separate sewer-only road-reserve route when that option is required.", "02 PREPARE"),
            Step("Multiple sewer networks", "CE_NETWORKFROMPOLYLINESBATCH", "Create selected gravity networks without duplicate source runs.", "03 CREATE"),
            Step("Sequence network + branches", "CE_SEWSEQWORKFLOW", "Sequence and number the complete connected sewer network.", "03 CREATE"),
            Step("Apply sewer rules / network data", "CE_SEWAPPLYRULESMULTI", "Apply the selected sewer surface and rule sets to selected parts, then review levels, lengths and slopes.", "04 DESIGN"),
            Step("Sewer alignments", "CE_SEWALIGN", "Create linked branch alignments.", "05 COMPLETE"),
            Step("Flow toward outlet / low point", "CE_SEWFLOWTOOUTLET", "Set gravity-pipe flow toward one selected downstream structure without moving endpoints or changing rules.", "05 COMPLETE"),
            Step("Sewer profiles / long sections", "CE_SEWPROFILE", "Create isolated branch profile views with the sewer ground/network sources.", "05 COMPLETE"),
            Step("Sewer long-section bands", "CE_SEWBANDLABELS", "Apply/repair native sewer bands and data sources.", "05 COMPLETE"),
            Step("Sewer labels", "CE_SEWLABELS", "Apply sewer-specific pipe/structure labels and branch presentation.", "05 COMPLETE"),
            Step("Vertex setting-out", "CE_VERTEXSETTINGOUT", "Generate linked sewer setting-out and leaders.", "05 COMPLETE"),
            Step("Sewer BOQ", "CE_BOQSEWER", "Create linked sewer quantities.", "06 DELIVER"),
            Step("Sewer report", "CE_REPORTSEWER", "Generate sewer report/drawing handoff.", "06 DELIVER")
        }); }

        [CommandMethod("CE_TOOLS", "CE_WATERWORKFLOW", CommandFlags.Modal)]
        public void Water() { Run("WATER", "Water settings/pressure parts → route/network → sequence → alignments/profiles/assets → setting-out/BOQ/report.", "Water", new[]
        {
            Step("Water settings", "CE_WATERSETTINGS", "Choose water-specific pressure parts, rules, alignment/profile styles, labels and spacing.", "01 SETTINGS"),
            Step("Water styles", "CE_WATERSTYLES", "Activate water-specific styles, rules and parts.", "01 SETTINGS"),
            Step("Utility route from road reserve", "CE_UTILITYFROMROADRESERVE", "Create preliminary water route geometry using the Water layer/discipline choice.", "02 PREPARE"),
            Step("Multiple water networks", "CE_NETWORKFROMPOLYLINESBATCH", "Create selected pressure networks without duplicate source runs.", "03 CREATE"),
            Step("Sequence mains and branches", "CE_WATERSEQ", "Create W-MAIN and branch route order.", "03 CREATE"),
            Step("Network data / levels", "CE_NETWORKDATA", "Review water pressure-network objects and levels.", "04 DESIGN"),
            Step("Water alignments", "CE_WATERALIGN", "Create/refresh linked water alignments.", "05 COMPLETE"),
            Step("Water profiles", "CE_WATERPROFILE", "Create profiles/profile views using water pressure-part projection where supported.", "05 COMPLETE"),
            Step("Safe water profile fallback", "CE_WATERPROFILESAFE", "Use the water-specific direct surface profile fallback when pressure projection is unavailable.", "05 COMPLETE"),
            Step("Water pressure-part labels", "CE_WATERLABELS", "Apply water-specific pressure pipe/fitting/appurtenance labels.", "05 COMPLETE"),
            Step("Valve / hydrant review markers", "CE_WATERPLACE", "Place linked water asset review markers.", "05 COMPLETE"),
            Step("Vertex setting-out", "CE_VERTEXSETTINGOUT", "Generate linked water setting-out and leaders.", "05 COMPLETE"),
            Step("Water BOQ", "CE_BOQWATER", "Create linked water quantities.", "06 DELIVER"),
            Step("Water report", "CE_REPORTWATER", "Generate water report/drawing handoff.", "06 DELIVER")
        }); }

        [CommandMethod("CE_TOOLS", "CE_BULKWATERWORKFLOW", CommandFlags.Modal)]
        public void BulkWater() { Run("BULK WATER", "Bulk-water settings/pressure parts → long route/network → profiles/assets → setting-out/BOQ/report.", "Bulk Water", new[]
        {
            Step("Bulk-water styles", "CE_BULKWATERSTYLES", "Choose bulk-water pressure-network, rule, profile and label styles.", "01 SETTINGS"),
            Step("Utility route from road reserve", "CE_UTILITYFROMROADRESERVE", "Create preliminary bulk-water route geometry using the Bulk Water discipline choice.", "02 PREPARE"),
            Step("Multiple pressure networks", "CE_NETWORKFROMPOLYLINESBATCH", "Batch selected source polylines/feature lines into pressure networks.", "03 CREATE"),
            Step("Network data / levels", "CE_NETWORKDATA", "Review bulk-water network objects and levels.", "04 DESIGN"),
            Step("Bulk-water profile fallback", "CE_WATERPROFILESAFE", "Create direct surface profiles/profile views for bulk-water long routes.", "05 COMPLETE"),
            Step("Bulk-water pressure-part labels", "CE_BULKWATERLABELS", "Apply bulk-water-specific pressure-part labels.", "05 COMPLETE"),
            Step("Vertex setting-out", "CE_VERTEXSETTINGOUT", "Generate linked bulk-water setting-out.", "05 COMPLETE"),
            Step("Bulk-water BOQ", "CE_BOQBULKWATER", "Create linked bulk-water quantities.", "06 DELIVER"),
            Step("Bulk-water report", "CE_REPORTBULKWATER", "Generate bulk-water report/drawing handoff.", "06 DELIVER")
        }); }

        [CommandMethod("CE_TOOLS", "CE_PARKINGWORKFLOW", CommandFlags.Modal)]
        public void Parking() { Run("PARKING", "Boundary/options → obstacle-aware layout → grading → numbers/checks/setting-out → quantities.", "Parking", new[]
        {
            Step("Parking tools / settings", "CE_PKTOOLS", "Set parking layout and annotation settings.", "01 SETTINGS"),
            Step("Parking styles", "CE_PARKINGSTYLES", "Activate parking-specific styles.", "01 SETTINGS"),
            Step("Parking options", "CE_PARKOPTIONS", "Compare arrangements inside a selected boundary.", "02 PREPARE"),
            Step("Parking optimiser", "CE_PARKOPTIMIZE", "Create the obstacle-aware parking alternative.", "03 CREATE"),
            Step("Parking grading", "CE_PARKGRADINGTOOLS", "Create linked grading/drainage guides.", "04 DESIGN"),
            Step("Parking numbers", "CE_PKNUMBERDYNAMIC", "Create linked annotative P-number labels.", "05 COMPLETE"),
            Step("Parking skew / width validation", "CE_PKSKVALIDATE", "Check bay width and skew.", "05 COMPLETE"),
            Step("Parking setting-out", "CE_GRIDSETTINGOUT", "Create grid/perimeter setting-out where applicable.", "05 COMPLETE"),
            Step("Parking quantities", "CE_PARKQTYTOOLS", "Create/export parking and layerwork quantities.", "06 DELIVER")
        }); }

        [CommandMethod("CE_TOOLS", "CE_FLOODWORKFLOW", CommandFlags.Modal)]
        public void Flood() { Run("FLOOD", "Terrain/catchment → hydrology → return-period review → affected area → culvert/report delivery.", "Flood", new[]
        {
            Step("Hydrology / flood inputs", "CE_HYDROLOGYTOOLS", "Review rainfall/runoff and analysis settings.", "01 SETTINGS"),
            Step("Flood styles", "CE_FLOODSTYLES", "Activate flood-specific styles.", "01 SETTINGS"),
            Step("Surface / catchment review", "CE_SURFTOOLS", "Review the terrain source before calculations.", "02 PREPARE"),
            Step("Quick flood / rational review", "CE_FLOODQUICK", "Run preliminary return-period peak-flow and culvert screening.", "03 CREATE"),
            Step("Surface hydrology", "CE_HYDROLOGYREVIEW", "Review flow routes, catchments and terrain storage.", "04 DESIGN"),
            Step("Affected property / flood results", "CE_FLOODRESULTTOOLS", "Review specialist flood results and affected properties.", "04 DESIGN"),
            Step("Culvert review", "CE_CULVERTREVIEW", "Review candidate crossings and culvert requirements.", "05 COMPLETE"),
            Step("Flood report", "CE_REPORTFULL", "Generate the final project/discipline report.", "06 DELIVER")
        }); }

        private static void Run(string title, string note, string discipline, IList<DisciplineWorkflowAction> actions)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            if (!string.IsNullOrWhiteSpace(discipline))
            {
                try { August11DisciplineStylePresetManager.ActivateForProduction(document.Database, discipline); }
                catch { }
            }
            DisciplineWorkflowDialogs.SelectAndRun(document, "CE-" + title + " PRODUCTION WORKFLOW", note, actions);
        }

        private static DisciplineWorkflowAction Step(string title, string command, string description, string group)
        {
            return new DisciplineWorkflowAction(title, command, description, group);
        }
    }
}
