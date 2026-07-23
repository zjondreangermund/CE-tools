using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivilBaseline = Autodesk.Civil.DatabaseServices.Baseline;
using CivilBaselineRegion = Autodesk.Civil.DatabaseServices.BaselineRegion;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivilEntity = Autodesk.Civil.DatabaseServices.Entity;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;

[assembly: CommandClass(typeof(CETools.Civil3D.CorridorCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Corridor reporting, baseline/region inspection and controlled rebuild tools.
    /// </summary>
    public sealed class CorridorCommands
    {
        private const string ReportKeyword = "Report";
        private const string BaselinesKeyword = "Baselines";
        private const string RebuildKeyword = "Rebuild";

        [CommandMethod(
            "CE_TOOLS",
            "CE_CORTOOLS",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CorridorTools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var options = new PromptKeywordOptions(
                "\nCorridor tool [Report/Baselines/Rebuild] <Report>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add(ReportKeyword);
            options.Keywords.Add(BaselinesKeyword);
            options.Keywords.Add(RebuildKeyword);

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string mode = result.Status == PromptStatus.None
                ? ReportKeyword
                : result.StringResult;

            if (string.Equals(mode, BaselinesKeyword, StringComparison.OrdinalIgnoreCase))
            {
                ReportBaselines(document);
            }
            else if (string.Equals(mode, RebuildKeyword, StringComparison.OrdinalIgnoreCase))
            {
                RebuildCorridors(document);
            }
            else
            {
                ReportCorridors(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_CORREPORT",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CorridorReport()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                ReportCorridors(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_CORBASE",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CorridorBaselineReport()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                ReportBaselines(document);
            }
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_CORREBUILD",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CorridorRebuild()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                RebuildCorridors(document);
            }
        }

        private static void ReportCorridors(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetCorridorSelection(
                editor,
                "\nSelect Civil 3D corridors to report: ");
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            int counted = 0;
            int skipped = 0;
            int totalBaselines = 0;
            int totalRegions = 0;
            int totalSurfaces = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    CivilCorridor corridor = OpenCorridor(
                        transaction,
                        selectedObject == null ? ObjectId.Null : selectedObject.ObjectId,
                        OpenMode.ForRead);
                    if (corridor == null)
                    {
                        skipped++;
                        continue;
                    }

                    int regionCount = CountRegions(corridor);
                    counted++;
                    totalBaselines += corridor.Baselines.Count;
                    totalRegions += regionCount;
                    totalSurfaces += corridor.CorridorSurfaces.Count;

                    editor.WriteMessage(
                        "\n  {0}: Style={1}; CodeSet={2}; Baselines={3}; Regions={4}; " +
                        "Surfaces={5}; FeatureLineCodes={6}; AutoRebuild={7}; " +
                        "OutOfDate={8}; Reference={9}; RegionLock={10}; MaxTriangle={11:N3}",
                        corridor.Name,
                        corridor.StyleName,
                        corridor.CodeSetStyleName,
                        corridor.Baselines.Count,
                        regionCount,
                        corridor.CorridorSurfaces.Count,
                        corridor.FeatureLineCodeInfos.Count,
                        corridor.RebuildAutomatic ? "Yes" : "No",
                        corridor.IsOutOfDate ? "Yes" : "No",
                        corridor.IsReferenceObject ? "Yes" : "No",
                        corridor.RegionLockMode,
                        corridor.MaximumTriangleSideLength);
                }
            }

            editor.WriteMessage(
                "\nCE_CORREPORT complete. Corridors={0}; skipped={1}; baselines={2}; regions={3}; surfaces={4}.",
                counted,
                skipped,
                totalBaselines,
                totalRegions,
                totalSurfaces);
        }

        private static void ReportBaselines(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetCorridorSelection(
                editor,
                "\nSelect Civil 3D corridors for baseline and region report: ");
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            int corridorCount = 0;
            int baselineCount = 0;
            int regionCount = 0;
            int skipped = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    CivilCorridor corridor = OpenCorridor(
                        transaction,
                        selectedObject == null ? ObjectId.Null : selectedObject.ObjectId,
                        OpenMode.ForRead);
                    if (corridor == null)
                    {
                        skipped++;
                        continue;
                    }

                    corridorCount++;
                    editor.WriteMessage(
                        "\nCORRIDOR {0} — Baselines={1}; OutOfDate={2}",
                        corridor.Name,
                        corridor.Baselines.Count,
                        corridor.IsOutOfDate ? "Yes" : "No");

                    foreach (CivilBaseline baseline in corridor.Baselines)
                    {
                        baselineCount++;
                        string alignmentName = ResolveEntityName(transaction, SafeAlignmentId(baseline));
                        string profileName = ResolveEntityName(transaction, SafeProfileId(baseline));
                        string featureLineName = ResolveEntityName(transaction, SafeFeatureLineId(baseline));

                        editor.WriteMessage(
                            "\n  BASELINE {0}: Type={1}; Start={2:N3}; End={3:N3}; Length={4:N3}; " +
                            "Alignment={5}; Profile={6}; FeatureLine={7}; Regions={8}; NeedsProcessing={9}",
                            baseline.Name,
                            baseline.BaselineType,
                            baseline.StartStation,
                            baseline.EndStation,
                            baseline.EndStation - baseline.StartStation,
                            alignmentName,
                            profileName,
                            featureLineName,
                            baseline.BaselineRegions.Count,
                            baseline.NeedsProcessing ? "Yes" : "No");

                        foreach (CivilBaselineRegion region in baseline.BaselineRegions)
                        {
                            regionCount++;
                            string assemblyName = ResolveEntityName(transaction, region.AssemblyId);
                            editor.WriteMessage(
                                "\n    REGION {0}: Start={1:N3}; End={2:N3}; Length={3:N3}; " +
                                "Assembly={4}; NeedsProcessing={5}",
                                region.Name,
                                region.StartStation,
                                region.EndStation,
                                region.EndStation - region.StartStation,
                                assemblyName,
                                region.NeedsProcessing ? "Yes" : "No");
                        }
                    }
                }
            }

            editor.WriteMessage(
                "\nCE_CORBASE complete. Corridors={0}; baselines={1}; regions={2}; skipped={3}.",
                corridorCount,
                baselineCount,
                regionCount,
                skipped);
        }

        private static void RebuildCorridors(Document document)
        {
            Editor editor = document.Editor;
            PromptSelectionResult selection = GetCorridorSelection(
                editor,
                "\nSelect Civil 3D corridors to rebuild: ");
            if (selection.Status != PromptStatus.OK)
            {
                return;
            }

            RebuildPreview preview = BuildRebuildPreview(document.Database, selection);
            if (preview.Rebuildable == 0)
            {
                editor.WriteMessage(
                    "\nCE_CORREBUILD preview: no editable out-of-date corridors found. " +
                    "Up-to-date={0}; skipped={1}.",
                    preview.UpToDate,
                    preview.Skipped);
                return;
            }

            var confirmOptions = new PromptKeywordOptions(
                string.Format(
                    "\nRebuild {0} out-of-date corridors? Up-to-date={1}; skipped={2}. [Yes/No] <No>: ",
                    preview.Rebuildable,
                    preview.UpToDate,
                    preview.Skipped))
            {
                AllowNone = true
            };
            confirmOptions.Keywords.Add("Yes");
            confirmOptions.Keywords.Add("No");

            PromptResult confirmResult = editor.GetKeywords(confirmOptions);
            if (confirmResult.Status != PromptStatus.OK ||
                !string.Equals(confirmResult.StringResult, "Yes", StringComparison.OrdinalIgnoreCase))
            {
                editor.WriteMessage("\nCE_CORREBUILD cancelled. No corridors were rebuilt.");
                return;
            }

            int rebuilt = 0;
            int skipped = 0;

            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject selectedObject in selection.Value)
                    {
                        CivilCorridor corridor = OpenCorridor(
                            transaction,
                            selectedObject == null ? ObjectId.Null : selectedObject.ObjectId,
                            OpenMode.ForWrite);

                        if (corridor == null ||
                            corridor.IsReferenceObject ||
                            IsLayerLocked(transaction, corridor.LayerId) ||
                            !corridor.IsOutOfDate)
                        {
                            skipped++;
                            continue;
                        }

                        corridor.Rebuild();
                        rebuilt++;
                    }

                    transaction.Commit();
                }

                editor.WriteMessage(
                    "\nCE_CORREBUILD complete. Corridors rebuilt={0}; skipped={1}.",
                    rebuilt,
                    skipped);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_CORREBUILD cancelled. No rebuild changes were committed. {0}",
                    exception.Message);
            }
        }

        private static RebuildPreview BuildRebuildPreview(
            Database database,
            PromptSelectionResult selection)
        {
            var preview = new RebuildPreview();

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    CivilCorridor corridor = OpenCorridor(
                        transaction,
                        selectedObject == null ? ObjectId.Null : selectedObject.ObjectId,
                        OpenMode.ForRead);

                    if (corridor == null ||
                        corridor.IsReferenceObject ||
                        IsLayerLocked(transaction, corridor.LayerId))
                    {
                        preview.Skipped++;
                    }
                    else if (corridor.IsOutOfDate)
                    {
                        preview.Rebuildable++;
                    }
                    else
                    {
                        preview.UpToDate++;
                    }
                }
            }

            return preview;
        }

        private static int CountRegions(CivilCorridor corridor)
        {
            int count = 0;
            foreach (CivilBaseline baseline in corridor.Baselines)
            {
                count += baseline.BaselineRegions.Count;
            }

            return count;
        }

        private static ObjectId SafeAlignmentId(CivilBaseline baseline)
        {
            try
            {
                return baseline.AlignmentId;
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        private static ObjectId SafeProfileId(CivilBaseline baseline)
        {
            try
            {
                return baseline.ProfileId;
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        private static ObjectId SafeFeatureLineId(CivilBaseline baseline)
        {
            try
            {
                return baseline.FeatureLineId;
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        private static string ResolveEntityName(Transaction transaction, ObjectId objectId)
        {
            if (objectId.IsNull || objectId.IsErased)
            {
                return "<None>";
            }

            try
            {
                DBObject databaseObject = transaction.GetObject(
                    objectId,
                    OpenMode.ForRead,
                    false);

                var civilEntity = databaseObject as CivilEntity;
                if (civilEntity != null && !string.IsNullOrWhiteSpace(civilEntity.Name))
                {
                    return civilEntity.Name;
                }

                var alignment = databaseObject as CivilAlignment;
                if (alignment != null)
                {
                    return alignment.Name;
                }

                var profile = databaseObject as CivilProfile;
                if (profile != null)
                {
                    return profile.Name;
                }

                var featureLine = databaseObject as CivilFeatureLine;
                if (featureLine != null)
                {
                    return featureLine.Name;
                }

                var assembly = databaseObject as CivilAssembly;
                if (assembly != null)
                {
                    return assembly.Name;
                }

                return databaseObject.GetType().Name + " " + databaseObject.Handle;
            }
            catch
            {
                return "<Unavailable>";
            }
        }

        private static PromptSelectionResult GetCorridorSelection(Editor editor, string message)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
            {
                editor.SetImpliedSelection(new ObjectId[0]);
                return implied;
            }

            return editor.GetSelection(
                new PromptSelectionOptions
                {
                    MessageForAdding = message,
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
        }

        private static CivilCorridor OpenCorridor(
            Transaction transaction,
            ObjectId objectId,
            OpenMode openMode)
        {
            if (objectId.IsNull)
            {
                return null;
            }

            return transaction.GetObject(
                objectId,
                openMode,
                false) as CivilCorridor;
        }

        private static bool IsLayerLocked(Transaction transaction, ObjectId layerId)
        {
            if (layerId.IsNull)
            {
                return false;
            }

            var layer = transaction.GetObject(
                layerId,
                OpenMode.ForRead,
                false) as LayerTableRecord;
            return layer != null && layer.IsLocked;
        }

        private sealed class RebuildPreview
        {
            public int Rebuildable { get; set; }

            public int UpToDate { get; set; }

            public int Skipped { get; set; }
        }
    }
}
