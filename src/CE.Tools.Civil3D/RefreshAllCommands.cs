using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.RefreshAllCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Coordinates explicit refreshes of CE Tools outputs that can be rebuilt
    /// safely without additional user input. Issue books and project summaries
    /// retain their dedicated confirmation workflows.
    /// </summary>
    public sealed class RefreshAllCommands
    {
        [CommandMethod(
            "CE_TOOLS",
            "CE_REFRESHALL",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshAll()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            var failures = new List<string>();
            int coordinateFollowers;
            int coordinateTables;
            int settingOutSchedules;
            int parkingLabels;
            int surfaceLinks;
            int boqTables;
            int costWorkbooks;
            int crossSections;
            int projectInformationTables;
            int restoredLinks = 0;
            LinkedTableAutoRefreshManager.BeginInternalUpdate();
            try
            {
                coordinateFollowers = Run(
                    "dynamic coordinate followers",
                    failures,
                    delegate { return DynamicCoordinateLinkStore.Refresh(document); });
                coordinateTables = Run(
                    "coordinate tables",
                    failures,
                    delegate { return SurveyCoordinateWorkflowCommands.RefreshAll(document); });
                settingOutSchedules = Run(
                    "setting-out schedules",
                    failures,
                    delegate { return SettingOutScheduleCommands.RefreshAll(document); });
                projectInformationTables = Run(
                    "project information tables",
                    failures,
                    delegate { return ProjectSetupCommands.RefreshInformationTables(document); });
                parkingLabels = Run(
                    "parking labels",
                    failures,
                    delegate { return ParkingNumberLinkCommands.Refresh(document, false); });
                surfaceLinks = Run(
                    "surface comparisons",
                    failures,
                    delegate { return SurfaceComparisonLinkStore.RefreshAll(document); });
                boqTables = Run(
                    "linked BOQs",
                    failures,
                    delegate { return BillOfQuantitiesCommands.RefreshAll(document); });
                costWorkbooks = Run(
                    "cost-estimate workbooks",
                    failures,
                    delegate { return WaterSewerCostEstimateCommands.RefreshAll(document); });
                restoredLinks += Run("alignment annotations", failures, delegate { return AlignmentAnnotationLinkStore.RefreshAll(document); });
                restoredLinks += Run("profile annotations", failures, delegate { return ProfileAnnotationLinkStore.RefreshAll(document); });
                restoredLinks += Run("corridor annotations", failures, delegate { return CorridorAnnotationLinkStore.RefreshAll(document); });
                restoredLinks += Run("polyline direction arrows", failures, delegate { return PolylineDirectionCommands.RefreshLinkedArrows(document); });
                restoredLinks += Run("network schedules", failures, delegate { return NetworkAssetScheduleCommands.RefreshAll(document); });
                restoredLinks += Run("road section schedules", failures, delegate { return RoadCrossSectionScheduleCommands.RefreshAll(document); });
                restoredLinks += Run("standard quantity schedules", failures, delegate { return StandardQuantityTemplateCommands.RefreshAll(document); });
                restoredLinks += Run("sewer excavation schedules", failures, delegate { return SewerExcavationCommentCommands.RefreshAll(document); });
                restoredLinks += Run("preserved parking numbers", failures, delegate { return ParkingNumberLinkStore.RefreshAll(document); });
                restoredLinks += Run("linked parking reports", failures, delegate { return ParkingReportLinkStore.RefreshAll(document); });
                crossSections = Run(
                    "dynamic cross sections",
                    failures,
                    delegate { return RefreshCrossSections(document); });
            }
            finally
            {
                LinkedTableAutoRefreshManager.EndInternalUpdate();
            }
            LinkedTableAutoRefreshManager.ClearPending(document);

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_REFRESHALL complete. Coordinate followers={0}; coordinate tables={1}; " +
                "setting-out schedules={2}; parking labels={3}; surface links changed={4}; " +
                "BOQ tables={5}; cost workbooks={6}; cross sections={7}; " +
                "project information tables={8}; restored linked outputs={9}; module failures={10}.",
                coordinateFollowers,
                coordinateTables,
                settingOutSchedules,
                parkingLabels,
                surfaceLinks,
                boqTables,
                costWorkbooks,
                crossSections,
                projectInformationTables,
                restoredLinks,
                failures.Count);

            if (failures.Count > 0)
            {
                document.Editor.WriteMessage(
                    "\nSkipped modules: {0}. Other linked outputs were still processed.",
                    string.Join("; ", failures));
            }
        }

        [CommandMethod("CE_TOOLS", "CE_AUTOREFRESH", CommandFlags.Modal)]
        public void ConfigureAutomaticRefresh()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            bool current = LinkedTableAutoRefreshManager.IsEnabled(document.Database);
            var options = new PromptKeywordOptions(
                "\nLinked-output out-of-date notifications [On/Off] <" +
                (current ? "On" : "Off") + ">: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("On");
            options.Keywords.Add("Off");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            bool enabled = result.Status == PromptStatus.None
                ? current
                : string.Equals(result.StringResult, "On", StringComparison.OrdinalIgnoreCase);
            LinkedTableAutoRefreshManager.SetEnabled(document.Database, enabled);
            document.Editor.WriteMessage(
                "\nLinked-output out-of-date notifications are {0}. CE Tools will not automatically refresh linked BOQs, sewer excavation schedules, annotations or other linked outputs. Use the notification Refresh button or CE_REFRESHALL when you want to update them.",
                enabled ? "ON" : "OFF");
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_REFRESHSTATUS",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshStatus()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Database database = document.Database;

            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Dynamic coordinate links", SafeCount(delegate { return DynamicCoordinateLinkStore.CountLinks(database); })),
                Pair("Linked coordinate tables", SafeCount(delegate { return SurveyCoordinateWorkflowCommands.CountLinkedTables(database); })),
                Pair("Linked setting-out schedules", SafeCount(delegate { return SettingOutScheduleCommands.CountLinkedTables(database); })),
                Pair("Linked parking labels", SafeCount(delegate { return ParkingNumberLinkCommands.CountLinkedLabels(database); })),
                Pair("Linked surface-comparison entities", SafeCount(delegate { return SurfaceComparisonLinkStore.CountLinkedEntities(database); })),
                Pair("Linked BOQ tables", SafeCount(delegate { return BillOfQuantitiesCommands.CountLinkedTables(database); })),
                Pair("Linked dynamic cross sections", SafeCount(delegate { return DynamicSectionUpdateManager.CountLinkedSections(document); })),
                Pair("Out-of-date notifications", LinkedTableAutoRefreshManager.IsEnabled(database) ? "On" : "Off"),
                Pair("Linked-output change monitor", LinkedTableAutoRefreshManager.IsInitialized ? "Active" : "Inactive"),
                Pair("Linked outputs marked out of date", LinkedTableAutoRefreshManager.HasPendingRefresh(document) ? "Yes" : "No"),
                Pair("Automatic linked-output refresh", "Off - explicit refresh only"),
                Pair("Dynamic section manager", DynamicSectionUpdateManager.IsInitialized ? "Active" : "Inactive"),
                Pair("Dynamic section refresh pending", DynamicSectionUpdateManager.HasPendingRefresh(document) ? "Yes" : "No"),
                Pair("Automatic cost-estimate refresh", WaterSewerCostEstimateCommands.IsAutomatic(database) ? "On" : "Off"),
                Pair("Explicit refresh command", "CE_REFRESHALL")
            };

            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Linked Output Refresh Status",
                "Counts are read from the active drawing. Issue books and project summaries use their dedicated commands.",
                rows,
                "CE TOOLS LINKED OUTPUT REFRESH STATUS");
        }

        private static int RefreshCrossSections(Document document)
        {
            int refreshed = 0;
            foreach (ObjectId sourceId in DynamicCrossSectionCommands.FindLinkedSectionSources(document.Database))
            {
                if (DynamicCrossSectionCommands.RefreshLinkedSection(
                    document,
                    sourceId,
                    false,
                    true))
                    refreshed++;
            }
            return refreshed;
        }

        private static int Run(string name, ICollection<string> failures, Func<int> action)
        {
            try
            {
                return action();
            }
            catch (System.Exception exception)
            {
                failures.Add(name + " (" + exception.Message + ")");
                return 0;
            }
        }

        private static string SafeCount(Func<int> action)
        {
            try
            {
                return action().ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
                return "Unavailable";
            }
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

    /// <summary>
    /// Tracks linked outputs that may be stale after drawing edits. Database
    /// event handlers only mark work as out of date. Nothing is refreshed
    /// automatically: after the active command ends, a small modeless notification
    /// offers Refresh or Ignore.
    /// </summary>
    internal static class LinkedTableAutoRefreshManager
    {
        private const string RecordName = "CE_LINKED_TABLE_AUTO_REFRESH";
        private static readonly Dictionary<Database, Document> Documents =
            new Dictionary<Database, Document>();
        private static readonly HashSet<Database> Pending =
            new HashSet<Database>();
        private static readonly Dictionary<Database, Window> Notices =
            new Dictionary<Database, Window>();
        private static bool _internalUpdate;

        public static bool IsInitialized { get; private set; }

        public static void Initialize()
        {
            if (IsInitialized) return;
            IsInitialized = true;
            AcApplication.DocumentManager.DocumentCreated += OnDocumentCreated;
            AcApplication.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            Attach(AcApplication.DocumentManager.MdiActiveDocument);
        }

        public static void Terminate()
        {
            if (!IsInitialized) return;
            AcApplication.DocumentManager.DocumentCreated -= OnDocumentCreated;
            AcApplication.DocumentManager.DocumentActivated -= OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            foreach (Document document in new List<Document>(Documents.Values))
                Detach(document);
            foreach (Window notice in new List<Window>(Notices.Values))
            {
                try { notice.Close(); } catch { }
            }
            Notices.Clear();
            Documents.Clear();
            Pending.Clear();
            IsInitialized = false;
            _internalUpdate = false;
        }

        public static void BeginInternalUpdate()
        {
            _internalUpdate = true;
        }

        public static void EndInternalUpdate()
        {
            _internalUpdate = false;
        }

        public static bool IsEnabled(Database database)
        {
            if (database == null) return false;
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary dictionary = transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (dictionary == null || !dictionary.Contains(RecordName)) return true;
                    Xrecord record = transaction.GetObject(
                        dictionary.GetAt(RecordName),
                        OpenMode.ForRead,
                        false) as Xrecord;
                    if (record == null || record.Data == null) return true;
                    foreach (TypedValue value in record.Data)
                    {
                        string text = value.Value as string;
                        if (string.Equals(text, "Off", StringComparison.OrdinalIgnoreCase)) return false;
                        if (string.Equals(text, "On", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            catch
            {
                return false;
            }
            return true;
        }

        public static void SetEnabled(Database database, bool enabled)
        {
            if (database == null) return;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary dictionary = transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForWrite,
                    false) as DBDictionary;
                if (dictionary == null) return;
                Xrecord record;
                if (dictionary.Contains(RecordName))
                {
                    record = transaction.GetObject(
                        dictionary.GetAt(RecordName),
                        OpenMode.ForWrite,
                        false) as Xrecord;
                }
                else
                {
                    record = new Xrecord();
                    dictionary.SetAt(RecordName, record);
                    transaction.AddNewlyCreatedDBObject(record, true);
                }
                if (record != null)
                {
                    record.Data = new ResultBuffer(
                        new TypedValue((int)DxfCode.Text, enabled ? "On" : "Off"));
                }
                transaction.Commit();
            }
            if (!enabled)
            {
                Window notice;
                if (Notices.TryGetValue(database, out notice))
                {
                    try { notice.Close(); } catch { }
                    Notices.Remove(database);
                }
            }
        }

        public static void Queue(Document document)
        {
            Attach(document);
            if (document != null)
                Pending.Add(document.Database);
        }

        public static void ClearPending(Document document)
        {
            if (document == null || document.Database == null) return;
            Pending.Remove(document.Database);
            Window notice;
            if (Notices.TryGetValue(document.Database, out notice))
            {
                try { notice.Close(); } catch { }
                Notices.Remove(document.Database);
            }
        }

        public static bool HasPendingRefresh(Document document)
        {
            return document != null && Pending.Contains(document.Database);
        }

        private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs args)
        {
            Attach(args.Document);
        }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs args)
        {
            Attach(args.Document);

            // A stale-output notice belongs only to the currently active DWG.
            // Close notices from every other drawing so they cannot remain
            // visible after the user switches documents.
            Database activeDatabase =
                args == null || args.Document == null
                    ? null
                    : args.Document.Database;
            foreach (KeyValuePair<Database, Window> item in
                new List<KeyValuePair<Database, Window>>(Notices))
            {
                if (activeDatabase != null &&
                    item.Key == activeDatabase)
                    continue;
                try { item.Value.Close(); } catch { }
                Notices.Remove(item.Key);
            }

            if (args != null &&
                args.Document != null &&
                Pending.Contains(args.Document.Database) &&
                IsEnabled(args.Document.Database))
            {
                ShowOutOfDateNotice(args.Document);
            }
        }

        private static void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs args)
        {
            Detach(args.Document);
        }

        private static void Attach(Document document)
        {
            if (document == null || document.Database == null || Documents.ContainsKey(document.Database))
                return;
            Documents.Add(document.Database, document);
            document.Database.ObjectModified += OnObjectChanged;
            document.Database.ObjectAppended += OnObjectChanged;
            document.Database.ObjectErased += OnObjectErased;
            document.CommandEnded += OnCommandEnded;
            document.CommandCancelled += OnCommandEnded;
            document.CommandFailed += OnCommandEnded;
        }

        private static void Detach(Document document)
        {
            if (document == null || document.Database == null || !Documents.ContainsKey(document.Database))
                return;
            document.Database.ObjectModified -= OnObjectChanged;
            document.Database.ObjectAppended -= OnObjectChanged;
            document.Database.ObjectErased -= OnObjectErased;
            document.CommandEnded -= OnCommandEnded;
            document.CommandCancelled -= OnCommandEnded;
            document.CommandFailed -= OnCommandEnded;
            Pending.Remove(document.Database);
            Window notice;
            if (Notices.TryGetValue(document.Database, out notice))
            {
                try { notice.Close(); } catch { }
                Notices.Remove(document.Database);
            }
            Documents.Remove(document.Database);
        }

        private static void OnObjectChanged(object sender, ObjectEventArgs args)
        {
            if (IsAssemblyObject(args == null ? null : args.DBObject)) return;
            MarkPending(sender as Database);
        }

        private static void OnObjectErased(object sender, ObjectErasedEventArgs args)
        {
            if (IsAssemblyObject(args == null ? null : args.DBObject)) return;
            MarkPending(sender as Database);
        }

        private static bool IsAssemblyObject(DBObject value)
        {
            if (value == null) return false;
            string name = value.GetType().Name;
            return string.Equals(name, "Assembly", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOf("Subassembly", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void MarkPending(Database database)
        {
            if (_internalUpdate || database == null || !Documents.ContainsKey(database)) return;
            Pending.Add(database);
        }

        private static void OnCommandEnded(object sender, CommandEventArgs args)
        {
            Document document = sender as Document;
            if (document == null || document.Database == null)
                return;

            string command = ReadCommandName(args);
            if (IsExplicitRefreshCommand(command))
            {
                // Refresh commands modify their output entities and therefore
                // raise ObjectModified/ObjectAppended events. Those changes do
                // not make the freshly rebuilt output stale.
                ClearPending(document);
                return;
            }

            if (!Pending.Contains(document.Database))
                return;

            // Never rebuild linked outputs merely because another command ended.
            // The user decides when to refresh.
            if (IsEnabled(document.Database))
                ShowOutOfDateNotice(document);
        }

        private static string ReadCommandName(CommandEventArgs args)
        {
            if (args == null) return string.Empty;
            try
            {
                return (args.GlobalCommandName ?? string.Empty).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsExplicitRefreshCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            return command.StartsWith(
                       "CE_",
                       StringComparison.OrdinalIgnoreCase) &&
                   command.IndexOf(
                       "REFRESH",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ShowOutOfDateNotice(Document document)
        {
            if (document == null || document.Database == null) return;
            if (!object.ReferenceEquals(
                    document,
                    AcApplication.DocumentManager.MdiActiveDocument))
                return;
            Window existing;
            if (Notices.TryGetValue(document.Database, out existing))
            {
                try
                {
                    if (existing.IsVisible) return;
                    existing.Close();
                }
                catch { }
                Notices.Remove(document.Database);
            }

            var window = new Window
            {
                Title = "CE Tools",
                Width = 245,
                Height = 92,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                ShowInTaskbar = false,
                Topmost = false,
                ShowActivated = false,
                SizeToContent = SizeToContent.Manual,
                WindowStartupLocation = WindowStartupLocation.Manual
            };

            // Native ownership keeps this modeless notice inside Civil 3D's
            // z-order. It minimizes/hides with Civil 3D and no longer floats
            // above Excel, browsers or other applications.
            try
            {
                IntPtr owner = AcApplication.MainWindow.Handle;
                if (owner != IntPtr.Zero)
                    new WindowInteropHelper(window).Owner = owner;
            }
            catch { }

            var root = new DockPanel
            {
                Margin = new Thickness(7)
            };
            window.Content = root;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 5, 0, 0)
            };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var ignore = new Button
            {
                Content = "Ignore",
                MinWidth = 58,
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(5, 0, 0, 0)
            };
            ignore.Click += delegate
            {
                ClearPending(document);
            };
            buttons.Children.Add(ignore);

            var refresh = new Button
            {
                Content = "Refresh",
                MinWidth = 64,
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(5, 0, 0, 0),
                IsDefault = true
            };
            refresh.Click += delegate
            {
                ClearPending(document);
                try
                {
                    document.SendStringToExecute(
                        "CE_REFRESHALL ",
                        true,
                        false,
                        false);
                }
                catch { }
            };
            buttons.Children.Add(refresh);

            var message = new TextBlock
            {
                Text = "CE Tools linked output may be out of date. Refresh?",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 10.0,
                VerticalAlignment = VerticalAlignment.Center
            };
            root.Children.Add(message);

            window.Loaded += delegate
            {
                try
                {
                    Rect area = SystemParameters.WorkArea;
                    window.Left = Math.Max(
                        area.Left,
                        area.Right - window.ActualWidth - 10.0);
                    window.Top = Math.Max(
                        area.Top,
                        area.Bottom - window.ActualHeight - 10.0);
                }
                catch { }
            };
            window.Closed += delegate
            {
                Window current;
                if (Notices.TryGetValue(
                        document.Database,
                        out current) &&
                    object.ReferenceEquals(current, window))
                    Notices.Remove(document.Database);
            };

            Notices[document.Database] = window;
            try
            {
                AcApplication.ShowModelessWindow(window);
            }
            catch
            {
                try { window.Show(); } catch { Notices.Remove(document.Database); }
            }
        }
    }
}
