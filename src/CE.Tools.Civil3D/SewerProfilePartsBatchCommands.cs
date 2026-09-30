using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerProfilePartsBatchCommands))]

namespace CETools.Civil3D
{
    public sealed class SewerProfilePartsBatchCommands
    {
        private static readonly HashSet<string> LastSelectedNetworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool LastMatchAlignment = true;
        private static bool LastOpenLabels = true;

        [CommandMethod("CE_TOOLS", "CE_SEWSELECTEDPARTSPROFILEMULTI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void DrawSelectedPartsInMatchingViews()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;
            List<NetworkChoice> networks = ReadNetworks(document.Database, civil);
            if (networks.Count == 0) return;

            PromptSelectionResult partSelection = document.Editor.SelectImplied();
            if (partSelection.Status != PromptStatus.OK || partSelection.Value == null || partSelection.Value.Count == 0)
                partSelection = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect multiple sewer pipes and structures to draw: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            document.Editor.SetImpliedSelection(new ObjectId[0]);
            if (partSelection.Status != PromptStatus.OK || partSelection.Value == null) return;
            var selectedIds = new HashSet<ObjectId>(partSelection.Value.GetObjectIds());
            var partNetworks = new Dictionary<ObjectId, NetworkChoice>();
            foreach (NetworkChoice network in networks)
                foreach (ObjectId id in ReadParts(document.Database, network.Id))
                    if (selectedIds.Contains(id)) partNetworks[id] = network;
            if (partNetworks.Count == 0)
            {
                document.Editor.WriteMessage("\nNo gravity-network pipes or structures were selected.");
                return;
            }

            PromptSelectionResult viewSelection = document.Editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect multiple destination sewer profile views: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (viewSelection.Status != PromptStatus.OK || viewSelection.Value == null) return;
            var views = new List<ViewChoice>();
            using (Transaction read = document.Database.TransactionManager.StartTransaction())
                foreach (ObjectId id in viewSelection.Value.GetObjectIds().Distinct())
                {
                    try
                    {
                        ProfileView view = read.GetObject(id, OpenMode.ForRead, false) as ProfileView;
                        if (view != null && !view.IsReferenceObject)
                            views.Add(new ViewChoice(id, view.AlignmentId));
                    }
                    catch { }
                }
            if (views.Count == 0) return;

            int drawn = 0, unmatched = 0, rejected = 0, removed = 0;
            using (DocumentLock documentLock = document.LockDocument())
                foreach (var pair in partNetworks)
                    foreach (ViewChoice view in views)
                    {
                        // A network with no assigned alignment cannot safely
                        // be placed into an arbitrary selected profile view.
                        if (!MatchesView(document.Database, pair.Key, pair.Value, view))
                        {
                            unmatched++;
                            if (RemoveFromWrongView(document.Database, pair.Key, view.Id)) removed++;
                            continue;
                        }
                        try
                        {
                            using (Transaction write = document.Database.TransactionManager.StartTransaction())
                            {
                                DBObject part = write.GetObject(pair.Key, OpenMode.ForWrite, false);
                                MethodInfo add = part.GetType().GetMethod("AddToProfileView",
                                    BindingFlags.Public | BindingFlags.Instance, null,
                                    new[] { typeof(ObjectId) }, null);
                                if (add == null) { rejected++; continue; }
                                add.Invoke(part, new object[] { view.Id });
                                write.Commit();
                                drawn++;
                            }
                        }
                        catch { rejected++; }
                    }
            SewerManholeConnectionDisplay.EnableForViews(document.Database, views.Select(view => view.Id));
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_SEWSELECTEDPARTSPROFILEMULTI complete. Selected parts={0}; views={1}; drawn={2}; branch mismatches={3}; already present/rejected={4}; misplaced parts removed={5}.",
                partNetworks.Count, views.Count, drawn, unmatched, rejected, removed);
        }

