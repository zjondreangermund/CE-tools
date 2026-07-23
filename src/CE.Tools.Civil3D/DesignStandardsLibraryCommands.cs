using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.DesignStandardsLibraryCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Extends the existing CE standards-selection workflow with a searchable,
    /// built-in Southern African civil-engineering standards catalogue.
    /// Catalogue entries are references only and never claim automatic compliance.
    /// </summary>
    public sealed class DesignStandardsLibraryCommands
    {
        private const string RootDictionaryName = "CE_TOOLS";
        private const string StandardsRecordName = "STANDARDS_SELECTION";
        private const string ProjectRecordName = "PROJECT_METADATA";
        private const string SchemaVersion = "1";
        private const int MaximumDisplayedMatches = 30;

        private static readonly string[] FieldOrder =
        {
            "Region / Framework",
            "Design Discipline",
            "Primary Standard",
            "Additional Standards",
            "Edition / Revision",
            "Approval Authority",
            "Notes",
            "Selection Date"
        };

        private static readonly IReadOnlyList<StandardEntry> Catalogue =
            new List<StandardEntry>
            {
                new StandardEntry(
                    "NAM-RA",
                    "Namibia",
                    "Roads",
                    "Roads Authority of Namibia requirements and project specifications",
                    "Roads Authority of Namibia",
                    "Verify the applicable current Roads Authority manuals, project brief and contract documents.",
                    "Roads Authority of Namibia downloads and project documentation",
                    "namibia; roads authority; road reserve; road maintenance; national roads"),
                new StandardEntry(
                    "NAM-LOCAL",
                    "Namibia",
                    "General",
                    "Namibian local-authority engineering standards and standard details",
                    "Applicable municipality, town council or regional council",
                    "Verify the authority-specific current edition and approval conditions.",
                    "Applicable Namibian local authority",
                    "namibia; municipality; local authority; town council; services; details"),
                new StandardEntry(
                    "COTO-2020",
                    "SouthAfrica",
                    "Roads",
                    "COTO Standard Specifications for Road and Bridge Works for South African Road Authorities",
                    "Committee of Transport Officials / applicable road authority",
                    "2020 Draft Standard family; verify the current adopted chapters, amendments and contract edition.",
                    "South African Department of Transport roads manuals portal",
                    "coto; colto; specifications; road works; bridge works; construction; quality assurance"),
                new StandardEntry(
                    "TRH4",
                    "SouthAfrica",
                    "Pavement",
                    "TRH4 Flexible Pavement Design",
                    "Applicable road authority",
                    "Verify the current authority-approved edition and project design method.",
                    "South African Department of Transport TRH library",
                    "trh; flexible pavement; pavement design; traffic loading"),
                new StandardEntry(
                    "TRH12",
                    "SouthAfrica",
                    "Pavement",
                    "TRH12 Flexible Pavement Rehabilitation Design",
                    "Applicable road authority",
                    "Verify the current authority-approved edition and project rehabilitation requirements.",
                    "South African Department of Transport TRH library",
                    "trh; rehabilitation; pavement; overlay; investigation"),
                new StandardEntry(
                    "TRH14",
                    "SouthAfrica",
                    "Materials",
                    "TRH14 Guidelines for Road Construction Materials",
                    "Applicable road authority",
                    "Verify the current authority-approved edition, material specifications and testing requirements.",
                    "South African Department of Transport TRH library",
                    "trh; materials; gravel; crushed stone; road construction; classification"),
                new StandardEntry(
                    "TRH15",
                    "SouthAfrica",
                    "Drainage",
                    "TRH15 Subsurface Drainage for Roads",
                    "Applicable road authority",
                    "Verify the current authority-approved edition and project drainage criteria.",
                    "South African Department of Transport TRH library",
                    "trh; drainage; subsurface; subsoil; pavement drainage"),
                new StandardEntry(
                    "TRH17",
                    "SouthAfrica",
                    "Roads",
                    "TRH17 Geometric Design of Rural Roads",
                    "Applicable road authority",
                    "Verify the current authority-approved geometric design guide and project design speed criteria.",
                    "South African Department of Transport TRH library",
                    "trh; geometric design; rural roads; alignment; sight distance"),
                new StandardEntry(
                    "TRH25",
                    "SouthAfrica",
                    "Drainage",
                    "TRH25 Hydraulic Design and Maintenance of River Crossings",
                    "Applicable road authority",
                    "Verify the current authority-approved edition, hydrology method and environmental requirements.",
                    "South African Department of Transport TRH library",
                    "trh; hydraulic; river crossing; culvert; bridge; hydrology; flood"),
                new StandardEntry(
                    "TRH29",
                    "SouthAfrica",
                    "RoadSafety",
                    "TRH29 South African road-safety audit and assessment manual family",
                    "Applicable road authority",
                    "Verify the current published title, edition and audit-stage requirements.",
                    "South African Department of Transport roads manuals portal",
                    "trh; safety audit; road safety; assessment; sarsam"),
                new StandardEntry(
                    "TMH",
                    "SouthAfrica",
                    "Materials",
                    "Technical Methods for Highways (TMH) series",
                    "Applicable road authority",
                    "Select and verify the specific current TMH method required by the project.",
                    "South African Department of Transport TMH library",
                    "tmh; testing; methods; highways; laboratory; field tests; materials"),
                new StandardEntry(
                    "SAPEM",
                    "SouthAfrica",
                    "Pavement",
                    "South African Pavement Engineering Manual",
                    "Applicable road authority",
                    "Verify the current manual version, chapter and authority adoption before use.",
                    "South African road-authority pavement-engineering resources",
                    "sapem; pavement; materials; design; construction; rehabilitation"),
                new StandardEntry(
                    "SARTSM",
                    "SouthAfrica",
                    "Traffic",
                    "South African Road Traffic Signs Manual",
                    "Applicable road and traffic authority",
                    "Verify the current volume, chapter, revision and local authority requirements.",
                    "South African Department of Transport roads portal",
                    "sartsm; signs; markings; traffic control; signals; road furniture"),
                new StandardEntry(
                    "REDBOOK",
                    "SouthAfrica",
                    "HumanSettlements",
                    "Neighbourhood Planning and Design Guide (Red Book): Creating Sustainable Human Settlements",
                    "Applicable human-settlements and local authority",
                    "Use the current guide and verify project-specific authority requirements.",
                    "CSIR Red Book portal",
                    "red book; neighbourhood; human settlements; roads; stormwater; water; sewer; solid waste"),
                new StandardEntry(
                    "SANS-CIVIL",
                    "SouthAfrica",
                    "General",
                    "Applicable SANS civil-engineering, construction, materials and services standards",
                    "Applicable authority and project professional team",
                    "Identify the exact SANS numbers and verify current editions through an authorised standards source.",
                    "South African Bureau of Standards catalogue",
                    "sans; sabs; civil; concrete; steel; materials; water; sewer; construction"),
                new StandardEntry(
                    "DWS",
                    "SouthAfrica",
                    "Water",
                    "Applicable Department of Water and Sanitation technical requirements",
                    "Department of Water and Sanitation / applicable water-services authority",
                    "Verify the current legislation, norms, guidelines, licences and authority conditions.",
                    "South African Department of Water and Sanitation",
                    "dws; water; sanitation; sewer; wastewater; water services; licence"),
                new StandardEntry(
                    "CLIENT-SPEC",
                    "Custom",
                    "General",
                    "Client, contract and project-specific design specifications",
                    "Client / approving authority",
                    "Use the signed current contract issue and record all departures and approvals.",
                    "Project contract documents",
                    "client; contract; employer requirements; scope; specifications; departures"),
                new StandardEntry(
                    "INTERNATIONAL",
                    "International",
                    "General",
                    "Project-specific international standards",
                    "Client / approving authority",
                    "Record the exact issuing body, standard number, edition and local adoption requirements.",
                    "Project contract and issuing organisation",
                    "aashto; eurocode; british standard; iso; international; project specific")
            };

        [CommandMethod("CE_TOOLS", "CE_DESIGNSTANDARDS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void DesignStandardsMenu()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var options = new PromptKeywordOptions(
                "\nCE Design Standards [Browse/Search/Apply/Current] <Browse>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Browse");
            options.Keywords.Add("Search");
            options.Keywords.Add("Apply");
            options.Keywords.Add("Current");

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string mode = result.Status == PromptStatus.None
                ? "Browse"
                : result.StringResult;

            if (string.Equals(mode, "Search", StringComparison.OrdinalIgnoreCase))
            {
                Search(document);
            }
            else if (string.Equals(mode, "Apply", StringComparison.OrdinalIgnoreCase))
            {
                Apply(document);
            }
            else if (string.Equals(mode, "Current", StringComparison.OrdinalIgnoreCase))
            {
                ReportCurrent(document);
            }
            else
            {
                Browse(document);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_STDBROWSE", CommandFlags.Modal)]
        public void BrowseCommand()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                Browse(document);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_STDSEARCH", CommandFlags.Modal)]
        public void SearchCommand()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                Search(document);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_STDAPPLY", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ApplyCommand()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                Apply(document);
            }
        }

        private static void Browse(Document document)
        {
            Editor editor = document.Editor;
            var options = new PromptKeywordOptions(
                "\nStandards category [Namibia/Roads/Pavement/Drainage/Settlements/General/All] <All>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Namibia");
            options.Keywords.Add("Roads");
            options.Keywords.Add("Pavement");
            options.Keywords.Add("Drainage");
            options.Keywords.Add("Settlements");
            options.Keywords.Add("General");
            options.Keywords.Add("All");

            PromptResult result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string category = result.Status == PromptStatus.None
                ? "All"
                : result.StringResult;
            IEnumerable<StandardEntry> matches = FilterCategory(category);
            WriteEntries(editor, matches, "CE Design Standards Library - " + category);
        }

        private static void Search(Document document)
        {
            var options = new PromptStringOptions(
                "\nSearch by code, title, discipline, authority or keyword: ")
            {
                AllowSpaces = true
            };
            PromptResult result = document.Editor.GetString(options);
            if (result.Status != PromptStatus.OK)
            {
                return;
            }

            string query = (result.StringResult ?? string.Empty).Trim();
            if (query.Length == 0)
            {
                document.Editor.WriteMessage("\nCE_STDSEARCH: enter at least one search term.");
                return;
            }

            string[] terms = query
                .Split(new[] { ' ', ',', ';', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            IEnumerable<StandardEntry> matches = Catalogue
                .Where(entry => terms.All(entry.Contains))
                .OrderBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase);
            WriteEntries(document.Editor, matches, "CE Standards search: " + query);
        }

        private static void Apply(Document document)
        {
            StandardsMetadata existing = ReadStandards(document.Database);
            Editor editor = document.Editor;
            editor.WriteMessage(
                "\nEnter a catalogue code such as NAM-RA, COTO-2020, TRH4, TRH17, REDBOOK or SANS-CIVIL.");

            var codeOptions = new PromptStringOptions("\nStandards catalogue code: ")
            {
                AllowSpaces = false
            };
            PromptResult codeResult = editor.GetString(codeOptions);
            if (codeResult.Status != PromptStatus.OK)
            {
                return;
            }

            string code = (codeResult.StringResult ?? string.Empty).Trim();
            StandardEntry entry = Catalogue.FirstOrDefault(
                item => string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                editor.WriteMessage(
                    "\nCE_STDAPPLY: code '{0}' was not found. Run CE_STDBROWSE or CE_STDSEARCH.",
                    code);
                return;
            }

            string defaultMode = existing.Exists &&
                                 !string.IsNullOrWhiteSpace(existing.Get("Primary Standard"))
                ? "Additional"
                : "Primary";
            var modeOptions = new PromptKeywordOptions(
                "\nApply as [Primary/Additional] <" + defaultMode + ">: ")
            {
                AllowNone = true
            };
            modeOptions.Keywords.Add("Primary");
            modeOptions.Keywords.Add("Additional");
            PromptResult modeResult = editor.GetKeywords(modeOptions);
            if (modeResult.Status == PromptStatus.Cancel)
            {
                return;
            }
            string mode = modeResult.Status == PromptStatus.None
                ? defaultMode
                : modeResult.StringResult;

            StandardsMetadata proposed = existing.Clone();
            proposed.Exists = true;
            if (string.IsNullOrWhiteSpace(proposed.Get("Region / Framework")) ||
                string.Equals(mode, "Primary", StringComparison.OrdinalIgnoreCase))
            {
                proposed.Set("Region / Framework", entry.Region);
            }
            if (string.IsNullOrWhiteSpace(proposed.Get("Design Discipline")) ||
                string.Equals(mode, "Primary", StringComparison.OrdinalIgnoreCase))
            {
                proposed.Set("Design Discipline", entry.Discipline);
            }

            string label = entry.Code + " - " + entry.Title;
            if (string.Equals(mode, "Primary", StringComparison.OrdinalIgnoreCase))
            {
                proposed.Set("Primary Standard", label);
                proposed.Set("Edition / Revision", entry.EditionNote);
                proposed.Set("Approval Authority", entry.Authority);
            }
            else
            {
                proposed.Set(
                    "Additional Standards",
                    AppendUnique(proposed.Get("Additional Standards"), label));
                if (string.IsNullOrWhiteSpace(proposed.Get("Approval Authority")))
                {
                    proposed.Set("Approval Authority", entry.Authority);
                }
            }

            proposed.Set(
                "Notes",
                AppendNote(
                    proposed.Get("Notes"),
                    "Catalogue source: " + entry.Source + ". " +
                    "Verify the current contract, authority adoption, amendments and edition before design."));
            proposed.Set("Selection Date", DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            editor.WriteMessage("\nCE_STDAPPLY preview");
            WriteStandards(editor, proposed);
            editor.WriteMessage(
                "\n  IMPORTANT: The library is a design-basis aid only. It does not verify licensing, edition, adoption or compliance.");

            if (!Confirm(editor, "Save this standards selection inside the DWG"))
            {
                editor.WriteMessage("\nCE_STDAPPLY cancelled. Existing standards metadata was not changed.");
                return;
            }

            try
            {
                bool projectUpdated;
                WriteStandards(document.Database, proposed, out projectUpdated);
                editor.WriteMessage(
                    "\nCE_STDAPPLY complete. '{0}' was saved as {1}.{2}",
                    entry.Code,
                    mode.ToLowerInvariant(),
                    projectUpdated
                        ? " The CE Project Standards field was synchronised."
                        : string.Empty);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_STDAPPLY cancelled. Existing metadata was retained. " +
                    exception.Message);
            }
        }

        private static void ReportCurrent(Document document)
        {
            StandardsMetadata metadata = ReadStandards(document.Database);
            if (!metadata.Exists)
            {
                document.Editor.WriteMessage(
                    "\nNo CE Tools standards selection is stored in this drawing.");
                return;
            }

            document.Editor.WriteMessage("\nCE Tools Current Project Standards");
            WriteStandards(document.Editor, metadata);
            document.Editor.WriteMessage(
                "\n  Status: recorded design basis only; compliance and current editions have not been automatically verified.");
        }

        private static IEnumerable<StandardEntry> FilterCategory(string category)
        {
            if (string.Equals(category, "All", StringComparison.OrdinalIgnoreCase))
            {
                return Catalogue.OrderBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase);
            }
            if (string.Equals(category, "Namibia", StringComparison.OrdinalIgnoreCase))
            {
                return Catalogue
                    .Where(entry => string.Equals(entry.Region, "Namibia", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase);
            }
            if (string.Equals(category, "Settlements", StringComparison.OrdinalIgnoreCase))
            {
                return Catalogue
                    .Where(entry =>
                        string.Equals(entry.Discipline, "HumanSettlements", StringComparison.OrdinalIgnoreCase) ||
                        entry.Contains("municipality") ||
                        entry.Contains("local authority"))
                    .OrderBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase);
            }
            if (string.Equals(category, "General", StringComparison.OrdinalIgnoreCase))
            {
                return Catalogue
                    .Where(entry => string.Equals(entry.Discipline, "General", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase);
            }
            return Catalogue
                .Where(entry =>
                    string.Equals(entry.Discipline, category, StringComparison.OrdinalIgnoreCase) ||
                    entry.Contains(category))
                .OrderBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase);
        }

        private static void WriteEntries(
            Editor editor,
            IEnumerable<StandardEntry> entries,
            string heading)
        {
            List<StandardEntry> matches = entries.Take(MaximumDisplayedMatches + 1).ToList();
            editor.WriteMessage("\n" + heading);
            if (matches.Count == 0)
            {
                editor.WriteMessage("\n  No catalogue entries matched.");
                return;
            }

            int displayed = Math.Min(matches.Count, MaximumDisplayedMatches);
            for (int index = 0; index < displayed; index++)
            {
                StandardEntry entry = matches[index];
                editor.WriteMessage(
                    "\n\n  {0} - {1}" +
                    "\n    Region: {2}; Discipline: {3}" +
                    "\n    Authority: {4}" +
                    "\n    Edition note: {5}" +
                    "\n    Source family: {6}",
                    entry.Code,
                    entry.Title,
                    entry.Region,
                    entry.Discipline,
                    entry.Authority,
                    entry.EditionNote,
                    entry.Source);
            }

            if (matches.Count > MaximumDisplayedMatches)
            {
                editor.WriteMessage(
                    "\n\n  More than {0} entries matched. Refine the search.",
                    MaximumDisplayedMatches);
            }
            editor.WriteMessage(
                "\n\n  Use CE_STDAPPLY and enter the catalogue code to record an item in the drawing.");
        }

        private static StandardsMetadata ReadStandards(Database database)
        {
            var metadata = new StandardsMetadata();
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary namedObjects = transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (namedObjects == null || !namedObjects.Contains(RootDictionaryName))
                    {
                        return metadata;
                    }

                    DBDictionary root = transaction.GetObject(
                        namedObjects.GetAt(RootDictionaryName),
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (root == null || !root.Contains(StandardsRecordName))
                    {
                        return metadata;
                    }

                    Xrecord record = transaction.GetObject(
                        root.GetAt(StandardsRecordName),
                        OpenMode.ForRead,
                        false) as Xrecord;
                    if (record == null || record.Data == null)
                    {
                        return metadata;
                    }

                    ReadPairs(record.Data, metadata.Set);
                    metadata.Exists = true;
                }
            }
            catch
            {
                // Malformed or inaccessible metadata is treated as absent.
            }
            return metadata;
        }

        private static void WriteStandards(
            Database database,
            StandardsMetadata metadata,
            out bool projectMetadataUpdated)
        {
            projectMetadataUpdated = false;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DBDictionary root = OpenOrCreateRootDictionary(database, transaction);
                Xrecord record = OpenOrCreateRecord(
                    root,
                    StandardsRecordName,
                    transaction);
                record.Data = BuildResultBuffer(metadata);
                projectMetadataUpdated = TryUpdateProjectStandards(
                    root,
                    BuildProjectStandardsSummary(metadata),
                    transaction);
                transaction.Commit();
            }
        }

        private static DBDictionary OpenOrCreateRootDictionary(
            Database database,
            Transaction transaction)
        {
            DBDictionary namedObjects = (DBDictionary)transaction.GetObject(
                database.NamedObjectsDictionaryId,
                OpenMode.ForWrite,
                false);
            if (namedObjects.Contains(RootDictionaryName))
            {
                return (DBDictionary)transaction.GetObject(
                    namedObjects.GetAt(RootDictionaryName),
                    OpenMode.ForWrite,
                    false);
            }

            var root = new DBDictionary();
            namedObjects.SetAt(RootDictionaryName, root);
            transaction.AddNewlyCreatedDBObject(root, true);
            return root;
        }

        private static Xrecord OpenOrCreateRecord(
            DBDictionary root,
            string recordName,
            Transaction transaction)
        {
            if (root.Contains(recordName))
            {
                return (Xrecord)transaction.GetObject(
                    root.GetAt(recordName),
                    OpenMode.ForWrite,
                    false);
            }

            var record = new Xrecord();
            root.SetAt(recordName, record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return record;
        }

        private static ResultBuffer BuildResultBuffer(StandardsMetadata metadata)
        {
            var values = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.Text, "Schema"),
                new TypedValue((int)DxfCode.Text, SchemaVersion)
            };
            foreach (string field in FieldOrder)
            {
                values.Add(new TypedValue((int)DxfCode.Text, field));
                values.Add(new TypedValue((int)DxfCode.Text, metadata.Get(field)));
            }
            return new ResultBuffer(values.ToArray());
        }

        private static bool TryUpdateProjectStandards(
            DBDictionary root,
            string standardsSummary,
            Transaction transaction)
        {
            if (!root.Contains(ProjectRecordName))
            {
                return false;
            }

            Xrecord projectRecord = transaction.GetObject(
                root.GetAt(ProjectRecordName),
                OpenMode.ForWrite,
                false) as Xrecord;
            if (projectRecord == null || projectRecord.Data == null)
            {
                return false;
            }

            var pairs = new List<KeyValuePair<string, string>>();
            ReadPairs(
                projectRecord.Data,
                delegate(string key, string value)
                {
                    pairs.Add(new KeyValuePair<string, string>(key, value));
                });

            int index = pairs.FindIndex(pair =>
                string.Equals(pair.Key, "Standards", StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                pairs[index] = new KeyValuePair<string, string>(
                    "Standards",
                    standardsSummary);
            }
            else
            {
                pairs.Add(new KeyValuePair<string, string>(
                    "Standards",
                    standardsSummary));
            }

            var values = new List<TypedValue>();
            foreach (KeyValuePair<string, string> pair in pairs)
            {
                values.Add(new TypedValue((int)DxfCode.Text, pair.Key));
                values.Add(new TypedValue((int)DxfCode.Text, pair.Value ?? string.Empty));
            }
            projectRecord.Data = new ResultBuffer(values.ToArray());
            return true;
        }

        private static void ReadPairs(
            ResultBuffer buffer,
            Action<string, string> consumer)
        {
            string pendingKey = null;
            foreach (TypedValue value in buffer)
            {
                string text = value.Value as string;
                if (text == null)
                {
                    continue;
                }
                if (pendingKey == null)
                {
                    pendingKey = text;
                }
                else
                {
                    consumer(pendingKey, text);
                    pendingKey = null;
                }
            }
        }

        private static void WriteStandards(
            Editor editor,
            StandardsMetadata metadata)
        {
            foreach (string field in FieldOrder)
            {
                string value = metadata.Get(field);
                editor.WriteMessage(
                    "\n  {0}: {1}",
                    field,
                    string.IsNullOrWhiteSpace(value) ? "<Not set>" : value);
            }
        }

        private static string BuildProjectStandardsSummary(
            StandardsMetadata metadata)
        {
            var values = new List<string>();
            AddIfPresent(values, metadata.Get("Primary Standard"));
            AddIfPresent(values, metadata.Get("Additional Standards"));
            AddIfPresent(values, metadata.Get("Edition / Revision"));
            return string.Join(" | ", values.ToArray());
        }

        private static void AddIfPresent(
            ICollection<string> values,
            string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value.Trim());
            }
        }

        private static string AppendUnique(string existing, string value)
        {
            var values = (existing ?? string.Empty)
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .ToList();
            if (!values.Any(item =>
                    string.Equals(item, value, StringComparison.OrdinalIgnoreCase)))
            {
                values.Add(value);
            }
            return string.Join("; ", values.ToArray());
        }

        private static string AppendNote(string existing, string note)
        {
            if (string.IsNullOrWhiteSpace(existing))
            {
                return note;
            }
            if (existing.IndexOf(note, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return existing;
            }
            return existing.Trim() + " | " + note;
        }

        private static bool Confirm(Editor editor, string message)
        {
            var options = new PromptKeywordOptions(
                "\n" + message + "? [Yes/No] <No>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK &&
                   string.Equals(
                       result.StringResult,
                       "Yes",
                       StringComparison.OrdinalIgnoreCase);
        }

        private sealed class StandardEntry
        {
            public StandardEntry(
                string code,
                string region,
                string discipline,
                string title,
                string authority,
                string editionNote,
                string source,
                string keywords)
            {
                Code = code;
                Region = region;
                Discipline = discipline;
                Title = title;
                Authority = authority;
                EditionNote = editionNote;
                Source = source;
                Keywords = keywords;
            }

            public string Code { get; }
            public string Region { get; }
            public string Discipline { get; }
            public string Title { get; }
            public string Authority { get; }
            public string EditionNote { get; }
            public string Source { get; }
            public string Keywords { get; }

            public bool Contains(string value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return true;
                }
                string searchable = string.Join(
                    " ",
                    new[]
                    {
                        Code,
                        Region,
                        Discipline,
                        Title,
                        Authority,
                        EditionNote,
                        Source,
                        Keywords
                    });
                return searchable.IndexOf(
                    value,
                    StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private sealed class StandardsMetadata
        {
            private readonly Dictionary<string, string> _values =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public bool Exists { get; set; }

            public string Get(string field)
            {
                string value;
                return _values.TryGetValue(field, out value)
                    ? value
                    : string.Empty;
            }

            public void Set(string field, string value)
            {
                if (!string.Equals(field, "Schema", StringComparison.OrdinalIgnoreCase))
                {
                    _values[field] = value ?? string.Empty;
                }
            }

            public StandardsMetadata Clone()
            {
                var copy = new StandardsMetadata
                {
                    Exists = Exists
                };
                foreach (KeyValuePair<string, string> pair in _values)
                {
                    copy.Set(pair.Key, pair.Value);
                }
                return copy;
            }
        }
    }
}
