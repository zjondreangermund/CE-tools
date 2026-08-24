using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

namespace CETools.Civil3D
{
    internal static class August24RoadElevationDynamicManager
    {
        private const string LinkKey = "CE_ROAD_ELEVATION_LINK";
        private const double PointTolerance = 0.001;

        private sealed class State
        {
            public readonly HashSet<string> ChangedHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool Busy;
        }

        private static bool _initialized;
        private static readonly Dictionary<Document, State> States = new Dictionary<Document, State>();

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            DocumentCollection documents = AcApplication.DocumentManager;
            documents.DocumentCreated += OnDocument;
            documents.DocumentActivated += OnDocument;
            documents.DocumentToBeDestroyed += OnDestroyed;
            Attach(documents.MdiActiveDocument);
        }

        internal static void Terminate()
        {
            if (!_initialized) return;
            _initialized = false;
            DocumentCollection documents = AcApplication.DocumentManager;
            documents.DocumentCreated -= OnDocument;
            documents.DocumentActivated -= OnDocument;
            documents.DocumentToBeDestroyed -= OnDestroyed;
            foreach (Document document in States.Keys.ToList()) Detach(document);
            States.Clear();
        }

        private static void OnDocument(object sender, DocumentCollectionEventArgs args)
        {
            if (args != null) Attach(args.Document);
        }

        private static void OnDestroyed(object sender, DocumentCollectionEventArgs args)
        {
            if (args != null) Detach(args.Document);
        }

        private static void Attach(Document document)
        {
            if (document == null || document.Database == null || States.ContainsKey(document)) return;
            States.Add(document, new State());
            try { document.Database.ObjectModified += OnObjectModified; } catch { }
            try { document.CommandEnded += OnCommandEnded; } catch { }
        }

        private static void Detach(Document document)
        {
            if (document == null || !States.ContainsKey(document)) return;
            try { document.Database.ObjectModified -= OnObjectModified; } catch { }
            try { document.CommandEnded -= OnCommandEnded; } catch { }
            States.Remove(document);
        }

        private static void OnObjectModified(object sender, ObjectEventArgs args)
        {
            CivilFeatureLine featureLine = args == null ? null : args.DBObject as CivilFeatureLine;
            if (featureLine == null) return;
            string handle;
            try { handle = featureLine.Handle.ToString(); } catch { return; }
            foreach (KeyValuePair<Document, State> pair in States)
            {
                if (!pair.Value.Busy && ReferenceEquals(pair.Key.Database, sender))
                {
                    pair.Value.ChangedHandles.Add(handle);
                    break;
                }
            }
        }

        private static void OnCommandEnded(object sender, CommandEventArgs args)
        {
            Document document = sender as Document;
            State state;
            if (document == null || !States.TryGetValue(document, out state) || state.Busy || state.ChangedHandles.Count == 0) return;
            var changed = new HashSet<string>(state.ChangedHandles, StringComparer.OrdinalIgnoreCase);
            state.ChangedHandles.Clear();
            state.Busy = true;
            try
            {
                int refreshed = RefreshAffected(document, changed);
                if (refreshed > 0) August21DisplayRefresh.Flush(document);
            }
            catch
            {
                foreach (string handle in changed) state.ChangedHandles.Add(handle);
            }
            finally { state.Busy = false; }
        }