        private static bool MatchesView(Database database, ObjectId partId,
            NetworkChoice network, ViewChoice view)
        {
            if (view.AlignmentId.IsNull) return false;
            try
            {
                using (Transaction read = database.TransactionManager.StartTransaction())
                {
                    Alignment alignment = read.GetObject(view.AlignmentId, OpenMode.ForRead, false) as Alignment;
                    DBObject part = read.GetObject(partId, OpenMode.ForRead, false);
                    if (alignment == null || part == null) return false;

                    // Per-part reference alignment is authoritative once the sewer
                    // branch references have been assigned. This prevents a Branch-2
                    // part from being drawn into Branch-1 merely because its plan
                    // geometry happens to sit close to both alignments at a junction.
                    Part civilPart = part as Part;
                    if (civilPart != null &&
                        !civilPart.RefAlignmentId.IsNull &&
                        !civilPart.RefAlignmentId.IsErased)
                        return civilPart.RefAlignmentId == view.AlignmentId;

                    var pipe = part as Pipe;
                    if (pipe != null)
                    {
                        // Legacy/fallback geometry match for drawings that have not
                        // yet run automatic branch reference assignment.
                        return OnBranch(alignment, pipe.StartPoint) &&
                               OnBranch(alignment, pipe.EndPoint);
                    }
                    var structure = part as Structure;
                    return structure != null && OnBranch(alignment, structure.Position);
                }
            }
            catch { return false; }
        }

        private static bool OnBranch(Alignment alignment, Point3d point)
        {
            double station = 0, offset = 0;
            alignment.StationOffset(point.X, point.Y, ref station, ref offset);
            return station >= alignment.StartingStation - 0.01 &&
                   station <= alignment.EndingStation + 0.01 &&
                   Math.Abs(offset) <= 1.0;
        }

        [CommandMethod("CE_TOOLS", "CE_SEWPROFILEPARTSMULTI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void DrawSelectedNetworksInProfileViews()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (document == null || civil == null) return;

            List<NetworkChoice> networks = ReadNetworks(document.Database, civil);
            if (networks.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_SEWPROFILEPARTSMULTI: no gravity pipe networks found.");
                return;
            }
            var picker = new NetworkPicker(networks);
            AcApplication.ShowModalWindow(picker);
            if (picker.DialogResult != true) return;
            List<NetworkChoice> chosen = picker.SelectedNetworks;
            if (chosen.Count == 0) return;
            LastSelectedNetworks.Clear();
            foreach (NetworkChoice item in chosen) LastSelectedNetworks.Add(item.Name);
            LastMatchAlignment = picker.MatchAlignment;
            LastOpenLabels = picker.OpenLabels;

            PromptSelectionResult selection = document.Editor.SelectImplied();
            if (selection.Status == PromptStatus.OK && selection.Value != null && selection.Value.Count > 0)
                document.Editor.SetImpliedSelection(new ObjectId[0]);
            else
                selection = document.Editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect multiple profile views to draw the chosen sewer networks: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            var views = new List<ViewChoice>();
            using (Transaction read = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    try
                    {
                        ProfileView view = read.GetObject(id, OpenMode.ForRead, false) as ProfileView;
                        if (view != null && !view.IsReferenceObject)
                            views.Add(new ViewChoice(id, view.AlignmentId));
                    }
                    catch { }
                }
            }
            if (views.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_SEWPROFILEPARTSMULTI: no editable profile views selected.");
                return;
            }

            int drawn = 0, skipped = 0, unmatched = 0, removed = 0;
            using (DocumentLock documentLock = document.LockDocument())
            {
                foreach (NetworkChoice network in chosen)
                {
                    List<ObjectId> parts = ReadParts(document.Database, network.Id);
                    foreach (ViewChoice view in views)
                    {
                        foreach (ObjectId partId in parts)
                        {
                            if (!MatchesView(document.Database, partId, network, view))
                            {
                                unmatched++;
                                if (RemoveFromWrongView(document.Database, partId, view.Id)) removed++;
                                continue;
                            }
                            try
                            {
                                // Civil 3D opens the profile view internally. One
                                // committed native operation per part prevents a
                                // bad part from rolling back other networks.
                                using (Transaction write = document.Database.TransactionManager.StartTransaction())
                                {
                                    DBObject part = write.GetObject(partId, OpenMode.ForWrite, false);
                                    MethodInfo add = part.GetType().GetMethod("AddToProfileView",
                                        BindingFlags.Public | BindingFlags.Instance, null,
                                        new[] { typeof(ObjectId) }, null);
                                    if (add == null) { skipped++; continue; }
                                    add.Invoke(part, new object[] { view.Id });
                                    write.Commit();
                                    drawn++;
                                }
                            }
                            catch { skipped++; }
                        }
                    }
                }
            }
            SewerManholeConnectionDisplay.EnableForViews(document.Database, views.Select(view => view.Id));
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_SEWPROFILEPARTSMULTI complete. Networks={0}; profile views={1}; parts drawn={2}; already present/rejected={3}; branch mismatches={4}; misplaced parts removed={5}. Pipe elevations, slopes, cover and rules were not changed.",
                chosen.Count, views.Count, drawn, skipped, unmatched, removed);
            if (picker.OpenLabels)
            {
                document.Editor.SetImpliedSelection(views.Select(item => item.Id).ToArray());
                document.SendStringToExecute("CE_SEWPROFILELABELSTYLESMULTI ", true, false, true);
            }
        }

