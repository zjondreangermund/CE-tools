using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.NetworkCommentCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Shared current-state reports for gravity and pressure networks, pipe and
    /// structure data, and a popup launcher for stormwater, sewer and water
    /// alignment/profile production commands already implemented by CE Tools.
    /// </summary>
    public sealed class NetworkCommentCommands
    {
        [CommandMethod("CE_TOOLS", "CE_NETWORKREPORT2", CommandFlags.Modal | CommandFlags.Redraw)]
        public void NetworkReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<NetworkRow> networks = ReadNetworks(document);
            if (networks.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_NETWORKREPORT2: no gravity or pressure networks were found.");
                return;
            }

            var rows = new List<IList<string>>();
            foreach (NetworkRow network in networks)
            {
                rows.Add(new List<string>
                {
                    network.Name,
                    network.Discipline,
                    network.NetworkType,
                    network.Pipes.ToString(CultureInfo.InvariantCulture),
                    network.Structures.ToString(CultureInfo.InvariantCulture),
                    network.Fittings.ToString(CultureInfo.InvariantCulture),
                    network.Appurtenances.ToString(CultureInfo.InvariantCulture),
                    network.TotalLength.ToString("N3", CultureInfo.CurrentCulture),
                    network.PartsList,
                    network.ReferenceState
                });
            }

            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Network Summary",
                "Current gravity and pressure network inventory. Re-run or use CE_REFRESHALL after model changes.",
                new List<string>
                {
                    "Network", "Discipline", "Type", "Pipes/Runs", "Structures", "Fittings", "Appurtenances", "Total Length", "Parts List", "Reference"
                },
                rows,
                "CE TOOLS NETWORK SUMMARY");
        }

        [CommandMethod("CE_TOOLS", "CE_NETWORKPARTREPORT2", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void NetworkPartReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect gravity/pressure pipes, structures, fittings or appurtenances: ");
            if (selection.Status != PromptStatus.OK) return;

            var rows = new List<IList<string>>();
            int rejected = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    if (selected == null || selected.ObjectId.IsNull)
                    {
                        rejected++;
                        continue;
                    }
                    DBObject value;
                    try
                    {
                        value = transaction.GetObject(selected.ObjectId, OpenMode.ForRead, false);
                    }
                    catch
                    {
                        rejected++;
                        continue;
                    }
                    Entity entity = value as Entity;
                    if (entity == null || !LooksLikeNetworkPart(value))
                    {
                        rejected++;
                        continue;
                    }

                    rows.Add(new List<string>
                    {
                        ReadText(value, "Name", value.GetType().Name),
                        FriendlyType(value.GetType().Name),
                        entity.Layer,
                        ReadText(value, "StyleName", "<Drawing default>"),
                        ReadText(value, "NetworkName", ReadParentNetworkName(value, transaction)),
                        ReadSize(value),
                        ReadNumber(value, "Length3DCenterToCenter", "Length2DCenterToCenter", "Length3D", "Length2D", "Length").ToString("N3", CultureInfo.CurrentCulture),
                        ReadNumber(value, "StartInvertElevation", "StartElevation", "Elevation").ToString("N3", CultureInfo.CurrentCulture),
                        ReadNumber(value, "EndInvertElevation", "EndElevation", "Elevation").ToString("N3", CultureInfo.CurrentCulture),
                        ReadText(value, "Description", ReadText(value, "RawDescription", string.Empty))
                    });
                }
            }

            if (rows.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_NETWORKPARTREPORT2 cancelled. No supported network parts were selected.");
                return;
            }

            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Network Part Data",
                string.Format(CultureInfo.CurrentCulture, "Accepted parts={0}; rejected={1}.", rows.Count, rejected),
                new List<string>
                {
                    "Name", "Part Type", "Layer", "Style", "Network", "Size", "Length", "Start Invert/Z", "End Invert/Z", "Description"
                },
                rows,
                "CE TOOLS NETWORK PART DATA");
        }

        [CommandMethod("CE_TOOLS", "CE_SERVICEPROFILES", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ServiceProfiles()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var choices = new List<ServiceCommandChoice>
            {
                new ServiceCommandChoice("Stormwater - sequence main and branches", "CE_SWSEQ "),
                new ServiceCommandChoice("Stormwater - create/refresh alignments", "CE_SWALIGN "),
                new ServiceCommandChoice("Stormwater - create/refresh profiles", "CE_SWPROFILE "),
                new ServiceCommandChoice("Sewer - automatic sequence", "CE_SEWSEQ "),
                new ServiceCommandChoice("Sewer - selected main sequence", "CE_SEWSEQMAIN "),
                new ServiceCommandChoice("Sewer - create/refresh alignments", "CE_SEWALIGN "),
                new ServiceCommandChoice("Sewer - apply styles and label spacing", "CE_SEWFORMAT "),
                new ServiceCommandChoice("Sewer - create/refresh profiles", "CE_SEWPROFILE "),
                new ServiceCommandChoice("Water - sequence mains and branches", "CE_WATERSEQ "),
                new ServiceCommandChoice("Water - create/refresh alignments", "CE_WATERALIGN "),
                new ServiceCommandChoice("Water - create/refresh profiles", "CE_WATERPROFILE "),
                new ServiceCommandChoice("Water - place valve/hydrant review markers", "CE_WATERPLACE "),
                new ServiceCommandChoice("Water - refresh asset review markers", "CE_WATERPLACEREFRESH "),
                new ServiceCommandChoice("Rebuild all supported Civil objects", "CE_REBUILDSERVICES ")
            };
            var window = new ServiceProfileLauncherWindow(choices);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted || window.Selected == null) return;
            document.SendStringToExecute(window.Selected.Command, true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_NETWORKDATA", CommandFlags.Modal | CommandFlags.Redraw)]
        public void NetworkData()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var window = new ServiceProfileLauncherWindow(new List<ServiceCommandChoice>
            {
                new ServiceCommandChoice("Complete network summary popup/table", "CE_NETWORKREPORT2 "),
                new ServiceCommandChoice("Selected pipe/structure/fitting data popup/table", "CE_NETWORKPARTREPORT2 "),
                new ServiceCommandChoice("Stormwater production information", "CE_SWINFO "),
                new ServiceCommandChoice("Sewer production information", "CE_SEWINFO "),
                new ServiceCommandChoice("Water production information", "CE_WATERINFO "),
                new ServiceCommandChoice("Refresh all linked tables and rebuild Civil objects", "CE_REFRESHALL ")
            });
            window.Title = "CE Tools - Network Data and Refresh";
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted || window.Selected == null) return;
            document.SendStringToExecute(window.Selected.Command, true, false, true);
        }

        private static List<NetworkRow> ReadNetworks(Document document)
        {
            var result = new List<NetworkRow>();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return result;
            var ids = new List<KeyValuePair<ObjectId, string>>();
            AddObjectIds(civilDocument, "GetPipeNetworkIds", "Gravity", ids);
            AddObjectIds(civilDocument, "GetPressurePipeNetworkIds", "Pressure", ids);

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (KeyValuePair<ObjectId, string> item in ids)
                {
                    DBObject network;
                    try
                    {
                        network = transaction.GetObject(item.Key, OpenMode.ForRead, false);
                    }
                    catch
                    {
                        continue;
                    }
                    if (network == null) continue;
                    string name = ReadText(network, "Name", network.GetType().Name);
                    List<ObjectId> pipes = ReadObjectIds(network, "GetPipeIds", "PipeIds", "GetPipeRunIds", "PipeRunIds");
                    List<ObjectId> structures = ReadObjectIds(network, "GetStructureIds", "StructureIds");
                    List<ObjectId> fittings = ReadObjectIds(network, "GetFittingIds", "FittingIds");
                    List<ObjectId> appurtenances = ReadObjectIds(network, "GetAppurtenanceIds", "AppurtenanceIds");
                    double length = 0.0;
                    foreach (ObjectId pipeId in pipes)
                    {
                        try
                        {
                            DBObject pipe = transaction.GetObject(pipeId, OpenMode.ForRead, false);
                            length += ReadNumber(pipe, "Length3DCenterToCenter", "Length2DCenterToCenter", "Length3D", "Length2D", "Length");
                        }
                        catch { }
                    }
                    result.Add(new NetworkRow
                    {
                        Name = name,
                        Discipline = ClassifyDiscipline(name),
                        NetworkType = item.Value,
                        Pipes = pipes.Count,
                        Structures = structures.Count,
                        Fittings = fittings.Count,
                        Appurtenances = appurtenances.Count,
                        TotalLength = length,
                        PartsList = ReadText(network, "PartsListName", ReadText(network, "PartsList", "<Not exposed>")),
                        ReferenceState = ReadBool(network, "IsReferenceObject") ? "Reference" : "Editable"
                    });
                }
            }
            return result.OrderBy(row => row.Discipline).ThenBy(row => row.Name).ToList();
        }

        private static void AddObjectIds(object owner, string methodName, string type, ICollection<KeyValuePair<ObjectId, string>> target)
        {
            try
            {
                MethodInfo method = owner.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                object raw = method == null ? null : method.Invoke(owner, null);
                IEnumerable enumerable = raw as IEnumerable;
                if (enumerable == null) return;
                foreach (object item in enumerable)
                    if (item is ObjectId && !((ObjectId)item).IsNull)
                        target.Add(new KeyValuePair<ObjectId, string>((ObjectId)item, type));
            }
            catch { }
        }

        private static List<ObjectId> ReadObjectIds(object owner, params string[] names)
        {
            var result = new List<ObjectId>();
            foreach (string name in names)
            {
                object raw = null;
                try
                {
                    MethodInfo method = owner.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (method != null) raw = method.Invoke(owner, null);
                    else
                    {
                        PropertyInfo property = owner.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                        if (property != null) raw = property.GetValue(owner, null);
                    }
                }
                catch { }
                IEnumerable enumerable = raw as IEnumerable;
                if (enumerable == null) continue;
                foreach (object item in enumerable)
                    if (item is ObjectId && !((ObjectId)item).IsNull && !result.Contains((ObjectId)item))
                        result.Add((ObjectId)item);
                if (result.Count > 0) break;
            }
            return result;
        }

        private static bool LooksLikeNetworkPart(object value)
        {
            if (value == null) return false;
            string type = value.GetType().Name.ToUpperInvariant();
            return type.Contains("PIPE") || type.Contains("STRUCTURE") || type.Contains("FITTING") || type.Contains("APPURTENANCE") || type.Contains("PART");
        }

        private static string ReadParentNetworkName(object value, Transaction transaction)
        {
            ObjectId id = ReadObjectId(value, "NetworkId", "PipeNetworkId", "PressureNetworkId");
            if (id.IsNull) return "<Unknown>";
            try
            {
                DBObject network = transaction.GetObject(id, OpenMode.ForRead, false);
                return ReadText(network, "Name", "<Unknown>");
            }
            catch { return "<Missing>"; }
        }

        private static ObjectId ReadObjectId(object value, params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    object raw = property == null ? null : property.GetValue(value, null);
                    if (raw is ObjectId) return (ObjectId)raw;
                }
                catch { }
            }
            return ObjectId.Null;
        }

        private static string ReadSize(object value)
        {
            double width = ReadNumber(value, "InnerDiameterOrWidth", "NominalDiameter", "Diameter", "OutsideDiameter");
            double height = ReadNumber(value, "InnerHeight", "Height");
            if (width <= 0.0) return string.Empty;
            return height > 0.0 && Math.Abs(height - width) > 0.0001
                ? width.ToString("N0", CultureInfo.CurrentCulture) + " x " + height.ToString("N0", CultureInfo.CurrentCulture)
                : width.ToString("N0", CultureInfo.CurrentCulture);
        }

        private static string ClassifyDiscipline(string name)
        {
            string value = (name ?? string.Empty).ToUpperInvariant();
            if (value.Contains("SEW") || value.Contains("FOUL")) return "Sewer";
            if (value.Contains("STORM") || value.Contains("SW") || value.Contains("DRAIN")) return "Stormwater";
            if (value.Contains("BULK")) return "Bulk Water";
            if (value.Contains("WATER") || value.Contains("PRESSURE")) return "Water";
            return "Unclassified";
        }

        private static string FriendlyType(string typeName)
        {
            return string.IsNullOrWhiteSpace(typeName)
                ? "Network Part"
                : typeName.Replace("Pressure", "Pressure ").Replace("Pipe", " Pipe").Replace("Structure", " Structure").Trim();
        }

        private static string ReadText(object value, string propertyName, string fallback)
        {
            if (value == null) return fallback;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                object raw = property == null ? null : property.GetValue(value, null);
                string text = Convert.ToString(raw, CultureInfo.CurrentCulture);
                return string.IsNullOrWhiteSpace(text) ? fallback : text;
            }
            catch { return fallback; }
        }

        private static double ReadNumber(object value, params string[] propertyNames)
        {
            if (value == null) return 0.0;
            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    object raw = property == null ? null : property.GetValue(value, null);
                    if (raw == null) continue;
                    double number = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    if (!double.IsNaN(number) && !double.IsInfinity(number)) return number;
                }
                catch { }
            }
            return 0.0;
        }

        private static bool ReadBool(object value, string propertyName)
        {
            try
            {
                PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                object raw = property == null ? null : property.GetValue(value, null);
                return raw != null && Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
            }
            catch { return false; }
        }

        private static PromptSelectionResult GetSelection(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class NetworkRow
        {
            public string Name { get; set; }
            public string Discipline { get; set; }
            public string NetworkType { get; set; }
            public int Pipes { get; set; }
            public int Structures { get; set; }
            public int Fittings { get; set; }
            public int Appurtenances { get; set; }
            public double TotalLength { get; set; }
            public string PartsList { get; set; }
            public string ReferenceState { get; set; }
        }
    }

    internal sealed class ServiceCommandChoice
    {
        public ServiceCommandChoice(string label, string command)
        {
            Label = label;
            Command = command;
        }
        public string Label { get; }
        public string Command { get; }
        public override string ToString() { return Label; }
    }

    internal sealed class ServiceProfileLauncherWindow : Window
    {
        private readonly ListBox _choices;
        public ServiceProfileLauncherWindow(IEnumerable<ServiceCommandChoice> choices)
        {
            Title = "CE Tools - Service Production";
            Width = 650;
            Height = 520;
            MinWidth = 480;
            MinHeight = 340;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            var cancel = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            cancel.Click += delegate { Close(); };
            buttons.Children.Add(cancel);
            var run = new Button { Content = "Run Selected", Width = 120, IsDefault = true };
            run.Click += delegate
            {
                Selected = _choices.SelectedItem as ServiceCommandChoice;
                if (Selected == null) return;
                Accepted = true;
                Close();
            };
            buttons.Children.Add(run);
            var header = new TextBlock
            {
                Text = "Choose one current CE Tools production, profile, report or refresh workflow.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            _choices = new ListBox { ItemsSource = choices.ToList() };
            if (_choices.Items.Count > 0) _choices.SelectedIndex = 0;
            _choices.MouseDoubleClick += delegate { run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
            root.Children.Add(_choices);
        }

        public bool Accepted { get; private set; }
        public ServiceCommandChoice Selected { get; private set; }
    }
}
