using System;
using System.Collections.Generic;
using System.Linq;

namespace CETools.Core
{
    public sealed class FloodScenarioResult
    {
        public FloodScenarioResult(int returnPeriodYears, double intensityMillimetresPerHour, double preDevelopmentFlow, double postDevelopmentFlow)
        {
            ReturnPeriodYears = returnPeriodYears;
            IntensityMillimetresPerHour = intensityMillimetresPerHour;
            PreDevelopmentFlowCubicMetresPerSecond = preDevelopmentFlow;
            PostDevelopmentFlowCubicMetresPerSecond = postDevelopmentFlow;
        }

        public int ReturnPeriodYears { get; }
        public double IntensityMillimetresPerHour { get; }
        public double PreDevelopmentFlowCubicMetresPerSecond { get; }
        public double PostDevelopmentFlowCubicMetresPerSecond { get; }
        public double IncreaseCubicMetresPerSecond => PostDevelopmentFlowCubicMetresPerSecond - PreDevelopmentFlowCubicMetresPerSecond;
        public double IncreasePercent => PreDevelopmentFlowCubicMetresPerSecond <= 0.0
            ? 0.0
            : IncreaseCubicMetresPerSecond / PreDevelopmentFlowCubicMetresPerSecond * 100.0;
    }

    public sealed class CulvertRecommendation
    {
        public CulvertRecommendation(double designFlow, double diameterMillimetres, double fullFlowCapacity, bool capacityAvailable)
        {
            DesignFlowCubicMetresPerSecond = designFlow;
            DiameterMillimetres = diameterMillimetres;
            FullFlowCapacityCubicMetresPerSecond = fullFlowCapacity;
            CapacityAvailable = capacityAvailable;
        }

        public double DesignFlowCubicMetresPerSecond { get; }
        public double DiameterMillimetres { get; }
        public double FullFlowCapacityCubicMetresPerSecond { get; }
        public bool CapacityAvailable { get; }
    }

    public static class FloodAnalysisCalculator
    {
        public static readonly int[] StandardReturnPeriods = { 2, 5, 10, 20, 25, 50, 100 };
        public static readonly double[] StandardCircularCulvertDiametersMillimetres =
            { 300, 375, 450, 525, 600, 750, 900, 1050, 1200, 1350, 1500, 1800, 2100, 2400 };

        public static double RationalPeakFlow(double runoffCoefficient, double intensityMillimetresPerHour, double catchmentAreaHectares)
        {
            RequireFinitePositive(catchmentAreaHectares, nameof(catchmentAreaHectares));
            RequireFinitePositive(intensityMillimetresPerHour, nameof(intensityMillimetresPerHour));
            if (double.IsNaN(runoffCoefficient) || double.IsInfinity(runoffCoefficient) ||
                runoffCoefficient <= 0.0 || runoffCoefficient > 1.0)
                throw new ArgumentOutOfRangeException(nameof(runoffCoefficient), "Runoff coefficient must be greater than zero and not exceed one.");

            return runoffCoefficient * intensityMillimetresPerHour * catchmentAreaHectares / 360.0;
        }

        public static IReadOnlyList<FloodScenarioResult> CompareScenarios(
            double catchmentAreaHectares,
            double preDevelopmentRunoffCoefficient,
            double postDevelopmentRunoffCoefficient,
            IReadOnlyDictionary<int, double> rainfallIntensitiesMillimetresPerHour)
        {
            if (rainfallIntensitiesMillimetresPerHour == null)
                throw new ArgumentNullException(nameof(rainfallIntensitiesMillimetresPerHour));

            var results = new List<FloodScenarioResult>();
            foreach (int period in StandardReturnPeriods)
            {
                if (!rainfallIntensitiesMillimetresPerHour.TryGetValue(period, out double intensity))
                    throw new ArgumentException("A rainfall intensity is required for every standard return period.", nameof(rainfallIntensitiesMillimetresPerHour));

                results.Add(new FloodScenarioResult(
                    period,
                    intensity,
                    RationalPeakFlow(preDevelopmentRunoffCoefficient, intensity, catchmentAreaHectares),
                    RationalPeakFlow(postDevelopmentRunoffCoefficient, intensity, catchmentAreaHectares)));
            }
            return results;
        }

        public static double CircularCulvertFullFlowCapacity(double diameterMillimetres, double slopeMetresPerMetre, double manningsN)
        {
            RequireFinitePositive(diameterMillimetres, nameof(diameterMillimetres));
            RequireFinitePositive(slopeMetresPerMetre, nameof(slopeMetresPerMetre));
            RequireFinitePositive(manningsN, nameof(manningsN));

            double diameter = diameterMillimetres / 1000.0;
            double area = Math.PI * diameter * diameter / 4.0;
            double hydraulicRadius = diameter / 4.0;
            return area * Math.Pow(hydraulicRadius, 2.0 / 3.0) * Math.Sqrt(slopeMetresPerMetre) / manningsN;
        }

        public static CulvertRecommendation RecommendCircularCulvert(double designFlowCubicMetresPerSecond, double slopeMetresPerMetre, double manningsN)
        {
            RequireFinitePositive(designFlowCubicMetresPerSecond, nameof(designFlowCubicMetresPerSecond));
            foreach (double diameter in StandardCircularCulvertDiametersMillimetres)
            {
                double capacity = CircularCulvertFullFlowCapacity(diameter, slopeMetresPerMetre, manningsN);
                if (capacity >= designFlowCubicMetresPerSecond)
                    return new CulvertRecommendation(designFlowCubicMetresPerSecond, diameter, capacity, true);
            }

            double largest = StandardCircularCulvertDiametersMillimetres.Last();
            return new CulvertRecommendation(
                designFlowCubicMetresPerSecond,
                largest,
                CircularCulvertFullFlowCapacity(largest, slopeMetresPerMetre, manningsN),
                false);
        }

        private static void RequireFinitePositive(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
                throw new ArgumentOutOfRangeException(name, "Value must be finite and greater than zero.");
        }
    }
}
