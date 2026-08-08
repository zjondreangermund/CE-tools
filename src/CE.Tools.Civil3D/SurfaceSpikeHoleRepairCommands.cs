using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using CivilTinSurface = Autodesk.Civil.DatabaseServices.TinSurface;

[assembly: CommandClass(typeof(CETools.Civil3D.SurfaceSpikeHoleRepairCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates a reversible TIN repair copy. Local high/low spikes are replaced
    /// with neighbourhood medians and internal open-edge components receive
    /// centroid fill points. The selected source surface remains read-only.
    /// </summary>
    public sealed class SurfaceSpikeHoleRepairCommands
    {
        private const string RegAppName = "CE_TOOLS_SPIKE_HOLE_REPAIR";
        private const int MaximumVertices = 250000;
        private const int MaximumInternalHoles = 1000;
        private const double GeometryTolerance = 0.000000001;

        [CommandMethod(
            "CE_TOOLS",
            "CE_SURFSPIKEHOLEFIX",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void RepairSpikesAndHoles()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (document == null || civilDocument == null) return;

            ObjectId sourceId;
            if (!PromptSurface(document.Editor, out sourceId)) return;

            double spikeTolerance;
            double neighbourRadius;
            int minimumNeighbours;
            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Surface Spike and Hole Repair",
                "Set the screening tolerances. The source surface remains unchanged and the repaired result is created as a separate TIN surface.");
            settings.AddPositiveDouble(
                "SpikeTolerance",
                "Repair Criteria",
                "Spike/low-point elevation difference",
                1.0,
                "Vertices exceeding the local-neighbour median by this amount are replaced in the repair copy.");
            settings.AddPositiveDouble(
                "NeighbourRadius",
                "Repair Criteria",
                "Neighbour search radius",
                5.0,
                "Plan distance used to collect local vertices for the median check.");
            settings.AddPositiveInteger(
                "MinimumNeighbours",
                "Repair Criteria",
                "Minimum neighbouring vertices",
                4,
                "A vertex is only screened when this many neighbours are available.");
            settings.AddChoice(
                "AdaptiveSearch",
                "Repair Criteria",
                "Adaptive neighbour retry",
                "Yes",
                "If the first local search finds no repair candidates, retry with a wider neighbourhood before reporting no repair.",
                new[] { "Yes", "No" });
            settings.AddChoice(
                "HoleHandling",
                "Hole Handling",
                "Internal holes",
                "Fill internal holes",
                "Choose whether internal open-edge components receive centroid repair points.",
                new[] { "Fill internal holes", "Keep internal holes" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;
            spikeTolerance = settings.Double("SpikeTolerance", 1.0);
            neighbourRadius = settings.Double("NeighbourRadius", 5.0);
            minimumNeighbours = settings.Integer("MinimumNeighbours", 4);
            bool adaptiveSearch = string.Equals(
                settings.Text("AdaptiveSearch"),
                "Yes",
                StringComparison.OrdinalIgnoreCase);
            bool fillHoles = !string.Equals(
                settings.Text("HoleHandling"),
                "Keep internal holes",
                StringComparison.OrdinalIgnoreCase);

            RepairPlan plan;
            try
            {
                plan = BuildPlan(
                    document.Database,
                    sourceId,
                    spikeTolerance,
                    neighbourRadius,
                    minimumNeighbours,
                    fillHoles);
                if (adaptiveSearch &&
                    plan.Replacements.Count == 0 &&
                    plan.HoleFillPoints.Count == 0)
                {
                    RepairPlan retry = BuildPlan(
                        document.Database,
                        sourceId,
                        spikeTolerance,
                        neighbourRadius * 4.0,
                        Math.Max(2, minimumNeighbours - 2),
                        fillHoles);
                    if (retry.Replacements.Count > 0 || retry.HoleFillPoints.Count > 0)
                    {
                        plan = retry;
                        document.Editor.WriteMessage(
                            "\nCE_SURFSPIKEHOLEFIX: the initial local search found no candidates; adaptive search expanded the neighbour radius to {0:N2} m.",
                            neighbourRadius * 4.0);
                    }
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SURFSPIKEHOLEFIX stopped. {0}",
                    exception.Message);
                return;
            }

            if (plan.Replacements.Count == 0 && plan.HoleFillPoints.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_SURFSPIKEHOLEFIX: no spike/low or internal-hole repair candidates were found with the selected criteria. No unchanged repair surface was created.");
                return;
            }

            ShowPreview(document, plan);
            document.Editor.WriteMessage(
                "\nCE_SURFSPIKEHOLEFIX preview: source={0}; vertices={1}; spike/low replacements={2}; open-edge components={3}; internal holes filled={4}; output points={5}.",
                plan.SourceName,
                plan.SourceVertices.Count,
                plan.Replacements.Count,
                plan.OpenComponents,
                plan.HoleFillPoints.Count,
                plan.OutputPoints.Count);
            document.Editor.WriteMessage(
                "\nThe original surface will not be edited. Internal-hole fill points are screening repairs and boundaries, breaklines, contours and drainage must be checked.");

            if (!Confirm(
                    document.Editor,
                    "Create the separate spike-and-hole repaired surface"))
                return;

            try
            {
                string generatedName;
                ObjectId generatedId = CreateSurface(
                    document.Database,
                    civilDocument,
                    plan,
                    out generatedName);
                document.Editor.WriteMessage(
                    "\nCE_SURFSPIKEHOLEFIX complete. Created '{0}' ({1}). Source '{2}' remains unchanged.",
                    generatedName,
                    generatedId.Handle,
                    plan.SourceName);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SURFSPIKEHOLEFIX failed. No generated surface was committed. {0}",
                    exception.Message);
            }
        }

        private static RepairPlan BuildPlan(
            Database database,
            ObjectId sourceId,
            double spikeTolerance,
            double neighbourRadius,
            int minimumNeighbours,
            bool fillHoles)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                CivilSurface source = transaction.GetObject(
                    sourceId,
                    OpenMode.ForRead,
                    false) as CivilSurface;
                if (source == null)
                    throw new InvalidOperationException(
                        "The selected object is not a Civil 3D surface.");

                List<TriangleRecord> triangles = ReadTriangles(source);
                List<Point3d> vertices = ReadVertices(source);
                if (vertices.Count < 3 && triangles.Count > 0)
                    vertices = UniqueTriangleVertices(triangles);
                if (vertices.Count < 3)
                    throw new InvalidOperationException(
                        "The selected TIN exposes fewer than three readable vertices. Rebuild the source surface and retry.");
                if (vertices.Count > MaximumVertices)
                    throw new InvalidOperationException(
                        "The selected surface exposes " +
                        vertices.Count.ToString(CultureInfo.InvariantCulture) +
                        " vertices. Increase workflow batching; this repair is limited to " +
                        MaximumVertices.ToString(CultureInfo.InvariantCulture) +
                        " vertices per run.");

                Dictionary<int, double> replacements = FindSpikeReplacements(
                    vertices,
                    spikeTolerance,
                    neighbourRadius,
                    minimumNeighbours);
                OpenEdgeResult openEdges = AnalyseOpenEdges(triangles);
                List<Point3d> holeFillPoints = fillHoles
                    ? openEdges.InternalComponents
                        .Take(MaximumInternalHoles)
                        .Select(component => component.Centroid)
                        .ToList()
                    : new List<Point3d>();

                var output = new List<Point3d>(vertices.Count + holeFillPoints.Count);
                for (int index = 0; index < vertices.Count; index++)
                {
                    Point3d point = vertices[index];
                    double replacement;
                    if (replacements.TryGetValue(index, out replacement))
                        point = new Point3d(point.X, point.Y, replacement);
                    output.Add(point);
                }
                foreach (Point3d fillPoint in holeFillPoints)
                {
                    if (!output.Any(point =>
                        PlanDistanceSquared(point, fillPoint) <= GeometryTolerance))
                        output.Add(fillPoint);
                }

                return new RepairPlan(
                    sourceId,
                    sourceId.Handle.ToString(),
                    ReadName(source),
                    vertices,
                    triangles,
                    replacements,
                    openEdges.ComponentCount,
                    holeFillPoints,
                    output,
                    spikeTolerance,
                    neighbourRadius,
                    minimumNeighbours,
                    fillHoles);
            }
        }

        private static Dictionary<int, double> FindSpikeReplacements(
            IReadOnlyList<Point3d> vertices,
            double tolerance,
            double radius,
            int minimumNeighbours)
        {
            Dictionary<GridKey, List<int>> grid = BuildGrid(vertices, radius);
            var result = new Dictionary<int, double>();
            for (int index = 0; index < vertices.Count; index++)
            {
                List<int> neighbours = FindNeighbours(
                    vertices,
                    grid,
                    index,
                    radius);
                if (neighbours.Count < minimumNeighbours) continue;
                double median = Median(neighbours.Select(item => vertices[item].Z));
                if (double.IsNaN(median) || double.IsInfinity(median)) continue;
                if (Math.Abs(vertices[index].Z - median) >= tolerance)
                    result[index] = median;
            }
            return result;
        }

        private static OpenEdgeResult AnalyseOpenEdges(
            IReadOnlyList<TriangleRecord> triangles)
        {
            if (triangles.Count == 0)
                return new OpenEdgeResult(0, new List<OpenComponent>());

            var counts = new Dictionary<EdgeKey, EdgeCount>();
            foreach (TriangleRecord triangle in triangles)
            {
                Increment(counts, triangle.A, triangle.B);
                Increment(counts, triangle.B, triangle.C);
                Increment(counts, triangle.C, triangle.A);
            }

            List<EdgeCount> open = counts.Values
                .Where(item => item.Count == 1)
                .ToList();
            if (open.Count == 0)
                return new OpenEdgeResult(0, new List<OpenComponent>());

            var adjacency = new Dictionary<PlanKey, HashSet<PlanKey>>();
            var points = new Dictionary<PlanKey, List<Point3d>>();
            foreach (EdgeCount edge in open)
            {
                AddAdjacency(adjacency, edge.FirstKey, edge.SecondKey);
                AddAdjacency(adjacency, edge.SecondKey, edge.FirstKey);
                AddPoint(points, edge.FirstKey, edge.First);
                AddPoint(points, edge.SecondKey, edge.Second);
            }

            var components = new List<OpenComponent>();
            var unvisited = new HashSet<PlanKey>(adjacency.Keys);
            while (unvisited.Count > 0)
            {
                PlanKey start = unvisited.First();
                var queue = new Queue<PlanKey>();
                var keys = new List<PlanKey>();
                queue.Enqueue(start);
                unvisited.Remove(start);
                while (queue.Count > 0)
                {
                    PlanKey current = queue.Dequeue();
                    keys.Add(current);
                    HashSet<PlanKey> neighbours;
                    if (!adjacency.TryGetValue(current, out neighbours)) continue;
                    foreach (PlanKey neighbour in neighbours)
                    {
                        if (unvisited.Remove(neighbour)) queue.Enqueue(neighbour);
                    }
                }

                List<Point3d> componentPoints = keys
                    .SelectMany(key => points[key])
                    .ToList();
                components.Add(OpenComponent.From(componentPoints));
            }

            OpenComponent outer = components
                .OrderByDescending(item => item.BoundingArea)
                .ThenByDescending(item => item.PointCount)
                .FirstOrDefault();
            List<OpenComponent> internalComponents = components
                .Where(item => !ReferenceEquals(item, outer))
                .OrderByDescending(item => item.BoundingArea)
                .ToList();
            return new OpenEdgeResult(components.Count, internalComponents);
        }

        private static void ShowPreview(Document document, RepairPlan plan)
        {
            var rows = new List<IList<string>>();
            foreach (KeyValuePair<int, double> replacement in plan.Replacements
                .OrderByDescending(item =>
                    Math.Abs(plan.SourceVertices[item.Key].Z - item.Value))
                .Take(5000))
            {
                Point3d point = plan.SourceVertices[replacement.Key];
                rows.Add(new List<string>
                {
                    "Spike/Low",
                    (replacement.Key + 1).ToString(CultureInfo.InvariantCulture),
                    point.X.ToString("0.###", CultureInfo.InvariantCulture),
                    point.Y.ToString("0.###", CultureInfo.InvariantCulture),
                    point.Z.ToString("0.###", CultureInfo.InvariantCulture),
                    replacement.Value.ToString("0.###", CultureInfo.InvariantCulture),
                    (replacement.Value - point.Z).ToString(
                        "+0.###;-0.###;0.000",
                        CultureInfo.InvariantCulture)
                });
            }
            for (int index = 0; index < plan.HoleFillPoints.Count; index++)
            {
                Point3d point = plan.HoleFillPoints[index];
                rows.Add(new List<string>
                {
                    "Hole Fill Point",
                    (index + 1).ToString(CultureInfo.InvariantCulture),
                    point.X.ToString("0.###", CultureInfo.InvariantCulture),
                    point.Y.ToString("0.###", CultureInfo.InvariantCulture),
                    "-",
                    point.Z.ToString("0.###", CultureInfo.InvariantCulture),
                    "Internal open-edge component"
                });
            }
            if (rows.Count == 0)
            {
                rows.Add(new List<string>
                {
                    "No repair", "-", "-", "-", "-", "-",
                    "No spike/low or internal-hole repair exceeded the selected settings"
                });
            }

            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Surface Spike and Hole Repair",
                "Source remains unchanged. The generated TIN copy requires contour, boundary, breakline, drainage and survey-control review.",
                new List<string>
                {
                    "Repair", "Source/No.", "X", "Y", "Original Z", "New Z", "Delta/Reason"
                },
                rows,
                "CE TOOLS SURFACE SPIKE AND HOLE REPAIR");
        }

        private static ObjectId CreateSurface(
            Database database,
            CivilDocument civilDocument,
            RepairPlan plan,
            out string generatedName)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction);
                var existingNames = new HashSet<string>(
                    civilDocument.GetSurfaceIds().Cast<ObjectId>()
                        .Select(id => transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false))
                        .Select(ReadName),
                    StringComparer.OrdinalIgnoreCase);
                generatedName = UniqueName(
                    plan.SourceName + " - CE SPIKE HOLE REPAIRED",
                    existingNames);

                ObjectId generatedId = CreateTinSurface(
                    database,
                    generatedName,
                    plan.SourceId,
                    transaction);
                DBObject generated = transaction.GetObject(
                    generatedId,
                    OpenMode.ForWrite,
                    false);
                AddPoints(generated, plan.OutputPoints);
                Rebuild(generated);
                int generatedVertexCount = ReadVertices(generated).Count;
                if (generatedVertexCount < 3)
                    throw new InvalidOperationException(
                        "The repaired TIN did not rebuild with readable vertices; no output surface will be committed.");
                generated.XData = new ResultBuffer(
                    new TypedValue(
                        (int)DxfCode.ExtendedDataRegAppName,
                        RegAppName),
                    new TypedValue(
                        (int)DxfCode.ExtendedDataAsciiString,
                        plan.SourceHandle),
                    new TypedValue(
                        (int)DxfCode.ExtendedDataAsciiString,
                        "SpikeTolerance=" + plan.SpikeTolerance.ToString(
                            "R",
                            CultureInfo.InvariantCulture)),
                    new TypedValue(
                        (int)DxfCode.ExtendedDataAsciiString,
                        "NeighbourRadius=" + plan.NeighbourRadius.ToString(
                            "R",
                            CultureInfo.InvariantCulture)),
                    new TypedValue(
                        (int)DxfCode.ExtendedDataAsciiString,
                        "MinimumNeighbours=" + plan.MinimumNeighbours.ToString(
                            CultureInfo.InvariantCulture)),
                    new TypedValue(
                        (int)DxfCode.ExtendedDataAsciiString,
                        "Replacements=" + plan.Replacements.Count.ToString(
                            CultureInfo.InvariantCulture)),
                    new TypedValue(
                        (int)DxfCode.ExtendedDataAsciiString,
                        "HoleFillPoints=" + plan.HoleFillPoints.Count.ToString(
                            CultureInfo.InvariantCulture)));
                TrySetProperty(
                    generated,
                    "Description",
                    "CE spike/hole repaired surface from source handle " +
                    plan.SourceHandle +
                    ". Original source was not modified.");
                transaction.Commit();
                return generatedId;
            }
        }

        private static List<Point3d> ReadVertices(object surface)
        {
            object raw = InvokeNoArgument(surface, "GetVertices") ??
                         ReadProperty(surface, "Vertices");
            IEnumerable values = raw as IEnumerable;
            var result = new List<Point3d>();
            if (values == null) return result;
            foreach (object value in values)
            {
                Point3d point;
                if (TryReadPoint(value, out point)) result.Add(point);
            }
            return result;
        }

        private static List<TriangleRecord> ReadTriangles(object surface)
        {
            object raw = null;
            CivilTinSurface tin = surface as CivilTinSurface;
            if (tin != null)
            {
                try { raw = tin.GetTriangles(false); }
                catch { raw = null; }
            }
            raw = raw ?? InvokeWithOptionalBoolean(surface, "GetTriangles") ??
                         ReadProperty(surface, "Triangles");
            IEnumerable values = raw as IEnumerable;
            var result = new List<TriangleRecord>();
            if (values == null) return result;
            foreach (object value in values)
            {
                Point3d a;
                Point3d b;
                Point3d c;
                if (TryReadTriangle(value, out a, out b, out c))
                    result.Add(new TriangleRecord(a, b, c));
            }
            return result;
        }

        private static bool TryReadTriangle(
            object value,
            out Point3d a,
            out Point3d b,
            out Point3d c)
        {
            a = b = c = Point3d.Origin;
            object first = ReadProperty(value, "Vertex1") ??
                           ReadProperty(value, "A") ??
                           ReadProperty(value, "Point1");
            object second = ReadProperty(value, "Vertex2") ??
                            ReadProperty(value, "B") ??
                            ReadProperty(value, "Point2");
            object third = ReadProperty(value, "Vertex3") ??
                           ReadProperty(value, "C") ??
                           ReadProperty(value, "Point3");
            return TryReadPoint(first, out a) &&
                   TryReadPoint(second, out b) &&
                   TryReadPoint(third, out c);
        }

        private static List<Point3d> UniqueTriangleVertices(IEnumerable<TriangleRecord> triangles)
        {
            var result = new List<Point3d>();
            foreach (TriangleRecord triangle in triangles ?? Enumerable.Empty<TriangleRecord>())
            {
                foreach (Point3d point in new[] { triangle.A, triangle.B, triangle.C })
                {
                    if (!result.Any(existing => PlanDistanceSquared(existing, point) <= GeometryTolerance && Math.Abs(existing.Z - point.Z) <= 0.000001))
                        result.Add(point);
                }
            }
            return result;
        }

        private static ObjectId CreateTinSurface(
            Database database,
            string name,
            ObjectId sourceId,
            Transaction transaction)
        {
            Type tinType = typeof(CivilSurface).Assembly.GetType(
                "Autodesk.Civil.DatabaseServices.TinSurface",
                true);
            DBObject source = transaction.GetObject(
                sourceId,
                OpenMode.ForRead,
                false);
            ObjectId styleId = ObjectId.Null;
            object style = ReadProperty(source, "StyleId");
            if (style is ObjectId) styleId = (ObjectId)style;

            if (!IsUsableStyleId(styleId, transaction))
                styleId = FindUsableSurfaceStyleId(transaction);

            foreach (MethodInfo method in tinType.GetMethods(
                BindingFlags.Public | BindingFlags.Static)
                .Where(item => item.Name == "Create")
                .OrderBy(item => item.GetParameters().Count(
                    parameter => parameter.ParameterType == typeof(ObjectId))))
            {
                ParameterInfo[] parameters = method.GetParameters();
                var arguments = new object[parameters.Length];
                bool supported = true;
                int objectIdIndex = 0;
                for (int index = 0; index < parameters.Length; index++)
                {
                    Type type = parameters[index].ParameterType;
                    if (type == typeof(string)) arguments[index] = name;
                    else if (type == typeof(Database)) arguments[index] = database;
                    else if (type == typeof(ObjectId))
                    {
                        objectIdIndex++;
                        if (styleId.IsNull)
                        {
                            supported = false;
                            break;
                        }
                        arguments[index] = styleId;
                    }
                    else
                    {
                        supported = false;
                        break;
                    }
                }
                if (!supported) continue;
                try
                {
                    object created = method.Invoke(null, arguments);
                    if (created is ObjectId) return (ObjectId)created;
                    DBObject databaseObject = created as DBObject;
                    if (databaseObject != null) return databaseObject.ObjectId;
                }
                catch (TargetInvocationException exception)
                {
                    if (exception.InnerException != null)
                        throw exception.InnerException;
                    throw;
                }
            }
            throw new MissingMethodException(
                "No supported TinSurface.Create overload was found.");
        }

        private static bool IsUsableStyleId(
            ObjectId styleId,
            Transaction transaction)
        {
            if (styleId.IsNull || styleId.IsErased) return false;
            try
            {
                DBObject value = transaction.GetObject(
                    styleId,
                    OpenMode.ForRead,
                    false);
                return value != null && !value.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static ObjectId FindUsableSurfaceStyleId(Transaction transaction)
        {
            try
            {
                CivilDocument civilDocument = CivilApplication.ActiveDocument;
                if (civilDocument == null) return ObjectId.Null;
                IEnumerable styles = civilDocument.Styles.SurfaceStyles as IEnumerable;
                if (styles == null) return ObjectId.Null;
                foreach (object value in styles)
                {
                    if (value is ObjectId &&
                        IsUsableStyleId((ObjectId)value, transaction))
                        return (ObjectId)value;
                }
            }
            catch
            {
                // Style-free TinSurface.Create overloads remain available.
            }
            return ObjectId.Null;
        }

        private static void AddPoints(
            DBObject surface,
            IReadOnlyList<Point3d> points)
        {
            CivilTinSurface tin = surface as CivilTinSurface;
            if (tin != null)
            {
                tin.AddVertices(new Point3dCollection(points.ToArray()));
                tin.Rebuild();
                return;
            }
            object definition = ReadProperty(surface, "Definition");
            if (definition == null)
                throw new MissingMemberException(
                    "The generated surface exposes no Definition object.");
            Point3dCollection collection = new Point3dCollection(points.ToArray());
            foreach (string name in new[] { "AddPointCollection", "AddPoints" })
            {
                foreach (MethodInfo method in definition.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.Instance)
                    .Where(item => item.Name == name))
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1) continue;
                    object argument = null;
                    if (parameters[0].ParameterType.IsAssignableFrom(
                            typeof(Point3dCollection)))
                        argument = collection;
                    else if (parameters[0].ParameterType.IsAssignableFrom(
                                 typeof(Point3d[])))
                        argument = points.ToArray();
                    if (argument == null) continue;
                    method.Invoke(definition, new[] { argument });
                    Rebuild(surface);
                    return;
                }
            }
            throw new MissingMethodException(
                "No supported generated-surface point-add method was found.");
        }

        private static void Rebuild(object surface)
        {
            MethodInfo method = surface.GetType().GetMethod(
                "Rebuild",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            if (method != null) method.Invoke(surface, null);
        }

        private static Dictionary<GridKey, List<int>> BuildGrid(
            IReadOnlyList<Point3d> points,
            double size)
        {
            var result = new Dictionary<GridKey, List<int>>();
            for (int index = 0; index < points.Count; index++)
            {
                GridKey key = GridKey.From(points[index], size);
                List<int> values;
                if (!result.TryGetValue(key, out values))
                {
                    values = new List<int>();
                    result[key] = values;
                }
                values.Add(index);
            }
            return result;
        }

        private static List<int> FindNeighbours(
            IReadOnlyList<Point3d> points,
            IDictionary<GridKey, List<int>> grid,
            int index,
            double radius)
        {
            Point3d point = points[index];
            GridKey centre = GridKey.From(point, radius);
            double radiusSquared = radius * radius;
            var result = new List<int>();
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    List<int> candidates;
                    if (!grid.TryGetValue(
                            new GridKey(centre.X + dx, centre.Y + dy),
                            out candidates))
                        continue;
                    foreach (int candidate in candidates)
                    {
                        if (candidate == index) continue;
                        if (PlanDistanceSquared(point, points[candidate]) <=
                            radiusSquared)
                            result.Add(candidate);
                    }
                }
            }
            return result;
        }

        private static double Median(IEnumerable<double> values)
        {
            double[] ordered = values.OrderBy(value => value).ToArray();
            if (ordered.Length == 0) return double.NaN;
            int middle = ordered.Length / 2;
            return ordered.Length % 2 == 0
                ? (ordered[middle - 1] + ordered[middle]) * 0.5
                : ordered[middle];
        }

        private static void Increment(
            IDictionary<EdgeKey, EdgeCount> counts,
            Point3d first,
            Point3d second)
        {
            EdgeKey key = new EdgeKey(first, second);
            EdgeCount value;
            if (!counts.TryGetValue(key, out value))
            {
                value = new EdgeCount(key, first, second);
                counts[key] = value;
            }
            value.Count++;
        }

        private static void AddAdjacency(
            IDictionary<PlanKey, HashSet<PlanKey>> adjacency,
            PlanKey from,
            PlanKey to)
        {
            HashSet<PlanKey> values;
            if (!adjacency.TryGetValue(from, out values))
            {
                values = new HashSet<PlanKey>();
                adjacency[from] = values;
            }
            values.Add(to);
        }

        private static void AddPoint(
            IDictionary<PlanKey, List<Point3d>> points,
            PlanKey key,
            Point3d point)
        {
            List<Point3d> values;
            if (!points.TryGetValue(key, out values))
            {
                values = new List<Point3d>();
                points[key] = values;
            }
            values.Add(point);
        }

        private static double PlanDistanceSquared(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return dx * dx + dy * dy;
        }

        private static object InvokeNoArgument(object owner, string name)
        {
            MethodInfo method = owner.GetType().GetMethod(
                name,
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            if (method == null) return null;
            try { return method.Invoke(owner, null); }
            catch { return null; }
        }

        private static object InvokeWithOptionalBoolean(object owner, string name)
        {
            MethodInfo noArguments = owner.GetType().GetMethod(
                name,
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            if (noArguments != null)
            {
                try { return noArguments.Invoke(owner, null); }
                catch { }
            }
            MethodInfo boolean = owner.GetType().GetMethod(
                name,
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(bool) },
                null);
            if (boolean == null) return null;
            try { return boolean.Invoke(owner, new object[] { false }); }
            catch { return null; }
        }

        private static object ReadProperty(object owner, string name)
        {
            if (owner == null) return null;
            PropertyInfo property = owner.GetType().GetProperty(
                name,
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.IgnoreCase);
            if (property == null || !property.CanRead) return null;
            try { return property.GetValue(owner, null); }
            catch { return null; }
        }

        private static void TrySetProperty(
            object owner,
            string name,
            object value)
        {
            PropertyInfo property = owner.GetType().GetProperty(
                name,
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.IgnoreCase);
            if (property == null || !property.CanWrite) return;
            try { property.SetValue(owner, value, null); }
            catch { }
        }

        private static bool TryReadPoint(object value, out Point3d point)
        {
            if (value is Point3d)
            {
                point = (Point3d)value;
                return true;
            }
            foreach (string name in new[] { "Location", "Position", "Point" })
            {
                object raw = ReadProperty(value, name);
                if (raw is Point3d)
                {
                    point = (Point3d)raw;
                    return true;
                }
            }
            point = Point3d.Origin;
            return false;
        }

        private static bool PromptSurface(Editor editor, out ObjectId surfaceId)
        {
            var options = new PromptEntityOptions(
                "\nSelect the original Civil 3D surface to repair: ");
            options.SetRejectMessage("\nSelect a Civil 3D surface.");
            options.AddAllowedClass(typeof(CivilSurface), false);
            PromptEntityResult result = editor.GetEntity(options);
            surfaceId = result.Status == PromptStatus.OK
                ? result.ObjectId
                : ObjectId.Null;
            return result.Status == PromptStatus.OK;
        }

        private static bool PromptPositiveDouble(
            Editor editor,
            string label,
            double defaultValue,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + label + " <" + defaultValue.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNegative = false,
                AllowZero = false,
                UseDefaultValue = true,
                DefaultValue = defaultValue
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK
                ? result.Value
                : defaultValue;
            return result.Status == PromptStatus.OK;
        }

        private static bool PromptPositiveInteger(
            Editor editor,
            string label,
            int defaultValue,
            out int value)
        {
            var options = new PromptIntegerOptions(
                "\n" + label + " <" + defaultValue.ToString(
                    CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNegative = false,
                AllowZero = false,
                UseDefaultValue = true,
                DefaultValue = defaultValue
            };
            PromptIntegerResult result = editor.GetInteger(options);
            value = result.Status == PromptStatus.OK
                ? result.Value
                : defaultValue;
            return result.Status == PromptStatus.OK;
        }

        private static bool Confirm(Editor editor, string message)
        {
            var options = new PromptKeywordOptions(
                "\n" + message + "? [Yes/No] <No>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK &&
                   string.Equals(
                       result.StringResult,
                       "Yes",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureRegApp(
            Database database,
            Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(
                database.RegAppTableId,
                OpenMode.ForRead,
                false) as RegAppTable;
            if (table == null || table.Has(RegAppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static string ReadName(DBObject item)
        {
            string name = Convert.ToString(
                ReadProperty(item, "Name"),
                CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(name)
                ? item.GetType().Name + " " + item.ObjectId.Handle
                : name;
        }

        private static string UniqueName(string preferred, ISet<string> existing)
        {
            string candidate = preferred;
            int suffix = 2;
            while (existing.Contains(candidate))
            {
                candidate = preferred + " (" +
                    suffix.ToString(CultureInfo.InvariantCulture) + ")";
                suffix++;
            }
            return candidate;
        }

        private sealed class RepairPlan
        {
            public RepairPlan(
                ObjectId sourceId,
                string sourceHandle,
                string sourceName,
                List<Point3d> sourceVertices,
                List<TriangleRecord> triangles,
                Dictionary<int, double> replacements,
                int openComponents,
                List<Point3d> holeFillPoints,
                List<Point3d> outputPoints,
                double spikeTolerance,
                double neighbourRadius,
                int minimumNeighbours,
                bool fillHoles)
            {
                SourceId = sourceId;
                SourceHandle = sourceHandle;
                SourceName = sourceName;
                SourceVertices = sourceVertices;
                Triangles = triangles;
                Replacements = replacements;
                OpenComponents = openComponents;
                HoleFillPoints = holeFillPoints;
                OutputPoints = outputPoints;
                SpikeTolerance = spikeTolerance;
                NeighbourRadius = neighbourRadius;
                MinimumNeighbours = minimumNeighbours;
                FillHoles = fillHoles;
            }

            public ObjectId SourceId { get; }
            public string SourceHandle { get; }
            public string SourceName { get; }
            public List<Point3d> SourceVertices { get; }
            public List<TriangleRecord> Triangles { get; }
            public Dictionary<int, double> Replacements { get; }
            public int OpenComponents { get; }
            public List<Point3d> HoleFillPoints { get; }
            public List<Point3d> OutputPoints { get; }
            public double SpikeTolerance { get; }
            public double NeighbourRadius { get; }
            public int MinimumNeighbours { get; }
            public bool FillHoles { get; }
        }

        private sealed class TriangleRecord
        {
            public TriangleRecord(Point3d a, Point3d b, Point3d c)
            {
                A = a;
                B = b;
                C = c;
            }
            public Point3d A { get; }
            public Point3d B { get; }
            public Point3d C { get; }
        }

        private sealed class OpenEdgeResult
        {
            public OpenEdgeResult(
                int componentCount,
                List<OpenComponent> internalComponents)
            {
                ComponentCount = componentCount;
                InternalComponents = internalComponents;
            }
            public int ComponentCount { get; }
            public List<OpenComponent> InternalComponents { get; }
        }

        private sealed class OpenComponent
        {
            private OpenComponent(
                Point3d centroid,
                double boundingArea,
                int pointCount)
            {
                Centroid = centroid;
                BoundingArea = boundingArea;
                PointCount = pointCount;
            }

            public Point3d Centroid { get; }
            public double BoundingArea { get; }
            public int PointCount { get; }

            public static OpenComponent From(IReadOnlyList<Point3d> points)
            {
                if (points == null || points.Count == 0)
                    return new OpenComponent(Point3d.Origin, 0.0, 0);
                double x = points.Average(point => point.X);
                double y = points.Average(point => point.Y);
                double z = points.Average(point => point.Z);
                double minX = points.Min(point => point.X);
                double maxX = points.Max(point => point.X);
                double minY = points.Min(point => point.Y);
                double maxY = points.Max(point => point.Y);
                return new OpenComponent(
                    new Point3d(x, y, z),
                    Math.Max(0.0, (maxX - minX) * (maxY - minY)),
                    points.Count);
            }
        }

        private sealed class EdgeCount
        {
            public EdgeCount(EdgeKey key, Point3d first, Point3d second)
            {
                Key = key;
                First = first;
                Second = second;
            }
            public EdgeKey Key { get; }
            public PlanKey FirstKey => Key.First;
            public PlanKey SecondKey => Key.Second;
            public Point3d First { get; }
            public Point3d Second { get; }
            public int Count { get; set; }
        }

        private struct GridKey : IEquatable<GridKey>
        {
            public GridKey(long x, long y)
            {
                X = x;
                Y = y;
            }
            public long X { get; }
            public long Y { get; }
            public static GridKey From(Point3d point, double size)
            {
                return new GridKey(
                    (long)Math.Floor(point.X / size),
                    (long)Math.Floor(point.Y / size));
            }
            public bool Equals(GridKey other) => X == other.X && Y == other.Y;
            public override bool Equals(object value) =>
                value is GridKey && Equals((GridKey)value);
            public override int GetHashCode()
            {
                unchecked { return (X.GetHashCode() * 397) ^ Y.GetHashCode(); }
            }
        }

        private struct PlanKey : IEquatable<PlanKey>
        {
            private const double Scale = 1000.0;
            public PlanKey(Point3d point)
            {
                X = (long)Math.Round(point.X * Scale);
                Y = (long)Math.Round(point.Y * Scale);
            }
            public long X { get; }
            public long Y { get; }
            public bool Equals(PlanKey other) => X == other.X && Y == other.Y;
            public override bool Equals(object value) =>
                value is PlanKey && Equals((PlanKey)value);
            public override int GetHashCode()
            {
                unchecked { return (X.GetHashCode() * 397) ^ Y.GetHashCode(); }
            }
        }

        private struct EdgeKey : IEquatable<EdgeKey>
        {
            public EdgeKey(Point3d first, Point3d second)
            {
                PlanKey a = new PlanKey(first);
                PlanKey b = new PlanKey(second);
                if (a.X < b.X || (a.X == b.X && a.Y <= b.Y))
                {
                    First = a;
                    Second = b;
                }
                else
                {
                    First = b;
                    Second = a;
                }
            }
            public PlanKey First { get; }
            public PlanKey Second { get; }
            public bool Equals(EdgeKey other) =>
                First.Equals(other.First) && Second.Equals(other.Second);
            public override bool Equals(object value) =>
                value is EdgeKey && Equals((EdgeKey)value);
            public override int GetHashCode()
            {
                unchecked
                {
                    return (First.GetHashCode() * 397) ^ Second.GetHashCode();
                }
            }
        }
    }
}
