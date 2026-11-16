using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

[assembly: CommandClass(typeof(CETools.Civil3D.AlignmentFreeCurveCommands))]

namespace CETools.Civil3D
{
    public sealed class AlignmentFreeCurveCommands
    {
        private sealed class WorkItem
        {
            internal ObjectId AlignmentId;
            internal string Name;
            internal int EntityId;
        }

        [CommandMethod("CE_TOOLS", "CE_ALIGNCURVESFREE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ConvertCurves()
        {
            var document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            Editor editor = document.Editor;
            var mode = new PromptKeywordOptions("\nAlignments [All/Selected] <Selected>: ");
            mode.Keywords.Add("All");
            mode.Keywords.Add("Selected");
            mode.AllowNone = true;
            PromptResult answer = editor.GetKeywords(mode);
            if (answer.Status != PromptStatus.OK && answer.Status != PromptStatus.None) return;
            var ids = new HashSet<ObjectId>();
            if (answer.Status == PromptStatus.OK && answer.StringResult == "All")
            {
                CivilDocument civil = CivilDocument.GetCivilDocument(document.Database);
                foreach (ObjectId id in civil.GetAlignmentIds()) ids.Add(id);
            }
            else
            {
                PromptSelectionResult selection = editor.SelectImplied();
                if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
                    selection = editor.GetSelection(new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect multiple Civil 3D alignments: "
                    }, new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "AECC_ALIGNMENT") }));
                editor.SetImpliedSelection(new ObjectId[0]);
                if (selection.Status != PromptStatus.OK || selection.Value == null) return;
                foreach (ObjectId id in selection.Value.GetObjectIds()) ids.Add(id);
            }

