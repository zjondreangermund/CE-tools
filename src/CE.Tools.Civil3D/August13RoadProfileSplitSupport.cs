using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace CETools.Civil3D
{
    internal sealed class August13ProfileSection
    {
        internal August13ProfileSection(double start, double end) { Start = start; End = end; }
        internal double Start { get; private set; }
        internal double End { get; private set; }
    }

    internal static class August13RoadProfileSplitSupport
    {
        private const double Epsilon = 1e-9;

        internal static List<ObjectId> ReadSelectedProfileViews(Database database, IEnumerable<ObjectId> ids)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids.Distinct())
                {
                    try
                    {
                        if (transaction.GetObject(id, OpenMode.ForRead, false) is ProfileView) result.Add(id);
                    }
                    catch { }
                }
            }
            return result;
        }

        internal static List<August13ProfileSection> BuildSections(double start, double end, double interval)
        {
            var result = new List<August13ProfileSection>();
            double station = start;
            int guard = 0;
            while (station < end - Epsilon && guard < 10000)
            {
                double next = Math.Min(end, station + interval);
                if (next <= station + Epsilon) break;
                result.Add(new August13ProfileSection(station, next));
                station = next;
                guard++;
            }
            return result;
        }

        internal static void SetRange(ProfileView view, August13ProfileSection section)
        {
            view.StationRangeMode = StationRangeType.UserSpecified;
            view.StationStart = section.Start;
            view.StationEnd = section.End;
        }

        internal static void ResolveRoadStyles(Database database, CivilDocument civilDocument, Transaction transaction, out ObjectId viewStyleId, out ObjectId bandSetId)
        {
            RoadProductionSettings road = RoadProductionSettings.Read(database);
            IList<string> bandNames = CivilStyleCatalogV2.ReadNames(database, civilDocument, "Profile View Band Set Style");
            string requestedView = string.IsNullOrWhiteSpace(road.ProfileViewStyle) ? CivilStyleCatalogV2.DrawingDefault : road.ProfileViewStyle;
            string requestedBand = RoadProductionSettings.SelectPreferredBandSet(bandNames, road.ProfileViewBandSetStyle);
            string actualView;
            string actualBand;
            viewStyleId = CivilStyleCatalogV2.ResolveStyleId(database, civilDocument, "Profile View Style", requestedView, transaction, out actualView);
            bandSetId = CivilStyleCatalogV2.ResolveStyleId(database, civilDocument, "Profile View Band Set Style", requestedBand, transaction, out actualBand);
        }

        internal static void ApplyStyles(ProfileView view, ObjectId viewStyleId, ObjectId bandSetId)
        {
            try { ProfileStyleLinker.Apply(view, viewStyleId, bandSetId); }
            catch { }
        }

        internal static ObjectId CreateProfileView(string name, ObjectId alignmentId, Point3d point, ObjectId bandSetId, ObjectId viewStyleId)
        {
            MethodInfo method = typeof(RoadProductionCommentCommands).GetMethod(
                "CreateProfileViewByReflection",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null) throw new InvalidOperationException("CE Tools profile-view creation helper was not found.");
            try
            {
                object result = method.Invoke(null, new object[] { name, alignmentId, point, bandSetId, viewStyleId });
                if (result is ObjectId) return (ObjectId)result;
            }
            catch (TargetInvocationException exception)
            {
                throw exception.InnerException ?? exception;
            }
            throw new InvalidOperationException("CE Tools profile-view creation helper returned no ObjectId.");
        }

        internal static HashSet<string> ReadNames(Database database, Transaction transaction)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead, false) as BlockTableRecord;
            if (model == null) return names;
            foreach (ObjectId id in model)
            {
                ProfileView view;
                try { view = transaction.GetObject(id, OpenMode.ForRead, false) as ProfileView; }
                catch { continue; }
                if (view == null) continue;
                string name = ReadString(view, "Name");
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            return names;
        }

        internal static string UniqueName(string requested, ISet<string> reserved)
        {
            string root = string.IsNullOrWhiteSpace(requested) ? "Road Profile View" : requested.Trim();
            string candidate = root;
            int suffix = 2;
            while (reserved.Contains(candidate))
            {
                candidate = root + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")";
                suffix++;
            }
            reserved.Add(candidate);
            return candidate;
        }

        internal static Point3d ReadLocation(ProfileView view)
        {
            Point3d point;
            if (TryReadPoint(view, "Location", out point) || TryReadPoint(view, "InsertionPoint", out point)) return point;
            try { return view.GeometricExtents.MinPoint; }
            catch { return Point3d.Origin; }
        }

        internal static ObjectId ReadObjectId(object target, params string[] names)
        {
            if (target == null) return ObjectId.Null;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(name);
                    if (property == null) continue;
                    object value = property.GetValue(target, null);
                    if (value is ObjectId) return (ObjectId)value;
                }
                catch { }
            }
            return ObjectId.Null;
        }

        internal static double ReadDouble(object target, double fallback, params string[] names)
        {
            if (target == null) return fallback;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(name);
                    if (property == null) continue;
                    object value = property.GetValue(target, null);
                    if (value != null) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
                }
                catch { }
            }
            return fallback;
        }

        internal static string ReadString(object target, params string[] names)
        {
            if (target == null) return null;
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo property = target.GetType().GetProperty(name);
                    if (property == null) continue;
                    object value = property.GetValue(target, null);
                    if (value != null) return Convert.ToString(value, CultureInfo.CurrentCulture);
                }
                catch { }
            }
            return null;
        }

        private static bool TryReadPoint(object target, string name, out Point3d point)
        {
            point = Point3d.Origin;
            if (target == null) return false;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(name);
                if (property == null) return false;
                object value = property.GetValue(target, null);
                if (!(value is Point3d)) return false;
                point = (Point3d)value;
                return true;
            }
            catch { return false; }
        }
    }
}
