using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August16RoadReserveCloseCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates genuinely closed lightweight-polyline plot/reserve boundaries for
    /// the Road Layout workflow. The command is deliberately separate from the
    /// road-centreline generator: prepare the cadastral/reserve boundaries here,
    /// then pass the closed boundaries to CE_ROADRESERVECENTERLINES.
    /// </summary>
    public sealed class August16RoadReserveCloseCommands
    {
        [CommandMethod("CE_TOOLS", "CE_ROADRESERVECLOSE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ClosePlotAndReservePolylines()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var settings = new ProductionSettingsDialogModel(
                "CE Tools - Closed Polylines for All Plots / Road Reserves",
                "Close multiple open lightweight-polylines by connecting their last endpoint back to the first endpoint. Existing vertices are preserved. Use the maximum endpoint gap to prevent an unintended long closing segment.");
            settings.AddChoice(
                "Scope",
                "01 Selection",
                "Source polylines",
                "Selected",
                "Process selected open polylines or every eligible open lightweight-polyline in the current space.",
                new[] { "Selected", "All" });
            settings.AddDouble(
                "MaxGap",
                "02 Closure",
                "Maximum endpoint gap (0 = any)",
                2.0,
                "Only close a polyline when the plan distance between first and last endpoints is within this value. Use 0 only when every selected polyline is known to require closure.");
            settings.AddChoice(
                "Handling",
                "03 Output",
                "Source handling",
                "Create closed copies",
                "Create closed copies and retain the source geometry, or close the selected source polylines in place.",
                new[] { "Create closed copies", "Close source polylines" });
            if (!DisciplineWorkflowDialogs.EditSettings(settings)) return;

            List<ObjectId> sourceIds = ResolveSources(document, settings.Text("Scope"));
            if (sourceIds.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_ROADRESERVECLOSE cancelled. No lightweight-polylines were selected/found.");
                return;
            }

            double maximumGap = Math.Max(0.0, settings.Double("MaxGap", 2.0));
            bool createCopies = string.Equals(
                settings.Text("Handling"),
                "Create closed copies",
                StringComparison.OrdinalIgnoreCase);

            int closed = 0;
            int alreadyClosed = 0;
            int tooFewVertices = 0;
            int gapRejected = 0;
            int failed = 0;

            using (DocumentLock documentLock = document.LockDocument())
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    document.Database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (space == null) return;

                foreach (ObjectId id in sourceIds.Distinct())
                {
                    Polyline source;
                    try { source = transaction.GetObject(id, createCopies ? OpenMode.ForRead : OpenMode.ForWrite, false) as Polyline; }
                    catch { failed++; continue; }
                    if (source == null) { failed++; continue; }
                    if (source.Closed) { alreadyClosed++; continue; }
                    if (source.NumberOfVertices < 3) { tooFewVertices++; continue; }

                    Point3d first;
                    Point3d last;
                    try
                    {
                        first = source.GetPoint3dAt(0);
                        last = source.GetPoint3dAt(source.NumberOfVertices - 1);
                    }
                    catch { failed++; continue; }

                    double dx = first.X - last.X;
                    double dy = first.Y - last.Y;
                    double gap = Math.Sqrt((dx * dx) + (dy * dy));
                    if (maximumGap > 0.0 && gap > maximumGap)
                    {
                        gapRejected++;
                        continue;
                    }

                    try
                    {
                        if (createCopies)
                        {
                            Polyline output = source.Clone() as Polyline;
                            if (output == null) { failed++; continue; }
                            output.SetDatabaseDefaults(document.Database);
                            output.LayerId = source.LayerId;
                            output.Closed = true;
                            space.AppendEntity(output);
                            transaction.AddNewlyCreatedDBObject(output, true);
                        }
                        else
                        {
                            source.Closed = true;
                        }
                        closed++;
                    }
                    catch { failed++; }
                }
                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_ROADRESERVECLOSE complete. Closed={0}; already closed={1}; gap rejected={2}; fewer than 3 vertices={3}; failed={4}. Next: CE_ROADRESERVECENTERLINES.",
                closed,
                alreadyClosed,
                gapRejected,
                tooFewVertices,
                failed);
        }

        // Backward compatibility for Road Production packages that still expose
        // the retired CE_ROADOVERLAY button. This calls the real current road
        // reserve-centreline implementation directly rather than dispatching an
        // unknown command string.
        [CommandMethod("CE_TOOLS", "CE_ROADOVERLAY", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void LegacyRoadOverlay()
        {
            new RoadLayoutProductionCommands().ReserveCenterlines();
        }

        private static List<ObjectId> ResolveSources(Document document, string scope)
        {
            if (document == null) return new List<ObjectId>();
            if (string.Equals(scope, "All", StringComparison.OrdinalIgnoreCase))
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord space = transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (space == null) return new List<ObjectId>();
                    return space.Cast<ObjectId>().Where(id =>
                    {
                        try { return transaction.GetObject(id, OpenMode.ForRead, false) is Polyline; }
                        catch { return false; }
                    }).ToList();
                }
            }

            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                selection = document.Editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect open plot / road-reserve lightweight-polylines to close: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    },
                    new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") }));
            }
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            if (selection.Status != PromptStatus.OK || selection.Value == null)
                return new List<ObjectId>();
            return selection.Value.GetObjectIds().Where(id => !id.IsNull && !id.IsErased).ToList();
        }
    }
}
