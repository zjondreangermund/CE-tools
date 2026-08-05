using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Records CE command executions, CE command duration and active drawing
    /// time per DWG. Data is user-local, but each saved drawing has its own
    /// isolated project record so a new drawing always starts empty.
    /// </summary>
    internal static class CommandUsageTracker
    {
        private const string ProjectRow = "P";
        private const string CommandRow = "C";
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, ProjectUsageRecord> ProjectsByKey =
            new Dictionary<string, ProjectUsageRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Document, ActiveCommandUsage> ActiveCommands =
            new Dictionary<Document, ActiveCommandUsage>();
        private static readonly Dictionary<Document, string> DocumentKeys =
            new Dictionary<Document, string>();
        private static Document _activeProjectDocument;
        private static DateTime _activeProjectStartedUtc = DateTime.MinValue;
        private static bool _initialized;

        public static event EventHandler UsageChanged;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            Load();
            AcApplication.DocumentManager.DocumentCreated += OnDocumentCreated;
            AcApplication.DocumentManager.DocumentActivated += OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            foreach (Document document in AcApplication.DocumentManager)
                Attach(document);
            Activate(AcApplication.DocumentManager.MdiActiveDocument);
        }

        public static void Terminate()
        {
            if (!_initialized) return;
            AcApplication.DocumentManager.DocumentCreated -= OnDocumentCreated;
            AcApplication.DocumentManager.DocumentActivated -= OnDocumentActivated;
            AcApplication.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            CheckpointActiveProject();
            foreach (Document document in ActiveCommands.Keys.ToList()) Detach(document);
            Save();
            _activeProjectDocument = null;
            _activeProjectStartedUtc = DateTime.MinValue;
            _initialized = false;
        }

        public static string CurrentProjectKey()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return string.Empty;
            lock (SyncRoot)
            {
                return EnsureProjectUnsafe(document, true).Key;
            }
        }

        public static IList<ProjectUsageSummary> Projects(int maximum)
        {
            lock (SyncRoot)
            {
                EnsureProjectUnsafe(
                    AcApplication.DocumentManager.MdiActiveDocument,
                    true);
                return ProjectsByKey.Values
                    .OrderByDescending(item => item.LastOpenedUtc)
                    .Take(Math.Max(1, maximum))
                    .Select(CreateSummaryUnsafe)
                    .ToList();
            }
        }

        public static ProjectUsageSummary Summary(string projectKey)
        {
            lock (SyncRoot)
            {
                ProjectUsageRecord project = FindProjectUnsafe(projectKey);
                return project == null
                    ? new ProjectUsageSummary { Key = projectKey ?? string.Empty }
                    : CreateSummaryUnsafe(project);
            }
        }

        public static IList<CommandUsageRecord> Favorites(string projectKey)
        {
            lock (SyncRoot)
            {
                ProjectUsageRecord project = FindProjectUnsafe(projectKey);
                if (project == null) return new List<CommandUsageRecord>();
                return project.Commands.Values
                    .Where(item => item.IsFavorite)
                    .OrderByDescending(item => item.Clicks)
                    .ThenByDescending(item => item.LastUsedUtc)
                    .Select(item => item.Clone())
                    .ToList();
            }
        }

        public static IList<CommandUsageRecord> MostUsed(string projectKey, int maximum)
        {
            lock (SyncRoot)
            {
                ProjectUsageRecord project = FindProjectUnsafe(projectKey);
                if (project == null) return new List<CommandUsageRecord>();
                return project.Commands.Values
                    .Where(item => item.Clicks > 0)
                    .OrderByDescending(item => item.Clicks)
                    .ThenByDescending(item => item.TotalSeconds)
                    .Take(Math.Max(1, maximum))
                    .Select(item => item.Clone())
                    .ToList();
            }
        }

        public static IList<CommandUsageRecord> Recent(string projectKey, int maximum)
        {
            lock (SyncRoot)
            {
                ProjectUsageRecord project = FindProjectUnsafe(projectKey);
                if (project == null) return new List<CommandUsageRecord>();
                return project.Commands.Values
                    .Where(item => item.Clicks > 0)
                    .OrderByDescending(item => item.LastUsedUtc)
                    .Take(Math.Max(1, maximum))
                    .Select(item => item.Clone())
                    .ToList();
            }
        }

        public static CommandUsageRecord Read(string projectKey, string command)
        {
            string commandKey = NormalizeCommand(command);
            lock (SyncRoot)
            {
                ProjectUsageRecord project = FindProjectUnsafe(projectKey);
                CommandUsageRecord value;
                return project != null && project.Commands.TryGetValue(commandKey, out value)
                    ? value.Clone()
                    : new CommandUsageRecord { Command = commandKey };
            }
        }

        public static void ToggleFavorite(string projectKey, string command)
        {
            string commandKey = NormalizeCommand(command);
            if (commandKey.Length == 0) return;
            lock (SyncRoot)
            {
                ProjectUsageRecord project = FindProjectUnsafe(projectKey) ??
                    EnsureProjectUnsafe(
                        AcApplication.DocumentManager.MdiActiveDocument,
                        true);
                CommandUsageRecord value;
                if (!project.Commands.TryGetValue(commandKey, out value))
                {
                    value = new CommandUsageRecord { Command = commandKey };
                    project.Commands.Add(commandKey, value);
                }
                value.IsFavorite = !value.IsFavorite;
                SaveUnsafe();
            }
            RaiseUsageChanged();
        }

        public static void ClearProject(string projectKey)
        {
            lock (SyncRoot)
            {
                ProjectUsageRecord project = FindProjectUnsafe(projectKey);
                if (project == null) return;
                if (_activeProjectDocument != null &&
                    string.Equals(
                        ProjectKeyUnsafe(_activeProjectDocument),
                        project.Key,
                        StringComparison.OrdinalIgnoreCase))
                    _activeProjectStartedUtc = DateTime.UtcNow;
                project.ActiveSeconds = 0.0;
                project.Commands.Clear();
                project.LastOpenedUtc = DateTime.UtcNow;
                SaveUnsafe();
            }
            RaiseUsageChanged();
        }

        private static void OnDocumentCreated(
            object sender,
            DocumentCollectionEventArgs args)
        {
            Attach(args.Document);
            lock (SyncRoot) EnsureProjectUnsafe(args.Document, true);
            RaiseUsageChanged();
        }

        private static void OnDocumentActivated(
            object sender,
            DocumentCollectionEventArgs args)
        {
            Activate(args.Document);
            RaiseUsageChanged();
        }

        private static void OnDocumentToBeDestroyed(
            object sender,
            DocumentCollectionEventArgs args)
        {
            if (ReferenceEquals(_activeProjectDocument, args.Document))
            {
                CheckpointActiveProject();
                _activeProjectDocument = null;
                _activeProjectStartedUtc = DateTime.MinValue;
            }
            Detach(args.Document);
            RaiseUsageChanged();
        }

        private static void Attach(Document document)
        {
            if (document == null || ActiveCommands.ContainsKey(document)) return;
            ActiveCommands.Add(document, null);
            lock (SyncRoot) EnsureProjectUnsafe(document, true);
            document.CommandWillStart += OnCommandWillStart;
            document.CommandEnded += OnCommandEnded;
            document.CommandCancelled += OnCommandEnded;
            document.CommandFailed += OnCommandEnded;
        }

        private static void Detach(Document document)
        {
            if (document == null || !ActiveCommands.ContainsKey(document)) return;
            document.CommandWillStart -= OnCommandWillStart;
            document.CommandEnded -= OnCommandEnded;
            document.CommandCancelled -= OnCommandEnded;
            document.CommandFailed -= OnCommandEnded;
            ActiveCommands.Remove(document);
            DocumentKeys.Remove(document);
        }

        private static void Activate(Document document)
        {
            CheckpointActiveProject();
            _activeProjectDocument = document;
            _activeProjectStartedUtc = document == null
                ? DateTime.MinValue
                : DateTime.UtcNow;
            lock (SyncRoot)
            {
                ProjectUsageRecord project = EnsureProjectUnsafe(document, true);
                if (project != null) project.LastOpenedUtc = DateTime.UtcNow;
                SaveUnsafe();
            }
        }

        private static void CheckpointActiveProject()
        {
            lock (SyncRoot)
            {
                if (_activeProjectDocument == null ||
                    _activeProjectStartedUtc == DateTime.MinValue) return;
                ProjectUsageRecord project = EnsureProjectUnsafe(
                    _activeProjectDocument,
                    true);
                DateTime now = DateTime.UtcNow;
                project.ActiveSeconds += Math.Max(
                    0.0,
                    (now - _activeProjectStartedUtc).TotalSeconds);
                _activeProjectStartedUtc = now;
                SaveUnsafe();
            }
        }

        private static void OnCommandWillStart(object sender, CommandEventArgs args)
        {
            Document document = sender as Document;
            string command = NormalizeCommand(
                args == null ? string.Empty : args.GlobalCommandName);
            if (document == null ||
                !command.StartsWith("CE_", StringComparison.OrdinalIgnoreCase))
                return;
            string projectKey;
            lock (SyncRoot)
            {
                projectKey = EnsureProjectUnsafe(document, true).Key;
            }
            ActiveCommands[document] = new ActiveCommandUsage
            {
                Command = command,
                ProjectKey = projectKey,
                StartedUtc = DateTime.UtcNow
            };
        }

        private static void OnCommandEnded(object sender, CommandEventArgs args)
        {
            Document document = sender as Document;
            if (document == null) return;
            ActiveCommandUsage active;
            if (!ActiveCommands.TryGetValue(document, out active) || active == null) return;
            ActiveCommands[document] = null;

            double seconds = Math.Max(
                0.0,
                (DateTime.UtcNow - active.StartedUtc).TotalSeconds);
            lock (SyncRoot)
            {
                ProjectUsageRecord project = FindProjectUnsafe(active.ProjectKey) ??
                    EnsureProjectUnsafe(document, true);
                CommandUsageRecord value;
                if (!project.Commands.TryGetValue(active.Command, out value))
                {
                    value = new CommandUsageRecord { Command = active.Command };
                    project.Commands.Add(active.Command, value);
                }
                value.Clicks++;
                value.TotalSeconds += seconds;
                value.LastUsedUtc = DateTime.UtcNow;
                project.LastOpenedUtc = DateTime.UtcNow;
                CheckpointActiveProjectUnsafe();
                SaveUnsafe();
            }
            RaiseUsageChanged();
        }

        private static void CheckpointActiveProjectUnsafe()
        {
            if (_activeProjectDocument == null ||
                _activeProjectStartedUtc == DateTime.MinValue) return;
            ProjectUsageRecord project = EnsureProjectUnsafe(
                _activeProjectDocument,
                true);
            DateTime now = DateTime.UtcNow;
            project.ActiveSeconds += Math.Max(
                0.0,
                (now - _activeProjectStartedUtc).TotalSeconds);
            _activeProjectStartedUtc = now;
        }

        private static ProjectUsageRecord EnsureProjectUnsafe(
            Document document,
            bool markOpened)
        {
            if (document == null)
                return new ProjectUsageRecord();

            string previousKey;
            DocumentKeys.TryGetValue(document, out previousKey);
            ProjectIdentity identity = ReadIdentity(document, previousKey);
            ProjectUsageRecord project;
            if (!ProjectsByKey.TryGetValue(identity.Key, out project))
            {
                project = new ProjectUsageRecord
                {
                    Key = identity.Key,
                    DisplayName = identity.DisplayName,
                    FullName = identity.FullName,
                    Persist = identity.Persist,
                    LastOpenedUtc = DateTime.UtcNow
                };
                ProjectsByKey.Add(project.Key, project);
            }

            if (!string.IsNullOrWhiteSpace(previousKey) &&
                !string.Equals(previousKey, identity.Key, StringComparison.OrdinalIgnoreCase))
            {
                ProjectUsageRecord previous;
                if (ProjectsByKey.TryGetValue(previousKey, out previous) &&
                    !previous.Persist)
                {
                    project.ActiveSeconds += previous.ActiveSeconds;
                    foreach (KeyValuePair<string, CommandUsageRecord> item in previous.Commands)
                    {
                        CommandUsageRecord existing;
                        if (!project.Commands.TryGetValue(item.Key, out existing))
                        {
                            project.Commands[item.Key] = item.Value;
                            continue;
                        }
                        existing.Clicks += item.Value.Clicks;
                        existing.TotalSeconds += item.Value.TotalSeconds;
                        existing.IsFavorite = existing.IsFavorite || item.Value.IsFavorite;
                        if (item.Value.LastUsedUtc > existing.LastUsedUtc)
                            existing.LastUsedUtc = item.Value.LastUsedUtc;
                    }
                    ProjectsByKey.Remove(previousKey);
                }
            }

            project.DisplayName = identity.DisplayName;
            project.FullName = identity.FullName;
            project.Persist = identity.Persist;
            if (markOpened) project.LastOpenedUtc = DateTime.UtcNow;
            DocumentKeys[document] = project.Key;
            return project;
        }

        private static string ProjectKeyUnsafe(Document document)
        {
            if (document == null) return string.Empty;
            return EnsureProjectUnsafe(document, false).Key;
        }

        private static ProjectIdentity ReadIdentity(
            Document document,
            string existingKey)
        {
            string filename = string.Empty;
            try
            {
                filename = document.Database == null
                    ? string.Empty
                    : document.Database.Filename;
            }
            catch
            {
                filename = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(filename))
            {
                string fullName;
                try { fullName = Path.GetFullPath(filename); }
                catch { fullName = filename.Trim(); }
                return new ProjectIdentity(
                    "FILE|" + fullName.ToUpperInvariant(),
                    Path.GetFileName(fullName),
                    fullName,
                    true);
            }

            string displayName;
            try { displayName = Path.GetFileName(document.Name); }
            catch { displayName = "New Drawing"; }
            if (string.IsNullOrWhiteSpace(displayName)) displayName = "New Drawing";
            string key = !string.IsNullOrWhiteSpace(existingKey) &&
                existingKey.StartsWith("UNSAVED|", StringComparison.OrdinalIgnoreCase)
                ? existingKey
                : "UNSAVED|" + Guid.NewGuid().ToString("N");
            return new ProjectIdentity(
                key,
                displayName + " (unsaved)",
                displayName,
                false);
        }

        private static ProjectUsageRecord FindProjectUnsafe(string projectKey)
        {
            ProjectUsageRecord project;
            return !string.IsNullOrWhiteSpace(projectKey) &&
                   ProjectsByKey.TryGetValue(projectKey, out project)
                ? project
                : null;
        }

        private static ProjectUsageSummary CreateSummaryUnsafe(ProjectUsageRecord project)
        {
            double activeSeconds = project.ActiveSeconds;
            string activeKey = string.Empty;
            if (_activeProjectDocument != null)
                DocumentKeys.TryGetValue(_activeProjectDocument, out activeKey);
            if (_activeProjectDocument != null &&
                _activeProjectStartedUtc != DateTime.MinValue &&
                string.Equals(
                    activeKey,
                    project.Key,
                    StringComparison.OrdinalIgnoreCase))
                activeSeconds += Math.Max(
                    0.0,
                    (DateTime.UtcNow - _activeProjectStartedUtc).TotalSeconds);

            return new ProjectUsageSummary
            {
                Key = project.Key,
                DisplayName = project.DisplayName,
                FullName = project.FullName,
                LastOpenedUtc = project.LastOpenedUtc,
                ActiveSeconds = activeSeconds,
                Clicks = project.Commands.Values.Sum(item => item.Clicks),
                CommandSeconds = project.Commands.Values.Sum(item => item.TotalSeconds),
                EstimatedClicksSaved = project.Commands.Values.Sum(
                    item => item.EstimatedClicksSaved),
                EstimatedSecondsSaved = project.Commands.Values.Sum(
                    item => item.EstimatedSecondsSaved)
            };
        }

        private static void Load()
        {
            lock (SyncRoot)
            {
                ProjectsByKey.Clear();
                string path = StoragePath();
                if (!File.Exists(path)) return;
                try
                {
                    foreach (string line in File.ReadAllLines(path))
                    {
                        string[] values = line.Split('\t');
                        if (values.Length == 0) continue;
                        if (values[0] == ProjectRow && values.Length >= 6)
                            LoadProject(values);
                        else if (values[0] == CommandRow && values.Length >= 8)
                            LoadCommand(values);
                        // Legacy global rows are deliberately ignored. They
                        // cannot be assigned safely to a specific DWG.
                    }
                }
                catch
                {
                    ProjectsByKey.Clear();
                }
            }
        }

        private static void LoadProject(string[] values)
        {
            string key = Decode(values[1]);
            if (string.IsNullOrWhiteSpace(key)) return;
            long ticks;
            double activeSeconds;
            if (!long.TryParse(values[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) ||
                !double.TryParse(values[5], NumberStyles.Float, CultureInfo.InvariantCulture, out activeSeconds))
                return;
            ProjectsByKey[key] = new ProjectUsageRecord
            {
                Key = key,
                DisplayName = Decode(values[2]),
                FullName = Decode(values[3]),
                LastOpenedUtc = ticks > 0
                    ? new DateTime(ticks, DateTimeKind.Utc)
                    : DateTime.MinValue,
                ActiveSeconds = Math.Max(0.0, activeSeconds),
                Persist = true
            };
        }

        private static void LoadCommand(string[] values)
        {
            string projectKey = Decode(values[1]);
            ProjectUsageRecord project = FindProjectUnsafe(projectKey);
            if (project == null) return;
            string command = NormalizeCommand(values[2]);
            long clicks;
            double total;
            long ticks;
            bool favorite;
            if (command.Length == 0 ||
                !long.TryParse(values[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out clicks) ||
                !double.TryParse(values[4], NumberStyles.Float, CultureInfo.InvariantCulture, out total) ||
                !long.TryParse(values[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) ||
                !bool.TryParse(values[6], out favorite)) return;
            project.Commands[command] = new CommandUsageRecord
            {
                Command = command,
                Clicks = Math.Max(0, clicks),
                TotalSeconds = Math.Max(0.0, total),
                LastUsedUtc = ticks > 0
                    ? new DateTime(ticks, DateTimeKind.Utc)
                    : DateTime.MinValue,
                IsFavorite = favorite
            };
        }

        private static void Save()
        {
            lock (SyncRoot) SaveUnsafe();
        }

        private static void SaveUnsafe()
        {
            try
            {
                string path = StoragePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = new List<string>();
                foreach (ProjectUsageRecord project in ProjectsByKey.Values
                    .Where(item => item.Persist)
                    .OrderByDescending(item => item.LastOpenedUtc))
                {
                    lines.Add(string.Join("\t", new[]
                    {
                        ProjectRow,
                        Encode(project.Key),
                        Encode(project.DisplayName),
                        Encode(project.FullName),
                        project.LastOpenedUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                        project.ActiveSeconds.ToString("R", CultureInfo.InvariantCulture)
                    }));
                    foreach (CommandUsageRecord item in project.Commands.Values
                        .OrderBy(value => value.Command))
                    {
                        lines.Add(string.Join("\t", new[]
                        {
                            CommandRow,
                            Encode(project.Key),
                            item.Command,
                            item.Clicks.ToString(CultureInfo.InvariantCulture),
                            item.TotalSeconds.ToString("R", CultureInfo.InvariantCulture),
                            item.LastUsedUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                            item.IsFavorite.ToString(),
                            "2"
                        }));
                    }
                }
                File.WriteAllLines(path, lines);
            }
            catch
            {
                // Usage analytics must never interrupt a design command.
            }
        }

        private static string StoragePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CE Tools",
                "CommandUsage.tsv");
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(
                Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(
                    Convert.FromBase64String(value ?? string.Empty));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeCommand(string command)
        {
            return (command ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static void RaiseUsageChanged()
        {
            EventHandler handler = UsageChanged;
            if (handler != null) handler(null, EventArgs.Empty);
        }
    }

    internal sealed class ProjectIdentity
    {
        public ProjectIdentity(
            string key,
            string displayName,
            string fullName,
            bool persist)
        {
            Key = key;
            DisplayName = displayName;
            FullName = fullName;
            Persist = persist;
        }

        public string Key { get; private set; }
        public string DisplayName { get; private set; }
        public string FullName { get; private set; }
        public bool Persist { get; private set; }
    }

    internal sealed class ProjectUsageRecord
    {
        public ProjectUsageRecord()
        {
            Commands = new Dictionary<string, CommandUsageRecord>(
                StringComparer.OrdinalIgnoreCase);
        }

        public string Key { get; set; }
        public string DisplayName { get; set; }
        public string FullName { get; set; }
        public DateTime LastOpenedUtc { get; set; }
        public double ActiveSeconds { get; set; }
        public bool Persist { get; set; }
        public Dictionary<string, CommandUsageRecord> Commands { get; private set; }
    }

    internal sealed class ProjectUsageSummary
    {
        public string Key { get; set; }
        public string DisplayName { get; set; }
        public string FullName { get; set; }
        public DateTime LastOpenedUtc { get; set; }
        public double ActiveSeconds { get; set; }
        public long Clicks { get; set; }
        public double CommandSeconds { get; set; }
        public long EstimatedClicksSaved { get; set; }
        public double EstimatedSecondsSaved { get; set; }

        public string SelectorText
        {
            get
            {
                string name = string.IsNullOrWhiteSpace(DisplayName)
                    ? "New Drawing"
                    : DisplayName;
                return LastOpenedUtc == DateTime.MinValue
                    ? name
                    : name + " — " + LastOpenedUtc.ToLocalTime().ToString(
                        "yyyy-MM-dd HH:mm",
                        CultureInfo.CurrentCulture);
            }
        }
    }

    internal sealed class CommandUsageRecord
    {
        public string Command { get; set; }
        public long Clicks { get; set; }
        public double TotalSeconds { get; set; }
        public DateTime LastUsedUtc { get; set; }
        public bool IsFavorite { get; set; }

        public long EstimatedClicksSaved
        {
            get { return Clicks * Math.Max(0, EstimatedNativeClicks(Command) - 1); }
        }

        public double EstimatedSecondsSaved
        {
            get { return EstimatedClicksSaved * 4.0; }
        }

        public CommandUsageRecord Clone()
        {
            return (CommandUsageRecord)MemberwiseClone();
        }

        private static int EstimatedNativeClicks(string command)
        {
            string value = command ?? string.Empty;
            if (value.Contains("SEQ")) return 18;
            if (value.Contains("PROFILE")) return 16;
            if (value.Contains("ALIGN")) return 12;
            if (value.Contains("BOQ") || value.Contains("COST")) return 20;
            if (value.Contains("REPORT") || value.Contains("SCHEDULE")) return 10;
            if (value.Contains("LABEL") || value.Contains("ANNOT")) return 8;
            if (value.Contains("REFRESH")) return 6;
            return 4;
        }
    }

    internal sealed class ActiveCommandUsage
    {
        public string Command { get; set; }
        public string ProjectKey { get; set; }
        public DateTime StartedUtc { get; set; }
    }
}
