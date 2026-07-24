using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using CivilBaseline = Autodesk.Civil.DatabaseServices.Baseline;
using CivilBaselineRegion = Autodesk.Civil.DatabaseServices.BaselineRegion;
using CivilCorridor = Autodesk.Civil.DatabaseServices.Corridor;
using CivilEntity = Autodesk.Civil.DatabaseServices.Entity;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.ReportPresentationCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Read-only report commands that present Civil 3D data in a pop-up grid and
    /// offer the same data as an AutoCAD table. Existing command-line reports are
    /// retained for backward compatibility while the ribbon routes users here.
    /// </summary>
    public sealed class ReportPresentationCommands
    {
        [CommandMethod(
            "CE_TOOLS",
            "CE_ALREPORTUI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void AlignmentReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect Civil 3D alignments to report: ");
            if (selection.Status != PromptStatus.OK) return;

            var columns = new List<string>
            {
                "Alignment", "Type", "Start", "End", "Length", "Site",
                "Style", "Profiles", "Reference"
            };
            var rows = new List<IList<string>>();
            int skipped = 0;
            double totalLength = 0.0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    CivilAlignment alignment = Open<CivilAlignment>(transaction, selectedObject);
                    if (alignment == null)
                    {
                        skipped++;
                        continue;
                    }

                    totalLength += alignment.Length;
                    rows.Add(new List<string>
                    {
                        alignment.Name,
                        alignment.AlignmentType.ToString(),
                        FormatStation(alignment, alignment.StartingStation),
                        FormatStation(alignment, alignment.EndingStation),
                        FormatNumber(alignment.Length),
                        string.IsNullOrWhiteSpace(alignment.SiteName) ? "<Siteless>" : alignment.SiteName,
                        alignment.StyleName,
                        SafeProfileCount(alignment).ToString(CultureInfo.InvariantCulture),
                        YesNo(alignment.IsReferenceObject)
                    });
                }
            }

            string note = string.Format(
                CultureInfo.CurrentCulture,
                "Alignments: {0}; skipped: {1}; total length: {2:N3}.",
                rows.Count,
                skipped,
                totalLength);
            editor.WriteMessage("\nCE_ALREPORTUI complete. " + note);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools Alignment Report",
                note,
                columns,
                rows,
                "Alignment Report");
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PRREPORTUI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ProfileReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect Civil 3D profiles to report: ");
            if (selection.Status != PromptStatus.OK) return;

            var columns = new List<string>
            {
                "Profile", "Type", "Alignment", "Start", "End", "Length",
                "Style", "Update", "PVIs", "Reference"
            };
            var rows = new List<IList<string>>();
            int skipped = 0;
            double totalLength = 0.0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    CivilProfile profile = Open<CivilProfile>(transaction, selectedObject);
                    if (profile == null)
                    {
                        skipped++;
                        continue;
                    }

                    CivilAlignment alignment = Open<CivilAlignment>(transaction, profile.AlignmentId);
                    totalLength += profile.Length;
                    rows.Add(new List<string>
                    {
                        profile.Name,
                        profile.ProfileType.ToString(),
                        alignment == null ? "<Unknown>" : alignment.Name,
                        FormatStation(alignment, profile.StartingStation),
                        FormatStation(alignment, profile.EndingStation),
                        FormatNumber(profile.Length),
                        profile.StyleName,
                        profile.UpdateMode.ToString(),
                        SafePviCount(profile).ToString(CultureInfo.InvariantCulture),
                        YesNo(profile.IsReferenceObject)
                    });
                }
            }

            string note = string.Format(
                CultureInfo.CurrentCulture,
                "Profiles: {0}; skipped: {1}; total length: {2:N3}.",
                rows.Count,
                skipped,
                totalLength);
            editor.WriteMessage("\nCE_PRREPORTUI complete. " + note);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools Profile Report",
                note,
                columns,
                rows,
                "Profile Report");
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_SFREPORTUI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void SurfaceReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect Civil 3D surfaces to report: ");
            if (selection.Status != PromptStatus.OK) return;

            var columns = new List<string>
            {
                "Surface", "Type", "Style", "Points", "Min Elev", "Max Elev",
                "Mean Elev", "Min X", "Max X", "Min Y", "Max Y", "Volume",
                "Reference", "Out of Date", "Locked"
            };
            var rows = new List<IList<string>>();
            int skipped = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    CivilSurface surface = Open<CivilSurface>(transaction, selectedObject);
                    if (surface == null)
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        var properties = surface.GetGeneralProperties();
                        rows.Add(new List<string>
                        {
                            surface.Name,
                            surface.GetType().Name,
                            surface.StyleName,
                            properties.NumberOfPoints.ToString(CultureInfo.InvariantCulture),
                            FormatNumber(properties.MinimumElevation),
                            FormatNumber(properties.MaximumElevation),
                            FormatNumber(properties.MeanElevation),
                            FormatNumber(properties.MinimumCoordinateX),
                            FormatNumber(properties.MaximumCoordinateX),
                            FormatNumber(properties.MinimumCoordinateY),
                            FormatNumber(properties.MaximumCoordinateY),
                            YesNo(surface.IsVolumeSurface),
                            YesNo(surface.IsReferenceObject),
                            YesNo(surface.IsOutOfDate),
                            YesNo(surface.Lock)
                        });
                    }
                    catch
                    {
                        skipped++;
                    }
                }
            }

            string note = string.Format(
                CultureInfo.CurrentCulture,
                "Surfaces: {0}; skipped: {1}.",
                rows.Count,
                skipped);
            editor.WriteMessage("\nCE_SFREPORTUI complete. " + note);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools Surface Report",
                note,
                columns,
                rows,
                "Surface Report");
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_CORREPORTUI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CorridorReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect Civil 3D corridors to report: ");
            if (selection.Status != PromptStatus.OK) return;

            var columns = new List<string>
            {
                "Corridor", "Style", "Code Set", "Baselines", "Regions",
                "Surfaces", "Feature Codes", "Auto Rebuild", "Out of Date",
                "Reference", "Region Lock", "Max Triangle"
            };
            var rows = new List<IList<string>>();
            int skipped = 0;
            int totalBaselines = 0;
            int totalRegions = 0;
            int totalSurfaces = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    CivilCorridor corridor = Open<CivilCorridor>(transaction, selectedObject);
                    if (corridor == null)
                    {
                        skipped++;
                        continue;
                    }

                    int regionCount = CountRegions(corridor);
                    totalBaselines += corridor.Baselines.Count;
                    totalRegions += regionCount;
                    totalSurfaces += corridor.CorridorSurfaces.Count;
                    rows.Add(new List<string>
                    {
                        corridor.Name,
                        corridor.StyleName,
                        corridor.CodeSetStyleName,
                        corridor.Baselines.Count.ToString(CultureInfo.InvariantCulture),
                        regionCount.ToString(CultureInfo.InvariantCulture),
                        corridor.CorridorSurfaces.Count.ToString(CultureInfo.InvariantCulture),
                        corridor.FeatureLineCodeInfos.Count.ToString(CultureInfo.InvariantCulture),
                        YesNo(corridor.RebuildAutomatic),
                        YesNo(corridor.IsOutOfDate),
                        YesNo(corridor.IsReferenceObject),
                        corridor.RegionLockMode.ToString(),
                        FormatNumber(corridor.MaximumTriangleSideLength)
                    });
                }
            }

            string note = string.Format(
                CultureInfo.CurrentCulture,
                "Corridors: {0}; skipped: {1}; baselines: {2}; regions: {3}; surfaces: {4}.",
                rows.Count,
                skipped,
                totalBaselines,
                totalRegions,
                totalSurfaces);
            editor.WriteMessage("\nCE_CORREPORTUI complete. " + note);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools Corridor Report",
                note,
                columns,
                rows,
                "Corridor Report");
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_CORBASEUI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CorridorBaselineReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect Civil 3D corridors for baseline and region report: ");
            if (selection.Status != PromptStatus.OK) return;

            var columns = new List<string>
            {
                "Corridor", "Baseline", "Type", "Alignment", "Profile",
                "Feature Line", "Baseline Start", "Baseline End", "Baseline Length",
                "Region", "Region Start", "Region End", "Assembly", "Needs Processing"
            };
            var rows = new List<IList<string>>();
            int corridorCount = 0;
            int baselineCount = 0;
            int regionCount = 0;
            int skipped = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    CivilCorridor corridor = Open<CivilCorridor>(transaction, selectedObject);
                    if (corridor == null)
                    {
                        skipped++;
                        continue;
                    }

                    corridorCount++;
                    foreach (CivilBaseline baseline in corridor.Baselines)
                    {
                        baselineCount++;
                        string alignmentName = ResolveName(transaction, SafeAlignmentId(baseline));
                        string profileName = ResolveName(transaction, SafeProfileId(baseline));
                        string featureLineName = ResolveName(transaction, SafeFeatureLineId(baseline));

                        if (baseline.BaselineRegions.Count == 0)
                        {
                            rows.Add(BuildBaselineRow(
                                corridor,
                                baseline,
                                alignmentName,
                                profileName,
                                featureLineName,
                                null,
                                transaction));
                        }
                        else
                        {
                            foreach (CivilBaselineRegion region in baseline.BaselineRegions)
                            {
                                regionCount++;
                                rows.Add(BuildBaselineRow(
                                    corridor,
                                    baseline,
                                    alignmentName,
                                    profileName,
                                    featureLineName,
                                    region,
                                    transaction));
                            }
                        }
                    }
                }
            }

            string note = string.Format(
                CultureInfo.CurrentCulture,
                "Corridors: {0}; baselines: {1}; regions: {2}; skipped: {3}.",
                corridorCount,
                baselineCount,
                regionCount,
                skipped);
            editor.WriteMessage("\nCE_CORBASEUI complete. " + note);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools Corridor Baseline and Region Report",
                note,
                columns,
                rows,
                "Corridor Baselines and Regions");
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_FLREPORTUI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FeatureLineReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect feature lines to report: ");
            if (selection.Status != PromptStatus.OK) return;

            var columns = new List<string>
            {
                "Feature Line", "Layer", "Length 2D", "Length 3D", "Min Elev",
                "Max Elev", "Min Grade %", "Max Grade %", "Points", "PI Points",
                "Elevation Points", "Reference"
            };
            var rows = new List<IList<string>>();
            int skipped = 0;
            double totalLength2D = 0.0;
            double totalLength3D = 0.0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    CivilFeatureLine featureLine = Open<CivilFeatureLine>(transaction, selectedObject);
                    if (featureLine == null || featureLine.GetType() != typeof(CivilFeatureLine))
                    {
                        skipped++;
                        continue;
                    }

                    totalLength2D += featureLine.Length2D;
                    totalLength3D += featureLine.Length3D;
                    rows.Add(new List<string>
                    {
                        string.IsNullOrWhiteSpace(featureLine.Name)
                            ? "FeatureLine " + featureLine.Handle
                            : featureLine.Name,
                        featureLine.Layer,
                        FormatNumber(featureLine.Length2D),
                        FormatNumber(featureLine.Length3D),
                        FormatNumber(featureLine.MinElevation),
                        FormatNumber(featureLine.MaxElevation),
                        FormatNumber(featureLine.MinGrade * 100.0),
                        FormatNumber(featureLine.MaxGrade * 100.0),
                        featureLine.PointsCount.ToString(CultureInfo.InvariantCulture),
                        featureLine.PIPointsCount.ToString(CultureInfo.InvariantCulture),
                        featureLine.ElevationPointsCount.ToString(CultureInfo.InvariantCulture),
                        YesNo(featureLine.IsReferenceObject)
                    });
                }
            }

            string note = string.Format(
                CultureInfo.CurrentCulture,
                "Feature lines: {0}; skipped: {1}; total L2D: {2:N3}; total L3D: {3:N3}.",
                rows.Count,
                skipped,
                totalLength2D,
                totalLength3D);
            editor.WriteMessage("\nCE_FLREPORTUI complete. " + note);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools Feature Line Report",
                note,
                columns,
                rows,
                "Feature Line Report");
        }

        [CommandMethod(
            "CE_TOOLS",
            "CE_PKREPORTUI",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void ParkingReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = GetSelection(
                editor,
                "\nSelect parking bay blocks and/or closed bay polylines: ");
            if (selection.Status != PromptStatus.OK) return;

            var groups = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            int skipped = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selectedObject in selection.Value)
                {
                    DBObject databaseObject = OpenObject(transaction, selectedObject);
                    string groupName = null;

                    var blockReference = databaseObject as BlockReference;
                    if (blockReference != null)
                    {
                        groupName = "Block: " + GetBlockName(transaction, blockReference);
                    }
                    else
                    {
                        var polyline = databaseObject as Polyline;
                        if (polyline != null && polyline.Closed)
                        {
                            groupName = "Closed polyline layer: " + polyline.Layer;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(groupName))
                    {
                        skipped++;
                        continue;
                    }

                    total++;
                    int current;
                    groups.TryGetValue(groupName, out current);
                    groups[groupName] = current + 1;
                }
            }

            var columns = new List<string> { "Parking Bay Group", "Count" };
            var rows = new List<IList<string>>();
            foreach (KeyValuePair<string, int> group in groups)
            {
                rows.Add(new List<string>
                {
                    group.Key,
                    group.Value.ToString(CultureInfo.InvariantCulture)
                });
            }

            string note = string.Format(
                CultureInfo.CurrentCulture,
                "Parking bays counted: {0}; skipped: {1}; groups: {2}.",
                total,
                skipped,
                groups.Count);
            editor.WriteMessage("\nCE_PKREPORTUI complete. " + note);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools Parking Report",
                note,
                columns,
                rows,
                "Parking Bay Report");
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
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

        private static T Open<T>(Transaction transaction, SelectedObject selectedObject)
            where T : DBObject
        {
            return selectedObject == null
                ? null
                : Open<T>(transaction, selectedObject.ObjectId);
        }

        private static T Open<T>(Transaction transaction, ObjectId objectId)
            where T : DBObject
        {
            if (objectId.IsNull || objectId.IsErased)
            {
                return null;
            }

            try
            {
                return transaction.GetObject(objectId, OpenMode.ForRead, false) as T;
            }
            catch
            {
                return null;
            }
        }

        private static DBObject OpenObject(
            Transaction transaction,
            SelectedObject selectedObject)
        {
            if (selectedObject == null || selectedObject.ObjectId.IsNull)
            {
                return null;
            }

            try
            {
                return transaction.GetObject(
                    selectedObject.ObjectId,
                    OpenMode.ForRead,
                    false);
            }
            catch
            {
                return null;
            }
        }

        private static string FormatStation(CivilAlignment alignment, double rawStation)
        {
            if (alignment != null)
            {
                try
                {
                    return alignment.GetStationStringWithEquations(rawStation);
                }
                catch
                {
                    // Fall through to the raw value.
                }
            }

            return FormatNumber(rawStation);
        }

        private static int SafeProfileCount(CivilAlignment alignment)
        {
            try
            {
                return alignment.GetProfileIds().Count;
            }
            catch
            {
                return 0;
            }
        }

        private static int SafePviCount(CivilProfile profile)
        {
            try
            {
                return profile.PVIs == null ? 0 : profile.PVIs.Count;
            }
            catch
            {
                return 0;
            }
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

        private static IList<string> BuildBaselineRow(
            CivilCorridor corridor,
            CivilBaseline baseline,
            string alignmentName,
            string profileName,
            string featureLineName,
            CivilBaselineRegion region,
            Transaction transaction)
        {
            string regionName = region == null ? "<None>" : region.Name;
            string regionStart = region == null ? string.Empty : FormatNumber(region.StartStation);
            string regionEnd = region == null ? string.Empty : FormatNumber(region.EndStation);
            string assemblyName = region == null
                ? string.Empty
                : ResolveName(transaction, region.AssemblyId);
            bool needsProcessing = region == null
                ? baseline.NeedsProcessing
                : region.NeedsProcessing;

            return new List<string>
            {
                corridor.Name,
                baseline.Name,
                baseline.BaselineType.ToString(),
                alignmentName,
                profileName,
                featureLineName,
                FormatNumber(baseline.StartStation),
                FormatNumber(baseline.EndStation),
                FormatNumber(baseline.EndStation - baseline.StartStation),
                regionName,
                regionStart,
                regionEnd,
                assemblyName,
                YesNo(needsProcessing)
            };
        }

        private static ObjectId SafeAlignmentId(CivilBaseline baseline)
        {
            try { return baseline.AlignmentId; }
            catch { return ObjectId.Null; }
        }

        private static ObjectId SafeProfileId(CivilBaseline baseline)
        {
            try { return baseline.ProfileId; }
            catch { return ObjectId.Null; }
        }

        private static ObjectId SafeFeatureLineId(CivilBaseline baseline)
        {
            try { return baseline.FeatureLineId; }
            catch { return ObjectId.Null; }
        }

        private static string ResolveName(Transaction transaction, ObjectId objectId)
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
                if (alignment != null) return alignment.Name;

                var profile = databaseObject as CivilProfile;
                if (profile != null) return profile.Name;

                var featureLine = databaseObject as CivilFeatureLine;
                if (featureLine != null) return featureLine.Name;

                var assembly = databaseObject as CivilAssembly;
                if (assembly != null) return assembly.Name;

                return databaseObject.GetType().Name + " " + databaseObject.Handle;
            }
            catch
            {
                return "<Unavailable>";
            }
        }

        private static string GetBlockName(
            Transaction transaction,
            BlockReference blockReference)
        {
            ObjectId definitionId = blockReference.IsDynamicBlock
                ? blockReference.DynamicBlockTableRecord
                : blockReference.BlockTableRecord;
            BlockTableRecord definition = transaction.GetObject(
                definitionId,
                OpenMode.ForRead,
                false) as BlockTableRecord;
            return definition == null ? "<Unknown>" : definition.Name;
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("N3", CultureInfo.CurrentCulture);
        }

        private static string YesNo(bool value)
        {
            return value ? "Yes" : "No";
        }
    }
}
