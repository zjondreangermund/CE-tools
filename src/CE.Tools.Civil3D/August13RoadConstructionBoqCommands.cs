using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using FeatureLinePointType = Autodesk.Civil.FeatureLinePointType;

[assembly: CommandClass(typeof(CETools.Civil3D.August13RoadConstructionBoqCommands))]

namespace CETools.Civil3D
{
    public sealed class August13RoadConstructionBoqCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ROADBOQCONSTRUCTION", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RoadConstructionBoq()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            ObjectId baseSurfaceId;
            if (!August12SurfaceSelectionPopup.TrySelectOne(
                    document,
                    "CE Tools - Road BOQ Existing Ground",
                    "Choose the existing-ground/base surface. CE Tools compares it with each road-numbered BOTTOM-RD-* corridor surface (Datum/Subgrade) for cut/fill to datum.",
                    "Existing ground / base surface",
                    out baseSurfaceId))
                return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Road Construction BOQ",
                "Quantities are read from the current Civil 3D corridor model. Layerwork is integrated from calculated corridor shapes; road/sidewalk/side-slope areas are integrated from coded links; kerb length is read from coded corridor feature lines.");
            model.AddDouble(
                "UnitsPerMetre",
                "01 Units",
                "Drawing units per metre",
                1.0,
                "Use 1 for metre-based Civil 3D drawings, or the appropriate drawing-unit conversion where required.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            double unitsPerMetre = Math.Max(model.Double("UnitsPerMetre", 1.0), 1e-9);

            var totals = new QuantityAccumulator();
            var warnings = new List<string>();
            int corridorCount = 0;

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId corridorId in civilDocument.CorridorCollection)
                    {
                        Corridor corridor = transaction.GetObject(
                            corridorId,
                            OpenMode.ForWrite,
                            false) as Corridor;
                        if (!IsRoadCorridor(corridor)) continue;
                        corridorCount++;

                        double roadLength =
                            ReadCorridorRoadLength(
                                corridor) /
                            unitsPerMetre;
                        totals.TotalRoadLength +=
                            roadLength;
                        AddValue(
                            totals.RoadLengths,
                            corridor.Name,
                            roadLength);

                        AddDatumCutFill(
                            corridor,
                            baseSurfaceId,
                            unitsPerMetre,
                            transaction,
                            totals,
                            warnings);
                        AddCorridorSectionQuantities(
                            corridor,
                            unitsPerMetre,
                            totals,
                            warnings);
                        totals.KerbLength += ReadKerbFeatureLineLength(
                            corridor,
                            unitsPerMetre);
                    }

                    totals.JunctionBellmouthLength =
                        ReadJunctionBellmouthLength(
                            document.Database,
                            transaction,
                            unitsPerMetre);

                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADBOQCONSTRUCTION failed. {0}",
                    exception.Message);
                return;
            }

            var rows = new List<IList<string>>();

            foreach (KeyValuePair<string, double> road in
                totals.RoadLengths
                    .OrderBy(
                        item => item.Key,
                        StringComparer.CurrentCultureIgnoreCase))
            {
                rows.Add(
                    Row(
                        "Road length - " + road.Key,
                        "m",
                        road.Value,
                        "Corridor baseline station range"));
            }
            rows.Add(
                Row(
                    "Total road length",
                    "m",
                    totals.TotalRoadLength,
                    "Sum of CE road corridor baseline lengths"));

            rows.Add(
                Row(
                    "Earthworks - Cut to corridor datum",
                    "m3",
                    totals.CutVolume,
                    "Existing ground vs BOTTOM-RD-* corridor surface"));
            rows.Add(
                Row(
                    "Earthworks - Fill to corridor datum",
                    "m3",
                    totals.FillVolume,
                    "Existing ground vs BOTTOM-RD-* corridor surface"));

            foreach (KeyValuePair<string, double> layer in
                totals.LayerVolumes.OrderBy(
                    item => item.Key,
                    StringComparer.CurrentCultureIgnoreCase))
            {
                rows.Add(Row(
                    "Road layerwork - " + layer.Key,
                    "m3",
                    layer.Value,
                    "Assembly shape area integrated between corridor stations"));
            }

            rows.Add(Row(
                "Kerbs",
                "m",
                totals.KerbLength,
                "Corridor feature lines carrying kerb/curb codes"));
            rows.Add(Row(
                "Junction bellmouths",
                "m",
                totals.JunctionBellmouthLength,
                "Unique CE road-junction / bellmouth return curves"));
            rows.Add(Row(
                "Road surface",
                "m2",
                totals.RoadSurfaceArea,
                "Road/top/pave/lane coded corridor links"));
            rows.Add(Row(
                "Sidewalks",
                "m2",
                totals.SidewalkArea,
                "Sidewalk/walk/footway coded corridor links"));
            rows.Add(Row(
                "Cut/fill side slopes",
                "m2",
                totals.SideSlopeArea,
                "Daylight/slope/batter coded corridor links"));

            string note = string.Format(
                CultureInfo.CurrentCulture,
                "Road corridors={0}; total road length={1:N3} m. Re-run CE_ROADBOQCONSTRUCTION after corridor/surface edits to recalculate from the live model. {2}",
                corridorCount,
                totals.TotalRoadLength,
                warnings.Count == 0
                    ? "No quantity warnings."
                    : "Warnings: " + string.Join(" | ", warnings.Take(6)));

            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Road Construction BOQ",
                note,
                new List<string>
                {
                    "Item",
                    "Unit",
                    "Quantity",
                    "Model Source"
                },
                rows,
                "CE ROAD CONSTRUCTION BOQ");

            document.Editor.WriteMessage(
                "\nCE_ROADBOQCONSTRUCTION complete. Corridors={0}; BOQ rows={1}; warnings={2}.",
                corridorCount,
                rows.Count,
                warnings.Count);
        }

        private static void AddDatumCutFill(
            Corridor corridor,
            ObjectId baseSurfaceId,
            double unitsPerMetre,
            Transaction transaction,
            QuantityAccumulator totals,
            ICollection<string> warnings)
        {
            CorridorSurface datum =
                FindBottomCorridorSurface(
                    corridor);
            if (datum == null)
            {
                warnings.Add(
                    corridor.Name +
                    ": road-numbered BOTTOM corridor surface is missing.");
                return;
            }

            try
            {
                if (!datum.IsBuild)
                    datum.IsBuild = true;
                corridor.Rebuild();
            }
            catch (System.Exception exception)
            {
                warnings.Add(
                    corridor.Name +
                    ": BOTTOM corridor surface could not be rebuilt - " +
                    exception.Message);
            }

            if (datum.SurfaceId.IsNull)
            {
                warnings.Add(
                    corridor.Name +
                    ": BOTTOM corridor surface exists but has no built SurfaceId.");
                return;
            }

            string tempName = "CE-TEMP-DATUM-VOLUME-" + Guid.NewGuid().ToString("N");
            ObjectId volumeId = ObjectId.Null;
            try
            {
                volumeId = TinVolumeSurface.Create(
                    tempName,
                    baseSurfaceId,
                    datum.SurfaceId);
                TinVolumeSurface volume = transaction.GetObject(
                    volumeId,
                    OpenMode.ForWrite,
                    false) as TinVolumeSurface;
                if (volume == null)
                    throw new InvalidOperationException("Civil 3D did not return the temporary datum volume surface.");

                VolumeSurfaceProperties properties = volume.GetVolumeProperties();
                double divisor = unitsPerMetre * unitsPerMetre * unitsPerMetre;
                totals.CutVolume += Math.Abs(properties.UnadjustedCutVolume) / divisor;
                totals.FillVolume += Math.Abs(properties.UnadjustedFillVolume) / divisor;
                volume.Erase(true);
            }
            catch (System.Exception exception)
            {
                if (!volumeId.IsNull)
                {
                    try
                    {
                        DBObject value = transaction.GetObject(
                            volumeId,
                            OpenMode.ForWrite,
                            false);
                        if (value != null && !value.IsErased) value.Erase(true);
                    }
                    catch { }
                }
                warnings.Add(
                    corridor.Name + ": datum cut/fill unavailable - " + exception.Message);
            }
        }

        private static CorridorSurface FindBottomCorridorSurface(
            Corridor corridor)
        {
            if (corridor == null)
                return null;

            string suffix =
                RoadSurfaceSuffix(
                    corridor.Name);
            CorridorSurface preferred =
                null;
            CorridorSurface fallback =
                null;

            foreach (CorridorSurface surface in
                corridor.CorridorSurfaces)
            {
                if (surface == null)
                    continue;
                string name =
                    surface.Name ?? string.Empty;
                string description =
                    surface.Description ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(suffix) &&
                    string.Equals(
                        name,
                        "BOTTOM-" + suffix,
                        StringComparison.OrdinalIgnoreCase))
                    return surface;

                if (name.StartsWith(
                        "BOTTOM-RD-",
                        StringComparison.OrdinalIgnoreCase))
                    preferred = preferred ?? surface;
                else if (string.Equals(
                             name,
                             "CE-BOTTOM",
                             StringComparison.OrdinalIgnoreCase) ||
                         name.IndexOf(
                             "BOTTOM",
                             StringComparison.OrdinalIgnoreCase) >= 0 ||
                         description.IndexOf(
                             "BOTTOM corridor surface",
                             StringComparison.OrdinalIgnoreCase) >= 0)
                    fallback = fallback ?? surface;
            }

            return preferred ?? fallback;
        }

        private static string RoadSurfaceSuffix(
            string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            int index =
                value.IndexOf(
                    "RD-",
                    StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return string.Empty;
            int start =
                index + 3;
            int end =
                start;
            while (end < value.Length &&
                   char.IsDigit(value[end]))
                end++;
            if (end <= start)
                return string.Empty;

            int number;
            return int.TryParse(
                       value.Substring(
                           start,
                           end - start),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out number)
                ? "RD-" +
                  number.ToString(
                      "00",
                      CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private static double ReadCorridorRoadLength(
            Corridor corridor)
        {
            if (corridor == null)
                return 0.0;
            double total = 0.0;
            foreach (Baseline baseline in corridor.Baselines)
            {
                if (baseline == null)
                    continue;
                double length =
                    Math.Abs(
                        baseline.EndStation -
                        baseline.StartStation);
                if (!double.IsNaN(length) &&
                    !double.IsInfinity(length))
                    total += length;
            }
            return total;
        }

        private static void AddCorridorSectionQuantities(
            Corridor corridor,
            double unitsPerMetre,
            QuantityAccumulator totals,
            ICollection<string> warnings)
        {
            double volumeDivisor = unitsPerMetre * unitsPerMetre * unitsPerMetre;
            double areaDivisor = unitsPerMetre * unitsPerMetre;

            foreach (Baseline baseline in corridor.Baselines)
            {
                if (baseline == null) continue;
                foreach (BaselineRegion region in baseline.BaselineRegions)
                {
                    if (region == null || !region.NeedsProcessing) continue;
                    List<SectionSnapshot> sections = ReadSections(region);
                    if (sections.Count < 2)
                    {
                        warnings.Add(
                            corridor.Name + ": one processed corridor region contains fewer than two usable applied assemblies.");
                        continue;
                    }

                    for (int index = 0; index < sections.Count - 1; index++)
                    {
                        SectionSnapshot first = sections[index];
                        SectionSnapshot second = sections[index + 1];
                        double delta = second.Station - first.Station;
                        if (delta <= 1e-9) continue;

                        var shapeKeys = new HashSet<string>(
                            first.ShapeAreas.Keys,
                            StringComparer.OrdinalIgnoreCase);
                        shapeKeys.UnionWith(second.ShapeAreas.Keys);
                        foreach (string key in shapeKeys)
                        {
                            double a1;
                            double a2;
                            first.ShapeAreas.TryGetValue(key, out a1);
                            second.ShapeAreas.TryGetValue(key, out a2);
                            double volume = 0.5 * (a1 + a2) * delta / volumeDivisor;
                            AddValue(totals.LayerVolumes, key, volume);
                        }

                        totals.RoadSurfaceArea +=
                            0.5 * (first.RoadSurfaceWidth + second.RoadSurfaceWidth) *
                            delta / areaDivisor;
                        totals.SidewalkArea +=
                            0.5 * (first.SidewalkWidth + second.SidewalkWidth) *
                            delta / areaDivisor;
                        totals.SideSlopeArea +=
                            0.5 * (first.SideSlopeWidth + second.SideSlopeWidth) *
                            delta / areaDivisor;
                    }
                }
            }
        }

        private static List<SectionSnapshot> ReadSections(BaselineRegion region)
        {
            var result = new List<SectionSnapshot>();
            foreach (AppliedAssembly assembly in region.AppliedAssemblies)
            {
                if (assembly == null) continue;
                double station;
                if (!TryReadAppliedAssemblyStation(assembly, out station)) continue;

                var snapshot = new SectionSnapshot(station);
                foreach (CalculatedShape shape in assembly.Shapes)
                {
                    if (shape == null) continue;
                    string code = PrimaryShapeCode(shape.CorridorCodes);
                    if (string.IsNullOrWhiteSpace(code)) code = "Unclassified";
                    AddValue(snapshot.ShapeAreas, code, Math.Abs(shape.Area));
                }

                foreach (CalculatedLink link in assembly.Links)
                {
                    if (link == null) continue;
                    double width = LinkCrossSectionLength(link);
                    if (width <= 1e-9) continue;
                    LinkQuantityClass quantityClass = ClassifyLink(link.CorridorCodes);
                    if (quantityClass == LinkQuantityClass.Sidewalk)
                        snapshot.SidewalkWidth += width;
                    else if (quantityClass == LinkQuantityClass.SideSlope)
                        snapshot.SideSlopeWidth += width;
                    else if (quantityClass == LinkQuantityClass.RoadSurface)
                        snapshot.RoadSurfaceWidth += width;
                }
                result.Add(snapshot);
            }

            return result
                .GroupBy(item => Math.Round(item.Station, 4))
                .Select(group => group.First())
                .OrderBy(item => item.Station)
                .ToList();
        }

        private static bool TryReadAppliedAssemblyStation(
            AppliedAssembly assembly,
            out double station)
        {
            station = 0.0;
            foreach (CalculatedPoint point in assembly.Points)
            {
                if (point == null) continue;
                station = point.StationOffsetElevationToBaseline.X;
                return true;
            }
            return false;
        }

        private static string PrimaryShapeCode(CorridorCodeCollection codes)
        {
            var values = new List<string>();
            if (codes != null)
            {
                foreach (string code in codes)
                {
                    if (!string.IsNullOrWhiteSpace(code)) values.Add(code.Trim());
                }
            }
            if (values.Count == 0) return string.Empty;

            string preferred = values.FirstOrDefault(item =>
                ContainsAny(
                    item,
                    "ASPHALT",
                    "PAVE",
                    "BASE",
                    "SUBBASE",
                    "SUB-BASE",
                    "SUBGRADE",
                    "SELECTED",
                    "LAYER",
                    "BED",
                    "FILL"));
            return string.IsNullOrWhiteSpace(preferred) ? values[0] : preferred;
        }

        private static LinkQuantityClass ClassifyLink(CorridorCodeCollection codes)
        {
            string text = JoinCodes(codes);
            if (ContainsAny(text, "SIDEWALK", "WALK", "FOOTWAY"))
                return LinkQuantityClass.Sidewalk;
            if (ContainsAny(text, "DAYLIGHT", "SLOPE", "BATTER"))
                return LinkQuantityClass.SideSlope;
            if (ContainsAny(text, "KERB", "CURB"))
                return LinkQuantityClass.None;
            if (ContainsAny(text, "PAVE", "LANE", "ROAD", "TOP", "ETW"))
                return LinkQuantityClass.RoadSurface;
            return LinkQuantityClass.None;
        }

        private static double LinkCrossSectionLength(CalculatedLink link)
        {
            if (link == null ||
                link.CalculatedPoints == null ||
                link.CalculatedPoints.Count < 2)
                return 0.0;

            double total = 0.0;
            CalculatedPoint previous = null;
            foreach (CalculatedPoint point in link.CalculatedPoints)
            {
                if (point == null) continue;
                if (previous != null)
                {
                    Point3d a = previous.StationOffsetElevationToBaseline;
                    Point3d b = point.StationOffsetElevationToBaseline;
                    double dy = b.Y - a.Y;
                    double dz = b.Z - a.Z;
                    total += Math.Sqrt(dy * dy + dz * dz);
                }
                previous = point;
            }
            return total;
        }

        private static double ReadKerbFeatureLineLength(
            Corridor corridor,
            double unitsPerMetre)
        {
            double total = 0.0;
            foreach (Baseline baseline in corridor.Baselines)
            {
                if (baseline == null) continue;
                BaselineFeatureLines main = baseline.MainBaselineFeatureLines;
                if (main == null) continue;
                foreach (FeatureLineCollection collection in main.FeatureLineCollectionMap)
                {
                    if (collection == null) continue;
                    foreach (CorridorFeatureLine line in collection)
                    {
                        if (line == null ||
                            !ContainsAny(line.CodeName, "KERB", "CURB"))
                            continue;

                        FeatureLinePoint previous = null;
                        foreach (FeatureLinePoint point in line.FeatureLinePoints)
                        {
                            if (point == null) continue;
                            if (previous != null)
                                total += previous.XYZ.DistanceTo(point.XYZ);
                            previous = point;
                        }
                    }
                }
            }
            return total / unitsPerMetre;
        }

        private static double ReadJunctionBellmouthLength(
            Database database,
            Transaction transaction,
            double unitsPerMetre)
        {
            if (database == null ||
                transaction == null)
                return 0.0;

            BlockTableRecord modelSpace = null;
            try
            {
                modelSpace =
                    transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(database),
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
            }
            catch { }
            if (modelSpace == null)
                return 0.0;

            double total = 0.0;
            var seen =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (ObjectId id in modelSpace)
            {
                Entity entity = null;
                try
                {
                    entity =
                        transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Entity;
                }
                catch { }

                if (!IsJunctionBellmouthCurve(entity))
                    continue;

                Point3d start;
                Point3d end;
                double length;
                if (!TryReadEntityCurveLength(
                        entity,
                        out start,
                        out end,
                        out length) ||
                    length <= 1e-9)
                    continue;

                string key =
                    BellmouthGeometryKey(
                        start,
                        end,
                        length);
                if (!seen.Add(key))
                    continue;

                total += length;
            }

            return total /
                   Math.Max(
                       unitsPerMetre,
                       1e-9);
        }

        private static bool IsJunctionBellmouthCurve(
            Entity entity)
        {
            if (entity == null)
                return false;

            string layer =
                entity.Layer ?? string.Empty;
            string name =
                ReadEntityName(entity);
            string identity =
                (layer + " " +
                 name + " " +
                 entity.GetType().Name)
                    .ToUpperInvariant();

            if (identity.Contains("CUT") ||
                identity.Contains("FILL") ||
                identity.Contains("SLOPE") ||
                identity.Contains("TOE") ||
                identity.Contains("DAYLIGHT") ||
                identity.Contains("GRADE"))
                return false;

            bool junctionIdentity =
                identity.Contains("BELLMOUTH") ||
                identity.Contains("ROAD-JUNCTION") ||
                identity.Contains("ROAD JUNCTION") ||
                string.Equals(
                    layer,
                    "CE-ROAD-JUNCTION",
                    StringComparison.OrdinalIgnoreCase);
            if (!junctionIdentity)
                return false;

            // T-junction closure lines share the junction layer but are not
            // bellmouth returns.
            if (entity is Line ||
                entity is Xline ||
                entity is Ray)
                return false;

            Arc arc = entity as Arc;
            if (arc != null)
                return true;

            Polyline polyline =
                entity as Polyline;
            if (polyline != null)
            {
                if (polyline.Closed)
                    return false;
                for (int index = 0;
                     index < polyline.NumberOfVertices - 1;
                     index++)
                {
                    try
                    {
                        if (Math.Abs(
                                polyline.GetBulgeAt(index)) >
                            1e-9)
                            return true;
                    }
                    catch { }
                }

                return identity.Contains(
                    "BELLMOUTH");
            }

            FeatureLine featureLine =
                entity as FeatureLine;
            if (featureLine != null)
            {
                try
                {
                    return !featureLine.Closed;
                }
                catch
                {
                    return true;
                }
            }

            return false;
        }

        private static string ReadEntityName(
            Entity entity)
        {
            if (entity == null)
                return string.Empty;
            try
            {
                System.Reflection.PropertyInfo property =
                    entity.GetType()
                        .GetProperty(
                            "Name",
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Instance);
                if (property == null ||
                    !property.CanRead)
                    return string.Empty;
                return Convert.ToString(
                           property.GetValue(
                               entity,
                               null),
                           CultureInfo.CurrentCulture) ??
                       string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryReadEntityCurveLength(
            Entity entity,
            out Point3d start,
            out Point3d end,
            out double length)
        {
            start = Point3d.Origin;
            end = Point3d.Origin;
            length = 0.0;
            if (entity == null)
                return false;

            Curve curve =
                entity as Curve;
            if (curve != null)
            {
                try
                {
                    start = curve.StartPoint;
                    end = curve.EndPoint;
                    length =
                        Math.Abs(
                            curve.GetDistanceAtParameter(
                                curve.EndParam) -
                            curve.GetDistanceAtParameter(
                                curve.StartParam));
                    if (length > 1e-9)
                        return true;
                }
                catch
                {
                    try
                    {
                        start = curve.StartPoint;
                        end = curve.EndPoint;
                        length =
                            start.DistanceTo(end);
                        return length > 1e-9;
                    }
                    catch { }
                }
            }

            FeatureLine featureLine =
                entity as FeatureLine;
            if (featureLine != null)
            {
                try
                {
                    Point3dCollection points =
                        featureLine.GetPoints(
                            FeatureLinePointType.AllPoints);
                    if (points == null ||
                        points.Count < 2)
                        return false;

                    start = points[0];
                    end =
                        points[
                            points.Count - 1];

                    try
                    {
                        System.Reflection.PropertyInfo property =
                            featureLine.GetType()
                                .GetProperty(
                                    "Length2D",
                                    System.Reflection.BindingFlags.Public |
                                    System.Reflection.BindingFlags.Instance);
                        if (property != null &&
                            property.CanRead)
                        {
                            length =
                                Convert.ToDouble(
                                    property.GetValue(
                                        featureLine,
                                        null),
                                    CultureInfo.InvariantCulture);
                        }
                    }
                    catch { }

                    if (length <= 1e-9)
                    {
                        for (int index = 1;
                             index < points.Count;
                             index++)
                        {
                            length +=
                                points[index - 1]
                                    .DistanceTo(
                                        points[index]);
                        }
                    }

                    return length > 1e-9;
                }
                catch { }
            }

            return false;
        }

        private static string BellmouthGeometryKey(
            Point3d first,
            Point3d second,
            double length)
        {
            string a =
                PointKey(first);
            string b =
                PointKey(second);
            if (string.Compare(
                    a,
                    b,
                    StringComparison.OrdinalIgnoreCase) > 0)
            {
                string swap = a;
                a = b;
                b = swap;
            }

            return a + "|" +
                   b + "|" +
                   Math.Round(
                       length,
                       3)
                       .ToString(
                           "0.000",
                           CultureInfo.InvariantCulture);
        }

        private static string PointKey(
            Point3d point)
        {
            return
                Math.Round(
                    point.X,
                    3)
                    .ToString(
                        "0.000",
                        CultureInfo.InvariantCulture) +
                "," +
                Math.Round(
                    point.Y,
                    3)
                    .ToString(
                        "0.000",
                        CultureInfo.InvariantCulture) +
                "," +
                Math.Round(
                    point.Z,
                    3)
                    .ToString(
                        "0.000",
                        CultureInfo.InvariantCulture);
        }

        private static string JoinCodes(CorridorCodeCollection codes)
        {
            if (codes == null) return string.Empty;
            var values = new List<string>();
            foreach (string code in codes)
            {
                if (!string.IsNullOrWhiteSpace(code)) values.Add(code.Trim());
            }
            return string.Join("|", values);
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            string text = value ?? string.Empty;
            foreach (string token in tokens)
            {
                if (text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static void AddValue(
            IDictionary<string, double> values,
            string key,
            double amount)
        {
            if (string.IsNullOrWhiteSpace(key) || Math.Abs(amount) <= 1e-12) return;
            double current;
            values.TryGetValue(key, out current);
            values[key] = current + amount;
        }

        private static IList<string> Row(
            string item,
            string unit,
            double quantity,
            string source)
        {
            return new List<string>
            {
                item,
                unit,
                quantity.ToString("N3", CultureInfo.CurrentCulture),
                source
            };
        }

        private static bool IsRoadCorridor(Corridor corridor)
        {
            if (corridor == null) return false;
            string name = corridor.Name ?? string.Empty;
            string description = corridor.Description ?? string.Empty;
            return name.IndexOf("CORRIDOR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.StartsWith("RD", StringComparison.OrdinalIgnoreCase) ||
                   description.IndexOf("CE road", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class SectionSnapshot
        {
            internal SectionSnapshot(double station)
            {
                Station = station;
                ShapeAreas = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }
            internal double Station { get; private set; }
            internal IDictionary<string, double> ShapeAreas { get; private set; }
            internal double RoadSurfaceWidth { get; set; }
            internal double SidewalkWidth { get; set; }
            internal double SideSlopeWidth { get; set; }
        }

        private sealed class QuantityAccumulator
        {
            internal QuantityAccumulator()
            {
                LayerVolumes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                RoadLengths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }
            internal double TotalRoadLength { get; set; }
            internal IDictionary<string, double> RoadLengths { get; private set; }
            internal double CutVolume { get; set; }
            internal double FillVolume { get; set; }
            internal double KerbLength { get; set; }
            internal double JunctionBellmouthLength { get; set; }
            internal double RoadSurfaceArea { get; set; }
            internal double SidewalkArea { get; set; }
            internal double SideSlopeArea { get; set; }
            internal IDictionary<string, double> LayerVolumes { get; private set; }
        }

        private enum LinkQuantityClass
        {
            None,
            RoadSurface,
            Sidewalk,
            SideSlope
        }
    }
}
