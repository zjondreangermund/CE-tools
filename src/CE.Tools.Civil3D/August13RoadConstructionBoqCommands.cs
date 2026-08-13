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
                    "Choose the existing-ground/base surface. CE Tools compares it with each road corridor CE-BOTTOM (Datum) surface for cut/fill to datum.",
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

            var rows = new List<IList<string>>
            {
                Row(
                    "Earthworks - Cut to corridor datum",
                    "m3",
                    totals.CutVolume,
                    "Existing ground vs CE-BOTTOM"),
                Row(
                    "Earthworks - Fill to corridor datum",
                    "m3",
                    totals.FillVolume,
                    "Existing ground vs CE-BOTTOM")
            };

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
                "Road corridors={0}. Re-run CE_ROADBOQCONSTRUCTION after corridor/surface edits to recalculate from the live model. {1}",
                corridorCount,
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
            CorridorSurface datum = FindCorridorSurface(corridor, "CE-BOTTOM");
            if (datum == null || datum.SurfaceId.IsNull)
            {
                warnings.Add(corridor.Name + ": CE-BOTTOM corridor surface is missing or is not built.");
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

        private static CorridorSurface FindCorridorSurface(Corridor corridor, string name)
        {
            if (corridor == null) return null;
            foreach (CorridorSurface surface in corridor.CorridorSurfaces)
            {
                if (surface != null &&
                    string.Equals(surface.Name, name, StringComparison.OrdinalIgnoreCase))
                    return surface;
            }
            return null;
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
            }
            internal double CutVolume { get; set; }
            internal double FillVolume { get; set; }
            internal double KerbLength { get; set; }
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
