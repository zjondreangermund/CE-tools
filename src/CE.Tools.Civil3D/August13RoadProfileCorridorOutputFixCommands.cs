using System;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;

[assembly: CommandClass(typeof(CETools.Civil3D.August13RoadProfileCorridorOutputFixCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Final Civil 3D 2023 road-output pass used after CE road profile/corridor
    /// production. It deliberately uses the typed Civil 3D API for the items that
    /// must be visible in the native Profile/Corridor Properties dialogs.
    /// </summary>
    public sealed class August13RoadProfileCorridorOutputFixCommands
    {
        private const double PreferredVerticalCurveLength = 30.0;
        private const double MinimumVerticalCurveLength = 6.0;
        private const double CurveSpacingShare = 0.80;

        [CommandMethod("CE_TOOLS", "CE_ROADVERTICALCURVESFINAL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FinalizeRoadVerticalCurves()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            int profiles = 0;
            int curves = 0;
            int alreadyCurved = 0;
            int noGradeChange = 0;
            int failed = 0;
            var failureMessages = new List<string>();

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
                {
                    CivilAlignment alignment;
                    try { alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment; }
                    catch { continue; }
                    if (alignment == null || !IsRoadAlignment(alignment)) continue;

                    foreach (ObjectId profileId in alignment.GetProfileIds())
                    {
                        CivilProfile profile;
                        try { profile = transaction.GetObject(profileId, OpenMode.ForWrite, false) as CivilProfile; }
                        catch { continue; }
                        if (profile == null || !IsFinalProfile(profile)) continue;
                        profiles++;

                        List<PviSnapshot> pvis = SnapshotPvis(profile);
                        if (pvis.Count < 3) continue;

                        // Work from the end towards the start. Adding a parabola changes
                        // neighbouring profile entities, so reacquire the live PVI before
                        // every operation instead of keeping stale ProfilePVI wrappers.
                        for (int index = pvis.Count - 2; index >= 1; index--)
                        {
                            PviSnapshot previous = pvis[index - 1];
                            PviSnapshot current = pvis[index];
                            PviSnapshot next = pvis[index + 1];
                            double available = Math.Min(
                                current.Station - previous.Station,
                                next.Station - current.Station) * CurveSpacingShare;
                            double requested = Math.Min(PreferredVerticalCurveLength, available);
                            if (requested < MinimumVerticalCurveLength) continue;

                            ProfilePVI live;
                            try { live = profile.PVIs.GetPVIAt(current.Station, current.Elevation); }
                            catch { failed++; continue; }
                            if (live == null) { failed++; continue; }

                            try
                            {
                                if (live.VerticalCurve != null)
                                {
                                    alreadyCurved++;
                                    continue;
                                }
                            }
                            catch { }

                            double gradeIn;
                            double gradeOut;
                            try
                            {
                                gradeIn = live.GradeIn;
                                gradeOut = live.GradeOut;
                            }
                            catch
                            {
                                gradeIn = (current.Elevation - previous.Elevation) /
                                    Math.Max(current.Station - previous.Station, 0.001);
                                gradeOut = (next.Elevation - current.Elevation) /
                                    Math.Max(next.Station - current.Station, 0.001);
                            }

                            if (Math.Abs(gradeOut - gradeIn) < 1e-10)
                            {
                                noGradeChange++;
                                continue;
                            }

                            string error;
                            if (TryAddVerticalCurve(profile, live, requested, gradeIn, gradeOut, out error))
                            {
                                curves++;
                                continue;
                            }

                            failed++;
                            if (!string.IsNullOrWhiteSpace(error) && failureMessages.Count < 8)
                            {
                                failureMessages.Add(string.Format(
                                    CultureInfo.CurrentCulture,
                                    "{0} / {1} @ {2:N3}: {3}",
                                    alignment.Name,
                                    profile.Name,
                                    current.Station,
                                    error));
                            }
                        }
                    }
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_ROADVERTICALCURVESFINAL complete. Profiles={0}; curves added={1}; already curved={2}; no grade change={3}; failed={4}.",
                profiles,
                curves,
                alreadyCurved,
                noGradeChange,
                failed);
            foreach (string message in failureMessages)
                document.Editor.WriteMessage("\n  " + message);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADCORRIDOROUTPUTFIX", CommandFlags.Modal | CommandFlags.Redraw)]
        public void FinalizeRoadCorridorOutputs()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            int corridors = 0;
            int topSurfaces = 0;
            int bottomSurfaces = 0;
            int slopePatterns = 0;
            int warnings = 0;

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId slopePatternStyleId = ObjectId.Null;
                try
                {
                    if (civilDocument.Styles.SlopePatternStyles.Count > 0)
                        slopePatternStyleId = civilDocument.Styles.SlopePatternStyles[0];
                }
                catch { }

                foreach (ObjectId corridorId in civilDocument.CorridorCollection)
                {
                    Corridor corridor;
                    try { corridor = transaction.GetObject(corridorId, OpenMode.ForWrite, false) as Corridor; }
                    catch { warnings++; continue; }
                    if (corridor == null || !IsRoadCorridor(corridor)) continue;
                    corridors++;

                    try { corridor.Rebuild(); }
                    catch { warnings++; }

                    string roadSurfaceSuffix = ResolveRoadSurfaceSuffix(corridor, transaction);
                    if (string.IsNullOrWhiteSpace(roadSurfaceSuffix))
                    {
                        warnings++;
                        document.Editor.WriteMessage(
                            "\n{0}: road-numbered TOP/BOTTOM surfaces were skipped because the road name could not be resolved.",
                            corridor.Name);
                    }
                    else
                    {
                        string topSurfaceName = "TOP-" + roadSurfaceSuffix;
                        string bottomSurfaceName = "BOTTOM-" + roadSurfaceSuffix;
                        RemoveLegacyGenericSurfaces(corridor, topSurfaceName, bottomSurfaceName);

                        try
                        {
                            CorridorSurface top = EnsureSurface(corridor, topSurfaceName);
                            ConfigureSurface(
                                top,
                                "Top",
                                OverhangCorrectionType.TopLinks,
                                "CE road TOP surface | Links: Top | Overhang: Top Links");
                            topSurfaces++;
                        }
                        catch (System.Exception exception)
                        {
                            warnings++;
                            document.Editor.WriteMessage(
                                "\n{0}: {1} surface warning: {2}",
                                corridor.Name,
                                topSurfaceName,
                                exception.Message);
                        }

                        try
                        {
                            CorridorSurface bottom = EnsureSurface(corridor, bottomSurfaceName);
                            ConfigureSurface(
                                bottom,
                                "Datum",
                                OverhangCorrectionType.BottomLinks,
                                "CE road BOTTOM surface | Links: Datum | Overhang: Bottom Links");
                            bottomSurfaces++;
                        }
                        catch (System.Exception exception)
                        {
                            warnings++;
                            document.Editor.WriteMessage(
                                "\n{0}: {1} surface warning: {2}",
                                corridor.Name,
                                bottomSurfaceName,
                                exception.Message);
                        }
                    }

                    try
                    {
                        slopePatterns += EnsureSlopePatterns(corridor, slopePatternStyleId);
                    }
                    catch (System.Exception exception)
                    {
                        warnings++;
                        document.Editor.WriteMessage(
                            "\n{0}: slope-pattern warning: {1}",
                            corridor.Name,
                            exception.Message);
                    }

                    try
                    {
                        corridor.Rebuild();
                        corridor.RecordGraphicsModified(true);
                    }
                    catch { warnings++; }
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_ROADCORRIDOROUTPUTFIX complete. Corridors={0}; CE-TOP={1}; CE-BOTTOM={2}; slope patterns added={3}; warnings={4}.",
                corridors,
                topSurfaces,
                bottomSurfaces,
                slopePatterns,
                warnings);
        }

        private static List<PviSnapshot> SnapshotPvis(CivilProfile profile)
        {
            var result = new List<PviSnapshot>();
            foreach (ProfilePVI pvi in profile.PVIs)
            {
                try { result.Add(new PviSnapshot(pvi.Station, pvi.Elevation)); }
                catch { }
            }
            return result.OrderBy(item => item.Station).ToList();
        }

        private static bool TryAddVerticalCurve(
            CivilProfile profile,
            ProfilePVI pvi,
            double requestedLength,
            double gradeIn,
            double gradeOut,
            out string error)
        {
            error = string.Empty;
            var attempts = new List<double>
            {
                requestedLength,
                requestedLength * 0.75,
                requestedLength * 0.50,
                MinimumVerticalCurveLength
            }
            .Where(value => value >= MinimumVerticalCurveLength - 1e-9)
            .Distinct()
            .OrderByDescending(value => value)
            .ToList();

            foreach (double length in attempts)
            {
                try
                {
                    profile.Entities.AddFreeSymmetricParabolaByPVIAndCurveLength(pvi, length);
                    return true;
                }
                catch (System.Exception first)
                {
                    error = first.Message;
                }

                try
                {
                    int before = pvi.EntityBefore;
                    int after = pvi.EntityAfter;
                    if (before <= 0 || after <= 0) continue;
                    VerticalCurveType curveType = gradeIn > gradeOut
                        ? VerticalCurveType.Crest
                        : VerticalCurveType.Sag;
                    profile.Entities.AddFreeSymmetricParabolaByLength(
                        unchecked((uint)before),
                        unchecked((uint)after),
                        curveType,
                        length,
                        true);
                    return true;
                }
                catch (System.Exception second)
                {
                    error = second.Message;
                }
            }
            return false;
        }

        private static string ResolveRoadSurfaceSuffix(
            Corridor corridor,
            Transaction transaction)
        {
            if (corridor == null || transaction == null) return null;
            foreach (Baseline baseline in corridor.Baselines)
            {
                ObjectId alignmentId = ReadObjectId(baseline, "AlignmentId");
                if (alignmentId.IsNull)
                    alignmentId = ReadObjectId(baseline, "AlignmentObjectId");
                if (alignmentId.IsNull) continue;
                CivilAlignment alignment = null;
                try { alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment; }
                catch { }
                if (alignment == null || !IsRoadAlignment(alignment)) continue;
                string suffix = NormalizeRoadSurfaceSuffix(alignment.Name);
                if (!string.IsNullOrWhiteSpace(suffix)) return suffix;
            }
            return NormalizeRoadSurfaceSuffix(corridor.Name);
        }

        private static string NormalizeRoadSurfaceSuffix(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            int index = value.IndexOf("RD-", StringComparison.OrdinalIgnoreCase);
            if (index < 0) return null;
            int start = index + 3;
            int end = start;
            while (end < value.Length && char.IsDigit(value[end])) end++;
            int number;
            if (end <= start ||
                !int.TryParse(value.Substring(start, end - start),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out number))
                return null;
            return "RD-" + number.ToString("00", CultureInfo.InvariantCulture);
        }

        private static void RemoveLegacyGenericSurfaces(
            Corridor corridor,
            string topSurfaceName,
            string bottomSurfaceName)
        {
            if (corridor == null) return;
            object collection = corridor.CorridorSurfaces;
            var surfaces = EnumerateObjects(collection).ToList();
            foreach (object surface in surfaces)
            {
                string name = Convert.ToString(
                    ReadProperty(surface, "Name"),
                    CultureInfo.CurrentCulture);
                if (!IsLegacyGenericSurface(name) ||
                    string.Equals(name, topSurfaceName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, bottomSurfaceName, StringComparison.OrdinalIgnoreCase))
                    continue;
                TryInvoke(collection, "Remove", surface);
            }

            foreach (object surface in EnumerateObjects(collection).ToList())
            {
                object boundaries = ReadProperty(surface, "Boundaries");
                var items = EnumerateObjects(boundaries).ToList();
                for (int index = items.Count - 1; index >= 0; index--)
                {
                    if (IsCorridorBoundary(items[index])) continue;
                    if (!TryInvoke(boundaries, "Remove", items[index]))
                        TryInvoke(boundaries, "RemoveAt", index);
                }
            }
        }

        private static bool IsLegacyGenericSurface(string name)
        {
            string value = (name ?? string.Empty).Trim();
            return string.Equals(value, "CE-TOP", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "CE-BOTTOM", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("CE-TOP (", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("CE-BOTTOM (", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCorridorBoundary(object boundary)
        {
            string identity = (
                boundary.GetType().Name + " " +
                Convert.ToString(ReadProperty(boundary, "Name"), CultureInfo.CurrentCulture) + " " +
                Convert.ToString(ReadProperty(boundary, "Description"), CultureInfo.CurrentCulture))
                .ToUpperInvariant();
            return identity.Contains("CORRIDOR") ||
                   identity.Contains("EXTENTS") ||
                   identity.Contains("OUTER");
        }

        private static IEnumerable<object> EnumerateObjects(object value)
        {
            IEnumerable enumerable = value as IEnumerable;
            if (enumerable == null || value is string) yield break;
            foreach (object item in enumerable) yield return item;
        }

        private static object ReadProperty(object target, string name)
        {
            if (target == null) return null;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);
                return property == null ? null : property.GetValue(target, null);
            }
            catch { return null; }
        }

        private static ObjectId ReadObjectId(object target, string name)
        {
            object value = ReadProperty(target, name);
            return value is ObjectId ? (ObjectId)value : ObjectId.Null;
        }

        private static bool TryInvoke(object target, string name, params object[] supplied)
        {
            if (target == null) return false;
            foreach (MethodInfo method in target.GetType().GetMethods(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != supplied.Length) continue;
                bool valid = true;
                for (int index = 0; index < parameters.Length; index++)
                {
                    if (supplied[index] == null) continue;
                    if (!parameters[index].ParameterType.IsInstanceOfType(supplied[index]))
                    {
                        valid = false;
                        break;
                    }
                }
                if (!valid) continue;
                try
                {
                    method.Invoke(target, supplied);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static CorridorSurface EnsureSurface(Corridor corridor, string name)
        {
            foreach (CorridorSurface existing in corridor.CorridorSurfaces)
            {
                if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
                    return existing;
            }
            return corridor.CorridorSurfaces.Add(name);
        }

        private static void ConfigureSurface(
            CorridorSurface surface,
            string linkCode,
            OverhangCorrectionType correction,
            string description)
        {
            if (surface == null) throw new InvalidOperationException("Corridor surface is unavailable.");

            surface.AddLinkCode(linkCode, true);
            try { surface.SetLinkCodeAsBreakLine(linkCode, true); }
            catch { }
            surface.OverhangCorrection = correction;
            surface.IsBuild = true;
            surface.Description = description;

            object boundaries = ReadProperty(surface, "Boundaries");
            var boundaryItems = EnumerateObjects(boundaries).ToList();
            for (int index = boundaryItems.Count - 1; index >= 0; index--)
            {
                if (IsCorridorBoundary(boundaryItems[index])) continue;
                if (!TryInvoke(boundaries, "Remove", boundaryItems[index]))
                    TryInvoke(boundaries, "RemoveAt", index);
            }
            if (boundaries != null &&
                !EnumerateObjects(boundaries).Any(IsCorridorBoundary))
            {
                TryInvoke(
                    boundaries,
                    "AddCorridorExtentsBoundary",
                    surface.Name + "-OUTER");
            }
        }

        private static int EnsureSlopePatterns(Corridor corridor, ObjectId styleId)
        {
            if (corridor == null || styleId.IsNull) return 0;
            CorridorSlopePatternCollection patterns = corridor.SlopePatterns;
            if (patterns == null) return 0;

            // Civil 3D retains old slope-pattern rows when a corridor is rebuilt.
            // Remove every existing row first, then recreate only the two requested
            // engineering pairs on each side: EPS -> Daylight_Cut and
            // EPS -> Daylight_Fill.
            List<object> existing = EnumerateObjects(patterns).ToList();
            for (int index = existing.Count - 1; index >= 0; index--)
            {
                if (!TryInvoke(patterns, "Remove", existing[index]))
                    TryInvoke(patterns, "RemoveAt", index);
            }

            int added = 0;
            foreach (Baseline baseline in corridor.Baselines)
            {
                if (baseline == null) continue;
                List<CorridorFeatureLine> lines = ReadFeatureLines(baseline);
                List<CorridorFeatureLine> eps = lines
                    .Where(item => IsSlopeCode(item.CodeName, "EPS"))
                    .ToList();
                List<CorridorFeatureLine> cut = lines
                    .Where(item => IsSlopeCode(item.CodeName, "DAYLIGHT_CUT"))
                    .ToList();
                List<CorridorFeatureLine> fill = lines
                    .Where(item => IsSlopeCode(item.CodeName, "DAYLIGHT_FILL"))
                    .ToList();

                foreach (CorridorFeatureLine hinge in eps)
                {
                    double hingeOffset = AverageOffset(hinge);
                    CorridorFeatureLine cutLine = FindSameSideSlopeLine(cut, hingeOffset);
                    CorridorFeatureLine fillLine = FindSameSideSlopeLine(fill, hingeOffset);
                    foreach (CorridorFeatureLine outer in new[] { cutLine, fillLine })
                    {
                        if (outer == null) continue;
                        try
                        {
                            CorridorSlopePattern pattern = patterns.Add(hinge, outer, styleId);
                            if (pattern == null) continue;
                            pattern.StartStation = baseline.StartStation;
                            pattern.EndStation = baseline.EndStation;
                            added++;
                        }
                        catch { }
                    }
                }
            }
            return added;
        }

        private static CorridorFeatureLine FindSameSideSlopeLine(
            IEnumerable<CorridorFeatureLine> candidates,
            double hingeOffset)
        {
            if (candidates == null) return null;
            List<CorridorFeatureLine> values = candidates
                .Where(item => item != null)
                .Where(item => Math.Abs(hingeOffset) < 0.001 ||
                    Math.Abs(AverageOffset(item)) < 0.001 ||
                    Math.Sign(AverageOffset(item)) == Math.Sign(hingeOffset))
                .ToList();
            if (values.Count == 0) return null;
            return values
                .OrderBy(item => Math.Abs(AverageOffset(item) - hingeOffset))
                .FirstOrDefault();
        }

        private static bool IsSlopeCode(string value, string expected)
        {
            string normal = (value ?? string.Empty)
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .ToUpperInvariant();
            string target = (expected ?? string.Empty)
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty)
                .ToUpperInvariant();
            return string.Equals(normal, target, StringComparison.OrdinalIgnoreCase);
        }

        private static List<CorridorFeatureLine> ReadFeatureLines(Baseline baseline)
        {
            var result = new List<CorridorFeatureLine>();
            BaselineFeatureLines main = baseline.MainBaselineFeatureLines;
            if (main != null)
            {
                foreach (FeatureLineCollection collection in main.FeatureLineCollectionMap)
                {
                    foreach (CorridorFeatureLine line in collection)
                    {
                        if (line != null) result.Add(line);
                    }
                }
            }
            return result;
        }

        private static bool HasSlopePattern(
            CorridorSlopePatternCollection patterns,
            CorridorFeatureLine first,
            CorridorFeatureLine second)
        {
            foreach (CorridorSlopePattern pattern in patterns)
            {
                if (pattern == null) continue;
                try
                {
                    string p1 = pattern.FeatureLine1 == null ? string.Empty : pattern.FeatureLine1.CodeName;
                    string p2 = pattern.FeatureLine2 == null ? string.Empty : pattern.FeatureLine2.CodeName;
                    if ((string.Equals(p1, first.CodeName, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(p2, second.CodeName, StringComparison.OrdinalIgnoreCase)) ||
                        (string.Equals(p1, second.CodeName, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(p2, first.CodeName, StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
                catch { }
            }
            return false;
        }

        private static double AverageOffset(CorridorFeatureLine line)
        {
            if (line == null) return 0.0;
            double total = 0.0;
            int count = 0;
            foreach (FeatureLinePoint point in line.FeatureLinePoints)
            {
                total += point.Offset;
                count++;
            }
            return count == 0 ? 0.0 : total / count;
        }

        private static bool SameSide(double first, double second)
        {
            if (Math.Abs(first) < 1e-9 || Math.Abs(second) < 1e-9) return false;
            return Math.Sign(first) == Math.Sign(second);
        }

        private static bool IsOuterSlopeCode(string code)
        {
            string value = code ?? string.Empty;
            return value.IndexOf("DAYLIGHT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("DITCH_OUT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("DAYLIGHT_MAX", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsInnerSlopeCode(string code)
        {
            string value = code ?? string.Empty;
            return value.IndexOf("HINGE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("ETW", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("EDGE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("SHOULDER", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsRoadAlignment(CivilAlignment alignment)
        {
            string name = alignment.Name ?? string.Empty;
            string description = alignment.Description ?? string.Empty;
            return name.StartsWith("RD", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("ROAD", StringComparison.OrdinalIgnoreCase) ||
                   description.IndexOf("CE road", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsFinalProfile(CivilProfile profile)
        {
            string name = profile.Name ?? string.Empty;
            string description = profile.Description ?? string.Empty;
            return name.IndexOf("-FG", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("FINAL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   description.IndexOf("CE final road profile", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsRoadCorridor(Corridor corridor)
        {
            string name = corridor.Name ?? string.Empty;
            string description = corridor.Description ?? string.Empty;
            return name.IndexOf("CORRIDOR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.StartsWith("RD", StringComparison.OrdinalIgnoreCase) ||
                   description.IndexOf("CE road", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class PviSnapshot
        {
            internal PviSnapshot(double station, double elevation)
            {
                Station = station;
                Elevation = elevation;
            }
            internal double Station { get; private set; }
            internal double Elevation { get; private set; }
        }
    }
}
