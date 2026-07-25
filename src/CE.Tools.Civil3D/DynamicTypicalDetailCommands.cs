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
    /// Parameter-driven, source-traceable typical details. Generated geometry is
    /// linked to a DBPoint anchor, can be regenerated or detached, and never writes
    /// to the optional approved source DWG. Quantities are preliminary measurable
    /// outputs and remain subject to engineering and authority review.
    /// </summary>
    public sealed partial class DynamicTypicalDetailCommands
    {
        internal const string LinkRecordName = "CE_DYNAMIC_TYPICAL_DETAIL";
        internal const string GeneratedRecordName = "CE_DYNAMIC_TYPICAL_DETAIL_GENERATED";
        internal const string BoqLinkRecordName = "CE_DYNAMIC_DETAIL_BOQ_LINK";
        private const string CeDictionaryName = "CE_TOOLS";
        private const string SettingsRecordName = "DYNAMIC_TYPICAL_DETAIL_SETTINGS";
        private const string SchemaVersion = "2";
        private const string DefaultDetailLayer = "CE-DYNAMIC-DETAIL";
        private const string DefaultBoqLayer = "CE-DYNAMIC-DETAIL-BOQ";
        private const double GeometryTolerance = 1e-9;

        private static readonly string[] SupportedTypes =
        {
            "TrenchDrain", "PipeTrench", "ValveChamber", "Kerb", "Headwall"
        };

        [CommandMethod("CE_DETAILPARAMTOOLS", CommandFlags.Modal | CommandFlags.Redraw)]
        public void DynamicDetailTools()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            var options = new PromptKeywordOptions(
                "\nDynamic typical-detail tools [Create/Edit/Refresh/BOQ/Export/Review/Information/Detach/Clear/Settings] <Create>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[]
            {
                "Create", "Edit", "Refresh", "BOQ", "Export", "Review",
                "Information", "Detach", "Clear", "Settings"
            })
                options.Keywords.Add(keyword);

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return;

            string choice = result.Status == PromptStatus.OK ? result.StringResult : "Create";
            if (choice.Equals("Edit", StringComparison.OrdinalIgnoreCase)) EditParameters();
            else if (choice.Equals("Refresh", StringComparison.OrdinalIgnoreCase)) Refresh();
            else if (choice.Equals("BOQ", StringComparison.OrdinalIgnoreCase)) RefreshBoq();
            else if (choice.Equals("Export", StringComparison.OrdinalIgnoreCase)) ExportBoq();
            else if (choice.Equals("Review", StringComparison.OrdinalIgnoreCase)) RecordReviewStatus();
            else if (choice.Equals("Information", StringComparison.OrdinalIgnoreCase)) Information();
            else if (choice.Equals("Detach", StringComparison.OrdinalIgnoreCase)) Detach();
            else if (choice.Equals("Clear", StringComparison.OrdinalIgnoreCase)) Clear();
            else if (choice.Equals("Settings", StringComparison.OrdinalIgnoreCase)) ConfigureSettings();
            else Create();
        }

        [CommandMethod("CE_DETAILPARAMSETTINGS", CommandFlags.Modal)]
        public void ConfigureSettings()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            Editor editor = document.Editor;
            DynamicDetailSettings settings = DynamicDetailSettings.Read(document.Database);
            if (!PromptPositiveDouble(editor, "Drawing units per metre (1000 for mm drawings, 1 for metre drawings)", settings.DrawingUnitsPerMetre, out settings.DrawingUnitsPerMetre)) return;
            if (!PromptPositiveDouble(editor, "Text height in drawing units", settings.TextHeight, out settings.TextHeight)) return;
            if (!PromptPositiveDouble(editor, "Dimension offset in drawing units", settings.DimensionOffset, out settings.DimensionOffset)) return;
            if (!PromptPositiveDouble(editor, "Schedule offset in drawing units", settings.ScheduleOffset, out settings.ScheduleOffset)) return;
            if (!PromptText(editor, "Generated detail layer", settings.DetailLayer, out settings.DetailLayer)) return;
            if (!PromptText(editor, "Generated BOQ layer", settings.BoqLayer, out settings.BoqLayer)) return;

            settings.Write(document.Database);
            editor.WriteMessage("\nCE_DETAILPARAMSETTINGS saved. Approved source templates remain external and read-only.");
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

            PromptPointResult insertion = document.Editor.GetPoint("\nPick the insertion point for the generated dynamic detail: ");
            if (insertion.Status != PromptStatus.OK)
                return;
            Point3d insertionPoint = insertion.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);

            DynamicDetailSettings settings = DynamicDetailSettings.Read(document.Database);
            WritePreview(document.Editor, parameters, sourcePath, settings);
            if (!Confirm(document.Editor, "Create this linked parameter-driven detail variant and quantity schedule"))
            {
                document.Editor.WriteMessage("\nCE_DETAILPARAMCREATE cancelled. No geometry or schedule was created.");
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
                    "\nCE_DETAILPARAMCREATE complete. Anchor handle={0}; type={1}; review status=Draft. The source template was not modified.",
                    anchorId.Handle,
                    DisplayType(parameters.DetailType));
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_DETAILPARAMCREATE failed. No linked-detail transaction was committed. " + exception.Message);
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
            document.Editor.WriteMessage("\nChanging parameters invalidates the previous review record. The regenerated variant will return to Draft.");
            if (!Confirm(document.Editor, "Regenerate this detail with the edited parameters"))
                return;

            ResetReview(edited);
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
            bool sourceDrift = !string.IsNullOrWhiteSpace(link.SourcePath) &&
                (!File.Exists(link.SourcePath) ||
                 string.IsNullOrWhiteSpace(currentHash) ||
                 !currentHash.Equals(link.SourceHash, StringComparison.OrdinalIgnoreCase));
            DynamicDetailLink refreshLink = link;
            if (sourceDrift)
            {
                document.Editor.WriteMessage(
                    "\nWARNING: the referenced source template is missing or its SHA-256 differs from the stored identity. The stored source identity will not be replaced silently and review status will reset to Draft.");
                DetailParameters parameters = link.Parameters.Clone();
                ResetReview(parameters);
                refreshLink = link.WithParameters(parameters);
            }
            Regenerate(document, anchorId, refreshLink, true, "CE_DETAILPARAMREFRESH");
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
            document.Editor.WriteMessage("\nExisting rates were preserved by item key where possible; quantities, BOQ-link metadata and amounts were recalculated.");
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
                        "Quantity", "Rate", "Amount", "Review Status", "Source Template", "Source SHA-256"
                    }
                };
                foreach (QuantityItem item in items)
                {
                    rows.Add(new List<string>
                    {
                        item.Key,
                        link.DetailId,
                        DisplayType(link.Parameters.DetailType),
                        item.Description,
                        item.Unit,
                        item.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
                        item.Rate.ToString("0.00", CultureInfo.InvariantCulture),
                        item.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                        link.Parameters.ReviewStatus,
                        link.SourcePath,
                        link.SourceHash
                    });
                }
                SimpleXlsxWriter.WriteWorkbook(dialog.Filename, "Dynamic Detail BOQ", rows);
                document.Editor.WriteMessage("\nCE_DETAILPARAMBOQEXPORT complete: " + dialog.Filename);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_DETAILPARAMBOQEXPORT failed. " + exception.Message);
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
                "\nRecord detail review status [Draft/ForReview/Reviewed/ApprovedRecord] <" + StatusKeyword(link.Parameters.ReviewStatus) + ">: ")
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

            string keyword = result.Status == PromptStatus.OK ? result.StringResult : StatusKeyword(link.Parameters.ReviewStatus);
            string status = keyword.Equals("ApprovedRecord", StringComparison.OrdinalIgnoreCase)
                ? "Approved (recorded)"
                : keyword.Equals("ForReview", StringComparison.OrdinalIgnoreCase) ? "For Review" : keyword;

            string reviewer = string.Empty;
            if (!status.Equals("Draft", StringComparison.OrdinalIgnoreCase))
            {
                if (!PromptText(document.Editor, "Reviewer/approver name or reference", link.Parameters.Reviewer, out reviewer) || string.IsNullOrWhiteSpace(reviewer))
                {
                    document.Editor.WriteMessage("\nA reviewer/approver name or reference is required for non-Draft status.");
                    return;
                }
            }

            if (status.StartsWith("Approved", StringComparison.OrdinalIgnoreCase))
            {
                document.Editor.WriteMessage("\nIMPORTANT: CE Tools records the entered status only. It cannot verify professional registration, delegated authority or engineering approval.");
                if (!Confirm(document.Editor, "Record this user-supplied approval status after external authority has been verified"))
                    return;
            }

            DetailParameters parameters = link.Parameters.Clone();
            parameters.ReviewStatus = status;
            parameters.Reviewer = reviewer;
            parameters.ReviewedAtUtc = status.Equals("Draft", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            Regenerate(document, anchorId, link.WithParameters(parameters), true, "CE_DETAILPARAMREVIEW");
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
                : !sourceExists ? "Missing"
                : currentHash.Equals(link.SourceHash, StringComparison.OrdinalIgnoreCase) ? "Live / hash matches" : "Live / hash changed";
            int liveGenerated = link.GeneratedHandles.Count(handle =>
            {
                ObjectId id;
                return TryResolveHandle(document.Database, handle, out id);
            });

            var rows = new List<IList<string>>
            {
                Row("Schema", link.Schema),
                Row("Detail ID", link.DetailId),
                Row("Detail type", DisplayType(link.Parameters.DetailType)),
                Row("Width", link.Parameters.WidthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Row("Depth", link.Parameters.DepthMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
                Row("Length / plan thickness", link.Parameters.LengthMetres.ToString("0.###", CultureInfo.InvariantCulture) + " m"),
                Row("Wall / slab thickness", link.Parameters.WallThicknessMillimetres.ToString("0.###", CultureInfo.InvariantCulture) + " mm"),
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
                Row("BOQ link record", BoqLinkRecordName),
                Row("Drawing units per metre", link.Settings.DrawingUnitsPerMetre.ToString("0.###", CultureInfo.InvariantCulture))
            };
            string note =
                "The external source template is never modified. Parameter edits and source-identity drift reset review status to Draft. " +
                "Generated geometry and schedules are linked, reviewable and reversible. Recorded approval is user supplied and does not verify authority.";
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

            var options = new PromptKeywordOptions("\nDetach generated detail [Keep/Delete] <Keep>: ") { AllowNone = true };
            options.Keywords.Add("Keep");
            options.Keywords.Add("Delete");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return;
            bool deleteGenerated = result.Status == PromptStatus.OK && result.StringResult.Equals("Delete", StringComparison.OrdinalIgnoreCase);

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
                        if (deleteGenerated) entity.Erase();
                        else
                        {
                            RemoveExtensionRecord(entity, transaction, GeneratedRecordName);
                            RemoveExtensionRecord(entity, transaction, BoqLinkRecordName);
                        }
                    }
                    Entity anchor = transaction.GetObject(anchorId, OpenMode.ForWrite, false) as Entity;
                    if (anchor != null)
                        anchor.Erase();
                    transaction.Commit();
                }
                document.Editor.WriteMessage(deleteGenerated
                    ? "\nCE_DETAILPARAMDETACH complete. Generated variant, schedules and anchor were deleted. The source template was unchanged."
                    : "\nCE_DETAILPARAMDETACH complete. The anchor was removed; generated objects were kept as ordinary drawing content.");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_DETAILPARAMDETACH failed. " + exception.Message);
            }
        }

        [CommandMethod("CE_DETAILPARAMCLEAR", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Clear()
        {
            Document document = ActiveDocument();
            if (document == null)
                return;

            var options = new PromptKeywordOptions("\nClear dynamic typical details [Selected/AllCurrentSpace] <Selected>: ") { AllowNone = true };
            options.Keywords.Add("Selected");
            options.Keywords.Add("AllCurrentSpace");
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
                return;

            List<ObjectId> anchors = result.Status == PromptStatus.OK && result.StringResult.Equals("AllCurrentSpace", StringComparison.OrdinalIgnoreCase)
                ? FindAnchorsInCurrentSpace(document.Database)
                : PromptAnchorSelection(document);
            if (anchors.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_DETAILPARAMCLEAR: no linked dynamic-detail anchors were found.");
                return;
            }

            int generatedCount = CountGenerated(document.Database, anchors);
            document.Editor.WriteMessage("\nCE_DETAILPARAMCLEAR preview: anchors={0}; linked generated objects={1}.", anchors.Count, generatedCount);
            if (!Confirm(document.Editor, "Delete these linked dynamic-detail anchors and their CE-generated geometry/schedules"))
                return;

            try
            {
                int deletedAnchors = 0;
                int deletedGenerated = 0;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId anchorId in anchors.Distinct())
                    {
                        Entity anchor = transaction.GetObject(anchorId, OpenMode.ForWrite, false) as Entity;
                        if (anchor == null || !HasExtensionRecord(anchor, transaction, LinkRecordName))
                            continue;
                        DynamicDetailLink link = ReadLink(anchor, transaction);
                        foreach (string handle in link.GeneratedHandles)
                        {
                            ObjectId id;
                            if (!TryResolveHandle(document.Database, handle, out id))
                                continue;
                            Entity generated = transaction.GetObject(id, OpenMode.ForWrite, false) as Entity;
                            if (generated != null && HasExtensionRecord(generated, transaction, GeneratedRecordName))
                            {
                                generated.Erase();
                                deletedGenerated++;
                            }
                        }
                        anchor.Erase();
                        deletedAnchors++;
                    }
                    transaction.Commit();
                }
                document.Editor.WriteMessage(
                    "\nCE_DETAILPARAMCLEAR complete. Anchors deleted={0}; generated objects deleted={1}; source templates modified=0.",
                    deletedAnchors,
                    deletedGenerated);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_DETAILPARAMCLEAR failed. No clear transaction was committed. " + exception.Message);
            }
        }
    }
}
