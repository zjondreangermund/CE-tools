using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using CETools.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ProjectPresentationCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Generates a dependency-free project review PowerPoint from the current
    /// drawing inventory and automated model-health checks. The presentation is
    /// a review aid and does not replace drawing, design or engineering approval.
    /// </summary>
    public sealed class ProjectPresentationCommands
    {
        private const int MaximumInventoryTypes = 15;
        private const int MaximumFindingsPerSlide = 9;

        [CommandMethod("CE_TOOLS", "CE_PRESENTATIONTOOLS", CommandFlags.Modal)]
        public void PresentationTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var options = new PromptKeywordOptions(
                "\nProject presentation tools [Preview/Create] <Create>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Preview");
            options.Keywords.Add("Create");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string command = result.Status == PromptStatus.OK &&
                string.Equals(result.StringResult, "Preview", StringComparison.OrdinalIgnoreCase)
                ? "CE_PRESENTATIONPREVIEW "
                : "CE_PRESENTATIONCREATE ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_PRESENTATIONPREVIEW", CommandFlags.Modal | CommandFlags.Redraw)]
        public void PreviewPresentation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PresentationProjectInput input;
            if (!PromptProjectInput(document.Editor, document.Database, out input)) return;
            try
            {
                DrawingPresentationSnapshot snapshot = ReadSnapshot(document.Database);
                PresentationDeck deck = BuildDeck(input, snapshot);
                ShowPreview(document, deck, snapshot);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_PRESENTATIONPREVIEW failed. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_PRESENTATIONCREATE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreatePresentation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PresentationProjectInput input;
            if (!PromptProjectInput(document.Editor, document.Database, out input)) return;

            var saveOptions = new PromptSaveFileOptions(
                "\nChoose the project presentation path: ")
            {
                Filter = "PowerPoint Presentation (*.pptx)|*.pptx",
                DialogCaption = "Create CE Tools Project Presentation",
                InitialFileName = SafeFileName(input.ProjectTitle) + "-Project-Review.pptx"
            };
            PromptFileNameResult saveResult = document.Editor.GetFileNameForSave(saveOptions);
            if (saveResult.Status != PromptStatus.OK) return;
            string path = saveResult.StringResult.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)
                ? saveResult.StringResult
                : saveResult.StringResult + ".pptx";
            if (File.Exists(path))
            {
                document.Editor.WriteMessage(
                    "\nCE_PRESENTATIONCREATE stopped. Existing presentation files are not overwritten.");
                return;
            }

            try
            {
                DrawingPresentationSnapshot snapshot = ReadSnapshot(document.Database);
                PresentationDeck deck = BuildDeck(input, snapshot);
                ShowPreview(document, deck, snapshot);
                if (!PromptYesNo(document.Editor, "Create this PowerPoint presentation", true))
                {
                    document.Editor.WriteMessage("\nCE_PRESENTATIONCREATE cancelled.");
                    return;
                }

                SimplePresentationPackage.Write(path, deck);
                document.Editor.WriteMessage(
                    "\nCE_PRESENTATIONCREATE complete. Slides={0}; findings={1}; path={2}.",
                    deck.Slides.Count,
                    snapshot.Findings.Count,
                    path);
                document.Editor.WriteMessage(
                    "\nThe presentation contains automated drawing/model observations. Verify every statement and add approved drawings, images and engineering conclusions before external issue.");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_PRESENTATIONCREATE failed. {0}", exception.Message);
            }
        }

        private static PresentationDeck BuildDeck(
            PresentationProjectInput input,
            DrawingPresentationSnapshot snapshot)
        {
            var slides = new List<PresentationSlide>
            {
                new PresentationSlide(
                    input.ProjectTitle,
                    input.Client + " | " + input.Stage + " | " + DateTime.Now.ToString("dd MMMM yyyy", CultureInfo.CurrentCulture),
                    new[]
                    {
                        input.Purpose,
                        "Source drawing: " + snapshot.DrawingName,
                        "Automatically generated from the current CE Tools drawing snapshot.",
                        "All content requires project-team and professional review before external issue."
                    },
                    new[]
                    {
                        Metric("Civil objects", snapshot.TotalCivilObjects),
                        Metric("Layouts", snapshot.LayoutCount),
                        Metric("XREFs", snapshot.XrefCount),
                        Metric("Findings", snapshot.Findings.Count)
                    }),
                new PresentationSlide(
                    "Project Overview",
                    "Drawing identity, units and production status",
                    new[]
                    {
                        "Client: " + input.Client,
                        "Project stage: " + input.Stage,
                        "Purpose: " + input.Purpose,
                        "Drawing: " + snapshot.DrawingName,
                        "Drawing units: " + snapshot.DrawingUnits,
                        "Coordinate system: " + ValueOr(snapshot.CoordinateSystemCode, "Not detected"),
                        "Model-space extents: " + snapshot.ExtentsSummary,
                        "Snapshot generated: " + DateTime.Now.ToString("dd MMM yyyy HH:mm", CultureInfo.CurrentCulture)
                    },
                    new[]
                    {
                        Metric("Entities", snapshot.TotalModelEntities),
                        Metric("Layers", snapshot.LayerCount),
                        Metric("Layouts", snapshot.LayoutCount),
                        Metric("Viewports", snapshot.ViewportCount)
                    }),
                new PresentationSlide(
                    "Civil 3D Design Inventory",
                    "Live object counts detected in the current drawing",
                    CivilInventoryBullets(snapshot),
                    new[]
                    {
                        Metric("Alignments", snapshot.CivilCount("Alignment")),
                        Metric("Surfaces", snapshot.CivilCount("Surface")),
                        Metric("Corridors", snapshot.CivilCount("Corridor")),
                        Metric("Networks", snapshot.CivilCount("Network"))
                    }),
                new PresentationSlide(
                    "Drawing Production",
                    "Layouts, references, annotation and delivery readiness",
                    new[]
                    {
                        "Paper-space layouts: " + snapshot.LayoutCount.ToString(CultureInfo.InvariantCulture),
                        "Layouts without active viewports: " + snapshot.LayoutsWithoutViewport.ToString(CultureInfo.InvariantCulture),
                        "Attached XREF definitions: " + snapshot.XrefCount.ToString(CultureInfo.InvariantCulture),
                        "Unresolved or unloaded XREFs: " + snapshot.UnresolvedXrefCount.ToString(CultureInfo.InvariantCulture),
                        "AutoCAD tables: " + snapshot.TableCount.ToString(CultureInfo.InvariantCulture),
                        "Dimensions: " + snapshot.DimensionCount.ToString(CultureInfo.InvariantCulture),
                        "Text and MText objects: " + snapshot.TextCount.ToString(CultureInfo.InvariantCulture),
                        "Locked/off/frozen layers: " + snapshot.RestrictedLayerCount.ToString(CultureInfo.InvariantCulture)
                    },
                    new[]
                    {
                        Metric("Layouts", snapshot.LayoutCount),
                        Metric("XREF issues", snapshot.UnresolvedXrefCount),
                        Metric("Tables", snapshot.TableCount),
                        Metric("Dimensions", snapshot.DimensionCount)
                    }),
                new PresentationSlide(
                    "Model Content by Object Type",
                    "Highest-count model-space entity types",
                    snapshot.TopEntityTypes
                        .Take(MaximumInventoryTypes)
                        .Select(item => item.Key + ": " + item.Value.ToString(CultureInfo.InvariantCulture)),
                    new[]
                    {
                        Metric("Entity types", snapshot.EntityTypeCounts.Count),
                        Metric("Model entities", snapshot.TotalModelEntities),
                        Metric("Proxy objects", snapshot.ProxyCount),
                        Metric("Empty drawing", snapshot.TotalModelEntities == 0 ? "Yes" : "No")
                    }),
                new PresentationSlide(
                    "Automated Model Health Review",
                    "Prioritised observations—not professional approval",
                    snapshot.Findings.Count == 0
                        ? new[] { "No automated review findings were raised by this drawing snapshot." }
                        : snapshot.Findings.Take(MaximumFindingsPerSlide).Select(item => item.Severity + " — " + item.Message),
                    new[]
                    {
                        Metric("Errors", snapshot.Findings.Count(item => item.Severity == "Error")),
                        Metric("Warnings", snapshot.Findings.Count(item => item.Severity == "Warning")),
                        Metric("Review", snapshot.Findings.Count(item => item.Severity == "Review")),
                        Metric("Checks", snapshot.AutomatedCheckCount)
                    }),
                new PresentationSlide(
                    "Recommended Next Actions",
                    "Resolve drawing and design review items before issue",
                    BuildNextActions(snapshot),
                    new[]
                    {
                        Metric("Open findings", snapshot.Findings.Count),
                        Metric("Unresolved XREFs", snapshot.UnresolvedXrefCount),
                        Metric("Layout gaps", snapshot.LayoutsWithoutViewport),
                        Metric("Proxy objects", snapshot.ProxyCount)
                    }),
                new PresentationSlide(
                    "Review Close-Out",
                    input.ProjectTitle + " | " + input.Stage,
                    new[]
                    {
                        "Confirm that the presentation reflects the latest approved drawing revision.",
                        "Replace automated observations with verified engineering conclusions where required.",
                        "Add approved plan, profile, section, visualisation and construction-detail images.",
                        "Record reviewers, approvals, assumptions, exclusions and revision history.",
                        "Do not issue this automatically generated deck without project-team review."
                    },
                    new[]
                    {
                        new PresentationMetric("Prepared by", input.Author),
                        new PresentationMetric("Company", input.Company),
                        new PresentationMetric("Stage", input.Stage),
                        new PresentationMetric("Slides", "8")
                    })
            };

            return new PresentationDeck(
                input.ProjectTitle,
                input.Purpose,
                input.Author,
                input.Company,
                DateTime.UtcNow,
                slides);
        }

        private static IEnumerable<string> CivilInventoryBullets(DrawingPresentationSnapshot snapshot)
        {
            string[] preferred =
            {
                "Alignment", "Profile", "ProfileView", "FeatureLine", "Surface",
                "Corridor", "Network", "Pipe", "Structure", "CogoPoint"
            };
            var bullets = new List<string>();
            foreach (string key in preferred)
                bullets.Add(Readable(key) + ": " + snapshot.CivilCount(key).ToString(CultureInfo.InvariantCulture));
            if (snapshot.TotalCivilObjects == 0)
                bullets.Add("No recognised Civil 3D design objects were detected in model space.");
            return bullets;
        }

        private static IEnumerable<string> BuildNextActions(DrawingPresentationSnapshot snapshot)
        {
            var actions = new List<string>();
            foreach (PresentationFinding finding in snapshot.Findings.Take(7))
                actions.Add(finding.Action);
            if (!actions.Any()) actions.Add("Complete discipline design checks and drawing-office review before issue.");
            actions.Add("Run the full CE Tools model audit and discipline reports on the final drawing revision.");
            actions.Add("Add approved screenshots, drawings and verified performance results to this presentation.");
            actions.Add("Record revision, reviewer, checker and approver information before external distribution.");
            return actions.Distinct(StringComparer.CurrentCultureIgnoreCase).Take(10);
        }

        private static PresentationMetric Metric(string label, int value)
        {
            return new PresentationMetric(label, value.ToString("N0", CultureInfo.CurrentCulture));
        }

        private static DrawingPresentationSnapshot ReadSnapshot(Database database)
        {
            var snapshot = new DrawingPresentationSnapshot
            {
                DrawingName = string.IsNullOrWhiteSpace(database.Filename)
                    ? "<Unsaved drawing>"
                    : Path.GetFileName(database.Filename),
                DrawingUnits = database.Insunits.ToString(),
                CoordinateSystemCode = ReadCoordinateSystemCode(),
                ExtentsSummary = ReadExtents(database)
            };

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                ReadLayers(database, transaction, snapshot);
                ReadLayouts(database, transaction, snapshot);
                ReadBlocksAndXrefs(database, transaction, snapshot);
                ReadModelSpace(database, transaction, snapshot);
            }
            BuildFindings(database, snapshot);
            return snapshot;
        }

        private static void ReadLayers(
            Database database,
            Transaction transaction,
            DrawingPresentationSnapshot snapshot)
        {
            LayerTable table = transaction.GetObject(database.LayerTableId, OpenMode.ForRead, false) as LayerTable;
            if (table == null) return;
            foreach (ObjectId id in table)
            {
                LayerTableRecord layer = transaction.GetObject(id, OpenMode.ForRead, false) as LayerTableRecord;
                if (layer == null) continue;
                snapshot.LayerCount++;
                if (layer.IsLocked || layer.IsOff || layer.IsFrozen) snapshot.RestrictedLayerCount++;
            }
        }

        private static void ReadLayouts(
            Database database,
            Transaction transaction,
            DrawingPresentationSnapshot snapshot)
        {
            DBDictionary dictionary = transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null) return;
            foreach (DBDictionaryEntry entry in dictionary)
            {
                Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead, false) as Layout;
                if (layout == null || layout.ModelType) continue;
                snapshot.LayoutCount++;
                BlockTableRecord paper = transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead, false) as BlockTableRecord;
                int viewports = 0;
                if (paper != null)
                {
                    foreach (ObjectId id in paper)
                    {
                        Viewport viewport = transaction.GetObject(id, OpenMode.ForRead, false) as Viewport;
                        if (viewport != null && viewport.Number > 1)
                        {
                            viewports++;
                            snapshot.ViewportCount++;
                        }
                    }
                }
                if (viewports == 0) snapshot.LayoutsWithoutViewport++;
            }
        }

        private static void ReadBlocksAndXrefs(
            Database database,
            Transaction transaction,
            DrawingPresentationSnapshot snapshot)
        {
            BlockTable table = transaction.GetObject(database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
            if (table == null) return;
            foreach (ObjectId id in table)
            {
                BlockTableRecord record = transaction.GetObject(id, OpenMode.ForRead, false) as BlockTableRecord;
                if (record == null) continue;
                if (!record.IsAnonymous && !record.IsLayout) snapshot.BlockDefinitionCount++;
                if (!record.IsFromExternalReference) continue;
                snapshot.XrefCount++;
                string status = record.XrefStatus.ToString();
                if (!string.Equals(status, "Resolved", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(status, "Loaded", StringComparison.OrdinalIgnoreCase))
                    snapshot.UnresolvedXrefCount++;
            }
        }

        private static void ReadModelSpace(
            Database database,
            Transaction transaction,
            DrawingPresentationSnapshot snapshot)
        {
            BlockTable table = transaction.GetObject(database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
            if (table == null) return;
            BlockTableRecord model = transaction.GetObject(table[BlockTableRecord.ModelSpace], OpenMode.ForRead, false) as BlockTableRecord;
            if (model == null) return;
            foreach (ObjectId id in model)
            {
                Entity entity;
                try { entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity; }
                catch { snapshot.UnreadableEntityCount++; continue; }
                if (entity == null || entity.IsErased) continue;
                snapshot.TotalModelEntities++;
                string type = entity.GetType().Name;
                Increment(snapshot.EntityTypeCounts, type);
                if (entity is Table) snapshot.TableCount++;
                if (entity is Dimension) snapshot.DimensionCount++;
                if (entity is DBText || entity is MText) snapshot.TextCount++;
                string dxf = string.Empty;
                try { dxf = entity.GetRXClass().DxfName ?? string.Empty; }
                catch { }
                if (type.IndexOf("Proxy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    dxf.IndexOf("PROXY", StringComparison.OrdinalIgnoreCase) >= 0)
                    snapshot.ProxyCount++;
                CountCivil(type, snapshot);
            }
            snapshot.TopEntityTypes = snapshot.EntityTypeCounts
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static void CountCivil(string type, DrawingPresentationSnapshot snapshot)
        {
            string key = null;
            if (Contains(type, "ProfileView")) key = "ProfileView";
            else if (Contains(type, "Alignment")) key = "Alignment";
            else if (Contains(type, "FeatureLine")) key = "FeatureLine";
            else if (Contains(type, "Profile")) key = "Profile";
            else if (Contains(type, "TinSurface") || Contains(type, "Surface")) key = "Surface";
            else if (Contains(type, "Corridor")) key = "Corridor";
            else if (Contains(type, "PipeNetwork") || Contains(type, "Network")) key = "Network";
            else if (Contains(type, "Structure")) key = "Structure";
            else if (Contains(type, "Pipe")) key = "Pipe";
            else if (Contains(type, "CogoPoint")) key = "CogoPoint";
            if (key == null) return;
            Increment(snapshot.CivilCounts, key);
            snapshot.TotalCivilObjects++;
        }

        private static void BuildFindings(Database database, DrawingPresentationSnapshot snapshot)
        {
            snapshot.AutomatedCheckCount = 9;
            if (string.IsNullOrWhiteSpace(database.Filename))
                snapshot.Findings.Add(Finding("Warning", "The source drawing is unsaved.", "Save the drawing to a controlled project path before issue."));
            if (string.IsNullOrWhiteSpace(snapshot.CoordinateSystemCode))
                snapshot.Findings.Add(Finding("Warning", "No Civil 3D coordinate-system code was detected.", "Assign and verify the approved project coordinate reference system."));
            if (snapshot.TotalModelEntities == 0)
                snapshot.Findings.Add(Finding("Error", "Model space contains no readable entities.", "Confirm that the correct design drawing is open and model content is loaded."));
            if (snapshot.TotalCivilObjects == 0)
                snapshot.Findings.Add(Finding("Review", "No recognised Civil 3D design objects were detected.", "Confirm whether this drawing is a background/drafting file or whether Civil objects are missing."));
            if (snapshot.LayoutCount == 0)
                snapshot.Findings.Add(Finding("Warning", "No paper-space layouts were detected.", "Create and review the required drawing layouts and title blocks."));
            if (snapshot.LayoutsWithoutViewport > 0)
                snapshot.Findings.Add(Finding("Review", snapshot.LayoutsWithoutViewport + " layout(s) have no active viewport.", "Review layout completeness and create correctly scaled viewports where required."));
            if (snapshot.UnresolvedXrefCount > 0)
                snapshot.Findings.Add(Finding("Error", snapshot.UnresolvedXrefCount + " XREF definition(s) are unresolved, unloaded or otherwise not ready.", "Resolve and reload every required external reference before issue."));
            if (snapshot.ProxyCount > 0)
                snapshot.Findings.Add(Finding("Warning", snapshot.ProxyCount + " proxy object(s) were detected.", "Open with the correct object enabler/product and verify all proxy content."));
            if (snapshot.UnreadableEntityCount > 0)
                snapshot.Findings.Add(Finding("Warning", snapshot.UnreadableEntityCount + " entity record(s) could not be read.", "Run AUDIT/RECOVER and inspect the affected drawing objects."));
        }

        private static void ShowPreview(
            Document document,
            PresentationDeck deck,
            DrawingPresentationSnapshot snapshot)
        {
            var rows = deck.Slides.Select((slide, index) => (IList<string>)new List<string>
            {
                (index + 1).ToString(CultureInfo.InvariantCulture),
                slide.Title,
                slide.Subtitle,
                slide.Metrics.Count.ToString(CultureInfo.InvariantCulture),
                slide.Bullets.Count.ToString(CultureInfo.InvariantCulture)
            }).ToList();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Automatic Project Presentation Preview",
                "Slides=" + deck.Slides.Count + "; model entities=" + snapshot.TotalModelEntities +
                    "; Civil objects=" + snapshot.TotalCivilObjects + "; findings=" + snapshot.Findings.Count +
                    ". Presentation statements require verification before issue.",
                new List<string> { "NO.", "SLIDE", "SUBTITLE", "METRICS", "BULLETS" },
                rows,
                "CE TOOLS PROJECT PRESENTATION PREVIEW");
        }

        private static bool PromptProjectInput(
            Editor editor,
            Database database,
            out PresentationProjectInput input)
        {
            input = null;
            string drawingBase = string.IsNullOrWhiteSpace(database.Filename)
                ? "Civil Engineering Project"
                : Path.GetFileNameWithoutExtension(database.Filename);
            string title, client, stage, purpose, author, company;
            if (!PromptString(editor, "Project title", drawingBase, out title) ||
                !PromptString(editor, "Client", "Client", out client) ||
                !PromptString(editor, "Project stage", "Design Review", out stage) ||
                !PromptString(editor, "Presentation purpose", "Civil engineering project and model review", out purpose) ||
                !PromptString(editor, "Prepared by", Environment.UserName, out author) ||
                !PromptString(editor, "Company", "CE Tools", out company))
                return false;
            input = new PresentationProjectInput(title, client, stage, purpose, author, company);
            return true;
        }

        private static bool PromptString(Editor editor, string label, string defaultValue, out string value)
        {
            var options = new PromptStringOptions("\n" + label + " <" + defaultValue + ">: ")
            {
                AllowSpaces = true,
                UseDefaultValue = true,
                DefaultValue = defaultValue
            };
            PromptResult result = editor.GetString(options);
            if (result.Status == PromptStatus.Cancel)
            {
                value = string.Empty;
                return false;
            }
            value = result.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(result.StringResult)
                ? result.StringResult.Trim()
                : defaultValue;
            return true;
        }

        private static bool PromptYesNo(Editor editor, string label, bool defaultValue)
        {
            var options = new PromptKeywordOptions(
                "\n" + label + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            return result.Status != PromptStatus.Cancel &&
                (result.Status == PromptStatus.None
                    ? defaultValue
                    : string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase));
        }

        private static string ReadCoordinateSystemCode()
        {
            try
            {
                object civilDocument = CivilApplication.ActiveDocument;
                object settings = GetProperty(civilDocument, "Settings");
                object drawingSettings = GetProperty(settings, "DrawingSettings");
                object unitZone = GetProperty(drawingSettings, "UnitZoneSettings");
                object value = GetProperty(unitZone, "CoordinateSystemCode");
                return value == null ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            catch { return string.Empty; }
        }

        private static object GetProperty(object source, string name)
        {
            if (source == null) return null;
            var property = source.GetType().GetProperty(name);
            return property == null ? null : property.GetValue(source, null);
        }

        private static string ReadExtents(Database database)
        {
            try
            {
                return string.Format(
                    CultureInfo.CurrentCulture,
                    "X {0:N3}–{1:N3}; Y {2:N3}–{3:N3}; Z {4:N3}–{5:N3}",
                    database.Extmin.X, database.Extmax.X,
                    database.Extmin.Y, database.Extmax.Y,
                    database.Extmin.Z, database.Extmax.Z);
            }
            catch { return "Unavailable"; }
        }

        private static void Increment(IDictionary<string, int> counts, string key)
        {
            int value;
            counts.TryGetValue(key, out value);
            counts[key] = value + 1;
        }

        private static PresentationFinding Finding(string severity, string message, string action)
        {
            return new PresentationFinding(severity, message, action);
        }

        private static bool Contains(string source, string value)
        {
            return source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Readable(string value)
        {
            return value == "ProfileView" ? "Profile views" :
                value == "FeatureLine" ? "Feature lines" :
                value == "CogoPoint" ? "COGO points" : value + "s";
        }

        private static string ValueOr(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string SafeFileName(string value)
        {
            string result = new string((value ?? "Project")
                .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character)
                .ToArray()).Trim();
            return string.IsNullOrWhiteSpace(result) ? "CE-Tools-Project" : result;
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class PresentationProjectInput
    {
        public PresentationProjectInput(string projectTitle, string client, string stage, string purpose, string author, string company)
        {
            ProjectTitle = projectTitle; Client = client; Stage = stage;
            Purpose = purpose; Author = author; Company = company;
        }
        public string ProjectTitle { get; private set; }
        public string Client { get; private set; }
        public string Stage { get; private set; }
        public string Purpose { get; private set; }
        public string Author { get; private set; }
        public string Company { get; private set; }
    }

    internal sealed class DrawingPresentationSnapshot
    {
        public DrawingPresentationSnapshot()
        {
            EntityTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            CivilCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            TopEntityTypes = new List<KeyValuePair<string, int>>();
            Findings = new List<PresentationFinding>();
        }
        public string DrawingName { get; set; }
        public string DrawingUnits { get; set; }
        public string CoordinateSystemCode { get; set; }
        public string ExtentsSummary { get; set; }
        public int TotalModelEntities { get; set; }
        public int TotalCivilObjects { get; set; }
        public int LayerCount { get; set; }
        public int RestrictedLayerCount { get; set; }
        public int LayoutCount { get; set; }
        public int LayoutsWithoutViewport { get; set; }
        public int ViewportCount { get; set; }
        public int XrefCount { get; set; }
        public int UnresolvedXrefCount { get; set; }
        public int BlockDefinitionCount { get; set; }
        public int TableCount { get; set; }
        public int DimensionCount { get; set; }
        public int TextCount { get; set; }
        public int ProxyCount { get; set; }
        public int UnreadableEntityCount { get; set; }
        public int AutomatedCheckCount { get; set; }
        public Dictionary<string, int> EntityTypeCounts { get; private set; }
        public Dictionary<string, int> CivilCounts { get; private set; }
        public List<KeyValuePair<string, int>> TopEntityTypes { get; set; }
        public List<PresentationFinding> Findings { get; private set; }
        public int CivilCount(string key)
        {
            int value; return CivilCounts.TryGetValue(key, out value) ? value : 0;
        }
    }

    internal sealed class PresentationFinding
    {
        public PresentationFinding(string severity, string message, string action)
        { Severity = severity; Message = message; Action = action; }
        public string Severity { get; private set; }
        public string Message { get; private set; }
        public string Action { get; private set; }
    }
}