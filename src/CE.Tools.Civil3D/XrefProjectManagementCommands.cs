using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.XrefProjectManagementCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Project-wide XREF discipline splitting and revision control. Splitting writes
    /// new DWGs only and refuses existing paths. Rollback is explicit, restricted to
    /// the source drawing's Revisions folder and creates a pre-restore backup first.
    /// </summary>
    public sealed class XrefProjectManagementCommands
    {
        private static readonly string[] DisciplineOrder =
        {
            "SURVEY", "ARCHITECTURE", "ROAD", "STORMWATER",
            "SEWER", "WATER", "LANDSCAPE", "OTHER"
        };

        [CommandMethod("CE_TOOLS", "CE_XREFPROJECTTOOLS", CommandFlags.Modal)]
        public void XrefProjectTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var options = new PromptKeywordOptions(
                "\nProject XREF tools [Split/Dashboard/BackupAll/Restore] <Dashboard>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Split");
            options.Keywords.Add("Dashboard");
            options.Keywords.Add("BackupAll");
            options.Keywords.Add("Restore");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string choice = result.Status == PromptStatus.OK
                ? result.StringResult
                : "Dashboard";
            string command;
            if (Equal(choice, "Split")) command = "CE_XREFDISCIPLINESPLIT ";
            else if (Equal(choice, "BackupAll")) command = "CE_XREFBACKUPALL ";
            else if (Equal(choice, "Restore")) command = "CE_XREFRESTORE ";
            else command = "CE_XREFREVISIONDASH ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_XREFDISCIPLINESPLIT",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void SplitProjectByDiscipline()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;

            var saveOptions = new PromptSaveFileOptions(
                "\nChoose a base path for discipline XREF drawings: ")
            {
                DialogCaption = "CE Tools Project XREF Discipline Split",
                Filter = "AutoCAD Drawing (*.dwg)|*.dwg",
                InitialFileName = DefaultProjectPrefix(document.Database) + "-XREF.dwg"
            };
            PromptFileNameResult fileResult = editor.GetFileNameForSave(saveOptions);
            if (fileResult.Status != PromptStatus.OK) return;
            string selectedPath = EnsureDwgExtension(fileResult.StringResult);
            string folder = Path.GetDirectoryName(selectedPath) ?? string.Empty;
            string prefix = Path.GetFileNameWithoutExtension(selectedPath);
            if (prefix.EndsWith("-XREF", StringComparison.OrdinalIgnoreCase))
                prefix = prefix.Substring(0, prefix.Length - "-XREF".Length);
            if (string.IsNullOrWhiteSpace(prefix)) prefix = "CE-PROJECT";

            var replaceOptions = new PromptKeywordOptions(
                "\nAfter successful XREF attachment [Keep/Replace] original model-space objects <Keep>: ")
            {
                AllowNone = true
            };
            replaceOptions.Keywords.Add("Keep");
            replaceOptions.Keywords.Add("Replace");
            PromptResult replaceResult = editor.GetKeywords(replaceOptions);
            if (replaceResult.Status == PromptStatus.Cancel) return;
            bool replace = replaceResult.Status == PromptStatus.OK &&
                Equal(replaceResult.StringResult, "Replace");

            DisciplineSplitPlan plan = BuildSplitPlan(
                document.Database,
                folder,
                prefix);
            if (plan.Groups.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_XREFDISCIPLINESPLIT stopped. No editable non-XREF model-space objects were available.");
                return;
            }
            if (plan.ExistingPaths.Count > 0)
            {
                editor.WriteMessage(
                    "\nCE_XREFDISCIPLINESPLIT stopped. Existing discipline files will not be overwritten:");
                foreach (string path in plan.ExistingPaths.Take(12))
                    editor.WriteMessage("\n  {0}", path);
                return;
            }

            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Output folder", folder),
                Pair("File prefix", prefix),
                Pair("Discipline drawings", plan.Groups.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Objects to export", plan.TotalObjects.ToString(CultureInfo.InvariantCulture)),
                Pair("Locked/dependent/XREF objects skipped", plan.SkippedObjects.ToString(CultureInfo.InvariantCulture)),
                Pair("Original model-space objects", replace ? "Replace only after every file and attachment succeeds" : "Keep"),
                Pair("Overwrite existing files", "Never")
            };
            foreach (DisciplineSplitGroup group in plan.Groups)
            {
                review.Add(Pair(
                    group.Discipline,
                    group.ObjectIds.Count + " objects → " + group.Path));
            }
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Project XREF Discipline Split",
                    "Objects are grouped from controlled layer-name keywords. Review every group before confirming. Existing DWGs are never overwritten.",
                    review,
                    "Create Discipline XREFs"))
            {
                editor.WriteMessage("\nCE_XREFDISCIPLINESPLIT cancelled.");
                return;
            }

            var createdFiles = new List<string>();
            try
            {
                Directory.CreateDirectory(folder);
                foreach (DisciplineSplitGroup group in plan.Groups)
                {
                    using (Database output = document.Database.Wblock(
                        new ObjectIdCollection(group.ObjectIds.ToArray()),
                        Point3d.Origin))
                    {
                        output.SaveAs(group.Path, DwgVersion.Current);
                    }
                    if (!File.Exists(group.Path))
                        throw new InvalidOperationException(
                            "AutoCAD did not create " + group.Path);
                    createdFiles.Add(group.Path);
                }

                ObjectId[] references = AttachDisciplineXrefs(
                    document.Database,
                    plan.Groups,
                    replace);
                editor.SetImpliedSelection(references);
                editor.Regen();
                editor.WriteMessage(
                    "\nCE_XREFDISCIPLINESPLIT complete. Files={0}; references={1}; originals={2}.",
                    createdFiles.Count,
                    references.Length,
                    replace ? "replaced" : "kept");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_XREFDISCIPLINESPLIT failed. Attached/source transactions were not completed where possible. {0}",
                    exception.Message);
                editor.WriteMessage(
                    "\nNew files already written are retained for inspection; no existing file was overwritten.");
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_XREFREVISIONDASH",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void RevisionDashboard()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<ProjectXrefRecord> records = ReadXrefs(document.Database);
            if (records.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_XREFREVISIONDASH: no file-based XREF definitions were found.");
                return;
            }
            List<IList<string>> rows = BuildDashboardRows(records);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - XREF Revision Dashboard",
                "Current XREF files are compared with timestamped DWG copies in each source file's Revisions folder. Hash comparison is read-only.",
                rows,
                "CE TOOLS XREF REVISION DASHBOARD");
            document.Editor.WriteMessage(
                "\nCE_XREFREVISIONDASH complete. XREFs={0}; missing sources={1}; revision folders={2}.",
                records.Count,
                records.Count(item => !item.SourceExists),
                records.Count(item => item.Revisions.Count > 0));
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_XREFBACKUPALL",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void BackupAllXrefs()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<ProjectXrefRecord> records = ReadXrefs(document.Database)
                .Where(item => item.SourceExists)
                .GroupBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (records.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_XREFBACKUPALL stopped. No resolved XREF source files were found.");
                return;
            }

            var review = new List<KeyValuePair<string, string>>
            {
                Pair("Unique source files", records.Count.ToString(CultureInfo.InvariantCulture)),
                Pair("Backup folder", "Revisions below each source drawing folder"),
                Pair("Source files modified", "No"),
                Pair("Duplicate source paths", "Backed up once")
            };
            foreach (ProjectXrefRecord record in records.Take(12))
                review.Add(Pair(record.Name, record.SourcePath));
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Backup All XREF Sources",
                    "A timestamped copy of every resolved unique XREF source will be written before coordinated revision work.",
                    review,
                    "Backup All"))
                return;

            int created = 0;
            var failures = new List<string>();
            foreach (ProjectXrefRecord record in records)
            {
                try
                {
                    CreateRevisionBackup(record.SourcePath, "");
                    created++;
                }
                catch (System.Exception exception)
                {
                    failures.Add(record.Name + ": " + exception.Message);
                }
            }
            document.Editor.WriteMessage(
                "\nCE_XREFBACKUPALL complete. Created={0}; failed={1}.",
                created,
                failures.Count);
            foreach (string failure in failures.Take(8))
                document.Editor.WriteMessage("\n  FAILED: {0}", failure);
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_XREFRESTORE",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void RestoreXrefRevision()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            Editor editor = document.Editor;
            PromptEntityResult entityResult = PromptXrefReference(editor);
            if (entityResult.Status != PromptStatus.OK) return;

            ProjectXrefRecord record = ReadXrefReference(
                document.Database,
                entityResult.ObjectId);
            if (record == null || !record.SourceExists)
            {
                editor.WriteMessage(
                    "\nCE_XREFRESTORE stopped. The selected reference does not resolve to an existing source DWG.");
                return;
            }
            string revisionsFolder = RevisionFolder(record.SourcePath);
            List<XrefRevisionFile> revisions = ReadRevisions(record.SourcePath);
            if (revisions.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_XREFRESTORE stopped. No revision DWGs were found in {0}",
                    revisionsFolder);
                return;
            }

            var openOptions = new PromptOpenFileOptions(
                "\nChoose a revision DWG to restore: ")
            {
                DialogCaption = "Select XREF Revision to Restore",
                Filter = "AutoCAD Drawing (*.dwg)|*.dwg",
                InitialDirectory = revisionsFolder
            };
            PromptFileNameResult fileResult = editor.GetFileNameForOpen(openOptions);
            if (fileResult.Status != PromptStatus.OK) return;
            string revisionPath = Path.GetFullPath(fileResult.StringResult);
            if (!IsInsideFolder(revisionPath, revisionsFolder) ||
                !File.Exists(revisionPath) ||
                !revisionPath.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            {
                editor.WriteMessage(
                    "\nCE_XREFRESTORE stopped. The selected file must be an existing DWG inside the source Revisions folder.");
                return;
            }

            FileAudit current = ReadFileAudit(record.SourcePath);
            FileAudit revision = ReadFileAudit(revisionPath);
            string preRestorePath = BuildRevisionPath(
                record.SourcePath,
                "pre-restore");
            var review = new List<KeyValuePair<string, string>>
            {
                Pair("XREF", record.Name),
                Pair("Current source", record.SourcePath),
                Pair("Current modified/hash", FormatAudit(current)),
                Pair("Selected revision", revisionPath),
                Pair("Revision modified/hash", FormatAudit(revision)),
                Pair("Files identical", Equal(current.Hash, revision.Hash) ? "Yes" : "No"),
                Pair("Automatic pre-restore backup", preRestorePath),
                Pair("Rollback action", "Overwrite current XREF source, then attempt reload")
            };
            if (!PopupTablePresenter.ShowReview(
                    "CE Tools - Restore XREF Revision",
                    "This operation changes an external DWG file. A pre-restore backup is created first. Confirm that no other user is editing the source drawing.",
                    review,
                    "Restore Revision"))
                return;

            ObjectId definitionId = record.DefinitionId;
            var ids = new ObjectIdCollection { definitionId };
            bool unloaded = false;
            try
            {
                TryInvokeXrefMethod(document.Database, "UnloadXrefs", ids);
                unloaded = true;
                Directory.CreateDirectory(revisionsFolder);
                File.Copy(record.SourcePath, preRestorePath, false);
                File.Copy(revisionPath, record.SourcePath, true);
                TryInvokeXrefMethod(document.Database, "ReloadXrefs", ids);
                editor.Regen();
                editor.WriteMessage(
                    "\nCE_XREFRESTORE complete. Restored={0}; pre-restore backup={1}",
                    revisionPath,
                    preRestorePath);
            }
            catch (System.Exception exception)
            {
                if (unloaded)
                {
                    try { TryInvokeXrefMethod(document.Database, "ReloadXrefs", ids); }
                    catch { }
                }
                editor.WriteMessage(
                    "\nCE_XREFRESTORE failed. Review the source and pre-restore backup before continuing. {0}",
                    exception.Message);
            }
        }

        private static DisciplineSplitPlan BuildSplitPlan(
            Database database,
            string folder,
            string prefix)
        {
            var plan = new DisciplineSplitPlan();
            var map = new Dictionary<string, DisciplineSplitGroup>(StringComparer.OrdinalIgnoreCase);
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable table = transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false) as BlockTable;
                if (table == null || !table.Has(BlockTableRecord.ModelSpace)) return plan;
                BlockTableRecord modelSpace = transaction.GetObject(
                    table[BlockTableRecord.ModelSpace],
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (modelSpace == null) return plan;

                foreach (ObjectId id in modelSpace)
                {
                    Entity entity;
                    try
                    {
                        entity = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Entity;
                    }
                    catch
                    {
                        plan.SkippedObjects++;
                        continue;
                    }
                    if (entity == null || entity.IsErased || entity is BlockReference && IsXrefReference(transaction, (BlockReference)entity))
                    {
                        plan.SkippedObjects++;
                        continue;
                    }
                    LayerTableRecord layer = transaction.GetObject(
                        entity.LayerId,
                        OpenMode.ForRead,
                        false) as LayerTableRecord;
                    if (layer != null && (layer.IsLocked || layer.IsDependent))
                    {
                        plan.SkippedObjects++;
                        continue;
                    }
                    string discipline = ClassifyDiscipline(entity.Layer, entity.GetType().Name);
                    DisciplineSplitGroup group;
                    if (!map.TryGetValue(discipline, out group))
                    {
                        string path = Path.Combine(
                            folder,
                            SanitizeFileName(prefix + "-" + discipline) + ".dwg");
                        group = new DisciplineSplitGroup(discipline, path);
                        map[discipline] = group;
                    }
                    group.ObjectIds.Add(id);
                    plan.TotalObjects++;
                }
            }

            foreach (string discipline in DisciplineOrder)
            {
                DisciplineSplitGroup group;
                if (!map.TryGetValue(discipline, out group) || group.ObjectIds.Count == 0)
                    continue;
                plan.Groups.Add(group);
                if (File.Exists(group.Path)) plan.ExistingPaths.Add(group.Path);
            }
            return plan;
        }

        private static ObjectId[] AttachDisciplineXrefs(
            Database database,
            IList<DisciplineSplitGroup> groups,
            bool replace)
        {
            var references = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null)
                    throw new InvalidOperationException(
                        "The current drawing space could not be opened.");

                foreach (DisciplineSplitGroup group in groups)
                {
                    string xrefName = UniqueXrefName(database, group.Discipline);
                    ObjectId definitionId = database.AttachXref(group.Path, xrefName);
                    if (definitionId.IsNull)
                        throw new InvalidOperationException(
                            "AutoCAD did not attach " + group.Path);
                    var reference = new BlockReference(Point3d.Origin, definitionId);
                    reference.SetDatabaseDefaults(database);
                    currentSpace.AppendEntity(reference);
                    transaction.AddNewlyCreatedDBObject(reference, true);
                    references.Add(reference.ObjectId);
                }

                if (replace)
                {
                    foreach (ObjectId id in groups.SelectMany(group => group.ObjectIds))
                    {
                        Entity entity = transaction.GetObject(
                            id,
                            OpenMode.ForWrite,
                            false) as Entity;
                        if (entity != null && !entity.IsErased) entity.Erase();
                    }
                }
                transaction.Commit();
            }
            return references.ToArray();
        }

        private static List<ProjectXrefRecord> ReadXrefs(Database database)
        {
            var result = new List<ProjectXrefRecord>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable table = transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false) as BlockTable;
                if (table == null) return result;
                foreach (ObjectId id in table)
                {
                    BlockTableRecord definition = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (definition == null ||
                        (!definition.IsFromExternalReference && !definition.IsFromOverlayReference))
                        continue;
                    string sourcePath = ResolveXrefPath(
                        database,
                        definition.PathName ?? string.Empty);
                    result.Add(BuildXrefRecord(
                        definition.ObjectId,
                        definition.Name,
                        sourcePath,
                        definition.XrefStatus.ToString(),
                        definition.IsUnloaded,
                        definition.IsFromOverlayReference));
                }
            }
            return result
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static ProjectXrefRecord ReadXrefReference(
            Database database,
            ObjectId referenceId)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockReference reference = transaction.GetObject(
                    referenceId,
                    OpenMode.ForRead,
                    false) as BlockReference;
                if (reference == null) return null;
                BlockTableRecord definition = transaction.GetObject(
                    reference.BlockTableRecord,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (definition == null ||
                    (!definition.IsFromExternalReference && !definition.IsFromOverlayReference))
                    return null;
                return BuildXrefRecord(
                    definition.ObjectId,
                    definition.Name,
                    ResolveXrefPath(database, definition.PathName ?? string.Empty),
                    definition.XrefStatus.ToString(),
                    definition.IsUnloaded,
                    definition.IsFromOverlayReference);
            }
        }

        private static ProjectXrefRecord BuildXrefRecord(
            ObjectId definitionId,
            string name,
            string sourcePath,
            string status,
            bool unloaded,
            bool overlay)
        {
            bool exists = File.Exists(sourcePath);
            FileAudit current = exists
                ? ReadFileAudit(sourcePath)
                : FileAudit.Missing(sourcePath);
            return new ProjectXrefRecord(
                definitionId,
                name,
                sourcePath,
                status,
                unloaded,
                overlay,
                exists,
                current,
                exists ? ReadRevisions(sourcePath) : new List<XrefRevisionFile>());
        }

        private static List<IList<string>> BuildDashboardRows(
            IList<ProjectXrefRecord> records)
        {
            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    "XREF", "STATUS", "SOURCE", "CURRENT FILE",
                    "LATEST REVISION", "COMPARE", "ACTION"
                }
            };
            foreach (ProjectXrefRecord record in records)
            {
                XrefRevisionFile latest = record.Revisions.FirstOrDefault();
                string comparison;
                if (!record.SourceExists) comparison = "Source missing";
                else if (latest == null) comparison = "No revision backup";
                else if (Equal(record.Current.Hash, latest.Audit.Hash)) comparison = "Same hash";
                else comparison = "Different hash";
                rows.Add(new List<string>
                {
                    record.Name,
                    record.Status + (record.Unloaded ? " / unloaded" : string.Empty),
                    record.SourcePath,
                    FormatAudit(record.Current),
                    latest == null ? "<None>" : latest.Path + " | " + FormatAudit(latest.Audit),
                    comparison + "; revisions=" + record.Revisions.Count,
                    !record.SourceExists
                        ? "Repair XREF path"
                        : latest == null
                            ? "Run CE_XREFBACKUP or CE_XREFBACKUPALL"
                            : "Review differences; use CE_XREFRESTORE only after coordination"
                });
            }
            return rows;
        }

        private static List<XrefRevisionFile> ReadRevisions(string sourcePath)
        {
            var result = new List<XrefRevisionFile>();
            string folder = RevisionFolder(sourcePath);
            if (!Directory.Exists(folder)) return result;
            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            foreach (string path in Directory.GetFiles(folder, "*.dwg"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (!name.StartsWith(baseName + "-", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    result.Add(new XrefRevisionFile(path, ReadFileAudit(path)));
                }
                catch
                {
                    // Unreadable revision files are omitted from controlled restore choices.
                }
            }
            return result
                .OrderByDescending(item => item.Audit.ModifiedUtc)
                .ToList();
        }

        private static string CreateRevisionBackup(
            string sourcePath,
            string tag)
        {
            string backupPath = BuildRevisionPath(sourcePath, tag);
            Directory.CreateDirectory(RevisionFolder(sourcePath));
            File.Copy(sourcePath, backupPath, false);
            return backupPath;
        }

        private static string BuildRevisionPath(
            string sourcePath,
            string tag)
        {
            string suffix = string.IsNullOrWhiteSpace(tag)
                ? string.Empty
                : "-" + SanitizeFileName(tag);
            return Path.Combine(
                RevisionFolder(sourcePath),
                Path.GetFileNameWithoutExtension(sourcePath) + suffix + "-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) +
                ".dwg");
        }

        private static string RevisionFolder(string sourcePath)
        {
            return Path.Combine(
                Path.GetDirectoryName(sourcePath) ?? string.Empty,
                "Revisions");
        }

        private static FileAudit ReadFileAudit(string path)
        {
            var info = new FileInfo(path);
            string hash;
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.Open(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite))
            {
                hash = BitConverter.ToString(sha.ComputeHash(stream))
                    .Replace("-", string.Empty);
            }
            return new FileAudit(
                path,
                info.Exists,
                info.Exists ? info.Length : 0L,
                info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
                hash);
        }

        private static string FormatAudit(FileAudit audit)
        {
            if (audit == null || !audit.Exists) return "<Missing>";
            return string.Format(
                CultureInfo.CurrentCulture,
                "{0:N0} bytes; {1}; SHA256 {2}",
                audit.SizeBytes,
                audit.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
                audit.Hash.Length > 12 ? audit.Hash.Substring(0, 12) : audit.Hash);
        }

        private static void TryInvokeXrefMethod(
            Database database,
            string methodName,
            ObjectIdCollection ids)
        {
            MethodInfo method = typeof(Database).GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(ObjectIdCollection) },
                null);
            if (method == null)
                throw new InvalidOperationException(
                    "The installed AutoCAD API does not expose Database." + methodName + ".");
            method.Invoke(database, new object[] { ids });
        }

        private static PromptEntityResult PromptXrefReference(Editor editor)
        {
            var options = new PromptEntityOptions(
                "\nSelect an attached XREF block reference: ");
            options.SetRejectMessage(
                "\nSelect a block reference that points to a file-based XREF.");
            options.AddAllowedClass(typeof(BlockReference), false);
            return editor.GetEntity(options);
        }

        private static bool IsXrefReference(
            Transaction transaction,
            BlockReference reference)
        {
            BlockTableRecord definition = transaction.GetObject(
                reference.BlockTableRecord,
                OpenMode.ForRead,
                false) as BlockTableRecord;
            return definition != null &&
                   (definition.IsFromExternalReference || definition.IsFromOverlayReference);
        }

        private static string ClassifyDiscipline(
            string layerName,
            string typeName)
        {
            string value = (layerName + " " + typeName).ToUpperInvariant();
            if (ContainsAny(value, "SURV", "TOPO", "CONTOUR", "COGO", "DTM", "BOUNDARY"))
                return "SURVEY";
            if (ContainsAny(value, "ARCH", "BUILD", "WALL", "DOOR", "WINDOW", "ROOF", "FLOOR"))
                return "ARCHITECTURE";
            if (ContainsAny(value, "STORM", "DRAIN", "CULVERT", "HEADWALL", "SW-", "SW_"))
                return "STORMWATER";
            if (ContainsAny(value, "SEWER", "SANIT", "FOUL", "MANHOLE", "SEW-", "SEW_"))
                return "SEWER";
            if (ContainsAny(value, "WATER", "VALVE", "HYDRANT", "WTR-", "WTR_", "WAT-", "WAT_"))
                return "WATER";
            if (ContainsAny(value, "ROAD", "KERB", "PAVE", "MARKING", "SIDEWALK", "RD-", "RD_"))
                return "ROAD";
            if (ContainsAny(value, "LAND", "TREE", "PLANT", "IRRIG", "GRASS"))
                return "LANDSCAPE";
            return "OTHER";
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            return terms.Any(term =>
                value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string UniqueXrefName(
            Database database,
            string baseName)
        {
            string clean = SanitizeFileName(baseName);
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable table = transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false) as BlockTable;
                if (table == null || !table.Has(clean)) return clean;
                int index = 2;
                while (table.Has(clean + "-" + index.ToString(CultureInfo.InvariantCulture)))
                    index++;
                return clean + "-" + index.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string ResolveXrefPath(
            Database hostDatabase,
            string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            string hostFolder = string.IsNullOrWhiteSpace(hostDatabase.Filename)
                ? Environment.CurrentDirectory
                : Path.GetDirectoryName(hostDatabase.Filename);
            return Path.GetFullPath(Path.Combine(hostFolder ?? string.Empty, path));
        }

        private static string DefaultProjectPrefix(Database database)
        {
            if (string.IsNullOrWhiteSpace(database.Filename)) return "CE-PROJECT";
            return SanitizeFileName(Path.GetFileNameWithoutExtension(database.Filename));
        }

        private static string EnsureDwgExtension(string path)
        {
            return path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)
                ? path
                : path + ".dwg";
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "CE-PROJECT";
            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(value
                .Select(character => invalid.Contains(character) ||
                    character == '|' || character == ':' || character == '*' ||
                    character == '?' || character == '<' || character == '>' ||
                    character == '/' || character == '\\'
                        ? '-'
                        : character)
                .ToArray())
                .Trim();
        }

        private static bool IsInsideFolder(
            string path,
            string folder)
        {
            string fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullFolder = Path.GetFullPath(folder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase);
        }

        private static bool Equal(string first, string second)
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
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

    internal sealed class DisciplineSplitPlan
    {
        public DisciplineSplitPlan()
        {
            Groups = new List<DisciplineSplitGroup>();
            ExistingPaths = new List<string>();
        }

        public IList<DisciplineSplitGroup> Groups { get; private set; }
        public IList<string> ExistingPaths { get; private set; }
        public int TotalObjects { get; set; }
        public int SkippedObjects { get; set; }
    }

    internal sealed class DisciplineSplitGroup
    {
        public DisciplineSplitGroup(string discipline, string path)
        {
            Discipline = discipline;
            Path = path;
            ObjectIds = new List<ObjectId>();
        }

        public string Discipline { get; private set; }
        public string Path { get; private set; }
        public List<ObjectId> ObjectIds { get; private set; }
    }

    internal sealed class ProjectXrefRecord
    {
        public ProjectXrefRecord(
            ObjectId definitionId,
            string name,
            string sourcePath,
            string status,
            bool unloaded,
            bool overlay,
            bool sourceExists,
            FileAudit current,
            IList<XrefRevisionFile> revisions)
        {
            DefinitionId = definitionId;
            Name = name;
            SourcePath = sourcePath;
            Status = status;
            Unloaded = unloaded;
            Overlay = overlay;
            SourceExists = sourceExists;
            Current = current;
            Revisions = revisions == null
                ? new List<XrefRevisionFile>()
                : new List<XrefRevisionFile>(revisions);
        }

        public ObjectId DefinitionId { get; private set; }
        public string Name { get; private set; }
        public string SourcePath { get; private set; }
        public string Status { get; private set; }
        public bool Unloaded { get; private set; }
        public bool Overlay { get; private set; }
        public bool SourceExists { get; private set; }
        public FileAudit Current { get; private set; }
        public IList<XrefRevisionFile> Revisions { get; private set; }
    }

    internal sealed class XrefRevisionFile
    {
        public XrefRevisionFile(string path, FileAudit audit)
        {
            Path = path;
            Audit = audit;
        }

        public string Path { get; private set; }
        public FileAudit Audit { get; private set; }
    }

    internal sealed class FileAudit
    {
        public FileAudit(
            string path,
            bool exists,
            long sizeBytes,
            DateTime modifiedUtc,
            string hash)
        {
            Path = path;
            Exists = exists;
            SizeBytes = sizeBytes;
            ModifiedUtc = modifiedUtc;
            Hash = hash ?? string.Empty;
        }

        public string Path { get; private set; }
        public bool Exists { get; private set; }
        public long SizeBytes { get; private set; }
        public DateTime ModifiedUtc { get; private set; }
        public string Hash { get; private set; }

        public static FileAudit Missing(string path)
        {
            return new FileAudit(path, false, 0L, DateTime.MinValue, string.Empty);
        }
    }
}
