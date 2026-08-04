using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Records CE command executions and elapsed time regardless of whether the
    /// command starts from the ribbon, command line or workflow centre.
    /// Statistics are user-local and remain available across drawings/sessions.
    /// </summary>
    internal static class CommandUsageTracker
    {
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, CommandUsageRecord> Records =
            new Dictionary<string, CommandUsageRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Document, ActiveCommandUsage> Active =
            new Dictionary<Document, ActiveCommandUsage>();
        private static bool _initialized;

        public static event EventHandler UsageChanged;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            Load();
            AcApplication.DocumentManager.DocumentCreated += OnDocumentCreated;
            AcApplication.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            Attach(AcApplication.DocumentManager.MdiActiveDocument);
        }

        public static void Terminate()
        {
            if (!_initialized) return;
            AcApplication.DocumentManager.DocumentCreated -= OnDocumentCreated;
            AcApplication.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            foreach (Document document in Active.Keys.ToList()) Detach(document);
            Save();
            _initialized = false;
        }

        public static IList<CommandUsageRecord> Favorites()
        {
            lock (SyncRoot)
            {
                return Records.Values
                    .Where(item => item.IsFavorite)
                    .OrderByDescending(item => item.Clicks)
                    .ThenByDescending(item => item.LastUsedUtc)
                    .Select(item => item.Clone())
                    .ToList();
            }
        }

        public static IList<CommandUsageRecord> MostUsed(int maximum)
        {
            lock (SyncRoot)
            {
                return Records.Values
                    .Where(item => item.Clicks > 0)
                    .OrderByDescending(item => item.Clicks)
                    .ThenByDescending(item => item.TotalSeconds)
                    .Take(Math.Max(1, maximum))
                    .Select(item => item.Clone())
                    .ToList();
            }
        }

        public static IList<CommandUsageRecord> Recent(int maximum)
        {
            lock (SyncRoot)
            {
                return Records.Values
                    .Where(item => item.Clicks > 0)
                    .OrderByDescending(item => item.LastUsedUtc)
                    .Take(Math.Max(1, maximum))
                    .Select(item => item.Clone())
                    .ToList();
            }
        }

        public static CommandUsageRecord Read(string command)
        {
            string key = Normalize(command);
            lock (SyncRoot)
            {
                CommandUsageRecord value;
                return Records.TryGetValue(key, out value)
                    ? value.Clone()
                    : new CommandUsageRecord { Command = key };
            }
        }

        public static void ToggleFavorite(string command)
        {
            string key = Normalize(command);
            if (key.Length == 0) return;
            lock (SyncRoot)
            {
                CommandUsageRecord value;
                if (!Records.TryGetValue(key, out value))
                {
                    value = new CommandUsageRecord { Command = key };
                    Records.Add(key, value);
                }
                value.IsFavorite = !value.IsFavorite;
                SaveUnsafe();
            }
            RaiseUsageChanged();
        }

        private static void OnDocumentCreated(
            object sender,
            DocumentCollectionEventArgs args)
        {
            Attach(args.Document);
        }

        private static void OnDocumentToBeDestroyed(
            object sender,
            DocumentCollectionEventArgs args)
        {
            Detach(args.Document);
        }

        private static void Attach(Document document)
        {
            if (document == null || Active.ContainsKey(document)) return;
            Active.Add(document, null);
            document.CommandWillStart += OnCommandWillStart;
            document.CommandEnded += OnCommandEnded;
            document.CommandCancelled += OnCommandEnded;
            document.CommandFailed += OnCommandEnded;
        }

        private static void Detach(Document document)
        {
            if (document == null || !Active.ContainsKey(document)) return;
            document.CommandWillStart -= OnCommandWillStart;
            document.CommandEnded -= OnCommandEnded;
            document.CommandCancelled -= OnCommandEnded;
            document.CommandFailed -= OnCommandEnded;
            Active.Remove(document);
        }

        private static void OnCommandWillStart(object sender, CommandEventArgs args)
        {
            Document document = sender as Document;
            string command = Normalize(args == null ? string.Empty : args.GlobalCommandName);
            if (document == null || !command.StartsWith("CE_", StringComparison.OrdinalIgnoreCase))
                return;
            Active[document] = new ActiveCommandUsage
            {
                Command = command,
                StartedUtc = DateTime.UtcNow
            };
        }

        private static void OnCommandEnded(object sender, CommandEventArgs args)
        {
            Document document = sender as Document;
            if (document == null) return;
            ActiveCommandUsage active;
            if (!Active.TryGetValue(document, out active) || active == null) return;
            Active[document] = null;

            double seconds = Math.Max(0.0, (DateTime.UtcNow - active.StartedUtc).TotalSeconds);
            lock (SyncRoot)
            {
                CommandUsageRecord value;
                if (!Records.TryGetValue(active.Command, out value))
                {
                    value = new CommandUsageRecord { Command = active.Command };
                    Records.Add(active.Command, value);
                }
                value.Clicks++;
                value.TotalSeconds += seconds;
                value.LastUsedUtc = DateTime.UtcNow;
                SaveUnsafe();
            }
            RaiseUsageChanged();
        }

        private static void Load()
        {
            lock (SyncRoot)
            {
                Records.Clear();
                string path = StoragePath();
                if (!File.Exists(path)) return;
                try
                {
                    foreach (string line in File.ReadAllLines(path))
                    {
                        string[] values = line.Split('\t');
                        if (values.Length < 5) continue;
                        long clicks;
                        double total;
                        long ticks;
                        bool favorite;
                        if (!long.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out clicks) ||
                            !double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out total) ||
                            !long.TryParse(values[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) ||
                            !bool.TryParse(values[4], out favorite)) continue;
                        string command = Normalize(values[0]);
                        if (command.Length == 0) continue;
                        Records[command] = new CommandUsageRecord
                        {
                            Command = command,
                            Clicks = Math.Max(0, clicks),
                            TotalSeconds = Math.Max(0.0, total),
                            LastUsedUtc = ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue,
                            IsFavorite = favorite
                        };
                    }
                }
                catch
                {
                    Records.Clear();
                }
            }
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
                File.WriteAllLines(
                    path,
                    Records.Values.OrderBy(item => item.Command)
                        .Select(item => string.Join("\t", new[]
                        {
                            item.Command,
                            item.Clicks.ToString(CultureInfo.InvariantCulture),
                            item.TotalSeconds.ToString("R", CultureInfo.InvariantCulture),
                            item.LastUsedUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                            item.IsFavorite.ToString()
                        })));
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

        private static string Normalize(string command)
        {
            return (command ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static void RaiseUsageChanged()
        {
            EventHandler handler = UsageChanged;
            if (handler != null) handler(null, EventArgs.Empty);
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
        public DateTime StartedUtc { get; set; }
    }
}
