using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;
using CivilProfile = Autodesk.Civil.DatabaseServices.Profile;
using CivilSite = Autodesk.Civil.DatabaseServices.Site;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.FeatureProfileSurfaceCommentCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Active-comment workflows for feature lines, profiles and surfaces. Reports
    /// use CE popup grids with optional drawing tables, and labels use the shared
    /// 1.8/2.0/5.0 annotation settings with marker and COGO/MText/MLeader output.
    /// </summary>
    public sealed class FeatureProfileSurfaceCommentCommands
    {
        [CommandMethod("CE_TOOLS", "CE_FLREPORT2", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FeatureLineReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect Civil 3D feature lines for the popup report: ");
            if (selection.Status != PromptStatus.OK) return;

            var columns = new List<string>
            {
                "Name", "Site", "Style", "Layer", "Length 2D", "Length 3D",
                "Start Z", "End Z", "Min Grade %", "Max Grade %",
                "Vertices", "Min Z", "Max Z", "Colour"
            };
            var rows = new List<IList<string>>();
            int rejected = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    CivilFeatureLine featureLine = selected == null || selected.ObjectId.IsNull
                        ? null
                        : transaction.GetObject(selected.ObjectId, OpenMode.ForRead, false) as CivilFeatureLine;
                    if (featureLine == null)
                    {
                        rejected++;
                        continue;
                    }

                    Point3dCollection points = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                    double minimum = points.Count == 0 ? 0.0 : points.Cast<Point3d>().Min(point => point.Z);
                    double maximum = points.Count == 0 ? 0.0 : points.Cast<Point3d>().Max(point => point.Z);
                    double startElevation = points.Count == 0 ? 0.0 : points[0].Z;
                    double endElevation = points.Count == 0 ? 0.0 : points[points.Count - 1].Z;
                    rows.Add(new List<string>
                    {
                        SafeName(featureLine),
                        ReadText(featureLine, "SiteName", "<Siteless>"),
                        ReadText(featureLine, "StyleName", "<Drawing default>"),
                        featureLine.Layer,
                        featureLine.Length2D.ToString("N3", CultureInfo.CurrentCulture),
                        featureLine.Length3D.ToString("N3", CultureInfo.CurrentCulture),
                        startElevation.ToString("N3", CultureInfo.CurrentCulture),
                        endElevation.ToString("N3", CultureInfo.CurrentCulture),
                        (featureLine.MinGrade * 100.0).ToString("N3", CultureInfo.CurrentCulture),
                        (featureLine.MaxGrade * 100.0).ToString("N3", CultureInfo.CurrentCulture),
                        points.Count.ToString(CultureInfo.InvariantCulture),
                        minimum.ToString("N3", CultureInfo.CurrentCulture),
                        maximum.ToString("N3", CultureInfo.CurrentCulture),
                        featureLine.ColorIndex.ToString(CultureInfo.InvariantCulture)
                    });
                }
            }

            if (rows.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_FLREPORT2 cancelled. No Civil 3D feature lines were selected.");
                return;
            }

            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Feature Line Report",
                string.Format(CultureInfo.CurrentCulture, "Feature lines={0}; rejected={1}.", rows.Count, rejected),
                columns,
                rows,
                "CE TOOLS FEATURE LINE REPORT");
        }

        [CommandMethod("CE_TOOLS", "CE_FLAPPEARANCE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FeatureLineAppearance()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect Civil 3D feature lines to assign colour, layer and site: ");
            if (selection.Status != PromptStatus.OK) return;

            List<CivilObjectChoice> sites = ReadSites(document);
            var window = new FeatureLineAppearanceWindow(sites);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted) return;

            int changed = 0;
            int styleChanged = 0;
            int siteChanged = 0;
            int layerChanged = 0;
            int rejected = 0;
            var changedIds = new List<ObjectId>();

            // Keep site creation, feature-line writes and site moves under one
            // document lock. Civil 3D 2023 otherwise reports a successful
            // property write but leaves the feature line in the original site.
            using (DocumentLock documentLock = document.LockDocument())
            {
                if (!string.IsNullOrWhiteSpace(window.NewSiteName))
                {
                    ObjectId newSiteId = TryCreateSite(
                        document,
                        window.NewSiteName.Trim());
                    if (newSiteId.IsNull)
                    {
                        document.Editor.WriteMessage(
                            "\nCE_FLAPPEARANCE cancelled. The requested Civil 3D site could not be created.");
                        return;
                    }
                    window.SelectedSiteId = newSiteId;
                }

                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    string requestedLayer = (window.LayerName ?? string.Empty).Trim();
                    ObjectId layerId = string.IsNullOrWhiteSpace(requestedLayer)
                        ? ObjectId.Null
                        : GetOrCreateLayer(document.Database, transaction, requestedLayer);

                    foreach (SelectedObject selected in selection.Value)
                    {
                        CivilFeatureLine featureLine = selected == null || selected.ObjectId.IsNull
                            ? null
                            : transaction.GetObject(selected.ObjectId, OpenMode.ForWrite, false) as CivilFeatureLine;
                        if (featureLine == null)
                        {
                            rejected++;
                            continue;
                        }

                        if (!layerId.IsNull)
                        {
                            try
                            {
                                featureLine.LayerId = layerId;
                                layerChanged++;
                            }
                            catch { }
                        }

                        // Civil feature-line styles override entity colour in plan/model
                        // display. Use a colour-specific sibling style when the API allows it.
                        ObjectId colourStyleId = ResolveFeatureLineColourStyle(
                            document,
                            featureLine,
                            window.ColourIndex,
                            transaction);
                        if (!colourStyleId.IsNull &&
                            TrySetFeatureLineStyleId(featureLine, colourStyleId, transaction))
                            styleChanged++;

                        // Apply the entity colour after changing its Civil 3D style.
                        // The selected style controls plan display, while the entity
                        // colour is what the Properties palette reports. Writing both
                        // keeps the drawing display and Properties palette in sync.
                        Color requestedColour = Color.FromColorIndex(
                            ColorMethod.ByAci,
                            (short)window.ColourIndex);
                        featureLine.Color = requestedColour;
                        featureLine.ColorIndex = window.ColourIndex;

                        try { featureLine.RecordGraphicsModified(true); } catch { }
                        changed++;
                        changedIds.Add(featureLine.ObjectId);
                    }
                    transaction.Commit();
                }

                foreach (ObjectId id in changedIds)
                    if (ApplySite(id, window.SelectedSiteId)) siteChanged++;
            }

            August23PlatformDynamicGradingCommands.SynchronizeLinkedAppearance(
                document,
                changedIds);

            try { document.Editor.SetImpliedSelection(new ObjectId[0]); } catch { }
            try { document.Database.TransactionManager.QueueForGraphicsFlush(); } catch { }
            try { AcApplication.UpdateScreen(); } catch { }
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_FLAPPEARANCE complete. Feature lines updated={0}; visible colour styles={1}; layers={2}; site assignments={3}; rejected={4}; colour={5}.",
                changed,
                styleChanged,
                layerChanged,
                siteChanged,
                rejected,
                window.ColourIndex);
        }

        [CommandMethod("CE_TOOLS", "CE_FLVERTEXLABELS", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FeatureLineVertexLabels()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, true, out settings)) return;
            PromptSelectionResult selection = GetSelection(
                document.Editor,
                "\nSelect Civil 3D feature lines to annotate at every vertex: ");
            if (selection.Status != PromptStatus.OK) return;

            ObjectId ngSurfaceId;
            if (!SurveyCoordinateWorkflowCommands.PromptOptionalNgSurface(document, out ngSurfaceId)) return;

            int created = 0;
            int rejected = 0;
            var work = new List<FeatureVertexWork>();
            var linkedPointIds = new List<ObjectId>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selection.Value)
                {
                    CivilFeatureLine featureLine = selected == null || selected.ObjectId.IsNull
                        ? null
                        : transaction.GetObject(selected.ObjectId, OpenMode.ForRead, false) as CivilFeatureLine;
                    if (featureLine == null)
                    {
                        rejected++;
                        continue;
                    }

                    Point3dCollection points = featureLine.GetPoints(FeatureLinePointType.AllPoints);
                    string featureName = SafeName(featureLine);
                    for (int index = 0; index < points.Count; index++)
                    {
                        Point3d target = points[index];
                        string pointName = featureName + "-P" +
                            (index + 1).ToString(CultureInfo.InvariantCulture);
                        string contents = string.Join(
                            "\\P",
                            "Point Name: " + pointName,
                            "X: " + target.X.ToString("N3", CultureInfo.CurrentCulture),
                            "Y: " + target.Y.ToString("N3", CultureInfo.CurrentCulture),
                            "Z: " + target.Z.ToString("N3", CultureInfo.CurrentCulture));
                        string plain = string.Format(
                            CultureInfo.CurrentCulture,
                            "Point Name {0}; X {1:N3}; Y {2:N3}; Z {3:N3}",
                            pointName,
                            target.X,
                            target.Y,
                            target.Z);
                        Point3d label = target + new Vector3d(
                            settings.TextHeight * 3.0,
                            settings.TextHeight * (2.0 + (index % 3)),
                            0.0);
                        work.Add(new FeatureVertexWork(
                            selected.ObjectId,
                            index,
                            target,
                            label,
                            pointName,
                            contents,
                            plain));
                    }
                }
            }

            foreach (FeatureVertexWork item in work)
            {
                var generatedIds = new List<ObjectId>();
                if (settings.Output == AnnotationOutput.Cogo)
                {
                    if (!AnnotationWriter.Create(
                        document,
                        item.Target,
                        item.Label,
                        item.Contents,
                        item.Plain,
                        settings,
                        true,
                        generatedIds) ||
                        generatedIds.Count == 0)
                    {
                        continue;
                    }

                    ObjectId pointId = generatedIds[0];
                    DynamicCoordinateLinkStore.SetPointName(
                        document.Database,
                        pointId,
                        item.PointName);
                    DynamicCoordinateLinkStore.LinkFeatureLineVertex(
                        document.Database,
                        item.FeatureLineId,
                        pointId,
                        item.VertexIndex);
                    linkedPointIds.Add(pointId);
                    if (generatedIds.Count > 1)
                    {
                        DynamicCoordinateLinkStore.LinkGeneratedObjects(
                            document.Database,
                            pointId,
                            generatedIds.Skip(1));
                    }
                    created++;
                    continue;
                }

                ObjectId anchorId = CreateCoordinateAnchor(
                    document.Database,
                    item.Target);
                if (anchorId.IsNull) continue;
                DynamicCoordinateLinkStore.SetPointName(
                    document.Database,
                    anchorId,
                    item.PointName);
                DynamicCoordinateLinkStore.LinkFeatureLineVertex(
                    document.Database,
                    item.FeatureLineId,
                    anchorId,
                    item.VertexIndex);
                linkedPointIds.Add(anchorId);

                if (AnnotationWriter.Create(
                    document,
                    item.Target,
                    item.Label,
                    item.Contents,
                    item.Plain,
                    settings,
                    true,
                    generatedIds))
                {
                    DynamicCoordinateLinkStore.LinkGeneratedObjects(
                        document.Database,
                        anchorId,
                        generatedIds);
                    created++;
                }
            }

            if (linkedPointIds.Count > 0 &&
                PromptYesNo(
                    document.Editor,
                    "Place a dynamic Point Name/X/Y/Z table for these feature-line vertices",
                    true))
            {
                PromptPointResult insertion = document.Editor.GetPoint(
                    "\nPick the feature-line point table insertion point: ");
                if (insertion.Status == PromptStatus.OK)
                {
                    SurveyCoordinateWorkflowCommands.CreateLinkedLevelTable(
                        document.Database,
                        insertion.Value.TransformBy(
                            document.Editor.CurrentUserCoordinateSystem),
                        linkedPointIds,
                        settings.TextHeight,
                        "FEATURE LINE POINTS",
                        ngSurfaceId);
                }
            }

            CommentAutoRefreshManager.MarkPending();
            try { CogoPointProjectStyleCommands.ApplySelectedStyles(document, true); } catch { }

            document.Editor.WriteMessage(
                "\nCE_FLVERTEXLABELS complete. Dynamic annotations created={0}; rejected selections={1}; output={2}.",
                created,
                rejected,
                settings.Output);
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILEREPORT2", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProfileReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<CivilObjectChoice> profiles = ReadProfiles(document);
            if (profiles.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_PROFILEREPORT2: no profiles were found in the active drawing.");
                return;
            }

            var columns = new List<string>
            {
                "Profile", "Alignment", "Style", "Start Station", "End Station", "Start Elevation", "End Elevation"
            };
            var rows = new List<IList<string>>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (CivilObjectChoice choice in profiles)
                {
                    CivilProfile profile = transaction.GetObject(choice.ObjectId, OpenMode.ForRead, false) as CivilProfile;
                    if (profile == null) continue;
                    CivilAlignment alignment = transaction.GetObject(profile.AlignmentId, OpenMode.ForRead, false) as CivilAlignment;
                    double start = profile.StartingStation;
                    double end = profile.EndingStation;
                    rows.Add(new List<string>
                    {
                        SafeName(profile),
                        alignment == null ? "<Missing alignment>" : SafeName(alignment),
                        ReadText(profile, "StyleName", "<Drawing default>"),
                        FormatStation(alignment, start),
                        FormatStation(alignment, end),
                        profile.ElevationAt(start).ToString("N3", CultureInfo.CurrentCulture),
                        profile.ElevationAt(end).ToString("N3", CultureInfo.CurrentCulture)
                    });
                }
            }

            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Profile Report",
                "Profiles found in the active Civil 3D drawing.",
                columns,
                rows,
                "CE TOOLS PROFILE REPORT");
        }

        [CommandMethod("CE_TOOLS", "CE_PROFILEELEVATION2", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ProfileElevation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CivilObjectChoice choice = PickObject(
                "CE Tools - Select Profile",
                "Select the profile used for elevation and grade reporting.",
                ReadProfiles(document));
            if (choice == null) return;

            PromptDoubleResult stationResult = document.Editor.GetDouble(
                new PromptDoubleOptions("\nEnter raw profile station: ")
                {
                    AllowNegative = true,
                    AllowZero = true,
                    AllowNone = false
                });
            if (stationResult.Status != PromptStatus.OK) return;

            Point3d target;
            string stationText;
            double elevation;
            double grade;
            string profileName;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilProfile profile = transaction.GetObject(choice.ObjectId, OpenMode.ForRead, false) as CivilProfile;
                if (profile == null) return;
                CivilAlignment alignment = transaction.GetObject(profile.AlignmentId, OpenMode.ForRead, false) as CivilAlignment;
                if (alignment == null) return;
                elevation = profile.ElevationAt(stationResult.Value);
                grade = profile.GradeAt(stationResult.Value) * 100.0;
                double x = 0.0;
                double y = 0.0;
                alignment.PointLocation(stationResult.Value, 0.0, ref x, ref y);
                target = new Point3d(x, y, elevation);
                stationText = FormatStation(alignment, stationResult.Value);
                profileName = SafeName(profile);
            }

            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    profileName,
                    stationText,
                    elevation.ToString("N3", CultureInfo.CurrentCulture),
                    grade.ToString("N3", CultureInfo.CurrentCulture) + "%"
                }
            };
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Profile Elevation",
                "Profile elevation and grade at the requested station.",
                new List<string> { "Profile", "Station", "Elevation", "Grade" },
                rows,
                "CE TOOLS PROFILE ELEVATION");

            if (!PromptYesNo(document.Editor, "Create a shared CE annotation at this profile location", true)) return;
            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, true, out settings)) return;
            Point3d label = target + new Vector3d(settings.TextHeight * 4.0, settings.TextHeight * 3.0, 0.0);
            string contents = string.Join(
                "\\P",
                profileName,
                "STA: " + stationText,
                "ELEV: " + elevation.ToString("N3", CultureInfo.CurrentCulture),
                "GRADE: " + grade.ToString("N3", CultureInfo.CurrentCulture) + "%");
            AnnotationWriter.Create(document, target, label, contents, contents.Replace("\\P", "; "), settings, true);
        }

        [CommandMethod("CE_TOOLS", "CE_SURFACEREPORT2", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SurfaceReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<CivilObjectChoice> surfaces = ReadSurfaces(document);
            if (surfaces.Count == 0)
            {
                document.Editor.WriteMessage("\nCE_SURFACEREPORT2: no surfaces were found in the active drawing.");
                return;
            }

            var rows = new List<IList<string>>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (CivilObjectChoice choice in surfaces)
                {
                    CivilSurface surface = transaction.GetObject(choice.ObjectId, OpenMode.ForRead, false) as CivilSurface;
                    if (surface == null) continue;
                    rows.Add(new List<string>
                    {
                        SafeName(surface),
                        surface.GetType().Name,
                        ReadText(surface, "StyleName", "<Drawing default>"),
                        ReadNumber(surface, "MinimumElevation").ToString("N3", CultureInfo.CurrentCulture),
                        ReadNumber(surface, "MaximumElevation").ToString("N3", CultureInfo.CurrentCulture),
                        ReadText(surface, "IsOutOfDate", "Unknown")
                    });
                }
            }

            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Surface Report",
                "Surface inventory, style and current elevation range.",
                new List<string> { "Surface", "Type", "Style", "Minimum Z", "Maximum Z", "Out of Date" },
                rows,
                "CE TOOLS SURFACE REPORT");
        }

        [CommandMethod("CE_TOOLS", "CE_SURFACEELEVATION2", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SurfaceElevation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            CivilObjectChoice surfaceChoice = PickObject(
                "CE Tools - Select Surface",
                "Select the surface used for the elevation annotation.",
                ReadSurfaces(document));
            if (surfaceChoice == null) return;
            PromptPointResult pointResult = document.Editor.GetPoint("\nPick the surface-elevation point: ");
            if (pointResult.Status != PromptStatus.OK) return;
            Point3d picked = pointResult.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);

            double elevation;
            string surfaceName;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilSurface surface = transaction.GetObject(surfaceChoice.ObjectId, OpenMode.ForRead, false) as CivilSurface;
                if (surface == null) return;
                elevation = surface.FindElevationAtXY(picked.X, picked.Y);
                surfaceName = SafeName(surface);
            }

            Point3d target = new Point3d(picked.X, picked.Y, elevation);
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Surface Elevation",
                "Current surface elevation at the selected XY location.",
                new List<string> { "Surface", "X", "Y", "Z" },
                new List<IList<string>>
                {
                    new List<string>
                    {
                        surfaceName,
                        target.X.ToString("N3", CultureInfo.CurrentCulture),
                        target.Y.ToString("N3", CultureInfo.CurrentCulture),
                        target.Z.ToString("N3", CultureInfo.CurrentCulture)
                    }
                },
                "CE TOOLS SURFACE ELEVATION");

            if (!PromptYesNo(document.Editor, "Create a shared CE annotation at this point", true)) return;
            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, true, out settings)) return;
            ObjectId anchorId = CreateCoordinateAnchor(document.Database, target);
            if (anchorId.IsNull)
            {
                document.Editor.WriteMessage(
                    "\nCE_SURFACEELEVATION2 cancelled. The dynamic point anchor could not be created.");
                return;
            }
            DynamicCoordinateLinkStore.SetPointName(
                document.Database,
                anchorId,
                surfaceName);
            DynamicCoordinateLinkStore.LinkSurfaceElevation(
                document.Database,
                surfaceChoice.ObjectId,
                new[] { anchorId });

            Point3d label = target + new Vector3d(settings.TextHeight * 4.0, settings.TextHeight * 3.0, 0.0);
            string contents = string.Join(
                "\\P",
                surfaceName,
                "X: " + target.X.ToString("N3", CultureInfo.CurrentCulture),
                "Y: " + target.Y.ToString("N3", CultureInfo.CurrentCulture),
                "Z: " + target.Z.ToString("N3", CultureInfo.CurrentCulture));
            var generatedIds = new List<ObjectId>();
            if (AnnotationWriter.Create(
                document,
                target,
                label,
                contents,
                contents.Replace("\\P", "; "),
                settings,
                false,
                generatedIds))
            {
                DynamicCoordinateLinkStore.LinkGeneratedObjects(
                    document.Database,
                    anchorId,
                    generatedIds);
            }

            if (PromptYesNo(
                document.Editor,
                "Place a linked dynamic X/Y/Z table for this surface point",
                false))
            {
                PromptPointResult insertion = document.Editor.GetPoint(
                    "\nPick the linked surface-elevation table insertion point: ");
                if (insertion.Status == PromptStatus.OK)
                {
                    SurveyCoordinateWorkflowCommands.CreateLinkedTable(
                        document.Database,
                        insertion.Value.TransformBy(
                            document.Editor.CurrentUserCoordinateSystem),
                        new[] { anchorId },
                        settings.TextHeight,
                        "SURFACE ELEVATION");
                }
            }
            CommentAutoRefreshManager.MarkPending();
        }

        [CommandMethod("CE_TOOLS", "CE_SURFACECOMPARE2", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SurfaceComparison()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<CivilObjectChoice> surfaces = ReadSurfaces(document);
            CivilObjectChoice first = PickObject(
                "CE Tools - Existing/Base Surface",
                "Select the existing or base surface.",
                surfaces);
            if (first == null) return;
            CivilObjectChoice second = PickObject(
                "CE Tools - Final/Comparison Surface",
                "Select the final or comparison surface.",
                surfaces.Where(item => item.ObjectId != first.ObjectId).ToList());
            if (second == null) return;
            PromptPointResult pointResult = document.Editor.GetPoint("\nPick the surface-comparison point: ");
            if (pointResult.Status != PromptStatus.OK) return;
            Point3d picked = pointResult.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);

            double existing;
            double final;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                CivilSurface baseSurface = transaction.GetObject(first.ObjectId, OpenMode.ForRead, false) as CivilSurface;
                CivilSurface comparison = transaction.GetObject(second.ObjectId, OpenMode.ForRead, false) as CivilSurface;
                if (baseSurface == null || comparison == null) return;
                existing = baseSurface.FindElevationAtXY(picked.X, picked.Y);
                final = comparison.FindElevationAtXY(picked.X, picked.Y);
            }
            double difference = final - existing;
            string classification = difference > 0.0005
                ? "Fill / raise"
                : difference < -0.0005 ? "Cut / lower" : "No material difference";

            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Surface Comparison",
                "Positive difference indicates the comparison surface is above the base surface.",
                new List<string> { "Base Surface", "Comparison Surface", "X", "Y", "Base Z", "Comparison Z", "Difference", "Result" },
                new List<IList<string>>
                {
                    new List<string>
                    {
                        first.Name,
                        second.Name,
                        picked.X.ToString("N3", CultureInfo.CurrentCulture),
                        picked.Y.ToString("N3", CultureInfo.CurrentCulture),
                        existing.ToString("N3", CultureInfo.CurrentCulture),
                        final.ToString("N3", CultureInfo.CurrentCulture),
                        difference.ToString("N3", CultureInfo.CurrentCulture),
                        classification
                    }
                },
                "CE TOOLS SURFACE COMPARISON");

            if (!PromptYesNo(document.Editor, "Create a shared CE comparison annotation", true)) return;
            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, true, out settings)) return;
            Point3d target = new Point3d(picked.X, picked.Y, final);
            Point3d label = target + new Vector3d(settings.TextHeight * 5.0, settings.TextHeight * 3.0, 0.0);
            string contents = string.Join(
                "\\P",
                first.Name + " → " + second.Name,
                "EXISTING: " + existing.ToString("N3", CultureInfo.CurrentCulture),
                "FINAL: " + final.ToString("N3", CultureInfo.CurrentCulture),
                "DIFF: " + difference.ToString("N3", CultureInfo.CurrentCulture),
                classification);
            var generatedIds = new List<ObjectId>();
            if (AnnotationWriter.Create(
                document,
                target,
                label,
                contents,
                contents.Replace("\\P", "; "),
                settings,
                false,
                generatedIds))
            {
                SurfaceComparisonLinkStore.LinkEntities(
                    document.Database,
                    first.ObjectId,
                    second.ObjectId,
                    target,
                    generatedIds);
            }

            if (PromptYesNo(
                document.Editor,
                "Place a linked dynamic surface-comparison table",
                true))
            {
                PromptPointResult insertion = document.Editor.GetPoint(
                    "\nPick the linked surface-comparison table insertion point: ");
                if (insertion.Status == PromptStatus.OK)
                {
                    SurfaceComparisonLinkStore.CreateLinkedTable(
                        document.Database,
                        insertion.Value.TransformBy(
                            document.Editor.CurrentUserCoordinateSystem),
                        first.ObjectId,
                        second.ObjectId,
                        target,
                        settings.TextHeight);
                }
            }
            CommentAutoRefreshManager.MarkPending();
        }

        private static List<CivilObjectChoice> ReadProfiles(Document document)
        {
            var result = new List<CivilObjectChoice>();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return result;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId alignmentId in civilDocument.GetAlignmentIds())
                {
                    CivilAlignment alignment = transaction.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment;
                    if (alignment == null) continue;
                    foreach (ObjectId profileId in alignment.GetProfileIds())
                    {
                        CivilProfile profile = transaction.GetObject(profileId, OpenMode.ForRead, false) as CivilProfile;
                        if (profile == null) continue;
                        result.Add(new CivilObjectChoice(profileId, SafeName(profile), SafeName(alignment)));
                    }
                }
            }
            return result.OrderBy(item => item.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        internal static List<CivilObjectChoice> ReadSurfaces(Document document)
        {
            var result = new List<CivilObjectChoice>();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return result;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId surfaceId in civilDocument.GetSurfaceIds())
                {
                    CivilSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                    if (surface != null)
                        result.Add(new CivilObjectChoice(surfaceId, SafeName(surface), surface.GetType().Name));
                }
            }
            return result.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static ObjectId TryCreateSite(Document document, string name)
        {
            if (document == null || string.IsNullOrWhiteSpace(name)) return ObjectId.Null;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return ObjectId.Null;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (MethodInfo method in typeof(CivilSite).GetMethods(
                    BindingFlags.Public | BindingFlags.Static)
                    .Where(item => string.Equals(
                        item.Name, "Create", StringComparison.OrdinalIgnoreCase)))
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    object[] arguments = new object[parameters.Length];
                    bool supported = true;
                    for (int index = 0; index < parameters.Length; index++)
                    {
                        Type type = parameters[index].ParameterType;
                        string parameterName = (parameters[index].Name ?? string.Empty).ToLowerInvariant();
                        if (type == typeof(string))
                            arguments[index] = name;
                        else if (type == typeof(CivilDocument))
                            arguments[index] = civilDocument;
                        else if (type == typeof(ObjectId))
                            arguments[index] = ObjectId.Null;
                        else if (parameters[index].HasDefaultValue)
                            arguments[index] = parameters[index].DefaultValue;
                        else if (type == typeof(bool))
                            arguments[index] = false;
                        else if (type.IsEnum)
                            arguments[index] = Enum.GetValues(type).GetValue(0);
                        else
                        {
                            supported = false;
                            break;
                        }
                    }
                    if (!supported) continue;
                    try
                    {
                        object result = method.Invoke(null, arguments);
                        ObjectId id = result is ObjectId
                            ? (ObjectId)result
                            : result is CivilSite
                                ? ((CivilSite)result).ObjectId
                                : ObjectId.Null;
                        if (!id.IsNull)
                        {
                            transaction.Commit();
                            return id;
                        }
                    }
                    catch { }
                }
            }
            return ObjectId.Null;
        }

        private static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string requested)
        {
            if (database == null ||
                transaction == null ||
                string.IsNullOrWhiteSpace(requested))
                return ObjectId.Null;

            try
            {
                LayerTable table = transaction.GetObject(
                    database.LayerTableId,
                    OpenMode.ForRead,
                    false) as LayerTable;
                if (table == null) return ObjectId.Null;
                string name = requested.Trim();
                if (table.Has(name)) return table[name];

                table.UpgradeOpen();
                LayerTableRecord layer = new LayerTableRecord { Name = name };
                ObjectId id = table.Add(layer);
                transaction.AddNewlyCreatedDBObject(layer, true);
                return id;
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        internal static List<CivilObjectChoice> ReadSites(Document document)
        {
            var result = new List<CivilObjectChoice>
            {
                new CivilObjectChoice(ObjectId.Null, "<Siteless>", "Do not assign to a Civil 3D site")
            };
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return result;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId siteId in civilDocument.GetSiteIds())
                {
                    CivilSite site = transaction.GetObject(siteId, OpenMode.ForRead, false) as CivilSite;
                    if (site != null) result.Add(new CivilObjectChoice(siteId, SafeName(site), "Civil 3D site"));
                }
            }
            return result;
        }

        private static ObjectId ResolveFeatureLineColourStyle(
            Document document,
            CivilFeatureLine featureLine,
            int colourIndex,
            Transaction transaction)
        {
            if (document == null ||
                featureLine == null ||
                transaction == null ||
                colourIndex < 1 ||
                colourIndex > 255)
                return ObjectId.Null;

            // FeatureLine.StyleId is write-only in Civil 3D 2023. Reading it
            // through reflection always returns null, so resolve the current
            // style from the readable StyleName and the Civil style collection.
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            string currentName = ReadText(
                featureLine,
                "StyleName",
                string.Empty);
            ObjectId currentStyleId = ObjectId.Null;
            if (!string.IsNullOrWhiteSpace(currentName))
                currentStyleId = FindFeatureLineStyleId(
                    civilDocument,
                    currentName,
                    transaction);

            // Keep compatibility with builds that expose a readable style id
            // under an alternate property name.
            if (currentStyleId.IsNull)
                currentStyleId = ReadObjectIdProperty(
                    featureLine,
                    "FeatureLineStyleId");
            if (currentStyleId.IsNull) return ObjectId.Null;

            DBObject currentStyle = null;
            try
            {
                currentStyle = transaction.GetObject(
                    currentStyleId,
                    OpenMode.ForRead,
                false);
            }
            catch { }
            if (currentStyle == null) return ObjectId.Null;

            currentName = ReadText(
                currentStyle,
                "Name",
                currentName);
            if (string.IsNullOrWhiteSpace(currentName))
                return ObjectId.Null;
            string safeBase = new string(currentName
                .Where(character =>
                    char.IsLetterOrDigit(character) ||
                    character == '-' ||
                    character == '_')
                .ToArray());
            if (string.IsNullOrWhiteSpace(safeBase))
                safeBase = "Basic";
            if (safeBase.Length > 50)
                safeBase = safeBase.Substring(0, 50);

            string targetName = "CE-FL-ACI-" +
                colourIndex.ToString(CultureInfo.InvariantCulture) +
                "-" + safeBase;

            ObjectId existing = FindFeatureLineStyleId(
                civilDocument,
                targetName,
                transaction);
            if (!existing.IsNull)
            {
                ApplyFeatureLineStyleColour(
                    transaction,
                    existing,
                    colourIndex);
                return existing;
            }

            try
            {
                ObjectId created = ObjectId.Null;
                FeatureLineStyle typedStyle = currentStyle as FeatureLineStyle;
                if (typedStyle != null)
                {
                    // Use the Civil 3D API directly. Reflection can miss an
                    // inherited StyleBase method on some Civil 3D 2023 builds.
                    created = typedStyle.CopyAsSibling(targetName);
                }
                else
                {
                    MethodInfo copy = currentStyle.GetType().GetMethod(
                        "CopyAsSibling",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { typeof(string) },
                        null);
                    if (copy != null)
                    {
                        object result = copy.Invoke(
                            currentStyle,
                            new object[] { targetName });
                        created = result is ObjectId
                            ? (ObjectId)result
                            : ObjectId.Null;
                    }
                }

                if (created.IsNull) return ObjectId.Null;

                ApplyFeatureLineStyleColour(
                    transaction,
                    created,
                    colourIndex);
                return created;
            }
            catch
            {
                return FindFeatureLineStyleId(
                    civilDocument,
                    targetName,
                    transaction);
            }
        }

        private static ObjectId FindFeatureLineStyleId(
            CivilDocument civilDocument,
            string name,
            Transaction transaction)
        {
            if (civilDocument == null ||
                transaction == null ||
                string.IsNullOrWhiteSpace(name))
                return ObjectId.Null;

            object styles = ReadPropertyValue(
                civilDocument,
                "Styles");
            object collection = ReadPropertyValue(
                styles,
                "FeatureLineStyles");
            foreach (object value in CivilStyleDiscovery.Enumerate(collection))
            {
                ObjectId id = value is ObjectId
                    ? (ObjectId)value
                    : value is DBObject
                        ? ((DBObject)value).ObjectId
                        : ObjectId.Null;
                if (id.IsNull || id.IsErased) continue;
                try
                {
                    DBObject style = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false);
                    if (string.Equals(
                            ReadText(style, "Name", string.Empty),
                            name,
                            StringComparison.OrdinalIgnoreCase))
                        return id;
                }
                catch { }
            }
            return ObjectId.Null;
        }

        private static bool TryApplyTypedFeatureLineStyleColour(
            FeatureLineStyle style,
            Color colour)
        {
            if (style == null || colour == null) return false;
            bool changed = false;

            try
            {
                DisplayStyle plan = style.GetFeatureLineDisplayStylePlan();
                if (plan != null)
                {
                    plan.Color = colour;
                    plan.Visible = true;
                    DisableLayerColour(plan);
                    changed = true;
                }
            }
            catch { }

            try
            {
                DisplayStyle model = style.GetFeatureLineDisplayStyleModel();
                if (model != null)
                {
                    model.Color = colour;
                    model.Visible = true;
                    DisableLayerColour(model);
                    changed = true;
                }
            }
            catch { }

            try
            {
                // Civil 3D 2023 exposes GetDisplayStyleProfile with a
                // FeatureLineDisplayStyleProfileType argument. Invoke the
                // available overload(s) reflectively so this remains compatible
                // with the installed Civil 3D API build.
                foreach (MethodInfo method in style.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!string.Equals(
                            method.Name,
                            "GetDisplayStyleProfile",
                            StringComparison.Ordinal))
                        continue;

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 0)
                    {
                        DisplayStyle profile =
                            method.Invoke(style, null) as DisplayStyle;
                        if (profile != null)
                        {
                            profile.Color = colour;
                            profile.Visible = true;
                            DisableLayerColour(profile);
                            changed = true;
                        }
                    }
                    else if (parameters.Length == 1 &&
                             parameters[0].ParameterType.IsEnum)
                    {
                        foreach (object profileType in Enum.GetValues(
                            parameters[0].ParameterType))
                        {
                            try
                            {
                                DisplayStyle profile =
                                    method.Invoke(
                                        style,
                                        new[] { profileType }) as DisplayStyle;
                                if (profile != null)
                                {
                                    profile.Color = colour;
                                    profile.Visible = true;
                                    DisableLayerColour(profile);
                                    changed = true;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            return changed;
        }

        private static void DisableLayerColour(object display)
        {
            if (display == null) return;
            foreach (string propertyName in new[] { "UseLayerColor", "ByLayer", "UseLayerColour" })
            {
                try
                {
                    PropertyInfo property = display.GetType().GetProperty(
                        propertyName,
                        BindingFlags.Public | BindingFlags.Instance);
                    if (property != null &&
                        property.CanWrite &&
                        property.PropertyType == typeof(bool))
                        property.SetValue(display, false, null);
                }
                catch { }
            }
        }

        private static void ApplyFeatureLineStyleColour(
            Transaction transaction,
            ObjectId styleId,
            int colourIndex)
        {
            if (transaction == null || styleId.IsNull) return;
            DBObject style = null;
            try
            {
                style = transaction.GetObject(
                    styleId,
                    OpenMode.ForWrite,
                    false);
            }
            catch { }
            if (style == null) return;

            Color colour = Color.FromColorIndex(
                ColorMethod.ByAci,
                (short)colourIndex);

            FeatureLineStyle typedStyle = style as FeatureLineStyle;
            if (typedStyle != null)
                TryApplyTypedFeatureLineStyleColour(typedStyle, colour);

            // Run the write-back path even when the typed getters succeeded.
            // Some Civil 3D 2023 builds return detached DisplayStyle wrappers;
            // the wrapper must be committed through the parent style when the
            // corresponding setter is available.
            foreach (string methodName in new[]
            {
                "GetFeatureLineDisplayStylePlan",
                "GetFeatureLineDisplayStyleModel",
                "GetDisplayStyleProfile"
            })
            {
                try
                {
                    MethodInfo method = style.GetType().GetMethod(
                        methodName,
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        Type.EmptyTypes,
                        null);
                    object display = method == null
                        ? null
                        : method.Invoke(style, null);
                    if (display == null) continue;

                    PropertyInfo colourProperty = display.GetType().GetProperty(
                        "Color",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (colourProperty != null &&
                        colourProperty.CanWrite &&
                        colourProperty.PropertyType.IsInstanceOfType(colour))
                        colourProperty.SetValue(display, colour, null);

                    PropertyInfo colourIndexProperty = display.GetType().GetProperty(
                        "ColorIndex",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (colourIndexProperty != null && colourIndexProperty.CanWrite)
                    {
                        if (colourIndexProperty.PropertyType == typeof(short))
                            colourIndexProperty.SetValue(display, (short)colourIndex, null);
                        else if (colourIndexProperty.PropertyType == typeof(int))
                            colourIndexProperty.SetValue(display, colourIndex, null);
                    }

                    // Feature-line display styles may expose colour through a setter
                    // method rather than a writable property in Civil 3D 2023.
                    foreach (MethodInfo setter in display.GetType().GetMethods(
                        BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (setter.Name.IndexOf("SetColor", StringComparison.OrdinalIgnoreCase) < 0 &&
                            setter.Name.IndexOf("SetColour", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        ParameterInfo[] parameters = setter.GetParameters();
                        if (parameters.Length != 1) continue;
                        try
                        {
                            if (parameters[0].ParameterType.IsInstanceOfType(colour))
                                setter.Invoke(display, new object[] { colour });
                            else if (parameters[0].ParameterType == typeof(short))
                                setter.Invoke(display, new object[] { (short)colourIndex });
                            else if (parameters[0].ParameterType == typeof(int))
                                setter.Invoke(display, new object[] { colourIndex });
                        }
                        catch { }
                    }

                    foreach (string propertyName in new[] { "UseLayerColor", "ByLayer", "UseLayerColour" })
                    {
                        PropertyInfo property = display.GetType().GetProperty(
                            propertyName,
                            BindingFlags.Public | BindingFlags.Instance);
                        if (property != null && property.CanWrite &&
                            property.PropertyType == typeof(bool))
                        {
                            try { property.SetValue(display, false, null); } catch { }
                        }
                    }

                    PropertyInfo visibleProperty = display.GetType().GetProperty(
                        "Visible",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (visibleProperty != null &&
                        visibleProperty.CanWrite &&
                        visibleProperty.PropertyType == typeof(bool))
                        visibleProperty.SetValue(display, true, null);

                    // Some Civil 3D 2023 builds return a detached display-style
                    // wrapper. Commit the edited wrapper back to the parent style.
                    foreach (MethodInfo setter in style.GetType().GetMethods(
                        BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (setter.Name.IndexOf("SetFeatureLineDisplayStyle", StringComparison.OrdinalIgnoreCase) < 0 &&
                            setter.Name.IndexOf("SetDisplayStylePlan", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        ParameterInfo[] parameters = setter.GetParameters();
                        if (parameters.Length != 1 ||
                            !parameters[0].ParameterType.IsInstanceOfType(display))
                            continue;
                        try { setter.Invoke(style, new[] { display }); } catch { }
                    }
                }
                catch { }
            }
            try
            {
                Entity styleEntity = style as Entity;
                if (styleEntity != null) styleEntity.RecordGraphicsModified(true);
            }
            catch { }

        }

        private static ObjectId ReadObjectIdProperty(
            object target,
            string name)
        {
            object value = ReadPropertyValue(target, name);
            return value is ObjectId
                ? (ObjectId)value
                : ObjectId.Null;
        }

        private static bool TrySetFeatureLineStyleId(
            CivilFeatureLine featureLine,
            ObjectId styleId,
            Transaction transaction)
        {
            if (featureLine == null || styleId.IsNull || transaction == null)
                return false;

            string expectedStyleName = string.Empty;
            try
            {
                DBObject style = transaction.GetObject(
                    styleId,
                    OpenMode.ForRead,
                    false);
                expectedStyleName = ReadText(style, "Name", string.Empty);
            }
            catch { }
            if (string.IsNullOrWhiteSpace(expectedStyleName)) return false;

            try { featureLine.StyleId = styleId; } catch { }
            if (string.Equals(
                    ReadText(featureLine, "StyleName", string.Empty),
                    expectedStyleName,
                    StringComparison.OrdinalIgnoreCase))
                return true;

            // StyleName is a documented readable/writable fallback for builds
            // where the setter-only StyleId assignment does not refresh the
            // feature-line's cached style binding immediately.
            try { featureLine.StyleName = expectedStyleName; } catch { }
            return string.Equals(
                ReadText(featureLine, "StyleName", string.Empty),
                expectedStyleName,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool TrySetObjectIdProperty(
            object target,
            string name,
            ObjectId value)
        {
            if (target == null || value.IsNull) return false;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);
                if (property == null ||
                    !property.CanWrite ||
                    property.PropertyType != typeof(ObjectId))
                    return false;
                property.SetValue(target, value, null);
                return true;
            }
            catch { return false; }
        }

        private static object ReadPropertyValue(
            object target,
            string name)
        {
            if (target == null) return null;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.Instance);
                return property == null
                    ? null
                    : property.GetValue(target, null);
            }
            catch { return null; }
        }

        private static bool ApplySite(ObjectId featureLineId, ObjectId siteId)
        {
            if (featureLineId.IsNull || featureLineId.Database == null) return false;
            try
            {
                using (Transaction transaction = featureLineId.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine current = transaction.GetObject(
                        featureLineId, OpenMode.ForWrite, false) as CivilFeatureLine;
                    if (current == null) return false;
                    if (current.SiteId == siteId) return true;
                    if (TrySetObjectIdProperty(current, "SiteId", siteId))
                    {
                        current.RecordGraphicsModified(true);
                        transaction.Commit();
                    }
                }

                using (Transaction verify = featureLineId.Database.TransactionManager.StartTransaction())
                {
                    CivilFeatureLine current = verify.GetObject(
                        featureLineId, OpenMode.ForRead, false) as CivilFeatureLine;
                    if (current != null && current.SiteId == siteId) return true;
                }

                string methodName = siteId.IsNull ? "MoveToNoneSite" : "MoveToSite";
                foreach (MethodInfo method in typeof(CivilFeatureLine).GetMethods(
                    BindingFlags.Public | BindingFlags.Static))
                {
                    if (!string.Equals(method.Name, methodName,
                        StringComparison.OrdinalIgnoreCase)) continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != (siteId.IsNull ? 1 : 2)) continue;
                    object[] arguments = siteId.IsNull
                        ? new object[] { featureLineId }
                        : new object[] { featureLineId, siteId };
                    try
                    {
                        method.Invoke(null, arguments);
                        using (Transaction verify = featureLineId.Database.TransactionManager.StartTransaction())
                        {
                            CivilFeatureLine current = verify.GetObject(
                                featureLineId, OpenMode.ForRead, false) as CivilFeatureLine;
                            if (current != null && current.SiteId == siteId) return true;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        internal static CivilObjectChoice PickObject(string title, string subtitle, IList<CivilObjectChoice> choices)
        {
            if (choices == null || choices.Count == 0) return null;
            var window = new CivilObjectPickerWindow(title, subtitle, choices);
            AcApplication.ShowModalWindow(window);
            return window.Accepted ? window.SelectedChoice : null;
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

        private static string FormatStation(CivilAlignment alignment, double station)
        {
            if (alignment == null) return station.ToString("N3", CultureInfo.CurrentCulture);
            try { return alignment.GetStationStringWithEquations(station); }
            catch { return station.ToString("N3", CultureInfo.CurrentCulture); }
        }

        private static string SafeName(object value)
        {
            return ReadText(value, "Name", value == null ? string.Empty : value.GetType().Name);
        }

        private static string ReadText(object value, string propertyName, string fallback)
        {
            if (value == null) return fallback;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                object raw = property == null ? null : property.GetValue(value, null);
                string text = Convert.ToString(raw, CultureInfo.CurrentCulture);
                return string.IsNullOrWhiteSpace(text) ? fallback : text;
            }
            catch { return fallback; }
        }

        private static double ReadNumber(object value, params string[] propertyNames)
        {
            if (value == null) return 0.0;
            foreach (string propertyName in propertyNames)
            {
                try
                {
                    PropertyInfo property = value.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                    object raw = property == null ? null : property.GetValue(value, null);
                    if (raw == null) continue;
                    double number = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                    if (!double.IsNaN(number) && !double.IsInfinity(number)) return number;
                }
                catch { }
            }
            return 0.0;
        }

        private static bool PromptYesNo(Editor editor, string message, bool defaultValue)
        {
            var options = new PromptKeywordOptions(
                "\n" + message + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ")
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

        internal static ObjectId CreateCoordinateAnchor(
            Database database,
            Point3d point)
        {
            try
            {
                using (Transaction transaction =
                    database.TransactionManager.StartTransaction())
                {
                    BlockTableRecord currentSpace =
                        transaction.GetObject(
                            database.CurrentSpaceId,
                            OpenMode.ForWrite,
                            false) as BlockTableRecord;
                    if (currentSpace == null) return ObjectId.Null;
                    var anchor = new DBPoint(point);
                    anchor.SetDatabaseDefaults(database);
                    ObjectId anchorId = currentSpace.AppendEntity(anchor);
                    transaction.AddNewlyCreatedDBObject(anchor, true);
                    transaction.Commit();
                    return anchorId;
                }
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        private sealed class FeatureVertexWork
        {
            public FeatureVertexWork(
                ObjectId featureLineId,
                int vertexIndex,
                Point3d target,
                Point3d label,
                string pointName,
                string contents,
                string plain)
            {
                FeatureLineId = featureLineId;
                VertexIndex = vertexIndex;
                Target = target;
                Label = label;
                PointName = pointName;
                Contents = contents;
                Plain = plain;
            }

            public ObjectId FeatureLineId { get; private set; }
            public int VertexIndex { get; private set; }
            public Point3d Target { get; private set; }
            public Point3d Label { get; private set; }
            public string PointName { get; private set; }
            public string Contents { get; private set; }
            public string Plain { get; private set; }
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class CivilObjectChoice
    {
        public CivilObjectChoice(ObjectId objectId, string name, string context)
        {
            ObjectId = objectId;
            Name = name ?? string.Empty;
            Context = context ?? string.Empty;
        }

        public ObjectId ObjectId { get; }
        public string Name { get; }
        public string Context { get; }
        public string Display => string.IsNullOrWhiteSpace(Context) ? Name : Name + " — " + Context;
        public override string ToString() { return Display; }
    }

    internal sealed class CivilObjectPickerWindow : Window
    {
        private readonly ListBox _choices;
        public CivilObjectPickerWindow(string title, string subtitle, IEnumerable<CivilObjectChoice> choices)
        {
            Title = title;
            Width = 620;
            Height = 480;
            MinWidth = 460;
            MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel { Margin = new Thickness(16) };
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
            var select = new Button { Content = "Select", Width = 100, IsDefault = true };
            select.Click += delegate
            {
                SelectedChoice = _choices.SelectedItem as CivilObjectChoice;
                if (SelectedChoice == null) return;
                Accepted = true;
                Close();
            };
            buttons.Children.Add(select);

            var header = new TextBlock
            {
                Text = subtitle,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            _choices = new ListBox { ItemsSource = choices.ToList() };
            if (_choices.Items.Count > 0) _choices.SelectedIndex = 0;
            _choices.MouseDoubleClick += delegate { select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
            root.Children.Add(_choices);
        }

        public bool Accepted { get; private set; }
        public CivilObjectChoice SelectedChoice { get; private set; }
    }

    internal sealed class FeatureLineAppearanceWindow : Window
    {
        private readonly ComboBox _colour;
        private readonly ComboBox _site;
        private readonly TextBox _newSite;
        private readonly TextBox _layer;

        public FeatureLineAppearanceWindow(IEnumerable<CivilObjectChoice> sites)
        {
            Title = "CE Tools - Feature Line Colour, Layer and Site";
            Width = 560;
            Height = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var grid = new Grid { Margin = new Thickness(18) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int index = 0; index < 5; index++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = grid;

            AddLabel(grid, "AutoCAD colour", 0);
            List<FeatureLineColourChoice> colourChoices = CreateColourChoices();
            _colour = new ComboBox
            {
                ItemsSource = colourChoices,
                DisplayMemberPath = "Name",
                Margin = new Thickness(8),
                MinWidth = 280,
                IsEditable = false,
                IsTextSearchEnabled = true,
                ToolTip = "Choose an AutoCAD indexed colour (ACI 1–255)."
            };
            _colour.SelectedItem = colourChoices.FirstOrDefault(choice => choice.Index == 7);
            Grid.SetRow(_colour, 0);
            Grid.SetColumn(_colour, 1);
            grid.Children.Add(_colour);

            AddLabel(grid, "Civil 3D site", 1);
            _site = new ComboBox
            {
                ItemsSource = sites.ToList(),
                Margin = new Thickness(8),
                MinWidth = 280
            };
            if (_site.Items.Count > 0) _site.SelectedIndex = 0;
            Grid.SetRow(_site, 1);
            Grid.SetColumn(_site, 1);
            grid.Children.Add(_site);

            AddLabel(grid, "New site name (optional)", 2);
            _newSite = new TextBox { Margin = new Thickness(8), MinWidth = 280 };
            Grid.SetRow(_newSite, 2);
            Grid.SetColumn(_newSite, 1);
            grid.Children.Add(_newSite);

            AddLabel(grid, "Feature-line layer (optional)", 3);
            _layer = new TextBox { Margin = new Thickness(8), MinWidth = 280 };
            Grid.SetRow(_layer, 3);
            Grid.SetColumn(_layer, 1);
            grid.Children.Add(_layer);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8)
            };
            Grid.SetRow(buttons, 4);
            Grid.SetColumnSpan(buttons, 2);
            grid.Children.Add(buttons);

            var cancel = new Button
            {
                Content = "Cancel",
                Width = 90,
                Margin = new Thickness(8),
                IsCancel = true
            };
            cancel.Click += delegate { Close(); };
            buttons.Children.Add(cancel);

            var apply = new Button
            {
                Content = "Apply",
                Width = 100,
                Margin = new Thickness(8),
                IsDefault = true
            };
            apply.Click += delegate
            {
                FeatureLineColourChoice colour = _colour.SelectedItem as FeatureLineColourChoice;
                if (colour == null)
                {
                    MessageBox.Show(
                        "Choose an AutoCAD colour from the list.",
                        "CE Tools",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                ColourIndex = colour.Index;
                NewSiteName = (_newSite.Text ?? string.Empty).Trim();
                LayerName = (_layer.Text ?? string.Empty).Trim();
                CivilObjectChoice choice = _site.SelectedItem as CivilObjectChoice;
                SelectedSiteId = choice == null ? ObjectId.Null : choice.ObjectId;
                Accepted = true;
                Close();
            };
            buttons.Children.Add(apply);
        }

        public bool Accepted { get; private set; }
        public int ColourIndex { get; private set; }
        public ObjectId SelectedSiteId { get; set; }
        public string NewSiteName { get; private set; }
        public string LayerName { get; private set; }

        private static List<FeatureLineColourChoice> CreateColourChoices()
        {
            var choices = new List<FeatureLineColourChoice>();
            for (int index = 1; index <= 255; index++)
            {
                string name;
                switch (index)
                {
                    case 1: name = "Red"; break;
                    case 2: name = "Yellow"; break;
                    case 3: name = "Green"; break;
                    case 4: name = "Cyan"; break;
                    case 5: name = "Blue"; break;
                    case 6: name = "Magenta"; break;
                    case 7: name = "White / Black"; break;
                    case 8: name = "Dark gray"; break;
                    case 9: name = "Light gray"; break;
                    case 250: name = "Gray 1"; break;
                    case 251: name = "Gray 2"; break;
                    case 252: name = "Gray 3"; break;
                    case 253: name = "Gray 4"; break;
                    case 254: name = "Gray 5"; break;
                    case 255: name = "Gray 6"; break;
                    default: name = "ACI " + index.ToString(CultureInfo.InvariantCulture); break;
                }
                choices.Add(new FeatureLineColourChoice(index, name));
            }
            return choices;
        }

        private sealed class FeatureLineColourChoice
        {
            public FeatureLineColourChoice(int index, string name)
            {
                Index = index;
                Name = index.ToString(CultureInfo.InvariantCulture) + " — " + name;
            }

            public int Index { get; private set; }
            public string Name { get; private set; }
        }

        private static void AddLabel(Grid grid, string text, int row)
        {
            var label = new TextBlock
            {
                Text = text,
                Margin = new Thickness(8),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);
        }
    }
}
