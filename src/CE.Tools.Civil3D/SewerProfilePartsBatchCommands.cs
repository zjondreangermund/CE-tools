using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
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
                        if (picker.MatchAlignment &&
                            (network.AlignmentId.IsNull || view.AlignmentId != network.AlignmentId))
                        {
                            unmatched++;
                            continue;
                        }
                        foreach (ObjectId partId in parts)
                        {
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
                        if (network != null && !network.IsReferenceObject)
                            result.Add(new NetworkChoice(id, network.Name, network.ReferenceAlignmentId));
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
                _match = new CheckBox { Content = "Match network reference alignment to profile view", IsChecked = true, Margin = new Thickness(0, 8, 0, 5) };
                footer.Children.Add(_match);
                _labels = new CheckBox { Content = "Open pipe and structure profile label popup after drawing", IsChecked = true, Margin = new Thickness(0, 0, 0, 12) };
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
                    var check = new CheckBox { Content = network.Name, IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
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
