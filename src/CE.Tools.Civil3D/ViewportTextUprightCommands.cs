#if CE_CIVIL3D_2024
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ViewportTextUprightCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Keeps model-space TEXT, MTEXT and MText-content MLEADERs readable after
    /// a Civil 3D 2024 plan viewport's rotation/twist changes.
    ///
    /// Native Match Orientation to Layout is used for ALREADY annotative
    /// DBText/MText. Nonannotative entities can only have ONE world rotation;
    /// rotate those by 180 degrees only when doing so is readable through ALL
    /// viewports showing the entity. Conflicts are reported and never forced.
    /// The drawing's paper-space title blocks/annotation are never modified.
    /// </summary>
    public sealed class ViewportTextUprightCommands
    {
        [CommandMethod("CE_TOOLS", "CE_VPTEXTUPRIGHT",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void ApplyNow()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            ViewportTextUprightManager.Refresh(doc, true);
        }

        [CommandMethod("CE_TOOLS", "CE_VPTEXTAUTO", CommandFlags.Modal)]
        public void ConfigureAutoRefresh()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var options = new PromptKeywordOptions(
                "\nUpright TEXT/MTEXT/MLEADER on viewport change [On/Off/Status] <Status>: ");
            options.Keywords.Add("On");
            options.Keywords.Add("Off");
            options.Keywords.Add("Status");
            options.Keywords.Default = "Status";
            options.AllowNone = true;
            PromptResult answer = doc.Editor.GetKeywords(options);
            if (answer.Status == PromptStatus.Cancel) return;
            if (answer.Status == PromptStatus.OK)
            {
                if (answer.StringResult == "On") ViewportTextUprightManager.AutoEnabled = true;
                if (answer.StringResult == "Off") ViewportTextUprightManager.AutoEnabled = false;
            }
            doc.Editor.WriteMessage(
                "\nCE_VPTEXTAUTO: {0}. Changes apply to Civil 3D 2024 this session.",
                ViewportTextUprightManager.AutoEnabled ? "On" : "Off");
        }
    }

    internal static class ViewportTextUprightManager
    {
        internal static bool AutoEnabled = true;
        private static bool _initialized;
        private static bool _busy;
        private static DateTime _lastAttemptUtc;
        private static readonly HashSet<Database> Attached = new HashSet<Database>();
        private static readonly HashSet<Database> Pending = new HashSet<Database>();

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            DocumentCollection docs = AcApplication.DocumentManager;
            docs.DocumentCreated += OnDocument;
            docs.DocumentActivated += OnDocument;
            docs.DocumentToBeDestroyed += OnDestroyed;
            Attach(docs.MdiActiveDocument);
            AcApplication.Idle += OnIdle;
        }

        internal static void Terminate()
        {
            if (!_initialized) return;
            _initialized = false;
            AcApplication.Idle -= OnIdle;
            DocumentCollection docs = AcApplication.DocumentManager;
            docs.DocumentCreated -= OnDocument;
            docs.DocumentActivated -= OnDocument;
            docs.DocumentToBeDestroyed -= OnDestroyed;
            foreach (Database db in Attached.ToList())
                try { db.ObjectModified -= OnObjectModified; } catch { }
            Attached.Clear();
            Pending.Clear();
        }

        private static void OnDocument(object sender, DocumentCollectionEventArgs e)
        {
            if (e != null) Attach(e.Document);
        }

        private static void OnDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            Database db = e != null && e.Document != null ? e.Document.Database : null;
            if (db == null) return;
            if (Attached.Remove(db))
                try { db.ObjectModified -= OnObjectModified; } catch { }
            Pending.Remove(db);
        }

        private static void Attach(Document doc)
        {
            if (doc == null) return;
            Database db = doc.Database;
            if (db == null || !Attached.Add(db)) return;
            try { db.ObjectModified += OnObjectModified; }
            catch { Attached.Remove(db); }
        }

        private static void OnObjectModified(object sender, ObjectEventArgs e)
        {
            if (!_initialized || !AutoEnabled || _busy ||
                e == null || !(e.DBObject is Viewport))
                return;
            // Do not open/modify other DBObjects inside this event. Defer until
            // AutoCAD has finished its viewport edit and returned to Idle.
            DBObject changed = e.DBObject;
            if (changed != null && changed.Database != null)
                Pending.Add(changed.Database);
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            if (!_initialized || !AutoEnabled || _busy ||
                (DateTime.UtcNow - _lastAttemptUtc).TotalMilliseconds < 650)
                return;
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null || !Pending.Contains(doc.Database)) return;
            _lastAttemptUtc = DateTime.UtcNow;
            try
            {
                string commands = Convert.ToString(
                    AcApplication.GetSystemVariable("CMDNAMES"),
                    CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(commands)) return;
                Refresh(doc, false);
                Pending.Remove(doc.Database);
            }
            catch (System.Exception ex)
            {
                // Retain Pending and retry when Civil 3D has released the DWG.
                doc.Editor.WriteMessage(
                    "\nCE viewport annotation update deferred: {0}", ex.Message);
            }
        }

        internal static void Refresh(Document doc, bool verbose)
        {
            if (doc == null || _busy) return;
            _busy = true;
            int changed = 0, native = 0, conflicts = 0, eligible = 0;
            try
            {
                using (doc.LockDocument())
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(
                        doc.Database.BlockTableId, OpenMode.ForRead);
                    var views = new List<PlanViewport>();
                    foreach (ObjectId spaceId in bt)
                    {
                        BlockTableRecord space = tr.GetObject(
                            spaceId, OpenMode.ForRead, false) as BlockTableRecord;
                        if (space == null || !space.IsLayout ||
                            string.Equals(space.Name, BlockTableRecord.ModelSpace,
                                StringComparison.OrdinalIgnoreCase))
                            continue;
                        foreach (ObjectId id in space)
                        {
                            Viewport vp = tr.GetObject(
                                id, OpenMode.ForRead, false) as Viewport;
                            if (vp == null || vp.Number == 1 || !vp.On ||
                                vp.Width <= 0 || vp.Height <= 0 ||
                                vp.ViewHeight <= 0) continue;
                            // Restrict to conventional plan/top viewports.
                            if (Math.Abs(vp.ViewDirection.GetNormal()
                                    .DotProduct(Vector3d.ZAxis)) < .999)
                                continue;
                            views.Add(new PlanViewport(vp));
                        }
                    }

                    BlockTableRecord model = (BlockTableRecord)tr.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId entityId in model)
                    {
                        Entity entity = tr.GetObject(entityId, OpenMode.ForRead, false)
                            as Entity;
                        if (entity == null ||
                            !(entity is DBText || entity is MText || entity is MLeader))
                            continue;
                        LayerTableRecord layer = tr.GetObject(
                            entity.LayerId, OpenMode.ForRead, false)
                            as LayerTableRecord;
                        if (layer == null || layer.IsLocked || layer.IsFrozen ||
                            layer.IsOff) continue;

                        Point3d anchor;
                        try
                        {
                            Extents3d box = entity.GeometricExtents;
                            anchor = new Point3d(
                                (box.MinPoint.X + box.MaxPoint.X) / 2,
                                (box.MinPoint.Y + box.MaxPoint.Y) / 2, 0);
                        }
                        catch { continue; }

                        List<PlanViewport> containing = views.Where(
                            view => view.Contains(anchor)).ToList();
                        if (containing.Count == 0) continue;
                        eligible++;

                        // Annotative TEXT/MTEXT have a built-in way to match each
                        // paper viewport independently. Do NOT make non-annotative
                        // objects annotative automatically: doing so could resize
                        // existing drawing text.
                        if ((entity is DBText || entity is MText) &&
                            IsAlreadyAnnotative(entity))
                        {
                            if (TryEnableNativePaperOrientation(entity))
                                native++;
                            continue;
                        }

                        double angle;
                        DBText dbText = entity as DBText;
                        MText mText = entity as MText;
                        MLeader leader = entity as MLeader;
                        MText leaderContent = null;
                        try
                        {
                            if (dbText != null) angle = dbText.Rotation;
                            else if (mText != null) angle = mText.Rotation;
                            else
                            {
                                if (leader.ContentType != ContentType.MTextContent)
                                    continue;
                                leaderContent = leader.MText;
                                if (leaderContent == null) continue;
                                angle = leaderContent.Rotation;
                            }

                            bool firstNeedsFlip = NeedsFlip(angle, containing[0].Twist);
                            bool inconsistent = containing.Any(
                                view => NeedsFlip(angle, view.Twist) != firstNeedsFlip);
                            // Shared annotation cannot be rotated independently per
                            // viewport without annotative representations/overlays.
                            if (inconsistent) { conflicts++; continue; }
                            if (!firstNeedsFlip) continue;

                            entity.UpgradeOpen();
                            if (dbText != null) dbText.Rotation = Normalize(angle + Math.PI);
                            else if (mText != null) mText.Rotation = Normalize(angle + Math.PI);
                            else
                            {
                                leaderContent.Rotation = Normalize(angle + Math.PI);
                                // MLeader.MText returns a value-like nested entity:
                                // writing it back is required to persist rotation.
                                leader.MText = leaderContent;
                            }
                            changed++;
                        }
                        catch (System.Exception ex)
                        {
                            if (verbose)
                                doc.Editor.WriteMessage(
                                    "\nCE_VPTEXTUPRIGHT skipped {0}: {1}",
                                    entity.Handle, ex.Message);
                        }
                        finally
                        {
                            if (leaderContent != null) leaderContent.Dispose();
                        }
                    }
                    tr.Commit();
                }
                if (verbose || changed > 0 || native > 0 || conflicts > 0)
                    doc.Editor.WriteMessage(
                        "\nCE viewport annotations: {0} model annotations in plan views; " +
                        "{1} rotated upright; {2} native layout orientation enabled; " +
                        "{3} shared across conflicting viewport rotations (left untouched).",
                        eligible, changed, native, conflicts);
                if (verbose || changed > 0 || native > 0)
                    try { doc.Editor.Regen(); } catch { }
            }
            finally { _busy = false; }
        }

        private static bool NeedsFlip(double objectRotation, double viewportTwist)
        {
            // A 180-degree reversal retains the existing slope and readability,
            // without forcing every road annotation to be perfectly horizontal.
            return Math.Cos(objectRotation - viewportTwist) < -0.000001;
        }

        private static double Normalize(double angle)
        {
            double twoPi = Math.PI * 2;
            angle %= twoPi;
            return angle < 0 ? angle + twoPi : angle;
        }

        private static bool IsAlreadyAnnotative(Entity entity)
        {
            try
            {
                PropertyInfo property = entity.GetType().GetProperty("Annotative");
                object value = property == null ? null : property.GetValue(entity, null);
                string state = value == null ? "" : value.ToString();
                return state.Equals("True", StringComparison.OrdinalIgnoreCase) ||
                       state.Equals("Yes", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool TryEnableNativePaperOrientation(Entity entity)
        {
            try
            {
                MethodInfo setter = entity.GetType().GetMethod(
                    "SetPaperOrientation", BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(bool) }, null);
                if (setter == null) return false;
                MethodInfo getter = entity.GetType().GetMethod(
                    "GetPaperOrientation", BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (getter != null &&
                    object.Equals(getter.Invoke(entity, null), true)) return false;
                entity.UpgradeOpen();
                setter.Invoke(entity, new object[] { true });
                return true;
            }
            catch { return false; }
        }

        private sealed class PlanViewport
        {
            internal readonly double Twist;
            private readonly Point3d _target;
            private readonly Point2d _center;
            private readonly double _halfWidth, _halfHeight;

            internal PlanViewport(Viewport viewport)
            {
                Twist = viewport.TwistAngle;
                _target = viewport.ViewTarget;
                _center = viewport.ViewCenter;
                _halfHeight = viewport.ViewHeight / 2;
                _halfWidth = _halfHeight * viewport.Width / viewport.Height;
            }

            internal bool Contains(Point3d anchor)
            {
                double dx = anchor.X - _target.X;
                double dy = anchor.Y - _target.Y;
                double c = Math.Cos(Twist), s = Math.Sin(Twist);
                double x = c * dx + s * dy - _center.X;
                double y = -s * dx + c * dy - _center.Y;
                return Math.Abs(x) <= _halfWidth && Math.Abs(y) <= _halfHeight;
            }
        }
    }
}
#endif
