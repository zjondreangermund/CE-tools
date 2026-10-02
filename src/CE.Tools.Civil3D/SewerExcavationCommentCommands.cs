using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;

[assembly: CommandClass(typeof(CETools.Civil3D.SewerExcavationCommentCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Linked sewer excavation schedule. Pipe handles and engineering assumptions
    /// are stored on the table so trench excavation, bedding and backfill can be
    /// refreshed after pipe lengths or sizes change.
    /// </summary>
    public sealed class SewerExcavationCommentCommands
    {
        private const string LinkRecordName = "CE_SEWER_EXCAVATION_LINKS";
        private const string LinkSchema = "3";
        private const int ColumnCount = 21;

        [CommandMethod("CE_TOOLS", "CE_SEWEREXCAVATION", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Build()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect sewer pipes and structures for the linked excavation schedule: ");
            if (selection.Status != PromptStatus.OK) return;

            SewerExcavationSettings defaults =
                SewerExcavationPreferenceStore.LoadSettings();
            var settingsWindow =
                new SewerExcavationSettingsWindow(defaults);
            AcApplication.ShowModalWindow(settingsWindow);
            if (!settingsWindow.Accepted) return;
            SewerExcavationSettings settings = settingsWindow.Settings;
            SewerExcavationPreferenceStore.SaveSettings(settings);

            List<ObjectId> sourceIds = selection.Value.GetObjectIds().ToList();
            ExtractionResult extraction = Extract(document.Database, sourceIds, settings);
            if (extraction.Rows.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWEREXCAVATION stopped. No supported sewer pipes/structures were found. Rejected={0}.",
                    extraction.Rejections.Count);
                foreach (string reason in extraction.Rejections.Take(8))
                    document.Editor.WriteMessage("\n  REJECTED: {0}", reason);
                return;
            }

            ShowPreview(document, extraction, settings);
            if (!Confirm(document.Editor, "Create the linked sewer excavation table")) return;
            PromptPointResult insertion = document.Editor.GetPoint(
                "\nPick insertion point for the linked sewer excavation table: ");
            if (insertion.Status != PromptStatus.OK) return;

            Point3d position = insertion.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
            try
            {
                ObjectId tableId = CreateLinkedTable(
                    document.Database,
                    position,
                    extraction.Rows,
                    settings,
                    extraction.UsableHandles);
                document.Editor.WriteMessage(
                    "\nCE_SEWEREXCAVATION complete. Pipes={0}; structures={1}; rejected={2}; table={3}; primary excavation={4:N3} m³.",
                    extraction.Rows.Count(row => row.ObjectType == "Pipe"),
                    extraction.Rows.Count(row => row.ObjectType == "Structure"),
                    extraction.Rejections.Count,
                    tableId.Handle,
                    extraction.Rows.Sum(row => row.PrimaryExcavation));
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWEREXCAVATION failed. No linked table was committed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_SEWEREXCAVATIONREFRESH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Refresh()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult tableResult = PromptForLinkedTable(
                document.Editor,
                "\nSelect a linked CE sewer excavation table to refresh: ");
            if (tableResult.Status != PromptStatus.OK) return;
            RefreshTable(document, tableResult.ObjectId, true);
        }

        [CommandMethod("CE_TOOLS", "CE_SEWEREXCAVATIONINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Information()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult tableResult = PromptForLinkedTable(
                document.Editor,
                "\nSelect a linked CE sewer excavation table for information: ");
            if (tableResult.Status != PromptStatus.OK) return;

            try
            {
                SewerExcavationLink link;
                int displayedRows;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(tableResult.ObjectId, OpenMode.ForRead, false) as Table;
                    link = ReadLink(table, transaction);
                    displayedRows = table == null ? 0 : Math.Max(0, table.Rows.Count - 3);
                }

                int active = 0;
                int missing = 0;
                foreach (string handle in link.Handles)
                {
                    ObjectId id;
                    if (TryResolveHandle(document.Database, handle, out id)) active++;
                    else missing++;
                }

                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Sewer Excavation Link Information",
                    "Stored source handles and engineering assumptions used by CE_SEWEREXCAVATIONREFRESH.",
                    new List<string> { "Property", "Value" },
                    new List<IList<string>>
                    {
                        new List<string> { "Schema", link.Schema },
                        new List<string> { "Stored sewer source handles", link.Handles.Count.ToString(CultureInfo.InvariantCulture) },
                        new List<string> { "Resolvable sewer sources", active.ToString(CultureInfo.InvariantCulture) },
                        new List<string> { "Missing sewer sources", missing.ToString(CultureInfo.InvariantCulture) },
                        new List<string> { "Displayed sewer rows", displayedRows.ToString(CultureInfo.InvariantCulture) },
                        new List<string> { "Drawing units per metre", link.Settings.UnitsPerMetre.ToString("N6", CultureInfo.CurrentCulture) },
                        new List<string> { "Legacy side allowance each side", link.Settings.SideAllowance.ToString("N3", CultureInfo.CurrentCulture) + " m" },
                        new List<string> { "Minimum trench width", link.Settings.MinimumWidth.ToString("N3", CultureInfo.CurrentCulture) + " m" },
                        new List<string> { "Specified trench width", link.Settings.TrenchWidth.ToString("N3", CultureInfo.CurrentCulture) + " m" },
                        new List<string> { "Bedding thickness", link.Settings.BeddingThickness.ToString("N3", CultureInfo.CurrentCulture) + " m" },
                        new List<string> { "Blanket above pipe", link.Settings.BlanketAbovePipe.ToString("N3", CultureInfo.CurrentCulture) + " m" },
                        new List<string> { "Structure side allowance", link.Settings.StructureSideAllowance.ToString("N3", CultureInfo.CurrentCulture) + " m" },
                        new List<string> { "Primary excavation basis", link.Settings.ExcavationToBottomOnly ? "To bottom of pipe/structure" : "Including bedding" },
                        new List<string> { "Fallback average cover", link.Settings.FallbackCover.ToString("N3", CultureInfo.CurrentCulture) + " m" },
                        new List<string> { "Refresh command", "CE_SEWEREXCAVATIONREFRESH" }
                    },
                    "CE TOOLS SEWER EXCAVATION INFORMATION");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_SEWEREXCAVATIONINFO failed. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_SEWEREXCAVATIONEXPORT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void Export()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptEntityResult tableResult = PromptForLinkedTable(
                document.Editor,
                "\nSelect a linked CE sewer excavation table to export: ");
            if (tableResult.Status != PromptStatus.OK) return;

            if (Confirm(document.Editor, "Refresh sewer excavation quantities before export"))
            {
                if (!RefreshTable(document, tableResult.ObjectId, false)) return;
            }

            var options = new PromptSaveFileOptions("\nSelect sewer excavation Excel workbook output path: ")
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DialogCaption = "Export CE Tools Sewer Excavation Schedule",
                InitialFileName = "CE-Tools-Sewer-Excavation.xlsx"
            };
            PromptFileNameResult pathResult = document.Editor.GetFileNameForSave(options);
            if (pathResult.Status != PromptStatus.OK) return;
            string path = pathResult.StringResult;
            if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) path += ".xlsx";

            try
            {
                List<IList<string>> rows;
                SewerExcavationLink link;
                using (Transaction transaction =
                    document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(
                        tableResult.ObjectId,
                        OpenMode.ForRead,
                        false) as Table;
                    link = ReadLink(table, transaction);
                    rows = ReadTableCells(table);
                }

                var ids = new List<ObjectId>();
                foreach (string handle in link.Handles)
                {
                    ObjectId id;
                    if (TryResolveHandle(
                            document.Database,
                            handle,
                            out id))
                        ids.Add(id);
                }

                ExtractionResult extraction = Extract(
                    document.Database,
                    ids,
                    link.Settings);

                SimpleXlsxWriter.Write(
                    path,
                    new List<XlsxSheet>
                    {
                        new XlsxSheet(
                            "Sewer Excavation",
                            rows),
                        new XlsxSheet(
                            "Summary",
                            BuildExcavationSummaryRows(
                                extraction.Rows,
                                link.Settings))
                    });
                document.Editor.WriteMessage(
                    "\nCE_SEWEREXCAVATIONEXPORT complete. Workbook includes Sewer Excavation and Summary sheets: {0}",
                    path);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWEREXCAVATIONEXPORT failed. {0}",
                    exception.Message);
            }
        }

        internal static int RefreshAll(Document document)
        {
            if (document == null) return 0;
            List<ObjectId> tableIds = FindLinkedTables(document.Database);
            int refreshed = 0;
            foreach (ObjectId tableId in tableIds)
            {
                if (RefreshTable(document, tableId, false)) refreshed++;
            }
            return refreshed;
        }

        private static bool RefreshTable(Document document, ObjectId tableId, bool askConfirmation)
        {
            try
            {
                SewerExcavationLink link;
                Point3d position;
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(tableId, OpenMode.ForRead, false) as Table;
                    link = ReadLink(table, transaction);
                    position = table.Position;
                }

                var ids = new List<ObjectId>();
                int stale = 0;
                foreach (string handle in link.Handles)
                {
                    ObjectId id;
                    if (TryResolveHandle(document.Database, handle, out id)) ids.Add(id);
                    else stale++;
                }
                ExtractionResult extraction = Extract(document.Database, ids, link.Settings);
                if (extraction.Rows.Count == 0)
                {
                    document.Editor.WriteMessage(
                        "\nCE_SEWEREXCAVATIONREFRESH stopped. No live source pipe produces a usable quantity; the existing table remains unchanged.");
                    return false;
                }

                document.Editor.WriteMessage(
                    "\nCE_SEWEREXCAVATIONREFRESH preview. Pipes={0}; structures={1}; stale handles={2}; rejected={3}; primary excavation={4:N3} m³.",
                    extraction.Rows.Count(row => row.ObjectType == "Pipe"),
                    extraction.Rows.Count(row => row.ObjectType == "Structure"),
                    stale,
                    extraction.Rejections.Count,
                    extraction.Rows.Sum(row => row.PrimaryExcavation));
                if (askConfirmation && !Confirm(document.Editor, "Replace the displayed excavation quantities"))
                    return false;

                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    Table table = transaction.GetObject(tableId, OpenMode.ForWrite, false) as Table;
                    if (table == null) throw new InvalidOperationException("The selected object is not a table.");
                    table.Position = position;
                    PopulateTable(document.Database, table, extraction.Rows, link.Settings);
                    WriteLink(
                        table,
                        transaction,
                        new SewerExcavationLink(
                            LinkSchema,
                            link.Settings,
                            extraction.UsableHandles));
                    table.GenerateLayout();
                    transaction.Commit();
                }

                document.Editor.WriteMessage(
                    "\nCE_SEWEREXCAVATIONREFRESH complete. Pipes={0}; structures={1}; stale removed={2}.",
                    extraction.Rows.Count(row => row.ObjectType == "Pipe"),
                    extraction.Rows.Count(row => row.ObjectType == "Structure"),
                    stale);
                return true;
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_SEWEREXCAVATIONREFRESH failed. The table was not changed. {0}",
                    exception.Message);
                return false;
            }
        }

        private static ExtractionResult Extract(
            Database database,
            IEnumerable<ObjectId> objectIds,
            SewerExcavationSettings settings)
        {
            var result = new ExtractionResult();
            if (objectIds == null) return result;
            settings.Validate();

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId objectId in objectIds.Distinct())
                {
                    if (objectId.IsNull || objectId.IsErased)
                    {
                        result.Rejections.Add(
                            "Null or erased source object.");
                        continue;
                    }

                    DBObject value;
                    try
                    {
                        value = transaction.GetObject(
                            objectId,
                            OpenMode.ForRead,
                            false);
                    }
                    catch (System.Exception exception)
                    {
                        result.Rejections.Add(
                            objectId.Handle +
                            ": cannot open - " +
                            exception.Message);
                        continue;
                    }

                    Entity entity = value as Entity;
                    if (entity == null)
                    {
                        result.Rejections.Add(
                            objectId.Handle +
                            ": object is not a drawable sewer part.");
                        continue;
                    }

                    if (LooksLikePipe(value))
                    {
                        PipeExcavationRow pipeRow;
                        string reason;
                        if (!TryBuildPipeRow(
                                objectId,
                                value,
                                entity,
                                transaction,
                                settings,
                                out pipeRow,
                                out reason))
                        {
                            result.Rejections.Add(
                                objectId.Handle + ": " + reason);
                            continue;
                        }

                        result.Rows.Add(pipeRow);
                        result.UsableHandles.Add(
                            objectId.Handle.ToString());
                        continue;
                    }

                    if (LooksLikeStructure(value))
                    {
                        PipeExcavationRow structureRow;
                        string reason;
                        if (!TryBuildStructureRow(
                                objectId,
                                value,
                                entity,
                                transaction,
                                settings,
                                out structureRow,
                                out reason))
                        {
                            result.Rejections.Add(
                                objectId.Handle + ": " + reason);
                            continue;
                        }

                        result.Rows.Add(structureRow);
                        result.UsableHandles.Add(
                            objectId.Handle.ToString());
                        continue;
                    }

                    result.Rejections.Add(
                        objectId.Handle +
                        ": object is not a supported sewer pipe or structure.");
                }
            }

            return result;
        }

        private static bool TryBuildPipeRow(
            ObjectId objectId,
            DBObject value,
            Entity entity,
            Transaction transaction,
            SewerExcavationSettings settings,
            out PipeExcavationRow row,
            out string reason)
        {
            row = null;
            reason = string.Empty;

            double rawLength;
            if (!TryGetLength(value, out rawLength))
            {
                reason = "no usable pipe length was found.";
                return false;
            }

            double rawDiameter;
            if (!TryReadNumber(
                    value,
                    out rawDiameter,
                    "OuterDiameterOrWidth",
                    "InnerDiameterOrWidth",
                    "NominalDiameter",
                    "Diameter",
                    "OutsideDiameter"))
            {
                reason = "no usable pipe diameter was found.";
                return false;
            }

            Point3d startPoint;
            Point3d endPoint;
            if (!TryReadPoint(value, "StartPoint", out startPoint) ||
                !TryReadPoint(value, "EndPoint", out endPoint))
            {
                reason = "pipe endpoint elevations are unavailable.";
                return false;
            }

            double length = rawLength / settings.UnitsPerMetre;
            double diameter = rawDiameter / settings.UnitsPerMetre;
            if (!Positive(length) || !Positive(diameter))
            {
                reason = "converted pipe length or diameter is invalid.";
                return false;
            }

            double rawStartGround;
            double rawEndGround;
            bool exactSurface =
                TryReferenceSurfaceElevation(
                    value,
                    startPoint,
                    transaction,
                    out rawStartGround) &&
                TryReferenceSurfaceElevation(
                    value,
                    endPoint,
                    transaction,
                    out rawEndGround);

            double startCover;
            double endCover;
            string depthSource;
            if (exactSurface)
            {
                double radiusRaw = rawDiameter / 2.0;
                startCover = Math.Max(
                    0.0,
                    (rawStartGround -
                     (startPoint.Z + radiusRaw)) /
                    settings.UnitsPerMetre);
                endCover = Math.Max(
                    0.0,
                    (rawEndGround -
                     (endPoint.Z + radiusRaw)) /
                    settings.UnitsPerMetre);
                depthSource = "Reference surface";
            }
            else
            {
                double cover = ReadAverageCover(
                    value,
                    settings);
                startCover = cover;
                endCover = cover;
                depthSource = "Fallback cover";
            }

            double averageCover =
                (startCover + endCover) / 2.0;
            double width = settings.TrenchWidth > 0.0
                ? Math.Max(settings.TrenchWidth, diameter)
                : Math.Max(
                    settings.MinimumWidth,
                    diameter + (2.0 * settings.SideAllowance));

            // Exact long-section trench depths are measured from the reference
            // natural-ground surface to the bottom of the bedding layer.
            double startDepthToPipeBottom =
                startCover + diameter;
            double endDepthToPipeBottom =
                endCover + diameter;
            double startDepthToBeddingBottom =
                startDepthToPipeBottom +
                settings.BeddingThickness;
            double endDepthToBeddingBottom =
                endDepthToPipeBottom +
                settings.BeddingThickness;
            double averageDepthToBeddingBottom =
                (startDepthToBeddingBottom +
                 endDepthToBeddingBottom) / 2.0;

            double excavationToPipeBottom =
                length *
                width *
                ((startDepthToPipeBottom +
                  endDepthToPipeBottom) / 2.0);
            double bedding =
                length *
                width *
                settings.BeddingThickness;

            // Total trench excavation is always to the bottom of bedding.
            double totalExcavation =
                length *
                width *
                averageDepthToBeddingBottom;

            double pipeVolume =
                Math.PI *
                Math.Pow(diameter / 2.0, 2.0) *
                length;

            double blanketZone =
                length *
                width *
                (diameter +
                 settings.BlanketAbovePipe);
            double blanketFill =
                Math.Max(
                    0.0,
                    blanketZone - pipeVolume);

            double fillAboveBlanket =
                length *
                width *
                Math.Max(
                    0.0,
                    averageCover -
                    settings.BlanketAbovePipe);

            double excavatedMaterialNet =
                Math.Max(
                    0.0,
                    totalExcavation - pipeVolume);

            row = new PipeExcavationRow
            {
                Handle = objectId.Handle.ToString(),
                ObjectType = "Pipe",
                Name = ReadText(
                    value,
                    "Name",
                    value.GetType().Name),
                Layer = entity.Layer,
                Length = length,
                Diameter = diameter,
                StartCover = startCover,
                EndCover = endCover,
                AverageCover = averageCover,
                TrenchWidth = width,
                StartDepthToBeddingBottom =
                    startDepthToBeddingBottom,
                EndDepthToBeddingBottom =
                    endDepthToBeddingBottom,
                DepthToBottom =
                    averageDepthToBeddingBottom,
                ExcavationToBottom =
                    excavationToPipeBottom,
                ExcavationIncludingBedding =
                    totalExcavation,
                Bedding = bedding,
                PipeVolume = pipeVolume,
                BlanketFill = blanketFill,
                FillAboveBlanket = fillAboveBlanket,
                ExcavatedMaterialNet =
                    excavatedMaterialNet,
                PrimaryExcavation =
                    totalExcavation,
                DepthSource = depthSource
            };
            return true;
        }

        private static bool TryBuildStructureRow(
            ObjectId objectId,
            DBObject value,
            Entity entity,
            Transaction transaction,
            SewerExcavationSettings settings,
            out PipeExcavationRow row,
            out string reason)
        {
            row = null;
            reason = string.Empty;

            double rawSize;
            if (!TryReadNumber(
                    value,
                    out rawSize,
                    "OuterDiameterOrWidth",
                    "InnerDiameterOrWidth",
                    "Diameter",
                    "StructureDiameter",
                    "Width"))
            {
                reason =
                    "no usable structure diameter/width was found.";
                return false;
            }

            Point3d position;
            if (!TryReadPoint(value, "Position", out position))
            {
                reason = "structure plan position is unavailable.";
                return false;
            }

            double ground;
            bool exactGround = TryReferenceSurfaceElevation(
                value,
                position,
                transaction,
                out ground);

            double floor;
            if (!TryReadNumberAllowZero(
                    value,
                    out floor,
                    "SumpElevation"))
            {
                floor = position.Z;
            }

            // Include the lowest connected pipe inside invert. The structure
            // excavation reaches the lower of that invert or the structure floor.
            CivilStructure structure = value as CivilStructure;
            if (structure != null)
            {
                foreach (ObjectId pipeId in
                    SewerPipeConnections.PipeIds(structure))
                {
                    if (pipeId.IsNull ||
                        pipeId.IsErased)
                        continue;
                    CivilPipe pipe = null;
                    try
                    {
                        pipe = transaction.GetObject(
                            pipeId,
                            OpenMode.ForRead,
                            false) as CivilPipe;
                    }
                    catch { }
                    if (pipe == null) continue;

                    bool atStart =
                        pipe.StartStructureId == objectId;
                    try
                    {
                        double invert =
                            SewerPipeConnections.Invert(
                                pipe,
                                atStart);
                        if (!double.IsNaN(invert) &&
                            !double.IsInfinity(invert))
                            floor = Math.Min(floor, invert);
                    }
                    catch { }
                }
            }

            if (!exactGround)
            {
                if (!TryReadNumberAllowZero(
                        value,
                        out ground,
                        "RimElevation"))
                {
                    reason =
                        "reference-surface ground elevation is unavailable.";
                    return false;
                }
            }

            double structureSize =
                rawSize / settings.UnitsPerMetre;
            double depthToBottom =
                (ground - floor) /
                settings.UnitsPerMetre;
            if (!Positive(structureSize) ||
                !Positive(depthToBottom))
            {
                reason =
                    "natural-ground-to-structure-bottom depth is invalid.";
                return false;
            }

            double width =
                Math.Max(
                    structureSize,
                    structureSize +
                    (2.0 *
                     settings.StructureSideAllowance));
            double excavation =
                width *
                width *
                depthToBottom;

            row = new PipeExcavationRow
            {
                Handle = objectId.Handle.ToString(),
                ObjectType = "Structure",
                Name = ReadText(
                    value,
                    "Name",
                    value.GetType().Name),
                Layer = entity.Layer,
                Length = 0.0,
                Diameter = structureSize,
                StartCover = 0.0,
                EndCover = 0.0,
                AverageCover = 0.0,
                TrenchWidth = width,
                StartDepthToBeddingBottom =
                    depthToBottom,
                EndDepthToBeddingBottom =
                    depthToBottom,
                DepthToBottom = depthToBottom,
                ExcavationToBottom = excavation,
                ExcavationIncludingBedding =
                    excavation,
                Bedding = 0.0,
                PipeVolume = 0.0,
                BlanketFill = 0.0,
                FillAboveBlanket = 0.0,
                ExcavatedMaterialNet = excavation,
                PrimaryExcavation = excavation,
                DepthSource = exactGround
                    ? "Reference surface"
                    : "Rim fallback"
            };
            return true;
        }

        private static double ReadAverageCover(
            object value,
            SewerExcavationSettings settings)
        {
            double raw;
            if (TryReadNumber(
                    value,
                    out raw,
                    "AverageCover",
                    "Cover"))
                return Math.Max(
                    0.0,
                    raw / settings.UnitsPerMetre);
            double start;
            double end;
            bool hasStart = TryReadNumber(
                value,
                out start,
                "StartCover",
                "CoverAtStart");
            bool hasEnd = TryReadNumber(
                value,
                out end,
                "EndCover",
                "CoverAtEnd");
            if (hasStart && hasEnd)
                return Math.Max(
                    0.0,
                    ((start + end) / 2.0) /
                    settings.UnitsPerMetre);
            if (hasStart)
                return Math.Max(
                    0.0,
                    start / settings.UnitsPerMetre);
            if (hasEnd)
                return Math.Max(
                    0.0,
                    end / settings.UnitsPerMetre);
            return settings.FallbackCover;
        }

        private static bool TryReferenceSurfaceElevation(
            object part,
            Point3d point,
            Transaction transaction,
            out double elevation)
        {
            elevation = 0.0;
            if (part == null || transaction == null)
                return false;

            ObjectId surfaceId;
            if (!TryReadObjectId(
                    part,
                    "RefSurfaceId",
                    out surfaceId) ||
                surfaceId.IsNull ||
                surfaceId.IsErased)
            {
                // Pipe parts can inherit the surface through a connected
                // structure when RefSurfaceId is not populated on the pipe.
                foreach (string endpointName in new[]
                {
                    "StartStructureId",
                    "EndStructureId"
                })
                {
                    ObjectId structureId;
                    if (!TryReadObjectId(
                            part,
                            endpointName,
                            out structureId) ||
                        structureId.IsNull ||
                        structureId.IsErased)
                        continue;
                    try
                    {
                        DBObject structure =
                            transaction.GetObject(
                                structureId,
                                OpenMode.ForRead,
                                false);
                        if (TryReadObjectId(
                                structure,
                                "RefSurfaceId",
                                out surfaceId) &&
                            !surfaceId.IsNull &&
                            !surfaceId.IsErased)
                            break;
                    }
                    catch { }
                }
            }

            if (surfaceId.IsNull ||
                surfaceId.IsErased)
                return false;

            try
            {
                DBObject surface = transaction.GetObject(
                    surfaceId,
                    OpenMode.ForRead,
                    false);
                MethodInfo method =
                    surface.GetType().GetMethod(
                        "FindElevationAtXY",
                        BindingFlags.Public |
                        BindingFlags.Instance,
                        null,
                        new[]
                        {
                            typeof(double),
                            typeof(double)
                        },
                        null);
                if (method == null) return false;
                object raw = method.Invoke(
                    surface,
                    new object[]
                    {
                        point.X,
                        point.Y
                    });
                elevation = Convert.ToDouble(
                    raw,
                    CultureInfo.InvariantCulture);
                return !double.IsNaN(elevation) &&
                       !double.IsInfinity(elevation);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadObjectId(
            object value,
            string propertyName,
            out ObjectId id)
        {
            id = ObjectId.Null;
            if (value == null) return false;
            try
            {
                PropertyInfo property =
                    value.GetType().GetProperty(
                        propertyName,
                        BindingFlags.Public |
                        BindingFlags.Instance);
                if (property == null ||
                    !property.CanRead ||
                    property.PropertyType !=
                        typeof(ObjectId))
                    return false;
                id = (ObjectId)property.GetValue(
                    value,
                    null);
                return !id.IsNull;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetLength(object value, out double length)
        {
            length = 0.0;
            // Civil 3D network objects can expose a stale 1.000 length. Prefer
            // actual endpoint geometry first so excavation and BOQs use the
            // physical pipe distance.
            string[,] pairs =
            {
                { "StartPoint", "EndPoint" },
                { "StartPointLocation", "EndPointLocation" },
                { "StartLocation", "EndLocation" }
            };
            for (int index = 0; index < pairs.GetLength(0); index++)
            {
                Point3d start;
                Point3d end;
                if (TryReadPoint(value, pairs[index, 0], out start) &&
                    TryReadPoint(value, pairs[index, 1], out end))
                {
                    double endpointLength = start.DistanceTo(end);
                    if (Positive(endpointLength) && Math.Abs(endpointLength - 1.0) > 0.0001)
                    {
                        length = endpointLength;
                        return true;
                    }
                }
            }
            Curve curve = value as Curve;
            if (curve != null)
            {
                try
                {
                    length = Math.Abs(
                        curve.GetDistanceAtParameter(curve.EndParam) -
                        curve.GetDistanceAtParameter(curve.StartParam));
                    if (Positive(length) && Math.Abs(length - 1.0) > 0.0001) return true;
                }
                catch { }
            }
            if (TryReadNumber(
                value,
                out length,
                "Length3DCenterToCenter",
                "Length2DCenterToCenter",
                "Length3D",
                "Length2D",
                "Length"))
            {
                if (Positive(length) && Math.Abs(length - 1.0) > 0.0001) return true;
            }
            // Final fallback: accept any positive endpoint length rather than
            // silently forcing every network part to exactly 1 m.
            for (int index = 0; index < pairs.GetLength(0); index++)
            {
                Point3d start;
                Point3d end;
                if (TryReadPoint(value, pairs[index, 0], out start) &&
                    TryReadPoint(value, pairs[index, 1], out end))
                {
                    length = start.DistanceTo(end);
                    if (Positive(length)) return true;
                }
            }
            return Positive(length);
        }

        private static bool LooksLikePipe(object value)
        {
            if (value == null) return false;
            string name = value.GetType().Name.ToUpperInvariant();
            return name.Contains("PIPE") &&
                   !name.Contains("NETWORK") &&
                   !name.Contains("STYLE") &&
                   !name.Contains("LABEL");
        }

        private static bool LooksLikeStructure(object value)
        {
            if (value == null) return false;
            string name = value.GetType().Name.ToUpperInvariant();
            return name.Contains("STRUCTURE") &&
                   !name.Contains("STYLE") &&
                   !name.Contains("LABEL");
        }

        private static ObjectId CreateLinkedTable(
            Database database,
            Point3d position,
            IList<PipeExcavationRow> rows,
            SewerExcavationSettings settings,
            IList<string> handles)
        {
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord currentSpace = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (currentSpace == null) throw new InvalidOperationException("Current drawing space could not be opened.");
                var table = new Table();
                table.SetDatabaseDefaults(database);
                table.TableStyle = database.Tablestyle;
                table.Position = position;
                PopulateTable(database, table, rows, settings);
                table.GenerateLayout();
                table.RecordGraphicsModified(true);
                try { table.RecomputeTableBlock(true); } catch { }
                ObjectId tableId = currentSpace.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                table.CreateExtensionDictionary();
                WriteLink(table, transaction, new SewerExcavationLink(LinkSchema, settings, handles));
                table.GenerateLayout();
                transaction.Commit();
                return tableId;
            }
        }

        private static int NominalDiameterMm(double diameterMetres)
        {
            double millimetres = diameterMetres > 10.0 ? diameterMetres : diameterMetres * 1000.0;
            int[] nominal = { 50, 63, 75, 90, 100, 110, 125, 140, 160, 180, 200, 225, 250, 280, 300, 315, 355, 400, 450, 500, 560, 600, 630, 710, 800, 900, 1000, 1200, 1500 };
            return nominal.OrderBy(value => Math.Abs(value - millimetres)).First();
        }

        private static void PopulateTable(
            Database database,
            Table table,
            IList<PipeExcavationRow> rows,
            SewerExcavationSettings settings)
        {
            table.SetSize(rows.Count + 3, ColumnCount);
            double height = ResolveTextHeight(database);
            table.SetRowHeight(height * 1.8);

            double[] widths =
            {
                height * 6.0, height * 8.0, height * 8.0, height * 5.5,
                height * 5.5, height * 5.0, height * 5.0, height * 6.0,
                height * 7.0, height * 7.0, height * 6.0, height * 6.0,
                height * 7.0, height * 7.0, height * 7.0, height * 7.0
            };
            for (int column = 0; column < ColumnCount; column++)
                table.Columns[column].Width = widths[column];

            table.MergeCells(
                CellRange.Create(
                    table,
                    0,
                    0,
                    0,
                    ColumnCount - 1));
            table.Cells[0, 0].TextString = string.Format(
                CultureInfo.CurrentCulture,
                "CE TOOLS LINKED SEWER EXCAVATION - TRENCH {0:N3} m - BEDDING {1:N3} m - BLANKET {2:N3} m",
                settings.TrenchWidth,
                settings.BeddingThickness,
                settings.BlanketAbovePipe);
            table.Cells[0, 0].Alignment =
                CellAlignment.MiddleCenter;
            table.Cells[0, 0].TextHeight =
                height * 1.15;

            string[] headings =
            {
                "TYPE", "NAME", "LAYER", "LENGTH m", "NOMINAL Ø mm",
                "COVER m", "WIDTH m", "DEPTH TO BOTTOM m",
                "EXC TO BOTTOM m³", "EXC incl BEDDING m³",
                "BEDDING m³", "PIPE VOL m³", "BLANKET FILL m³",
                "FILL ABOVE BLANKET m³", "NET EXC MATERIAL m³",
                "PRIMARY EXC m³"
            };

            for (int column = 0; column < headings.Length; column++)
            {
                table.Cells[1, column].TextString = headings[column];
                table.Cells[1, column].Alignment =
                    CellAlignment.MiddleCenter;
                table.Cells[1, column].TextHeight = height;
            }

            for (int index = 0; index < rows.Count; index++)
            {
                PipeExcavationRow row = rows[index];
                int tableRow = index + 2;
                bool pipe = string.Equals(
                    row.ObjectType,
                    "Pipe",
                    StringComparison.OrdinalIgnoreCase);

                string[] values =
                {
                    row.ObjectType,
                    row.Name,
                    row.Layer,
                    pipe
                        ? row.Length.ToString("N3", CultureInfo.CurrentCulture)
                        : string.Empty,
                    NominalDiameterMm(row.Diameter).ToString(
                        CultureInfo.CurrentCulture),
                    pipe
                        ? row.AverageCover.ToString("N3", CultureInfo.CurrentCulture)
                        : string.Empty,
                    row.TrenchWidth.ToString("N3", CultureInfo.CurrentCulture),
                    row.DepthToBottom.ToString("N3", CultureInfo.CurrentCulture),
                    row.ExcavationToBottom.ToString("N3", CultureInfo.CurrentCulture),
                    row.ExcavationIncludingBedding.ToString("N3", CultureInfo.CurrentCulture),
                    row.Bedding.ToString("N3", CultureInfo.CurrentCulture),
                    row.PipeVolume.ToString("N3", CultureInfo.CurrentCulture),
                    row.BlanketFill.ToString("N3", CultureInfo.CurrentCulture),
                    row.FillAboveBlanket.ToString("N3", CultureInfo.CurrentCulture),
                    row.ExcavatedMaterialNet.ToString("N3", CultureInfo.CurrentCulture),
                    row.PrimaryExcavation.ToString("N3", CultureInfo.CurrentCulture)
                };

                for (int column = 0; column < ColumnCount; column++)
                {
                    table.Cells[tableRow, column].TextString =
                        values[column];
                    table.Cells[tableRow, column].TextHeight =
                        height;
                    table.Cells[tableRow, column].Alignment =
                        CellAlignment.MiddleCenter;
                }
            }

            int totalRow = rows.Count + 2;
            table.Cells[totalRow, 0].TextString = "TOTAL";
            table.Cells[totalRow, 1].TextString = string.Format(
                CultureInfo.CurrentCulture,
                "Pipes {0}; Structures {1}",
                rows.Count(row => row.ObjectType == "Pipe"),
                rows.Count(row => row.ObjectType == "Structure"));
            table.Cells[totalRow, 3].TextString =
                rows.Where(row => row.ObjectType == "Pipe")
                    .Sum(row => row.Length)
                    .ToString("N3", CultureInfo.CurrentCulture);
            table.Cells[totalRow, 8].TextString =
                rows.Sum(row => row.ExcavationToBottom)
                    .ToString("N3", CultureInfo.CurrentCulture);
            table.Cells[totalRow, 9].TextString =
                rows.Sum(row => row.ExcavationIncludingBedding)
                    .ToString("N3", CultureInfo.CurrentCulture);
            table.Cells[totalRow, 10].TextString =
                rows.Sum(row => row.Bedding)
                    .ToString("N3", CultureInfo.CurrentCulture);
            table.Cells[totalRow, 11].TextString =
                rows.Sum(row => row.PipeVolume)
                    .ToString("N3", CultureInfo.CurrentCulture);
            table.Cells[totalRow, 12].TextString =
                rows.Sum(row => row.BlanketFill)
                    .ToString("N3", CultureInfo.CurrentCulture);
            table.Cells[totalRow, 13].TextString =
                rows.Sum(row => row.FillAboveBlanket)
                    .ToString("N3", CultureInfo.CurrentCulture);
            table.Cells[totalRow, 14].TextString =
                rows.Sum(row => row.ExcavatedMaterialNet)
                    .ToString("N3", CultureInfo.CurrentCulture);
            table.Cells[totalRow, 15].TextString =
                rows.Sum(row => row.PrimaryExcavation)
                    .ToString("N3", CultureInfo.CurrentCulture);

            for (int column = 0; column < ColumnCount; column++)
            {
                table.Cells[totalRow, column].TextHeight = height;
                table.Cells[totalRow, column].Alignment =
                    CellAlignment.MiddleCenter;
            }
        }

        private static void WriteLink(
            Table table,
            Transaction transaction,
            SewerExcavationLink link)
        {
            if (table.ExtensionDictionary.IsNull) table.CreateExtensionDictionary();
            DBDictionary dictionary = transaction.GetObject(
                table.ExtensionDictionary,
                OpenMode.ForWrite,
                false) as DBDictionary;
            if (dictionary == null) throw new InvalidOperationException("Excavation extension dictionary could not be opened.");
            Xrecord record;
            if (dictionary.Contains(LinkRecordName))
            {
                record = transaction.GetObject(dictionary.GetAt(LinkRecordName), OpenMode.ForWrite, false) as Xrecord;
            }
            else
            {
                record = new Xrecord();
                dictionary.SetAt(LinkRecordName, record);
                transaction.AddNewlyCreatedDBObject(record, true);
            }
            var values = new List<TypedValue>
            {
                TextValue("Schema", link.Schema),
                NumberValue("UnitsPerMetre", link.Settings.UnitsPerMetre),
                NumberValue("SideAllowance", link.Settings.SideAllowance),
                NumberValue("MinimumWidth", link.Settings.MinimumWidth),
                NumberValue("TrenchWidth", link.Settings.TrenchWidth),
                NumberValue("BeddingThickness", link.Settings.BeddingThickness),
                NumberValue("BlanketAbovePipe", link.Settings.BlanketAbovePipe),
                NumberValue("StructureSideAllowance", link.Settings.StructureSideAllowance),
                NumberValue("FallbackCover", link.Settings.FallbackCover),
                NumberValue(
                    "ExcavationToBottomOnly",
                    link.Settings.ExcavationToBottomOnly ? 1.0 : 0.0)
            };
            foreach (string handle in link.Handles.Distinct(StringComparer.OrdinalIgnoreCase))
                values.Add(TextValue("Handle", handle));
            record.Data = new ResultBuffer(values.ToArray());
        }

        private static SewerExcavationLink ReadLink(Table table, Transaction transaction)
        {
            if (table == null) throw new InvalidOperationException("The selected object is not a table.");
            if (table.ExtensionDictionary.IsNull) throw new InvalidOperationException("The table has no CE sewer excavation link.");
            DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
            if (dictionary == null || !dictionary.Contains(LinkRecordName))
                throw new InvalidOperationException("The table is not a linked CE sewer excavation schedule.");
            Xrecord record = transaction.GetObject(dictionary.GetAt(LinkRecordName), OpenMode.ForRead, false) as Xrecord;
            if (record == null || record.Data == null) throw new InvalidOperationException("The sewer excavation link is empty.");

            var settings = new SewerExcavationSettings();
            string schema = LinkSchema;
            var handles = new List<string>();
            foreach (TypedValue typedValue in record.Data)
            {
                string text = typedValue.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                int equals = text.IndexOf('=');
                if (equals <= 0) continue;
                string key = text.Substring(0, equals);
                string value = text.Substring(equals + 1);
                double number;
                if (key.Equals("Schema", StringComparison.OrdinalIgnoreCase)) schema = value;
                else if (key.Equals("Handle", StringComparison.OrdinalIgnoreCase)) handles.Add(value);
                else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    if (key.Equals("UnitsPerMetre", StringComparison.OrdinalIgnoreCase)) settings.UnitsPerMetre = number;
                    else if (key.Equals("SideAllowance", StringComparison.OrdinalIgnoreCase)) settings.SideAllowance = number;
                    else if (key.Equals("MinimumWidth", StringComparison.OrdinalIgnoreCase)) settings.MinimumWidth = number;
                    else if (key.Equals("TrenchWidth", StringComparison.OrdinalIgnoreCase)) settings.TrenchWidth = number;
                    else if (key.Equals("BeddingThickness", StringComparison.OrdinalIgnoreCase)) settings.BeddingThickness = number;
                    else if (key.Equals("BlanketAbovePipe", StringComparison.OrdinalIgnoreCase)) settings.BlanketAbovePipe = number;
                    else if (key.Equals("StructureSideAllowance", StringComparison.OrdinalIgnoreCase)) settings.StructureSideAllowance = number;
                    else if (key.Equals("FallbackCover", StringComparison.OrdinalIgnoreCase)) settings.FallbackCover = number;
                    else if (key.Equals("ExcavationToBottomOnly", StringComparison.OrdinalIgnoreCase)) settings.ExcavationToBottomOnly = number >= 0.5;
                }
            }
            if (handles.Count == 0) throw new InvalidOperationException("The sewer excavation link has no sewer source handles.");
            settings.Validate();
            return new SewerExcavationLink(schema, settings, handles);
        }

        private static TypedValue TextValue(string key, string value)
        {
            return new TypedValue((int)DxfCode.Text, key + "=" + (value ?? string.Empty));
        }

        private static TypedValue NumberValue(string key, double value)
        {
            return TextValue(key, value.ToString("R", CultureInfo.InvariantCulture));
        }

        private static List<ObjectId> FindLinkedTables(Database database)
        {
            var result = new List<ObjectId>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = transaction.GetObject(database.BlockTableId, OpenMode.ForRead, false) as BlockTable;
                if (blockTable == null) return result;
                foreach (ObjectId blockId in blockTable)
                {
                    BlockTableRecord block = transaction.GetObject(blockId, OpenMode.ForRead, false) as BlockTableRecord;
                    if (block == null || block.IsFromExternalReference) continue;
                    foreach (ObjectId entityId in block)
                    {
                        Table table = transaction.GetObject(entityId, OpenMode.ForRead, false) as Table;
                        if (table == null || table.ExtensionDictionary.IsNull) continue;
                        DBDictionary dictionary = transaction.GetObject(table.ExtensionDictionary, OpenMode.ForRead, false) as DBDictionary;
                        if (dictionary != null && dictionary.Contains(LinkRecordName)) result.Add(entityId);
                    }
                }
            }
            return result;
        }

        private static void ShowPreview(
            Document document,
            ExtractionResult extraction,
            SewerExcavationSettings settings)
        {
            var rows = new List<IList<string>>();
            foreach (PipeExcavationRow row in extraction.Rows)
            {
                bool pipe = string.Equals(
                    row.ObjectType,
                    "Pipe",
                    StringComparison.OrdinalIgnoreCase);
                rows.Add(new List<string>
                {
                    row.ObjectType,
                    row.Name,
                    row.Layer,
                    pipe
                        ? row.Length.ToString("N3", CultureInfo.CurrentCulture)
                        : string.Empty,
                    NominalDiameterMm(row.Diameter).ToString(
                        CultureInfo.CurrentCulture),
                    pipe
                        ? row.AverageCover.ToString("N3", CultureInfo.CurrentCulture)
                        : string.Empty,
                    row.TrenchWidth.ToString("N3", CultureInfo.CurrentCulture),
                    row.DepthToBottom.ToString("N3", CultureInfo.CurrentCulture),
                    row.ExcavationToBottom.ToString("N3", CultureInfo.CurrentCulture),
                    row.ExcavationIncludingBedding.ToString("N3", CultureInfo.CurrentCulture),
                    row.Bedding.ToString("N3", CultureInfo.CurrentCulture),
                    row.PipeVolume.ToString("N3", CultureInfo.CurrentCulture),
                    row.BlanketFill.ToString("N3", CultureInfo.CurrentCulture),
                    row.FillAboveBlanket.ToString("N3", CultureInfo.CurrentCulture),
                    row.ExcavatedMaterialNet.ToString("N3", CultureInfo.CurrentCulture),
                    row.PrimaryExcavation.ToString("N3", CultureInfo.CurrentCulture)
                });
            }

            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Sewer Excavation Preview",
                string.Format(
                    CultureInfo.CurrentCulture,
                    "Pipes={0}; structures={1}; rejected={2}; trench width={3:N3} m; bedding={4:N3} m; blanket above crown={5:N3} m; primary basis={6}. Pipe volume is deducted from excavated material.",
                    extraction.Rows.Count(row => row.ObjectType == "Pipe"),
                    extraction.Rows.Count(row => row.ObjectType == "Structure"),
                    extraction.Rejections.Count,
                    settings.TrenchWidth,
                    settings.BeddingThickness,
                    settings.BlanketAbovePipe,
                    settings.ExcavationToBottomOnly
                        ? "to bottom of pipe/structure"
                        : "including bedding"),
                new List<string>
                {
                    "Type", "Name", "Layer", "Length m", "NOMINAL Ø mm",
                    "Cover m", "Width m", "Depth to Bottom m",
                    "Exc to Bottom m³", "Exc incl Bedding m³",
                    "Bedding m³", "Pipe Vol m³", "Blanket Fill m³",
                    "Fill above Blanket m³", "Net Excavated Material m³",
                    "Primary Excavation m³"
                },
                rows,
                "CE TOOLS SEWER EXCAVATION PREVIEW");
        }

        private static List<IList<string>> BuildExcavationSummaryRows(
            IList<PipeExcavationRow> rows,
            SewerExcavationSettings settings)
        {
            rows = rows ?? new List<PipeExcavationRow>();
            int[] sizes = { 110, 160, 200, 250 };
            var counts = sizes.ToDictionary(size => size, size => 0);
            var lengths = sizes.ToDictionary(size => size, size => 0.0);
            var excBottom = sizes.ToDictionary(size => size, size => 0.0);
            var bedding = sizes.ToDictionary(size => size, size => 0.0);
            var blanket = sizes.ToDictionary(size => size, size => 0.0);
            var fillAbove = sizes.ToDictionary(size => size, size => 0.0);
            var pipeVolume = sizes.ToDictionary(size => size, size => 0.0);

            List<PipeExcavationRow> pipes = rows
                .Where(row => row.ObjectType == "Pipe")
                .ToList();
            List<PipeExcavationRow> structures = rows
                .Where(row => row.ObjectType == "Structure")
                .ToList();

            foreach (PipeExcavationRow row in pipes)
            {
                int size = NominalDiameterMm(row.Diameter);
                if (!counts.ContainsKey(size)) continue;
                counts[size]++;
                lengths[size] += row.Length;
                excBottom[size] += row.ExcavationToBottom;
                bedding[size] += row.Bedding;
                blanket[size] += row.BlanketFill;
                fillAbove[size] += row.FillAboveBlanket;
                pipeVolume[size] += row.PipeVolume;
            }

            Func<IDictionary<int, double>, int, string> number =
                delegate(IDictionary<int, double> values, int size)
                {
                    return values[size].ToString(
                        "0.###",
                        CultureInfo.InvariantCulture);
                };

            return new List<IList<string>>
            {
                new List<string>
                {
                    "CE TOOLS SEWER EXCAVATION SUMMARY",
                    string.Empty, string.Empty, string.Empty,
                    string.Empty, string.Empty
                },
                new List<string>
                {
                    "METRIC", "110 mm", "160 mm", "200 mm",
                    "250 mm", "ALL"
                },
                new List<string>
                {
                    "Number of pipes",
                    counts[110].ToString(CultureInfo.InvariantCulture),
                    counts[160].ToString(CultureInfo.InvariantCulture),
                    counts[200].ToString(CultureInfo.InvariantCulture),
                    counts[250].ToString(CultureInfo.InvariantCulture),
                    pipes.Count.ToString(CultureInfo.InvariantCulture)
                },
                new List<string>
                {
                    "Total pipe length (m)",
                    number(lengths, 110),
                    number(lengths, 160),
                    number(lengths, 200),
                    number(lengths, 250),
                    pipes.Sum(row => row.Length).ToString("0.###", CultureInfo.InvariantCulture)
                },
                new List<string>
                {
                    "Excavation to pipe bottom (m³)",
                    number(excBottom, 110),
                    number(excBottom, 160),
                    number(excBottom, 200),
                    number(excBottom, 250),
                    pipes.Sum(row => row.ExcavationToBottom).ToString("0.###", CultureInfo.InvariantCulture)
                },
                new List<string>
                {
                    "Bedding (m³)",
                    number(bedding, 110),
                    number(bedding, 160),
                    number(bedding, 200),
                    number(bedding, 250),
                    pipes.Sum(row => row.Bedding).ToString("0.###", CultureInfo.InvariantCulture)
                },
                new List<string>
                {
                    "Pipe volume deducted (m³)",
                    number(pipeVolume, 110),
                    number(pipeVolume, 160),
                    number(pipeVolume, 200),
                    number(pipeVolume, 250),
                    pipes.Sum(row => row.PipeVolume).ToString("0.###", CultureInfo.InvariantCulture)
                },
                new List<string>
                {
                    "Blanket fill (m³)",
                    number(blanket, 110),
                    number(blanket, 160),
                    number(blanket, 200),
                    number(blanket, 250),
                    pipes.Sum(row => row.BlanketFill).ToString("0.###", CultureInfo.InvariantCulture)
                },
                new List<string>
                {
                    "Fill above blanket to NG (m³)",
                    number(fillAbove, 110),
                    number(fillAbove, 160),
                    number(fillAbove, 200),
                    number(fillAbove, 250),
                    pipes.Sum(row => row.FillAboveBlanket).ToString("0.###", CultureInfo.InvariantCulture)
                },
                new List<string>
                {
                    "Total structures",
                    string.Empty, string.Empty, string.Empty,
                    string.Empty,
                    structures.Count.ToString(CultureInfo.InvariantCulture)
                },
                new List<string>
                {
                    "Structure excavation to bottom (m³)",
                    string.Empty, string.Empty, string.Empty,
                    string.Empty,
                    structures.Sum(row => row.ExcavationToBottom).ToString("0.###", CultureInfo.InvariantCulture)
                },
                new List<string>
                {
                    "Net excavated material after pipe volume (m³)",
                    string.Empty, string.Empty, string.Empty,
                    string.Empty,
                    rows.Sum(row => row.ExcavatedMaterialNet).ToString("0.###", CultureInfo.InvariantCulture)
                },
                new List<string>
                {
                    "Primary excavation total (m³)",
                    string.Empty, string.Empty, string.Empty,
                    string.Empty,
                    rows.Sum(row => row.PrimaryExcavation).ToString("0.###", CultureInfo.InvariantCulture)
                },
                new List<string>
                {
                    "Specified trench width (m)",
                    string.Empty, string.Empty, string.Empty,
                    string.Empty,
                    settings.TrenchWidth.ToString("0.###", CultureInfo.InvariantCulture)
                },
                new List<string>
                {
                    "Bedding depth (m)",
                    string.Empty, string.Empty, string.Empty,
                    string.Empty,
                    settings.BeddingThickness.ToString("0.###", CultureInfo.InvariantCulture)
                },
                new List<string>
                {
                    "Blanket above pipe crown (m)",
                    string.Empty, string.Empty, string.Empty,
                    string.Empty,
                    settings.BlanketAbovePipe.ToString("0.###", CultureInfo.InvariantCulture)
                }
            };
        }

        private static List<IList<string>> ReadTableCells(Table table)
        {
            var result = new List<IList<string>>();
            if (table == null) return result;
            for (int row = 0; row < table.Rows.Count; row++)
            {
                var values = new List<string>();
                for (int column = 0; column < table.Columns.Count; column++)
                {
                    try { values.Add(table.Cells[row, column].TextString ?? string.Empty); }
                    catch { values.Add(string.Empty); }
                }
                result.Add(values);
            }
            return result;
        }

        private static bool TryResolveHandle(Database database, string text, out ObjectId objectId)
        {
            objectId = ObjectId.Null;
            long value;
            if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) return false;
            try
            {
                objectId = database.GetObjectId(false, new Handle(value), 0);
                return !objectId.IsNull && !objectId.IsErased;
            }
            catch { return false; }
        }

        private static bool TryReadNumber(object value, out double number, params string[] propertyNames)
        {
            number = 0.0;
            if (value == null) return false;
            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    if (property == null || property.GetIndexParameters().Length != 0) continue;
                    object raw = property.GetValue(value, null);
                    if (raw == null) continue;
                    number = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    if (Positive(number)) return true;
                }
                catch { }
            }
            return false;
        }

        private static bool TryReadNumberAllowZero(
            object value,
            out double number,
            params string[] propertyNames)
        {
            number = 0.0;
            if (value == null) return false;
            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(
                        propertyName,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property == null ||
                        property.GetIndexParameters().Length != 0)
                        continue;
                    object raw = property.GetValue(value, null);
                    if (raw == null) continue;
                    number = Convert.ToDouble(
                        raw,
                        CultureInfo.InvariantCulture);
                    if (!double.IsNaN(number) &&
                        !double.IsInfinity(number))
                        return true;
                }
                catch { }
            }
            return false;
        }

        private static bool TryReadPoint(object value, string propertyName, out Point3d point)
        {
            point = Point3d.Origin;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                object raw = property == null ? null : property.GetValue(value, null);
                if (!(raw is Point3d)) return false;
                point = (Point3d)raw;
                return true;
            }
            catch { return false; }
        }

        private static string ReadText(object value, string propertyName, string fallback)
        {
            try
            {
                PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                object raw = property == null ? null : property.GetValue(value, null);
                string text = Convert.ToString(raw, CultureInfo.CurrentCulture);
                return string.IsNullOrWhiteSpace(text) ? fallback : text;
            }
            catch { return fallback; }
        }

        private static double ResolveTextHeight(Database database)
        {
            double height = database == null ? 2.0 : database.Textsize;
            if (Math.Abs(height - 1.8) < 0.05) return 1.8;
            if (Math.Abs(height - 5.0) < 0.05) return 5.0;
            return 2.0;
        }

        private static bool Positive(double value)
        {
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static PromptSelectionResult GetSelection(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }
            return editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
        }

        private static PromptEntityResult PromptForLinkedTable(Editor editor, string message)
        {
            var options = new PromptEntityOptions(message);
            options.SetRejectMessage("\nSelect an AutoCAD table.");
            options.AddAllowedClass(typeof(Table), false);
            return editor.GetEntity(options);
        }

        private static bool Confirm(Editor editor, string message)
        {
            var options = new PromptKeywordOptions("\n" + message + "? [Yes/No] <No>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK &&
                   string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class PipeExcavationRow
        {
            public string Handle { get; set; }
            public string ObjectType { get; set; }
            public string Name { get; set; }
            public string Layer { get; set; }
            public double Length { get; set; }
            public double Diameter { get; set; }
            public double StartCover { get; set; }
            public double EndCover { get; set; }
            public double AverageCover { get; set; }
            public double TrenchWidth { get; set; }
            public double StartDepthToBeddingBottom { get; set; }
            public double EndDepthToBeddingBottom { get; set; }
            public double DepthToBottom { get; set; }
            public string DepthSource { get; set; }
            public double ExcavationToBottom { get; set; }
            public double ExcavationIncludingBedding { get; set; }
            public double Bedding { get; set; }
            public double PipeVolume { get; set; }
            public double BlanketFill { get; set; }
            public double FillAboveBlanket { get; set; }
            public double ExcavatedMaterialNet { get; set; }
            public double PrimaryExcavation { get; set; }
        }

        private sealed class ExtractionResult
        {
            public ExtractionResult()
            {
                Rows = new List<PipeExcavationRow>();
                UsableHandles = new List<string>();
                Rejections = new List<string>();
            }
            public List<PipeExcavationRow> Rows { get; }
            public List<string> UsableHandles { get; }
            public List<string> Rejections { get; }
        }

        private sealed class SewerExcavationLink
        {
            public SewerExcavationLink(
                string schema,
                SewerExcavationSettings settings,
                IEnumerable<string> handles)
            {
                Schema = string.IsNullOrWhiteSpace(schema) ? LinkSchema : schema;
                Settings = settings;
                Handles = handles == null
                    ? new List<string>()
                    : handles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            public string Schema { get; }
            public SewerExcavationSettings Settings { get; }
            public List<string> Handles { get; }
        }
    }

    internal sealed class SewerExcavationSettings
    {
        public SewerExcavationSettings()
        {
            UnitsPerMetre = 1.0;
            SideAllowance = 0.30;
            MinimumWidth = 0.60;
            TrenchWidth = 0.76;
            BeddingThickness = 0.10;
            BlanketAbovePipe = 0.30;
            StructureSideAllowance = 0.30;
            FallbackCover = 1.20;
            ExcavationToBottomOnly = false;
        }
        public double UnitsPerMetre { get; set; }
        public double SideAllowance { get; set; }
        public double MinimumWidth { get; set; }
        public double TrenchWidth { get; set; }
        public double BeddingThickness { get; set; }
        public double BlanketAbovePipe { get; set; }
        public double StructureSideAllowance { get; set; }
        public double FallbackCover { get; set; }
        public bool ExcavationToBottomOnly { get; set; }
        public void Validate()
        {
            if (!IsPositive(UnitsPerMetre)) UnitsPerMetre = 1.0;
            if (SideAllowance < 0.0 || double.IsNaN(SideAllowance) || double.IsInfinity(SideAllowance)) SideAllowance = 0.30;
            if (!IsPositive(MinimumWidth)) MinimumWidth = 0.60;
            if (TrenchWidth < 0.0 || double.IsNaN(TrenchWidth) || double.IsInfinity(TrenchWidth)) TrenchWidth = 0.76;
            if (BeddingThickness < 0.0 || double.IsNaN(BeddingThickness) || double.IsInfinity(BeddingThickness)) BeddingThickness = 0.10;
            if (BlanketAbovePipe < 0.0 || double.IsNaN(BlanketAbovePipe) || double.IsInfinity(BlanketAbovePipe)) BlanketAbovePipe = 0.30;
            if (StructureSideAllowance < 0.0 || double.IsNaN(StructureSideAllowance) || double.IsInfinity(StructureSideAllowance)) StructureSideAllowance = 0.30;
            if (!IsPositive(FallbackCover)) FallbackCover = 1.20;
        }
        private static bool IsPositive(double value)
        {
            return value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    internal sealed class SewerExcavationSettingsWindow : Window
    {
        private readonly Dictionary<string, TextBox> _values;
        private readonly CheckBox _toBottomOnly;
        public SewerExcavationSettingsWindow(SewerExcavationSettings initial)
        {
            Title = "CE Tools - Sewer Excavation Settings";
            Width = 640;
            Height = 610;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            _values = new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
            _toBottomOnly = new CheckBox
            {
                Content = "Total excavation is measured to the bottom of bedding.",
                IsChecked = true,
                IsEnabled = false,
                Margin = new Thickness(0, 10, 0, 4)
            };
            var root = new DockPanel { Margin = new Thickness(18) };
            Content = root;
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            var cancel = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            cancel.Click += delegate { Close(); };
            buttons.Children.Add(cancel);
            var apply = new Button { Content = "Review Quantities", Width = 130, IsDefault = true };
            apply.Click += delegate
            {
                SewerExcavationSettings parsed;
                string error;
                if (!TryReadSettings(out parsed, out error))
                {
                    MessageBox.Show(error, "CE Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Settings = parsed;
                Accepted = true;
                Close();
            };
            buttons.Children.Add(apply);

            var panel = new StackPanel();
            root.Children.Add(panel);
            panel.Children.Add(new TextBlock
            {
                Text = "Quantities use the sewer reference surface and actual long-section pipe elevations. Cover is natural ground to pipe crown; trench depth is natural ground to bottom of bedding. Total excavation is always to bottom of bedding. Structures use natural ground to the lower of the lowest connected pipe invert or structure floor.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            AddField(panel, "UnitsPerMetre", "Drawing units per metre", initial.UnitsPerMetre);
            AddField(panel, "TrenchWidth", "Specified trench width (m)", initial.TrenchWidth);
            AddField(panel, "SideAllowance", "Legacy auto-width side allowance each side (m)", initial.SideAllowance);
            AddField(panel, "MinimumWidth", "Legacy minimum trench width (m)", initial.MinimumWidth);
            AddField(panel, "BeddingThickness", "Bedding depth (m)", initial.BeddingThickness);
            AddField(panel, "BlanketAbovePipe", "Blanket fill above pipe crown (m)", initial.BlanketAbovePipe);
            AddField(panel, "StructureSideAllowance", "Structure excavation allowance each side (m)", initial.StructureSideAllowance);
            AddField(panel, "FallbackCover", "Fallback average cover (m)", initial.FallbackCover);
            panel.Children.Add(_toBottomOnly);
        }

        public bool Accepted { get; private set; }
        public SewerExcavationSettings Settings { get; private set; }

        private void AddField(Panel parent, string key, string label, double value)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);
            var box = new TextBox
            {
                Text = value.ToString("0.###", CultureInfo.InvariantCulture),
                MinWidth = 180,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);
            _values[key] = box;
            parent.Children.Add(grid);
        }

        private bool TryReadSettings(out SewerExcavationSettings settings, out string error)
        {
            settings = new SewerExcavationSettings();
            error = string.Empty;

            double units = 0.0;
            double trenchWidth = 0.0;
            double side = 0.0;
            double minimumWidth = 0.0;
            double bedding = 0.0;
            double blanket = 0.0;
            double structureAllowance = 0.0;
            double cover = 0.0;

            if (!TryRead("UnitsPerMetre", out units) || units <= 0.0)
                error = "Drawing units per metre must be greater than zero.";
            else if (!TryRead("TrenchWidth", out trenchWidth) || trenchWidth <= 0.0)
                error = "Specified trench width must be greater than zero.";
            else if (!TryRead("SideAllowance", out side) || side < 0.0)
                error = "Side allowance cannot be negative.";
            else if (!TryRead("MinimumWidth", out minimumWidth) || minimumWidth <= 0.0)
                error = "Minimum trench width must be greater than zero.";
            else if (!TryRead("BeddingThickness", out bedding) || bedding < 0.0)
                error = "Bedding depth cannot be negative.";
            else if (!TryRead("BlanketAbovePipe", out blanket) || blanket < 0.0)
                error = "Blanket fill depth cannot be negative.";
            else if (!TryRead("StructureSideAllowance", out structureAllowance) || structureAllowance < 0.0)
                error = "Structure excavation allowance cannot be negative.";
            else if (!TryRead("FallbackCover", out cover) || cover <= 0.0)
                error = "Fallback cover must be greater than zero.";

            if (!string.IsNullOrEmpty(error))
                return false;

            settings.UnitsPerMetre = units;
            settings.TrenchWidth = trenchWidth;
            settings.SideAllowance = side;
            settings.MinimumWidth = minimumWidth;
            settings.BeddingThickness = bedding;
            settings.BlanketAbovePipe = blanket;
            settings.StructureSideAllowance = structureAllowance;
            settings.FallbackCover = cover;
            settings.ExcavationToBottomOnly = false;
            settings.Validate();
            return true;
        }

        private bool TryRead(string key, out double value)
        {
            string text = _values[key].Text;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
                   double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