        private static bool RemoveFromWrongView(Database database, ObjectId partId, ObjectId viewId)
        {
            try
            {
                using (Transaction write = database.TransactionManager.StartTransaction())
                {
                    DBObject part = write.GetObject(partId, OpenMode.ForWrite, false);
                    MethodInfo displayed = part.GetType().GetMethod("GetProfileViewsDisplayingMe", Type.EmptyTypes);
                    object ids = displayed == null ? null : displayed.Invoke(part, null);
                    var collection = ids as System.Collections.IEnumerable;
                    if (collection == null || !collection.Cast<object>().Any(value =>
                        value is ObjectId && (ObjectId)value == viewId)) return false;
                    MethodInfo remove = part.GetType().GetMethod("RemoveFromProfileView",
                        new[] { typeof(ObjectId) });
                    if (remove == null) return false;
                    remove.Invoke(part, new object[] { viewId });
                    write.Commit();
                    return true;
                }
            }
            catch { return false; }
        }

        private static List<NetworkChoice> ReadNetworks(Database database, CivilDocument civil)
        {
            var result = new List<NetworkChoice>();
            using (Transaction read = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetPipeNetworkIds())
                {
                    try
                    {
                        CivilNetwork network = read.GetObject(id, OpenMode.ForRead, false) as CivilNetwork;
                        if (network == null) continue;
                        // An unassigned reference alignment is normal for a
                        // gravity network. Civil 3D 2023 can throw from this
                        // getter; do not discard the entire valid network.
                        ObjectId alignmentId = ObjectId.Null;
                        try { alignmentId = network.ReferenceAlignmentId; }
                        catch { }
                        result.Add(new NetworkChoice(id, network.Name, alignmentId));
                    }
                    catch { }
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static List<ObjectId> ReadParts(Database database, ObjectId networkId)
        {
            using (Transaction read = database.TransactionManager.StartTransaction())
            {
                CivilNetwork network = read.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                if (network == null) return new List<ObjectId>();
                return network.GetPipeIds().Cast<ObjectId>()
                    .Concat(network.GetStructureIds().Cast<ObjectId>()).Distinct().ToList();
            }
        }

        private sealed class NetworkChoice
        {
            internal NetworkChoice(ObjectId id, string name, ObjectId alignmentId)
            { Id = id; Name = name; AlignmentId = alignmentId; }
            internal readonly ObjectId Id;
            internal readonly string Name;
            internal readonly ObjectId AlignmentId;
        }

        private sealed class ViewChoice
        {
            internal ViewChoice(ObjectId id, ObjectId alignmentId)
            { Id = id; AlignmentId = alignmentId; }
            internal readonly ObjectId Id;
            internal readonly ObjectId AlignmentId;
        }

        private sealed class NetworkPicker : Window
        {
            private readonly List<NetworkChoice> _networks;
            private readonly ListBox _networkList = new ListBox
            {
                SelectionMode = System.Windows.Controls.SelectionMode.Multiple,
                MinHeight = 120
            };
            private readonly CheckBox _match;
            private readonly System.Windows.Controls.Primitives.ToggleButton _labels;

            internal NetworkPicker(List<NetworkChoice> networks)
            {
                _networks = networks;
                Title = "CE Tools - Draw Multiple Sewer Networks in Profile Views";
                Width = 550; Height = 520; MinWidth = 440; MinHeight = 360;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                var root = new DockPanel { Margin = new Thickness(16) };
                Content = root;
                var footer = new StackPanel { Orientation = Orientation.Vertical };
                DockPanel.SetDock(footer, Dock.Bottom);
                root.Children.Add(footer);
                _match = new CheckBox { Content = "Only draw parts on each profile view's branch (required)", IsChecked = true, IsEnabled = false, Margin = new Thickness(0, 8, 0, 5) };
                footer.Children.Add(_match);
                _labels = new System.Windows.Controls.Primitives.ToggleButton
                {
                    Content = "Open label popup after drawing: " + (LastOpenLabels ? "Yes" : "No"),
                    IsChecked = LastOpenLabels,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 0, 12)
                };
                _labels.Click += (sender, args) =>
                    _labels.Content = "Open label popup after drawing: " + (_labels.IsChecked == true ? "Yes" : "No");
                footer.Children.Add(_labels);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var apply = new Button { Content = "Draw in selected profile views", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
                apply.Click += (sender, args) => { if (_networkList.SelectedItems.Count > 0) DialogResult = true; };
                buttons.Children.Add(apply);
                var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 6, 12, 6) };
                cancel.Click += (sender, args) => DialogResult = false;
                buttons.Children.Add(cancel);
                footer.Children.Add(buttons);
                var list = new DockPanel();
                var selectionActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                DockPanel.SetDock(selectionActions, Dock.Top);
                selectionActions.Children.Add(new TextBlock { Text = "Select gravity networks (Ctrl or Shift for several):", FontSize = 15, Margin = new Thickness(0, 0, 12, 0) });
                var all = new Button { Content = "Select all", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) };
                all.Click += (sender, args) => _networkList.SelectAll();
                selectionActions.Children.Add(all);
                var none = new Button { Content = "Clear", Padding = new Thickness(8, 3, 8, 3) };
                none.Click += (sender, args) => _networkList.UnselectAll();
                selectionActions.Children.Add(none);
                list.Children.Add(selectionActions);
                foreach (NetworkChoice network in networks)
                {
                    var item = new ListBoxItem { Content = network.Name, Padding = new Thickness(10, 8, 10, 8), Tag = network };
                    _networkList.Items.Add(item);
                    if (LastSelectedNetworks.Count == 0 || LastSelectedNetworks.Contains(network.Name))
                        item.IsSelected = true;
                }
                list.Children.Add(_networkList);
                root.Children.Add(list);
            }
            internal List<NetworkChoice> SelectedNetworks =>
                _networkList.SelectedItems.Cast<ListBoxItem>().Select(item => (NetworkChoice)item.Tag).ToList();
            internal bool MatchAlignment => _match.IsChecked == true;
            internal bool OpenLabels => _labels.IsChecked == true;
        }
    }
}
