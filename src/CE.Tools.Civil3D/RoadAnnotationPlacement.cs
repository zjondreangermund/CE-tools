using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CETools.Core;

namespace CETools.Civil3D
{
    internal static class RoadAnnotationPlacement
    {
        internal static IList<double> Stations(Curve road, IEnumerable<Curve> allRoads, bool sections)
        {
            double length = road.GetDistanceAtParameter(road.EndParam);
            if (!sections) return new[] { length * 0.5 };
            var breaks = new List<double>();
            foreach (Curve other in allRoads)
            {
                if (other.ObjectId == road.ObjectId) continue;
                var hits = new Point3dCollection();
                try
                {
                    road.IntersectWith(other, Intersect.OnBothOperands, hits, IntPtr.Zero, IntPtr.Zero);
                    foreach (Point3d hit in hits)
                    {
                        double a = road.GetDistAtPoint(road.GetClosestPointTo(hit, false));
                        double b = other.GetDistAtPoint(other.GetClosestPointTo(hit, false));
                        double otherLength = other.GetDistanceAtParameter(other.EndParam);
                        // Two meeting endpoints are a bend/continuation, not a cross/T-junction.
                        bool aEnd = a < 0.01 || length - a < 0.01;
                        bool bEnd = b < 0.01 || otherLength - b < 0.01;
                        if (!(aEnd && bEnd)) breaks.Add(a);
                    }
                }
                catch { }
            }
            return RoadAnnotationPlan.SectionMidpoints(length, breaks);
        }

        internal static Vector3d Tangent(Curve curve, double station)
        {
            double length = curve.GetDistanceAtParameter(curve.EndParam);
            double delta = Math.Min(0.1, length * 0.01);
            Vector3d tangent = curve.GetPointAtDist(Math.Min(length, station + delta)) -
                curve.GetPointAtDist(Math.Max(0.0, station - delta));
            double angle = RoadAnnotationPlan.ReadableAngle(Math.Atan2(tangent.Y, tangent.X));
            return new Vector3d(Math.Cos(angle), Math.Sin(angle), 0);
        }

        internal static Vector3d Above(Vector3d tangent)
        {
            var normal = new Vector3d(-tangent.Y, tangent.X, 0);
            return normal.Y < -1e-8 || (Math.Abs(normal.Y) < 1e-8 && normal.X > 0) ? -normal : normal;
        }

        internal static ObjectId Layer(Database database, Transaction transaction, string name, string fallback)
        {
            string value = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
            foreach (char invalid in "<>/\\\":;?*|=,") value = value.Replace(invalid, '-');
            LayerTable layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (layers.Has(value)) return layers[value];
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = value };
            ObjectId id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return id;
        }

        // Keep a shared inventory of text and dimension extents. Candidates are checked
        // before append, and successful output is immediately reserved for later roads.
        internal sealed class Occupancy
        {
            private readonly List<Extents3d> boxes = new List<Extents3d>();

            internal Occupancy(BlockTableRecord space, Transaction transaction, ISet<ObjectId> exclude = null)
            {
                foreach (ObjectId id in space)
                {
                    if (id.IsErased) continue;
                    if (exclude != null && exclude.Contains(id)) continue;
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || entity.IsErased ||
                        !(entity is MText || entity is DBText || entity is Dimension || entity is MLeader)) continue;
                    try { boxes.Add(entity.GeometricExtents); } catch { }
                }
            }

            internal static Extents3d TextBox(Point3d point, double width, double height, double angle)
            {
                double x = (Math.Abs(Math.Cos(angle)) * width + Math.Abs(Math.Sin(angle)) * height) * 0.5;
                double y = (Math.Abs(Math.Sin(angle)) * width + Math.Abs(Math.Cos(angle)) * height) * 0.5;
                return new Extents3d(new Point3d(point.X - x, point.Y - y, 0), new Point3d(point.X + x, point.Y + y, 0));
            }

            internal bool Free(Extents3d candidate, double gap)
            {
                return !boxes.Any(b => candidate.MinPoint.X - gap < b.MaxPoint.X && candidate.MaxPoint.X + gap > b.MinPoint.X &&
                    candidate.MinPoint.Y - gap < b.MaxPoint.Y && candidate.MaxPoint.Y + gap > b.MinPoint.Y);
            }

            internal void Reserve(Extents3d box) { boxes.Add(box); }

            internal bool PlaceText(MText text, Point3d anchor, Vector3d direction, double offset, bool avoid)
            {
                double width = Math.Max(text.TextHeight, text.Contents.Length * text.TextHeight * 0.8);
                try { width = Math.Max(width, text.ActualWidth); } catch { }
                for (int attempt = 0; attempt < (avoid ? 40 : 1); attempt++)
                {
                    Point3d location = anchor + direction * (offset + attempt * text.TextHeight * 1.6);
                    Extents3d bounds = TextBox(location, width, text.TextHeight * 1.5, text.Rotation);
                    if (avoid && !Free(bounds, text.TextHeight * 0.5)) continue;
                    text.Location = location;
                    Reserve(bounds);
                    return true;
                }
                return false;
            }
        }
    }
}
