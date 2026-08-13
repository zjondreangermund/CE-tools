using System;
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
using CivilProfileView = Autodesk.Civil.DatabaseServices.ProfileView;

[assembly: CommandClass(typeof(CETools.Civil3D.August13RoadProductionWorkflowEnhancements))]

namespace CETools.Civil3D
{
    /// <summary>
    /// 13-August road-production workflow refinements requested during field review.
    /// The staged centres keep layout and Civil 3D design separate, while the helper
    /// commands add station-section profile views, selection by polyline length,
    /// X/Y value swapping, ordered four-return junction setting-out, corridor-region
    /// splitting and a stepped-feature-line junction fallback.
    /// </summary>
    public sealed class August13RoadProductionWorkflowEnhancements
    {
        private const double Tolerance = 1e-7;
        private const string JunctionLayer = "CE-ROAD-JUNCTION";

        [CommandMethod("CE_TOOLS", "CE_ROADPRODUCTIONV2", CommandFlags.Modal)]
        public void RoadProductionV2()
        {
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE-ROAD PRODUCTION",
                "Choose Road Settings, Road Layout Production or Road Design Production. Layout geometry is completed before Civil 3D alignments, profiles and corridors.",
                new List<DisciplineWorkflowAction>
                {
                    Action("CE-Road Settings", "CE_ROADSETTINGSCENTRE", "Road styles, Civil 3D project settings and production defaults.", "01 Road Production"),
                    Action("CE-Road Layout Production", "CE_ROADLAYOUTPRODUCTIONCENTRE", "Cadastral/reserve geometry, road strings, junctions, names, dimensions and setting-out.", "01 Road Production"),
                    Action("CE-Road Design Production", "CE_ROADDESIGNPRODUCTIONCENTRE", "Alignments, NGL/final profiles, profile-view sections, assemblies, corridors, junction design, BOQ and drawings.", "01 Road Production")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_ROADSETTINGSCENTRE", CommandFlags.Modal)]
        public void RoadSettingsCentre()
        {
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE-Road Settings",
                "Road-only settings before layout or Civil 3D production.",
                new List<DisciplineWorkflowAction>
                {
                    Action("Project Road Styles", "CE_PROJECTSTYLES", "Alignment, profile, profile-view, band-set, assembly, corridor and annotation styles.", "01 Settings"),
                    Action("Road Project Settings", "CE_ROADPROJECTSETTINGS", "Road production layers, styles and linked road defaults.", "01 Settings"),
                    Action("Profile View Batch Styles", "CE_PROFILEVIEWBATCHTOOLS", "Review/apply profile-view and band-set styles before production.", "02 Profile Views"),
                    Action("Setting-Out / Coordinate Options", "CE_VERTEXSETTINGOUTTOOLS", "Point, table, coordinate display and linked setting-out options.", "03 Setting-Out"),
                    Action("Swap X/Y Values in Setting-Out Tables", "CE_SETTINGOUTSWAPXY", "Exchange displayed X and Y numeric values in selected setting-out tables without changing drawing geometry.", "03 Setting-Out")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_ROADLAYOUTPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void RoadLayoutProductionCentre()
        {
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE-Road Layout Production",
                "Complete the cadastral/reserve road layout first. The sequence follows the requested production order and keeps junction setting-out grouped four returns at a time.",
                new List<DisciplineWorkflowAction>
                {
                    Action("CE-Closed Polylines for All Plots", "CE_ROADRESERVECLOSE", "Close plot/reserve source polylines before road layout generation.", "01 Source Geometry"),
                    Action("CE-Road Reserve Centrelines", "CE_ROADOVERLAY", "Generate road reserve centrelines from the selected cadastral/reserve geometry.", "02 Centrelines"),
                    Action("CE-Join Continuous Road Reserve Centrelines", "CE_ROADCONTINUITYFIX", "Join touching reserve centreline pieces and continue through junctions on the straightest route.", "02 Centrelines"),
                    Action("CE-Multiple Horizontal Centreline Curves", "CE_ROADCURVE", "Create/review multiple horizontal centreline curves.", "03 Geometry"),
                    Action("CE-Road Offsets", "CE_ROADOFFSET", "Create linked road-edge and related offsets.", "03 Geometry"),
                    Action("CE-Multiple T/Cross Junction Bellmouths", "CE_ROADJUNCTIONBULK", "Create multiple T/cross-junction bellmouth returns.", "04 Junctions"),
                    Action("CE-Multiple Junction Trim", "CE_ROADJUNCTIONTRIM", "Trim multiple junctions after bellmouth generation.", "04 Junctions"),
                    Action("CE-Road Names", "CE_ROADNAME", "Create/update road names on the linked layout.", "05 Annotation"),
                    Action("CE-Annotation Presentation", "CE_ROUTEANNOTATIONSTYLE", "Apply paper-size annotation presentation, masks and arrow settings.", "05 Annotation"),
                    Action("CE-Shift Annotations", "CE_ROUTESHIFTANNOTATION", "Move selected route annotations together to resolve overlap.", "05 Annotation"),
                    Action("CE-Road Dimensions", "CE_ROADDIM", "Create/update road layout dimensions.", "05 Annotation"),
                    Action("CE-Multiple Junction Bellmouth Setting-out", "CE_JUNCTIONSETTINGOUT4FIX", "Complete up to four bellmouth/return curves for one junction before continuing to the next junction.", "06 Setting-Out"),
                    Action("CE-Select Polylines Shorter Than Length", "CE_SELECTPOLYSHORTER", "Select all current-space polylines shorter than a user-specified length.", "07 Selection Utilities"),
                    Action("CE-Select Polylines With Same Length", "CE_SELECTPOLYSAMELENGTH", "Use one reference polyline and select all polylines matching its length within tolerance.", "07 Selection Utilities"),
                    Action("CE-Refresh Linked Road Layout", "CE_ROADREFRESH", "Refresh linked road layout geometry and annotations.", "08 Refresh")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_ROADDESIGNPRODUCTIONCENTRE", CommandFlags.Modal)]
        public void RoadDesignProductionCentre()
        {
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE-Road Design Production",
                "Civil 3D road design after the layout is complete. Profile views can be produced in fixed station sections and junction corridors have a stepped-feature-line fallback.",
                new List<DisciplineWorkflowAction>
                {
                    Action("CE-Create Road Alignments", "CE_ROADALIGN", "Create linked Civil 3D road alignments.", "01 Alignment"),
                    Action("CE-NGL and Final Road Profiles", "CE_ROADPROFILEFULL", "Create NGL and final editable road profiles and bind them to profile views.", "02 Profiles"),
                    Action("CE-Split Road Profile Views", "CE_ROADPROFILEVIEWSPLIT", "Create profile-view sections such as 0.000-750.000, 750.000-1500.000 and continue to alignment end.", "02 Profiles"),
                    Action("CE-Road Assemblies", "CE_ASSEMBLYTOOLS", "Create/select road assemblies and verify assembly availability.", "03 Corridors"),
                    Action("CE-Road Corridors", "CE_ROADCORRIDORFULL", "Create and complete road corridors, regions, targets and surfaces.", "03 Corridors"),
                    Action("CE-Split Corridors", "CE_CORSPLIT", "Split selected corridor baseline regions at specified stations.", "03 Corridors"),
                    Action("CE-Rebuild Corridors", "CE_CORREBUILD", "Rebuild selected/all corridor model data.", "03 Corridors"),
                    Action("CE-Road Junction Design", "CE_ROADJUNCTIONCONSTRUCT", "Create/update the Civil 3D junction design.", "04 Junction Design"),
                    Action("CE-Junction Stepped Offset Fallback", "CE_JUNCTIONSTEPPEDOFFSETWORKFLOW", "Fallback workflow for gutters, kerbs, sidewalk/shoulder, daylight and closed junction infill when corridor junctions cannot be completed.", "04 Junction Design"),
                    Action("CE-Refresh All Linked Model Data", "CE_REFRESHALL", "Refresh all linked design/model data after edits.", "05 Refresh"),
                    Action("CE-Road Production Information", "CE_ROADPRODUCTIONINFO", "Quick road model inventory and production status.", "06 Information / Reports"),
                    Action("CE-Road Profile Report", "CE_ROADPROFILEREPORT", "Report road profile-view names, alignments and station ranges.", "06 Information / Reports"),
                    Action("CE-Road Corridor Report", "CE_CORREPORT", "Generate the corridor report.", "06 Information / Reports"),
                    Action("CE-Road Bill of Quantities", "CE_BOQROAD", "Create/update linked road quantities.", "07 Deliver"),
                    Action("CE-Detail Road Design Report", "CE_REPORTROAD", "Generate the detailed road design report.", "07 Deliver"),
                    Action("CE-Road Drawing Production", "CE_DRAWINGBOOKROAD", "Create the road drawing production/book output.", "07 Deliver")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_SELECTPOLYSHORTER", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void SelectPolylinesShorterThanLength()
        {
            Document document = Active();
            if (document == null) return;
            PromptDoubleOptions options = new PromptDoubleOptions("\nSelect polylines shorter than length: ")
            {
                AllowNegative = false,
                AllowZero = false,
                AllowNone = false
            };
            PromptDoubleResult lengthResult = document.Editor.GetDouble(options);
            if (lengthResult.Status != PromptStatus.OK) return;

            List<ObjectId> matches = FindCurrentSpacePolylines(document, delegate(Curve curve)
            {
                double length;
                return TryCurveLength(curve, out length) && length < lengthResult.Value - Tolerance;
            });
            document.Editor.SetImpliedSelection(matches.ToArray());
            document.Editor.WriteMessage("\nCE_SELECTPOLYSHORTER: selected {0} polyline(s) shorter than {1:N3}.", matches.Count, lengthResult.Value);
        }

        [CommandMethod("CE_TOOLS", "CE_SELECTPOLYSAMELENGTH", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void SelectPolylinesWithSameLength()
        {
            Document document = Active();
            if (document == null) return;
            var entityOptions = new PromptEntityOptions("\nSelect reference polyline: ");
            entityOptions.SetRejectMessage("\nSelect a 2D, 3D or lightweight polyline.");
            PromptEntityResult referenceResult = document.Editor.GetEntity(entityOptions);
            if (referenceResult.Status != PromptStatus.OK) return;

            double referenceLength;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                Curve curve = transaction.GetObject(referenceResult.ObjectId, OpenMode.ForRead, false) as Curve;
                if (!IsPolyline(curve) || !TryCurveLength(curve, out referenceLength))
                {
                    document.Editor.WriteMessage("\nCE_SELECTPOLYSAMELENGTH cancelled. The selected object is not a supported polyline.");
                    return;
                }
            }

            var toleranceOptions = new PromptDoubleOptions("\nLength tolerance <0.001>: ")
            {
                DefaultValue = 0.001,
                UseDefaultValue = true,
                AllowNegative = false,
                AllowZero = true,
                AllowNone = true
            };
            PromptDoubleResult toleranceResult = document.Editor.GetDouble(toleranceOptions);
            if (toleranceResult.Status == PromptStatus.Cancel) return;
            double matchTolerance = toleranceResult.Status == PromptStatus.OK ? toleranceResult.Value : 0.001;

            List<ObjectId> matches = FindCurrentSpacePolylines(document, delegate(Curve curve)
            {
                double length;
                return TryCurveLength(curve, out length) && Math.Abs(length - referenceLength) <= Math.Max(matchTolerance, Tolerance);
            });
            document.Editor.SetImpliedSelection(matches.ToArray());
            document.Editor.WriteMessage("\nCE_SELECTPOLYSAMELENGTH: reference={0:N3}; tolerance={1:N4}; selected={2}.", referenceLength, matchTolerance, matches.Count);
        }

        [CommandMethod("CE_TOOLS", "CE_SETTINGOUTSWAPXY", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void SwapSettingOutXyValues()
        {
            Document document = Active();
            if (document == null) return;
            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                selection = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect setting-out table(s) whose X and Y numeric values must be exchanged: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            }
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            int tablesChanged = 0;
            int rowsChanged = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    Table table;
                    try { table = transaction.GetObject(id, OpenMode.ForWrite, false) as Table; }
                    catch { continue; }
                    if (table == null) continue;
                    int headerRow;
                    int xColumn;
                    int yColumn;
                    if (!FindCoordinateColumns(table, out headerRow, out xColumn, out yColumn)) continue;
                    int changedThisTable = 0;
                    for (int row = headerRow + 1; row < table.Rows.Count; row++)
                    {
                        string x = SafeCellText(table, row, xColumn);
                        string y = SafeCellText(table, row, yColumn);
                        if (string.IsNullOrWhiteSpace(x) && string.IsNullOrWhiteSpace(y)) continue;
                        try
                        {
                            table.Cells[row, xColumn].TextString = y;
                            table.Cells[row, yColumn].TextString = x;
                            changedThisTable++;
                        }
                        catch { }
                    }
                    if (changedThisTable > 0)
                    {
                        tablesChanged++;
                        rowsChanged += changedThisTable;
                    }
                }
                transaction.Commit();
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_SETTINGOUTSWAPXY complete. Tables={0}; data rows swapped={1}. Drawing/source geometry was not transformed.", tablesChanged, rowsChanged);
        }

        [CommandMethod("CE_TOOLS", "CE_JUNCTIONSETTINGOUT4FIX", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void JunctionSettingOutFourReturns()
        {
            Document document = Active();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Ordered Junction Setting-Out (4 Returns)",
                "Finish one full junction before moving to the next. Spatial grouping finds the junction; the return count prevents a nearby junction from being chained into the same group.");
            model.AddChoice("Scope", "01 Junctions", "Junction geometry", "All", "Use all CE junction return curves or only selected curves.", new[] { "All", "Selected" });
            model.AddPositiveDouble("Grouping", "02 Order", "Junction grouping distance", 30.0, "Maximum spatial distance used when finding return curves belonging to the same junction. This is a tolerance, not the number of bellmouths.");
            model.AddPositiveInteger("Returns", "02 Order", "Bellmouth/return curves per junction", 4, "Maximum number of return curves completed before CE Tools advances to the next junction. For the requested T/cross workflow keep this at 4.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            List<ObjectId> ids = ResolveJunctionCurves(document, model.Text("Scope"));
            if (ids.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_JUNCTIONSETTINGOUT4FIX: no CE-ROAD-JUNCTION curves found.");
                return;
            }

            int perJunction = Math.Max(1, model.Integer("Returns", 4));
            double groupingDistance = Math.Max(0.1, model.Double("Grouping", 30.0));
            List<CurveCentre> centres = ReadCurveCentres(document.Database, ids);
            List<List<CurveCentre>> groups = GroupNearestReturns(centres, groupingDistance, perJunction);
            ObjectId[] ordered = groups
                .OrderByDescending(group => group.Average(item => item.Point.Y))
                .ThenBy(group => group.Average(item => item.Point.X))
                .SelectMany(OrderAroundCentre)
                .Select(item => item.Id)
                .ToArray();
            document.Editor.SetImpliedSelection(ordered);
            document.Editor.WriteMessage("\nCE_JUNCTIONSETTINGOUT4FIX: junction groups={0}; ordered return curves={1}; maximum returns per junction={2}. The 30.0 default is only the spatial grouping tolerance.", groups.Count, ordered.Length, perJunction);
            document.SendStringToExecute("CE_ROADJUNCTIONSETTINGOUT ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADPROFILEVIEWSPLIT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SplitRoadProfileViewsIntoSections()
        {
            Document document = Active();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            var pickOptions = new PromptEntityOptions("\nSelect an existing road profile view to use as the section template: ");
            pickOptions.SetRejectMessage("\nSelect a Civil 3D profile view.");
            pickOptions.AddAllowedClass(typeof(CivilProfileView), false);
            PromptEntityResult pick = document.Editor.GetEntity(pickOptions);
            if (pick.Status != PromptStatus.OK) return;

            double alignmentStart;
            double alignmentEnd;
            ObjectId alignmentId;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilProfileView template = transaction.GetObject(pick.ObjectId, OpenMode.ForRead, false) as CivilProfileView;
                if (template == null) return;
                alignmentId = ReadObjectId(template, "AlignmentId");
                CivilAlignment alignment = alignmentId.IsNull ? null : transaction.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment;
                if (alignment == null)
                {
                    document.Editor.WriteMessage("\nCE_ROADPROFILEVIEWSPLIT cancelled. Parent alignment could not be resolved.");
                    return;
                }
                alignmentStart = alignment.StartingStation;
                alignmentEnd = alignment.EndingStation;
            }

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Split Road Profile Views",
                "Create consecutive profile-view windows from the selected template. Example with 750 m sections: 0.000-750.000, 750.000-1500.000, then continue until alignment end.");
            settings.AddDouble("Start", "01 Station Sections", "Start station", alignmentStart, "First station to show.");
            settings.AddDouble("End", "01 Station Sections", "End station", alignmentEnd, "Last station to show.");
            settings.AddPositiveDouble("Length", "01 Station Sections", "Section length", 750.0, "Profile-view station length per section.");
            settings.AddPositiveDouble("Spacing", "02 Placement", "Horizontal spacing between views", 250.0, "Drawing-unit horizontal spacing between successive profile views.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            double start = Math.Max(alignmentStart, settings.Double("Start", alignmentStart));
            double end = Math.Min(alignmentEnd, settings.Double("End", alignmentEnd));
            double sectionLength = Math.Max(0.001, settings.Double("Length", 750.0));
            double spacing = Math.Max(0.001, settings.Double("Spacing", 250.0));
            if (end <= start + Tolerance)
            {
                document.Editor.WriteMessage("\nCE_ROADPROFILEVIEWSPLIT cancelled. End station must be greater than start station.");
                return;
            }
            PromptPointResult insertion = document.Editor.GetPoint("\nPick insertion point for the first section profile view: ");
            if (insertion.Status != PromptStatus.OK) return;

            int created = 0;
            int unsupported = 0;
            var labels = new List<string>();
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilProfileView template = transaction.GetObject(pick.ObjectId, OpenMode.ForRead, false) as CivilProfileView;
                    if (template == null) return;
                    ObjectId templateStyle = ReadObjectId(template, "StyleId");
                    ObjectId templateBandSet = FirstObjectId(template, "BandSetStyleId", "ProfileViewBandSetStyleId");
                    string alignmentName = ReadString(transaction.GetObject(alignmentId, OpenMode.ForRead, false), "Name", "ROAD");
                    int index = 0;
                    for (double sectionStart = start; sectionStart < end - Tolerance; sectionStart += sectionLength)
                    {
                        double sectionEnd = Math.Min(sectionStart + sectionLength, end);
                        Point3d location = new Point3d(insertion.Value.X + index * spacing, insertion.Value.Y, insertion.Value.Z);
                        ObjectId newId;
                        if (!TryCreateProfileView(alignmentId, location, out newId))
                        {
                            unsupported++;
                            break;
                        }
                        CivilProfileView view = transaction.GetObject(newId, OpenMode.ForWrite, false) as CivilProfileView;
                        if (view == null)
                        {
                            unsupported++;
                            index++;
                            continue;
                        }
                        TrySetObjectId(view, "StyleId", templateStyle);
                        if (!templateBandSet.IsNull)
                        {
                            TrySetObjectId(view, "BandSetStyleId", templateBandSet);
                            TrySetObjectId(view, "ProfileViewBandSetStyleId", templateBandSet);
                        }
                        bool rangeSet = TrySetSpecifiedRange(view, sectionStart, sectionEnd);
                        if (!rangeSet)
                        {
                            view.Erase();
                            unsupported++;
                            index++;
                            continue;
                        }
                        string newName = string.Format(CultureInfo.InvariantCulture, "{0}-STA-{1:0.000}-{2:0.000}", alignmentName, sectionStart, sectionEnd);
                        TrySetString(view, "Name", newName);
                        TryInvoke(view, "Rebuild");
                        TryInvoke(view, "Update");
                        labels.Add(string.Format(CultureInfo.CurrentCulture, "{0:N3} to {1:N3}", sectionStart, sectionEnd));
                        created++;
                        index++;
                    }
                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_ROADPROFILEVIEWSPLIT failed. {0}", exception.Message);
                return;
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_ROADPROFILEVIEWSPLIT complete. Section profile views created={0}; unsupported/skipped={1}. Sections: {2}", created, unsupported, string.Join("; ", labels));
        }

        [CommandMethod("CE_TOOLS", "CE_CORSPLIT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SplitCorridorRegions()
        {
            Document document = Active();
            if (document == null) return;
            var pickOptions = new PromptEntityOptions("\nSelect Civil 3D corridor to split: ");
            pickOptions.SetRejectMessage("\nSelect a Civil 3D corridor.");
            pickOptions.AddAllowedClass(typeof(Corridor), false);
            PromptEntityResult pick = document.Editor.GetEntity(pickOptions);
            if (pick.Status != PromptStatus.OK) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Split Corridor Regions",
                "Enter one or more raw alignment stations separated by commas, for example 750,1500,2250. A baseline region is split only when the station lies inside it.");
            settings.AddText("Stations", "01 Split", "Split stations", "750,1500", "Comma/semicolon separated raw station values.");
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            List<double> stations = ParseStations(settings.Text("Stations"));
            if (stations.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_CORSPLIT cancelled. No valid stations were entered.");
                return;
            }

            int splits = 0;
            var skipped = new List<double>();
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Corridor corridor = transaction.GetObject(pick.ObjectId, OpenMode.ForWrite, false) as Corridor;
                    if (corridor == null) return;
                    foreach (double station in stations.OrderBy(value => value))
                    {
                        bool done = false;
                        foreach (Baseline baseline in corridor.Baselines)
                        {
                            foreach (BaselineRegion region in baseline.BaselineRegions)
                            {
                                if (station <= region.StartStation + 0.001 || station >= region.EndStation - 0.001) continue;
                                region.Split(station);
                                splits++;
                                done = true;
                                break;
                            }
                            if (done) break;
                        }
                        if (!done) skipped.Add(station);
                    }
                    if (splits > 0) corridor.Rebuild();
                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_CORSPLIT failed. {0}", exception.Message);
                return;
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_CORSPLIT complete. Region splits={0}; stations not inside a region={1}.", splits, skipped.Count == 0 ? "none" : string.Join(", ", skipped.Select(value => value.ToString("N3", CultureInfo.CurrentCulture))));
        }

        [CommandMethod("CE_TOOLS", "CE_JUNCTIONSTEPPEDOFFSETWORKFLOW", CommandFlags.Modal)]
        public void JunctionSteppedOffsetWorkflow()
        {
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Junction Stepped Offset Fallback",
                "Use this when a corridor junction cannot be completed reliably. Build linked feature-line strings from the bellmouth outward; keep junction controls on a dedicated site/surface so they do not corrupt the main road surface.",
                new List<DisciplineWorkflowAction>
                {
                    Action("1. Bellmouths -> Feature Lines", "CE_FLCREATE", "Create feature lines from the selected bellmouth/junction control strings. Use a dedicated junction site where available.", "01 Controls"),
                    Action("2. Gutter Edge Stepped Offsets", "CE_FLOFFSET", "Create linked stepped offsets for the gutter edge from the bellmouth controls.", "02 Kerb / Gutter"),
                    Action("3. Bottom of Kerb Stepped Offsets", "CE_FLOFFSET", "Create the bottom-of-kerb control strings.", "02 Kerb / Gutter"),
                    Action("4. Top of Kerb Stepped Offsets", "CE_FLOFFSET", "Create the top-of-kerb control strings with the required level difference/slope.", "02 Kerb / Gutter"),
                    Action("5. Sidewalk / Shoulder Edge", "CE_FLOFFSET", "Create the outer sidewalk or shoulder edge control strings.", "03 Outside"),
                    Action("6. Daylight / Selected Surface", "CE_PLATFORMDRAPE", "Use the selected target surface to establish outer/daylight levels where the linked drape workflow is applicable.", "03 Outside"),
                    Action("7. Join / Close Stepped Strings", "CE_FLSTEPJOIN", "Join the stepped feature-line pieces, close gaps and add endpoint vertices before surface infill.", "04 Close / Infill"),
                    Action("8. Junction Surface / Infill Tools", "CE_SURFTOOLS", "Create/review the dedicated junction surface and add the closed junction controls/infill without mixing the construction site with unrelated feature lines.", "04 Close / Infill"),
                    Action("9. Refresh Linked Model Data", "CE_REFRESHALL", "Refresh dependent model data after the junction fallback is complete.", "05 Refresh")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_ROADPRODUCTIONINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RoadProductionInformation()
        {
            Document document = Active();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            int alignments = 0;
            int profiles = 0;
            int profileViews = 0;
            int corridors = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
                {
                    CivilAlignment alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null || !IsRoadAlignment(alignment)) continue;
                    alignments++;
                    profiles += alignment.GetProfileIds().Count;
                    profileViews += ReadObjectIds(alignment, "GetProfileViewIds").Count;
                }
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (model != null)
                {
                    foreach (ObjectId id in model)
                    {
                        Corridor corridor;
                        try { corridor = transaction.GetObject(id, OpenMode.ForRead, false) as Corridor; }
                        catch { continue; }
                        if (corridor != null) corridors++;
                    }
                }
            }
            document.Editor.WriteMessage("\nCE_ROADPRODUCTIONINFO: road alignments={0}; profiles={1}; profile views={2}; corridors={3}.", alignments, profiles, profileViews, corridors);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADPROFILEREPORT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RoadProfileReport()
        {
            Document document = Active();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            int count = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
                {
                    CivilAlignment alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null || !IsRoadAlignment(alignment)) continue;
                    foreach (ObjectId viewId in ReadObjectIds(alignment, "GetProfileViewIds"))
                    {
                        DBObject view = transaction.GetObject(viewId, OpenMode.ForRead, false);
                        if (view == null) continue;
                        double start = ReadDouble(view, "StationStart", alignment.StartingStation);
                        double end = ReadDouble(view, "StationEnd", alignment.EndingStation);
                        document.Editor.WriteMessage("\n  {0} | Alignment={1} | {2:N3} to {3:N3} | Style={4}", ReadString(view, "Name", view.Handle.ToString()), alignment.Name, start, end, ReadString(view, "StyleName", "<Current>"));
                        count++;
                    }
                }
            }
            document.Editor.WriteMessage("\nCE_ROADPROFILEREPORT complete. Road profile views={0}.", count);
        }

        private static DisciplineWorkflowAction Action(string title, string command, string description, string group)
        {
            return new DisciplineWorkflowAction(title, command, description, group);
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private static List<ObjectId> FindCurrentSpacePolylines(Document document, Func<Curve, bool> predicate)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) return result;
                foreach (ObjectId id in space)
                {
                    Curve curve;
                    try { curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve; }
                    catch { continue; }
                    if (!IsPolyline(curve)) continue;
                    try { if (predicate(curve)) result.Add(id); }
                    catch { }
                }
            }
            return result;
        }

        private static bool IsPolyline(Curve curve)
        {
            return curve is Polyline || curve is Polyline2d || curve is Polyline3d;
        }

        private static bool TryCurveLength(Curve curve, out double length)
        {
            length = 0.0;
            if (curve == null) return false;
            try
            {
                double start = curve.GetDistanceAtParameter(curve.StartParam);
                double end = curve.GetDistanceAtParameter(curve.EndParam);
                length = Math.Abs(end - start);
                return !double.IsNaN(length) && !double.IsInfinity(length);
            }
            catch { return false; }
        }

        private static bool FindCoordinateColumns(Table table, out int headerRow, out int xColumn, out int yColumn)
        {
            headerRow = -1;
            xColumn = -1;
            yColumn = -1;
            if (table == null) return false;
            int rows = Math.Min(table.Rows.Count, 8);
            for (int row = 0; row < rows; row++)
            {
                int x = -1;
                int y = -1;
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    string text = NormalizeHeader(SafeCellText(table, row, column));
                    if (text == "X" || text == "EASTING" || text == "E") x = column;
                    if (text == "Y" || text == "NORTHING" || text == "N") y = column;
                }
                if (x >= 0 && y >= 0 && x != y)
                {
                    headerRow = row;
                    xColumn = x;
                    yColumn = y;
                    return true;
                }
            }
            return false;
        }

        private static string NormalizeHeader(string text)
        {
            return (text ?? string.Empty).Replace("\\P", " ").Trim().ToUpperInvariant();
        }

        private static string SafeCellText(Table table, int row, int column)
        {
            try { return table.Cells[row, column].TextString ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static List<ObjectId> ResolveJunctionCurves(Document document, string scope)
        {
            IEnumerable<ObjectId> ids;
            if (string.Equals(scope, "Selected", StringComparison.OrdinalIgnoreCase))
            {
                PromptSelectionResult selected = document.Editor.SelectImplied();
                if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
                    selected = document.Editor.GetSelection(new PromptSelectionOptions { MessageForAdding = "\nSelect junction return curves: ", AllowDuplicates = false, RejectObjectsFromNonCurrentSpace = true });
                if (selected.Status != PromptStatus.OK || selected.Value == null) return new List<ObjectId>();
                ids = selected.Value.GetObjectIds();
            }
            else
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                    ids = space == null ? new ObjectId[0] : space.Cast<ObjectId>().ToArray();
                }
            }
            var result = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Curve curve;
                    try { curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve; }
                    catch { continue; }
                    if (curve != null && string.Equals(curve.Layer, JunctionLayer, StringComparison.OrdinalIgnoreCase)) result.Add(id);
                }
            }
            return result;
        }

        private static List<CurveCentre> ReadCurveCentres(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<CurveCentre>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Curve curve;
                    try { curve = transaction.GetObject(id, OpenMode.ForRead, false) as Curve; }
                    catch { continue; }
                    if (curve == null) continue;
                    try
                    {
                        Extents3d extents = curve.GeometricExtents;
                        result.Add(new CurveCentre(id, new Point2d((extents.MinPoint.X + extents.MaxPoint.X) * 0.5, (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5)));
                    }
                    catch
                    {
                        try
                        {
                            Point3d point = curve.GetPointAtParameter((curve.StartParam + curve.EndParam) * 0.5);
                            result.Add(new CurveCentre(id, new Point2d(point.X, point.Y)));
                        }
                        catch { }
                    }
                }
            }
            return result;
        }

        private static List<List<CurveCentre>> GroupNearestReturns(List<CurveCentre> input, double maximumDistance, int maximumCount)
        {
            var remaining = new List<CurveCentre>(input.OrderByDescending(item => item.Point.Y).ThenBy(item => item.Point.X));
            var groups = new List<List<CurveCentre>>();
            while (remaining.Count > 0)
            {
                CurveCentre seed = remaining[0];
                remaining.RemoveAt(0);
                var group = new List<CurveCentre> { seed };
                while (group.Count < maximumCount && remaining.Count > 0)
                {
                    Point2d centre = new Point2d(group.Average(item => item.Point.X), group.Average(item => item.Point.Y));
                    CurveCentre nearest = remaining.OrderBy(item => item.Point.GetDistanceTo(centre)).First();
                    if (nearest.Point.GetDistanceTo(centre) > maximumDistance) break;
                    group.Add(nearest);
                    remaining.Remove(nearest);
                }
                groups.Add(group);
            }
            return groups;
        }

        private static IEnumerable<CurveCentre> OrderAroundCentre(List<CurveCentre> group)
        {
            Point2d centre = new Point2d(group.Average(item => item.Point.X), group.Average(item => item.Point.Y));
            return group.OrderBy(item => NormalizeAngle(Math.Atan2(item.Point.Y - centre.Y, item.Point.X - centre.X)));
        }

        private static double NormalizeAngle(double value)
        {
            double result = value;
            while (result < 0.0) result += Math.PI * 2.0;
            while (result >= Math.PI * 2.0) result -= Math.PI * 2.0;
            return result;
        }

        private static List<double> ParseStations(string text)
        {
            var values = new List<double>();
            foreach (string token in (text ?? string.Empty).Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                double value;
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.TryParse(token, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                    values.Add(value);
            }
            return values.Distinct().ToList();
        }

        private static bool TryCreateProfileView(ObjectId alignmentId, Point3d location, out ObjectId result)
        {
            result = ObjectId.Null;
            foreach (MethodInfo method in typeof(CivilProfileView).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(item => string.Equals(item.Name, "Create", StringComparison.OrdinalIgnoreCase)))
            {
                ParameterInfo[] parameters = method.GetParameters();
                try
                {
                    object value = null;
                    if (parameters.Length == 2 && parameters[0].ParameterType == typeof(ObjectId) && parameters[1].ParameterType == typeof(Point3d))
                        value = method.Invoke(null, new object[] { alignmentId, location });
                    if (value is ObjectId && !((ObjectId)value).IsNull)
                    {
                        result = (ObjectId)value;
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static bool TrySetSpecifiedRange(object view, double start, double end)
        {
            bool mode = TrySetSpecifiedEnum(view, "StationRangeMode");
            bool startSet = TrySetDouble(view, "StationStart", start) || TrySetDouble(view, "StartingStation", start);
            bool endSet = TrySetDouble(view, "StationEnd", end) || TrySetDouble(view, "EndingStation", end);
            return startSet && endSet && (mode || ReadDouble(view, "StationStart", double.NaN).Equals(start));
        }

        private static bool TrySetSpecifiedEnum(object target, string propertyName)
        {
            PropertyInfo property = target == null ? null : target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite || !property.PropertyType.IsEnum) return false;
            string name = Enum.GetNames(property.PropertyType).FirstOrDefault(item => item.IndexOf("User", StringComparison.OrdinalIgnoreCase) >= 0 || item.IndexOf("Specified", StringComparison.OrdinalIgnoreCase) >= 0 || item.IndexOf("Manual", StringComparison.OrdinalIgnoreCase) >= 0);
            if (string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                property.SetValue(target, Enum.Parse(property.PropertyType, name), null);
                return true;
            }
            catch { return false; }
        }

        private static bool TrySetDouble(object target, string propertyName, double value)
        {
            PropertyInfo property = target == null ? null : target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite || property.PropertyType != typeof(double)) return false;
            try { property.SetValue(target, value, null); return true; }
            catch { return false; }
        }

        private static bool TrySetObjectId(object target, string propertyName, ObjectId value)
        {
            if (value.IsNull || target == null) return false;
            PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite || property.PropertyType != typeof(ObjectId)) return false;
            try { property.SetValue(target, value, null); return true; }
            catch { return false; }
        }

        private static bool TrySetString(object target, string propertyName, string value)
        {
            if (target == null) return false;
            PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite || property.PropertyType != typeof(string)) return false;
            try { property.SetValue(target, value, null); return true; }
            catch { return false; }
        }

        private static bool TryInvoke(object target, string methodName)
        {
            if (target == null) return false;
            try
            {
                MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method == null) return false;
                method.Invoke(target, null);
                return true;
            }
            catch { return false; }
        }

        private static ObjectId ReadObjectId(object target, string propertyName)
        {
            if (target == null) return ObjectId.Null;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                object value = property == null ? null : property.GetValue(target, null);
                return value is ObjectId ? (ObjectId)value : ObjectId.Null;
            }
            catch { return ObjectId.Null; }
        }

        private static ObjectId FirstObjectId(object target, params string[] propertyNames)
        {
            foreach (string name in propertyNames)
            {
                ObjectId id = ReadObjectId(target, name);
                if (!id.IsNull) return id;
            }
            return ObjectId.Null;
        }

        private static string ReadString(object target, string propertyName, string fallback)
        {
            if (target == null) return fallback;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                object value = property == null ? null : property.GetValue(target, null);
                string text = Convert.ToString(value, CultureInfo.CurrentCulture);
                return string.IsNullOrWhiteSpace(text) ? fallback : text;
            }
            catch { return fallback; }
        }

        private static double ReadDouble(object target, string propertyName, double fallback)
        {
            if (target == null) return fallback;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                object value = property == null ? null : property.GetValue(target, null);
                return value == null ? fallback : Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch { return fallback; }
        }

        private static List<ObjectId> ReadObjectIds(object target, string methodName)
        {
            var result = new List<ObjectId>();
            if (target == null) return result;
            try
            {
                MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                object value = method == null ? null : method.Invoke(target, null);
                System.Collections.IEnumerable values = value as System.Collections.IEnumerable;
                if (values == null) return result;
                foreach (object item in values)
                    if (item is ObjectId) result.Add((ObjectId)item);
            }
            catch { }
            return result;
        }

        private static bool IsRoadAlignment(CivilAlignment alignment)
        {
            if (alignment == null) return false;
            string identity = ((alignment.Name ?? string.Empty) + " " + (alignment.Description ?? string.Empty)).ToUpperInvariant();
            return identity.StartsWith("RD") || identity.StartsWith("ROAD") || identity.Contains("CE ROAD");
        }

        private sealed class CurveCentre
        {
            internal CurveCentre(ObjectId id, Point2d point)
            {
                Id = id;
                Point = point;
            }
            internal ObjectId Id { get; private set; }
            internal Point2d Point { get; private set; }
        }
    }
}
