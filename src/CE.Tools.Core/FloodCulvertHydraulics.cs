using System;
using System.Collections.Generic;
using System.Linq;

namespace CETools.Core
{
    public enum CulvertShape
    {
        Pipe,
        Box
    }

    public sealed class CulvertSection
    {
        public CulvertSection(
            CulvertShape shape,
            double widthMetres,
            double heightMetres,
            int barrels,
            double manningN,
            double slopeDecimal)
        {
            if (widthMetres <= 0.0) throw new ArgumentOutOfRangeException(nameof(widthMetres));
            if (heightMetres <= 0.0) throw new ArgumentOutOfRangeException(nameof(heightMetres));
            if (barrels <= 0) throw new ArgumentOutOfRangeException(nameof(barrels));
            if (manningN <= 0.0) throw new ArgumentOutOfRangeException(nameof(manningN));
            if (slopeDecimal <= 0.0) throw new ArgumentOutOfRangeException(nameof(slopeDecimal));
            Shape = shape;
            WidthMetres = widthMetres;
            HeightMetres = heightMetres;
            Barrels = barrels;
            ManningN = manningN;
            SlopeDecimal = slopeDecimal;
        }

        public CulvertShape Shape { get; private set; }
        public double WidthMetres { get; private set; }
        public double HeightMetres { get; private set; }
        public int Barrels { get; private set; }
        public double ManningN { get; private set; }
        public double SlopeDecimal { get; private set; }

        public string Description
        {
            get
            {
                if (Shape == CulvertShape.Pipe)
                    return Barrels + " x " + Math.Round(WidthMetres * 1000.0) + " mm pipe";
                return Barrels + " x " + Math.Round(WidthMetres * 1000.0) + " x " +
                    Math.Round(HeightMetres * 1000.0) + " mm box";
            }
        }
    }

    public sealed class CulvertHydraulicResult
    {
        public CulvertHydraulicResult(
            CulvertSection section,
            double designFlow,
            double capacity,
            double normalDepth,
            double velocity)
        {
            Section = section;
            DesignFlowCubicMetresPerSecond = designFlow;
            CapacityCubicMetresPerSecond = capacity;
            NormalDepthMetres = normalDepth;
            VelocityMetresPerSecond = velocity;
        }

        public CulvertSection Section { get; private set; }
        public double DesignFlowCubicMetresPerSecond { get; private set; }
        public double CapacityCubicMetresPerSecond { get; private set; }
        public double NormalDepthMetres { get; private set; }
        public double VelocityMetresPerSecond { get; private set; }
        public bool Adequate { get { return CapacityCubicMetresPerSecond + 1e-9 >= DesignFlowCubicMetresPerSecond; } }
        public double Utilisation
        {
            get
            {
                return CapacityCubicMetresPerSecond <= 0.0
                    ? double.PositiveInfinity
                    : DesignFlowCubicMetresPerSecond / CapacityCubicMetresPerSecond;
            }
        }
    }

    public static class FloodCulvertHydraulics
    {
        public static readonly int[] StandardReturnPeriods = { 2, 5, 10, 20, 25, 50, 100 };

        private static readonly double[] StandardPipeDiametersMetres =
        {
            0.450, 0.600, 0.750, 0.900, 1.050, 1.200,
            1.350, 1.500, 1.800, 2.100, 2.400, 2.700, 3.000
        };

        private static readonly double[,] StandardBoxSizesMetres =
        {
            { 0.900, 0.900 },
            { 1.200, 0.900 },
            { 1.200, 1.200 },
            { 1.500, 1.200 },
            { 1.500, 1.500 },
            { 1.800, 1.200 },
            { 1.800, 1.500 },
            { 1.800, 1.800 },
            { 2.100, 1.500 },
            { 2.400, 1.200 },
            { 2.400, 1.500 },
            { 2.400, 1.800 },
            { 3.000, 1.800 },
            { 3.000, 2.100 },
            { 3.600, 2.100 },
            { 3.600, 2.400 }
        };

        /// <summary>Rational method with A in km2 and i in mm/h: Q = C i A / 3.6.</summary>
        public static double RationalPeakFlow(
            double catchmentAreaSquareKilometres,
            double runoffCoefficient,
            double intensityMillimetresPerHour)
        {
            if (catchmentAreaSquareKilometres <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(catchmentAreaSquareKilometres));
            if (runoffCoefficient <= 0.0 || runoffCoefficient > 1.0)
                throw new ArgumentOutOfRangeException(nameof(runoffCoefficient));
            if (intensityMillimetresPerHour <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(intensityMillimetresPerHour));
            return runoffCoefficient * intensityMillimetresPerHour *
                catchmentAreaSquareKilometres / 3.6;
        }

        public static IDictionary<int, double> RationalReturnPeriodFlows(
            double catchmentAreaSquareKilometres,
            double runoffCoefficient,
            IDictionary<int, double> intensities)
        {
            if (intensities == null) throw new ArgumentNullException(nameof(intensities));
            var result = new Dictionary<int, double>();
            foreach (int period in StandardReturnPeriods)
            {
                double intensity;
                if (!intensities.TryGetValue(period, out intensity))
                    throw new ArgumentException("Missing rainfall intensity for Q" + period + ".", nameof(intensities));
                result[period] = RationalPeakFlow(
                    catchmentAreaSquareKilometres,
                    runoffCoefficient,
                    intensity);
            }
            return result;
        }

