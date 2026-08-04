using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
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

        [CommandMethod("CE_TOOLS", "CE_PROJECTPRESENTATIONTOOLS", CommandFlags.Modal)]
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
                if (!PromptYesNo(
                        document.Editor,
                        "Create the PowerPoint presentation after reviewing the slide plan",
                        true))
                {
                    document.Editor.WriteMessage("\nCE_PRESENTATIONCREATE cancelled. No file was created.");
                    return;
                }
                SimplePresentationPackage.Write(path, deck);
                document.Editor.WriteMessage(
                    "\nCE_PRESENTATIONCREATE complete. Slides={0}; file={1}",
                    deck.Slides.Count,
                    path);
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
                    input.Stage + " | " + input.Client,
                    new[]
                    {
                        new PresentationMetric("Purpose", input.Purpose),
                        new PresentationMetric("Drawing", snapshot.DrawingName),
                        new PresentationMetric("Prepared by", input.Author),
                        new PresentationMetric("Company", input.Company)
                    },
                    new[]
                    {
                        "Generated from the current Civil 3D drawing on " +
                            DateTime.Now.ToString("dd MMMM yyyy HH:mm", CultureInfo.CurrentCulture) + ".",
                        "This presentation is a project-review starting point and requires approved project conclusions, assumptions, exclusions, reviewers and revision history before external issue."
                    }),
                new PresentationSlide(
                    "Project and Drawing Overview",
                    "Current drawing identity, units and spatial context",
                    new[]
                    {
                        new PresentationMetric("File", snapshot.DrawingName),
                        new PresentationMetric("Drawing units", snapshot.InsertionUnits),
                        new PresentationMetric("Coordinate system", Empty(snapshot.CoordinateSystemCode, "Not assigned")),
                        new PresentationMetric("Model entities", snapshot.ModelSpaceEntityCount.ToString("N0", CultureInfo.CurrentCulture))
                    },
                    new[]
                    {
                        "Drawing path: " + snapshot.DrawingPath,
                        "Model extents: " + snapshot.ModelExtents,
                        "Database version: " + snapshot.DatabaseVersion,
                        "Verify coordinate reference system, datum, units and project origin before issue or specialist-model exchange."
                    }),
                BuildInventorySlide(snapshot),
                new PresentationSlide(
                    "Civil 3D Design Inventory",
                    "Current Civil object counts; presence does not prove design completeness",
                    snapshot.CivilMetrics,
                    new[]
                    {
                        "Review object names, styles, data shortcuts/references, rebuild state and design criteria.",
                        "Confirm alignments, profiles, profile views, surfaces, corridors and networks are coordinated with the latest project inputs.",
                        "Run the CE Tools model audit and discipline reports before drawing issue."
                    }),
                new PresentationSlide(
                    "Drawing Production",
                    "Layouts, viewports, references and sheet-production readiness",
                    new[]
                    {
                        new PresentationMetric("Layouts", snapshot.LayoutCount.ToString("N0", CultureInfo.CurrentCulture)),
                        new PresentationMetric("Viewports", snapshot.ViewportCount.ToString("N0", CultureInfo.CurrentCulture)),
                        new PresentationMetric("XREFs", snapshot.XrefCount.ToString("N0", CultureInfo.CurrentCulture)),
                        new PresentationMetric("Tables", snapshot.TableCount.ToString("N0", CultureInfo.CurrentCulture))
                    },
                    new[]
                    {
                        "Layers: " + snapshot.LayerCount.ToString("N0", CultureInfo.CurrentCulture) +
                            "; dimensions: " + snapshot.DimensionCount.ToString("N0", CultureInfo.CurrentCulture) +
                            "; text objects: " + snapshot.TextCount.ToString("N0", CultureInfo.CurrentCulture) + ".",
                        "Confirm title blocks, revision records, notes, legends, north arrows, fonts, dimensions, logos, sheet numbering, plot configuration and issue status.",
                        "Use the CE Tools client-book, drawing-book and publish workflows only after layout and plot review."
                    }),
                new PresentationSlide(
                    "Automated Model Health Review",
                    "Automated drawing/model observations; verify every finding",
                    snapshot.HealthMetrics,
                    snapshot.Findings.Take(MaximumFindingsPerSlide).ToList()),
                new PresentationSlide(
                    "Recommended Next Actions",
                    "Prioritised coordination, design and production checks",
                    new PresentationMetric[0],
                    snapshot.Actions.Take(MaximumFindingsPerSlide).ToList()),
                new PresentationSlide(
                    "Review Close-Out",
                    "Complete these controls before external distribution",
                    new[]
                    {
                        new PresentationMetric("Automated findings", snapshot.Findings.Count.ToString("N0", CultureInfo.CurrentCulture)),
                        new PresentationMetric("Civil objects", snapshot.TotalCivilObjects.ToString("N0", CultureInfo.CurrentCulture)),
                        new PresentationMetric("Layouts", snapshot.LayoutCount.ToString("N0", CultureInfo.CurrentCulture)),
                        new PresentationMetric("XREFs", snapshot.XrefCount.ToString("N0", CultureInfo.CurrentCulture))
                    },
                    new[]
                    {
                        "Add approved drawings, screenshots, visualisations, design conclusions and decision records.",
                        "Record assumptions, exclusions, design standards, review comments, responses, approvers and revision history.",
                        "Confirm model, drawing and BOQ outputs against current contracts, specifications and authority requirements.",
                        "The generated presentation does not replace drawing, design or engineering approval."
                    })
            };

            return new PresentationDeck(
                input.ProjectTitle,
                input.Author,
                input.Company,
                input.Purpose,
                slides);
        }

        private static PresentationSlide BuildInventorySlide(DrawingPresentationSnapshot snapshot)
        {
            var metrics = snapshot.TopEntityTypes
                .Take(8)
                .Select(item => new PresentationMetric(item.Key, item.Value.ToString("N0", CultureInfo.CurrentCulture)))
                .ToList();
            return new PresentationSlide(
                "AutoCAD Drawing Inventory",
                "Most common model-space object types",
                metrics,
                new[]
                {
                    "Objects outside the top " + MaximumInventoryTypes.ToString(CultureInfo.InvariantCulture) +
                        " inventory groups are summarised in the total model-space count.",
                    "Use object count, layer and standards audits to identify duplicate, proxy, unexploded, unreferenced or legacy content.",
                    "Inventory is descriptive only and does not prove drawing correctness."
                });
        }

        private static DrawingPresentationSnapshot ReadSnapshot(Database database)
        {
            var snapshot = new DrawingPresentationSnapshot
            {
                DrawingName = Path.GetFileName(database.Filename),
                DrawingPath = string.IsNullOrWhiteSpace(database.Filename) ? "Unsaved drawing" : database.Filename,
                InsertionUnits = database.Insunits.ToString(),
                DatabaseVersion = database.OriginalFileVersion.ToString(),
                ModelExtents = FormatExtents(database.Extmin, database.Extmax),
                CoordinateSystemCode = ReadCoordinateSystemCode(),
                TopEntityTypes = new List<KeyValuePair<string, int>>(),
                CivilMetrics = new List<PresentationMetric>(),
                Findings = new List<string>(),
                Actions = new List<string>()
            };

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                ReadLayers(database, transaction, snapshot);
                ReadLayouts(database, transaction, snapshot);
                ReadBlocksAndXrefs(database, transaction, snapshot);
                ReadModelSpace(database, transaction, snapshot);
            }
            ReadCivil(snapshot);
            BuildFindings(database, snapshot);
            return snapshot;
        }

        private static void ReadLayers(
            Database database,
            Transaction transaction,
            DrawingPresentationSnapshot snapshot)
        {
            LayerTable layers = transaction.GetObject(database.LayerTableId, OpenMode.ForRead) as LayerTable;
            if (layers == null) return;
            snapshot.LayerCount = layers.Cast<ObjectId>().Count();
            foreach (ObjectId layerId in layers)
            {
                LayerTableRecord layer = transaction.GetObject(layerId, OpenMode.ForRead, false) as LayerTableRecord;
                if (layer == null) continue;
                if (layer.IsOff) snapshot.OffLayerCount++;
                if (layer.IsFrozen) snapshot.FrozenLayerCount++;
                if (layer.IsLocked) snapshot.LockedLayerCount++;
            }
        }

        private static void ReadLayouts(
            Database database,
            Transaction transaction,
            DrawingPresentationSnapshot snapshot)
        {
            DBDictionary layouts = transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead, false) as DBDictionary;
            if (layouts == null) return;
            foreach (DBDictionaryEntry entry in layouts)
            {
                Layout layout = transaction.GetObject(entry.Value, OpenMode.ForRead, false) as Layout;
                if (layout == null || layout.ModelType) continue;
                snapshot.LayoutCount++;
                BlockTableRecord record = transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead, false) as BlockTableRecord;
                if (record == null) continue;
                foreach (ObjectId objectId in record)
                {
                    if (transaction.GetObject(objectId, OpenMode.ForRead, false) is Viewport)
                        snapshot.ViewportCount++;
                }
            }
        }

        private static void ReadBlocksAndXrefs(
            Database database,
            Transaction transaction,
            DrawingPresentationSnapshot snapshot)
        {
            BlockTable blocks = transaction.GetObject(database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
            if (blocks == null) return;
            snapshot.BlockDefinitionCount = blocks.Cast<ObjectId>().Count();
            foreach (ObjectId blockId in blocks)
            {
                BlockTableRecord record = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord;
                if (record != null && record.IsFromExternalReference) snapshot.XrefCount++;
            }
        }

        private static void ReadModelSpace(
            Database database,
            Transaction transaction,
            DrawingPresentationSnapshot snapshot)
        {
            BlockTable blocks = transaction.GetObject(database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
            if (blocks == null) return;
            BlockTableRecord model = transaction.GetObject(blocks[BlockTableRecord.ModelSpace], OpenMode.ForRead, false) as BlockTableRecord;
            if (model == null) return;
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId objectId in model)
            {
                DBObject value = transaction.GetObject(objectId, OpenMode.ForRead, false);
                if (value == null) continue;
                snapshot.ModelSpaceEntityCount++;
                string type = FriendlyType(value.GetType().Name);
                int count;
                counts[type] = counts.TryGetValue(type, out count) ? count + 1 : 1;
                if (value is Table) snapshot.TableCount++;
                if (value is Dimension) snapshot.DimensionCount++;
                if (value is DBText || value is MText || value is MLeader) snapshot.TextCount++;
                if (value is BlockReference) snapshot.BlockReferenceCount++;
            }
            snapshot.TopEntityTypes = counts
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaximumInventoryTypes)
                .ToList();
        }

        private static void ReadCivil(DrawingPresentationSnapshot snapshot)
        {
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (civil == null) return;
            AddCivilMetric(snapshot, "Alignments", CountCivil(civil, "GetAlignmentIds"));
            AddCivilMetric(snapshot, "Surfaces", CountCivil(civil, "GetSurfaceIds"));
            AddCivilMetric(snapshot, "Corridors", CountCivil(civil, "GetCorridorIds"));
            AddCivilMetric(snapshot, "Pipe networks", CountCivil(civil, "GetPipeNetworkIds"));
            AddCivilMetric(snapshot, "Pressure networks", CountCivil(civil, "GetPressureNetworkIds"));
            AddCivilMetric(snapshot, "Sites", CountCivil(civil, "GetSiteIds"));
            AddCivilMetric(snapshot, "Point groups", CountCivil(civil, "PointGroups"));
            snapshot.TotalCivilObjects = snapshot.CivilMetrics.Sum(metric => ParseInt(metric.Value));
        }

        private static int CountCivil(object source, string memberName)
        {
            try
            {
                object value = ReflectionValue(source, memberName);
                if (value == null) return 0;
                var collection = value as System.Collections.IEnumerable;
                return collection == null ? 0 : collection.Cast<object>().Count();
            }
            catch { return 0; }
        }

        private static void AddCivilMetric(
            DrawingPresentationSnapshot snapshot,
            string label,
            int count)
        {
            snapshot.CivilMetrics.Add(new PresentationMetric(
                label,
                count.ToString("N0", CultureInfo.CurrentCulture)));
        }

        private static object ReflectionValue(object source, string memberName)
        {
            if (source == null) return null;
            System.Reflection.MethodInfo method = source.GetType().GetMethod(
                memberName,
                Type.EmptyTypes);
            if (method != null) return method.Invoke(source, null);
            System.Reflection.PropertyInfo property = source.GetType().GetProperty(memberName);
            return property == null ? null : property.GetValue(source, null);
        }

        private static void BuildFindings(
            Database database,
            DrawingPresentationSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(snapshot.CoordinateSystemCode))
                AddFinding(snapshot, "Coordinate system is not assigned or could not be read.", "Assign and verify the project coordinate reference system and datum.");
            if (snapshot.LayoutCount == 0)
                AddFinding(snapshot, "No paper-space layouts were found.", "Create and review issue layouts, title blocks and plot configuration.");
            if (snapshot.ViewportCount == 0 && snapshot.LayoutCount > 0)
                AddFinding(snapshot, "Layouts contain no detected viewports.", "Create and lock coordinated viewports at approved scales.");
            if (snapshot.XrefCount == 0)
                AddFinding(snapshot, "No XREF definitions were found.", "Confirm whether project backgrounds/design disciplines should be separated into controlled XREFs.");
            if (snapshot.TableCount == 0)
                AddFinding(snapshot, "No model-space tables were found.", "Confirm whether BOQ, coordinates, schedules, reports or indexes are required.");
            if (snapshot.DimensionCount == 0)
                AddFinding(snapshot, "No model-space dimensions were found.", "Review dimension requirements for design and detail drawings.");
            if (snapshot.TextCount == 0)
                AddFinding(snapshot, "No model-space text/MLeader objects were found.", "Review notes, labels, callouts, legends and drawing standards.");
            if (snapshot.OffLayerCount + snapshot.FrozenLayerCount > Math.Max(10, snapshot.LayerCount / 2))
                AddFinding(snapshot, "A high proportion of layers are off or frozen.", "Review background, legacy and discipline-layer visibility before issue.");
            if (snapshot.TotalCivilObjects == 0)
                AddFinding(snapshot, "No Civil 3D objects were counted through the available API members.", "Confirm the drawing type and Civil object inventory manually.");
            if (snapshot.ModelSpaceEntityCount > 250000)
                AddFinding(snapshot, "Model space contains more than 250,000 entities.", "Review XREF separation, duplicate content, surface display and performance controls.");

            if (snapshot.Findings.Count == 0)
            {
                snapshot.Findings.Add("No high-level automated model-health warnings were generated. Complete detailed discipline, standards, geometry and engineering checks.");
                snapshot.Actions.Add("Proceed with detailed CE Tools reports, model audit, drawing review and professional design verification.");
            }

            snapshot.HealthMetrics = new List<PresentationMetric>
            {
                new PresentationMetric("Findings", snapshot.Findings.Count.ToString("N0", CultureInfo.CurrentCulture)),
                new PresentationMetric("Off/frozen layers", (snapshot.OffLayerCount + snapshot.FrozenLayerCount).ToString("N0", CultureInfo.CurrentCulture)),
                new PresentationMetric("Locked layers", snapshot.LockedLayerCount.ToString("N0", CultureInfo.CurrentCulture)),
                new PresentationMetric("Civil objects", snapshot.TotalCivilObjects.ToString("N0", CultureInfo.CurrentCulture))
            };
        }

        private static void AddFinding(
            DrawingPresentationSnapshot snapshot,
            string finding,
            string action)
        {
            snapshot.Findings.Add(finding);
            snapshot.Actions.Add(action);
        }

        private static void ShowPreview(
            Document document,
            PresentationDeck deck,
            DrawingPresentationSnapshot snapshot)
        {
            var rows = new List<IList<string>>
            {
                new List<string> { "SLIDE", "TITLE", "METRICS", "BULLETS" }
            };
            for (int index = 0; index < deck.Slides.Count; index++)
            {
                PresentationSlide slide = deck.Slides[index];
                rows.Add(new List<string>
                {
                    (index + 1).ToString(CultureInfo.InvariantCulture),
                    slide.Title,
                    slide.Metrics.Count.ToString(CultureInfo.InvariantCulture),
                    slide.Bullets.Count.ToString(CultureInfo.InvariantCulture)
                });
            }
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Project Presentation Preview",
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Slides={0}; model entities={1:N0}; Civil objects={2:N0}; automated findings={3:N0}. Add approved project visuals and conclusions before external issue.",
                    deck.Slides.Count,
                    snapshot.ModelSpaceEntityCount,
                    snapshot.TotalCivilObjects,
                    snapshot.Findings.Count),
                rows,
                "CE TOOLS PROJECT PRESENTATION PREVIEW");
        }

        private static bool PromptProjectInput(
            Editor editor,
            Database database,
            out PresentationProjectInput input)
        {
            string defaultTitle = string.IsNullOrWhiteSpace(database.Filename)
                ? "Civil Engineering Project"
                : Path.GetFileNameWithoutExtension(database.Filename);
            string title;
            string client;
            string stage;
            string purpose;
            string author;
            string company;
            if (!PromptText(editor, "Project title", defaultTitle, out title) ||
                !PromptText(editor, "Client", "Client", out client) ||
                !PromptText(editor, "Project stage", "Design Review", out stage) ||
                !PromptText(editor, "Presentation purpose", "Civil 3D project review", out purpose) ||
                !PromptText(editor, "Prepared by", Environment.UserName, out author) ||
                !PromptText(editor, "Company", "CE Tools", out company))
            {
                input = null;
                return false;
            }
            input = new PresentationProjectInput(title, client, stage, purpose, author, company);
            return true;
        }

        private static bool PromptText(
            Editor editor,
            string label,
            string defaultValue,
            out string value)
        {
            var options = new PromptStringOptions("\n" + label + " <" + defaultValue + ">: ")
            {
                AllowSpaces = true,
                UseDefaultValue = true,
                DefaultValue = defaultValue
            };
            PromptResult result = editor.GetString(options);
            value = result.Status == PromptStatus.OK ? result.StringResult : defaultValue;
            return result.Status != PromptStatus.Cancel && !string.IsNullOrWhiteSpace(value);
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
            if (result.Status == PromptStatus.Cancel) return false;
            return result.Status == PromptStatus.None
                ? defaultValue
                : string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadCoordinateSystemCode()
        {
            try
            {
                CivilDocument civil = CivilApplication.ActiveDocument;
                if (civil == null) return string.Empty;
                object settings = ReflectionValue(civil, "Settings");
                object ambient = ReflectionValue(settings, "DrawingSettings");
                object code = ReflectionValue(ambient, "CoordinateSystemCode");
                return Convert.ToString(code, CultureInfo.CurrentCulture);
            }
            catch { return string.Empty; }
        }

        private static int ParseInt(string value)
        {
            int result;
            return int.TryParse(
                (value ?? string.Empty).Replace(",", string.Empty).Replace(" ", string.Empty),
                NumberStyles.Integer,
                CultureInfo.CurrentCulture,
                out result)
                ? result
                : 0;
        }

        private static string FormatExtents(Point3d minimum, Point3d maximum)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                "X {0:N3} to {1:N3}; Y {2:N3} to {3:N3}; Z {4:N3} to {5:N3}",
                minimum.X, maximum.X, minimum.Y, maximum.Y, minimum.Z, maximum.Z);
        }

        private static string FriendlyType(string type)
        {
            return (type ?? string.Empty)
                .Replace("Polyline", "Polyline")
                .Replace("BlockReference", "Block references")
                .Replace("DBText", "Text")
                .Replace("MText", "MText")
                .Replace("MLeader", "MLeaders");
        }

        private static string SafeFileName(string value)
        {
            string result = value ?? "CE-Tools-Project";
            foreach (char invalid in Path.GetInvalidFileNameChars())
                result = result.Replace(invalid, '-');
            result = result.Trim();
            return string.IsNullOrWhiteSpace(result) ? "CE-Tools-Project" : result;
        }

        private static string Empty(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class PresentationProjectInput
    {
        public PresentationProjectInput(
            string projectTitle,
            string client,
            string stage,
            string purpose,
            string author,
            string company)
        {
            ProjectTitle = projectTitle;
            Client = client;
            Stage = stage;
            Purpose = purpose;
            Author = author;
            Company = company;
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
        public string DrawingName { get; set; }
        public string DrawingPath { get; set; }
        public string InsertionUnits { get; set; }
        public string CoordinateSystemCode { get; set; }
        public string DatabaseVersion { get; set; }
        public string ModelExtents { get; set; }
        public int ModelSpaceEntityCount { get; set; }
        public int LayerCount { get; set; }
        public int OffLayerCount { get; set; }
        public int FrozenLayerCount { get; set; }
        public int LockedLayerCount { get; set; }
        public int LayoutCount { get; set; }
        public int ViewportCount { get; set; }
        public int XrefCount { get; set; }
        public int BlockDefinitionCount { get; set; }
        public int BlockReferenceCount { get; set; }
        public int TableCount { get; set; }
        public int DimensionCount { get; set; }
        public int TextCount { get; set; }
        public int TotalCivilObjects { get; set; }
        public List<KeyValuePair<string, int>> TopEntityTypes { get; set; }
        public List<PresentationMetric> CivilMetrics { get; set; }
        public List<PresentationMetric> HealthMetrics { get; set; }
        public List<string> Findings { get; set; }
        public List<string> Actions { get; set; }
    }
}
