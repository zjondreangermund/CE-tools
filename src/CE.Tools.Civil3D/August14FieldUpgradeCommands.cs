using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

[assembly: CommandClass(typeof(CETools.Civil3D.August14FieldUpgradeCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Field-review upgrades requested on 14 August 2026. These commands reuse the
    /// existing CE link stores and production managers instead of introducing a
    /// parallel implementation of parking, feature-line or network logic.
    /// </summary>
    public sealed class August14FieldUpgradeCommands
    {
        [CommandMethod("CE_TOOLS", "CE_AUG14UPGRADES", CommandFlags.Modal)]
        public void UpgradeCentre()
        {
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - August 14 Production Upgrades",
                "Linked parking numbering, infill feature lines, flow correction, multi-network recovery, reusable styles, export and 2D fillet preparation.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Dynamic / linked parking numbers", "CE_PKNUMBERDYNAMIC", "Create annotative parking numbers linked to their bay geometry.", "01 Parking"),
                    new DisciplineWorkflowAction("Upgrade existing parking numbers", "CE_PKNUMBERUPGRADE", "Link existing P-number MText to the nearest selected/closed parking bay and make it annotative.", "01 Parking"),
                    new DisciplineWorkflowAction("Close feature lines for infill", "CE_FLCLOSEINFILL", "Create closed feature-line copies with endpoint vertices, common site and optional common surface elevations.", "02 Feature Lines"),
                    new DisciplineWorkflowAction("Check / correct flow to outlet", "CE_FLOWTOOUTLET", "Reverse selected open curves so their direction terminates at the low end or selected outlet.", "03 Flow"),
                    new DisciplineWorkflowAction("Reset multiple-network batch", "CE_NETWORKBATCHRESET", "Clear a stale CE network-from-object batch without touching completed network geometry.", "04 Networks"),
                    new DisciplineWorkflowAction("Discipline style presets / import", "CE_STYLEPRESETCENTRE", "Import project styles and save/apply independent discipline presets.", "05 Styles"),
                    new DisciplineWorkflowAction("Fillet with optional Z=0 preparation", "CE_FILLETFLAT", "Optionally flatten selected AutoCAD curves to elevation zero before launching FILLET.", "06 Geometry"),
                    new DisciplineWorkflowAction("CE / Civil object export centre", "CE_EXPORTCETOOLS", "Keep controlled Civil 3D objects or create an AutoCAD-object export through the host export workflow.", "07 Exchange")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_PKNUMBERDYNAMIC", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void DynamicParkingNumbers()
        {
            Document document = Active();
            if (document == null) return;
            Editor editor = document.Editor;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Dynamic Parking Numbers",
                "Create parking numbers as linked annotative MText. Moving or editing a linked bay keeps its number attached to the bay through CE's automatic parking refresh manager.");
            model.AddText("Prefix", "01 Numbering", "Prefix", "P", "Text placed before the parking number.");
            model.AddPositiveInteger("Start", "01 Numbering", "Start number", 1, "First parking number.");
            model.AddChoice("Order", "01 Numbering", "Numbering order", "Top to bottom, then left to right", "Choose how selected bay centres are ordered before numbering.", new[] { "Selection order", "Top to bottom, then left to right", "Left to right, then top to bottom" });
            model.AddPaperHeight("PaperHeight", "02 Annotation", "Paper text height", 2.5, "Absolute paper height in millimetres. CE Tools converts this to the active annotation scale.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK || selection.Value == null || selection.Value.Count == 0)
            {
                selection = editor.GetSelection(new PromptSelectionOptions
                {
                    MessageForAdding = "\nSelect CLOSED parking-bay polylines to number: ",
                    AllowDuplicates = false,
                    RejectObjectsFromNonCurrentSpace = true
                });
            }
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            string prefix = model.Text("Prefix") ?? "P";
            int start = model.Integer("Start", 1);
            string order = model.Text("Order") ?? "Selection order";
            double paperHeight = model.Double("PaperHeight", 2.5);
            int created = 0;
            int skipped = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                var bays = new List<ParkingBayCandidate>();
                int selectionIndex = 0;
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    Polyline bay = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                    if (bay == null || !bay.Closed || bay.NumberOfVertices < 3)
                    {
                        skipped++;
                        selectionIndex++;
                        continue;
                    }
                    Point3d centre;
                    if (!TryCentre(bay, out centre))
                    {
                        skipped++;
                        selectionIndex++;
                        continue;
                    }
                    bays.Add(new ParkingBayCandidate(id, centre, selectionIndex++));
                }

                if (string.Equals(order, "Top to bottom, then left to right", StringComparison.OrdinalIgnoreCase))
                    bays = bays.OrderByDescending(item => item.Centre.Y).ThenBy(item => item.Centre.X).ToList();
                else if (string.Equals(order, "Left to right, then top to bottom", StringComparison.OrdinalIgnoreCase))
                    bays = bays.OrderBy(item => item.Centre.X).ThenByDescending(item => item.Centre.Y).ToList();
                else
                    bays = bays.OrderBy(item => item.SelectionIndex).ToList();

                BlockTableRecord modelSpace = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (modelSpace == null) return;

                for (int index = 0; index < bays.Count; index++)
                {
                    ParkingBayCandidate candidate = bays[index];
                    Entity bay = transaction.GetObject(candidate.Id, OpenMode.ForRead, false) as Entity;
                    if (bay == null) continue;
                    int number = start + index;
                    var text = new MText
                    {
                        Contents = prefix + number.ToString(CultureInfo.InvariantCulture),
                        Location = candidate.Centre,
                        Attachment = AttachmentPoint.MiddleCenter,
                        LayerId = bay.LayerId,
                        TextHeight = PaperAnnotationScale.AnnotativeTextHeight(document.Database, paperHeight)
                    };
                    text.SetDatabaseDefaults(document.Database);
                    PaperAnnotationScale.SetAnnotative(text);
                    modelSpace.AppendEntity(text);
                    transaction.AddNewlyCreatedDBObject(text, true);
                    ParkingNumberLinkCommands.Link(transaction, text, bay);
                    ParkingNumberLinkStore.Link(transaction, bay, text, prefix, number);
                    created++;
                }
                transaction.Commit();
            }

            ParkingNumberAutoRefreshManager.QueueRefresh(document.Database);
            editor.Regen();
            editor.WriteMessage(
                "\nCE_PKNUMBERDYNAMIC complete. Linked annotative numbers created={0}; skipped non-closed/non-polyline objects={1}.",
                created,
                skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_PKNUMBERUPGRADE", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void UpgradeParkingNumbers()
        {
            Document document = Active();
            if (document == null) return;
            Editor editor = document.Editor;

            PromptSelectionResult labels = editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect existing parking-number MText to make dynamic/annotative: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (labels.Status != PromptStatus.OK || labels.Value == null) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Upgrade Parking Numbers",
                "CE Tools links every selected parking-number MText to the nearest closed bay in model space. Existing text content is preserved.");
            model.AddPaperHeight("PaperHeight", "Annotation", "Paper text height", 2.5, "Absolute paper height to apply to upgraded labels.");
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;
            double paperHeight = model.Double("PaperHeight", 2.5);

            int linked = 0;
            int skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                BlockTableRecord modelSpace = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                var bays = new List<ParkingBayCandidate>();
                if (modelSpace != null)
                {
                    foreach (ObjectId id in modelSpace)
                    {
                        Polyline bay = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                        if (bay == null || !bay.Closed || bay.NumberOfVertices < 3) continue;
                        Point3d centre;
                        if (TryCentre(bay, out centre)) bays.Add(new ParkingBayCandidate(id, centre, bays.Count));
                    }
                }

                foreach (ObjectId id in labels.Value.GetObjectIds())
                {
                    MText text = transaction.GetObject(id, OpenMode.ForWrite, false) as MText;
                    if (text == null || bays.Count == 0)
                    {
                        skipped++;
                        continue;
                    }
                    ParkingBayCandidate nearest = bays
                        .OrderBy(item => item.Centre.DistanceTo(text.Location))
                        .First();
                    Entity bay = transaction.GetObject(nearest.Id, OpenMode.ForRead, false) as Entity;
                    if (bay == null)
                    {
                        skipped++;
                        continue;
                    }
                    text.TextHeight = PaperAnnotationScale.AnnotativeTextHeight(document.Database, paperHeight);
                    PaperAnnotationScale.SetAnnotative(text);
                    ParkingNumberLinkCommands.Link(transaction, text, bay);
                    linked++;
                }
                transaction.Commit();
            }
            ParkingNumberAutoRefreshManager.QueueRefresh(document.Database);
            editor.Regen();
            editor.WriteMessage("\nCE_PKNUMBERUPGRADE complete. Labels linked={0}; skipped={1}.", linked, skipped);
        }

        [CommandMethod("CE_TOOLS", "CE_FLCLOSEINFILL", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CloseFeatureLinesForInfill()
        {
            Document document = Active();
            if (document == null) return;
            Editor editor = document.Editor;
            PromptSelectionResult selection = editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect OPEN feature lines to create closed infill-control copies: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            List<SurfaceChoice> surfaces = WorkflowRepairCommands.ReadSurfaceChoices(document);
            string[] surfaceNames = new[] { "<Keep feature-line elevations>" }
                .Concat(surfaces.Select(item => item.Name))
                .ToArray();
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Close Feature Lines for Infill",
                "A new closed feature line is created for every selected open feature line. Original vertices/endpoints are preserved, the closing segment joins the two open ends, and source lines remain untouched.");
            model.AddText("Suffix", "01 Output", "Name suffix", "-INFILL-CLOSED", "Suffix added to the source feature-line name.");
            model.AddChoice("Surface", "02 Elevations", "Common surface", surfaceNames[0], "Keep existing elevations, or assign every new closed feature line to elevations sampled from one selected Civil 3D surface.", surfaceNames);
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            SurfaceChoice selectedSurface = surfaces.FirstOrDefault(item =>
                string.Equals(item.Name, model.Text("Surface"), StringComparison.OrdinalIgnoreCase));
            string suffix = string.IsNullOrWhiteSpace(model.Text("Suffix")) ? "-INFILL-CLOSED" : model.Text("Suffix");
            int created = 0;
            int skipped = 0;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId sharedSiteId = ObjectId.Null;
                bool sharedSiteResolved = false;
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    CivilFeatureLine source = transaction.GetObject(id, OpenMode.ForRead, false) as CivilFeatureLine;
                    if (source == null || source.IsReferenceObject || source.Closed)
                    {
                        skipped++;
                        continue;
                    }
                    if (!sharedSiteResolved)
                    {
                        sharedSiteId = source.SiteId;
                        sharedSiteResolved = true;
                    }
                    else if (source.SiteId != sharedSiteId)
                    {
                        throw new InvalidOperationException("All selected feature lines must be on the same Civil 3D Site. Move them to one Site first, then rerun CE_FLCLOSEINFILL.");
                    }
                }

                BlockTableRecord modelSpace = transaction.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(document.Database),
                    OpenMode.ForWrite,
                    false) as BlockTableRecord;
                if (modelSpace == null) return;
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId itemId in modelSpace)
                {
                    CivilFeatureLine existing = transaction.GetObject(itemId, OpenMode.ForRead, false) as CivilFeatureLine;
                    if (existing != null && !string.IsNullOrWhiteSpace(existing.Name)) usedNames.Add(existing.Name);
                }

                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    CivilFeatureLine source = transaction.GetObject(id, OpenMode.ForRead, false) as CivilFeatureLine;
                    if (source == null || source.IsReferenceObject || source.Closed) continue;
                    Point3dCollection sourcePoints = source.GetPoints(FeatureLinePointType.AllPoints);
                    if (sourcePoints == null || sourcePoints.Count < 3)
                    {
                        skipped++;
                        continue;
                    }
                    var points = new Point3dCollection();
                    foreach (Point3d point in sourcePoints) points.Add(point);
                    var poly3d = new Polyline3d(Poly3dType.SimplePoly, points, true);
                    poly3d.SetDatabaseDefaults(document.Database);
                    poly3d.LayerId = source.LayerId;
                    modelSpace.AppendEntity(poly3d);
                    transaction.AddNewlyCreatedDBObject(poly3d, true);

                    string baseName = (string.IsNullOrWhiteSpace(source.Name) ? "CE-FEATURELINE" : source.Name) + suffix;
                    string name = UniqueName(baseName, usedNames);
                    ObjectId createdId = source.SiteId.IsNull
                        ? CivilFeatureLine.Create(name, poly3d.ObjectId)
                        : CivilFeatureLine.Create(name, poly3d.ObjectId, source.SiteId);
                    CivilFeatureLine result = transaction.GetObject(createdId, OpenMode.ForWrite, false) as CivilFeatureLine;
                    if (result == null) throw new InvalidOperationException("Civil 3D did not return the new closed feature line.");
                    result.LayerId = source.LayerId;
                    if (!string.IsNullOrWhiteSpace(source.StyleName)) result.StyleName = source.StyleName;
                    if (selectedSurface != null) result.AssignElevationsFromSurface(selectedSurface.ObjectId, false);
                    if (!poly3d.IsErased) poly3d.Erase();
                    created++;
                }
                transaction.Commit();
            }
            editor.Regen();
            editor.WriteMessage("\nCE_FLCLOSEINFILL complete. Closed feature lines created={0}; skipped={1}; surface={2}.", created, skipped, selectedSurface == null ? "source elevations" : selectedSurface.Name);
        }

        [CommandMethod("CE_TOOLS", "CE_FLOWTOOUTLET", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void CorrectFlowToOutlet()
        {
            Document document = Active();
            if (document == null) return;
            Editor editor = document.Editor;
            PromptSelectionResult selection = editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect OPEN AutoCAD curves whose flow direction must be checked: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;

            var mode = new PromptKeywordOptions("\nFlow target [LowZ/Outlet] <LowZ>: ", "LowZ Outlet") { AllowNone = true };
            PromptResult modeResult = editor.GetKeywords(mode);
            if (modeResult.Status != PromptStatus.OK && modeResult.Status != PromptStatus.None) return;
            bool useOutlet = string.Equals(modeResult.StringResult, "Outlet", StringComparison.OrdinalIgnoreCase);
            Point3d outlet = Point3d.Origin;
            if (useOutlet)
            {
                PromptPointResult point = editor.GetPoint("\nPick the downstream / outlet point: ");
                if (point.Status != PromptStatus.OK) return;
                outlet = point.Value.TransformBy(editor.CurrentUserCoordinateSystem);
            }

            int reversed = 0;
            int alreadyCorrect = 0;
            int skipped = 0;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    Curve curve = transaction.GetObject(id, OpenMode.ForWrite, false) as Curve;
                    if (curve == null || curve.Closed)
                    {
                        skipped++;
                        continue;
                    }
                    Point3d start;
                    Point3d end;
                    try
                    {
                        start = curve.StartPoint;
                        end = curve.EndPoint;
                    }
                    catch
                    {
                        skipped++;
                        continue;
                    }
                    bool shouldReverse = useOutlet
                        ? start.DistanceTo(outlet) < end.DistanceTo(outlet)
                        : start.Z < end.Z - 1e-8;
                    if (shouldReverse)
                    {
                        try
                        {
                            curve.ReverseCurve();
                            reversed++;
                        }
                        catch
                        {
                            skipped++;
                        }
                    }
                    else alreadyCorrect++;
                }
                transaction.Commit();
            }
            editor.Regen();
            try { new PolylineDirectionCommands().RefreshDirectionArrows(); } catch { }
            editor.WriteMessage("\nCE_FLOWTOOUTLET complete. Reversed={0}; already downstream={1}; skipped={2}; rule={3}.", reversed, alreadyCorrect, skipped, useOutlet ? "selected outlet" : "endpoint Z high-to-low");
        }

        [CommandMethod("CE_TOOLS", "CE_NETWORKBATCHRESET", CommandFlags.Modal)]
        public void ResetNetworkBatch()
        {
            Document document = Active();
            if (document == null) return;
            bool wasRunning = NetworkFromObjectBatchManager.IsRunning;
            try
            {
                MethodInfo stop = typeof(NetworkFromObjectBatchManager).GetMethod(
                    "Stop",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if (stop == null) throw new MissingMethodException("Network batch reset hook was not found.");
                stop.Invoke(null, new object[] { false });
                document.Editor.SetImpliedSelection(new ObjectId[0]);
                document.Editor.WriteMessage(wasRunning
                    ? "\nCE_NETWORKBATCHRESET complete. The stale/in-progress CE batch state was cleared. Existing Civil 3D networks and CE completion markers were not deleted."
                    : "\nCE_NETWORKBATCHRESET: no active CE batch was running; selection state was cleared.");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_NETWORKBATCHRESET failed: " + (exception.InnerException == null ? exception.Message : exception.InnerException.Message));
            }
        }

        [CommandMethod("CE_TOOLS", "CE_STYLEPRESETCENTRE", CommandFlags.Modal)]
        public void StylePresetCentre()
        {
            Document document = Active();
            if (document == null) return;
            DisciplineWorkflowDialogs.SelectAndRun(
                document,
                "CE Tools - Reusable Discipline Styles",
                "Use one project style library, import source drawing styles when dropdowns are empty, then save/activate separate style choices for Roads, Stormwater, Sewer, Water, Bulk Water, Platforms, Parking and Flood.",
                new List<DisciplineWorkflowAction>
                {
                    new DisciplineWorkflowAction("Import styles from project/source drawing", "CE_PROJECTSTYLEIMPORT", "Import Civil 3D styles into the active drawing before opening discipline settings.", "01 Import"),
                    new DisciplineWorkflowAction("Project Style Centre", "CE_PROJECTSTYLES", "Select the current discipline's style choices from installed drawing styles.", "02 Select"),
                    new DisciplineWorkflowAction("Save / activate discipline preset", "CE_DISCIPLINESTYLEPRESETS", "Copy current selections to a discipline or activate a previously saved discipline preset.", "03 Presets"),
                    new DisciplineWorkflowAction("Review all discipline presets", "CE_DISCIPLINESTYLEINFO", "Review which disciplines already have saved reusable choices.", "04 Review")
                });
        }

        [CommandMethod("CE_TOOLS", "CE_FILLETFLAT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void FilletFlat()
        {
            Document document = Active();
            if (document == null) return;
            Editor editor = document.Editor;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Fillet Preparation",
                "Use this when AutoCAD FILLET refuses objects because their Z/elevations differ. CE Tools can flatten supported AutoCAD curve geometry first, then launches the native FILLET command.");
            model.AddChoice("Flatten", "Preparation", "Set supported object elevations to zero first", "Yes", "Flatten Line, Arc, 2D Polyline and 3D Polyline geometry before FILLET. Civil 3D feature lines are not altered by this helper.", new[] { "Yes", "No" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            PromptSelectionResult selection = editor.GetSelection(new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect the AutoCAD curves to prepare for FILLET: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            });
            if (selection.Status != PromptStatus.OK || selection.Value == null) return;
            ObjectId[] ids = selection.Value.GetObjectIds().Where(id => !id.IsNull && !id.IsErased).Distinct().ToArray();
            int flattened = 0;
            int skipped = 0;

            if (string.Equals(model.Text("Flatten"), "Yes", StringComparison.OrdinalIgnoreCase))
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in ids)
                    {
                        DBObject value = transaction.GetObject(id, OpenMode.ForWrite, false);
                        if (Flatten(value, transaction)) flattened++;
                        else skipped++;
                    }
                    transaction.Commit();
                }
            }
            editor.SetImpliedSelection(ids);
            editor.WriteMessage("\nCE_FILLETFLAT prepared. Flattened={0}; unsupported/skipped={1}. Native FILLET is starting; select the required pair(s).", flattened, skipped);
            document.SendStringToExecute("_.FILLET ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_EXPORTCETOOLS", CommandFlags.Modal)]
        public void ExportCeTools()
        {
            Document document = Active();
            if (document == null) return;
            var model = new ProductionSettingsDialogModel(
                "CE Tools - Civil / AutoCAD Export",
                "Choose whether the deliverable should retain Civil 3D/CE-controlled objects or be converted by Civil 3D's native Export Civil 3D Drawing workflow to standard AutoCAD-compatible objects.");
            model.AddChoice("Mode", "Export", "Output mode", "Keep controlled Civil 3D objects", "Keeping controlled objects preserves CE dynamic relationships. AutoCAD-object export creates a downstream deliverable and does not modify the current source drawing.", new[] { "Keep controlled Civil 3D objects", "Export as AutoCAD objects" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            if (string.Equals(model.Text("Mode"), "Keep controlled Civil 3D objects", StringComparison.OrdinalIgnoreCase))
            {
                document.Editor.WriteMessage("\nCE_EXPORTCETOOLS: controlled-object mode selected. Use SAVEAS to create the deliverable copy; CE links and Civil 3D objects remain live.");
                document.SendStringToExecute("_.SAVEAS ", true, false, true);
                return;
            }

            document.Editor.WriteMessage("\nCE_EXPORTCETOOLS: starting Civil 3D's native Export Civil 3D Drawing workflow. The current CE-controlled source drawing remains unchanged; choose the required AutoCAD target version in the export dialog.");
            // Civil 3D exposes this host command in English installations. Using
            // ExecuteInCommandContext keeps this CE command free of object-by-object
            // explode logic that could silently lose labels or proxy geometry.
            document.SendStringToExecute("-EXPORTC3DDRAWING ", true, false, true);
        }

        private static bool Flatten(DBObject value, Transaction transaction)
        {
            Line line = value as Line;
            if (line != null)
            {
                line.StartPoint = Flat(line.StartPoint);
                line.EndPoint = Flat(line.EndPoint);
                return true;
            }
            Arc arc = value as Arc;
            if (arc != null)
            {
                if (Math.Abs(arc.Normal.X) > 1e-8 || Math.Abs(arc.Normal.Y) > 1e-8) return false;
                arc.Center = Flat(arc.Center);
                return true;
            }
            Polyline polyline = value as Polyline;
            if (polyline != null)
            {
                polyline.Elevation = 0.0;
                return true;
            }
            Polyline3d polyline3d = value as Polyline3d;
            if (polyline3d != null)
            {
                foreach (ObjectId vertexId in polyline3d)
                {
                    PolylineVertex3d vertex = transaction.GetObject(vertexId, OpenMode.ForWrite, false) as PolylineVertex3d;
                    if (vertex != null) vertex.Position = Flat(vertex.Position);
                }
                return true;
            }
            return false;
        }

        private static Point3d Flat(Point3d point)
        {
            return new Point3d(point.X, point.Y, 0.0);
        }

        private static bool TryCentre(Entity entity, out Point3d centre)
        {
            centre = Point3d.Origin;
            try
            {
                Extents3d extents = entity.GeometricExtents;
                centre = new Point3d(
                    (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
                    (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5,
                    (extents.MinPoint.Z + extents.MaxPoint.Z) * 0.5);
                return true;
            }
            catch { return false; }
        }

        private static string UniqueName(string requested, ISet<string> names)
        {
            string baseName = string.IsNullOrWhiteSpace(requested) ? "CE-INFILL-FL" : requested.Trim();
            string candidate = baseName;
            int suffix = 2;
            while (!names.Add(candidate))
            {
                candidate = baseName + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")";
                suffix++;
            }
            return candidate;
        }

        private static Document Active()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class ParkingBayCandidate
        {
            internal readonly ObjectId Id;
            internal readonly Point3d Centre;
            internal readonly int SelectionIndex;
            internal ParkingBayCandidate(ObjectId id, Point3d centre, int selectionIndex)
            {
                Id = id;
                Centre = centre;
                SelectionIndex = selectionIndex;
            }
        }
    }
}
