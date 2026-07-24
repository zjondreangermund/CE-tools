using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.DynamicTypicalDetailCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Creates traceable parameter-driven detail variants in the active drawing.
    /// An optional approved DWG template is referenced by path, timestamp and SHA-256
    /// but is never opened for write or modified. Generated geometry, parameter
    /// schedules and measurable BOQ rows are linked to a DBPoint anchor. Editing
    /// parameters regenerates the variant and resets its recorded review status to
    /// Draft. Recorded approval status does not verify a person's authority.
    /// </summary>
    public sealed class DynamicTypicalDetailCommands
    {
        internal const string LinkRecordName = "CE_DYNAMIC_TYPICAL_DETAIL";
        internal const string GeneratedRecordName = "CE_DYNAMIC_TYPICAL_DETAIL_GENERATED";
        private const string CeDictionaryName = "CE_TOOLS";
        private const string SettingsRecordName = "DYNAMIC_TYPICAL_DETAIL_SETTINGS";
        private const string SchemaVersion = "1";
        private const string DefaultDetailLayer = "CE-DYNAMIC-DETAIL";
        private const string DefaultBoqLayer = "CE-DYNAMIC-DETAIL-BOQ";
        private const double GeometryTolerance = 1e-9;

        [CommandMethod("CE_DETAILPARAMTOOLS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void DynamicDetailTools()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            var options = new PromptKeywordOptions(
                "\nDynamic typical-detail tools [Create/Edit/Refresh/BOQ/Export/Review/Information/Detach/Settings] <Create>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[]
            {
                "Create", "Edit", "Refresh", "BOQ", "Export",
                "Review", "Information", "Detach", "Settings"
            })
                options.Keywords.Add(keyword);
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return;

            string choice = result.Status == PromptStatus.OK
                ? result.StringResult
                : "Create";
            if (choice.Equals("Edit", StringComparison.OrdinalIgnoreCase))
                EditParameters();
            else if (choice.Equals("Refresh", StringComparison.OrdinalIgnoreCase))
                Refresh();
            else if (choice.Equals("BOQ", StringComparison.OrdinalIgnoreCase))
                RefreshBoq();
            else if (choice.Equals("Export", StringComparison.OrdinalIgnoreCase))
                ExportBoq();
            else if (choice.Equals("Review", StringComparison.OrdinalIgnoreCase))
                RecordReviewStatus();
            else if (choice.Equals("Information", StringComparison.OrdinalIgnoreCase))
                Information();
            else if (choice.Equals("Detach", StringComparison.OrdinalIgnoreCase))
                Detach();
            else if (choice.Equals("Settings", StringComparison.OrdinalIgnoreCase))
                ConfigureSettings();
            else
                Create();
        }

        [CommandMethod("CE_DETAILPARAMSETTINGS", CommandFlags.Modal)]
        public void ConfigureSettings()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            Editor editor = document.Editor;
            DynamicDetailSettings settings = DynamicDetailSettings.Read(document.Database);
            if (!PromptPositiveDouble(
                    editor,
                    "Drawing units per metre (1000 for mm drawings, 1 for metre drawings)",
                    settings.DrawingUnitsPerMetre,
                    out settings.DrawingUnitsPerMetre))
                return;
            if (!PromptPositiveDouble(editor, "Text height in drawing units", settings.TextHeight, out settings.TextHeight))
                return;
            if (!PromptPositiveDouble(editor, "Dimension offset in drawing units", settings.DimensionOffset, out settings.DimensionOffset))
                return;
            if (!PromptPositiveDouble(editor, "Schedule offset in drawing units", settings.ScheduleOffset, out settings.ScheduleOffset))
                return;
            if (!PromptText(editor, "Generated detail layer", settings.DetailLayer, out settings.DetailLayer))
                return;
            if (!PromptText(editor, "Generated BOQ layer", settings.BoqLayer, out settings.BoqLayer))
                return;

            settings.Write(document.Database);
            editor.WriteMessage(
                "\nCE_DETAILPARAMSETTINGS saved. Approved source templates remain external/read-only.");
        }

        [CommandMethod("CE_DETAILPARAMCREATE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Create()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            DetailParameters parameters;
            if (!PromptNewParameters(document.Editor, out parameters))
                return;

            string sourcePath = PromptOptionalSourceTemplate(document.Editor);
            string sourceHash = ComputeSha256(sourcePath);
            string sourceModified = ReadSourceModifiedUtc(sourcePath);

            PromptPointResult insertion = document.Editor.GetPoint(
                "\nPick the insertion point for the generated dynamic detail: ");
            if (insertion.Status != PromptStatus.OK)
                return;
            Point3d insertionPoint = insertion.Value.TransformBy(
                document.Editor.CurrentUserCoordinateSystem);

            DynamicDetailSettings settings = DynamicDetailSettings.Read(document.Database);
            WritePreview(document.Editor, parameters, sourcePath, settings);
            if (!Confirm(document.Editor, "Create this linked parameter-driven detail variant and quantity schedule"))
            {
                document.Editor.WriteMessage(
                    "\nCE_DETAILPARAMCREATE cancelled. No geometry or schedule was created.");
                return;
            }

            try
            {
                ObjectId anchorId = CreateLinkedDetail(
                    document.Database,
                    insertionPoint,
                    parameters,
                    settings,
                    sourcePath,
                    sourceHash,
                    sourceModified);
                document.Editor.WriteMessage(
                    "\nCE_DETAILPARAMCREATE complete. Anchor handle={0}; type={1}; review status=Draft. " +
                    "The approved source template was not modified.",
                    anchorId.Handle,
                    parameters.DetailType);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_DETAILPARAMCREATE failed. No linked detail transaction was committed. " +
                    exception.Message);
            }
        }

        [CommandMethod("CE_DETAILPARAMEDIT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void EditParameters()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            ObjectId anchorId;
            DynamicDetailLink link;
            if (!PromptLinkedDetail(document, out anchorId, out link))
                return;

            DetailParameters edited = link.Parameters.Clone();
            if (!PromptEditableParameters(document.Editor, edited))
                return;
            WritePreview(document.Editor, edited, link.SourcePath, link.Settings);
            document.Editor.WriteMessage(
                "\nChanging parameters invalidates the previous review-status record. The regenerated variant will return to Draft.");
            if (!Confirm(document.Editor, "Regenerate this detail with the edited parameters"))
                return;

            edited.ReviewStatus = "Draft";
            edited.Reviewer = string.Empty;
            edited.ReviewedAtUtc = string.Empty;
            Regenerate(document, anchorId, link.WithParameters(edited), true, "CE_DETAILPARAMEDIT");
        }

        [CommandMethod("CE_DETAILPARAMREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Refresh()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            ObjectId anchorId;
            DynamicDetailLink link;
            if (!PromptLinkedDetail(document, out anchorId, out link))
                return;

            string currentHash = ComputeSha256(link.SourcePath);
            if (!string.IsNullOrWhiteSpace(link.SourcePath) &&
                !string.IsNullOrWhiteSpace(link.SourceHash) &&
                !currentHash.Equals(link.SourceHash, StringComparison.OrdinalIgnoreCase))
            {
                document.Editor.WriteMessage(
                    "\nWARNING: the referenced approved source template is missing or its SHA-256 has changed. " +
                    "Refresh will retain the stored source identity and review status for traceability; verify the source manually.");
            }
            Regenerate(document, anchorId, link, true, "CE_DETAILPARAMREFRESH");
        }

        [CommandMethod("CE_DETAILPARAMBOQ", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RefreshBoq()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            ObjectId anchorId;
            DynamicDetailLink link;
            if (!PromptLinkedDetail(document, out anchorId, out link))
                return;

            Regenerate(document, anchorId, link, true, "CE_DETAILPARAMBOQ");
            document.Editor.WriteMessage(
                "\nRates entered in the previous CE detail BOQ were preserved by item key where possible; quantities and amounts were recalculated.");
        }

        [CommandMethod("CE_DETAILPARAMBOQEXPORT", CommandFlags.Modal)]
        public void ExportBoq()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            ObjectId anchorId;
            DynamicDetailLink link;
            if (!PromptLinkedDetail(document, out anchorId, out link))
                return;

            Dictionary<string, double> rates = ReadExistingRates(document.Database, link);
            List<QuantityItem> items = CalculateQuantities(link.Parameters, rates);
            var dialog = new SaveFileDialog(
                "Export dynamic detail BOQ",
                link.DetailId + "-BOQ.xlsx",
                "xlsx",
                "CE_DETAILPARAMBOQEXPORT",
                SaveFileDialog.SaveFileDialogFlags.DoNotTransferRemoteFiles);
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return;

            try
            {
                var rows = new List<IReadOnlyList<string>>
                {
                    new List<string>
                    {
                        "Item", "Detail ID", "Detail Type", "Description", "Unit",
                        "Quantity", "Rate", "Amount", "Review Status", "Source Template"
                    }
                };
                foreach (QuantityItem item in items)
                {
                    rows.Add(new List<string>
                    {
                        item.Key,
                        link.DetailId,
                        link.Parameters.DetailType,
                        item.Description,
                        item.Unit,
                        item.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
                        item.Rate.ToString("0.00", CultureInfo.InvariantCulture),
                        item.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                        link.Parameters.ReviewStatus,
                        link.SourcePath
                    });
                }
                SimpleXlsxWriter.WriteWorkbook(
                    dialog.Filename,
                    "Dynamic Detail BOQ",
                    rows);
                document.Editor.WriteMessage(
                    "\nCE_DETAILPARAMBOQEXPORT complete: " + dialog.Filename);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_DETAILPARAMBOQEXPORT failed. " + exception.Message);
            }
        }

        [CommandMethod("CE_DETAILPARAMREVIEW", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RecordReviewStatus()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            ObjectId anchorId;
            DynamicDetailLink link;
            if (!PromptLinkedDetail(document, out anchorId, out link))
                return;

            var options = new PromptKeywordOptions(
                "\nRecord detail review status [Draft/ForReview/Reviewed/ApprovedRecord] <" +
                StatusKeyword(link.Parameters.ReviewStatus) + ">: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Draft");
            options.Keywords.Add("ForReview");
            options.Keywords.Add("Reviewed");
            options.Keywords.Add("ApprovedRecord");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return;

            string keyword = result.Status == PromptStatus.OK
                ? result.StringResult
                : StatusKeyword(link.Parameters.ReviewStatus);
            string status = keyword.Equals("ApprovedRecord", StringComparison.OrdinalIgnoreCase)
                ? "Approved (recorded)"
                : keyword.Equals("ForReview", StringComparison.OrdinalIgnoreCase)
                    ? "For Review"
                    : keyword;

            string reviewer = string.Empty;
            if (!status.Equals("Draft", StringComparison.OrdinalIgnoreCase))
            {
                if (!PromptText(document.Editor, "Reviewer/approver name or reference", link.Parameters.Reviewer, out reviewer) ||
                    string.IsNullOrWhiteSpace(reviewer))
                {
                    document.Editor.WriteMessage(
                        "\nA reviewer/approver name or reference is required for non-Draft status.");
                    return;
                }
            }

            if (status.StartsWith("Approved", StringComparison.OrdinalIgnoreCase))
            {
                document.Editor.WriteMessage(
                    "\nIMPORTANT: CE Tools records the entered status only. It cannot verify professional registration, delegated authority or engineering approval.");
                if (!Confirm(document.Editor, "Record this user-supplied approval status after external authority has been verified"))
                    return;
            }

            DetailParameters parameters = link.Parameters.Clone();
            parameters.ReviewStatus = status;
            parameters.Reviewer = reviewer;
            parameters.ReviewedAtUtc = status.Equals("Draft", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            Regenerate(
                document,
                anchorId,
                link.WithParameters(parameters),
                true,
                "CE_DETAILPARAMREVIEW");
        }

        [CommandMethod("CE_DETAILPARAMINFO", CommandFlags.Modal)]
        public void Information()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            ObjectId anchorId;
            DynamicDetailLink link;
            if (!PromptLinkedDetail(document, out anchorId, out link))
                return;

            bool sourceExists = !string.IsNullOrWhiteSpace(link.SourcePath) && File.Exists(link.SourcePath);
            string currentHash = ComputeSha256(link.SourcePath);
            string sourceState = string.IsNullOrWhiteSpace(link.SourcePath)
                ? "No external source selected"
                : !sourceExists
                    ? "Missing"
                    : currentHash.Equals(link.SourceHash, StringComparison.OrdinalIgnoreCase)
                        ? "Live / hash matches"
                        : "Live / hash changed";
            int liveGenerated = link.GeneratedHandles.Count(handle =>
            {
                ObjectId id;
                return TryResolveHandle(document.Database, handle, out id);
            });

            var rows = new List<IList<string>>
            {
                Row("Detail ID", link.DetailId),
                Row("Detail type", link.Parameters.DetailType),
                Row("Width", link.Parameters.WidthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Row("Depth", link.Parameters.DepthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Row("Length", link.Parameters.LengthMetres.ToString("0.###", CultureInfo.InvariantCulture) + " m"),
                Row("Wall thickness", link.Parameters.WallThicknessMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Row("Pipe diameter", link.Parameters.PipeDiameterMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Row("Bedding depth", link.Parameters.BeddingDepthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Row("Concrete strength", link.Parameters.ConcreteStrength),
                Row("Reinforcement", link.Parameters.Reinforcement),
                Row("Grating / cover", link.Parameters.GratingType),
                Row("Review status", link.Parameters.ReviewStatus),
                Row("Reviewer / reference", link.Parameters.Reviewer),
                Row("Reviewed at UTC", link.Parameters.ReviewedAtUtc),
                Row("Source template", link.SourcePath),
                Row("Source state", sourceState),
                Row("Stored source SHA-256", link.SourceHash),
                Row("Stored source modified UTC", link.SourceModifiedUtc),
                Row("Generated handles", link.GeneratedHandles.Count.ToString(CultureInfo.InvariantCulture)),
                Row("Live generated", liveGenerated.ToString(CultureInfo.InvariantCulture)),
                Row("BOQ table handle", link.BoqTableHandle),
                Row("Drawing units per metre", link.Settings.DrawingUnitsPerMetre.ToString("0.###", CultureInfo.InvariantCulture))
            };
            string note =
                "The external source template is never modified. Parameter edits regenerate the linked variant and reset its status to Draft. " +
                "Recorded approval status is user supplied and does not verify authority.";
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Dynamic Typical Detail Information",
                note,
                new List<string> { "Property", "Value" },
                rows,
                "CE Dynamic Typical Detail - " + link.DetailId);
        }

        [CommandMethod("CE_DETAILPARAMDETACH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Detach()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            ObjectId anchorId;
            DynamicDetailLink link;
            if (!PromptLinkedDetail(document, out anchorId, out link))
                return;

            var options = new PromptKeywordOptions(
                "\nDetach generated detail [Keep/Delete] <Keep>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Keep");
            options.Keywords.Add("Delete");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return;
            bool deleteGenerated = result.Status == PromptStatus.OK &&
                result.StringResult.Equals("Delete", StringComparison.OrdinalIgnoreCase);

            if (!Confirm(document.Editor, deleteGenerated
                    ? "Delete the linked generated variant, schedules and anchor"
                    : "Detach the link and keep generated geometry/schedules as ordinary drawing objects"))
                return;

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (string handle in link.GeneratedHandles)
                    {
                        ObjectId id;
                        if (!TryResolveHandle(document.Database, handle, out id))
                            continue;
                        Entity entity = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                        if (entity == null)
                            continue;
                        if (deleteGenerated)
                            entity.Erase();
                        else
                            RemoveExtensionRecord(entity, transaction, GeneratedRecordName);
                    }
                    Entity anchor = transaction.GetObject(anchorId, OpenMode.ForWrite, false) as Entity;
                    if (anchor != null)
                        anchor.Erase();
                    transaction.Commit();
                }
                document.Editor.WriteMessage(deleteGenerated
                    ? "\nCE_DETAILPARAMDETACH complete. Generated variant, schedules and anchor were deleted. External source template was unchanged."
                    : "\nCE_DETAILPARAMDETACH complete. Link anchor was removed; generated objects were kept as ordinary drawing content.");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_DETAILPARAMDETACH failed. " + exception.Message);
            }
        }

        private static ObjectId CreateLinkedDetail(
            Database database,
            Point3d insertionPoint,
            DetailParameters parameters,
            DynamicDetailSettings settings,
            string sourcePath,
            string sourceHash,
            string sourceModifiedUtc)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                ObjectId detailLayer = GetOrCreateLayer(
                    database,
                    transaction,
                    settings.DetailLayer,
                    DefaultDetailLayer);
                ObjectId boqLayer = GetOrCreateLayer(
                    database,
                    transaction,
                    settings.BoqLayer,
                    DefaultBoqLayer);

                var anchor = new DBPoint(insertionPoint);
                anchor.SetDatabaseDefaults(database);
                anchor.LayerId = detailLayer;
                currentSpace.AppendEntity(anchor);
                transaction.AddNewlyCreatedDBObject(anchor, true);
                anchor.CreateExtensionDictionary();

                string detailId = "DD-" + anchor.Handle;
                DynamicDetailLink link = new DynamicDetailLink(
                    SchemaVersion,
                    detailId,
                    insertionPoint,
                    parameters,
                    settings,
                    sourcePath,
                    sourceHash,
                    sourceModifiedUtc,
                    new List<string>(),
                    string.Empty);
                Dictionary<string, double> rates = new Dictionary<string, double>(
                    StringComparer.OrdinalIgnoreCase);
                GeneratedSet generated = GenerateAll(
                    database,
                    currentSpace,
                    anchor,
                    link,
                    rates,
                    detailLayer,
                    boqLayer,
                    transaction);
                WriteLink(anchor, transaction, link.WithGenerated(
                    generated.Handles,
                    generated.BoqTableHandle));
                transaction.Commit();
                return anchor.ObjectId;
            }
        }

        private static void Regenerate(
            Document document,
            ObjectId anchorId,
            DynamicDetailLink newLink,
            bool report,
            string commandName)
        {
            try
            {
                Dictionary<string, double> rates = ReadExistingRates(
                    document.Database,
                    newLink);
                int oldCount = newLink.GeneratedHandles.Count;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Entity anchor = transaction.GetObject(
                        anchorId,
                        OpenMode.ForWrite,
                        false) as Entity;
                    if (anchor == null || !HasExtensionRecord(anchor, transaction, LinkRecordName))
                        throw new InvalidOperationException("The selected dynamic-detail anchor is missing or detached.");

                    foreach (string handle in newLink.GeneratedHandles)
                    {
                        ObjectId id;
                        if (!TryResolveHandle(document.Database, handle, out id))
                            continue;
                        Entity generated = transaction.GetObject(
                            id,
                            OpenMode.ForWrite,
                            false) as Entity;
                        if (generated != null && HasExtensionRecord(
                                generated,
                                transaction,
                                GeneratedRecordName))
                            generated.Erase();
                    }

                    BlockTableRecord currentSpace = transaction.GetObject(
                        document.Database.CurrentSpaceId,
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                    ObjectId detailLayer = GetOrCreateLayer(
                        document.Database,
                        transaction,
                        newLink.Settings.DetailLayer,
                        DefaultDetailLayer);
                    ObjectId boqLayer = GetOrCreateLayer(
                        document.Database,
                        transaction,
                        newLink.Settings.BoqLayer,
                        DefaultBoqLayer);
                    GeneratedSet generatedSet = GenerateAll(
                        document.Database,
                        currentSpace,
                        anchor,
                        newLink,
                        rates,
                        detailLayer,
                        boqLayer,
                        transaction);
                    WriteLink(anchor, transaction, newLink.WithGenerated(
                        generatedSet.Handles,
                        generatedSet.BoqTableHandle));
                    transaction.Commit();

                    if (report)
                    {
                        document.Editor.WriteMessage(
                            "\n{0} complete. Detail={1}; old generated={2}; new generated={3}; review status={4}; source template changed=0.",
                            commandName,
                            newLink.DetailId,
                            oldCount,
                            generatedSet.Handles.Count,
                            newLink.Parameters.ReviewStatus);
                    }
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\n" + commandName + " failed. Existing linked output may require review. " +
                    exception.Message);
            }
        }

        private static GeneratedSet GenerateAll(
            Database database,
            BlockTableRecord currentSpace,
            Entity anchor,
            DynamicDetailLink link,
            IDictionary<string, double> rates,
            ObjectId detailLayer,
            ObjectId boqLayer,
            Transaction transaction)
        {
            var handles = new List<string>();
            string ownerHandle = anchor.Handle.ToString();
            AddTitleAndStatus(
                database,
                currentSpace,
                link,
                detailLayer,
                transaction,
                ownerHandle,
                handles);

            if (link.Parameters.DetailType.Equals("TrenchDrain", StringComparison.OrdinalIgnoreCase))
                GenerateTrenchDrain(database, currentSpace, link, detailLayer, transaction, ownerHandle, handles);
            else if (link.Parameters.DetailType.Equals("PipeTrench", StringComparison.OrdinalIgnoreCase))
                GeneratePipeTrench(database, currentSpace, link, detailLayer, transaction, ownerHandle, handles);
            else
                GenerateValveChamber(database, currentSpace, link, detailLayer, transaction, ownerHandle, handles);

            Table parameterTable = BuildParameterTable(database, link);
            parameterTable.LayerId = boqLayer;
            AppendGenerated(currentSpace, parameterTable, transaction, ownerHandle, handles);

            List<QuantityItem> quantities = CalculateQuantities(link.Parameters, rates);
            Table boqTable = BuildBoqTable(database, link, quantities);
            boqTable.LayerId = boqLayer;
            AppendGenerated(currentSpace, boqTable, transaction, ownerHandle, handles);
            return new GeneratedSet(handles, boqTable.Handle.ToString());
        }

        private static void AddTitleAndStatus(
            Database database,
            BlockTableRecord space,
            DynamicDetailLink link,
            ObjectId layerId,
            Transaction transaction,
            string ownerHandle,
            ICollection<string> handles)
        {
            DynamicDetailSettings settings = link.Settings;
            Point3d origin = link.InsertionPoint;
            var title = new MText();
            title.SetDatabaseDefaults(database);
            title.LayerId = layerId;
            title.Location = origin + new Vector3d(0.0, settings.TextHeight * 2.5, 0.0);
            title.Attachment = AttachmentPoint.BottomLeft;
            title.TextHeight = settings.TextHeight * 1.25;
            title.Contents =
                link.DetailId + " - " + DisplayType(link.Parameters.DetailType) +
                "\nPARAMETER-DRIVEN GENERATED VARIANT" +
                "\nRecorded status: " + link.Parameters.ReviewStatus +
                (string.IsNullOrWhiteSpace(link.Parameters.Reviewer)
                    ? string.Empty
                    : " | " + link.Parameters.Reviewer) +
                "\nNot an approved library standard unless external authority is verified.";
            title.BackgroundFill = true;
            title.UseBackgroundColor = true;
            AppendGenerated(space, title, transaction, ownerHandle, handles);

            double marker = settings.TextHeight * 0.45;
            var anchorMarker = new Circle(origin, Vector3d.ZAxis, marker);
            anchorMarker.SetDatabaseDefaults(database);
            anchorMarker.LayerId = layerId;
            AppendGenerated(space, anchorMarker, transaction, ownerHandle, handles);
        }

        private static void GenerateTrenchDrain(
            Database database,
            BlockTableRecord space,
            DynamicDetailLink link,
            ObjectId layerId,
            Transaction transaction,
            string ownerHandle,
            ICollection<string> handles)
        {
            DetailParameters p = link.Parameters;
            DynamicDetailSettings s = link.Settings;
            double width = ToDrawingUnits(p.WidthMillimetres, s);
            double depth = ToDrawingUnits(p.DepthMillimetres, s);
            double wall = ToDrawingUnits(p.WallThicknessMillimetres, s);
            ValidateSection(width, depth, wall);

            Point3d o = link.InsertionPoint;
            AddRectangle(database, space, o, width, depth, layerId, transaction, ownerHandle, handles);
            AddOpenInnerChannel(
                database,
                space,
                o + new Vector3d(wall, wall, 0.0),
                width - 2.0 * wall,
                depth - wall,
                layerId,
                transaction,
                ownerHandle,
                handles);
            AddGrating(
                database,
                space,
                o + new Vector3d(0.0, depth, 0.0),
                width,
                p.GratingType,
                s,
                layerId,
                transaction,
                ownerHandle,
                handles);
            AddReinforcementMarkers(
                database,
                space,
                o,
                width,
                depth,
                wall,
                layerId,
                transaction,
                ownerHandle,
                handles);
            AddDimensionsAndCallout(
                database,
                space,
                o,
                width,
                depth,
                p,
                s,
                "Trench drain section",
                layerId,
                transaction,
                ownerHandle,
                handles);
        }

        private static void GeneratePipeTrench(
            Database database,
            BlockTableRecord space,
            DynamicDetailLink link,
            ObjectId layerId,
            Transaction transaction,
            string ownerHandle,
            ICollection<string> handles)
        {
            DetailParameters p = link.Parameters;
            DynamicDetailSettings s = link.Settings;
            double width = ToDrawingUnits(p.WidthMillimetres, s);
            double depth = ToDrawingUnits(p.DepthMillimetres, s);
            double diameter = ToDrawingUnits(p.PipeDiameterMillimetres, s);
            double bedding = ToDrawingUnits(p.BeddingDepthMillimetres, s);
            if (width <= diameter || depth <= diameter + bedding)
                throw new InvalidOperationException(
                    "Pipe trench width/depth must exceed the pipe diameter and bedding depth.");

            Point3d o = link.InsertionPoint;
            AddRectangle(database, space, o, width, depth, layerId, transaction, ownerHandle, handles);
            var beddingLine = new Line(
                o + new Vector3d(0.0, bedding, 0.0),
                o + new Vector3d(width, bedding, 0.0));
            beddingLine.SetDatabaseDefaults(database);
            beddingLine.LayerId = layerId;
            AppendGenerated(space, beddingLine, transaction, ownerHandle, handles);

            Point3d pipeCenter = o + new Vector3d(
                width * 0.5,
                bedding + diameter * 0.5,
                0.0);
            var pipe = new Circle(pipeCenter, Vector3d.ZAxis, diameter * 0.5);
            pipe.SetDatabaseDefaults(database);
            pipe.LayerId = layerId;
            AppendGenerated(space, pipe, transaction, ownerHandle, handles);
            AddDimensionsAndCallout(
                database,
                space,
                o,
                width,
                depth,
                p,
                s,
                "Pipe trench section",
                layerId,
                transaction,
                ownerHandle,
                handles);
        }

        private static void GenerateValveChamber(
            Database database,
            BlockTableRecord space,
            DynamicDetailLink link,
            ObjectId layerId,
            Transaction transaction,
            string ownerHandle,
            ICollection<string> handles)
        {
            DetailParameters p = link.Parameters;
            DynamicDetailSettings s = link.Settings;
            double width = ToDrawingUnits(p.WidthMillimetres, s);
            double depth = ToDrawingUnits(p.DepthMillimetres, s);
            double wall = ToDrawingUnits(p.WallThicknessMillimetres, s);
            ValidateSection(width, depth, wall);

            Point3d o = link.InsertionPoint;
            AddRectangle(database, space, o, width, depth, layerId, transaction, ownerHandle, handles);
            AddRectangle(
                database,
                space,
                o + new Vector3d(wall, wall, 0.0),
                width - 2.0 * wall,
                depth - 2.0 * wall,
                layerId,
                transaction,
                ownerHandle,
                handles);
            AddGrating(
                database,
                space,
                o + new Vector3d(width * 0.25, depth, 0.0),
                width * 0.5,
                p.GratingType,
                s,
                layerId,
                transaction,
                ownerHandle,
                handles);

            double rungSpacing = Math.Max(s.TextHeight * 2.5, wall * 0.8);
            for (double y = wall * 1.5; y < depth - wall * 1.5; y += rungSpacing)
            {
                var rung = new Line(
                    o + new Vector3d(wall * 1.2, y, 0.0),
                    o + new Vector3d(wall * 2.0, y, 0.0));
                rung.SetDatabaseDefaults(database);
                rung.LayerId = layerId;
                AppendGenerated(space, rung, transaction, ownerHandle, handles);
            }
            AddDimensionsAndCallout(
                database,
                space,
                o,
                width,
                depth,
                p,
                s,
                "Valve chamber section",
                layerId,
                transaction,
                ownerHandle,
                handles);
        }

        private static void AddRectangle(
            Database database,
            BlockTableRecord space,
            Point3d origin,
            double width,
            double height,
            ObjectId layerId,
            Transaction transaction,
            string ownerHandle,
            ICollection<string> handles)
        {
            var polyline = new Polyline(4);
            polyline.SetDatabaseDefaults(database);
            polyline.LayerId = layerId;
            polyline.AddVertexAt(0, new Point2d(origin.X, origin.Y), 0.0, 0.0, 0.0);
            polyline.AddVertexAt(1, new Point2d(origin.X + width, origin.Y), 0.0, 0.0, 0.0);
            polyline.AddVertexAt(2, new Point2d(origin.X + width, origin.Y + height), 0.0, 0.0, 0.0);
            polyline.AddVertexAt(3, new Point2d(origin.X, origin.Y + height), 0.0, 0.0, 0.0);
            polyline.Closed = true;
            AppendGenerated(space, polyline, transaction, ownerHandle, handles);
        }

        private static void AddOpenInnerChannel(
            Database database,
            BlockTableRecord space,
            Point3d origin,
            double width,
            double height,
            ObjectId layerId,
            Transaction transaction,
            string ownerHandle,
            ICollection<string> handles)
        {
            var channel = new Polyline(4);
            channel.SetDatabaseDefaults(database);
            channel.LayerId = layerId;
            channel.AddVertexAt(0, new Point2d(origin.X, origin.Y + height), 0.0, 0.0, 0.0);
            channel.AddVertexAt(1, new Point2d(origin.X, origin.Y), 0.0, 0.0, 0.0);
            channel.AddVertexAt(2, new Point2d(origin.X + width, origin.Y), 0.0, 0.0, 0.0);
            channel.AddVertexAt(3, new Point2d(origin.X + width, origin.Y + height), 0.0, 0.0, 0.0);
            AppendGenerated(space, channel, transaction, ownerHandle, handles);
        }

        private static void AddGrating(
            Database database,
            BlockTableRecord space,
            Point3d start,
            double width,
            string gratingType,
            DynamicDetailSettings settings,
            ObjectId layerId,
            Transaction transaction,
            string ownerHandle,
            ICollection<string> handles)
        {
            var top = new Line(start, start + new Vector3d(width, 0.0, 0.0));
            top.SetDatabaseDefaults(database);
            top.LayerId = layerId;
            AppendGenerated(space, top, transaction, ownerHandle, handles);

            int divisions = Math.Max(4, Math.Min(20, (int)Math.Round(width / Math.Max(settings.TextHeight * 2.0, width / 10.0))));
            for (int index = 0; index <= divisions; index++)
            {
                double x = width * index / divisions;
                var bar = new Line(
                    start + new Vector3d(x, 0.0, 0.0),
                    start + new Vector3d(x + settings.TextHeight * 0.35, settings.TextHeight * 0.7, 0.0));
                bar.SetDatabaseDefaults(database);
                bar.LayerId = layerId;
                AppendGenerated(space, bar, transaction, ownerHandle, handles);
            }

            var label = new MText();
            label.SetDatabaseDefaults(database);
            label.LayerId = layerId;
            label.Location = start + new Vector3d(width * 0.5, settings.TextHeight * 1.2, 0.0);
            label.Attachment = AttachmentPoint.BottomCenter;
            label.TextHeight = settings.TextHeight;
            label.Contents = string.IsNullOrWhiteSpace(gratingType)
                ? "Cover / grating to approved specification"
                : gratingType;
            AppendGenerated(space, label, transaction, ownerHandle, handles);
        }

        private static void AddReinforcementMarkers(
            Database database,
            BlockTableRecord space,
            Point3d origin,
            double width,
            double depth,
            double wall,
            ObjectId layerId,
            Transaction transaction,
            string ownerHandle,
            ICollection<string> handles)
        {
            double radius = Math.Max(wall * 0.08, Math.Min(width, depth) * 0.008);
            Point3d[] points =
            {
                origin + new Vector3d(wall * 0.5, wall * 0.5, 0.0),
                origin + new Vector3d(width - wall * 0.5, wall * 0.5, 0.0),
                origin + new Vector3d(wall * 0.5, depth - wall * 0.5, 0.0),
                origin + new Vector3d(width - wall * 0.5, depth - wall * 0.5, 0.0)
            };
            foreach (Point3d point in points)
            {
                var circle = new Circle(point, Vector3d.ZAxis, radius);
                circle.SetDatabaseDefaults(database);
                circle.LayerId = layerId;
                AppendGenerated(space, circle, transaction, ownerHandle, handles);
            }
        }

        private static void AddDimensionsAndCallout(
            Database database,
            BlockTableRecord space,
            Point3d origin,
            double width,
            double depth,
            DetailParameters parameters,
            DynamicDetailSettings settings,
            string description,
            ObjectId layerId,
            Transaction transaction,
            string ownerHandle,
            ICollection<string> handles)
        {
            var widthDimension = new AlignedDimension(
                origin,
                origin + new Vector3d(width, 0.0, 0.0),
                origin + new Vector3d(0.0, -settings.DimensionOffset, 0.0),
                parameters.WidthMillimetres.ToString("0", CultureInfo.InvariantCulture) + " mm",
                database.Dimstyle);
            widthDimension.SetDatabaseDefaults(database);
            widthDimension.LayerId = layerId;
            AppendGenerated(space, widthDimension, transaction, ownerHandle, handles);

            var depthDimension = new AlignedDimension(
                origin,
                origin + new Vector3d(0.0, depth, 0.0),
                origin + new Vector3d(-settings.DimensionOffset, 0.0, 0.0),
                parameters.DepthMillimetres.ToString("0", CultureInfo.InvariantCulture) + " mm",
                database.Dimstyle);
            depthDimension.SetDatabaseDefaults(database);
            depthDimension.LayerId = layerId;
            AppendGenerated(space, depthDimension, transaction, ownerHandle, handles);

            var note = new MText();
            note.SetDatabaseDefaults(database);
            note.LayerId = layerId;
            note.Location = origin + new Vector3d(width + settings.TextHeight * 2.0, depth * 0.5, 0.0);
            note.Attachment = AttachmentPoint.MiddleLeft;
            note.TextHeight = settings.TextHeight;
            note.Contents =
                description +
                "\nConcrete: " + parameters.ConcreteStrength +
                "\nReinforcement: " + parameters.Reinforcement +
                "\nCover / grating: " + parameters.GratingType +
                "\nAll dimensions and specifications require engineer/authority review.";
            note.BackgroundFill = true;
            note.UseBackgroundColor = true;
            AppendGenerated(space, note, transaction, ownerHandle, handles);
        }

        private static Table BuildParameterTable(Database database, DynamicDetailLink link)
        {
            DynamicDetailSettings s = link.Settings;
            DetailParameters p = link.Parameters;
            double width = ToDrawingUnits(p.WidthMillimetres, s);
            Point3d position = link.InsertionPoint + new Vector3d(
                width + s.ScheduleOffset,
                -s.TextHeight * 8.0,
                0.0);
            var rows = new List<KeyValuePair<string, string>>
            {
                Pair("Detail ID", link.DetailId),
                Pair("Type", DisplayType(p.DetailType)),
                Pair("Width", p.WidthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Pair("Depth", p.DepthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Pair("Length", p.LengthMetres.ToString("0.###", CultureInfo.InvariantCulture) + " m"),
                Pair("Wall thickness", p.WallThicknessMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Pair("Pipe diameter", p.PipeDiameterMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Pair("Bedding depth", p.BeddingDepthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Pair("Concrete", p.ConcreteStrength),
                Pair("Reinforcement", p.Reinforcement),
                Pair("Grating / cover", p.GratingType),
                Pair("Review status", p.ReviewStatus),
                Pair("Reviewer / reference", p.Reviewer),
                Pair("Source template", string.IsNullOrWhiteSpace(link.SourcePath) ? "Built-in schematic / no external source selected" : link.SourcePath),
                Pair("Source SHA-256", link.SourceHash)
            };

            var table = new Table
            {
                TableStyle = database.Tablestyle,
                Position = position
            };
            table.SetSize(rows.Count + 2, 2);
            table.SetRowHeight(s.TextHeight * 1.8);
            table.Columns[0].Width = s.TextHeight * 18.0;
            table.Columns[1].Width = s.TextHeight * 55.0;
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 1));
            table.Cells[0, 0].TextString = "CE Dynamic Typical Detail Parameters";
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            table.Cells[1, 0].TextString = "Parameter";
            table.Cells[1, 1].TextString = "Value";
            for (int index = 0; index < rows.Count; index++)
            {
                table.Cells[index + 2, 0].TextString = rows[index].Key;
                table.Cells[index + 2, 1].TextString = rows[index].Value;
            }
            for (int row = 0; row < table.Rows.Count; row++)
            {
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    table.Cells[row, column].TextHeight = s.TextHeight;
                    table.Cells[row, column].Alignment = CellAlignment.MiddleLeft;
                }
            }
            table.GenerateLayout();
            return table;
        }

        private static Table BuildBoqTable(
            Database database,
            DynamicDetailLink link,
            IReadOnlyList<QuantityItem> quantities)
        {
            DynamicDetailSettings s = link.Settings;
            double width = ToDrawingUnits(link.Parameters.WidthMillimetres, s);
            Point3d position = link.InsertionPoint + new Vector3d(
                width + s.ScheduleOffset,
                -s.TextHeight * 45.0,
                0.0);
            var table = new Table
            {
                TableStyle = database.Tablestyle,
                Position = position
            };
            table.SetSize(quantities.Count + 3, 6);
            table.SetRowHeight(s.TextHeight * 1.8);
            double[] widths = { 10, 38, 8, 14, 14, 16 };
            for (int column = 0; column < widths.Length; column++)
                table.Columns[column].Width = s.TextHeight * widths[column];
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 5));
            table.Cells[0, 0].TextString =
                "Linked Dynamic Detail Quantity Schedule - " + link.DetailId;
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
            string[] headings = { "Item", "Description", "Unit", "Quantity", "Rate", "Amount" };
            for (int column = 0; column < headings.Length; column++)
                table.Cells[1, column].TextString = headings[column];
            for (int index = 0; index < quantities.Count; index++)
            {
                QuantityItem item = quantities[index];
                string[] values =
                {
                    item.Key,
                    item.Description,
                    item.Unit,
                    item.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
                    item.Rate.ToString("0.00", CultureInfo.InvariantCulture),
                    item.Amount.ToString("0.00", CultureInfo.InvariantCulture)
                };
                for (int column = 0; column < values.Length; column++)
                    table.Cells[index + 2, column].TextString = values[column];
            }
            table.MergeCells(CellRange.Create(table, quantities.Count + 2, 0, quantities.Count + 2, 5));
            table.Cells[quantities.Count + 2, 0].TextString =
                "Quantities are parameter-derived preliminary values. Rates may be entered manually; run CE_DETAILPARAMBOQ to recalculate amounts. " +
                "Reinforcement specification is recorded but not converted into a certified bar schedule.";
            for (int row = 0; row < table.Rows.Count; row++)
            {
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    table.Cells[row, column].TextHeight = s.TextHeight;
                    table.Cells[row, column].Alignment = CellAlignment.MiddleLeft;
                }
            }
            table.GenerateLayout();
            return table;
        }

        private static List<QuantityItem> CalculateQuantities(
            DetailParameters p,
            IDictionary<string, double> rates)
        {
            double width = p.WidthMillimetres / 1000.0;
            double depth = p.DepthMillimetres / 1000.0;
            double length = p.LengthMetres;
            double wall = p.WallThicknessMillimetres / 1000.0;
            double diameter = p.PipeDiameterMillimetres / 1000.0;
            double bedding = p.BeddingDepthMillimetres / 1000.0;
            var items = new List<QuantityItem>();

            if (p.DetailType.Equals("TrenchDrain", StringComparison.OrdinalIgnoreCase))
            {
                double innerWidth = Math.Max(0.0, width - 2.0 * wall);
                double innerDepth = Math.Max(0.0, depth - wall);
                AddQuantity(items, rates, "EXC", "Trench excavation", "m³", width * depth * length);
                AddQuantity(items, rates, "CONC", p.ConcreteStrength + " concrete", "m³", Math.Max(0.0, width * depth - innerWidth * innerDepth) * length);
                AddQuantity(items, rates, "GRATE", p.GratingType, "m", length);
                AddQuantity(items, rates, "REINF", "Reinforcement specification: " + p.Reinforcement, "item", 1.0);
            }
            else if (p.DetailType.Equals("PipeTrench", StringComparison.OrdinalIgnoreCase))
            {
                double excavation = width * depth * length;
                double beddingVolume = width * bedding * length;
                double pipeVolume = Math.PI * diameter * diameter * 0.25 * length;
                AddQuantity(items, rates, "EXC", "Trench excavation", "m³", excavation);
                AddQuantity(items, rates, "PIPE", "Pipe DN " + p.PipeDiameterMillimetres.ToString("0", CultureInfo.InvariantCulture), "m", length);
                AddQuantity(items, rates, "BED", "Selected bedding", "m³", beddingVolume);
                AddQuantity(items, rates, "BACKFILL", "Selected backfill excluding idealised pipe displacement", "m³", Math.Max(0.0, excavation - beddingVolume - pipeVolume));
            }
            else
            {
                double planLength = length;
                double innerWidth = Math.Max(0.0, width - 2.0 * wall);
                double innerLength = Math.Max(0.0, planLength - 2.0 * wall);
                double innerDepth = Math.Max(0.0, depth - wall);
                double outerVolume = width * planLength * depth;
                double voidVolume = innerWidth * innerLength * innerDepth;
                AddQuantity(items, rates, "EXC", "Valve chamber excavation envelope", "m³", outerVolume);
                AddQuantity(items, rates, "CONC", p.ConcreteStrength + " chamber concrete", "m³", Math.Max(0.0, outerVolume - voidVolume));
                AddQuantity(items, rates, "COVER", p.GratingType, "No.", 1.0);
                AddQuantity(items, rates, "REINF", "Reinforcement specification: " + p.Reinforcement, "item", 1.0);
            }
            return items;
        }

        private static void AddQuantity(
            ICollection<QuantityItem> items,
            IDictionary<string, double> rates,
            string key,
            string description,
            string unit,
            double quantity)
        {
            double rate;
            if (rates == null || !rates.TryGetValue(key, out rate))
                rate = 0.0;
            items.Add(new QuantityItem(
                key,
                description,
                unit,
                Math.Max(0.0, quantity),
                rate));
        }

        private static Dictionary<string, double> ReadExistingRates(
            Database database,
            DynamicDetailLink link)
        {
            var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            ObjectId tableId;
            if (string.IsNullOrWhiteSpace(link.BoqTableHandle) ||
                !TryResolveHandle(database, link.BoqTableHandle, out tableId))
                return rates;

            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(
                        tableId,
                        OpenMode.ForRead,
                        false) as Table;
                    if (table == null)
                        return rates;
                    for (int row = 2; row < table.Rows.Count - 1; row++)
                    {
                        string key = table.Cells[row, 0].TextString;
                        string rateText = table.Cells[row, 4].TextString;
                        double rate;
                        if (!string.IsNullOrWhiteSpace(key) &&
                            double.TryParse(
                                rateText,
                                NumberStyles.Any,
                                CultureInfo.InvariantCulture,
                                out rate))
                            rates[key.Trim()] = rate;
                    }
                }
            }
            catch
            {
                // A corrupt or manually changed schedule does not block regeneration.
            }
            return rates;
        }

        private static void AppendGenerated(
            BlockTableRecord space,
            Entity entity,
            Transaction transaction,
            string ownerHandle,
            ICollection<string> handles)
        {
            space.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
            entity.CreateExtensionDictionary();
            WriteGeneratedOwner(entity, transaction, ownerHandle);
            handles.Add(entity.Handle.ToString());
        }

        private static void WriteGeneratedOwner(
            Entity entity,
            Transaction transaction,
            string ownerHandle)
        {
            DBDictionary dictionary = transaction.GetObject(
                entity.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            Xrecord record = OpenOrCreateRecord(
                dictionary,
                GeneratedRecordName,
                transaction);
            record.Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "Owner=" + ownerHandle));
        }

        private static void WriteLink(
            Entity anchor,
            Transaction transaction,
            DynamicDetailLink link)
        {
            if (anchor.ExtensionDictionary.IsNull)
                anchor.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                anchor.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            Xrecord record = OpenOrCreateRecord(dictionary, LinkRecordName, transaction);
            var values = new List<TypedValue>
            {
                Text("Schema", link.Schema),
                Text("DetailId", link.DetailId),
                Text("InsertionX", link.InsertionPoint.X.ToString("R", CultureInfo.InvariantCulture)),
                Text("InsertionY", link.InsertionPoint.Y.ToString("R", CultureInfo.InvariantCulture)),
                Text("InsertionZ", link.InsertionPoint.Z.ToString("R", CultureInfo.InvariantCulture)),
                Text("DetailType", link.Parameters.DetailType),
                Text("WidthMm", link.Parameters.WidthMillimetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("DepthMm", link.Parameters.DepthMillimetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("LengthM", link.Parameters.LengthMetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("WallMm", link.Parameters.WallThicknessMillimetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("PipeMm", link.Parameters.PipeDiameterMillimetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("BeddingMm", link.Parameters.BeddingDepthMillimetres.ToString("R", CultureInfo.InvariantCulture)),
                Text("Concrete", Encode(link.Parameters.ConcreteStrength)),
                Text("Reinforcement", Encode(link.Parameters.Reinforcement)),
                Text("Grating", Encode(link.Parameters.GratingType)),
                Text("ReviewStatus", Encode(link.Parameters.ReviewStatus)),
                Text("Reviewer", Encode(link.Parameters.Reviewer)),
                Text("ReviewedAt", link.Parameters.ReviewedAtUtc),
                Text("SourcePath", Encode(link.SourcePath)),
                Text("SourceHash", link.SourceHash),
                Text("SourceModified", link.SourceModifiedUtc),
                Text("UnitsPerMetre", link.Settings.DrawingUnitsPerMetre.ToString("R", CultureInfo.InvariantCulture)),
                Text("TextHeight", link.Settings.TextHeight.ToString("R", CultureInfo.InvariantCulture)),
                Text("DimensionOffset", link.Settings.DimensionOffset.ToString("R", CultureInfo.InvariantCulture)),
                Text("ScheduleOffset", link.Settings.ScheduleOffset.ToString("R", CultureInfo.InvariantCulture)),
                Text("DetailLayer", Encode(link.Settings.DetailLayer)),
                Text("BoqLayer", Encode(link.Settings.BoqLayer)),
                Text("BoqTable", link.BoqTableHandle)
            };
            foreach (string handle in link.GeneratedHandles.Distinct(StringComparer.OrdinalIgnoreCase))
                values.Add(Text("Generated", handle));
            record.Data = new ResultBuffer(values.ToArray());
        }

        private static DynamicDetailLink ReadLink(
            Entity anchor,
            Transaction transaction)
        {
            if (anchor == null || anchor.ExtensionDictionary.IsNull)
                throw new InvalidOperationException("The selected object has no dynamic-detail link.");
            DBDictionary dictionary = transaction.GetObject(
                anchor.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkRecordName))
                throw new InvalidOperationException("The selected object has no dynamic-detail link.");
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(LinkRecordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null)
                throw new InvalidOperationException("The dynamic-detail link record is empty.");

            var data = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (TypedValue value in record.Data)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                int divider = text.IndexOf('=');
                if (divider < 0)
                    continue;
                string key = text.Substring(0, divider);
                string item = text.Substring(divider + 1);
                List<string> list;
                if (!data.TryGetValue(key, out list))
                {
                    list = new List<string>();
                    data[key] = list;
                }
                list.Add(item);
            }

            var parameters = new DetailParameters
            {
                DetailType = Get(data, "DetailType", "TrenchDrain"),
                WidthMillimetres = GetDouble(data, "WidthMm", 1000.0),
                DepthMillimetres = GetDouble(data, "DepthMm", 1000.0),
                LengthMetres = GetDouble(data, "LengthM", 1.0),
                WallThicknessMillimetres = GetDouble(data, "WallMm", 150.0),
                PipeDiameterMillimetres = GetDouble(data, "PipeMm", 300.0),
                BeddingDepthMillimetres = GetDouble(data, "BeddingMm", 150.0),
                ConcreteStrength = Decode(Get(data, "Concrete", Encode("30 MPa"))),
                Reinforcement = Decode(Get(data, "Reinforcement", Encode("Engineer designed"))),
                GratingType = Decode(Get(data, "Grating", Encode("Heavy-duty grating"))),
                ReviewStatus = Decode(Get(data, "ReviewStatus", Encode("Draft"))),
                Reviewer = Decode(Get(data, "Reviewer", string.Empty)),
                ReviewedAtUtc = Get(data, "ReviewedAt", string.Empty)
            };
            var settings = new DynamicDetailSettings
            {
                DrawingUnitsPerMetre = GetDouble(data, "UnitsPerMetre", 1000.0),
                TextHeight = GetDouble(data, "TextHeight", 100.0),
                DimensionOffset = GetDouble(data, "DimensionOffset", 300.0),
                ScheduleOffset = GetDouble(data, "ScheduleOffset", 1200.0),
                DetailLayer = Decode(Get(data, "DetailLayer", Encode(DefaultDetailLayer))),
                BoqLayer = Decode(Get(data, "BoqLayer", Encode(DefaultBoqLayer)))
            };
            settings.Normalize();
            parameters.Normalize();
            return new DynamicDetailLink(
                Get(data, "Schema", SchemaVersion),
                Get(data, "DetailId", "DD-" + anchor.Handle),
                new Point3d(
                    GetDouble(data, "InsertionX", 0.0),
                    GetDouble(data, "InsertionY", 0.0),
                    GetDouble(data, "InsertionZ", 0.0)),
                parameters,
                settings,
                Decode(Get(data, "SourcePath", string.Empty)),
                Get(data, "SourceHash", string.Empty),
                Get(data, "SourceModified", string.Empty),
                GetList(data, "Generated"),
                Get(data, "BoqTable", string.Empty));
        }

        private static bool PromptLinkedDetail(
            Document document,
            out ObjectId anchorId,
            out DynamicDetailLink link)
        {
            anchorId = ObjectId.Null;
            link = null;
            PromptEntityResult result = document.Editor.GetEntity(
                "\nSelect a CE dynamic detail anchor, generated geometry or linked schedule: ");
            if (result.Status != PromptStatus.OK)
                return false;

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Entity selected = transaction.GetObject(
                        result.ObjectId,
                        OpenMode.ForRead,
                        false) as Entity;
                    if (selected == null)
                        return false;
                    if (HasExtensionRecord(selected, transaction, LinkRecordName))
                        anchorId = selected.ObjectId;
                    else
                    {
                        string ownerHandle = ReadGeneratedOwner(selected, transaction);
                        if (string.IsNullOrWhiteSpace(ownerHandle) ||
                            !TryResolveHandle(document.Database, ownerHandle, out anchorId))
                        {
                            document.Editor.WriteMessage(
                                "\nThe selected object is not linked to a CE dynamic typical detail.");
                            return false;
                        }
                    }
                    Entity anchor = transaction.GetObject(
                        anchorId,
                        OpenMode.ForRead,
                        false) as Entity;
                    link = ReadLink(anchor, transaction);
                    return true;
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nThe selected dynamic-detail link could not be read. " + exception.Message);
                return false;
            }
        }

        private static string ReadGeneratedOwner(Entity entity, Transaction transaction)
        {
            if (entity == null || entity.ExtensionDictionary.IsNull)
                return string.Empty;
            DBDictionary dictionary = transaction.GetObject(
                entity.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(GeneratedRecordName))
                return string.Empty;
            Xrecord record = transaction.GetObject(
                dictionary.GetAt(GeneratedRecordName),
                OpenMode.ForRead,
                false) as Xrecord;
            if (record == null || record.Data == null)
                return string.Empty;
            string value = record.Data.AsArray()
                .Where(item => item.TypeCode == (int)DxfCode.Text)
                .Select(item => item.Value as string)
                .FirstOrDefault(item => item != null && item.StartsWith("Owner=", StringComparison.OrdinalIgnoreCase));
            return value == null ? string.Empty : value.Substring("Owner=".Length);
        }

        private static bool HasExtensionRecord(
            Entity entity,
            Transaction transaction,
            string name)
        {
            if (entity == null || entity.ExtensionDictionary.IsNull)
                return false;
            DBDictionary dictionary = transaction.GetObject(
                entity.ExtensionDictionary,
                OpenMode.ForRead,
                false) as DBDictionary;
            return dictionary != null && dictionary.Contains(name);
        }

        private static void RemoveExtensionRecord(
            Entity entity,
            Transaction transaction,
            string name)
        {
            if (entity == null || entity.ExtensionDictionary.IsNull)
                return;
            DBDictionary dictionary = transaction.GetObject(
                entity.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(name))
                return;
            DBObject record = transaction.GetObject(
                dictionary.GetAt(name),
                OpenMode.ForWrite,
                false);
            dictionary.Remove(name);
            record.Erase();
        }

        private static Xrecord OpenOrCreateRecord(
            DBDictionary dictionary,
            string name,
            Transaction transaction)
        {
            if (dictionary.Contains(name))
                return transaction.GetObject(
                    dictionary.GetAt(name),
                    OpenMode.ForWrite,
                    false) as Xrecord;
            var record = new Xrecord();
            dictionary.SetAt(name, record);
            transaction.AddNewlyCreatedDBObject(record, true);
            return record;
        }

        private static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string requested,
            string fallback)
        {
            string name = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
            LayerTable layers = transaction.GetObject(
                database.LayerTableId,
                OpenMode.ForRead,
                false) as LayerTable;
            if (layers.Has(name))
            {
                ObjectId id = layers[name];
                LayerTableRecord existing = transaction.GetObject(
                    id,
                    OpenMode.ForRead,
                    false) as LayerTableRecord;
                if (existing != null && existing.IsLocked)
                    throw new InvalidOperationException("Layer '" + name + "' is locked.");
                return id;
            }
            layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            ObjectId layerId = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            return layerId;
        }

        private static bool PromptNewParameters(
            Editor editor,
            out DetailParameters parameters)
        {
            parameters = new DetailParameters();
            var typeOptions = new PromptKeywordOptions(
                "\nDynamic detail type [TrenchDrain/PipeTrench/ValveChamber] <TrenchDrain>: ")
            {
                AllowNone = true
            };
            typeOptions.Keywords.Add("TrenchDrain");
            typeOptions.Keywords.Add("PipeTrench");
            typeOptions.Keywords.Add("ValveChamber");
            PromptResult typeResult = editor.GetKeywords(typeOptions);
            if (typeResult.Status == PromptStatus.Cancel)
                return false;
            parameters.DetailType = typeResult.Status == PromptStatus.OK
                ? typeResult.StringResult
                : "TrenchDrain";
            return PromptEditableParameters(editor, parameters);
        }

        private static bool PromptEditableParameters(Editor editor, DetailParameters p)
        {
            if (!PromptPositiveDouble(editor, "Overall width in millimetres", p.WidthMillimetres, out p.WidthMillimetres))
                return false;
            if (!PromptPositiveDouble(editor, "Overall depth in millimetres", p.DepthMillimetres, out p.DepthMillimetres))
                return false;
            if (!PromptPositiveDouble(
                    editor,
                    p.DetailType.Equals("ValveChamber", StringComparison.OrdinalIgnoreCase)
                        ? "Plan length in metres"
                        : "Scheduled detail length in metres",
                    p.LengthMetres,
                    out p.LengthMetres))
                return false;
            if (!PromptPositiveDouble(editor, "Wall/base thickness in millimetres", p.WallThicknessMillimetres, out p.WallThicknessMillimetres))
                return false;
            if (!PromptPositiveDouble(editor, "Pipe diameter in millimetres", p.PipeDiameterMillimetres, out p.PipeDiameterMillimetres))
                return false;
            if (!PromptPositiveDouble(editor, "Bedding depth in millimetres", p.BeddingDepthMillimetres, out p.BeddingDepthMillimetres))
                return false;
            if (!PromptText(editor, "Concrete strength/specification", p.ConcreteStrength, out p.ConcreteStrength))
                return false;
            if (!PromptText(editor, "Reinforcement specification", p.Reinforcement, out p.Reinforcement))
                return false;
            if (!PromptText(editor, "Grating/cover type", p.GratingType, out p.GratingType))
                return false;
            p.Normalize();
            return true;
        }

        private static string PromptOptionalSourceTemplate(Editor editor)
        {
            var options = new PromptKeywordOptions(
                "\nReference an approved source DWG template [Select/None] <None>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Select");
            options.Keywords.Add("None");
            PromptResult result = editor.GetKeywords(options);
            if (result.Status != PromptStatus.OK ||
                !result.StringResult.Equals("Select", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            var dialog = new OpenFileDialog(
                "Select approved source DWG template (read-only reference)",
                string.Empty,
                "dwg",
                "CE_DETAILPARAMCREATE",
                OpenFileDialog.OpenFileDialogFlags.DoNotTransferRemoteFiles);
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return string.Empty;
            return Path.GetFullPath(dialog.Filename);
        }

        private static void WritePreview(
            Editor editor,
            DetailParameters p,
            string sourcePath,
            DynamicDetailSettings settings)
        {
            editor.WriteMessage(
                "\nCE dynamic-detail preview" +
                "\n  Type: " + DisplayType(p.DetailType) +
                "\n  Width x depth: " + p.WidthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) +
                " x " + p.DepthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm" +
                "\n  Scheduled/plan length: " + p.LengthMetres.ToString("0.###", CultureInfo.InvariantCulture) + " m" +
                "\n  Wall/base thickness: " + p.WallThicknessMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm" +
                "\n  Pipe diameter: " + p.PipeDiameterMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm" +
                "\n  Bedding: " + p.BeddingDepthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm" +
                "\n  Concrete: " + p.ConcreteStrength +
                "\n  Reinforcement: " + p.Reinforcement +
                "\n  Grating/cover: " + p.GratingType +
                "\n  Source template: " + (string.IsNullOrWhiteSpace(sourcePath) ? "<None / built-in schematic>" : sourcePath) +
                "\n  Drawing units per metre: " + settings.DrawingUnitsPerMetre.ToString("0.###", CultureInfo.InvariantCulture) +
                "\n  The source template will remain external and unmodified. Generated geometry and quantities require engineer/authority review.");
        }

        private static void ValidateSection(double width, double depth, double wall)
        {
            if (width <= GeometryTolerance || depth <= GeometryTolerance || wall <= GeometryTolerance)
                throw new InvalidOperationException("Width, depth and wall thickness must be positive.");
            if (width <= 2.0 * wall || depth <= 2.0 * wall)
                throw new InvalidOperationException(
                    "Overall width/depth must exceed twice the wall thickness.");
        }

        private static double ToDrawingUnits(double millimetres, DynamicDetailSettings settings)
        {
            return millimetres * settings.DrawingUnitsPerMetre / 1000.0;
        }

        private static string DisplayType(string value)
        {
            if (value.Equals("TrenchDrain", StringComparison.OrdinalIgnoreCase)) return "Trench Drain";
            if (value.Equals("PipeTrench", StringComparison.OrdinalIgnoreCase)) return "Pipe Trench";
            return "Valve Chamber";
        }

        private static string StatusKeyword(string status)
        {
            if (status == null) return "Draft";
            if (status.StartsWith("Approved", StringComparison.OrdinalIgnoreCase)) return "ApprovedRecord";
            if (status.Equals("For Review", StringComparison.OrdinalIgnoreCase)) return "ForReview";
            if (status.Equals("Reviewed", StringComparison.OrdinalIgnoreCase)) return "Reviewed";
            return "Draft";
        }

        private static string ComputeSha256(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return string.Empty;
            try
            {
                using (FileStream stream = File.Open(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (SHA256 algorithm = SHA256.Create())
                {
                    return string.Concat(algorithm.ComputeHash(stream)
                        .Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ReadSourceModifiedUtc(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return string.Empty;
            try
            {
                return File.GetLastWriteTimeUtc(path).ToString("o", CultureInfo.InvariantCulture);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool TryResolveHandle(
            Database database,
            string handleText,
            out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            long value;
            if (!long.TryParse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                return false;
            try
            {
                objectId = database.GetObjectId(false, new Handle(value), 0);
                return !objectId.IsNull && !objectId.IsErased;
            }
            catch
            {
                return false;
            }
        }

        private static TypedValue Text(string key, string value)
        {
            return new TypedValue(
                (int)DxfCode.Text,
                key + "=" + (value ?? string.Empty));
        }

        private static string Get(
            IDictionary<string, List<string>> data,
            string key,
            string fallback)
        {
            List<string> values;
            return data.TryGetValue(key, out values) && values.Count > 0
                ? values[0]
                : fallback;
        }

        private static List<string> GetList(
            IDictionary<string, List<string>> data,
            string key)
        {
            List<string> values;
            return data.TryGetValue(key, out values)
                ? new List<string>(values)
                : new List<string>();
        }

        private static double GetDouble(
            IDictionary<string, List<string>> data,
            string key,
            double fallback)
        {
            double value;
            return double.TryParse(
                Get(data, key, string.Empty),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value)
                ? value
                : fallback;
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty)); }
            catch { return string.Empty; }
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value ?? string.Empty);
        }

        private static IList<string> Row(string key, string value)
        {
            return new List<string> { key, value ?? string.Empty };
        }

        private static bool PromptText(
            Editor editor,
            string label,
            string current,
            out string value)
        {
            var options = new PromptStringOptions(
                "\n" + label + " <" + (current ?? string.Empty) + ">: ")
            {
                AllowSpaces = true
            };
            PromptResult result = editor.GetString(options);
            if (result.Status == PromptStatus.Cancel)
            {
                value = current;
                return false;
            }
            value = result.Status == PromptStatus.None
                ? current
                : result.StringResult.Trim();
            return true;
        }

        private static bool PromptPositiveDouble(
            Editor editor,
            string label,
            double current,
            out double value)
        {
            var options = new PromptDoubleOptions(
                "\n" + label + " <" + current.ToString("0.###", CultureInfo.InvariantCulture) + ">: ")
            {
                AllowNegative = false,
                AllowZero = false,
                UseDefaultValue = true,
                DefaultValue = current
            };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : current;
            return result.Status == PromptStatus.OK;
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
                   result.StringResult.Equals("Yes", StringComparison.OrdinalIgnoreCase);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class GeneratedSet
        {
            public GeneratedSet(List<string> handles, string boqTableHandle)
            {
                Handles = handles;
                BoqTableHandle = boqTableHandle;
            }
            public List<string> Handles { get; }
            public string BoqTableHandle { get; }
        }

        private sealed class QuantityItem
        {
            public QuantityItem(
                string key,
                string description,
                string unit,
                double quantity,
                double rate)
            {
                Key = key;
                Description = description;
                Unit = unit;
                Quantity = quantity;
                Rate = rate;
            }
            public string Key { get; }
            public string Description { get; }
            public string Unit { get; }
            public double Quantity { get; }
            public double Rate { get; }
            public double Amount => Quantity * Rate;
        }

        private sealed class DetailParameters
        {
            public string DetailType = "TrenchDrain";
            public double WidthMillimetres = 1000.0;
            public double DepthMillimetres = 1000.0;
            public double LengthMetres = 1.0;
            public double WallThicknessMillimetres = 150.0;
            public double PipeDiameterMillimetres = 300.0;
            public double BeddingDepthMillimetres = 150.0;
            public string ConcreteStrength = "30 MPa";
            public string Reinforcement = "Engineer designed";
            public string GratingType = "Heavy-duty grating / cover";
            public string ReviewStatus = "Draft";
            public string Reviewer = string.Empty;
            public string ReviewedAtUtc = string.Empty;

            public DetailParameters Clone()
            {
                return (DetailParameters)MemberwiseClone();
            }

            public void Normalize()
            {
                if (!new[] { "TrenchDrain", "PipeTrench", "ValveChamber" }
                    .Contains(DetailType, StringComparer.OrdinalIgnoreCase))
                    DetailType = "TrenchDrain";
                if (WidthMillimetres <= 0.0) WidthMillimetres = 1000.0;
                if (DepthMillimetres <= 0.0) DepthMillimetres = 1000.0;
                if (LengthMetres <= 0.0) LengthMetres = 1.0;
                if (WallThicknessMillimetres <= 0.0) WallThicknessMillimetres = 150.0;
                if (PipeDiameterMillimetres <= 0.0) PipeDiameterMillimetres = 300.0;
                if (BeddingDepthMillimetres <= 0.0) BeddingDepthMillimetres = 150.0;
                if (ConcreteStrength == null) ConcreteStrength = string.Empty;
                if (Reinforcement == null) Reinforcement = string.Empty;
                if (GratingType == null) GratingType = string.Empty;
                if (string.IsNullOrWhiteSpace(ReviewStatus)) ReviewStatus = "Draft";
                if (Reviewer == null) Reviewer = string.Empty;
                if (ReviewedAtUtc == null) ReviewedAtUtc = string.Empty;
            }
        }

        private sealed class DynamicDetailSettings
        {
            public double DrawingUnitsPerMetre = 1000.0;
            public double TextHeight = 100.0;
            public double DimensionOffset = 300.0;
            public double ScheduleOffset = 1200.0;
            public string DetailLayer = DefaultDetailLayer;
            public string BoqLayer = DefaultBoqLayer;

            public static DynamicDetailSettings Read(Database database)
            {
                var settings = new DynamicDetailSettings();
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary nod = transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (nod == null || !nod.Contains(CeDictionaryName))
                        return settings;
                    DBDictionary ce = transaction.GetObject(
                        nod.GetAt(CeDictionaryName),
                        OpenMode.ForRead,
                        false) as DBDictionary;
                    if (ce == null || !ce.Contains(SettingsRecordName))
                        return settings;
                    Xrecord record = transaction.GetObject(
                        ce.GetAt(SettingsRecordName),
                        OpenMode.ForRead,
                        false) as Xrecord;
                    string[] values = record == null || record.Data == null
                        ? new string[0]
                        : record.Data.AsArray()
                            .Where(value => value.TypeCode == (int)DxfCode.Text)
                            .Select(value => Convert.ToString(value.Value, CultureInfo.InvariantCulture))
                            .ToArray();
                    if (values.Length >= 6)
                    {
                        double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.DrawingUnitsPerMetre);
                        double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.TextHeight);
                        double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.DimensionOffset);
                        double.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out settings.ScheduleOffset);
                        settings.DetailLayer = values[4];
                        settings.BoqLayer = values[5];
                    }
                }
                settings.Normalize();
                return settings;
            }

            public void Write(Database database)
            {
                Normalize();
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    DBDictionary nod = transaction.GetObject(
                        database.NamedObjectsDictionaryId,
                        OpenMode.ForWrite,
                        false) as DBDictionary;
                    DBDictionary ce;
                    if (nod.Contains(CeDictionaryName))
                        ce = transaction.GetObject(
                            nod.GetAt(CeDictionaryName),
                            OpenMode.ForWrite,
                            false) as DBDictionary;
                    else
                    {
                        ce = new DBDictionary();
                        nod.SetAt(CeDictionaryName, ce);
                        transaction.AddNewlyCreatedDBObject(ce, true);
                    }
                    Xrecord record = OpenOrCreateRecord(
                        ce,
                        SettingsRecordName,
                        transaction);
                    string[] values =
                    {
                        DrawingUnitsPerMetre.ToString("R", CultureInfo.InvariantCulture),
                        TextHeight.ToString("R", CultureInfo.InvariantCulture),
                        DimensionOffset.ToString("R", CultureInfo.InvariantCulture),
                        ScheduleOffset.ToString("R", CultureInfo.InvariantCulture),
                        DetailLayer,
                        BoqLayer
                    };
                    record.Data = new ResultBuffer(values
                        .Select(value => new TypedValue((int)DxfCode.Text, value))
                        .ToArray());
                    transaction.Commit();
                }
            }

            public void Normalize()
            {
                if (DrawingUnitsPerMetre <= 0.0) DrawingUnitsPerMetre = 1000.0;
                if (TextHeight <= 0.0) TextHeight = DrawingUnitsPerMetre * 0.1;
                if (DimensionOffset <= 0.0) DimensionOffset = DrawingUnitsPerMetre * 0.3;
                if (ScheduleOffset <= 0.0) ScheduleOffset = DrawingUnitsPerMetre * 1.2;
                if (string.IsNullOrWhiteSpace(DetailLayer)) DetailLayer = DefaultDetailLayer;
                if (string.IsNullOrWhiteSpace(BoqLayer)) BoqLayer = DefaultBoqLayer;
            }
        }

        private sealed class DynamicDetailLink
        {
            public DynamicDetailLink(
                string schema,
                string detailId,
                Point3d insertionPoint,
                DetailParameters parameters,
                DynamicDetailSettings settings,
                string sourcePath,
                string sourceHash,
                string sourceModifiedUtc,
                IEnumerable<string> generatedHandles,
                string boqTableHandle)
            {
                Schema = schema ?? SchemaVersion;
                DetailId = detailId ?? string.Empty;
                InsertionPoint = insertionPoint;
                Parameters = parameters ?? new DetailParameters();
                Settings = settings ?? new DynamicDetailSettings();
                SourcePath = sourcePath ?? string.Empty;
                SourceHash = sourceHash ?? string.Empty;
                SourceModifiedUtc = sourceModifiedUtc ?? string.Empty;
                GeneratedHandles = generatedHandles == null
                    ? new List<string>()
                    : generatedHandles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                BoqTableHandle = boqTableHandle ?? string.Empty;
            }
            public string Schema { get; }
            public string DetailId { get; }
            public Point3d InsertionPoint { get; }
            public DetailParameters Parameters { get; }
            public DynamicDetailSettings Settings { get; }
            public string SourcePath { get; }
            public string SourceHash { get; }
            public string SourceModifiedUtc { get; }
            public List<string> GeneratedHandles { get; }
            public string BoqTableHandle { get; }

            public DynamicDetailLink WithParameters(DetailParameters parameters)
            {
                return new DynamicDetailLink(
                    Schema,
                    DetailId,
                    InsertionPoint,
                    parameters,
                    Settings,
                    SourcePath,
                    SourceHash,
                    SourceModifiedUtc,
                    GeneratedHandles,
                    BoqTableHandle);
            }

            public DynamicDetailLink WithGenerated(
                IEnumerable<string> handles,
                string boqTableHandle)
            {
                return new DynamicDetailLink(
                    Schema,
                    DetailId,
                    InsertionPoint,
                    Parameters,
                    Settings,
                    SourcePath,
                    SourceHash,
                    SourceModifiedUtc,
                    handles,
                    boqTableHandle);
            }
        }
    }
}