        internal static int RefreshAffected(Document document, ISet<string> changedHandles)
        {
            if (document == null || document.Database == null || changedHandles == null || changedHandles.Count == 0) return 0;
            int refreshed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord model = transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(document.Database), OpenMode.ForRead, false) as BlockTableRecord;
                if (model == null) return 0;
                foreach (ObjectId id in model.Cast<ObjectId>().ToList())
                {
                    CivilFeatureLine target = null;
                    try { target = transaction.GetObject(id, OpenMode.ForRead, false) as CivilFeatureLine; } catch { }
                    if (target == null || target.IsReferenceObject) continue;
                    string masterHandle;
                    if (!TryReadMasterHandle(target, transaction, out masterHandle)) continue;
                    string targetHandle = target.Handle.ToString();
                    if (!changedHandles.Contains(targetHandle) && !changedHandles.Contains(masterHandle)) continue;
                    ObjectId masterId = ResolveHandle(document.Database, masterHandle);
                    CivilFeatureLine master = masterId.IsNull ? null : transaction.GetObject(masterId, OpenMode.ForRead, false) as CivilFeatureLine;
                    if (master == null || master.IsReferenceObject) continue;
                    Point3dCollection intersections = new Point3dCollection();
                    try { ((Entity)master).IntersectWith((Entity)target, Intersect.OnBothOperands, intersections, IntPtr.Zero, IntPtr.Zero); } catch { continue; }
                    if (intersections.Count == 0) continue;
                    target.UpgradeOpen();
                    foreach (Point3d intersection in intersections)
                    {
                        try
                        {
                            Point3d masterPoint = master.GetClosestPointTo(new Point3d(intersection.X, intersection.Y, 0.0), Vector3d.ZAxis, false);
                            Point3d targetPoint = target.GetClosestPointTo(new Point3d(intersection.X, intersection.Y, 0.0), Vector3d.ZAxis, false);
                            if (SetCrossingElevation(target, targetPoint, masterPoint.Z)) refreshed++;
                        }
                        catch { }
                    }
                }
                transaction.Commit();
            }
            return refreshed;
        }

        private static bool SetCrossingElevation(CivilFeatureLine target, Point3d crossing, double elevation)
        {
            Point3dCollection piPoints = null;
            try { piPoints = target.GetPoints(FeatureLinePointType.PIPoint); } catch { }
            if (piPoints != null)
            {
                int index = ClosestPointIndex(piPoints, crossing);
                if (index >= 0 && PlanDistance(piPoints[index], crossing) <= PointTolerance)
                {
                    target.SetPointElevation(index, elevation);
                    return true;
                }
            }
            try
            {
                target.InsertElevationPoint(new Point3d(crossing.X, crossing.Y, elevation));
                return true;
            }
            catch { }
            Point3dCollection allPoints = null;
            try { allPoints = target.GetPoints(FeatureLinePointType.AllPoints); } catch { }
            if (allPoints == null) return false;
            int allIndex = ClosestPointIndex(allPoints, crossing);
            if (allIndex < 0 || PlanDistance(allPoints[allIndex], crossing) > PointTolerance) return false;
            try { target.SetPointElevation(allIndex, elevation); return true; } catch { return false; }
        }

        private static bool TryReadMasterHandle(DBObject owner, Transaction transaction, out string masterHandle)
        {
            masterHandle = string.Empty;
            if (owner == null || owner.ExtensionDictionary.IsNull) return false;
            try
            {
                DBDictionary dictionary = transaction.GetObject(owner.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                if (dictionary == null || !dictionary.Contains(LinkKey)) return false;
                Xrecord record = transaction.GetObject(dictionary.GetAt(LinkKey), OpenMode.ForRead, false) as Xrecord;
                TypedValue[] values = record == null || record.Data == null ? new TypedValue[0] : record.Data.AsArray();
                if (values.Length == 0) return false;
                masterHandle = Convert.ToString(values[0].Value, CultureInfo.InvariantCulture);
                return !string.IsNullOrWhiteSpace(masterHandle);
            }
            catch { return false; }
        }

        private static ObjectId ResolveHandle(Database database, string text)
        {
            long value;
            if (string.IsNullOrWhiteSpace(text) || !long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return ObjectId.Null;
            try { return database.GetObjectId(false, new Handle(value), 0); } catch { return ObjectId.Null; }
        }

        private static int ClosestPointIndex(Point3dCollection points, Point3d target)
        {
            if (points == null || points.Count == 0) return -1;
            int best = 0;
            double distance = double.PositiveInfinity;
            for (int index = 0; index < points.Count; index++)
            {
                double current = PlanDistance(points[index], target);
                if (current < distance) { distance = current; best = index; }
            }
            return best;
        }

        private static double PlanDistance(Point3d first, Point3d second)
        {
            double dx = first.X - second.X;
            double dy = first.Y - second.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
