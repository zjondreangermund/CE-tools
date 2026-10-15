using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfileView = Autodesk.Civil.DatabaseServices.ProfileView;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.AlignmentProfileJumpCommands))]
namespace CETools.Civil3D
{
    // V154 jump/locate command. Standalone to avoid modifications to production sheets.
    public sealed class AlignmentProfileJumpCommands
    {
        [CommandMethod("CE_ALPROFILEJUMP", CommandFlags.Modal | CommandFlags.Redraw)]
        public void JumpBetweenAlignmentAndProfile()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            Editor editor = document.Editor;
            editor.WriteMessage("\nCE_ALPROFILEJUMP: select alignment or profile view, then point to locate. Esc exits.");
            while (true)
            {
                PromptEntityOptions options = new PromptEntityOptions("\nSelect alignment or profile view: ");
                options.SetRejectMessage("\nSelect a Civil 3D alignment or profile view.");
                options.AddAllowedClass(typeof(CivilAlignment), false);
                options.AddAllowedClass(typeof(CivilProfileView), false);
                PromptEntityResult item = editor.GetEntity(options);
                if (item.Status != PromptStatus.OK) return;
                PromptPointResult point = editor.GetPoint("\nPick exact station/point: ");
                if (point.Status != PromptStatus.OK) return;
                Point3d target = Point3d.Origin;
                double station = 0, elevation = 0;
                bool resolved = false;
                try
                {
                    using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                    {
                        DBObject source = tr.GetObject(item.ObjectId, OpenMode.ForRead, false);
                        CivilAlignment alignment = source as CivilAlignment;
                        CivilProfileView profileView = source as CivilProfileView;
                        if (alignment != null)
                        {
                            double offset = 0;
                            try { alignment.StationOffset(point.Value.X, point.Value.Y, ref station, ref offset); }
                            catch { editor.WriteMessage("\nCould not find station on selected alignment."); continue; }
                            profileView = FindProfileView(tr, document.Database, alignment.ObjectId);
                            if (profileView == null)
                            {
                                editor.WriteMessage("\nNo associated Civil 3D profile view found."); continue;
                            }
                            elevation = (profileView.ElevationMin + profileView.ElevationMax) / 2.0;
                            resolved = TryStationToXY(profileView, station, elevation, out target);
                        }
                        else if (profileView != null)
                        {
                            if (!TryXYToStation(profileView, point.Value, out station, out elevation))
                            {
                                editor.WriteMessage("\nCould not determine station/elevation in profile."); continue;
                            }
                            alignment = tr.GetObject(profileView.AlignmentId, OpenMode.ForRead, false) as CivilAlignment;
                            if (alignment == null) continue;
                            double x = 0, y = 0;
                            try { alignment.PointLocation(station, 0.0, ref x, ref y); }
                            catch { editor.WriteMessage("\nStation is outside the alignment."); continue; }
                            target = new Point3d(x, y, 0);
                            resolved = true;
                        }
                        tr.Commit();
                    }
                    if (!resolved) { editor.WriteMessage("\nCould not resolve jump target."); continue; }
                    CentreView(editor, target);
                    editor.WriteMessage("\nCE_ALPROFILEJUMP located chainage {0}.",
                        station.ToString("0.###", CultureInfo.CurrentCulture));
                }
                catch (System.Exception error) { editor.WriteMessage("\nCE_ALPROFILEJUMP: {0}", error.Message); }
            }
        }
        private static CivilProfileView FindProfileView(Transaction tr, Database db, ObjectId alignmentId)
        {
            BlockTable table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId spaceId in table)
            {
                BlockTableRecord space = tr.GetObject(spaceId, OpenMode.ForRead, false) as BlockTableRecord;
                if (space == null) continue;
                foreach (ObjectId id in space)
                {
                    CivilProfileView view = tr.GetObject(id, OpenMode.ForRead, false) as CivilProfileView;
                    if (view == null) continue;
                    try { if (view.AlignmentId == alignmentId) return view; } catch { }
                }
            }
            return null;
        }
        private static bool TryXYToStation(CivilProfileView view, Point3d p, out double station, out double elev)
        {
            station = elev = 0;
            foreach (string methodName in new[] { "FindStationAndElevationAtXY", "GetStationAndElevationAtXY" })
            {
                foreach (MethodInfo method in view.GetType().GetMethods().Where(x => x.Name == methodName))
                {
                    ParameterInfo[] ps = method.GetParameters();
                    if (ps.Length != 4 || ps[0].ParameterType != typeof(double)) continue;
                    try
                    {
                        object[] args = { p.X, p.Y, 0.0, 0.0 };
                        object result = method.Invoke(view, args);
                        if (result is bool && !(bool)result) continue;
                        station = Convert.ToDouble(args[2], CultureInfo.InvariantCulture);
                        elev = Convert.ToDouble(args[3], CultureInfo.InvariantCulture);
                        return !double.IsNaN(station) && !double.IsNaN(elev);
                    }
                    catch { }
                }
            }
            return false;
        }
        private static bool TryStationToXY(CivilProfileView view, double station, double elev, out Point3d target)
        {
            target = Point3d.Origin;
            foreach (string methodName in new[] { "FindXYAtStationAndElevation", "GetXYAtStationAndElevation" })
            {
                foreach (MethodInfo method in view.GetType().GetMethods().Where(x => x.Name == methodName))
                {
                    if (method.GetParameters().Length != 4) continue;
                    try
                    {
                        object[] args = { station, elev, 0.0, 0.0 };
                        object result = method.Invoke(view, args);
                        if (result is bool && !(bool)result) continue;
                        target = new Point3d(Convert.ToDouble(args[2], CultureInfo.InvariantCulture),
                            Convert.ToDouble(args[3], CultureInfo.InvariantCulture), 0);
                        return true;
                    }
                    catch { }
                }
            }
            return false;
        }
        private static void CentreView(Editor editor, Point3d target)
        {
            // Update the view only after the transaction has ended, as in V154.
            using (ViewTableRecord view = editor.GetCurrentView())
            {
                view.Width = Math.Max(view.Width * 0.15, 0.01);
                view.Height = Math.Max(view.Height * 0.15, 0.01);
                view.Target = target;
                view.CenterPoint = Point2d.Origin;
                editor.SetCurrentView(view);
            }
        }
    }
}
