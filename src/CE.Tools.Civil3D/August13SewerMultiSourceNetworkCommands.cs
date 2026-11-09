using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using ConnectorPositionType = Autodesk.Civil.DatabaseServices.ConnectorPositionType;
using DomainType = Autodesk.Civil.DatabaseServices.DomainType;
using PartFamily = Autodesk.Civil.DatabaseServices.Styles.PartFamily;
using PartsList = Autodesk.Civil.DatabaseServices.Styles.PartsList;
using PartSize = Autodesk.Civil.DatabaseServices.Styles.PartSize;

[assembly: CommandClass(typeof(CETools.Civil3D.August13SewerMultiSourceNetworkCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates one Civil 3D gravity sewer network directly from a complete set of
    /// selected source lines/polylines/feature lines. Unlike Civil 3D's native
    /// CreateNetworkFromObject command, this workflow never falls back to a second
    /// one-object selection prompt. Shared source vertices reuse one structure so
    /// branches become part of the same connected network.
    /// </summary>
    public sealed class August13SewerMultiSourceNetworkCommands
    {
        private const double PointTolerance = 0.001;

        [CommandMethod(
            "CE_TOOLS",
            "CE_SEWERNETWORKMULTI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CreateSewerNetworkFromMultipleSources()
        {
            // Reuse the established surface-first sewer gravity-network runtime.
            // This is the same vetted workflow that exposes the reference surface,
            // independent pipe/structure rule sets, shared junctions and
            // maximum structure spacing. Do not maintain a second divergent
            // CreateNetworkFromMultipleSources implementation here.
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;
            September09SewerSurfaceRulesRuntime.CreateNetworkFromMultipleSources(
                document, civilDocument);
        }

        private static ObjectId EnsureStructure(
            CivilNetwork network,
            Transaction transaction,
            IDictionary<string, ObjectId> structures,
            PartChoice structureChoice,
            Point3d point,
            bool applyRules,
            ref int created)
        {
            string key = NodeKey(point);
            ObjectId existing;
            if (structures.TryGetValue(key, out existing) &&
                !existing.IsNull &&
                !existing.IsErased)
                return existing;

            ObjectId structureId = ObjectId.Null;
            network.AddStructure(
                structureChoice.FamilyId,
                structureChoice.SizeId,
                point,
                0.0,
                ref structureId,
                applyRules);
            if (!structureId.IsNull)
            {
                structures[key] = structureId;
                created++;
            }
            return structureId;
        }

        private static string NodeKey(Point3d point)
        {
            long x = (long)Math.Round(point.X / PointTolerance);
            long y = (long)Math.Round(point.Y / PointTolerance);
            return x.ToString(CultureInfo.InvariantCulture) + "|" +
                   y.ToString(CultureInfo.InvariantCulture);
        }

        private static List<ObjectId> FilterSupportedSources(
            Database database,
            IEnumerable<ObjectId> ids)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids
                    .Where(value => !value.IsNull && !value.IsErased)
                    .Distinct())
                {
                    DBObject value;
                    try
                    {
                        value = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false);
                    }
                    catch
                    {
                        continue;
                    }

                    if (value is Line ||
                        value is Polyline ||
                        value is Polyline2d ||
                        value is Polyline3d ||
                        value is CivilFeatureLine)
                    {
                        result.Add(id);
                    }
                }
            }
            return result;
        }

        private static List<SourcePath> ReadSourcePaths(
            Database database,
            IEnumerable<ObjectId> sourceIds)
        {
            var result = new List<SourcePath>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in sourceIds)
                {
                    DBObject value;
                    try
                    {
                        value = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false);
                    }
                    catch
                    {
                        continue;
                    }

                    List<Point3d> points = ReadPoints(
                        value,
                        transaction);
                    RemoveConsecutiveDuplicates(points);
                    if (points.Count < 2) continue;
                    result.Add(new SourcePath
                    {
                        SourceId = id,
                        Points = points
                    });
                }
            }
            return result;
        }

        private static List<Point3d> ReadPoints(
            DBObject value,
            Transaction transaction)
        {
            var points = new List<Point3d>();

            Line line = value as Line;
            if (line != null)
            {
                points.Add(line.StartPoint);
                points.Add(line.EndPoint);
                return points;
            }

            Polyline polyline = value as Polyline;
            if (polyline != null)
            {
                for (int index = 0; index < polyline.NumberOfVertices; index++)
                    points.Add(polyline.GetPoint3dAt(index));
                if (polyline.Closed && points.Count > 2)
                    points.Add(points[0]);
                return points;
            }

            Polyline3d polyline3d = value as Polyline3d;
            if (polyline3d != null)
            {
                foreach (ObjectId vertexId in polyline3d)
                {
                    PolylineVertex3d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as PolylineVertex3d;
                    if (vertex != null)
                        points.Add(vertex.Position);
                }
                if (polyline3d.Closed && points.Count > 2)
                    points.Add(points[0]);
                return points;
            }

            Polyline2d polyline2d = value as Polyline2d;
            if (polyline2d != null)
            {
                foreach (ObjectId vertexId in polyline2d)
                {
                    Vertex2d vertex = transaction.GetObject(
                        vertexId,
                        OpenMode.ForRead,
                        false) as Vertex2d;
                    if (vertex != null)
                        points.Add(vertex.Position);
                }
                if (polyline2d.Closed && points.Count > 2)
                    points.Add(points[0]);
                return points;
            }

            CivilFeatureLine featureLine = value as CivilFeatureLine;
            if (featureLine != null)
            {
                Point3dCollection featurePoints = featureLine.GetPoints(
                    Autodesk.Civil.FeatureLinePointType.AllPoints);
                foreach (Point3d point in featurePoints)
                    points.Add(point);
                return points;
            }

            return points;
        }

        private static void RemoveConsecutiveDuplicates(
            IList<Point3d> points)
        {
            for (int index = points.Count - 1; index > 0; index--)
            {
                if (points[index].DistanceTo(points[index - 1]) <= PointTolerance)
                    points.RemoveAt(index);
            }
        }

        private static List<PartsListChoice> ReadPartsLists(
            Database database,
            CivilDocument civilDocument)
        {
            var result = new List<PartsListChoice>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                Autodesk.Civil.DatabaseServices.Styles.PartsListCollection lists =
                    civilDocument.Styles.PartsListSet;
                for (int index = 0; index < lists.Count; index++)
                {
                    ObjectId id = lists[index];
                    PartsList list = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as PartsList;
                    if (list == null || string.IsNullOrWhiteSpace(list.Name))
                        continue;
                    result.Add(new PartsListChoice
                    {
                        ObjectId = id,
                        Name = list.Name
                    });
                }
            }
            return result
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static List<PartChoice> ReadPartChoices(
            Database database,
            ObjectId partsListId,
            DomainType domain,
            bool skipNullStructure)
        {
            var result = new List<PartChoice>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                PartsList list = transaction.GetObject(
                    partsListId,
                    OpenMode.ForRead,
                    false) as PartsList;
                if (list == null) return result;

                ObjectIdCollection familyIds =
                    list.GetPartFamilyIdsByDomain(domain);
                foreach (ObjectId familyId in familyIds)
                {
                    PartFamily family = transaction.GetObject(
                        familyId,
                        OpenMode.ForRead,
                        false) as PartFamily;
                    if (family == null) continue;
                    if (skipNullStructure &&
                        family.Name != null &&
                        family.Name.IndexOf(
                            "null",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    for (int sizeIndex = 0;
                        sizeIndex < family.PartSizeCount;
                        sizeIndex++)
                    {
                        ObjectId sizeId = family[sizeIndex];
                        PartSize size = transaction.GetObject(
                            sizeId,
                            OpenMode.ForRead,
                            false) as PartSize;
                        if (size == null) continue;
                        string sizeName = string.IsNullOrWhiteSpace(size.Name)
                            ? "Size " + (sizeIndex + 1).ToString(CultureInfo.InvariantCulture)
                            : size.Name;
                        string familyName = string.IsNullOrWhiteSpace(family.Name)
                            ? "Part Family"
                            : family.Name;
                        result.Add(new PartChoice
                        {
                            FamilyId = familyId,
                            SizeId = sizeId,
                            FamilyName = familyName,
                            SizeName = sizeName,
                            Label = familyName + " | " + sizeName
                        });
                    }
                }
            }
            return result
                .OrderBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static string PreferredPartsList(
            IList<PartsListChoice> choices)
        {
            PartsListChoice preferred = choices.FirstOrDefault(
                item => item.Name.IndexOf(
                    "sewer",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                        item.Name.IndexOf(
                            "sanitary",
                            StringComparison.OrdinalIgnoreCase) >= 0);
            return preferred == null ? choices[0].Name : preferred.Name;
        }

        private static string PreferredPart(
            IList<PartChoice> choices,
            IEnumerable<string> keywords)
        {
            foreach (string keyword in keywords)
            {
                PartChoice preferred = choices.FirstOrDefault(
                    item => item.Label.IndexOf(
                        keyword,
                        StringComparison.OrdinalIgnoreCase) >= 0);
                if (preferred != null)
                    return preferred.Label;
            }
            return choices[0].Label;
        }

        private static PartChoice FindPart(
            IEnumerable<PartChoice> choices,
            string label)
        {
            return choices.FirstOrDefault(
                item => string.Equals(
                    item.Label,
                    label,
                    StringComparison.OrdinalIgnoreCase));
        }

        private sealed class SourcePath
        {
            internal ObjectId SourceId;
            internal List<Point3d> Points = new List<Point3d>();
        }

        private sealed class PartsListChoice
        {
            internal ObjectId ObjectId;
            internal string Name;
        }

        private sealed class PartChoice
        {
            internal ObjectId FamilyId;
            internal ObjectId SizeId;
            internal string FamilyName;
            internal string SizeName;
            internal string Label;
        }
    }
}
