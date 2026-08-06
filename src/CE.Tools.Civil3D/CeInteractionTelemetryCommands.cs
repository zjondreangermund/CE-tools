using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using WinMessage = System.Windows.Forms.Message;

[assembly: CommandClass(typeof(CETools.Civil3D.CeInteractionTelemetryCommands))]

namespace CETools.Civil3D
{
    public sealed class CeInteractionTelemetryCommands
    {
        [CommandMethod("CE_TOOLS", "CE_CLICKSTATS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ShowStats()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            IList<CeInteractionStat> stats = CeInteractionTelemetryManager.ReadStats(document);
            var rows = stats
                .OrderByDescending(item => item.TotalActions)
                .ThenBy(item => item.Command, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => (IList<string>)new List<string>
                {
                    item.Command,
                    item.Starts.ToString(CultureInfo.CurrentCulture),
                    item.LeftClicks.ToString(CultureInfo.CurrentCulture),
                    item.RightClicks.ToString(CultureInfo.CurrentCulture),
                    item.UndoRedo.ToString(CultureInfo.CurrentCulture),
                    item.Cancellations.ToString(CultureInfo.CurrentCulture),
                    item.Failures.ToString(CultureInfo.CurrentCulture),
                    item.TotalActions.ToString(CultureInfo.CurrentCulture),
                    TimeSpan.FromMilliseconds(item.ElapsedMilliseconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
                })
                .ToList();
            rows.Add(new List<string>
            {
                "TOTAL",
                stats.Sum(item => item.Starts).ToString(CultureInfo.CurrentCulture),
                stats.Sum(item => item.LeftClicks).ToString(CultureInfo.CurrentCulture),
                stats.Sum(item => item.RightClicks).ToString(CultureInfo.CurrentCulture),
                stats.Sum(item => item.UndoRedo).ToString(CultureInfo.CurrentCulture),
                stats.Sum(item => item.Cancellations).ToString(CultureInfo.CurrentCulture),
                stats.Sum(item => item.Failures).ToString(CultureInfo.CurrentCulture),
                stats.Sum(item => item.TotalActions).ToString(CultureInfo.CurrentCulture),
                TimeSpan.FromMilliseconds(stats.Sum(item => item.ElapsedMilliseconds)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            });
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Click and Time Statistics",
                "Every CE command start and every mouse/undo/right-click action occurring while a CE command is active is counted. Statistics are separated by DWG and also written to the CE Tools user profile.",
                new List<string>
                {
                    "Command", "Starts", "Left Clicks", "Right Clicks", "Undo / Redo",
                    "Cancelled", "Failed", "Total Actions", "Time"
                },
                rows,
                "CE TOOLS CLICK AND TIME STATISTICS");
        }

        [CommandMethod("CE_TOOLS", "CE_CLICKSTATSCLEAR", CommandFlags.Modal)]
        public void ClearStats()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            CeInteractionTelemetryManager.Clear(document);
            document.Editor.WriteMessage("\nCE_CLICKSTATSCLEAR complete. Click and time statistics for this DWG were cleared.");
        }
    }

    internal static class CeInteractionTelemetryManager
    {
        private const string RootName = "CE_TOOLS";
        private const string RecordName = "INTERACTION_TELEMETRY_V2";
        private static readonly Dictionary<Document, CeDocumentTelemetry> States =
            new Dictionary<Document, CeDocumentTelemetry>();
        private static readonly CeMouseMessageFilter MouseFilter = new CeMouseMessageFilter();
        private static bool _initialised;

        internal static void Initialize()
        {
            if (_initialised) return;
            _initialised = true;
            DocumentCollection documents = AcApplication.DocumentManager;
            documents.DocumentCreated += OnDocumentCreated;
            documents.DocumentActivated += OnDocumentActivated;
            documents.DocumentToBeDestroyed += OnDocumentDestroyed;
            System.Windows.Forms.Application.AddMessageFilter(MouseFilter);
            Attach(documents.MdiActiveDocument);
        }

        internal static void Terminate()
        {
            if (!_initialised) return;
            _initialised = false;
            DocumentCollection documents = AcApplication.DocumentManager;
            documents.DocumentCreated -= OnDocumentCreated;
            documents.DocumentActivated -= OnDocumentActivated;
            documents.DocumentToBeDestroyed -= OnDocumentDestroyed;
            System.Windows.Forms.Application.RemoveMessageFilter(MouseFilter);
            foreach (Document document in States.Keys.ToList()) Detach(document, true);
            States.Clear();
        }

