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

            int drawn = 0, unmatched = 0, rejected = 0;
            using (DocumentLock documentLock = document.LockDocument())
                foreach (var pair in partNetworks)
                    foreach (ViewChoice view in views)
                    {
                        // A network with no assigned alignment cannot safely
                        // be placed into an arbitrary selected profile view.
                        if (!MatchesView(document.Database, pair.Key, pair.Value, view))
                        {
                            unmatched++;
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
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_SEWSELECTEDPARTSPROFILEMULTI complete. Selected parts={0}; views={1}; drawn={2}; unmatched alignment pairs={3}; already present/rejected={4}.",
                partNetworks.Count, views.Count, drawn, unmatched, rejected);
        }

        private static bool MatchesView(Database database, ObjectId partId,
            NetworkChoice network, ViewChoice view)
        {
            if (!network.AlignmentId.IsNull && network.AlignmentId == view.AlignmentId)
                return true;
            if (view.AlignmentId.IsNull) return false;
            try
            {
                using (Transaction read = database.TransactionManager.StartTransaction())
                {
                    Alignment alignment = read.GetObject(view.AlignmentId, OpenMode.ForRead, false) as Alignment;
                    DBObject part = read.GetObject(partId, OpenMode.ForRead, false);
                    if (alignment == null || part == null) return false;
                    var pipe = part as Pipe;
                    Point3d point;
                    if (pipe != null)
                    {
                        Point3d a = pipe.StartPoint, b = pipe.EndPoint;
                        point = new Point3d((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);
                    }
                    else
                    {
                        var structure = part as Structure;
                        if (structure == null) return false;
                        point = structure.Position;
                    }
                    double station = 0, offset = 0;
                    alignment.StationOffset(point.X, point.Y, ref station, ref offset);
                    return Math.Abs(offset) <= 5.0;
                }
            }
            catch { return false; }
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

            int drawn = 0, skipped = 0, unmatched = 0;
            using (DocumentLock documentLock = document.LockDocument())
            {
                foreach (NetworkChoice network in chosen)
                {
                    List<ObjectId> parts = ReadParts(document.Database, network.Id);
                    foreach (ViewChoice view in views)
                    {
                        foreach (ObjectId partId in parts)
                        {
                            if (picker.MatchAlignment && !MatchesView(document.Database, partId, network, view))
                            {
                                unmatched++;
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
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_SEWPROFILEPARTSMULTI complete. Networks={0}; profile views={1}; parts drawn={2}; already present/rejected={3}; alignment pairs skipped={4}. Pipe elevations, slopes, cover and rules were not changed.",
                chosen.Count, views.Count, drawn, skipped, unmatched);
            if (picker.OpenLabels)
            {
                document.Editor.SetImpliedSelection(views.Select(item => item.Id).ToArray());
                document.SendStringToExecute("CE_SEWPROFILELABELSTYLESMULTI ", true, false, true);
            }
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
            private readonly List<CheckBox> _checks = new List<CheckBox>();
            private readonly CheckBox _match;
            private readonly CheckBox _labels;

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
                _match = new CheckBox { Content = "Match assigned network alignment to profile view", IsChecked = LastMatchAlignment, Margin = new Thickness(0, 8, 0, 5) };
                footer.Children.Add(_match);
                _labels = new CheckBox { Content = "Open pipe and structure profile label popup after drawing", IsChecked = LastOpenLabels, Margin = new Thickness(0, 0, 0, 12) };
                footer.Children.Add(_labels);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                var apply = new Button { Content = "Draw in selected profile views", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
                apply.Click += (sender, args) => { if (_checks.Any(item => item.IsChecked == true)) DialogResult = true; };
                buttons.Children.Add(apply);
                var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 6, 12, 6) };
                cancel.Click += (sender, args) => DialogResult = false;
                buttons.Children.Add(cancel);
                footer.Children.Add(buttons);
                var list = new StackPanel();
                list.Children.Add(new TextBlock { Text = "Select the gravity networks to draw:", FontSize = 16, Margin = new Thickness(0, 0, 0, 12) });
                foreach (NetworkChoice network in networks)
                {
                    var check = new CheckBox { Content = network.Name, IsChecked = LastSelectedNetworks.Count == 0 || LastSelectedNetworks.Contains(network.Name), Margin = new Thickness(0, 0, 0, 8) };
                    _checks.Add(check); list.Children.Add(check);
                }
                root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            }
            internal List<NetworkChoice> SelectedNetworks => _networks.Where((item, index) => _checks[index].IsChecked == true).ToList();
            internal bool MatchAlignment => _match.IsChecked == true;
            internal bool OpenLabels => _labels.IsChecked == true;
        }
    }
}