        public static double FullFlowCapacity(CulvertSection section)
        {
            if (section == null) throw new ArgumentNullException(nameof(section));
            double area;
            double wettedPerimeter;
            if (section.Shape == CulvertShape.Pipe)
            {
                double diameter = section.WidthMetres;
                area = Math.PI * diameter * diameter / 4.0;
                wettedPerimeter = Math.PI * diameter;
            }
            else
            {
                area = section.WidthMetres * section.HeightMetres;
                wettedPerimeter = 2.0 * (section.WidthMetres + section.HeightMetres);
            }
            double hydraulicRadius = area / wettedPerimeter;
            double oneBarrel = Manning(
                area,
                hydraulicRadius,
                section.ManningN,
                section.SlopeDecimal);
            return oneBarrel * section.Barrels;
        }

        public static CulvertHydraulicResult Review(
            CulvertSection section,
            double designFlowCubicMetresPerSecond)
        {
            if (designFlowCubicMetresPerSecond <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(designFlowCubicMetresPerSecond));
            double capacity = FullFlowCapacity(section);
            double depth = NormalDepth(section, designFlowCubicMetresPerSecond);
            double flowPerBarrel = designFlowCubicMetresPerSecond / section.Barrels;
            double area = FlowArea(section, depth);
            double velocity = area <= 1e-12 ? 0.0 : flowPerBarrel / area;
            return new CulvertHydraulicResult(section, designFlowCubicMetresPerSecond, capacity, depth, velocity);
        }

        public static CulvertHydraulicResult Recommend(
            double designFlowCubicMetresPerSecond,
            CulvertShape preferredShape,
            double manningN,
            double slopeDecimal,
            int maximumBarrels = 4,
            double capacityFactor = 1.0)
        {
            if (designFlowCubicMetresPerSecond <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(designFlowCubicMetresPerSecond));
            if (maximumBarrels <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBarrels));
            if (capacityFactor < 1.0) throw new ArgumentOutOfRangeException(nameof(capacityFactor));
            double required = designFlowCubicMetresPerSecond * capacityFactor;
            foreach (CulvertSection section in EnumerateStandards(
                preferredShape,
                manningN,
                slopeDecimal,
                maximumBarrels))
            {
                if (FullFlowCapacity(section) + 1e-9 < required) continue;
                return Review(section, designFlowCubicMetresPerSecond);
            }
            return null;
        }

        public static double NormalDepth(
            CulvertSection section,
            double totalFlowCubicMetresPerSecond)
        {
            if (section == null) throw new ArgumentNullException(nameof(section));
            if (totalFlowCubicMetresPerSecond <= 0.0) return 0.0;
            double target = totalFlowCubicMetresPerSecond / section.Barrels;
            double maximumDepth = section.HeightMetres;
            double fullOneBarrel = FullFlowCapacity(new CulvertSection(
                section.Shape,
                section.WidthMetres,
                section.HeightMetres,
                1,
                section.ManningN,
                section.SlopeDecimal));
            if (target >= fullOneBarrel) return maximumDepth;

            double low = 1e-6 * maximumDepth;
            double high = maximumDepth;
            for (int iteration = 0; iteration < 80; iteration++)
            {
                double mid = (low + high) * 0.5;
                double q = PartialFlow(section, mid);
                if (q < target) low = mid;
                else high = mid;
            }
            return (low + high) * 0.5;
        }

        private static IEnumerable<CulvertSection> EnumerateStandards(
            CulvertShape shape,
            double manningN,
            double slopeDecimal,
            int maximumBarrels)
        {
            for (int barrels = 1; barrels <= maximumBarrels; barrels++)
            {
                if (shape == CulvertShape.Pipe)
                {
                    foreach (double diameter in StandardPipeDiametersMetres)
                        yield return new CulvertSection(shape, diameter, diameter, barrels, manningN, slopeDecimal);
                }
                else
                {
                    for (int index = 0; index < StandardBoxSizesMetres.GetLength(0); index++)
                        yield return new CulvertSection(
                            shape,
                            StandardBoxSizesMetres[index, 0],
                            StandardBoxSizesMetres[index, 1],
                            barrels,
                            manningN,
                            slopeDecimal);
                }
            }
        }

        private static double PartialFlow(CulvertSection section, double depth)
        {
            double area = FlowArea(section, depth);
            double perimeter = WettedPerimeter(section, depth);
            if (area <= 0.0 || perimeter <= 0.0) return 0.0;
            return Manning(
                area,
                area / perimeter,
                section.ManningN,
                section.SlopeDecimal);
        }

        private static double FlowArea(CulvertSection section, double depth)
        {
            depth = Math.Max(0.0, Math.Min(depth, section.HeightMetres));
            if (section.Shape == CulvertShape.Box)
                return section.WidthMetres * depth;

            double diameter = section.WidthMetres;
            double radius = diameter * 0.5;
            if (depth <= 0.0) return 0.0;
            if (depth >= diameter) return Math.PI * radius * radius;
            double theta = 2.0 * Math.Acos((radius - depth) / radius);
            return radius * radius * 0.5 * (theta - Math.Sin(theta));
        }

        private static double WettedPerimeter(CulvertSection section, double depth)
        {
            depth = Math.Max(0.0, Math.Min(depth, section.HeightMetres));
            if (section.Shape == CulvertShape.Box)
            {
                if (depth >= section.HeightMetres - 1e-10)
                    return 2.0 * (section.WidthMetres + section.HeightMetres);
                return section.WidthMetres + 2.0 * depth;
            }

            double diameter = section.WidthMetres;
            double radius = diameter * 0.5;
            if (depth <= 0.0) return 0.0;
            if (depth >= diameter) return Math.PI * diameter;
            double theta = 2.0 * Math.Acos((radius - depth) / radius);
            return radius * theta;
        }

        private static double Manning(
            double area,
            double hydraulicRadius,
            double manningN,
            double slopeDecimal)
        {
            return (1.0 / manningN) * area *
                Math.Pow(hydraulicRadius, 2.0 / 3.0) *
                Math.Sqrt(slopeDecimal);
        }
    }
}
