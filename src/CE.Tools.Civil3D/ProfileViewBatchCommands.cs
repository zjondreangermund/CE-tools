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

[assembly: CommandClass(typeof(CETools.Civil3D.ProfileViewBatchCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Version-tolerant batch profile-view cleanup. The workflow applies selected
    /// profile-view and band-set styles, requests automatic station/elevation
    /// ranges where supported, rebuilds available views and reports unsupported
    /// API operations instead of silently claiming success.
    /// </summary>
    public sealed class ProfileViewBatchCommands
    {
        [CommandMethod("CE_TOOLS", "CE_PROFILEVIEWBATCHTOOLS", CommandFlags.Modal)]
        public void ProfileViewBatchTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var options = new PromptKeywordOptions(
                "\nProfile view batch tools [Cleanup/Fit/Info] <Cleanup>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Cleanup");
            options.Keywords.Add("Fit");
            options.Keywords.Add("Info");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string choice = result.Status == PromptStatus.OK
                ? result.StringResult
                : "Cleanup";
            string command = string.Equals(choice, "Fit", StringComparison.OrdinalIgnoreCase)
                ? "CE_PROFILEVIEWFITALL "
                : string.Equals(choice, "Info", StringComparison.OrdinalIgnoreCase)
                    ? "CE_PROFILEVIEWBATCHINFO "
                    : "CE_PROFILEVIEWBATCH ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILEVIEWBATCH", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void BatchCleanup()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<ProfileViewItem> views = ReadProfileViews(document);
            if (views.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROFILEVIEWBATCH: no accessible Civil 3D profile views were found.");
                return;
            }

            ProfileViewStyleCatalogue catalogue = ReadStyleCatalogue(document);
            var window = new ProfileViewBatchWindow(views, catalogue);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted)
            {
                document.Editor.WriteMessage("\nCE_PROFILEVIEWBATCH cancelled.");
                return;
            }
            List<ProfileViewItem> selected = window.SelectedViews;
            if (selected.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROFILEVIEWBATCH cancelled. No profile views were selected.");
                return;
            }

            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Selected profile views", selected.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Profile-view style", window.ApplyProfileViewStyle ? window.SelectedProfileViewStyle.DisplayName : "<Keep current>"),
                Pair("Band-set style", window.ApplyBandSetStyle ? window.SelectedBandSetStyle.DisplayName : "<Keep current>"),
                Pair("Automatic station/elevation fit", window.AutoFit ? "Yes" : "No"),
                Pair("Rebuild/update views", window.Rebuild ? "Yes" : "No"),
                Pair("Run annotation overlap cleanup", window.RunOverlapCleanup ? "Yes" : "No"),
                Pair("API boundary", "Unsupported properties/methods are reported and skipped")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Batch Profile View Cleanup",
                    "The selected operations are applied in one transaction where possible. Civil 3D version-specific operations that are unavailable are counted as unsupported.",
                    review,
                    "Apply Cleanup"))
            {
                document.Editor.WriteMessage("\nCE_PROFILEVIEWBATCH cancelled.");
                return;
            }

            ProfileViewBatchResult result = ApplyBatch(
                document,
                selected,
                window.ApplyProfileViewStyle ? window.SelectedProfileViewStyle : null,
                window.ApplyBandSetStyle ? window.SelectedBandSetStyle : null,
                window.AutoFit,
                window.Rebuild);
            document.Editor.Regen();
            ShowResult(document, result, "CE Tools - Profile View Batch Result");
            if (window.RunOverlapCleanup)
            {
                document.Editor.WriteMessage(
                    "\nSelect the profile-view labels/notes requiring overlap cleanup.");
                document.SendStringToExecute("CE_OVERLAPFIX ", true, false, true);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILEVIEWFITALL", CommandFlags.Modal | CommandFlags.Redraw | CommandFlags.UsePickSet)]
        public void FitAllProfileViews()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<ProfileViewItem> views = PromptScope(document, ReadProfileViews(document));
            if (views.Count == 0) return;
            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Profile views", views.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Station range", "Automatic where supported"),
                Pair("Elevation range", "Automatic where supported"),
                Pair("Rebuild/update", "Yes")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Fit Profile Views",
                    "Automatic range properties and update/rebuild methods vary by Civil 3D release. Every unavailable operation will be reported.",
                    review,
                    "Fit Views"))
                return;
            ProfileViewBatchResult result = ApplyBatch(
                document,
                views,
                null,
                null,
                true,
                true);
            document.Editor.Regen();
            ShowResult(document, result, "CE Tools - Profile View Fit Result");
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILEVIEWBATCHINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProfileViewBatchInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<ProfileViewItem> views = ReadProfileViews(document);
            if (views.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PROFILEVIEWBATCHINFO: no accessible profile views were found.");
                return;
            }
            var rows = new List<IList<string>>();
            foreach (ProfileViewItem view in views)
            {
                rows.Add(new List<string>
                {
                    view.Name,
                    view.AlignmentName,
                    view.StyleName,
                    view.BandSetName,
                    view.StationRange,
                    view.ElevationRange,
                    view.OutOfDate
                });
            }
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Profile View Batch Information",
                "Accessible profile views, current styles, ranges and update state.",
                new[]
                {
                    "PROFILE VIEW",
                    "ALIGNMENT",
                    "STYLE",
                    "BAND SET",
                    "STATION RANGE",
                    "ELEVATION RANGE",
                    "STATE"
                },
                rows,
                "CE TOOLS PROFILE VIEW BATCH INFORMATION");
        }

        private static ProfileViewBatchResult ApplyBatch(
            Document document,
            IList<ProfileViewItem> views,
            ProfileViewStyleChoice profileViewStyle,
            ProfileViewStyleChoice bandSetStyle,
            bool autoFit,
            bool rebuild)
        {
            var result = new ProfileViewBatchResult();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ProfileViewItem item in views)
                {
                    DBObject view;
                    try
                    {
                        view = transaction.GetObject(
                            item.ObjectId,
                            OpenMode.ForWrite,
                            false);
                    }
                    catch
                    {
                        result.Failed++;
                        continue;
                    }
                    if (view == null)
                    {
                        result.Failed++;
                        continue;
                    }

                    bool changed = false;
                    if (profileViewStyle != null)
                    {
                        if (TrySetObjectIdProperty(view, "StyleId", profileViewStyle.ObjectId) ||
                            TrySetStringProperty(view, "StyleName", profileViewStyle.DisplayName))
                        {
                            result.ProfileStylesApplied++;
                            changed = true;
                        }
                        else result.Unsupported++;
                    }
                    if (bandSetStyle != null)
                    {
                        if (TryApplyBandSet(view, bandSetStyle))
                        {
                            result.BandSetsApplied++;
                            changed = true;
                        }
                        else result.Unsupported++;
                    }
                    if (autoFit)
                    {
                        int fit = 0;
                        if (TrySetAutomaticEnum(view, "StationRangeMode")) fit++;
                        if (TrySetAutomaticEnum(view, "ElevationRangeMode")) fit++;
                        if (TrySetBooleanProperty(view, "AutomaticStationRange", true)) fit++;
                        if (TrySetBooleanProperty(view, "AutomaticElevationRange", true)) fit++;
                        if (fit > 0)
                        {
                            result.ViewsAutoFit++;
                            changed = true;
                        }
                        else result.Unsupported++;
                    }
                    if (rebuild)
                    {
                        if (TryInvokeNoArguments(view, "Rebuild") ||
                            TryInvokeNoArguments(view, "Update") ||
                            TryInvokeNoArguments(view, "UpdateDisplay"))
                        {
                            result.ViewsRebuilt++;
                            changed = true;
                        }
                        else result.Unsupported++;
                    }
                    if (changed) result.ViewsChanged++;
                }
                transaction.Commit();
            }
            return result;
        }

        private static bool TryApplyBandSet(
            object profileView,
            ProfileViewStyleChoice style)
        {
            if (profileView == null || style == null) return false;
            if (TrySetObjectIdProperty(profileView, "BandSetStyleId", style.ObjectId) ||
                TrySetObjectIdProperty(profileView, "ProfileViewBandSetStyleId", style.ObjectId) ||
                TrySetStringProperty(profileView, "BandSetStyleName", style.DisplayName))
                return true;

            object bands = ReadProperty(profileView, "Bands");
            if (bands == null) return false;
            foreach (string methodName in new[]
            {
                "ImportBandSetStyle",
                "ApplyBandSetStyle",
                "SetBandSetStyle"
            })
            {
                if (TryInvokeObjectId(bands, methodName, style.ObjectId)) return true;
            }
            return false;
        }

        private static bool TrySetAutomaticEnum(object value, string propertyName)
        {
            PropertyInfo property = FindWritableProperty(value, propertyName);
            if (property == null || !property.PropertyType.IsEnum) return false;
            string automaticName = Enum.GetNames(property.PropertyType)
                .FirstOrDefault(name =>
                    string.Equals(name, "Automatic", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "Auto", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(automaticName)) return false;
            try
            {
                property.SetValue(value, Enum.Parse(property.PropertyType, automaticName), null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetBooleanProperty(object value, string name, bool setting)
        {
            PropertyInfo property = FindWritableProperty(value, name);
            if (property == null || property.PropertyType != typeof(bool)) return false;
            try
            {
                property.SetValue(value, setting, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetObjectIdProperty(object value, string name, ObjectId setting)
        {
            if (setting.IsNull) return false;
            PropertyInfo property = FindWritableProperty(value, name);
            if (property == null || property.PropertyType != typeof(ObjectId)) return false;
            try
            {
                property.SetValue(value, setting, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetStringProperty(object value, string name, string setting)
        {
            PropertyInfo property = FindWritableProperty(value, name);
            if (property == null || property.PropertyType != typeof(string)) return false;
            try
            {
                property.SetValue(value, setting, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static PropertyInfo FindWritableProperty(object value, string name)
        {
            return value == null
                ? null
                : value.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);
        }

        private static bool TryInvokeNoArguments(object value, string methodName)
        {
            if (value == null) return false;
            try
            {
                MethodInfo method = value.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                if (method == null) return false;
                method.Invoke(value, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryInvokeObjectId(object value, string methodName, ObjectId objectId)
        {
            if (value == null || objectId.IsNull) return false;
            try
            {
                MethodInfo method = value.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(ObjectId) },
                    null);
                if (method == null) return false;
                method.Invoke(value, new object[] { objectId });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<ProfileViewItem> PromptScope(
            Document document,
            IList<ProfileViewItem> all)
        {
            if (all == null || all.Count == 0) return new List<ProfileViewItem>();
            var options = new PromptKeywordOptions(
                "\nProfile view scope [All/Select] <All>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("All");
            options.Keywords.Add("Select");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return new List<ProfileViewItem>();
            if (result.Status != PromptStatus.OK ||
                string.Equals(result.StringResult, "All", StringComparison.OrdinalIgnoreCase))
                return all.ToList();

            PromptSelectionResult selection = document.Editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect Civil 3D profile views: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (selection.Status != PromptStatus.OK) return new List<ProfileViewItem>();
            var selectedIds = new HashSet<ObjectId>(selection.Value.GetObjectIds());
            return all.Where(item => selectedIds.Contains(item.ObjectId)).ToList();
        }

        private static List<ProfileViewItem> ReadProfileViews(Document document)
        {
            var result = new List<ProfileViewItem>();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return result;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
                {
                    DBObject alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false);
                    string alignmentName = ReadStringProperty(alignment, "Name");
                    foreach (ObjectId viewId in ReadObjectIds(alignment, "GetProfileViewIds"))
                    {
                        DBObject view = transaction.GetObject(viewId, OpenMode.ForRead, false);
                        if (view == null) continue;
                        result.Add(new ProfileViewItem(
                            viewId,
                            ReadStringProperty(view, "Name"),
                            alignmentName,
                            ReadStringProperty(view, "StyleName"),
                            FirstNonBlank(
                                ReadStringProperty(view, "BandSetStyleName"),
                                ReadNestedString(view, "Bands", "BandSetStyleName")),
                            FormatRange(view, "StationStart", "StationEnd"),
                            FormatRange(view, "ElevationMin", "ElevationMax"),
                            ReadBoolProperty(view, "IsOutOfDate") ? "Out of date" : "Current"));
                    }
                }
            }
            return result
                .OrderBy(item => item.AlignmentName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static ProfileViewStyleCatalogue ReadStyleCatalogue(Document document)
        {
            var catalogue = new ProfileViewStyleCatalogue();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return catalogue;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                object styles = ReadProperty(civilDocument, "Styles");
                AddStyles(
                    ReadProperty(styles, "ProfileViewStyles"),
                    transaction,
                    catalogue.ProfileViewStyles);
                object bandSets = ReadProperty(styles, "ProfileViewBandSetStyles");
                if (bandSets == null)
                {
                    object root = ReadProperty(styles, "BandSetStyles");
                    bandSets = ReadProperty(root, "ProfileViewBandSetStyles");
                }
                AddStyles(
                    bandSets,
                    transaction,
                    catalogue.BandSetStyles);
            }
            return catalogue;
        }

        private static void AddStyles(
            object collection,
            Transaction transaction,
            ICollection<ProfileViewStyleChoice> target)
        {
            IEnumerable enumerable = collection as IEnumerable;
            if (enumerable == null) return;
            foreach (object item in enumerable)
            {
                if (!(item is ObjectId)) continue;
                ObjectId id = (ObjectId)item;
                if (id.IsNull) continue;
                DBObject style = transaction.GetObject(id, OpenMode.ForRead, false);
                string name = ReadStringProperty(style, "Name");
                if (!string.IsNullOrWhiteSpace(name))
                    target.Add(new ProfileViewStyleChoice(id, name));
            }
        }

        private static IEnumerable<ObjectId> ReadObjectIds(object value, string methodName)
        {
            if (value == null) return Enumerable.Empty<ObjectId>();
            try
            {
                MethodInfo method = value.GetType().GetMethod(
                    methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                IEnumerable enumerable = method == null
                    ? null
                    : method.Invoke(value, null) as IEnumerable;
                if (enumerable == null) return Enumerable.Empty<ObjectId>();
                var result = new List<ObjectId>();
                foreach (object item in enumerable)
                    if (item is ObjectId) result.Add((ObjectId)item);
                return result;
            }
            catch
            {
                return Enumerable.Empty<ObjectId>();
            }
        }

        private static string FormatRange(object value, string minimumName, string maximumName)
        {
            double? minimum = ReadDoubleProperty(value, minimumName);
            double? maximum = ReadDoubleProperty(value, maximumName);
            return minimum.HasValue && maximum.HasValue
                ? minimum.Value.ToString("N3", CultureInfo.CurrentCulture) + " - " +
                  maximum.Value.ToString("N3", CultureInfo.CurrentCulture)
                : "<Unavailable>";
        }

        private static double? ReadDoubleProperty(object value, string name)
        {
            object raw = ReadProperty(value, name);
            if (raw == null) return null;
            try
            {
                double result = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return double.IsNaN(result) || double.IsInfinity(result)
                    ? (double?)null
                    : result;
            }
            catch
            {
                return null;
            }
        }

        private static bool ReadBoolProperty(object value, string name)
        {
            object raw = ReadProperty(value, name);
            return raw is bool && (bool)raw;
        }

        private static string ReadStringProperty(object value, string name)
        {
            return Convert.ToString(ReadProperty(value, name), CultureInfo.CurrentCulture) ?? string.Empty;
        }

        private static string ReadNestedString(object value, string parent, string child)
        {
            return ReadStringProperty(ReadProperty(value, parent), child);
        }

        private static object ReadProperty(object value, string name)
        {
            if (value == null) return null;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);
                return property == null || property.GetIndexParameters().Length != 0
                    ? null
                    : property.GetValue(value, null);
            }
            catch
            {
                return null;
            }
        }

        private static string FirstNonBlank(params string[] values)
        {
            foreach (string value in values)
                if (!string.IsNullOrWhiteSpace(value)) return value;
            return string.Empty;
        }

        private static void ShowResult(
            Document document,
            ProfileViewBatchResult result,
            string title)
        {
            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Profile views changed", result.ViewsChanged.ToString(CultureInfo.InvariantCulture)),
                Pair("Profile-view styles applied", result.ProfileStylesApplied.ToString(CultureInfo.InvariantCulture)),
                Pair("Band sets applied", result.BandSetsApplied.ToString(CultureInfo.InvariantCulture)),
                Pair("Views set to automatic fit", result.ViewsAutoFit.ToString(CultureInfo.InvariantCulture)),
                Pair("Views rebuilt/updated", result.ViewsRebuilt.ToString(CultureInfo.InvariantCulture)),
                Pair("Unsupported API operations", result.Unsupported.ToString(CultureInfo.InvariantCulture)),
                Pair("Failed views", result.Failed.ToString(CultureInfo.InvariantCulture))
            };
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                title,
                "Unsupported operations were not reported as successful. Review the result before drawing issue.",
                rows,
                "CE TOOLS PROFILE VIEW BATCH RESULT");
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class ProfileViewItem
    {
        public ProfileViewItem(
            ObjectId objectId,
            string name,
            string alignmentName,
            string styleName,
            string bandSetName,
            string stationRange,
            string elevationRange,
            string outOfDate)
        {
            ObjectId = objectId;
            Name = string.IsNullOrWhiteSpace(name) ? objectId.Handle.ToString() : name;
            AlignmentName = alignmentName;
            StyleName = styleName;
            BandSetName = bandSetName;
            StationRange = stationRange;
            ElevationRange = elevationRange;
            OutOfDate = outOfDate;
            IsSelected = true;
        }

        public ObjectId ObjectId { get; private set; }
        public string Name { get; private set; }
        public string AlignmentName { get; private set; }
        public string StyleName { get; private set; }
        public string BandSetName { get; private set; }
        public string StationRange { get; private set; }
        public string ElevationRange { get; private set; }
        public string OutOfDate { get; private set; }
        public bool IsSelected { get; set; }
    }

    internal sealed class ProfileViewStyleChoice
    {
        public ProfileViewStyleChoice(ObjectId objectId, string displayName)
        {
            ObjectId = objectId;
            DisplayName = displayName;
        }

        public ObjectId ObjectId { get; private set; }
        public string DisplayName { get; private set; }
        public override string ToString() { return DisplayName; }
    }

    internal sealed class ProfileViewStyleCatalogue
    {
        public ProfileViewStyleCatalogue()
        {
            ProfileViewStyles = new List<ProfileViewStyleChoice>();
            BandSetStyles = new List<ProfileViewStyleChoice>();
        }

        public List<ProfileViewStyleChoice> ProfileViewStyles { get; private set; }
        public List<ProfileViewStyleChoice> BandSetStyles { get; private set; }
    }

    internal sealed class ProfileViewBatchResult
    {
        public int ViewsChanged { get; set; }
        public int ProfileStylesApplied { get; set; }
        public int BandSetsApplied { get; set; }
        public int ViewsAutoFit { get; set; }
        public int ViewsRebuilt { get; set; }
        public int Unsupported { get; set; }
        public int Failed { get; set; }
    }

    internal sealed class ProfileViewBatchWindow : Window
    {
        private readonly IList<ProfileViewItem> _views;
        private readonly ComboBox _profileStyle;
        private readonly ComboBox _bandSet;
        private readonly CheckBox _applyProfileStyle;
        private readonly CheckBox _applyBandSet;
        private readonly CheckBox _autoFit;
        private readonly CheckBox _rebuild;
        private readonly CheckBox _overlap;

        public ProfileViewBatchWindow(
            IList<ProfileViewItem> views,
            ProfileViewStyleCatalogue catalogue)
        {
            _views = views;
            Accepted = false;
            Title = "CE Tools - Batch Profile View Cleanup";
            Width = 860;
            Height = 720;
            MinWidth = 680;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            var root = new DockPanel { Margin = new Thickness(16) };
            Content = root;
            var heading = new TextBlock
            {
                Text = "Batch Profile View Cleanup",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);
            var note = new TextBlock
            {
                Text = "Select profile views and the operations to apply. Unsupported Civil 3D API operations will be counted and reported.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            var actions = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            _applyProfileStyle = new CheckBox { Content = "Apply profile-view style", Margin = new Thickness(0, 2, 0, 2) };
            _profileStyle = new ComboBox
            {
                ItemsSource = catalogue.ProfileViewStyles,
                SelectedIndex = catalogue.ProfileViewStyles.Count > 0 ? 0 : -1,
                IsTextSearchEnabled = true,
                MinWidth = 360,
                Margin = new Thickness(20, 2, 0, 4)
            };
            _applyBandSet = new CheckBox { Content = "Apply profile-view band-set style", Margin = new Thickness(0, 2, 0, 2) };
            _bandSet = new ComboBox
            {
                ItemsSource = catalogue.BandSetStyles,
                SelectedIndex = catalogue.BandSetStyles.Count > 0 ? 0 : -1,
                IsTextSearchEnabled = true,
                MinWidth = 360,
                Margin = new Thickness(20, 2, 0, 4)
            };
            _autoFit = new CheckBox { Content = "Set automatic station/elevation range where supported", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
            _rebuild = new CheckBox { Content = "Rebuild/update views where supported", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
            _overlap = new CheckBox { Content = "Open CE overlap cleanup after batch operation", IsChecked = false, Margin = new Thickness(0, 2, 0, 2) };
            actions.Children.Add(_applyProfileStyle);
            actions.Children.Add(_profileStyle);
            actions.Children.Add(_applyBandSet);
            actions.Children.Add(_bandSet);
            actions.Children.Add(_autoFit);
            actions.Children.Add(_rebuild);
            actions.Children.Add(_overlap);
            DockPanel.SetDock(actions, Dock.Top);
            root.Children.Add(actions);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var selectAll = new Button { Content = "Select All", MinWidth = 90, Padding = new Thickness(8, 5, 8, 5) };
            selectAll.Click += delegate { foreach (ProfileViewItem item in _views) item.IsSelected = true; RefreshItems(); };
            var selectNone = new Button { Content = "Clear Selection", MinWidth = 110, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 5, 8, 5) };
            selectNone.Click += delegate { foreach (ProfileViewItem item in _views) item.IsSelected = false; RefreshItems(); };
            var apply = new Button { Content = "Review and Apply", MinWidth = 120, Margin = new Thickness(16, 0, 0, 0), Padding = new Thickness(8, 5, 8, 5) };
            apply.Click += delegate
            {
                if (!SelectedViews.Any())
                {
                    MessageBox.Show(this, "Select at least one profile view.", "CE Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (ApplyProfileViewStyle && SelectedProfileViewStyle == null)
                {
                    MessageBox.Show(this, "No profile-view style is available/selected.", "CE Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (ApplyBandSetStyle && SelectedBandSetStyle == null)
                {
                    MessageBox.Show(this, "No profile-view band-set style is available/selected.", "CE Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Accepted = true;
                DialogResult = true;
            };
            var cancel = new Button { Content = "Cancel", MinWidth = 90, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 5, 8, 5) };
            cancel.Click += delegate { Accepted = false; DialogResult = false; };
            buttons.Children.Add(selectAll);
            buttons.Children.Add(selectNone);
            buttons.Children.Add(apply);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _itemsPanel = new StackPanel();
            scroll.Content = _itemsPanel;
            root.Children.Add(scroll);
            RefreshItems();
        }

        private readonly StackPanel _itemsPanel;
        public bool Accepted { get; private set; }
        public bool ApplyProfileViewStyle { get { return _applyProfileStyle.IsChecked == true; } }
        public bool ApplyBandSetStyle { get { return _applyBandSet.IsChecked == true; } }
        public bool AutoFit { get { return _autoFit.IsChecked == true; } }
        public bool Rebuild { get { return _rebuild.IsChecked == true; } }
        public bool RunOverlapCleanup { get { return _overlap.IsChecked == true; } }
        public ProfileViewStyleChoice SelectedProfileViewStyle { get { return _profileStyle.SelectedItem as ProfileViewStyleChoice; } }
        public ProfileViewStyleChoice SelectedBandSetStyle { get { return _bandSet.SelectedItem as ProfileViewStyleChoice; } }
        public List<ProfileViewItem> SelectedViews { get { return _views.Where(item => item.IsSelected).ToList(); } }

        private void RefreshItems()
        {
            _itemsPanel.Children.Clear();
            foreach (ProfileViewItem item in _views)
            {
                var check = new CheckBox
                {
                    IsChecked = item.IsSelected,
                    Content = item.AlignmentName + " — " + item.Name + " | " + item.StyleName + " | " + item.OutOfDate,
                    Margin = new Thickness(0, 3, 0, 3)
                };
                ProfileViewItem captured = item;
                check.Checked += delegate { captured.IsSelected = true; };
                check.Unchecked += delegate { captured.IsSelected = false; };
                _itemsPanel.Children.Add(check);
            }
        }
    }
}
