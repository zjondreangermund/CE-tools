using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CETools.Core
{
    /// <summary>
    /// Host-independent preliminary pump/system-curve screening.
    /// All inputs must use SI units: flow in litres per second, head/length/
    /// diameter in metres, Hazen-Williams C dimensionless and minor-loss K
    /// dimensionless. Final pump selection requires manufacturer and engineer review.
    /// </summary>
    public static class PumpSystemCurve
    {
        private const double Gravity = 9.80665;

        public static double SystemHeadMetres(
            double flowLitresPerSecond,
            SystemCurveDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            definition.Validate();
            if (!IsFinite(flowLitresPerSecond) || flowLitresPerSecond < 0.0)
                throw new ArgumentOutOfRangeException(nameof(flowLitresPerSecond));

            double flowCubicMetresPerSecond = flowLitresPerSecond / 1000.0;
            if (flowCubicMetresPerSecond <= 0.0) return definition.StaticHeadMetres;

            double friction = definition.PipeLengthMetres <= 0.0
                ? 0.0
                : 10.67 * definition.PipeLengthMetres *
                  Math.Pow(flowCubicMetresPerSecond, 1.852) /
                  (Math.Pow(definition.HazenWilliamsC, 1.852) *
                   Math.Pow(definition.InternalDiameterMetres, 4.8704));

            double area = Math.PI * definition.InternalDiameterMetres *
                definition.InternalDiameterMetres / 4.0;
            double velocity = flowCubicMetresPerSecond / area;
            double minor = definition.MinorLossCoefficient <= 0.0
                ? 0.0
                : definition.MinorLossCoefficient * velocity * velocity /
                  (2.0 * Gravity);

            return definition.StaticHeadMetres + friction + minor;
        }

        public static IReadOnlyList<SystemCurvePoint> BuildSystemCurve(
            IEnumerable<double> flowLitresPerSecond,
            SystemCurveDefinition definition)
        {
            if (flowLitresPerSecond == null)
                throw new ArgumentNullException(nameof(flowLitresPerSecond));
            return flowLitresPerSecond
                .Select(flow => new SystemCurvePoint(
                    flow,
                    SystemHeadMetres(flow, definition)))
                .OrderBy(point => point.FlowLitresPerSecond)
                .ToList();
        }

        public static PumpDutyPoint FindDutyPoint(
            IEnumerable<PumpCurvePoint> pumpCurve,
            SystemCurveDefinition definition)
        {
            if (pumpCurve == null) throw new ArgumentNullException(nameof(pumpCurve));
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            definition.Validate();

            List<PumpCurvePoint> points = pumpCurve
                .OrderBy(point => point.FlowLitresPerSecond)
                .ToList();
            if (points.Count < 2)
                throw new ArgumentException("At least two pump-curve points are required.", nameof(pumpCurve));
            for (int index = 0; index < points.Count; index++)
                points[index].Validate(index);
            for (int index = 1; index < points.Count; index++)
            {
                if (points[index].FlowLitresPerSecond <= points[index - 1].FlowLitresPerSecond)
                    throw new ArgumentException("Pump-curve flow values must be strictly increasing.", nameof(pumpCurve));
            }

            for (int index = 0; index < points.Count - 1; index++)
            {
                PumpCurvePoint first = points[index];
                PumpCurvePoint second = points[index + 1];
                double firstSystem = SystemHeadMetres(first.FlowLitresPerSecond, definition);
                double secondSystem = SystemHeadMetres(second.FlowLitresPerSecond, definition);
                double firstDifference = first.HeadMetres - firstSystem;
                double secondDifference = second.HeadMetres - secondSystem;

                if (Math.Abs(firstDifference) <= 1e-10)
                    return CreateDutyPoint(first, firstSystem, 0.0);
                if (Math.Abs(secondDifference) <= 1e-10)
                    return CreateDutyPoint(second, secondSystem, 0.0);
                if ((firstDifference > 0.0 && secondDifference > 0.0) ||
                    (firstDifference < 0.0 && secondDifference < 0.0))
                    continue;

                double fraction = firstDifference /
                    (firstDifference - secondDifference);
                fraction = Math.Max(0.0, Math.Min(1.0, fraction));
                double flow = Interpolate(
                    first.FlowLitresPerSecond,
                    second.FlowLitresPerSecond,
                    fraction);
                double pumpHead = Interpolate(first.HeadMetres, second.HeadMetres, fraction);
                double systemHead = SystemHeadMetres(flow, definition);
                return new PumpDutyPoint(
                    flow,
                    pumpHead,
                    systemHead,
                    InterpolateOptional(first.EfficiencyPercent, second.EfficiencyPercent, fraction),
                    InterpolateOptional(first.PowerKilowatts, second.PowerKilowatts, fraction),
                    InterpolateOptional(first.NpshRequiredMetres, second.NpshRequiredMetres, fraction),
                    Math.Abs(pumpHead - systemHead));
            }

            return null;
        }

        public static PumpSuitabilityReview Review(
            IEnumerable<PumpCurvePoint> pumpCurve,
            SystemCurveDefinition definition,
            double? npshAvailableMetres,
            double? minimumNpshMarginMetres)
        {
            PumpDutyPoint duty = FindDutyPoint(pumpCurve, definition);
            if (duty == null)
            {
                return new PumpSuitabilityReview(
                    null,
                    "No duty-point intersection occurs inside the supplied pump curve.",
                    null,
                    false);
            }

            double? margin = null;
            bool npshPass = true;
            string message = "A duty-point intersection was found inside the supplied pump curve.";
            if (npshAvailableMetres.HasValue && duty.NpshRequiredMetres.HasValue)
            {
                margin = npshAvailableMetres.Value - duty.NpshRequiredMetres.Value;
                double requiredMargin = minimumNpshMarginMetres ?? 0.0;
                npshPass = margin.Value >= requiredMargin;
                message += npshPass
                    ? " The entered NPSH available meets the screening margin."
                    : " The entered NPSH available does not meet the screening margin.";
            }
            else if (npshAvailableMetres.HasValue || duty.NpshRequiredMetres.HasValue)
            {
                npshPass = false;
                message += " NPSH screening is incomplete because either NPSHa or NPSHr is missing.";
            }

            return new PumpSuitabilityReview(duty, message, margin, npshPass);
        }

        private static PumpDutyPoint CreateDutyPoint(
            PumpCurvePoint point,
            double systemHead,
            double residual)
        {
            return new PumpDutyPoint(
                point.FlowLitresPerSecond,
                point.HeadMetres,
                systemHead,
                point.EfficiencyPercent,
                point.PowerKilowatts,
                point.NpshRequiredMetres,
                residual);
        }

        private static double Interpolate(double first, double second, double fraction)
        {
            return first + (second - first) * fraction;
        }

        private static double? InterpolateOptional(
            double? first,
            double? second,
            double fraction)
        {
            if (!first.HasValue || !second.HasValue) return null;
            return Interpolate(first.Value, second.Value, fraction);
        }

        internal static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    public sealed class SystemCurveDefinition
    {
        public SystemCurveDefinition(
            double staticHeadMetres,
            double pipeLengthMetres,
            double internalDiameterMetres,
            double hazenWilliamsC,
            double minorLossCoefficient)
        {
            StaticHeadMetres = staticHeadMetres;
            PipeLengthMetres = pipeLengthMetres;
            InternalDiameterMetres = internalDiameterMetres;
            HazenWilliamsC = hazenWilliamsC;
            MinorLossCoefficient = minorLossCoefficient;
        }

        public double StaticHeadMetres { get; private set; }
        public double PipeLengthMetres { get; private set; }
        public double InternalDiameterMetres { get; private set; }
        public double HazenWilliamsC { get; private set; }
        public double MinorLossCoefficient { get; private set; }

        public void Validate()
        {
            if (!PumpSystemCurve.IsFinite(StaticHeadMetres) || StaticHeadMetres < 0.0)
                throw new ArgumentOutOfRangeException(nameof(StaticHeadMetres));
            if (!PumpSystemCurve.IsFinite(PipeLengthMetres) || PipeLengthMetres < 0.0)
                throw new ArgumentOutOfRangeException(nameof(PipeLengthMetres));
            if (!PumpSystemCurve.IsFinite(InternalDiameterMetres) || InternalDiameterMetres <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(InternalDiameterMetres));
            if (!PumpSystemCurve.IsFinite(HazenWilliamsC) || HazenWilliamsC <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(HazenWilliamsC));
            if (!PumpSystemCurve.IsFinite(MinorLossCoefficient) || MinorLossCoefficient < 0.0)
                throw new ArgumentOutOfRangeException(nameof(MinorLossCoefficient));
        }
    }

    public sealed class PumpCurvePoint
    {
        public PumpCurvePoint(
            double flowLitresPerSecond,
            double headMetres,
            double? efficiencyPercent,
            double? powerKilowatts,
            double? npshRequiredMetres)
        {
            FlowLitresPerSecond = flowLitresPerSecond;
            HeadMetres = headMetres;
            EfficiencyPercent = efficiencyPercent;
            PowerKilowatts = powerKilowatts;
            NpshRequiredMetres = npshRequiredMetres;
        }

        public double FlowLitresPerSecond { get; private set; }
        public double HeadMetres { get; private set; }
        public double? EfficiencyPercent { get; private set; }
        public double? PowerKilowatts { get; private set; }
        public double? NpshRequiredMetres { get; private set; }

        public void Validate(int index)
        {
            if (!PumpSystemCurve.IsFinite(FlowLitresPerSecond) || FlowLitresPerSecond < 0.0)
                throw new ArgumentOutOfRangeException("flowLitresPerSecond", "Invalid pump flow at row " + index.ToString(CultureInfo.InvariantCulture));
            if (!PumpSystemCurve.IsFinite(HeadMetres) || HeadMetres < 0.0)
                throw new ArgumentOutOfRangeException("headMetres", "Invalid pump head at row " + index.ToString(CultureInfo.InvariantCulture));
            ValidateOptional(EfficiencyPercent, 0.0, 100.0, "efficiencyPercent", index);
            ValidateOptional(PowerKilowatts, 0.0, double.MaxValue, "powerKilowatts", index);
            ValidateOptional(NpshRequiredMetres, 0.0, double.MaxValue, "npshRequiredMetres", index);
        }

        private static void ValidateOptional(
            double? value,
            double minimum,
            double maximum,
            string name,
            int index)
        {
            if (!value.HasValue) return;
            if (!PumpSystemCurve.IsFinite(value.Value) ||
                value.Value < minimum || value.Value > maximum)
                throw new ArgumentOutOfRangeException(
                    name,
                    "Invalid " + name + " at pump-curve row " + index.ToString(CultureInfo.InvariantCulture));
        }
    }

    public sealed class SystemCurvePoint
    {
        public SystemCurvePoint(double flowLitresPerSecond, double headMetres)
        {
            FlowLitresPerSecond = flowLitresPerSecond;
            HeadMetres = headMetres;
        }

        public double FlowLitresPerSecond { get; private set; }
        public double HeadMetres { get; private set; }
    }

    public sealed class PumpDutyPoint
    {
        public PumpDutyPoint(
            double flowLitresPerSecond,
            double pumpHeadMetres,
            double systemHeadMetres,
            double? efficiencyPercent,
            double? powerKilowatts,
            double? npshRequiredMetres,
            double headResidualMetres)
        {
            FlowLitresPerSecond = flowLitresPerSecond;
            PumpHeadMetres = pumpHeadMetres;
            SystemHeadMetres = systemHeadMetres;
            EfficiencyPercent = efficiencyPercent;
            PowerKilowatts = powerKilowatts;
            NpshRequiredMetres = npshRequiredMetres;
            HeadResidualMetres = headResidualMetres;
        }

        public double FlowLitresPerSecond { get; private set; }
        public double PumpHeadMetres { get; private set; }
        public double SystemHeadMetres { get; private set; }
        public double? EfficiencyPercent { get; private set; }
        public double? PowerKilowatts { get; private set; }
        public double? NpshRequiredMetres { get; private set; }
        public double HeadResidualMetres { get; private set; }
    }

    public sealed class PumpSuitabilityReview
    {
        public PumpSuitabilityReview(
            PumpDutyPoint dutyPoint,
            string message,
            double? npshMarginMetres,
            bool npshPass)
        {
            DutyPoint = dutyPoint;
            Message = message;
            NpshMarginMetres = npshMarginMetres;
            NpshPass = npshPass;
        }

        public PumpDutyPoint DutyPoint { get; private set; }
        public string Message { get; private set; }
        public double? NpshMarginMetres { get; private set; }
        public bool NpshPass { get; private set; }
    }
}