        internal static void RecordMouse(bool right)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            CeDocumentTelemetry state;
            if (document == null || !States.TryGetValue(document, out state) || string.IsNullOrWhiteSpace(state.ActiveCeCommand)) return;
            CeInteractionStat stat = state.Get(state.ActiveCeCommand);
            if (right) stat.RightClicks++;
            else stat.LeftClicks++;
            state.Dirty = true;
        }

        internal static IList<CeInteractionStat> ReadStats(Document document)
        {
            Attach(document);
            CeDocumentTelemetry state;
            if (document == null || !States.TryGetValue(document, out state)) return new List<CeInteractionStat>();
            Save(document, state);
            return state.Stats.Values.Select(item => item.Clone()).ToList();
        }

        internal static void Clear(Document document)
        {
            Attach(document);
            CeDocumentTelemetry state;
            if (document == null || !States.TryGetValue(document, out state)) return;
            state.Stats.Clear();
            state.ActiveCeCommand = string.Empty;
            state.StartedUtc = DateTime.MinValue;
            state.Dirty = true;
            Save(document, state);
        }

        private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e) { Attach(e == null ? null : e.Document); }
        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e) { Attach(e == null ? null : e.Document); }
        private static void OnDocumentDestroyed(object sender, DocumentCollectionEventArgs e) { Detach(e == null ? null : e.Document, true); }

        private static void Attach(Document document)
        {
            if (document == null || States.ContainsKey(document)) return;
            var state = new CeDocumentTelemetry();
            Load(document, state);
            States[document] = state;
            document.CommandWillStart += OnCommandWillStart;
            document.CommandEnded += OnCommandEnded;
            document.CommandCancelled += OnCommandCancelled;
            document.CommandFailed += OnCommandFailed;
        }

        private static void Detach(Document document, bool save)
        {
            if (document == null) return;
            CeDocumentTelemetry state;
            if (!States.TryGetValue(document, out state)) return;
            if (save) Save(document, state);
            document.CommandWillStart -= OnCommandWillStart;
            document.CommandEnded -= OnCommandEnded;
            document.CommandCancelled -= OnCommandCancelled;
            document.CommandFailed -= OnCommandFailed;
            States.Remove(document);
        }

        private static void OnCommandWillStart(object sender, CommandEventArgs e)
        {
            Document document = sender as Document;
            if (document == null || e == null) return;
            Attach(document);
            CeDocumentTelemetry state = States[document];
            string command = Normalize(e.GlobalCommandName);
            bool undoRedo = IsUndoRedo(command);
            if (undoRedo)
            {
                string target = string.IsNullOrWhiteSpace(state.ActiveCeCommand) ? command : state.ActiveCeCommand;
                state.Get(target).UndoRedo++;
                state.Dirty = true;
            }
            if (!IsCeCommand(command)) return;
            FinishElapsed(state);
            state.ActiveCeCommand = command;
            state.StartedUtc = DateTime.UtcNow;
            state.Get(command).Starts++;
            state.Dirty = true;
        }

        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            Finish(sender as Document, e, 0);
        }

        private static void OnCommandCancelled(object sender, CommandEventArgs e)
        {
            Finish(sender as Document, e, 1);
        }

        private static void OnCommandFailed(object sender, CommandEventArgs e)
        {
            Finish(sender as Document, e, 2);
        }

        private static void Finish(Document document, CommandEventArgs e, int outcome)
        {
            CeDocumentTelemetry state;
            if (document == null || !States.TryGetValue(document, out state)) return;
            string command = Normalize(e == null ? string.Empty : e.GlobalCommandName);
            if (IsCeCommand(command))
            {
                CeInteractionStat stat = state.Get(command);
                if (outcome == 1) stat.Cancellations++;
                else if (outcome == 2) stat.Failures++;
            }
            if (string.Equals(command, state.ActiveCeCommand, StringComparison.OrdinalIgnoreCase))
            {
                FinishElapsed(state);
                state.ActiveCeCommand = string.Empty;
                state.StartedUtc = DateTime.MinValue;
            }
            state.Dirty = true;
            Save(document, state);
        }

        private static void FinishElapsed(CeDocumentTelemetry state)
        {
            if (state == null || string.IsNullOrWhiteSpace(state.ActiveCeCommand) || state.StartedUtc == DateTime.MinValue) return;
            double elapsed = Math.Max(0.0, (DateTime.UtcNow - state.StartedUtc).TotalMilliseconds);
            state.Get(state.ActiveCeCommand).ElapsedMilliseconds += elapsed;
        }

        private static bool IsCeCommand(string command)
        {
            return !string.IsNullOrWhiteSpace(command) &&
                (command.StartsWith("CE_", StringComparison.OrdinalIgnoreCase) ||
                 command.StartsWith("CETOOLS", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsUndoRedo(string command)
        {
            return string.Equals(command, "U", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "UNDO", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(command, "REDO", StringComparison.OrdinalIgnoreCase) ||
                command.IndexOf("UNDO", StringComparison.OrdinalIgnoreCase) >= 0 ||
                command.IndexOf("REDO", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Normalize(string value)
        {
            return (value ?? string.Empty).Trim().TrimStart('.', '_').ToUpperInvariant();
        }

        private static void Load(Document document, CeDocumentTelemetry state)
        {
            if (document == null || state == null) return;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    DBDictionary named = transaction.GetObject(document.Database.NamedObjectsDictionaryId, OpenMode.ForRead, false) as DBDictionary;
                    if (named == null || !named.Contains(RootName)) return;
                    DBDictionary root = transaction.GetObject(named.GetAt(RootName), OpenMode.ForRead, false) as DBDictionary;
                    if (root == null || !root.Contains(RecordName)) return;
                    Xrecord record = transaction.GetObject(root.GetAt(RecordName), OpenMode.ForRead, false) as Xrecord;
                    if (record == null || record.Data == null) return;
                    foreach (TypedValue typed in record.Data)
                    {
                        string line = typed.Value as string;
                        CeInteractionStat stat = CeInteractionStat.Parse(line);
                        if (stat != null) state.Stats[stat.Command] = stat;
                    }
                }
            }
            catch { }
        }

        private static void Save(Document document, CeDocumentTelemetry state)
        {
            if (document == null || state == null || !state.Dirty) return;
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    DBDictionary named = transaction.GetObject(document.Database.NamedObjectsDictionaryId, OpenMode.ForWrite, false) as DBDictionary;
                    DBDictionary root;
                    if (named.Contains(RootName)) root = transaction.GetObject(named.GetAt(RootName), OpenMode.ForWrite, false) as DBDictionary;
                    else
                    {
                        root = new DBDictionary();
                        named.SetAt(RootName, root);
                        transaction.AddNewlyCreatedDBObject(root, true);
                    }
                    Xrecord record;
                    if (root.Contains(RecordName)) record = transaction.GetObject(root.GetAt(RecordName), OpenMode.ForWrite, false) as Xrecord;
                    else
                    {
                        record = new Xrecord();
                        root.SetAt(RecordName, record);
                        transaction.AddNewlyCreatedDBObject(record, true);
                    }
                    record.Data = new ResultBuffer(state.Stats.Values.OrderBy(item => item.Command).Select(item => new TypedValue((int)DxfCode.Text, item.Serialize())).ToArray());
                    transaction.Commit();
                }
                SaveUserProfile(document, state);
                state.Dirty = false;
            }
            catch { }
        }

        private static void SaveUserProfile(Document document, CeDocumentTelemetry state)
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CE Tools");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "CE-Tools-Interaction-Telemetry.csv");
                var lines = new List<string> { "Drawing,Command,Starts,LeftClicks,RightClicks,UndoRedo,Cancelled,Failed,TotalActions,ElapsedMilliseconds" };
                string drawing = document.Database == null ? string.Empty : document.Database.Filename;
                foreach (CeInteractionStat item in state.Stats.Values.OrderBy(value => value.Command))
                {
                    lines.Add(string.Join(",", new[]
                    {
                        Csv(drawing), Csv(item.Command), item.Starts.ToString(CultureInfo.InvariantCulture),
                        item.LeftClicks.ToString(CultureInfo.InvariantCulture), item.RightClicks.ToString(CultureInfo.InvariantCulture),
                        item.UndoRedo.ToString(CultureInfo.InvariantCulture), item.Cancellations.ToString(CultureInfo.InvariantCulture),
                        item.Failures.ToString(CultureInfo.InvariantCulture), item.TotalActions.ToString(CultureInfo.InvariantCulture),
                        item.ElapsedMilliseconds.ToString("R", CultureInfo.InvariantCulture)
                    }));
                }
                File.WriteAllLines(path, lines, Encoding.UTF8);
            }
            catch { }
        }

        private static string Csv(string value) { return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\""; }
    }

    internal sealed class CeMouseMessageFilter : IMessageFilter
    {
        private const int WmLeftButtonDown = 0x0201;
        private const int WmRightButtonDown = 0x0204;
        private const int WmMiddleButtonDown = 0x0207;
        private const int WmXButtonDown = 0x020B;

        public bool PreFilterMessage(ref WinMessage m)
        {
            if (m.Msg == WmRightButtonDown) CeInteractionTelemetryManager.RecordMouse(true);
            else if (m.Msg == WmLeftButtonDown || m.Msg == WmMiddleButtonDown || m.Msg == WmXButtonDown)
                CeInteractionTelemetryManager.RecordMouse(false);
            return false;
        }
    }

    internal sealed class CeDocumentTelemetry
    {
        internal CeDocumentTelemetry()
        {
            Stats = new Dictionary<string, CeInteractionStat>(StringComparer.OrdinalIgnoreCase);
            ActiveCeCommand = string.Empty;
        }
        internal Dictionary<string, CeInteractionStat> Stats { get; private set; }
        internal string ActiveCeCommand { get; set; }
        internal DateTime StartedUtc { get; set; }
        internal bool Dirty { get; set; }
        internal CeInteractionStat Get(string command)
        {
            CeInteractionStat result;
            string key = string.IsNullOrWhiteSpace(command) ? "CE_UNKNOWN" : command;
            if (!Stats.TryGetValue(key, out result))
            {
                result = new CeInteractionStat { Command = key };
                Stats[key] = result;
            }
            return result;
        }
    }

    internal sealed class CeInteractionStat
    {
        internal string Command { get; set; }
        internal long Starts { get; set; }
        internal long LeftClicks { get; set; }
        internal long RightClicks { get; set; }
        internal long UndoRedo { get; set; }
        internal long Cancellations { get; set; }
        internal long Failures { get; set; }
        internal double ElapsedMilliseconds { get; set; }
        internal long TotalActions { get { return Starts + LeftClicks + RightClicks + UndoRedo; } }
        internal CeInteractionStat Clone()
        {
            return new CeInteractionStat
            {
                Command = Command, Starts = Starts, LeftClicks = LeftClicks, RightClicks = RightClicks,
                UndoRedo = UndoRedo, Cancellations = Cancellations, Failures = Failures,
                ElapsedMilliseconds = ElapsedMilliseconds
            };
        }
        internal string Serialize()
        {
            return string.Join("|", new[]
            {
                Encode(Command), Starts.ToString(CultureInfo.InvariantCulture), LeftClicks.ToString(CultureInfo.InvariantCulture),
                RightClicks.ToString(CultureInfo.InvariantCulture), UndoRedo.ToString(CultureInfo.InvariantCulture),
                Cancellations.ToString(CultureInfo.InvariantCulture), Failures.ToString(CultureInfo.InvariantCulture),
                ElapsedMilliseconds.ToString("R", CultureInfo.InvariantCulture)
            });
        }
        internal static CeInteractionStat Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string[] parts = value.Split('|');
            if (parts.Length != 8) return null;
            long starts, left, right, undo, cancelled, failed;
            double elapsed;
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out starts) ||
                !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out left) ||
                !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out right) ||
                !long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out undo) ||
                !long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out cancelled) ||
                !long.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out failed) ||
                !double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out elapsed)) return null;
            return new CeInteractionStat
            {
                Command = Decode(parts[0]), Starts = starts, LeftClicks = left, RightClicks = right,
                UndoRedo = undo, Cancellations = cancelled, Failures = failed, ElapsedMilliseconds = elapsed
            };
        }
        private static string Encode(string value) { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty)); }
        private static string Decode(string value)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty)); }
            catch { return string.Empty; }
        }
    }
}
