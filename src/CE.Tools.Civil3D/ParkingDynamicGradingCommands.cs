using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ParkingDynamicGradingCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Phase 2 parking automation: automatic refresh of boundary-driven parking
    /// options and linked 3D grading guides after source-boundary grip edits.
    /// Grading outputs are design-assistance geometry, not a final drainage design.
    /// </summary>
    public sealed class ParkingDynamicGradingCommands
    {
        [CommandMethod("CE_TOOLS", "CE_PARKAUTOMONITOR", CommandFlags.Modal)]
        public void ConfigureParkingMonitor()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            string choice = DisciplineWorkflowDialogs.SelectWorkflow(
                "CE Tools - Dynamic Parking Monitor",
                "Control automatic parking option refresh after boundary grip edits.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Enable automatic refresh", "On", "Monitor linked parking boundaries and queue refreshes.", "01 Monitor"),
                    new DisciplineWorkflowAction("Disable automatic refresh", "Off", "Stop automatic parking refresh monitoring.", "01 Monitor"),
                    new DisciplineWorkflowAction("Refresh all now", "RefreshNow", "Immediately refresh linked parking options and grading guides.", "02 Actions"),
                    new DisciplineWorkflowAction("Monitor status", "Status", "Report the current monitor and linkage status.", "02 Actions")
                });
            if (string.IsNullOrWhiteSpace(choice)) return;
            if (string.Equals(choice, "On", StringComparison.OrdinalIgnoreCase))
            {
                ParkingOptionAutoRefreshManager.Enabled = true;
                ParkingOptionAutoRefreshManager.RebuildCache(document.Database);
            }
            else if (string.Equals(choice, "Off", StringComparison.OrdinalIgnoreCase))
            {
                ParkingOptionAutoRefreshManager.Enabled = false;
            }
            else if (string.Equals(choice, "RefreshNow", StringComparison.OrdinalIgnoreCase))
            {
                RefreshAllNow(document);
                return;
            }
            ShowMonitorStatus(document);
        }

        [CommandMethod("CE_TOOLS", "CE_PARKAUTOREFRESHALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshAllParkingLinks()
        {
            Document document = ActiveDocument();
            if (document != null) RefreshAllNow(document);
        }

        [CommandMethod("CE_TOOLS", "CE_PARKAUTOSTATUS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ParkingMonitorStatus()
        {
            Document document = ActiveDocument();
            if (document != null) ShowMonitorStatus(document);
        }

        [CommandMethod("CE_TOOLS", "CE_PARKGRADETOOLS", CommandFlags.Modal)]
        public void ParkingGradeTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Parking Grading",
                "Create and maintain boundary-linked 3D parking grading guides.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Create grading guides", "CE_PARKGRADECREATE", "Create linked 3D grading guides for a parking option.", "01 Grading"),
                    new DisciplineWorkflowAction("Refresh grading guides", "CE_PARKGRADEREFRESH", "Rebuild grading guides after source changes.", "01 Grading"),
                    new DisciplineWorkflowAction("Grading information", "CE_PARKGRADEINFO", "Inspect grading linkage and current settings.", "02 Review"),
                    new DisciplineWorkflowAction("Clear grading guides", "CE_PARKGRADECLEAR", "Remove generated parking grading guides.", "03 Cleanup")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PARKGRADECREATE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateParkingGradeGuide()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;
            ParkingGradeBoundary boundary = ParkingGradeGuideStore.PromptBoundary(document);
            if (boundary == null) return;

            var modeOptions = new PromptKeywordOptions(
                "\nParking grading mode [LowPoint/Crown/Valley] <LowPoint>: ")
            {
                AllowNone = true
            };
            modeOptions.Keywords.Add("LowPoint");
            modeOptions.Keywords.Add("Crown");
            modeOptions.Keywords.Add("Valley");
            PromptResult modeResult = editor.GetKeywords(modeOptions);
            if (modeResult.Status == PromptStatus.Cancel) return;
            ParkingGradeMode mode = ParseMode(
                modeResult.Status == PromptStatus.OK ? modeResult.StringResult : "LowPoint");

            double slope;
            double referenceElevation;
            double spacing;
            if (!PromptPositiveDouble(editor, "Design slope (%)", 2.0, out slope)) return;
            if (!PromptAnyDouble(editor, "Reference elevation", boundary.Elevation, out referenceElevation)) return;
            if (!PromptPositiveDouble(
                    editor,
                    "Guide spacing in drawing units",
                    Math.Max(boundary.Length / 10.0, 1.0),
                    out spacing))
                return;

            Point3d lowPoint = boundary.CentreWorld;
            if (mode == ParkingGradeMode.LowPoint)
            {
                PromptPointResult pointResult = editor.GetPoint(
                    "\nPick the intended parking low point inside the boundary: ");
                if (pointResult.Status != PromptStatus.OK) return;
                Point2d local = boundary.ToLocal(pointResult.Value);
                if (!ParkingGradeGuideStore.PointInPolygon(boundary.Polygon, local))
                {
                    editor.WriteMessage(
                        "\nCE_PARKGRADECREATE stopped. The selected low point lies outside the parking boundary.");
                    return;
                }
                lowPoint = new Point3d(
                    pointResult.Value.X,
                    pointResult.Value.Y,
                    referenceElevation);
            }

            var settings = new ParkingGradeSettings(
                mode,
                slope,
                referenceElevation,
                spacing,
                lowPoint);
            List<IList<Point3d>> guides = ParkingGradeGuideStore.BuildGuides(boundary, settings);
            if (guides.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_PARKGRADECREATE stopped. No grading guide geometry could be generated from this boundary.");
                return;
            }

            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Mode", settings.Mode.ToString()),
                Pair("Slope", settings.SlopePercent.ToString("N3", CultureInfo.CurrentCulture) + "%"),
                Pair("Reference elevation", settings.ReferenceElevation.ToString("N3", CultureInfo.CurrentCulture)),
                Pair("Guide spacing", settings.Spacing.ToString("N3", CultureInfo.CurrentCulture)),
                Pair("Guide polylines", guides.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Output type", "Linked 3D polylines"),
                Pair("Automatic boundary refresh", ParkingOptionAutoRefreshManager.Enabled ? "Enabled" : "Disabled"),
                Pair("Engineering status", "Design assistance — verify grading, drainage paths, tie-ins and earthworks")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Parking Grading Guide",
                    "The command creates linked 3D guide polylines. It does not create a finished Civil 3D grading surface or replace drainage design review.",
                    review,
                    "Create Guides"))
            {
                editor.WriteMessage("\nCE_PARKGRADECREATE cancelled.");
                return;
            }

            try
            {
                int created = ParkingGradeGuideStore.ReplaceGuides(
                    document,
                    boundary,
                    settings,
                    guides);
                ParkingOptionAutoRefreshManager.RebuildCache(document.Database);
                editor.SetImpliedSelection(
                    ParkingGradeGuideStore.ReadGuideIds(
                        document.Database,
                        boundary.HandleText).ToArray());
                editor.Regen();
                editor.WriteMessage(
                    "\nCE_PARKGRADECREATE complete. Mode={0}; linked guides={1}; slope={2:N3}%.",
                    settings.Mode,
                    created,
                    settings.SlopePercent);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PARKGRADECREATE stopped. No guide transaction was committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_PARKGRADEREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshParkingGradeGuide()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            string boundaryHandle;
            if (!ParkingGradeGuideStore.PromptBoundaryOrGuide(document, out boundaryHandle)) return;
            int created;
            string error;
            if (!ParkingGradeGuideStore.RefreshBoundary(document, boundaryHandle, out created, out error))
            {
                document.Editor.WriteMessage(
                    "\nCE_PARKGRADEREFRESH stopped. {0}",
                    error);
                return;
            }
            ParkingOptionAutoRefreshManager.RebuildCache(document.Database);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PARKGRADEREFRESH complete. Linked guides recreated={0}.",
                created);
        }

        [CommandMethod("CE_TOOLS", "CE_PARKGRADEINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ParkingGradeInformation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            string boundaryHandle;
            if (!ParkingGradeGuideStore.PromptBoundaryOrGuide(document, out boundaryHandle)) return;
            ParkingGradeSettings settings;
            int guideCount;
            if (!ParkingGradeGuideStore.TryReadSettings(
                    document.Database,
                    boundaryHandle,
                    out settings,
                    out guideCount))
            {
                document.Editor.WriteMessage(
                    "\nCE_PARKGRADEINFO: no linked CE parking grading guide was found.");
                return;
            }
            ObjectId boundaryId;
            bool boundaryLive = ParkingGradeGuideStore.TryResolveHandle(
                document.Database,
                boundaryHandle,
                out boundaryId);
            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Boundary handle", boundaryHandle),
                Pair("Boundary live", boundaryLive ? "Yes" : "No"),
                Pair("Mode", settings.Mode.ToString()),
                Pair("Slope", settings.SlopePercent.ToString("N3", CultureInfo.CurrentCulture) + "%"),
                Pair("Reference elevation", settings.ReferenceElevation.ToString("N3", CultureInfo.CurrentCulture)),
                Pair("Guide spacing", settings.Spacing.ToString("N3", CultureInfo.CurrentCulture)),
                Pair("Linked guides", guideCount.ToString(CultureInfo.InvariantCulture)),
                Pair("Low point", settings.Mode == ParkingGradeMode.LowPoint
                    ? FormatPoint(settings.LowPoint)
                    : "<Not used>"),
                Pair("Automatic refresh", ParkingOptionAutoRefreshManager.Enabled ? "Enabled" : "Disabled")
            };
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Parking Grading Guide Information",
                "Linked 3D guide polylines are design-assistance geometry and must be checked against the final Civil 3D grading surface and drainage design.",
                rows,
                "CE TOOLS PARKING GRADING GUIDE INFORMATION");
        }

        [CommandMethod("CE_TOOLS", "CE_PARKGRADECLEAR", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ClearParkingGradeGuide()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            string boundaryHandle;
            if (!ParkingGradeGuideStore.PromptBoundaryOrGuide(document, out boundaryHandle)) return;
            List<ObjectId> guides = ParkingGradeGuideStore.ReadGuideIds(
                document.Database,
                boundaryHandle);
            if (guides.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_PARKGRADECLEAR: no linked guide geometry was found.");
                return;
            }
            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Boundary handle", boundaryHandle),
                Pair("Guide objects to remove", guides.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Source boundary retained", "Yes"),
                Pair("Parking bay layout retained", "Yes")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Clear Parking Grading Guides",
                    "Only linked CE parking grading guide objects will be erased.",
                    rows,
                    "Clear Guides"))
                return;
            int erased = ParkingGradeGuideStore.EraseGuides(
                document.Database,
                boundaryHandle);
            ParkingOptionAutoRefreshManager.RebuildCache(document.Database);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PARKGRADECLEAR complete. Guide objects removed={0}.",
                erased);
        }

        private static void RefreshAllNow(Document document)
        {
            int parkingCreated;
            int gradeCreated;
            int failed;
            ParkingOptionAutoRefreshManager.RefreshAllNow(
                document,
                out parkingCreated,
                out gradeCreated,
                out failed);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PARKAUTOREFRESHALL complete. Parking bays recreated={0}; grading guides recreated={1}; failed links={2}.",
                parkingCreated,
                gradeCreated,
                failed);
        }

        private static void ShowMonitorStatus(Document document)
        {
            ParkingMonitorStatus status = ParkingOptionAutoRefreshManager.ReadStatus(
                document.Database);
            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Automatic refresh", status.Enabled ? "Enabled" : "Disabled"),
                Pair("Linked parking boundaries", status.ParkingBoundaryCount.ToString(CultureInfo.InvariantCulture)),
                Pair("Linked grading boundaries", status.GradingBoundaryCount.ToString(CultureInfo.InvariantCulture)),
                Pair("Pending boundaries", status.PendingCount.ToString(CultureInfo.InvariantCulture)),
                Pair("Last successful refresh", status.LastRefreshText),
                Pair("Last failure", status.LastFailure),
                Pair("Trigger", "Source boundary modification after the AutoCAD command completes")
            };
            PopupTablePresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Dynamic Parking Monitor",
                "The monitor refreshes linked bay layouts and grading guides after source-boundary grip edits. It does not modify unrelated parking or grading objects.",
                rows,
                "CE TOOLS DYNAMIC PARKING MONITOR");
        }

        private static ParkingGradeMode ParseMode(string value)
        {
            ParkingGradeMode mode;
            return Enum.TryParse(value, true, out mode)
                ? mode
                : ParkingGradeMode.LowPoint;
        }

        private static bool PromptPositiveDouble(
            Editor editor,
            string name,
            double defaultValue,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + name + " <" +
                defaultValue.ToString("0.###", CultureInfo.InvariantCulture) +
                ">: ")
            {
                AllowNone = true,
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status == PromptStatus.OK;
        }

        private static bool PromptAnyDouble(
            Editor editor,
            string name,
            double defaultValue,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + name + " <" +
                defaultValue.ToString("0.###", CultureInfo.InvariantCulture) +
                ">: ")
            {
                AllowNone = true,
                AllowNegative = true,
                AllowZero = true,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status == PromptStatus.OK;
        }

        private static string FormatPoint(Point3d point)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                "X {0:N3}; Y {1:N3}; Z {2:N3}",
                point.X,
                point.Y,
                point.Z);
        }

        private static KeyValuePair<string, string> Pair(
            string key,
            string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal static class ParkingOptionAutoRefreshManager
    {
        private const string ParkingRegApp = "CE_PARK_OPTIONS";
        private const string GradeRegApp = "CE_PARK_GRADE_GUIDE";
        private static readonly HashSet<string> ParkingBoundaries =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> GradingBoundaries =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Pending =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static Database _database;
        private static bool _initialised;
        private static bool _busy;
        private static bool _cacheDirty = true;
        private static DateTime _lastCacheUtc = DateTime.MinValue;
        private static DateTime _lastRefreshUtc = DateTime.MinValue;
        private static string _lastFailure = string.Empty;

        public static bool Enabled { get; set; } = true;

        public static void Initialize()
        {
            if (_initialised) return;
            _initialised = true;
            AcApplication.Idle += OnIdle;
        }

        public static void Terminate()
        {
            if (!_initialised) return;
            AcApplication.Idle -= OnIdle;
            DetachDatabase();
            ParkingBoundaries.Clear();
            GradingBoundaries.Clear();
            Pending.Clear();
            _initialised = false;
        }

        public static void RebuildCache(Database database)
        {
            AttachDatabase(database);
            ReadBoundaryCache(database);
        }

        public static ParkingMonitorStatus ReadStatus(Database database)
        {
            AttachDatabase(database);
            if (_cacheDirty ||
                (DateTime.UtcNow - _lastCacheUtc).TotalSeconds > 3.0)
                ReadBoundaryCache(database);
            return new ParkingMonitorStatus(
                Enabled,
                ParkingBoundaries.Count,
                GradingBoundaries.Count,
                Pending.Count,
                _lastRefreshUtc == DateTime.MinValue
                    ? "<Not refreshed this session>"
                    : _lastRefreshUtc.ToLocalTime().ToString(
                        "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.CurrentCulture),
                string.IsNullOrWhiteSpace(_lastFailure)
                    ? "<None>"
                    : _lastFailure);
        }

        public static void RefreshAllNow(
            Document document,
            out int parkingCreated,
            out int gradingCreated,
            out int failed)
        {
            parkingCreated = 0;
            gradingCreated = 0;
            failed = 0;
            if (document == null) return;
            RebuildCache(document.Database);
            var handles = new HashSet<string>(ParkingBoundaries, StringComparer.OrdinalIgnoreCase);
            handles.UnionWith(GradingBoundaries);
            foreach (string handle in handles)
            {
                int parking;
                int grading;
                string error;
                if (RefreshBoundary(document, handle, out parking, out grading, out error))
                {
                    parkingCreated += parking;
                    gradingCreated += grading;
                }
                else
                {
                    failed++;
                    _lastFailure = error;
                }
            }
            if (failed == 0) _lastFailure = string.Empty;
            _lastRefreshUtc = DateTime.UtcNow;
            ReadBoundaryCache(document.Database);
        }

        private static void OnIdle(object sender, EventArgs eventArgs)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            AttachDatabase(document == null ? null : document.Database);
            if (document == null) return;
            if (_cacheDirty ||
                (DateTime.UtcNow - _lastCacheUtc).TotalSeconds > 3.0)
                ReadBoundaryCache(document.Database);
            if (!Enabled || _busy || Pending.Count == 0) return;
            string commandNames = Convert.ToString(
                AcApplication.GetSystemVariable("CMDNAMES"),
                CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(commandNames)) return;

            _busy = true;
            try
            {
                List<string> handles = Pending.ToList();
                Pending.Clear();
                using (DocumentLock documentLock = document.LockDocument())
                {
                    foreach (string handle in handles)
                    {
                        int parking;
                        int grading;
                        string error;
                        if (!RefreshBoundary(
                                document,
                                handle,
                                out parking,
                                out grading,
                                out error))
                            _lastFailure = error;
                        else
                            _lastFailure = string.Empty;
                    }
                }
                _lastRefreshUtc = DateTime.UtcNow;
                ReadBoundaryCache(document.Database);
            }
            catch (System.Exception exception)
            {
                _lastFailure = exception.Message;
                foreach (string handle in ParkingBoundaries)
                    Pending.Add(handle);
            }
            finally
            {
                _busy = false;
            }
        }

        private static bool RefreshBoundary(
            Document document,
            string boundaryHandle,
            out int parkingCreated,
            out int gradingCreated,
            out string error)
        {
            parkingCreated = 0;
            gradingCreated = 0;
            error = string.Empty;
            bool anyLink = false;
            if (ParkingBoundaries.Contains(boundaryHandle))
            {
                anyLink = true;
                string parkingError;
                if (!RefreshParkingOption(
                        document,
                        boundaryHandle,
                        out parkingCreated,
                        out parkingError))
                    error = parkingError;
            }
            if (GradingBoundaries.Contains(boundaryHandle))
            {
                anyLink = true;
                string gradingError;
                if (!ParkingGradeGuideStore.RefreshBoundary(
                        document,
                        boundaryHandle,
                        out gradingCreated,
                        out gradingError))
                    error = JoinErrors(error, gradingError);
            }
            return anyLink && string.IsNullOrWhiteSpace(error);
        }

        private static bool RefreshParkingOption(
            Document document,
            string boundaryHandle,
            out int created,
            out string error)
        {
            created = 0;
            error = string.Empty;
            try
            {
                ObjectId boundaryId;
                if (!ParkingGradeGuideStore.TryResolveHandle(
                        document.Database,
                        boundaryHandle,
                        out boundaryId))
                {
                    error = "Parking source boundary " + boundaryHandle + " is missing.";
                    return false;
                }

                Type type = typeof(AdvancedParkingPlanningCommands);
                MethodInfo readBoundary = type.GetMethod(
                    "ReadBoundary",
                    BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo readSettings = type.GetMethod(
                    "TryReadLinkedSettings",
                    BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo buildOption = type.GetMethod(
                    "BuildOption",
                    BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo replace = type.GetMethod(
                    "ReplaceLinkedOption",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (readBoundary == null || readSettings == null ||
                    buildOption == null || replace == null)
                {
                    error = "The boundary-parking refresh API is unavailable in this build.";
                    return false;
                }

                object boundary = readBoundary.Invoke(
                    null,
                    new object[] { document.Database, boundaryId });
                if (boundary == null)
                {
                    error = "The linked parking boundary is no longer a valid closed polyline.";
                    return false;
                }
                object[] settingsArguments =
                {
                    document.Database,
                    boundaryHandle,
                    null,
                    0
                };
                bool hasSettings = Convert.ToBoolean(
                    readSettings.Invoke(null, settingsArguments),
                    CultureInfo.InvariantCulture);
                if (!hasSettings || settingsArguments[2] == null)
                {
                    error = "The linked parking option settings are missing.";
                    return false;
                }
                object settings = settingsArguments[2];
                PropertyInfo angleProperty = settings.GetType().GetProperty(
                    "AngleDegrees",
                    BindingFlags.Public | BindingFlags.Instance);
                if (angleProperty == null)
                {
                    error = "The stored parking angle is unavailable.";
                    return false;
                }
                double angle = Convert.ToDouble(
                    angleProperty.GetValue(settings, null),
                    CultureInfo.InvariantCulture);
                object option = buildOption.Invoke(
                    null,
                    new[] { boundary, settings, (object)angle });
                created = Convert.ToInt32(
                    replace.Invoke(
                        null,
                        new[] { document, boundary, option, settings }),
                    CultureInfo.InvariantCulture);
                return true;
            }
            catch (TargetInvocationException exception)
            {
                error = exception.InnerException == null
                    ? exception.Message
                    : exception.InnerException.Message;
                return false;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static void AttachDatabase(Database database)
        {
            if (ReferenceEquals(_database, database)) return;
            DetachDatabase();
            _database = database;
            _cacheDirty = true;
            if (_database == null) return;
            _database.ObjectModified += OnObjectModified;
            _database.ObjectErased += OnObjectErased;
            _database.ObjectAppended += OnObjectAppended;
        }

        private static void DetachDatabase()
        {
            if (_database != null)
            {
                _database.ObjectModified -= OnObjectModified;
                _database.ObjectErased -= OnObjectErased;
                _database.ObjectAppended -= OnObjectAppended;
            }
            _database = null;
        }

        private static void OnObjectModified(
            object sender,
            ObjectEventArgs eventArgs)
        {
            if (_busy || eventArgs == null || eventArgs.DBObject == null) return;
            string handle = SafeHandle(eventArgs.DBObject);
            if (ParkingBoundaries.Contains(handle) ||
                GradingBoundaries.Contains(handle))
                Pending.Add(handle);
        }

        private static void OnObjectErased(
            object sender,
            ObjectErasedEventArgs eventArgs)
        {
            if (_busy || eventArgs == null || eventArgs.DBObject == null) return;
            string handle = SafeHandle(eventArgs.DBObject);
            if (ParkingBoundaries.Contains(handle) ||
                GradingBoundaries.Contains(handle))
                Pending.Add(handle);
            _cacheDirty = true;
        }

        private static void OnObjectAppended(
            object sender,
            ObjectEventArgs eventArgs)
        {
            if (!_busy) _cacheDirty = true;
        }

        private static void ReadBoundaryCache(Database database)
        {
            ParkingBoundaries.Clear();
            GradingBoundaries.Clear();
            if (database == null)
            {
                _cacheDirty = false;
                _lastCacheUtc = DateTime.UtcNow;
                return;
            }
            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace = transaction.GetObject(
                        database.CurrentSpaceId,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (currentSpace != null)
                    {
                        foreach (ObjectId objectId in currentSpace)
                        {
                            Entity entity = transaction.GetObject(
                                objectId,
                                OpenMode.ForRead,
                                false) as Entity;
                            if (entity == null) continue;
                            string parkingBoundary = ReadXDataValue(
                                entity,
                                ParkingRegApp,
                                "Boundary=");
                            if (!string.IsNullOrWhiteSpace(parkingBoundary))
                                ParkingBoundaries.Add(parkingBoundary);
                            string gradingBoundary = ReadXDataValue(
                                entity,
                                GradeRegApp,
                                "Boundary=");
                            if (!string.IsNullOrWhiteSpace(gradingBoundary))
                                GradingBoundaries.Add(gradingBoundary);
                        }
                    }
                }
            }
            catch
            {
                // Keep the monitor alive; status will show no cached links.
            }
            _cacheDirty = false;
            _lastCacheUtc = DateTime.UtcNow;
        }

        private static string ReadXDataValue(
            Entity entity,
            string regApp,
            string prefix)
        {
            ResultBuffer data = entity.GetXDataForApplication(regApp);
            if (data == null) return string.Empty;
            foreach (TypedValue value in data)
            {
                string text = value.Value as string;
                if (!string.IsNullOrWhiteSpace(text) &&
                    text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return text.Substring(prefix.Length);
            }
            return string.Empty;
        }

        private static string SafeHandle(DBObject value)
        {
            try
            {
                return value.ObjectId.IsNull
                    ? string.Empty
                    : value.ObjectId.Handle.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string JoinErrors(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first)) return second;
            if (string.IsNullOrWhiteSpace(second)) return first;
            return first + " | " + second;
        }
    }

    internal static class ParkingGradeGuideStore
    {
        private const string RegAppName = "CE_PARK_GRADE_GUIDE";
        private const string GuideLayer = "CE-PARK-GRADING-GUIDE";
        private const double Tolerance = 0.000001;

        public static ParkingGradeBoundary PromptBoundary(Document document)
        {
            var options = new PromptEntityOptions(
                "\nSelect one closed parking-area boundary polyline: ");
            options.SetRejectMessage("\nSelect a closed lightweight 2D polyline.");
            options.AddAllowedClass(typeof(Polyline), false);
            PromptEntityResult result = document.Editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return null;
            ParkingGradeBoundary boundary = ReadBoundary(
                document.Database,
                result.ObjectId);
            if (boundary == null)
            {
                document.Editor.WriteMessage(
                    "\nThe selected object is not a valid editable closed parking boundary.");
            }
            return boundary;
        }

        public static bool PromptBoundaryOrGuide(
            Document document,
            out string boundaryHandle)
        {
            boundaryHandle = string.Empty;
            var options = new PromptEntityOptions(
                "\nSelect the source parking boundary or one linked grading guide: ");
            options.SetRejectMessage("\nSelect a polyline or 3D polyline.");
            options.AddAllowedClass(typeof(Polyline), false);
            options.AddAllowedClass(typeof(Polyline3d), false);
            PromptEntityResult result = document.Editor.GetEntity(options);
            if (result.Status != PromptStatus.OK) return false;
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                Entity entity = transaction.GetObject(
                    result.ObjectId,
                    OpenMode.ForRead,
                    false) as Entity;
                ParkingGradeSettings settings;
                string linkedBoundary;
                if (entity != null &&
                    TryReadGuideLink(entity, out linkedBoundary, out settings))
                {
                    boundaryHandle = linkedBoundary;
                    return true;
                }
            }
            ParkingGradeBoundary boundary = ReadBoundary(
                document.Database,
                result.ObjectId);
            if (boundary == null)
            {
                document.Editor.WriteMessage(
                    "\nThe selected object is neither a valid boundary nor a linked grading guide.");
                return false;
            }
            boundaryHandle = boundary.HandleText;
            return true;
        }

        public static List<IList<Point3d>> BuildGuides(
            ParkingGradeBoundary boundary,
            ParkingGradeSettings settings)
        {
            if (settings.Mode == ParkingGradeMode.LowPoint)
                return BuildLowPointGuides(boundary, settings);
            return BuildCrownValleyGuides(boundary, settings);
        }

        public static int ReplaceGuides(
            Document document,
            ParkingGradeBoundary boundary,
            ParkingGradeSettings settings,
            IList<IList<Point3d>> guides)
        {
            Database database = document.Database;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(database, transaction);
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException(
                        "The current drawing space could not be opened.");
                EraseGuides(currentSpace, transaction, boundary.HandleText);
                ObjectId layerId = GetOrCreateLayer(
                    database,
                    transaction,
                    GuideLayer);
                int created = 0;
                for (int index = 0; index < guides.Count; index++)
                {
                    IList<Point3d> points = guides[index];
                    if (points == null || points.Count < 2) continue;
                    var collection = new Point3dCollection();
                    foreach (Point3d point in points) collection.Add(point);
                    var polyline = new Polyline3d(
                        Poly3dType.SimplePoly,
                        collection,
                        false);
                    polyline.SetDatabaseDefaults(database);
                    polyline.LayerId = layerId;
                    WriteGuideLink(
                        polyline,
                        boundary.HandleText,
                        settings,
                        index + 1);
                    currentSpace.AppendEntity(polyline);
                    transaction.AddNewlyCreatedDBObject(polyline, true);
                    created++;
                }
                transaction.Commit();
                return created;
            }
        }

        public static bool RefreshBoundary(
            Document document,
            string boundaryHandle,
            out int created,
            out string error)
        {
            created = 0;
            error = string.Empty;
            try
            {
                ParkingGradeSettings settings;
                int guideCount;
                if (!TryReadSettings(
                        document.Database,
                        boundaryHandle,
                        out settings,
                        out guideCount))
                {
                    error = "The linked parking grading settings are missing.";
                    return false;
                }
                ObjectId boundaryId;
                if (!TryResolveHandle(
                        document.Database,
                        boundaryHandle,
                        out boundaryId))
                {
                    error = "The linked parking grading boundary is missing.";
                    return false;
                }
                ParkingGradeBoundary boundary = ReadBoundary(
                    document.Database,
                    boundaryId);
                if (boundary == null)
                {
                    error = "The linked parking grading boundary is no longer valid.";
                    return false;
                }
                List<IList<Point3d>> guides = BuildGuides(boundary, settings);
                created = ReplaceGuides(
                    document,
                    boundary,
                    settings,
                    guides);
                return true;
            }
            catch (System.Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public static bool TryReadSettings(
            Database database,
            string boundaryHandle,
            out ParkingGradeSettings settings,
            out int guideCount)
        {
            settings = null;
            guideCount = 0;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (currentSpace == null) return false;
                foreach (ObjectId objectId in currentSpace)
                {
                    Entity entity = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as Entity;
                    string linkedBoundary;
                    ParkingGradeSettings candidate;
                    if (entity == null ||
                        !TryReadGuideLink(
                            entity,
                            out linkedBoundary,
                            out candidate) ||
                        !string.Equals(
                            linkedBoundary,
                            boundaryHandle,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    guideCount++;
                    if (settings == null) settings = candidate;
                }
            }
            return settings != null;
        }

        public static List<ObjectId> ReadGuideIds(
            Database database,
            string boundaryHandle)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (currentSpace == null) return result;
                foreach (ObjectId objectId in currentSpace)
                {
                    Entity entity = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as Entity;
                    string linkedBoundary;
                    ParkingGradeSettings settings;
                    if (entity != null &&
                        TryReadGuideLink(
                            entity,
                            out linkedBoundary,
                            out settings) &&
                        string.Equals(
                            linkedBoundary,
                            boundaryHandle,
                            StringComparison.OrdinalIgnoreCase))
                        result.Add(objectId);
                }
            }
            return result;
        }

        public static int EraseGuides(
            Database database,
            string boundaryHandle)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null) return 0;
                int erased = EraseGuides(
                    currentSpace,
                    transaction,
                    boundaryHandle);
                transaction.Commit();
                return erased;
            }
        }

        public static bool TryResolveHandle(
            Database database,
            string handleText,
            out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            long value;
            if (!long.TryParse(
                    handleText,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out value))
                return false;
            try
            {
                objectId = database.GetObjectId(
                    false,
                    new Handle(value),
                    0);
                return !objectId.IsNull && !objectId.IsErased;
            }
            catch
            {
                return false;
            }
        }

        public static bool PointInPolygon(
            IList<Point2d> polygon,
            Point2d point)
        {
            if (polygon == null || polygon.Count < 3) return false;
            bool inside = false;
            int previous = polygon.Count - 1;
            for (int current = 0; current < polygon.Count; current++)
            {
                Point2d first = polygon[previous];
                Point2d second = polygon[current];
                if (PointOnSegment(first, second, point)) return true;
                bool crosses =
                    ((second.Y > point.Y) != (first.Y > point.Y)) &&
                    (point.X <
                     ((first.X - second.X) *
                      (point.Y - second.Y) /
                      ((first.Y - second.Y) + Tolerance)) + second.X);
                if (crosses) inside = !inside;
                previous = current;
            }
            return inside;
        }

        private static List<IList<Point3d>> BuildLowPointGuides(
            ParkingGradeBoundary boundary,
            ParkingGradeSettings settings)
        {
            var result = new List<IList<Point3d>>();
            Point2d lowLocal = boundary.ToLocal(settings.LowPoint);
            var boundary3d = new List<Point3d>();
            for (int index = 0; index < boundary.Polygon.Count; index++)
            {
                Point2d local = boundary.Polygon[index];
                double distance = local.GetDistanceTo(lowLocal);
                Point3d outer = boundary.ToWorld(
                    local,
                    settings.ReferenceElevation +
                    distance * settings.SlopeRatio);
                boundary3d.Add(outer);
                result.Add(new List<Point3d>
                {
                    outer,
                    new Point3d(
                        settings.LowPoint.X,
                        settings.LowPoint.Y,
                        settings.ReferenceElevation)
                });
            }
            if (boundary3d.Count > 2)
            {
                boundary3d.Add(boundary3d[0]);
                result.Add(boundary3d);
            }
            return result;
        }

        private static List<IList<Point3d>> BuildCrownValleyGuides(
            ParkingGradeBoundary boundary,
            ParkingGradeSettings settings)
        {
            var result = new List<IList<Point3d>>();
            double spacing = Math.Max(settings.Spacing, Tolerance);
            var stations = new List<double> { boundary.MinX };
            double first = Math.Ceiling(boundary.MinX / spacing) * spacing;
            if (Math.Abs(first - boundary.MinX) < Tolerance)
                first += spacing;
            for (double x = first; x < boundary.MaxX - Tolerance; x += spacing)
                stations.Add(x);
            if (boundary.MaxX > boundary.MinX + Tolerance)
                stations.Add(boundary.MaxX);

            foreach (double x in stations.Distinct())
            {
                List<double> intersections = VerticalIntersections(
                    boundary.Polygon,
                    x);
                for (int pair = 0; pair + 1 < intersections.Count; pair += 2)
                {
                    double lowY = intersections[pair];
                    double highY = intersections[pair + 1];
                    if (highY - lowY <= Tolerance) continue;
                    double centreY = (lowY + highY) / 2.0;
                    double halfWidth = (highY - lowY) / 2.0;
                    double edgeElevation;
                    double centreElevation;
                    if (settings.Mode == ParkingGradeMode.Crown)
                    {
                        edgeElevation = settings.ReferenceElevation;
                        centreElevation = settings.ReferenceElevation +
                            halfWidth * settings.SlopeRatio;
                    }
                    else if (settings.Mode == ParkingGradeMode.Valley)
                    {
                        centreElevation = settings.ReferenceElevation;
                        edgeElevation = settings.ReferenceElevation +
                            halfWidth * settings.SlopeRatio;
                    }
                    else
                    {
                        continue;
                    }
                    result.Add(new List<Point3d>
                    {
                        boundary.ToWorld(
                            new Point2d(x, lowY),
                            edgeElevation),
                        boundary.ToWorld(
                            new Point2d(x, centreY),
                            centreElevation),
                        boundary.ToWorld(
                            new Point2d(x, highY),
                            edgeElevation)
                    });
                }
            }
            return result;
        }

        private static List<double> VerticalIntersections(
            IList<Point2d> polygon,
            double x)
        {
            var values = new List<double>();
            int previous = polygon.Count - 1;
            for (int current = 0; current < polygon.Count; current++)
            {
                Point2d first = polygon[previous];
                Point2d second = polygon[current];
                double minX = Math.Min(first.X, second.X);
                double maxX = Math.Max(first.X, second.X);
                if (x < minX - Tolerance || x > maxX + Tolerance)
                {
                    previous = current;
                    continue;
                }
                double dx = second.X - first.X;
                if (Math.Abs(dx) <= Tolerance)
                {
                    if (Math.Abs(x - first.X) <= Tolerance)
                    {
                        values.Add(first.Y);
                        values.Add(second.Y);
                    }
                }
                else
                {
                    double t = (x - first.X) / dx;
                    if (t >= -Tolerance && t <= 1.0 + Tolerance)
                        values.Add(first.Y +
                            (second.Y - first.Y) * t);
                }
                previous = current;
            }
            return values
                .OrderBy(value => value)
                .GroupBy(value => Math.Round(value, 8))
                .Select(group => group.First())
                .ToList();
        }

        private static ParkingGradeBoundary ReadBoundary(
            Database database,
            ObjectId objectId)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                Polyline polyline = transaction.GetObject(
                    objectId,
                    OpenMode.ForRead,
                    false) as Polyline;
                if (polyline == null ||
                    !polyline.Closed ||
                    polyline.NumberOfVertices < 3 ||
                    Math.Abs(polyline.Area) <= Tolerance)
                    return null;
                LayerTableRecord layer = transaction.GetObject(
                    polyline.LayerId,
                    OpenMode.ForRead,
                    false) as LayerTableRecord;
                if (layer != null && layer.IsLocked) return null;

                var worldPoints = new List<Point3d>();
                for (int segment = 0;
                     segment < polyline.NumberOfVertices;
                     segment++)
                {
                    SegmentType segmentType = polyline.GetSegmentType(segment);
                    int samples = segmentType == SegmentType.Arc ? 12 : 1;
                    for (int sample = 0; sample < samples; sample++)
                    {
                        double parameter = segment +
                            sample / (double)samples;
                        Point3d point = polyline.GetPointAtParameter(parameter);
                        if (worldPoints.Count == 0 ||
                            worldPoints[worldPoints.Count - 1]
                                .DistanceTo(point) > Tolerance)
                            worldPoints.Add(point);
                    }
                }
                if (worldPoints.Count < 3) return null;

                int longestIndex = 0;
                double longest = 0.0;
                for (int index = 0; index < worldPoints.Count; index++)
                {
                    Point3d first = worldPoints[index];
                    Point3d second = worldPoints[
                        (index + 1) % worldPoints.Count];
                    double length = new Vector2d(
                        second.X - first.X,
                        second.Y - first.Y).Length;
                    if (length > longest)
                    {
                        longest = length;
                        longestIndex = index;
                    }
                }
                if (longest <= Tolerance) return null;
                Point3d origin = worldPoints[longestIndex];
                Point3d next = worldPoints[
                    (longestIndex + 1) % worldPoints.Count];
                Vector3d direction = new Vector3d(
                    next.X - origin.X,
                    next.Y - origin.Y,
                    0.0).GetNormal();
                Vector3d normal = Vector3d.ZAxis
                    .CrossProduct(direction)
                    .GetNormal();
                var local = new List<Point2d>();
                foreach (Point3d world in worldPoints)
                {
                    Vector3d offset = world - origin;
                    local.Add(new Point2d(
                        offset.DotProduct(direction),
                        offset.DotProduct(normal)));
                }
                return new ParkingGradeBoundary(
                    objectId,
                    objectId.Handle.ToString(),
                    polyline.Elevation,
                    polyline.Length,
                    origin,
                    direction,
                    normal,
                    local);
            }
        }

        private static int EraseGuides(
            BlockTableRecord currentSpace,
            Transaction transaction,
            string boundaryHandle)
        {
            int erased = 0;
            foreach (ObjectId objectId in
                currentSpace.Cast<ObjectId>().ToList())
            {
                Entity entity = transaction.GetObject(
                    objectId,
                    OpenMode.ForRead,
                    false) as Entity;
                string linkedBoundary;
                ParkingGradeSettings settings;
                if (entity == null ||
                    !TryReadGuideLink(
                        entity,
                        out linkedBoundary,
                        out settings) ||
                    !string.Equals(
                        linkedBoundary,
                        boundaryHandle,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                entity.UpgradeOpen();
                entity.Erase();
                erased++;
            }
            return erased;
        }

        private static void WriteGuideLink(
            Entity entity,
            string boundaryHandle,
            ParkingGradeSettings settings,
            int index)
        {
            entity.XData = new ResultBuffer(
                new TypedValue(
                    (int)DxfCode.ExtendedDataRegAppName,
                    RegAppName),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Boundary=" + boundaryHandle),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Mode=" + settings.Mode),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Slope=" + settings.SlopePercent.ToString(
                        "R",
                        CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Reference=" + settings.ReferenceElevation.ToString(
                        "R",
                        CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Spacing=" + settings.Spacing.ToString(
                        "R",
                        CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "LowX=" + settings.LowPoint.X.ToString(
                        "R",
                        CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "LowY=" + settings.LowPoint.Y.ToString(
                        "R",
                        CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "LowZ=" + settings.LowPoint.Z.ToString(
                        "R",
                        CultureInfo.InvariantCulture)),
                new TypedValue(
                    (int)DxfCode.ExtendedDataAsciiString,
                    "Index=" + index.ToString(
                        CultureInfo.InvariantCulture)));
        }

        private static bool TryReadGuideLink(
            Entity entity,
            out string boundaryHandle,
            out ParkingGradeSettings settings)
        {
            boundaryHandle = string.Empty;
            settings = null;
            ResultBuffer data = entity.GetXDataForApplication(RegAppName);
            if (data == null) return false;
            ParkingGradeMode mode = ParkingGradeMode.LowPoint;
            double slope = 2.0;
            double reference = 0.0;
            double spacing = 10.0;
            double lowX = 0.0;
            double lowY = 0.0;
            double lowZ = 0.0;
            foreach (TypedValue value in data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.StartsWith(
                        "Boundary=",
                        StringComparison.OrdinalIgnoreCase))
                    boundaryHandle = text.Substring("Boundary=".Length);
                else if (text.StartsWith(
                        "Mode=",
                        StringComparison.OrdinalIgnoreCase))
                    Enum.TryParse(
                        text.Substring("Mode=".Length),
                        true,
                        out mode);
                else if (text.StartsWith(
                        "Slope=",
                        StringComparison.OrdinalIgnoreCase))
                    TryParse(text.Substring("Slope=".Length), out slope);
                else if (text.StartsWith(
                        "Reference=",
                        StringComparison.OrdinalIgnoreCase))
                    TryParse(text.Substring("Reference=".Length), out reference);
                else if (text.StartsWith(
                        "Spacing=",
                        StringComparison.OrdinalIgnoreCase))
                    TryParse(text.Substring("Spacing=".Length), out spacing);
                else if (text.StartsWith(
                        "LowX=",
                        StringComparison.OrdinalIgnoreCase))
                    TryParse(text.Substring("LowX=".Length), out lowX);
                else if (text.StartsWith(
                        "LowY=",
                        StringComparison.OrdinalIgnoreCase))
                    TryParse(text.Substring("LowY=".Length), out lowY);
                else if (text.StartsWith(
                        "LowZ=",
                        StringComparison.OrdinalIgnoreCase))
                    TryParse(text.Substring("LowZ=".Length), out lowZ);
            }
            if (string.IsNullOrWhiteSpace(boundaryHandle) ||
                slope <= 0.0 || spacing <= 0.0)
                return false;
            settings = new ParkingGradeSettings(
                mode,
                slope,
                reference,
                spacing,
                new Point3d(lowX, lowY, lowZ));
            return true;
        }

        private static bool TryParse(string text, out double value)
        {
            return double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static bool PointOnSegment(
            Point2d first,
            Point2d second,
            Point2d point)
        {
            Vector2d segment = second - first;
            Vector2d offset = point - first;
            double cross = segment.X * offset.Y -
                segment.Y * offset.X;
            if (Math.Abs(cross) > Tolerance) return false;
            double dot = offset.DotProduct(segment);
            return dot >= -Tolerance &&
                dot <= segment.LengthSqrd + Tolerance;
        }

        private static void EnsureRegApp(
            Database database,
            Transaction transaction)
        {
            RegAppTable table = transaction.GetObject(
                database.RegAppTableId,
                OpenMode.ForRead,
                false) as RegAppTable;
            if (table == null || table.Has(RegAppName)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = RegAppName };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string name)
        {
            LayerTable table = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (table == null)
                throw new InvalidOperationException(
                    "The layer table could not be opened.");
            if (table.Has(name)) return table[name];
            table.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId objectId = table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return objectId;
        }
    }

    internal enum ParkingGradeMode
    {
        LowPoint,
        Crown,
        Valley
    }

    internal sealed class ParkingGradeSettings
    {
        public ParkingGradeSettings(
            ParkingGradeMode mode,
            double slopePercent,
            double referenceElevation,
            double spacing,
            Point3d lowPoint)
        {
            Mode = mode;
            SlopePercent = slopePercent;
            ReferenceElevation = referenceElevation;
            Spacing = spacing;
            LowPoint = lowPoint;
        }

        public ParkingGradeMode Mode { get; private set; }
        public double SlopePercent { get; private set; }
        public double SlopeRatio
        {
            get { return SlopePercent / 100.0; }
        }
        public double ReferenceElevation { get; private set; }
        public double Spacing { get; private set; }
        public Point3d LowPoint { get; private set; }
    }

    internal sealed class ParkingGradeBoundary
    {
        public ParkingGradeBoundary(
            ObjectId objectId,
            string handleText,
            double elevation,
            double length,
            Point3d origin,
            Vector3d direction,
            Vector3d normal,
            IList<Point2d> polygon)
        {
            ObjectId = objectId;
            HandleText = handleText;
            Elevation = elevation;
            Length = length;
            Origin = origin;
            Direction = direction;
            Normal = normal;
            Polygon = polygon.ToList();
            MinX = Polygon.Min(point => point.X);
            MaxX = Polygon.Max(point => point.X);
            MinY = Polygon.Min(point => point.Y);
            MaxY = Polygon.Max(point => point.Y);
        }

        public ObjectId ObjectId { get; private set; }
        public string HandleText { get; private set; }
        public double Elevation { get; private set; }
        public double Length { get; private set; }
        public Point3d Origin { get; private set; }
        public Vector3d Direction { get; private set; }
        public Vector3d Normal { get; private set; }
        public List<Point2d> Polygon { get; private set; }
        public double MinX { get; private set; }
        public double MaxX { get; private set; }
        public double MinY { get; private set; }
        public double MaxY { get; private set; }
        public Point3d CentreWorld
        {
            get
            {
                return ToWorld(
                    new Point2d(
                        (MinX + MaxX) / 2.0,
                        (MinY + MaxY) / 2.0),
                    Elevation);
            }
        }

        public Point2d ToLocal(Point3d point)
        {
            Vector3d offset = point - Origin;
            return new Point2d(
                offset.DotProduct(Direction),
                offset.DotProduct(Normal));
        }

        public Point3d ToWorld(Point2d point, double elevation)
        {
            Point3d plan = Origin +
                Direction * point.X +
                Normal * point.Y;
            return new Point3d(plan.X, plan.Y, elevation);
        }
    }

    internal sealed class ParkingMonitorStatus
    {
        public ParkingMonitorStatus(
            bool enabled,
            int parkingBoundaryCount,
            int gradingBoundaryCount,
            int pendingCount,
            string lastRefreshText,
            string lastFailure)
        {
            Enabled = enabled;
            ParkingBoundaryCount = parkingBoundaryCount;
            GradingBoundaryCount = gradingBoundaryCount;
            PendingCount = pendingCount;
            LastRefreshText = lastRefreshText;
            LastFailure = lastFailure;
        }

        public bool Enabled { get; private set; }
        public int ParkingBoundaryCount { get; private set; }
        public int GradingBoundaryCount { get; private set; }
        public int PendingCount { get; private set; }
        public string LastRefreshText { get; private set; }
        public string LastFailure { get; private set; }
    }
}
