using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ExistingNameUprightCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// V154 addition: normalize existing road/sewer TEXT or MTEXT to stay readable.
    /// This does not create viewports, edit road/profile sheets or alter text content.
    /// </summary>
    public sealed class ExistingNameUprightCommands
    {
        private static readonly string[] RoadLayers = { "CE-ROAD-NAME", "CE-ROAD-NAMES", "CE-ROADNAME" };
        private static readonly string[] SewerLayers = { "CE-SEWER-BRANCH-NAME", "CE-SEWER-BRANCH-NAMES", "CE-SEWER-BRANCH-LABELS" };

        [CommandMethod("CE_TOOLS", "CE_ROADNAMEUPRIGHT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RoadNamesUpright() { Process("Road", false, false); }

        [CommandMethod("CE_TOOLS", "CE_SEWBRANCHUPRIGHT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SewerBranchNamesUpright() { Process("Sewer", false, false); }

        [CommandMethod("CE_TOOLS", "CE_ROADTEXTROTATE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RoadTextRotate()
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            using (var dialog = new RoadTextRotateDialog())
            {
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                Process(dialog.TextType, dialog.SelectedViewports, true);
            }
            return;
            // Previous command line prompts retained below for reference only.
            var type = new PromptKeywordOptions("\nText type [Road/Sewer/Both] <Both>: ");
            type.Keywords.Add("Road"); type.Keywords.Add("Sewer"); type.Keywords.Add("Both");
            type.Keywords.Default = "Both"; type.AllowNone = true;
            PromptResult choice = doc.Editor.GetKeywords(type);
            if (choice.Status == PromptStatus.Cancel) return;
            string selectedType = choice.Status == PromptStatus.OK ? choice.StringResult : "Both";
            var scope = new PromptKeywordOptions("\nText scope [All/Viewport] <All>: ");
            scope.Keywords.Add("All"); scope.Keywords.Add("Viewport");
            scope.Keywords.Default = "All"; scope.AllowNone = true;
            PromptResult selectedScope = doc.Editor.GetKeywords(scope);
            if (selectedScope.Status == PromptStatus.Cancel) return;
            bool selectedViewports = selectedScope.Status == PromptStatus.OK &&
                string.Equals(selectedScope.StringResult, "Viewport", StringComparison.OrdinalIgnoreCase);
            Process(selectedType, selectedViewports, true);
        }

        private static bool EligibleLayer(string layer, string textType)
        {
            return ((textType == "Road" || textType == "Both") &&
                    RoadLayers.Any(x => string.Equals(x, layer, StringComparison.OrdinalIgnoreCase))) ||
                   ((textType == "Sewer" || textType == "Both") &&
                    SewerLayers.Any(x => string.Equals(x, layer, StringComparison.OrdinalIgnoreCase)));
        }

        private static double Upright(double angle)
        {
            const double twoPi = Math.PI * 2.0;
            angle %= twoPi;
            if (angle < 0) angle += twoPi;
            return angle > Math.PI / 2 && angle < 3 * Math.PI / 2
                ? angle + Math.PI : angle;
        }

        private static void Process(string textType, bool selectedViewports, bool interactive)
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var viewports = new List<ViewportBounds>();
            if (selectedViewports)
            {
                PromptSelectionResult selection = doc.Editor.GetSelection(
                    new PromptSelectionOptions { MessageForAdding = "\nSelect paper-space viewport frames: " },
                    new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "VIEWPORT") }));
                if (selection.Status != PromptStatus.OK) return;
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in selection.Value.GetObjectIds())
                    {
                        Viewport vp = tr.GetObject(id, OpenMode.ForRead, false) as Viewport;
                        if (vp != null && vp.Number > 1)
                            viewports.Add(new ViewportBounds(vp));
                    }
                }
                if (viewports.Count == 0)
                {
                    doc.Editor.WriteMessage("\nNo valid paper-space viewports selected.");
                    return;
                }
            }

            int changed = 0, retained = 0, skipped = 0;
            using (doc.LockDocument())
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                BlockTable bt = tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead) as BlockTable;
                BlockTableRecord model = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                foreach (ObjectId id in model)
                {
                    Entity entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || !(entity is DBText || entity is MText) ||
                        !EligibleLayer(entity.Layer, textType)) continue;
                    LayerTableRecord layer = tr.GetObject(entity.LayerId, OpenMode.ForRead) as LayerTableRecord;
                    if (layer == null || layer.IsLocked) { skipped++; continue; }
                    Point3d anchor;
                    try
                    {
                        Extents3d ext = entity.GeometricExtents;
                        anchor = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) * .5,
                            (ext.MinPoint.Y + ext.MaxPoint.Y) * .5,
                            (ext.MinPoint.Z + ext.MaxPoint.Z) * .5);
                    }
                    catch { skipped++; continue; }
                    double twist = 0;
                    if (selectedViewports)
                    {
                        ViewportBounds match = viewports.FirstOrDefault(v => v.Contains(anchor));
                        if (match == null) { skipped++; continue; }
                        twist = match.Twist;
                    }
                    else if (interactive)
                    {
                        // All mode does not assume a particular viewport; keep the
                        // text legible in its unrotated model-space orientation.
                        twist = 0;
                    }
                    double angle = entity is DBText ? ((DBText)entity).Rotation : ((MText)entity).Rotation;
                    double effective = angle - twist;
                    double corrected = Upright(effective) + twist;
                    if (Math.Abs(Math.Sin((corrected - angle) / 2)) < 0.000001) { retained++; continue; }
                    entity.UpgradeOpen();
                    if (entity is DBText) ((DBText)entity).Rotation = corrected;
                    else ((MText)entity).Rotation = corrected;
                    changed++;
                }
                tr.Commit();
            }
            doc.Editor.WriteMessage("\nCE text rotation: changed={0}; already upright={1}; skipped={2}.",
                changed, retained, skipped);
        }

        private sealed class RoadTextRotateDialog : System.Windows.Forms.Form
        {
            private readonly System.Windows.Forms.ComboBox _type;
            private readonly System.Windows.Forms.CheckBox _viewports;
            internal string TextType { get { return _type.SelectedItem as string ?? "Both"; } }
            internal bool SelectedViewports { get { return _viewports.Checked; } }
            internal RoadTextRotateDialog()
            {
                Text = "CE Tools — Road / Sewer Text Rotation";
                Width = 480; Height = 235;
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
                StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
                MaximizeBox = false; MinimizeBox = false;
                var heading = new System.Windows.Forms.Label {
                    Text = "Rotate existing road names and sewer branch text upright",
                    Left = 16, Top = 16, Width = 435, Height = 32 };
                Controls.Add(heading);
                Controls.Add(new System.Windows.Forms.Label {
                    Text = "Text type", Left = 16, Top = 59, Width = 125 });
                _type = new System.Windows.Forms.ComboBox {
                    Left = 155, Top = 54, Width = 280,
                    DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList };
                _type.Items.AddRange(new object[] { "Both", "Road", "Sewer" });
                _type.SelectedIndex = 0;
                Controls.Add(_type);
                _viewports = new System.Windows.Forms.CheckBox {
                    Text = "Select viewport(s) instead of all matching text",
                    Left = 16, Top = 102, Width = 430 };
                Controls.Add(_viewports);
                var ok = new System.Windows.Forms.Button {
                    Text = "Apply", Left = 250, Top = 144,
                    Width = 90, DialogResult = System.Windows.Forms.DialogResult.OK };
                var cancel = new System.Windows.Forms.Button {
                    Text = "Cancel", Left = 347, Top = 144,
                    Width = 90, DialogResult = System.Windows.Forms.DialogResult.Cancel };
                Controls.Add(ok); Controls.Add(cancel);
                AcceptButton = ok; CancelButton = cancel;
            }
        }

        private sealed class ViewportBounds
        {
            private readonly Point3d _target;
            private readonly Point2d _center;
            private readonly double _width, _height, _viewHeight;
            internal readonly double Twist;
            internal ViewportBounds(Viewport vp)
            {
                _target = vp.ViewTarget;
                _center = vp.ViewCenter;
                _width = vp.Width;
                _height = vp.Height;
                _viewHeight = vp.ViewHeight;
                Twist = vp.TwistAngle;
            }
            internal bool Contains(Point3d point)
            {
                // Convert into the viewport's display plane and test its visible
                // model-space window, without changing the viewport itself.
                Vector3d difference = point - _target;
                double cosine = Math.Cos(Twist), sine = Math.Sin(Twist);
                double u = difference.X * cosine + difference.Y * sine;
                double v = -difference.X * sine + difference.Y * cosine;
                double worldWidth = _viewHeight * _width / Math.Max(_height, 0.000001);
                return Math.Abs(u - _center.X) <= worldWidth / 2 &&
                       Math.Abs(v - _center.Y) <= _viewHeight / 2;
            }
        }
    }
}
