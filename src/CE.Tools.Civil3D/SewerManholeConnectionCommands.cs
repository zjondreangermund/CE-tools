using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerManholeConnectionCommands))]

namespace CETools.Civil3D
{
    public sealed class SewerManholeConnectionCommands
    {
        [CommandMethod("CE_TOOLS", "CE_SEWINCOMINGPIPES", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void ShowManholeConnections()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            PromptSelectionResult selection = document.Editor.SelectImplied();
            bool all = false;
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                var scope = new PromptKeywordOptions("\nManholes to show [Selected/All] <All>: ") { AllowNone = true };
                scope.Keywords.Add("Selected");
                scope.Keywords.Add("All");
                scope.Keywords.Default = "All";
                PromptResult choice = document.Editor.GetKeywords(scope);
                if (choice.Status != PromptStatus.OK && choice.Status != PromptStatus.None) return;
                all = choice.Status == PromptStatus.None || choice.StringResult == "All";
                if (!all) selection = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect manholes, sewer pipes, networks or profile views: ",
                    AllowDuplicates = false
                });
            }
            var ids = new HashSet<ObjectId>();
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
            {
                if (all)
                {
                    foreach (ObjectId networkId in CivilApplication.ActiveDocument.GetPipeNetworkIds())
                        AddSelection(tr.GetObject(networkId, OpenMode.ForRead, false), ids);
                }
                else if (selection.Status == PromptStatus.OK && selection.Value != null)
                {
                    foreach (ObjectId id in selection.Value.GetObjectIds())
                        AddSelection(tr.GetObject(id, OpenMode.ForRead, false), ids);
                }
                else return;
            }
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            using (document.LockDocument()) EnableAndReport(document, ids, true);
        }

        internal static void EnableAndReport(Document document, IEnumerable<ObjectId> structureIds, bool report)
        {
            var ids = structureIds.Where(id => !id.IsNull && !id.IsErased).Distinct().ToList();
            int failed;
            int enabled = SewerManholeConnectionDisplay.Enable(document.Database, ids, out failed);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nManhole pipe connection outlines enabled={0}; display failures={1}. All connected pipe openings use their native elevations and sizes.", enabled, failed);
            if (!report || ids.Count == 0) return;

            var rows = new List<IList<string>>();
            using (Transaction tr = document.Database.TransactionManager.StartTransaction())
                foreach (ObjectId id in ids)
                {
                    var structure = tr.GetObject(id, OpenMode.ForRead, false) as Structure;
                    if (structure == null) continue;
                    var connections = new List<Connection>();
                    foreach (ObjectId pipeId in SewerPipeConnections.PipeIds(structure))
                    {
                        var pipe = tr.GetObject(pipeId, OpenMode.ForRead, false) as Pipe;
                        if (pipe == null) continue;
                        bool atStart = pipe.StartStructureId == id;
                        bool forward;
                        bool known = SewerPipeConnections.TryForward(pipe, tr, out forward);
                        connections.Add(new Connection
                        {
                            Name = pipe.Name, Invert = SewerPipeConnections.Invert(pipe, atStart),
                            Direction = !known ? "Unresolved" : (atStart != forward ? "IN" : "OUT")
                        });
                    }
                    double lowest = connections.Where(c => c.Direction == "IN").Select(c => c.Invert)
                        .DefaultIfEmpty(double.NaN).Min();
                    foreach (Connection connection in connections.OrderBy(c => c.Invert).ThenBy(c => c.Name))
                        rows.Add(new List<string>
                        {
                            structure.Name, connection.Name, connection.Direction,
                            connection.Invert.ToString("0.000", CultureInfo.InvariantCulture),
                            connection.Direction == "IN" && Math.Abs(connection.Invert - lowest) < 1e-6 ? "Lowest incoming" : ""
                        });
                    if (connections.Count == 0)
                        rows.Add(new List<string> { structure.Name, "", "", "", "No connected pipes" });
                }
            GridReportPresenter.ShowReportAndOfferTable(document,
                "CE Tools - Manhole Pipe Connections",
                "Each connected pipe has its own row. IN/OUT follows CE sequence or existing flow direction; the lowest incoming inside invert controls the outgoing gravity grade. Native profile outlines update with pipe geometry.",
                new[] { "Manhole", "Pipe", "In / Out", "Invert (m)", "Control" }, rows,
                "CE MANHOLE PIPE CONNECTIONS");
        }

        internal static void AddSelection(DBObject item, ISet<ObjectId> ids)
        {
            var structure = item as Structure;
            if (structure != null) { ids.Add(structure.ObjectId); return; }
            var pipe = item as Pipe;
            if (pipe != null)
            {
                if (!pipe.StartStructureId.IsNull) ids.Add(pipe.StartStructureId);
                if (!pipe.EndStructureId.IsNull) ids.Add(pipe.EndStructureId);
                return;
            }
            var network = item as Network;
            if (network != null) { foreach (ObjectId id in network.GetStructureIds()) ids.Add(id); return; }
            var view = item as ProfileView;
            if (view == null) return;
            using (StructureOverrideCollection overrides = view.StructureOverrides)
                foreach (StructureOverride entry in overrides)
                    if (entry.Draw) ids.Add(entry.StructId);
        }

        private sealed class Connection
        {
            internal string Name;
            internal string Direction;
            internal double Invert;
        }
    }

    internal static class SewerManholeConnectionDisplay
    {
        internal static void EnableForViews(Database database, IEnumerable<ObjectId> viewIds)
        {
            var structures = new HashSet<ObjectId>();
            using (Transaction tr = database.TransactionManager.StartTransaction())
                foreach (ObjectId id in viewIds.Distinct())
                    SewerManholeConnectionCommands.AddSelection(tr.GetObject(id, OpenMode.ForRead, false), structures);
            int failed;
            Enable(database, structures, out failed);
            if (failed > 0)
                AcApplication.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
                    "\nManhole connection display could not update {0} structure(s). Run CE_SEWINCOMINGPIPES to review.", failed);
        }

        internal static int Enable(Database database, IEnumerable<ObjectId> ids, out int failed)
        {
            failed = 0;
            int enabled = 0;
            foreach (ObjectId id in ids.Where(id => !id.IsNull && !id.IsErased).Distinct())
            {
                try
                {
                    using (Transaction tr = database.TransactionManager.StartTransaction())
                    {
                        var structure = tr.GetObject(id, OpenMode.ForWrite, false) as Structure;
                        if (structure == null || structure.IsReferenceObject) { failed++; continue; }
                        StructureStyleCollection styles = CivilDocument.GetCivilDocument(database).Styles.StructureStyles;
                        ObjectId currentStyle = !string.IsNullOrEmpty(structure.StyleName) && styles.Contains(structure.StyleName)
                            ? styles[structure.StyleName] : ObjectId.Null;
                        structure.StyleId = PrepareStyle(currentStyle, styles, tr);
                        // A view-specific override can hide outlines even when the
                        // structure's normal style shows them. Preserve its own
                        // appearance by cloning that override, too.
                        foreach (ObjectId viewId in structure.GetProfileViewsDisplayingMe())
                        {
                            var view = tr.GetObject(viewId, OpenMode.ForWrite, false) as ProfileView;
                            if (view == null || view.IsReferenceObject) continue;
                            using (StructureOverrideCollection overrides = view.StructureOverrides)
                                foreach (StructureOverride entry in overrides)
                                    if (entry.StructId == id && entry.UseOverrideStyle)
                                    {
                                        ObjectId original = ((GraphOverride)entry).OverrideStyleId;
                                        entry.OverrideStyleId = PrepareStyle(original, styles, tr);
                                    }
                            view.RecordGraphicsModified(true);
                        }
                        structure.RecordGraphicsModified(true);
                        tr.Commit();
                        enabled++;
                    }
                }
                catch (System.Exception exception)
                {
                    failed++;
                    AcApplication.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
                        "\nManhole {0} connection display: {1}", id.Handle, exception.Message);
                }
            }
            return enabled;
        }

        private static ObjectId PrepareStyle(ObjectId sourceId, StructureStyleCollection styles, Transaction tr)
        {
            const string prefix = "CE-PIPE-CONNECTIONS-";
            var source = sourceId.IsNull ? null : tr.GetObject(sourceId, OpenMode.ForWrite, false) as StructureStyle;
            string name = source == null ? prefix + "Basic" : CivilStyleNames.Get(source);
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) name = prefix + sourceId.Handle;
            ObjectId targetId = styles.Contains(name) ? styles[name] :
                source == null ? styles.Add(name) : source.CopyAsSibling(name);
            var target = (StructureStyle)tr.GetObject(targetId, OpenMode.ForWrite, false);
            DisplayStyle outlines = target.GetDisplayStyleProfile(StructureDisplayStyleProfileType.StructurePipeOutlines);
            outlines.Visible = true;
            outlines.Layer = "0";
            outlines.Color = Color.FromColorIndex(ColorMethod.ByAci, 3);
            return targetId;
        }
    }
}
