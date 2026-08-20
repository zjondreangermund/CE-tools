using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.August20GeometryFirstSewerCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Geometry-first planning workflows.  Midblock sewer, Road Reserve sewer and
    /// road-reserve centrelines are calculated as ordinary AutoCAD polylines first.
    /// Planning manholes are ordinary circles.  Civil 3D network/alignment creation
    /// is deliberately deferred to separate commands after the layout is visible and
    /// can be grip-edited safely.
    /// </summary>
    public sealed class August20GeometryFirstSewerCommands
    {
        private const double Tol = 1e-7;
        private const double KeyTolerance = 0.01;
        private const string MidblockRouteLayer = "CE-SEWER-MIDBLOCK-LAYOUT";
        private const string MidblockMhLayer = "CE-SEWER-MIDBLOCK-LAYOUT-MH";
        private const string MidblockLabelLayer = "CE-SEWER-MIDBLOCK-LAYOUT-LABEL";
        private const string RoadReserveRouteLayer = "CE-SEWER-ROADRESERVE-LAYOUT";
        private const string RoadReserveMhLayer = "CE-SEWER-ROADRESERVE-LAYOUT-MH";
        private const string RoadReserveLabelLayer = "CE-SEWER-ROADRESERVE-LAYOUT-LABEL";
        private const string RoadCenterLayer = "CE-ROAD-CENTERLINE-LAYOUT";

        [CommandMethod("CE_TOOLS", "CE_SEWERLAYOUTMIDBLOCK", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void MidblockLayout()
        {
            RunMidblock(false);
        }

        // This bridge token is replaced by the final staged repair with the legacy
        // public command name after the old implementation is moved out of the way.
        [CommandMethod("CE_TOOLS", "CE_AUG20MIDBLOCKBRIDGE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void MidblockLegacyBridge()
        {
            RunMidblock(true);
        }

        [CommandMethod("CE_TOOLS", "CE_SEWERLAYOUTROADRESERVE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RoadReserveLayout()
        {
            RunRoadReserve(false);
        }

        [CommandMethod("CE_TOOLS", "CE_AUG20ROADRESERVEBRIDGE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RoadReserveLegacyBridge()
        {
            RunRoadReserve(true);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADCENTERLINEPOLY", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RoadCenterlinePolyline()
        {
            RunRoadCenterline(false);
        }

        [CommandMethod("CE_TOOLS", "CE_AUG20ROADCENTERBRIDGE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RoadCenterlineLegacyBridge()
        {
            RunRoadCenterline(true);
        }

        [CommandMethod("CE_TOOLS", "CE_CENTERLINETOALIGNMENT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CenterlineToAlignment()
        {
            Document document = Active();
            if (document == null) return;
            PromptSelectionResult selected = SelectPolylines(document.Editor,
                "\nSelect approved CE road-centreline polylines to convert to Civil 3D Alignment(s): ");
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0) return;
            ObjectId[] ids = FilterByLayerPrefix(document, selected.Value.GetObjectIds(), "CE-ROAD-CENTERLINE");
            if (ids.Length == 0)
            {
                document.Editor.WriteMessage("\nCE_CENTERLINETOALIGNMENT: no CE road-centreline layout polylines were selected.");
                return;
            }
            document.Editor.SetImpliedSelection(ids);
            document.Editor.WriteMessage("\nCE_CENTERLINETOALIGNMENT: launching Civil 3D Create Alignment From Objects for the approved polyline geometry.");
            document.SendStringToExecute("CreateAlignmentEntities ", true, false, false);
        }

        [CommandMethod("CE_TOOLS", "CE_SEWERBUILDNETWORK", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void BuildSewerNetwork()
        {
            Document document = Active();
            if (document == null) return;
            PromptSelectionResult selected = SelectPolylines(document.Editor,
                "\nSelect approved CE sewer-layout polylines to convert to a Civil 3D Pipe Network: ");
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0) return;
            ObjectId[] ids = FilterByLayerPrefix(document, selected.Value.GetObjectIds(), "CE-SEWER-");
            if (ids.Length == 0)
            {
                document.Editor.WriteMessage("\nCE_SEWERBUILDNETWORK: no CE sewer-layout polylines were selected.");
                return;
            }
            document.Editor.SetImpliedSelection(ids);
            document.Editor.WriteMessage("\nCE_SEWERBUILDNETWORK: approved planning polylines are selected. Opening the CE multi-source Pipe Network workflow.");
            document.SendStringToExecute("CE_SEWERNETWORKMULTI ", true, false, false);
        }

        [CommandMethod("CE_TOOLS", "CE_SEWERREFRESHLAYOUT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RefreshSewerLayout()
        {
            Document document = Active();
            if (document == null) return;
            PromptSelectionResult selected = SelectPolylines(document.Editor,
                "\nSelect CE Midblock/Road-Reserve sewer layout polylines to rebuild planning manholes: ");
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Refresh Sewer Layout",
                "Rebuild planning manhole vertices/circles on the selected geometry-first sewer routes. This does not touch Civil 3D Pipe Networks.");
            model.AddChoice("Spacing", "01 Manholes", "Maximum manhole spacing", "60 m",
                "Maximum distance between planning manholes.", new[] { "60 m", "80 m", "Custom" });
            model.AddPositiveDouble("CustomSpacing", "01 Manholes", "Custom spacing", 60.0,
                "Used only when Custom is selected.");
            model.AddPositiveDouble("Diameter", "01 Manholes", "Planning manhole diameter", 1.2,
                "Diameter of the visible planning circles.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            double spacing = ReadSpacing(model);
            double diameter = Math.Max(0.1, model.Double("Diameter", 1.2));
            int routes = 0;
            int manholes = 0;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = OpenModelSpace(document.Database, transaction, OpenMode.ForWrite);
                    if (space == null) return;
                    EraseLayoutManholes(space, transaction, MidblockMhLayer, MidblockLabelLayer, RoadReserveMhLayer, RoadReserveLabelLayer);
                    foreach (ObjectId id in selected.Value.GetObjectIds().Where(value => !value.IsNull && !value.IsErased).Distinct())
                    {
                        Polyline route;
                        try { route = transaction.GetObject(id, OpenMode.ForWrite, false) as Polyline; }
                        catch { continue; }
                        if (route == null || route.IsErased || route.Length <= Tol) continue;
                        bool midblock = string.Equals(route.Layer, MidblockRouteLayer, StringComparison.OrdinalIgnoreCase);
                        bool road = string.Equals(route.Layer, RoadReserveRouteLayer, StringComparison.OrdinalIgnoreCase);
                        if (!midblock && !road) continue;
                        InsertSpacingVertices(route, spacing);
                        ObjectId mhLayer = EnsureLayer(document.Database, transaction, midblock ? MidblockMhLayer : RoadReserveMhLayer);
                        ObjectId labelLayer = EnsureLayer(document.Database, transaction, midblock ? MidblockLabelLayer : RoadReserveLabelLayer);
                        string prefix = midblock ? "MB-MH" : "RR-MH";
                        manholes += AddManholesAtVertices(document.Database, transaction, space, route, mhLayer, labelLayer, diameter, prefix, routes + 1);
                        routes++;
                    }
                    transaction.Commit();
                }
            }
            catch (System.Exception ex)
            {
                document.Editor.WriteMessage("\nCE_SEWERREFRESHLAYOUT stopped safely: {0}", ex.Message);
                return;
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE_SEWERREFRESHLAYOUT complete. Routes={0}; planning manholes={1}.", routes, manholes);
        }

        private static void RunMidblock(bool legacyEntry)
        {
            Document document = Active();
            if (document == null) return;
            List<string> surfaceChoices = SurfaceChoices(document);
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Geometry-First Midblock Sewer",
                "Create editable AutoCAD sewer polylines beside shared cadastral/midblock boundaries. Manholes are circles and route vertices. Civil 3D pipes/structures are created only later with CE_SEWERBUILDNETWORK.");
            model.AddChoice("Surface", "01 Analysis", "Surface for flow direction", surfaceChoices[0],
                "Optional Civil 3D surface used only to orient each completed planning route from higher to lower endpoint. Geometry creation remains ordinary AutoCAD objects.", surfaceChoices);
            model.AddChoice("Direction", "02 Midblock", "Shared-boundary direction", "Automatic dominant direction",
                "Automatic selects the dominant horizontal/vertical shared-boundary direction. Force Horizontal or Vertical when required.",
                new[] { "Automatic dominant direction", "Horizontal", "Vertical" });
            model.AddChoice("Side", "02 Midblock", "Offset side", "Automatic lower side",
                "Automatic samples both offset sides when a surface is selected; otherwise the first stable side is used.",
                new[] { "Automatic lower side", "Left / Top", "Right / Bottom", "On shared boundary" });
            model.AddPositiveDouble("Offset", "02 Midblock", "Offset from shared erf boundary", 1.5,
                "Planning sewer offset from the shared cadastral/midblock line.");
            model.AddChoice("Spacing", "03 Manholes", "Maximum manhole spacing", "60 m",
                "Route vertices and planning circles are inserted at this maximum interval.",
                new[] { "60 m", "80 m", "Custom" });
            model.AddPositiveDouble("CustomSpacing", "03 Manholes", "Custom spacing", 60.0, "Used only for Custom spacing.");
            model.AddPositiveDouble("Diameter", "03 Manholes", "Planning manhole diameter", 1.2, "Visible planning-circle diameter.");
            model.AddPositiveDouble("MinEdge", "04 Safety", "Minimum usable shared edge length", 2.0,
                "Ignore very short shared cadastral edges.");
            model.AddChoice("Replace", "05 Output", "Existing Midblock layout", "Replace existing",
                "Replace previous geometry-first Midblock layout or keep it.", new[] { "Replace existing", "Keep existing" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            ObjectId surfaceId = August20SurfaceChoice.ResolveSurfaceId(document, model.Text("Surface"));
            PromptSelectionResult selection = SelectClosedPolylines(document.Editor,
                "\nSelect closed cadastral erf polylines for geometry-first Midblock sewer layout: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0) return;

            double offset = Math.Max(0.0, model.Double("Offset", 1.5));
            double spacing = ReadSpacing(model);
            double diameter = Math.Max(0.1, model.Double("Diameter", 1.2));
            double minEdge = Math.Max(0.1, model.Double("MinEdge", 2.0));
            int routeCount = 0;
            int manholeCount = 0;
            int sharedCount = 0;

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    List<ParcelLite> parcels = ReadParcels(selection.Value.GetObjectIds(), transaction);
                    if (parcels.Count == 0)
                    {
                        document.Editor.WriteMessage("\nCE Midblock: no usable closed cadastral polylines were found.");
                        return;
                    }
                    List<EdgeLite> allEdges = BuildEdges(parcels, minEdge);
                    List<EdgeLite> shared = SharedEdges(allEdges);
                    if (shared.Count == 0)
                    {
                        document.Editor.WriteMessage("\nCE Midblock: no exact shared cadastral edges were found. Check that adjoining erf boundaries use common vertices.");
                        return;
                    }
                    bool horizontal = ResolveHorizontal(shared, model.Text("Direction"));
                    shared = shared.Where(edge => horizontal ? edge.Horizontal : edge.Vertical).ToList();
                    sharedCount = shared.Count;
                    List<Line2> merged = MergeAxisAligned(shared, horizontal);
                    if (merged.Count == 0)
                    {
                        document.Editor.WriteMessage("\nCE Midblock: shared edges did not form usable continuous midblock lines.");
                        return;
                    }

                    CivilSurface surface = OpenSurface(transaction, surfaceId);
                    BlockTableRecord space = OpenModelSpace(document.Database, transaction, OpenMode.ForWrite);
                    if (space == null) return;
                    if (string.Equals(model.Text("Replace"), "Replace existing", StringComparison.OrdinalIgnoreCase))
                        SafeEraseByLayers(space, transaction, MidblockRouteLayer, MidblockMhLayer, MidblockLabelLayer);
                    ObjectId routeLayer = EnsureLayer(document.Database, transaction, MidblockRouteLayer);
                    ObjectId mhLayer = EnsureLayer(document.Database, transaction, MidblockMhLayer);
                    ObjectId labelLayer = EnsureLayer(document.Database, transaction, MidblockLabelLayer);

                    int number = 1;
                    foreach (Line2 baseLine in merged)
                    {
                        Line2 routeLine = OffsetMidblock(baseLine, horizontal, offset, model.Text("Side"), surface);
                        if (routeLine.Length <= Tol) continue;
                        OrientDownhill(ref routeLine, surface);
                        Polyline route = CreateSpacedPolyline(document.Database, routeLine, spacing, routeLayer);
                        space.AppendEntity(route);
                        transaction.AddNewlyCreatedDBObject(route, true);
                        WriteLayoutRecord(route, transaction, "MIDBLOCK", spacing, diameter, model.Text("Surface"));
                        manholeCount += AddManholesAtVertices(document.Database, transaction, space, route, mhLayer, labelLayer, diameter, "MB-MH", number);
                        routeCount++;
                        number++;
                    }
                    transaction.Commit();
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                document.Editor.WriteMessage("\nCE geometry-first Midblock stopped safely: {0}", ex.Message);
                return;
            }
            catch (System.Exception ex)
            {
                document.Editor.WriteMessage("\nCE geometry-first Midblock stopped safely: {0}", ex.Message);
                return;
            }
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE geometry-first Midblock complete. Shared edges={0}; routes={1}; planning manholes={2}. Use CE_SEWERBUILDNETWORK only after reviewing the polylines.", sharedCount, routeCount, manholeCount);
        }

        private static void RunRoadReserve(bool legacyEntry)
        {
            Document document = Active();
            if (document == null) return;
            List<string> surfaceChoices = SurfaceChoices(document);
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Geometry-First Road Reserve Sewer",
                "Create editable sewer polylines inside road reserves from facing outer erf boundaries. No Civil 3D Pipe Network is touched during layout calculation.");
            model.AddChoice("Surface", "01 Analysis", "Surface for flow direction", surfaceChoices[0],
                "Optional surface used only to orient finished planning routes from higher to lower endpoint.", surfaceChoices);
            model.AddPositiveDouble("Offset", "02 Road Reserve", "Offset from erf boundary toward road centre", 1.5,
                "Create a sewer route on each side of an accepted road reserve, offset toward the road centre.");
            model.AddPositiveDouble("MinWidth", "02 Road Reserve", "Minimum road reserve width", 6.0, "Minimum facing-boundary separation.");
            model.AddPositiveDouble("MaxWidth", "02 Road Reserve", "Maximum road reserve width", 60.0, "Maximum facing-boundary separation.");
            model.AddPositiveDouble("MinOverlap", "02 Road Reserve", "Minimum overlap (%)", 50.0, "Minimum overlap as percentage of the shorter facing edge.");
            model.AddPositiveDouble("MinEdge", "02 Road Reserve", "Minimum usable outer edge length", 4.0, "Ignore shorter outer erf edges.");
            model.AddChoice("Spacing", "03 Manholes", "Maximum manhole spacing", "60 m",
                "Route vertices and manhole circles are created at this maximum interval.", new[] { "60 m", "80 m", "Custom" });
            model.AddPositiveDouble("CustomSpacing", "03 Manholes", "Custom spacing", 60.0, "Used only for Custom spacing.");
            model.AddPositiveDouble("Diameter", "03 Manholes", "Planning manhole diameter", 1.2, "Visible planning-circle diameter.");
            model.AddChoice("Replace", "04 Output", "Existing Road Reserve layout", "Replace existing",
                "Replace previous geometry-first Road Reserve sewer layout or keep it.", new[] { "Replace existing", "Keep existing" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            ObjectId surfaceId = August20SurfaceChoice.ResolveSurfaceId(document, model.Text("Surface"));
            PromptSelectionResult selection = SelectClosedPolylines(document.Editor,
                "\nSelect closed cadastral erf polylines around the road reserves: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0) return;

            double offset = Math.Max(0.0, model.Double("Offset", 1.5));
            double minWidth = Math.Max(0.1, model.Double("MinWidth", 6.0));
            double maxWidth = Math.Max(minWidth, model.Double("MaxWidth", 60.0));
            double minOverlap = Math.Max(1.0, Math.Min(100.0, model.Double("MinOverlap", 50.0)));
            double minEdge = Math.Max(0.1, model.Double("MinEdge", 4.0));
            double spacing = ReadSpacing(model);
            double diameter = Math.Max(0.1, model.Double("Diameter", 1.2));
            int pairCount = 0;
            int routeCount = 0;
            int manholeCount = 0;

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    List<ParcelLite> parcels = ReadParcels(selection.Value.GetObjectIds(), transaction);
                    List<EdgeLite> exterior = ExteriorEdges(BuildEdges(parcels, minEdge));
                    List<EdgePair> pairs = PairRoadReserveEdges(exterior, parcels, minWidth, maxWidth, minOverlap);
                    if (pairs.Count == 0)
                    {
                        document.Editor.WriteMessage("\nCE Road Reserve: no facing outer erf edges satisfied the width/overlap conditions.");
                        return;
                    }
                    CivilSurface surface = OpenSurface(transaction, surfaceId);
                    BlockTableRecord space = OpenModelSpace(document.Database, transaction, OpenMode.ForWrite);
                    if (space == null) return;
                    if (string.Equals(model.Text("Replace"), "Replace existing", StringComparison.OrdinalIgnoreCase))
                        SafeEraseByLayers(space, transaction, RoadReserveRouteLayer, RoadReserveMhLayer, RoadReserveLabelLayer);
                    ObjectId routeLayer = EnsureLayer(document.Database, transaction, RoadReserveRouteLayer);
                    ObjectId mhLayer = EnsureLayer(document.Database, transaction, RoadReserveMhLayer);
                    ObjectId labelLayer = EnsureLayer(document.Database, transaction, RoadReserveLabelLayer);

                    int routeNumber = 1;
                    foreach (EdgePair pair in pairs)
                    {
                        Line2 first;
                        Line2 second;
                        if (!BuildRoadReserveOffsetLines(pair, offset, out first, out second)) continue;
                        foreach (Line2 routeLineValue in new[] { first, second })
                        {
                            Line2 routeLine = routeLineValue;
                            if (routeLine.Length <= Tol) continue;
                            OrientDownhill(ref routeLine, surface);
                            Polyline route = CreateSpacedPolyline(document.Database, routeLine, spacing, routeLayer);
                            space.AppendEntity(route);
                            transaction.AddNewlyCreatedDBObject(route, true);
                            WriteLayoutRecord(route, transaction, "ROADRESERVE", spacing, diameter, model.Text("Surface"));
                            manholeCount += AddManholesAtVertices(document.Database, transaction, space, route, mhLayer, labelLayer, diameter, "RR-MH", routeNumber);
                            routeCount++;
                            routeNumber++;
                        }
                        pairCount++;
                    }
                    transaction.Commit();
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                document.Editor.WriteMessage("\nCE geometry-first Road Reserve sewer stopped safely: {0}", ex.Message);
                return;
            }
            catch (System.Exception ex)
            {
                document.Editor.WriteMessage("\nCE geometry-first Road Reserve sewer stopped safely: {0}", ex.Message);
                return;
            }
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE geometry-first Road Reserve sewer complete. Reserve pairs={0}; routes={1}; planning manholes={2}. Review/edit the polylines before CE_SEWERBUILDNETWORK.", pairCount, routeCount, manholeCount);
        }

        private static void RunRoadCenterline(bool legacyEntry)
        {
            Document document = Active();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Road Centreline from Road Reserves (Polyline First)",
                "Create ordinary AutoCAD centreline polylines from facing road-reserve erf boundaries. Civil 3D Alignment creation is a separate CE_CENTERLINETOALIGNMENT step.");
            model.AddPositiveDouble("MinWidth", "01 Reserve", "Minimum road reserve width", 6.0, "Minimum facing-boundary separation.");
            model.AddPositiveDouble("MaxWidth", "01 Reserve", "Maximum road reserve width", 60.0, "Maximum facing-boundary separation.");
            model.AddPositiveDouble("MinOverlap", "01 Reserve", "Minimum overlap (%)", 50.0, "Minimum overlap as percentage of the shorter edge.");
            model.AddPositiveDouble("MinEdge", "01 Reserve", "Minimum usable outer edge length", 4.0, "Ignore shorter outer erf edges.");
            model.AddChoice("Replace", "02 Output", "Existing CE centreline layout", "Replace existing",
                "Replace previous geometry-first road centreline polylines or keep them.", new[] { "Replace existing", "Keep existing" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = SelectClosedPolylines(document.Editor,
                "\nSelect closed cadastral erf polylines around the road reserves: ");
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0) return;
            double minWidth = Math.Max(0.1, model.Double("MinWidth", 6.0));
            double maxWidth = Math.Max(minWidth, model.Double("MaxWidth", 60.0));
            double minOverlap = Math.Max(1.0, Math.Min(100.0, model.Double("MinOverlap", 50.0)));
            double minEdge = Math.Max(0.1, model.Double("MinEdge", 4.0));
            int created = 0;

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    List<ParcelLite> parcels = ReadParcels(selection.Value.GetObjectIds(), transaction);
                    List<EdgeLite> exterior = ExteriorEdges(BuildEdges(parcels, minEdge));
                    List<EdgePair> pairs = PairRoadReserveEdges(exterior, parcels, minWidth, maxWidth, minOverlap);
                    BlockTableRecord space = OpenModelSpace(document.Database, transaction, OpenMode.ForWrite);
                    if (space == null) return;
                    if (string.Equals(model.Text("Replace"), "Replace existing", StringComparison.OrdinalIgnoreCase))
                        SafeEraseByLayers(space, transaction, RoadCenterLayer);
                    ObjectId layer = EnsureLayer(document.Database, transaction, RoadCenterLayer);
                    foreach (EdgePair pair in pairs)
                    {
                        Line2 centre;
                        if (!BuildRoadCenterline(pair, out centre) || centre.Length <= Tol) continue;
                        var polyline = new Polyline(2);
                        polyline.SetDatabaseDefaults(document.Database);
                        polyline.LayerId = layer;
                        polyline.AddVertexAt(0, centre.A, 0.0, 0.0, 0.0);
                        polyline.AddVertexAt(1, centre.B, 0.0, 0.0, 0.0);
                        space.AppendEntity(polyline);
                        transaction.AddNewlyCreatedDBObject(polyline, true);
                        created++;
                    }
                    transaction.Commit();
                }
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                document.Editor.WriteMessage("\nCE road-centreline layout stopped safely: {0}", ex.Message);
                return;
            }
            catch (System.Exception ex)
            {
                document.Editor.WriteMessage("\nCE road-centreline layout stopped safely: {0}", ex.Message);
                return;
            }
            document.Editor.Regen();
            document.Editor.WriteMessage("\nCE road-centreline polyline layout complete. Centreline segments={0}. Review them, then run CE_CENTERLINETOALIGNMENT.", created);
        }

        private static List<string> SurfaceChoices(Document document)
        {
            List<string> names = August20SurfaceChoice.ReadSurfaceNames(document);
            var result = new List<string> { August20SurfaceChoice.None };
            result.AddRange(names.Where(value => !string.Equals(value, August20SurfaceChoice.None, StringComparison.OrdinalIgnoreCase)));
            return result;
        }

        private static double ReadSpacing(ProductionSettingsDialogModel model)
        {
            if (string.Equals(model.Text("Spacing"), "80 m", StringComparison.OrdinalIgnoreCase)) return 80.0;
            if (string.Equals(model.Text("Spacing"), "Custom", StringComparison.OrdinalIgnoreCase))
                return Math.Max(1.0, model.Double("CustomSpacing", 60.0));
            return 60.0;
        }

        private static PromptSelectionResult SelectClosedPolylines(Editor editor, string message)
        {
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            }, new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") }));
        }

        private static PromptSelectionResult SelectPolylines(Editor editor, string message)
        {
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            }, new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") }));
        }

        private static ObjectId[] FilterByLayerPrefix(Document document, IEnumerable<ObjectId> source, string prefix)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in source.Where(value => !value.IsNull && !value.IsErased).Distinct())
                {
                    Polyline polyline;
                    try { polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                    catch { continue; }
                    if (polyline != null && !string.IsNullOrWhiteSpace(polyline.Layer) && polyline.Layer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        result.Add(id);
                }
            }
            return result.ToArray();
        }

        private static List<ParcelLite> ReadParcels(IEnumerable<ObjectId> ids, Transaction transaction)
        {
            var result = new List<ParcelLite>();
            int index = 0;
            foreach (ObjectId id in ids.Where(value => !value.IsNull && !value.IsErased).Distinct())
            {
                Polyline polyline;
                try { polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline; }
                catch { continue; }
                if (polyline == null || !polyline.Closed || polyline.NumberOfVertices < 3) continue;
                var points = new List<Point2d>();
                bool valid = true;
                for (int i = 0; i < polyline.NumberOfVertices; i++)
                {
                    Point2d point;
                    try { point = polyline.GetPoint2dAt(i); }
                    catch { valid = false; break; }
                    if (!Finite(point)) { valid = false; break; }
                    if (points.Count == 0 || Distance(points[points.Count - 1], point) > Tol)
                        points.Add(point);
                }
                if (!valid || points.Count < 3) continue;
                double minX = points.Min(point => point.X);
                double maxX = points.Max(point => point.X);
                double minY = points.Min(point => point.Y);
                double maxY = points.Max(point => point.Y);
                if (maxX - minX <= Tol || maxY - minY <= Tol) continue;
                result.Add(new ParcelLite(index++, id, points,
                    new Point2d((minX + maxX) * 0.5, (minY + maxY) * 0.5)));
            }
            return result;
        }

        private static List<EdgeLite> BuildEdges(List<ParcelLite> parcels, double minimumLength)
        {
            var result = new List<EdgeLite>();
            int id = 0;
            foreach (ParcelLite parcel in parcels)
            {
                for (int i = 0; i < parcel.Points.Count; i++)
                {
                    Point2d a = parcel.Points[i];
                    Point2d b = parcel.Points[(i + 1) % parcel.Points.Count];
                    double length = Distance(a, b);
                    if (length + Tol < minimumLength) continue;
                    double dx = Math.Abs(b.X - a.X);
                    double dy = Math.Abs(b.Y - a.Y);
                    bool horizontal = dx >= dy && dy <= Math.Max(KeyTolerance, dx * 0.02);
                    bool vertical = dy > dx && dx <= Math.Max(KeyTolerance, dy * 0.02);
                    if (!horizontal && !vertical) continue;
                    result.Add(new EdgeLite(id++, parcel.Index, a, b, length, horizontal, vertical, parcel.Center));
                }
            }
            return result;
        }

        private static List<EdgeLite> SharedEdges(List<EdgeLite> edges)
        {
            return edges.GroupBy(edge => EdgeKey(edge.A, edge.B), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Select(edge => edge.ParcelIndex).Distinct().Count() >= 2)
                .Select(group => group.First())
                .ToList();
        }

        private static List<EdgeLite> ExteriorEdges(List<EdgeLite> edges)
        {
            return edges.GroupBy(edge => EdgeKey(edge.A, edge.B), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Select(edge => edge.ParcelIndex).Distinct().Count() == 1)
                .Select(group => group.First())
                .ToList();
        }

        private static bool ResolveHorizontal(List<EdgeLite> edges, string choice)
        {
            if (string.Equals(choice, "Horizontal", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(choice, "Vertical", StringComparison.OrdinalIgnoreCase)) return false;
            double horizontal = edges.Where(edge => edge.Horizontal).Sum(edge => edge.Length);
            double vertical = edges.Where(edge => edge.Vertical).Sum(edge => edge.Length);
            return horizontal >= vertical;
        }

        private static List<Line2> MergeAxisAligned(List<EdgeLite> edges, bool horizontal)
        {
            var groups = new Dictionary<long, List<Line2>>();
            foreach (EdgeLite edge in edges)
            {
                double fixedValue = horizontal ? (edge.A.Y + edge.B.Y) * 0.5 : (edge.A.X + edge.B.X) * 0.5;
                long key = Quantize(fixedValue);
                List<Line2> list;
                if (!groups.TryGetValue(key, out list))
                {
                    list = new List<Line2>();
                    groups[key] = list;
                }
                Point2d a;
                Point2d b;
                if (horizontal)
                {
                    double lo = Math.Min(edge.A.X, edge.B.X);
                    double hi = Math.Max(edge.A.X, edge.B.X);
                    a = new Point2d(lo, fixedValue);
                    b = new Point2d(hi, fixedValue);
                }
                else
                {
                    double lo = Math.Min(edge.A.Y, edge.B.Y);
                    double hi = Math.Max(edge.A.Y, edge.B.Y);
                    a = new Point2d(fixedValue, lo);
                    b = new Point2d(fixedValue, hi);
                }
                list.Add(new Line2(a, b));
            }

            var result = new List<Line2>();
            foreach (List<Line2> list in groups.Values)
            {
                List<Line2> ordered = horizontal
                    ? list.OrderBy(line => line.A.X).ToList()
                    : list.OrderBy(line => line.A.Y).ToList();
                if (ordered.Count == 0) continue;
                Line2 current = ordered[0];
                for (int i = 1; i < ordered.Count; i++)
                {
                    Line2 next = ordered[i];
                    double gap = horizontal ? next.A.X - current.B.X : next.A.Y - current.B.Y;
                    if (gap <= KeyTolerance * 2.0)
                    {
                        if (horizontal)
                            current = new Line2(current.A, new Point2d(Math.Max(current.B.X, next.B.X), current.A.Y));
                        else
                            current = new Line2(current.A, new Point2d(current.A.X, Math.Max(current.B.Y, next.B.Y)));
                    }
                    else
                    {
                        result.Add(current);
                        current = next;
                    }
                }
                result.Add(current);
            }
            return result;
        }

        private static Line2 OffsetMidblock(Line2 source, bool horizontal, double offset, string sideChoice, CivilSurface surface)
        {
            if (offset <= Tol || string.Equals(sideChoice, "On shared boundary", StringComparison.OrdinalIgnoreCase)) return source;
            Vector2d normal = horizontal ? new Vector2d(0.0, 1.0) : new Vector2d(-1.0, 0.0);
            if (string.Equals(sideChoice, "Right / Bottom", StringComparison.OrdinalIgnoreCase)) normal = -normal;
            if (string.Equals(sideChoice, "Automatic lower side", StringComparison.OrdinalIgnoreCase) && surface != null)
            {
                Point2d mid = Mid(source.A, source.B);
                double positive;
                double negative;
                bool hasPositive = TryElevation(surface, mid + normal.MultiplyBy(offset), out positive);
                bool hasNegative = TryElevation(surface, mid - normal.MultiplyBy(offset), out negative);
                if (hasPositive && hasNegative && negative < positive) normal = -normal;
            }
            Vector2d shift = normal.MultiplyBy(offset);
            return new Line2(source.A + shift, source.B + shift);
        }

        private static List<EdgePair> PairRoadReserveEdges(List<EdgeLite> exterior, List<ParcelLite> parcels, double minWidth, double maxWidth, double minOverlapPercent)
        {
            var pairs = new List<EdgePair>();
            var used = new HashSet<int>();
            foreach (EdgeLite first in exterior.OrderByDescending(edge => edge.Length))
            {
                if (used.Contains(first.Id)) continue;
                EdgeLite best = null;
                double bestGap = double.MaxValue;
                double bestLo = 0.0;
                double bestHi = 0.0;
                foreach (EdgeLite second in exterior)
                {
                    if (second.Id == first.Id || used.Contains(second.Id) || second.ParcelIndex == first.ParcelIndex) continue;
                    if (first.Horizontal != second.Horizontal || first.Vertical != second.Vertical) continue;
                    double gap;
                    double lo;
                    double hi;
                    if (!OverlapAndGap(first, second, out gap, out lo, out hi)) continue;
                    if (gap < minWidth - Tol || gap > maxWidth + Tol) continue;
                    double overlap = hi - lo;
                    double required = Math.Min(first.Length, second.Length) * minOverlapPercent / 100.0;
                    if (overlap + Tol < required) continue;
                    if (!FacesGap(first, second)) continue;
                    if (gap < bestGap)
                    {
                        best = second;
                        bestGap = gap;
                        bestLo = lo;
                        bestHi = hi;
                    }
                }
                if (best == null) continue;
                used.Add(first.Id);
                used.Add(best.Id);
                pairs.Add(new EdgePair(first, best, bestGap, bestLo, bestHi));
            }
            return pairs;
        }

        private static bool OverlapAndGap(EdgeLite first, EdgeLite second, out double gap, out double lo, out double hi)
        {
            gap = 0.0;
            lo = 0.0;
            hi = 0.0;
            if (first.Horizontal && second.Horizontal)
            {
                double y1 = (first.A.Y + first.B.Y) * 0.5;
                double y2 = (second.A.Y + second.B.Y) * 0.5;
                gap = Math.Abs(y2 - y1);
                lo = Math.Max(Math.Min(first.A.X, first.B.X), Math.Min(second.A.X, second.B.X));
                hi = Math.Min(Math.Max(first.A.X, first.B.X), Math.Max(second.A.X, second.B.X));
                return hi - lo > Tol;
            }
            if (first.Vertical && second.Vertical)
            {
                double x1 = (first.A.X + first.B.X) * 0.5;
                double x2 = (second.A.X + second.B.X) * 0.5;
                gap = Math.Abs(x2 - x1);
                lo = Math.Max(Math.Min(first.A.Y, first.B.Y), Math.Min(second.A.Y, second.B.Y));
                hi = Math.Min(Math.Max(first.A.Y, first.B.Y), Math.Max(second.A.Y, second.B.Y));
                return hi - lo > Tol;
            }
            return false;
        }

        private static bool FacesGap(EdgeLite first, EdgeLite second)
        {
            Point2d m1 = Mid(first.A, first.B);
            Point2d m2 = Mid(second.A, second.B);
            if (first.Horizontal)
            {
                double parcelSide1 = first.ParcelCenter.Y - m1.Y;
                double otherSide1 = m2.Y - m1.Y;
                double parcelSide2 = second.ParcelCenter.Y - m2.Y;
                double otherSide2 = m1.Y - m2.Y;
                return parcelSide1 * otherSide1 < 0.0 && parcelSide2 * otherSide2 < 0.0;
            }
            double parcelX1 = first.ParcelCenter.X - m1.X;
            double otherX1 = m2.X - m1.X;
            double parcelX2 = second.ParcelCenter.X - m2.X;
            double otherX2 = m1.X - m2.X;
            return parcelX1 * otherX1 < 0.0 && parcelX2 * otherX2 < 0.0;
        }

        private static bool BuildRoadReserveOffsetLines(EdgePair pair, double requestedOffset, out Line2 first, out Line2 second)
        {
            first = new Line2();
            second = new Line2();
            double offset = Math.Min(Math.Max(0.0, requestedOffset), pair.Width * 0.45);
            if (pair.First.Horizontal)
            {
                double y1 = (pair.First.A.Y + pair.First.B.Y) * 0.5;
                double y2 = (pair.Second.A.Y + pair.Second.B.Y) * 0.5;
                double sign1 = y2 >= y1 ? 1.0 : -1.0;
                double sign2 = -sign1;
                first = new Line2(new Point2d(pair.OverlapLo, y1 + sign1 * offset), new Point2d(pair.OverlapHi, y1 + sign1 * offset));
                second = new Line2(new Point2d(pair.OverlapLo, y2 + sign2 * offset), new Point2d(pair.OverlapHi, y2 + sign2 * offset));
                return true;
            }
            if (pair.First.Vertical)
            {
                double x1 = (pair.First.A.X + pair.First.B.X) * 0.5;
                double x2 = (pair.Second.A.X + pair.Second.B.X) * 0.5;
                double sign1 = x2 >= x1 ? 1.0 : -1.0;
                double sign2 = -sign1;
                first = new Line2(new Point2d(x1 + sign1 * offset, pair.OverlapLo), new Point2d(x1 + sign1 * offset, pair.OverlapHi));
                second = new Line2(new Point2d(x2 + sign2 * offset, pair.OverlapLo), new Point2d(x2 + sign2 * offset, pair.OverlapHi));
                return true;
            }
            return false;
        }

        private static bool BuildRoadCenterline(EdgePair pair, out Line2 centre)
        {
            centre = new Line2();
            if (pair.First.Horizontal)
            {
                double y = ((pair.First.A.Y + pair.First.B.Y) * 0.5 + (pair.Second.A.Y + pair.Second.B.Y) * 0.5) * 0.5;
                centre = new Line2(new Point2d(pair.OverlapLo, y), new Point2d(pair.OverlapHi, y));
                return true;
            }
            if (pair.First.Vertical)
            {
                double x = ((pair.First.A.X + pair.First.B.X) * 0.5 + (pair.Second.A.X + pair.Second.B.X) * 0.5) * 0.5;
                centre = new Line2(new Point2d(x, pair.OverlapLo), new Point2d(x, pair.OverlapHi));
                return true;
            }
            return false;
        }

        private static CivilSurface OpenSurface(Transaction transaction, ObjectId id)
        {
            if (id.IsNull || id.IsErased) return null;
            try { return transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface; }
            catch { return null; }
        }

        private static bool TryElevation(CivilSurface surface, Point2d point, out double elevation)
        {
            elevation = double.NaN;
            if (surface == null || !Finite(point)) return false;
            try
            {
                Extents3d extents = surface.GeometricExtents;
                double margin = 0.01;
                if (point.X < extents.MinPoint.X - margin || point.X > extents.MaxPoint.X + margin ||
                    point.Y < extents.MinPoint.Y - margin || point.Y > extents.MaxPoint.Y + margin)
                    return false;
            }
            catch { }
            try
            {
                elevation = surface.FindElevationAtXY(point.X, point.Y);
                return !double.IsNaN(elevation) && !double.IsInfinity(elevation);
            }
            catch
            {
                elevation = double.NaN;
                return false;
            }
        }

        private static void OrientDownhill(ref Line2 line, CivilSurface surface)
        {
            if (surface == null) return;
            double start;
            double end;
            if (!TryElevation(surface, line.A, out start) || !TryElevation(surface, line.B, out end)) return;
            if (start + 1e-6 < end)
                line = new Line2(line.B, line.A);
        }

        private static Polyline CreateSpacedPolyline(Database database, Line2 line, double spacing, ObjectId layerId)
        {
            double length = line.Length;
            int intervals = Math.Max(1, (int)Math.Ceiling(length / Math.Max(1.0, spacing)));
            var polyline = new Polyline(intervals + 1);
            polyline.SetDatabaseDefaults(database);
            polyline.LayerId = layerId;
            for (int i = 0; i <= intervals; i++)
            {
                double t = intervals == 0 ? 0.0 : (double)i / intervals;
                Point2d point = new Point2d(line.A.X + (line.B.X - line.A.X) * t, line.A.Y + (line.B.Y - line.A.Y) * t);
                polyline.AddVertexAt(polyline.NumberOfVertices, point, 0.0, 0.0, 0.0);
            }
            return polyline;
        }

        private static void InsertSpacingVertices(Polyline route, double spacing)
        {
            if (route == null || route.Length <= Tol || spacing <= 0.0) return;
            var points = new List<Point2d>();
            double length = route.Length;
            int intervals = Math.Max(1, (int)Math.Ceiling(length / spacing));
            for (int i = 0; i <= intervals; i++)
            {
                double station = length * i / intervals;
                Point3d point;
                try { point = route.GetPointAtDist(Math.Min(length, Math.Max(0.0, station))); }
                catch { continue; }
                points.Add(new Point2d(point.X, point.Y));
            }
            if (points.Count < 2) return;
            while (route.NumberOfVertices > 0) route.RemoveVertexAt(route.NumberOfVertices - 1);
            foreach (Point2d point in points) route.AddVertexAt(route.NumberOfVertices, point, 0.0, 0.0, 0.0);
        }

        private static int AddManholesAtVertices(Database database, Transaction transaction, BlockTableRecord space,
            Polyline route, ObjectId mhLayer, ObjectId labelLayer, double diameter, string prefix, int routeNumber)
        {
            int count = 0;
            for (int i = 0; i < route.NumberOfVertices; i++)
            {
                Point2d point = route.GetPoint2dAt(i);
                Point3d location = new Point3d(point.X, point.Y, route.Elevation);
                var circle = new Circle(location, Vector3d.ZAxis, diameter * 0.5);
                circle.SetDatabaseDefaults(database);
                circle.LayerId = mhLayer;
                space.AppendEntity(circle);
                transaction.AddNewlyCreatedDBObject(circle, true);
                WriteLayoutRecord(circle, transaction, prefix, 0.0, diameter, string.Empty);

                var text = new DBText();
                text.SetDatabaseDefaults(database);
                text.LayerId = labelLayer;
                text.Position = location + new Vector3d(diameter, diameter, 0.0);
                text.TextString = prefix + routeNumber.ToString(CultureInfo.InvariantCulture) + "." + (i + 1).ToString(CultureInfo.InvariantCulture);
                text.Height = Math.Max(PaperAnnotationScale.ModelTextHeight(database, 2.0), 0.001);
                space.AppendEntity(text);
                transaction.AddNewlyCreatedDBObject(text, true);
                count++;
            }
            return count;
        }

        private static void WriteLayoutRecord(DBObject owner, Transaction transaction, string kind, double spacing, double diameter, string surfaceName)
        {
            try
            {
                if (owner.ExtensionDictionary.IsNull) owner.CreateExtensionDictionary();
                DBDictionary dictionary = transaction.GetObject(owner.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
                if (dictionary == null) return;
                const string key = "CE_GEOMETRY_FIRST_LAYOUT";
                Xrecord record;
                if (dictionary.Contains(key))
                    record = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForWrite, false) as Xrecord;
                else
                {
                    record = new Xrecord();
                    dictionary.SetAt(key, record);
                    transaction.AddNewlyCreatedDBObject(record, true);
                }
                if (record != null)
                {
                    record.Data = new ResultBuffer(
                        new TypedValue((int)DxfCode.Text, kind ?? string.Empty),
                        new TypedValue((int)DxfCode.Real, spacing),
                        new TypedValue((int)DxfCode.Real, diameter),
                        new TypedValue((int)DxfCode.Text, surfaceName ?? string.Empty));
                }
            }
            catch { }
        }

        private static ObjectId EnsureLayer(Database database, Transaction transaction, string name)
        {
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table == null) return ObjectId.Null;
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var record = new LayerTableRecord { Name = name };
            ObjectId id = table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return id;
        }

        private static void SafeEraseByLayers(BlockTableRecord space, Transaction transaction, params string[] layerNames)
        {
            var names = new HashSet<string>(layerNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in space.Cast<ObjectId>().ToList())
            {
                Entity entity;
                try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch { continue; }
                if (entity == null || entity.IsErased || !names.Contains(entity.Layer)) continue;
                if (!(entity is Polyline) && !(entity is Circle) && !(entity is DBText)) continue;
                try
                {
                    entity.UpgradeOpen();
                    entity.Erase();
                }
                catch { }
            }
        }

        private static void EraseLayoutManholes(BlockTableRecord space, Transaction transaction, params string[] layerNames)
        {
            SafeEraseByLayers(space, transaction, layerNames);
        }

        private static BlockTableRecord OpenModelSpace(Database database, Transaction transaction, OpenMode mode)
        {
            try
            {
                return transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), mode, false) as BlockTableRecord;
            }
            catch { return null; }
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private static string EdgeKey(Point2d a, Point2d b)
        {
            string first = PointKey(a);
            string second = PointKey(b);
            return string.Compare(first, second, StringComparison.OrdinalIgnoreCase) <= 0
                ? first + "|" + second
                : second + "|" + first;
        }

        private static string PointKey(Point2d point)
        {
            return Quantize(point.X).ToString(CultureInfo.InvariantCulture) + ":" + Quantize(point.Y).ToString(CultureInfo.InvariantCulture);
        }

        private static long Quantize(double value)
        {
            return (long)Math.Round(value / KeyTolerance, MidpointRounding.AwayFromZero);
        }

        private static bool Finite(Point2d point)
        {
            return !double.IsNaN(point.X) && !double.IsInfinity(point.X) && !double.IsNaN(point.Y) && !double.IsInfinity(point.Y);
        }

        private static double Distance(Point2d a, Point2d b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static Point2d Mid(Point2d a, Point2d b)
        {
            return new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
        }

        private sealed class ParcelLite
        {
            internal readonly int Index;
            internal readonly ObjectId Id;
            internal readonly List<Point2d> Points;
            internal readonly Point2d Center;
            internal ParcelLite(int index, ObjectId id, List<Point2d> points, Point2d center)
            {
                Index = index;
                Id = id;
                Points = points;
                Center = center;
            }
        }

        private sealed class EdgeLite
        {
            internal readonly int Id;
            internal readonly int ParcelIndex;
            internal readonly Point2d A;
            internal readonly Point2d B;
            internal readonly double Length;
            internal readonly bool Horizontal;
            internal readonly bool Vertical;
            internal readonly Point2d ParcelCenter;
            internal EdgeLite(int id, int parcelIndex, Point2d a, Point2d b, double length, bool horizontal, bool vertical, Point2d parcelCenter)
            {
                Id = id;
                ParcelIndex = parcelIndex;
                A = a;
                B = b;
                Length = length;
                Horizontal = horizontal;
                Vertical = vertical;
                ParcelCenter = parcelCenter;
            }
        }

        private sealed class EdgePair
        {
            internal readonly EdgeLite First;
            internal readonly EdgeLite Second;
            internal readonly double Width;
            internal readonly double OverlapLo;
            internal readonly double OverlapHi;
            internal EdgePair(EdgeLite first, EdgeLite second, double width, double overlapLo, double overlapHi)
            {
                First = first;
                Second = second;
                Width = width;
                OverlapLo = overlapLo;
                OverlapHi = overlapHi;
            }
        }

        private struct Line2
        {
            internal Point2d A;
            internal Point2d B;
            internal Line2(Point2d a, Point2d b)
            {
                A = a;
                B = b;
            }
            internal double Length { get { return Distance(A, B); } }
        }
    }
}