            var work = new List<WorkItem>();
            int alreadyFree = 0, skipped = 0;
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    try
                    {
                        var alignment = tr.GetObject(id, OpenMode.ForRead, false) as CivilAlignment;
                        if (alignment == null) continue;
                        if (alignment.IsReferenceObject)
                        {
                            editor.WriteMessage("\n  {0}: reference alignment skipped.", alignment.Name);
                            skipped++;
                            continue;
                        }
                        foreach (AlignmentEntity entity in alignment.Entities)
                        {
                            var arc = entity as AlignmentArc;
                            if (arc == null)
                            {
                                if (!(entity is AlignmentLine))
                                {
                                    skipped++;
                                    editor.WriteMessage("\n  {0}, entity {1}: spiral/group geometry skipped.", alignment.Name, entity.EntityId);
                                }
                                continue;
                            }
                            if (arc.Constraint1 == AlignmentEntityConstraintType.Free) { alreadyFree++; continue; }
                            string reason = Eligibility(alignment.Entities, arc);
                            if (reason != null)
                            {
                                skipped++;
                                editor.WriteMessage("\n  {0}, curve {1}: {2}.", alignment.Name, arc.EntityId, reason);
                                continue;
                            }
                            work.Add(new WorkItem { AlignmentId = id, Name = alignment.Name, EntityId = arc.EntityId });
                        }
                    }
                    catch (System.Exception ex)
                    {
                        skipped++;
                        editor.WriteMessage("\n  Alignment could not be inspected: {0}", ex.Message);
                    }
                }
            }
            editor.WriteMessage("\nCE_ALIGNCURVESFREE: eligible curves={0}; already Free={1}; skipped items={2}.", work.Count, alreadyFree, skipped);
            if (work.Count == 0) return;
            editor.WriteMessage("\nRetains radius and turn direction. Tangency correction can move endpoints and change chainages; check dependent profiles/corridors and curve labels after conversion.");
            var confirm = new PromptKeywordOptions("\nConvert eligible curves to Constrained on Both Sides (Free)? [Yes/No] <No>: ");
            confirm.Keywords.Add("Yes"); confirm.Keywords.Add("No"); confirm.AllowNone = true;
            answer = editor.GetKeywords(confirm);
            if (answer.Status != PromptStatus.OK || answer.StringResult != "Yes") return;

            int converted = 0, failed = 0;
            foreach (WorkItem item in work)
            {
                // One transaction per curve: a rejected solve must restore the
                // original arc AND any neighbouring geometry changed by Civil 3D.
                try
                {
                    using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                    {
                        var alignment = tr.GetObject(item.AlignmentId, OpenMode.ForWrite, false) as CivilAlignment;
                        if (alignment == null || alignment.IsReferenceObject)
                            throw new InvalidOperationException("Alignment is no longer editable.");
                        ConvertOne(alignment.Entities, item.EntityId);
                        tr.Commit();
                    }
                    converted++;
                }
                catch (System.Exception ex)
                {
                    failed++;
                    editor.WriteMessage("\n  {0}, curve {1}: unchanged (rolled back): {2}", item.Name, item.EntityId, ex.Message);
                }
            }
            editor.Regen();
            editor.WriteMessage("\nCE_ALIGNCURVESFREE complete: converted={0}; already Free={1}; skipped={2}; failed/rolled back={3}.", converted, alreadyFree, skipped, failed);
        }

        internal static string Eligibility(AlignmentEntityCollection entities, AlignmentArc arc)
        {
            if (arc == null) return "not a standalone circular curve";
            if (arc.Constraint1 == AlignmentEntityConstraintType.Free) return "already Free";
            if (double.IsNaN(arc.Radius) || double.IsInfinity(arc.Radius) || arc.Radius <= 0.0)
                return "invalid radius";
            try
            {
                var before = entities.EntityAtId(arc.EntityBefore) as AlignmentLine;
                var after = entities.EntityAtId(arc.EntityAfter) as AlignmentLine;
                if (before == null || after == null || before.EntityId == after.EntityId)
                    return "requires a separate straight line on each side";
                if (before.Constraint1 != AlignmentEntityConstraintType.Fixed || after.Constraint1 != AlignmentEntityConstraintType.Fixed)
                    return "neighbouring lines must be Fixed; dependent geometry needs manual review";
                if (before.EntityAfter != arc.EntityId || after.EntityBefore != arc.EntityId)
                    return "neighbours are not connected to this curve";
            }
            catch { return "missing or disconnected neighbouring entities"; }
            return null;
        }

        internal static void ConvertOne(AlignmentEntityCollection entities, int entityId)
        {
            var arc = entities.EntityAtId(entityId) as AlignmentArc;
            string reason = Eligibility(entities, arc);
            if (reason != null) throw new InvalidOperationException(reason);
            int beforeId = arc.EntityBefore, afterId = arc.EntityAfter, count = entities.Count;
            double radius = arc.Radius;
            bool clockwise = arc.Clockwise, greater = arc.Length / arc.Radius > Math.PI;
            var before = (AlignmentLine)entities.EntityAtId(beforeId);
            var after = (AlignmentLine)entities.EntityAtId(afterId);
            var outerStart = before.StartPoint;
            var outerEnd = after.EndPoint;
            var beforeDirection = before.EndPoint - before.StartPoint;
            var afterDirection = after.EndPoint - after.StartPoint;
            // Constraint1 is read-only in Autodesk's managed API. Recreate ONLY
            // this arc using the documented radius-constrained free-curve overload.
            entities.Remove(arc);
            AlignmentArc replacement = entities.AddFreeCurve(beforeId, afterId, radius,
                CurveParamType.Radius, greater, CurveType.Compound);
            if (replacement == null || replacement.Constraint1 != AlignmentEntityConstraintType.Free ||
                entities.Count != count || replacement.Clockwise != clockwise ||
                replacement.GreaterThan180 != greater || Math.Abs(replacement.Radius - radius) > Math.Max(1e-7, radius * 1e-9) ||
                replacement.EntityBefore != beforeId || replacement.EntityAfter != afterId)
                throw new InvalidOperationException("Civil 3D did not retain radius, turn direction and connected Free geometry.");
            before = (AlignmentLine)entities.EntityAtId(beforeId);
            after = (AlignmentLine)entities.EntityAtId(afterId);
            const double tolerance = 1e-5;
            if (before.EntityAfter != replacement.EntityId || after.EntityBefore != replacement.EntityId ||
                before.EndPoint.GetDistanceTo(replacement.StartPoint) > tolerance ||
                after.StartPoint.GetDistanceTo(replacement.EndPoint) > tolerance ||
                before.StartPoint.GetDistanceTo(outerStart) > tolerance || after.EndPoint.GetDistanceTo(outerEnd) > tolerance)
                throw new InvalidOperationException("Conversion disconnected neighbours or moved an outer line endpoint.");
            var firstDirection = before.EndPoint - before.StartPoint;
            var lastDirection = after.EndPoint - after.StartPoint;
            if (firstDirection.Length <= tolerance || lastDirection.Length <= tolerance ||
                firstDirection.GetNormal().DotProduct(beforeDirection.GetNormal()) < 1.0 - 1e-8 ||
                lastDirection.GetNormal().DotProduct(afterDirection.GetNormal()) < 1.0 - 1e-8 ||
                Math.Abs(firstDirection.GetNormal().DotProduct((replacement.StartPoint - replacement.CenterPoint).GetNormal())) > 1e-7 ||
                Math.Abs(lastDirection.GetNormal().DotProduct((replacement.EndPoint - replacement.CenterPoint).GetNormal())) > 1e-7)
                throw new InvalidOperationException("Converted curve is not tangent to both original line directions.");
        }
    }
}
