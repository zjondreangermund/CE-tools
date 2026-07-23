using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcCurve = Autodesk.AutoCAD.DatabaseServices.Curve;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

[assembly: CommandClass(typeof(CETools.Civil3D.FeatureLineRelativeCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates and maintains CE Tools stepped-offset feature lines that retain a
    /// stored relationship to one editable source feature line.
    /// </summary>
    public sealed class FeatureLineRelativeCommands
    {
        private const string RecordKey = "CE_FLREL";
        private const double Tolerance = 1e-7;

        [CommandMethod("CE_TOOLS", "CE_FLREL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Menu()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var options = new PromptKeywordOptions(
                "\nLinked feature-line tool [Create/Update/Info/Detach] <Create>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Create");
            options.Keywords.Add("Update");
            options.Keywords.Add("Info");
            options.Keywords.Add("Detach");

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string mode = result.Status == PromptStatus.None ? "Create" : result.StringResult;

            if (mode.Equals("Update", StringComparison.OrdinalIgnoreCase)) Update(document);
            else if (mode.Equals("Info", StringComparison.OrdinalIgnoreCase)) Info(document);
            else if (mode.Equals("Detach", StringComparison.OrdinalIgnoreCase)) Detach(document);
            else Create(document);
        }

        [CommandMethod("CE_TOOLS", "CE_FLRELCREATE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateCommand()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null) Create(document);
        }

        [CommandMethod("CE_TOOLS", "CE_FLRELUPDATE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void UpdateCommand()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null) Update(document);
        }

        [CommandMethod("CE_TOOLS", "CE_FLRELINFO", CommandFlags.Modal)]
        public void InfoCommand()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null) Info(document);
        }

        [CommandMethod("CE_TOOLS", "CE_FLRELDETACH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void DetachCommand()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null) Detach(document);
        }

        private static void Create(Document document)
        {
            Editor editor = document.Editor;
            PromptEntityResult sourceResult = PromptFeatureLine(editor, "\nSelect SOURCE feature line: ");
            if (sourceResult.Status != PromptStatus.OK) return;

            PromptPointResult sideResult = editor.GetPoint(
                "\nPick the side on which the stepped offsets must be created: ");
            if (sideResult.Status != PromptStatus.OK) return;

            PromptDoubleResult horizontalResult = editor.GetDouble(new PromptDoubleOptions(
                "\nHorizontal step distance <1.000>: ")
            {
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = 1.0,
                UseDefaultValue = true
            });
            if (horizontalResult.Status != PromptStatus.OK) return;

            PromptDoubleResult verticalResult = editor.GetDouble(new PromptDoubleOptions(
                "\nVertical step difference; positive is above, negative is below <0.000>: ")
            {
                AllowNegative = true,
                AllowZero = true,
                DefaultValue = 0.0,
                UseDefaultValue = true
            });
            if (verticalResult.Status != PromptStatus.OK) return;

            PromptIntegerResult countResult = editor.GetInteger(new PromptIntegerOptions(
                "\nNumber of linked stepped offsets <1>: ")
            {
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = 1,
                LowerLimit = 1,
                UseDefaultValue = true
            });
            if (countResult.Status != PromptStatus.OK) return;

            string defaultPrefix;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine source = OpenFeatureLine(
                        transaction, sourceResult.ObjectId, OpenMode.ForRead);
                    EnsureEditable(source, transaction);
                    defaultPrefix = string.IsNullOrWhiteSpace(source.Name)
                        ? "FeatureLine-STEP"
                        : source.Name + "-STEP";
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_FLREL cancelled. " + exception.Message);
                return;
            }

            PromptResult prefixResult = editor.GetString(new PromptStringOptions(
                "\nLinked feature-line name prefix <" + defaultPrefix + ">: ")
            {
                AllowSpaces = true,
                DefaultValue = defaultPrefix,
                UseDefaultValue = true
            });
            if (prefixResult.Status != PromptStatus.OK) return;
            string prefix = string.IsNullOrWhiteSpace(prefixResult.StringResult)
                ? defaultPrefix
                : prefixResult.StringResult.Trim();

            Point3d sidePoint = sideResult.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            double sign;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine source = OpenFeatureLine(
                        transaction, sourceResult.ObjectId, OpenMode.ForRead);
                    EnsureEditable(source, transaction);
                    using (Polyline plan = BuildPlanPolyline(source))
                    {
                        sign = ResolveOffsetSign(plan, horizontalResult.Value, sidePoint);
                    }
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_FLREL cancelled while preparing the offset. " + exception.Message);
                return;
            }

            editor.WriteMessage(
                "\nCE_FLREL preview: offsets={0}; horizontal step={1:N3}; vertical step={2:N3}; side={3}.",
                countResult.Value,
                horizontalResult.Value,
                verticalResult.Value,
                sign > 0.0 ? "Left" : "Right");
            for (int index = 1; index <= countResult.Value; index++)
            {
                editor.WriteMessage(
                    "\n  {0}-{1}: horizontal={2:N3}; vertical={3:N3}",
                    prefix,
                    index,
                    sign * horizontalResult.Value * index,
                    verticalResult.Value * index);
            }

            if (!Confirm(editor, "Create these linked stepped-offset feature lines"))
            {
                editor.WriteMessage("\nCE_FLREL cancelled. No feature lines were created.");
                return;
            }

            try
            {
                int created = 0;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine source = OpenFeatureLine(
                        transaction, sourceResult.ObjectId, OpenMode.ForRead);
                    EnsureEditable(source, transaction);
                    BlockTableRecord modelSpace = GetModelSpace(
                        document.Database, transaction, OpenMode.ForWrite);
                    HashSet<string> names = ReadFeatureLineNames(modelSpace, transaction);

                    using (Polyline plan = BuildPlanPolyline(source))
                    {
                        modelSpace.AppendEntity(plan);
                        transaction.AddNewlyCreatedDBObject(plan, true);

                        for (int index = 1; index <= countResult.Value; index++)
                        {
                            double horizontal = sign * horizontalResult.Value * index;
                            double vertical = verticalResult.Value * index;
                            string name = UniqueName(
                                prefix + "-" + index.ToString(CultureInfo.InvariantCulture), names);
                            ObjectId childId = CreateChild(
                                source,
                                plan,
                                horizontal,
                                vertical,
                                name,
                                source.LayerId,
                                source.StyleName,
                                source.SiteId,
                                modelSpace,
                                transaction);
                            CivilFeatureLine child = OpenFeatureLine(
                                transaction, childId, OpenMode.ForWrite);
                            WriteRelation(
                                child,
                                source.Handle.ToString(),
                                horizontal,
                                vertical,
                                index,
                                transaction);
                            created++;
                        }

                        if (!plan.IsErased) plan.Erase();
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_FLREL complete. Linked feature lines created: {0}. Run CE_FLRELUPDATE after changing the source.",
                    created);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_FLREL cancelled. No changes were committed. " + exception.Message);
            }
        }

        private static void Update(Document document)
        {
            Editor editor = document.Editor;
            PromptEntityResult selectedResult = PromptFeatureLine(
                editor,
                "\nSelect a source feature line or one linked child: ");
            if (selectedResult.Status != PromptStatus.OK) return;

            ObjectId sourceId;
            List<ChildRecord> children;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine selected = OpenFeatureLine(
                        transaction, selectedResult.ObjectId, OpenMode.ForRead);
                    if (selected == null) throw new InvalidOperationException("Select an ordinary feature line.");

                    Relation relation;
                    sourceId = TryReadRelation(selected, transaction, out relation)
                        ? ResolveHandle(document.Database, relation.SourceHandle)
                        : selected.ObjectId;

                    CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForRead);
                    EnsureEditable(source, transaction);
                    BlockTableRecord modelSpace = GetModelSpace(
                        document.Database, transaction, OpenMode.ForRead);
                    children = FindChildren(
                        modelSpace, source.Handle.ToString(), transaction);
                }
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_FLRELUPDATE cancelled. " + exception.Message);
                return;
            }

            if (children.Count == 0)
            {
                editor.WriteMessage("\nNo linked stepped-offset feature lines were found.");
                return;
            }

            editor.WriteMessage("\nCE_FLRELUPDATE preview: linked children={0}.", children.Count);
            foreach (ChildRecord child in children.OrderBy(item => item.Relation.Sequence))
            {
                editor.WriteMessage(
                    "\n  {0}: horizontal={1:N3}; vertical={2:N3}",
                    child.Name,
                    child.Relation.HorizontalOffset,
                    child.Relation.VerticalOffset);
            }

            if (!Confirm(editor, "Refresh all linked feature lines from this source"))
            {
                editor.WriteMessage("\nCE_FLRELUPDATE cancelled. No feature lines were changed.");
                return;
            }

            try
            {
                int rebuilt = 0;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForRead);
                    EnsureEditable(source, transaction);
                    BlockTableRecord modelSpace = GetModelSpace(
                        document.Database, transaction, OpenMode.ForWrite);

                    using (Polyline plan = BuildPlanPolyline(source))
                    {
                        modelSpace.AppendEntity(plan);
                        transaction.AddNewlyCreatedDBObject(plan, true);

                        foreach (ChildRecord record in children.OrderBy(item => item.Relation.Sequence))
                        {
                            CivilFeatureLine oldChild = OpenFeatureLine(
                                transaction, record.ObjectId, OpenMode.ForWrite);
                            if (oldChild == null || oldChild.IsReferenceObject)
                                throw new InvalidOperationException("A linked child is unavailable or referenced.");
                            if (IsLayerLocked(transaction, oldChild.LayerId))
                                throw new InvalidOperationException("Linked feature line '" + oldChild.Name + "' is on a locked layer.");

                            string name = oldChild.Name;
                            ObjectId layerId = oldChild.LayerId;
                            string styleName = oldChild.StyleName;
                            ObjectId siteId = oldChild.SiteId;
                            oldChild.Name = "CE_TMP_FLREL_" + Guid.NewGuid().ToString("N");
                            oldChild.Erase();

                            ObjectId childId = CreateChild(
                                source,
                                plan,
                                record.Relation.HorizontalOffset,
                                record.Relation.VerticalOffset,
                                name,
                                layerId,
                                styleName,
                                siteId,
                                modelSpace,
                                transaction);
                            CivilFeatureLine newChild = OpenFeatureLine(
                                transaction, childId, OpenMode.ForWrite);
                            WriteRelation(
                                newChild,
                                source.Handle.ToString(),
                                record.Relation.HorizontalOffset,
                                record.Relation.VerticalOffset,
                                record.Relation.Sequence,
                                transaction);
                            rebuilt++;
                        }

                        if (!plan.IsErased) plan.Erase();
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_FLRELUPDATE complete. Linked feature lines rebuilt: {0}.", rebuilt);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_FLRELUPDATE cancelled. No changes were committed. " + exception.Message);
            }
        }

        private static void Info(Document document)
        {
            PromptEntityResult result = PromptFeatureLine(
                document.Editor, "\nSelect a linked stepped-offset feature line: ");
            if (result.Status != PromptStatus.OK) return;

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine child = OpenFeatureLine(
                        transaction, result.ObjectId, OpenMode.ForRead);
                    Relation relation;
                    if (child == null || !TryReadRelation(child, transaction, out relation))
                    {
                        document.Editor.WriteMessage("\nThe selected feature line is not linked by CE Tools.");
                        return;
                    }

                    ObjectId sourceId = ResolveHandle(document.Database, relation.SourceHandle);
                    CivilFeatureLine source = OpenFeatureLine(transaction, sourceId, OpenMode.ForRead);
                    document.Editor.WriteMessage(
                        "\nCE linked feature line" +
                        "\n  Child: {0}" +
                        "\n  Source: {1}" +
                        "\n  Horizontal offset: {2:N3}" +
                        "\n  Vertical difference: {3:N3}" +
                        "\n  Step sequence: {4}",
                        child.Name,
                        source == null ? relation.SourceHandle : source.Name,
                        relation.HorizontalOffset,
                        relation.VerticalOffset,
                        relation.Sequence);
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_FLRELINFO cancelled. " + exception.Message);
            }
        }

        private static void Detach(Document document)
        {
            PromptEntityResult result = PromptFeatureLine(
                document.Editor, "\nSelect a linked feature line to detach: ");
            if (result.Status != PromptStatus.OK) return;

            string name;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilFeatureLine child = OpenFeatureLine(
                    transaction, result.ObjectId, OpenMode.ForRead);
                Relation relation;
                if (child == null || !TryReadRelation(child, transaction, out relation))
                {
                    document.Editor.WriteMessage("\nThe selected feature line is not linked by CE Tools.");
                    return;
                }
                name = child.Name;
            }

            if (!Confirm(document.Editor, "Detach '" + name + "' while keeping its current geometry"))
            {
                document.Editor.WriteMessage("\nCE_FLRELDETACH cancelled.");
                return;
            }

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine child = OpenFeatureLine(
                        transaction, result.ObjectId, OpenMode.ForWrite);
                    RemoveRelation(child, transaction);
                    transaction.Commit();
                }
                document.Editor.WriteMessage(
                    "\nCE_FLRELDETACH complete. The feature line remains but is no longer linked.");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_FLRELDETACH cancelled. No changes were committed. " + exception.Message);
            }
        }

        private static ObjectId CreateChild(
            CivilFeatureLine source,
            Polyline plan,
            double horizontalOffset,
            double verticalOffset,
            string name,
            ObjectId layerId,
            string styleName,
            ObjectId siteId,
            BlockTableRecord modelSpace,
            Transaction transaction)
        {
            DBObjectCollection offsets = plan.GetOffsetCurves(horizontalOffset);
            if (offsets == null || offsets.Count != 1)
            {
                Dispose(offsets);
                throw new InvalidOperationException(
                    "The stepped offset produced multiple or no curves. Reduce the offset or simplify self-intersections.");
            }

            AcCurve curve = offsets[0] as AcCurve;
            if (curve == null)
            {
                Dispose(offsets);
                throw new InvalidOperationException("The offset did not produce a supported curve.");
            }

            curve.SetDatabaseDefaults(source.Database);
            curve.LayerId = layerId;
            modelSpace.AppendEntity(curve);
            transaction.AddNewlyCreatedDBObject(curve, true);

            ObjectId childId = siteId.IsNull
                ? CivilFeatureLine.Create(name, curve.ObjectId)
                : CivilFeatureLine.Create(name, curve.ObjectId, siteId);
            CivilFeatureLine child = OpenFeatureLine(transaction, childId, OpenMode.ForWrite);
            if (child == null) throw new InvalidOperationException("Civil 3D did not return the new feature line.");

            child.LayerId = layerId;
            if (!string.IsNullOrWhiteSpace(styleName)) child.StyleName = styleName;
            SetRelativeElevations(source, child, verticalOffset);
            if (!curve.IsErased) curve.Erase();
            return childId;
        }

        private static void SetRelativeElevations(
            CivilFeatureLine source,
            CivilFeatureLine child,
            double verticalOffset)
        {
            Point3dCollection points = child.GetPoints(FeatureLinePointType.AllPoints);
            for (int index = 0; index < points.Count; index++)
            {
                Point3d point = points[index];
                Point3d sourcePoint = source.GetClosestPointTo(
                    new Point3d(point.X, point.Y, 0.0), Vector3d.ZAxis, false);
                child.SetPointElevation(index, sourcePoint.Z + verticalOffset);
            }

            Point3dCollection sourceElevationPoints = source.GetPoints(
                FeatureLinePointType.ElevationPoint);
            foreach (Point3d sourcePoint in sourceElevationPoints)
            {
                try
                {
                    double parameter = source.GetParameterAtPoint(sourcePoint);
                    Point3d target = child.GetPointAtParameter(parameter);
                    child.InsertElevationPoint(target);
                    Point3dCollection updated = child.GetPoints(FeatureLinePointType.AllPoints);
                    int index = ClosestIndex(updated, target);
                    child.SetPointElevation(index, sourcePoint.Z + verticalOffset);
                }
                catch (ArgumentException)
                {
                    // A PI or elevation point already exists at this location.
                }
            }
        }

        private static Polyline BuildPlanPolyline(CivilFeatureLine source)
        {
            Point3dCollection piPoints = source.GetPoints(FeatureLinePointType.PIPoint);
            if (piPoints == null || piPoints.Count < 2)
                throw new InvalidOperationException("The source requires at least two PI points.");

            List<Point3d> points = piPoints.Cast<Point3d>().ToList();
            bool closed = source.Closed;
            if (closed && points.Count > 2 && PlanDistance(points[0], points[points.Count - 1]) <= Tolerance)
                points.RemoveAt(points.Count - 1);

            var polyline = new Polyline(points.Count)
            {
                Normal = Vector3d.ZAxis,
                Elevation = 0.0,
                Closed = closed
            };

            int segmentCount = closed ? points.Count : points.Count - 1;
            for (int index = 0; index < points.Count; index++)
            {
                double bulge = index < segmentCount ? source.GetBulge(index) : 0.0;
                polyline.AddVertexAt(
                    index,
                    new Point2d(points[index].X, points[index].Y),
                    bulge,
                    0.0,
                    0.0);
            }
            return polyline;
        }

        private static double ResolveOffsetSign(Polyline plan, double distance, Point3d sidePoint)
        {
            double positive = DistanceToOffset(plan, distance, sidePoint);
            double negative = DistanceToOffset(plan, -distance, sidePoint);
            if (double.IsInfinity(positive) && double.IsInfinity(negative))
                throw new InvalidOperationException("An offset could not be created on either side.");
            return positive <= negative ? 1.0 : -1.0;
        }

        private static double DistanceToOffset(Polyline plan, double offset, Point3d sidePoint)
        {
            DBObjectCollection curves = plan.GetOffsetCurves(offset);
            try
            {
                if (curves == null || curves.Count != 1) return double.PositiveInfinity;
                AcCurve curve = curves[0] as AcCurve;
                if (curve == null) return double.PositiveInfinity;
                Point3d closest = curve.GetClosestPointTo(
                    new Point3d(sidePoint.X, sidePoint.Y, 0.0), false);
                return PlanDistance(closest, sidePoint);
            }
            finally
            {
                Dispose(curves);
            }
        }

        private static List<ChildRecord> FindChildren(
            BlockTableRecord modelSpace,
            string sourceHandle,
            Transaction transaction)
        {
            var children = new List<ChildRecord>();
            foreach (ObjectId objectId in modelSpace)
            {
                CivilFeatureLine child = transaction.GetObject(
                    objectId, OpenMode.ForRead, false) as CivilFeatureLine;
                if (child == null) continue;
                Relation relation;
                if (TryReadRelation(child, transaction, out relation) &&
                    relation.SourceHandle.Equals(sourceHandle, StringComparison.OrdinalIgnoreCase))
                    children.Add(new ChildRecord(objectId, child.Name, relation));
            }
            return children;
        }

        private static void WriteRelation(
            CivilFeatureLine child,
            string sourceHandle,
            double horizontalOffset,
            double verticalOffset,
            int sequence,
            Transaction transaction)
        {
            if (child.ExtensionDictionary.IsNull) child.CreateExtensionDictionary();
            DBDictionary dictionary = (DBDictionary)transaction.GetObject(
                child.ExtensionDictionary, OpenMode.ForWrite, false);
            Xrecord record;
            if (dictionary.Contains(RecordKey))
            {
                record = (Xrecord)transaction.GetObject(
                    dictionary.GetAt(RecordKey), OpenMode.ForWrite, false);
            }
            else
            {
                record = new Xrecord();
                dictionary.SetAt(RecordKey, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, sourceHandle),
                new TypedValue((int)DxfCode.Real, horizontalOffset),
                new TypedValue((int)DxfCode.Real, verticalOffset),
                new TypedValue((int)DxfCode.Int32, sequence));
        }

        private static bool TryReadRelation(
            CivilFeatureLine child,
            Transaction transaction,
            out Relation relation)
        {
            relation = null;
            if (child.ExtensionDictionary.IsNull) return false;
            DBDictionary dictionary = transaction.GetObject(
                child.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(RecordKey)) return false;
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(RecordKey), OpenMode.ForRead, false) as Xrecord;
            TypedValue[] values = record?.Data?.AsArray();
            if (values == null || values.Length < 4) return false;
            relation = new Relation(
                Convert.ToString(values[0].Value, CultureInfo.InvariantCulture),
                Convert.ToDouble(values[1].Value, CultureInfo.InvariantCulture),
                Convert.ToDouble(values[2].Value, CultureInfo.InvariantCulture),
                Convert.ToInt32(values[3].Value, CultureInfo.InvariantCulture));
            return !string.IsNullOrWhiteSpace(relation.SourceHandle);
        }

        private static void RemoveRelation(CivilFeatureLine child, Transaction transaction)
        {
            if (child == null || child.ExtensionDictionary.IsNull) return;
            DBDictionary dictionary = transaction.GetObject(
                child.ExtensionDictionary, OpenMode.ForWrite, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(RecordKey)) return;
            ObjectId recordId = dictionary.GetAt(RecordKey);
            dictionary.Remove(RecordKey);
            DBObject record = transaction.GetObject(recordId, OpenMode.ForWrite, false);
            record.Erase();
        }

        private static ObjectId ResolveHandle(Database database, string text)
        {
            long value;
            if (string.IsNullOrWhiteSpace(text) ||
                !long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                throw new InvalidOperationException("The linked source handle is invalid.");
            ObjectId id = database.GetObjectId(false, new Handle(value), 0);
            if (id.IsNull || id.IsErased)
                throw new InvalidOperationException("The linked source feature line no longer exists.");
            return id;
        }

        private static PromptEntityResult PromptFeatureLine(Editor editor, string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect an ordinary Civil 3D feature line.");
            options.AddAllowedClass(typeof(CivilFeatureLine), false);
            return editor.GetEntity(options);
        }

        private static CivilFeatureLine OpenFeatureLine(
            Transaction transaction,
            ObjectId id,
            OpenMode mode)
        {
            return id.IsNull
                ? null
                : transaction.GetObject(id, mode, false) as CivilFeatureLine;
        }

        private static void EnsureEditable(CivilFeatureLine featureLine, Transaction transaction)
        {
            if (featureLine == null || featureLine.IsReferenceObject ||
                IsLayerLocked(transaction, featureLine.LayerId))
                throw new InvalidOperationException(
                    "The feature line must be editable and on an unlocked layer.");
        }

        private static bool IsLayerLocked(Transaction transaction, ObjectId layerId)
        {
            LayerTableRecord layer = transaction.GetObject(
                layerId, OpenMode.ForRead, false) as LayerTableRecord;
            return layer != null && layer.IsLocked;
        }

        private static BlockTableRecord GetModelSpace(
            Database database,
            Transaction transaction,
            OpenMode mode)
        {
            BlockTable blockTable = (BlockTable)transaction.GetObject(
                database.BlockTableId, OpenMode.ForRead, false);
            return (BlockTableRecord)transaction.GetObject(
                blockTable[BlockTableRecord.ModelSpace], mode, false);
        }

        private static HashSet<string> ReadFeatureLineNames(
            BlockTableRecord modelSpace,
            Transaction transaction)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in modelSpace)
            {
                CivilFeatureLine featureLine = transaction.GetObject(
                    id, OpenMode.ForRead, false) as CivilFeatureLine;
                if (featureLine != null && !string.IsNullOrWhiteSpace(featureLine.Name))
                    names.Add(featureLine.Name);
            }
            return names;
        }

        private static string UniqueName(string requested, ISet<string> names)
        {
            string baseName = string.IsNullOrWhiteSpace(requested)
                ? "FeatureLine-STEP"
                : requested.Trim();
            string candidate = baseName;
            int suffix = 2;
            while (!names.Add(candidate))
            {
                candidate = baseName + " (" +
                    suffix.ToString(CultureInfo.InvariantCulture) + ")";
                suffix++;
            }
            return candidate;
        }

        private static int ClosestIndex(Point3dCollection points, Point3d target)
        {
            int best = 0;
            double distance = double.PositiveInfinity;
            for (int index = 0; index < points.Count; index++)
            {
                double current = PlanDistance(points[index], target);
                if (current < distance)
                {
                    distance = current;
                    best = index;
                }
            }
            return best;
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        private static void Dispose(DBObjectCollection collection)
        {
            if (collection == null) return;
            foreach (DBObject item in collection) item?.Dispose();
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
                   result.StringResult.Equals("Yes", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class Relation
        {
            public Relation(string sourceHandle, double horizontalOffset, double verticalOffset, int sequence)
            {
                SourceHandle = sourceHandle;
                HorizontalOffset = horizontalOffset;
                VerticalOffset = verticalOffset;
                Sequence = sequence;
            }
            public string SourceHandle { get; }
            public double HorizontalOffset { get; }
            public double VerticalOffset { get; }
            public int Sequence { get; }
        }

        private sealed class ChildRecord
        {
            public ChildRecord(ObjectId objectId, string name, Relation relation)
            {
                ObjectId = objectId;
                Name = name;
                Relation = relation;
            }
            public ObjectId ObjectId { get; }
            public string Name { get; }
            public Relation Relation { get; }
        }
    }
}
