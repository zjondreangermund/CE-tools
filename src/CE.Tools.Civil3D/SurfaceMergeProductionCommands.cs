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
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

[assembly: CommandClass(typeof(CETools.Civil3D.SurfaceMergeProductionCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Production surface outputs used by Road, Platform and Survey delivery.
    /// Corridor surfaces remain road-numbered (TOP-RD-n/BOTTOM-RD-n); this
    /// command creates the three named, repeatable Civil 3D surface outputs that
    /// downstream profiles, quantities and final checks can use:
    /// CE Top All, CE Bottom All and CE Final Surface.
    /// </summary>
    public sealed class SurfaceMergeProductionCommands
    {
        private const string TopOutput = "CE Top All";
        private const string BottomOutput = "CE Bottom All";
        private const string FinalOutput = "CE Final Surface";

        [CommandMethod("CE_TOOLS", "CE_ROADSURFACEMERGE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void MergeProductionSurfaces()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Merge Production Surfaces",
                "Create or refresh one named Civil 3D surface output. Road TOP-RD-n and BOTTOM-RD-n surfaces are merged without changing the numbered source surfaces. For CE Final Surface, select the natural-ground source and merge all or selected road TOP/grading surfaces.");
            model.AddChoice(
                "Output",
                "01 Output",
                "Output surface",
                TopOutput,
                "Choose the named output to create or refresh.",
                new[] { TopOutput, BottomOutput, FinalOutput });
            model.AddChoice(
                "Sources",
                "02 Final surface sources",
                "Road / grading sources",
                "All road TOP and grading surfaces",
                "Used only for CE Final Surface. Select the smaller source set when the drawing contains unrelated grading surfaces.",
                new[] { "All road TOP and grading surfaces", "Select road TOP / grading surfaces" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            string output = model.Text("Output");
            if (string.Equals(output, FinalOutput, StringComparison.OrdinalIgnoreCase))
            {
                MergeFinalSurface(document, model.Text("Sources"));
                return;
            }

            bool top = string.Equals(output, TopOutput, StringComparison.OrdinalIgnoreCase);
            MergeRoadSurfaces(document, top ? TopOutput : BottomOutput, top);
        }

        // Short aliases keep the command available to scripts and to older CE
        // production centres while the visible tabs expose one merged command.
        [CommandMethod("CE_TOOLS", "CE_ROADTOPSURFACEALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void MergeTopSurfaces()
        {
            Document document = ActiveDocument();
            if (document != null) MergeRoadSurfaces(document, TopOutput, true);
        }

        [CommandMethod("CE_TOOLS", "CE_ROADBOTTOMSURFACEALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void MergeBottomSurfaces()
        {
            Document document = ActiveDocument();
            if (document != null) MergeRoadSurfaces(document, BottomOutput, false);
        }

        [CommandMethod("CE_TOOLS", "CE_FINALSURFACEMERGE", CommandFlags.Modal | CommandFlags.Redraw)]
        public void MergeFinalSurfaceCommand()
        {
            Document document = ActiveDocument();
            if (document != null) MergeFinalSurface(document, "All road TOP and grading surfaces");
        }

        private static void MergeRoadSurfaces(Document document, string outputName, bool top)
        {
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (civil == null) return;

            List<SurfaceChoice> sources = ReadSurfaces(document, civil)
                .Where(item => IsRoadSurface(item.Name, top))
                .ToList();
            if (sources.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\n{0} cancelled. No {1} road surfaces named {2}-RD-n were found.",
                    outputName,
                    top ? "TOP" : "BOTTOM",
                    top ? "TOP" : "BOTTOM");
                return;
            }

            ObjectId styleId = sources[0].StyleId;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                {
                    EraseNamedOutput(document, civil, outputName);
                    MergeIntoNewSurface(document, civil, outputName, sources, styleId);
                }
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_ROADSURFACEMERGE complete. Output={0}; source {1} surfaces merged={2}.",
                    outputName,
                    top ? "TOP" : "BOTTOM",
                    sources.Count);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADSURFACEMERGE failed for {0}. {1}",
                    outputName,
                    exception.Message);
            }
        }

        private static void MergeFinalSurface(Document document, string sourceMode)
        {
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (civil == null) return;

            ObjectId naturalGround;
            if (!August12SurfaceSelectionPopup.TrySelectOne(
                    document,
                    "CE Tools - Final Surface Natural Ground",
                    "Select the natural-ground/base surface. It is copied first, then selected road TOP and grading surfaces are merged over it.",
                    "Natural-ground surface",
                    out naturalGround)) return;

            List<SurfaceChoice> available = ReadSurfaces(document, civil)
                .Where(item => item.Id != naturalGround &&
                               !IsNamedOutput(item.Name) &&
                               IsRoadTopOrGrading(item.Name))
                .ToList();
            if (available.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE Final Surface cancelled. No road TOP or grading surfaces are available after the natural-ground selection.");
                return;
            }

            List<SurfaceChoice> sources = available;
            if (string.Equals(sourceMode, "Select road TOP / grading surfaces", StringComparison.OrdinalIgnoreCase))
            {
                sources = PromptSurfaceSelection(document, "\nSelect road TOP / grading surfaces to merge into CE Final Surface: ");
                sources = sources
                    .Where(item => item.Id != naturalGround && !IsNamedOutput(item.Name) && IsRoadTopOrGrading(item.Name))
                    .ToList();
            }
            if (sources.Count == 0)
            {
                document.Editor.WriteMessage("\nCE Final Surface cancelled. No valid road TOP / grading sources were selected.");
                return;
            }

            SurfaceChoice ng = available.FirstOrDefault(item => item.Id == naturalGround) ??
                               ReadSurfaces(document, civil).FirstOrDefault(item => item.Id == naturalGround);
            ObjectId styleId = ng == null ? sources[0].StyleId : ng.StyleId;
            var mergeSources = new List<SurfaceChoice> { ng ?? new SurfaceChoice(naturalGround, "Natural Ground", styleId) };
            mergeSources.AddRange(sources);

            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                {
                    EraseNamedOutput(document, civil, FinalOutput);
                    MergeIntoNewSurface(document, civil, FinalOutput, mergeSources, styleId);
                }
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_ROADSURFACEMERGE complete. Output={0}; natural ground={1}; road TOP/grading sources merged={2}.",
                    FinalOutput,
                    ng == null ? naturalGround.Handle.ToString() : ng.Name,
                    sources.Count);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_ROADSURFACEMERGE failed for {0}. {1}", FinalOutput, exception.Message);
            }
        }

        private static void MergeIntoNewSurface(
            Document document,
            CivilDocument civil,
            string outputName,
            IList<SurfaceChoice> sources,
            ObjectId styleId)
        {
            if (sources == null || sources.Count == 0) throw new InvalidOperationException("No source surfaces were supplied.");

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                ObjectId outputId = styleId.IsNull
                    ? TinSurface.Create(document.Database, outputName)
                    : TinSurface.Create(outputName, styleId);
                TinSurface output = transaction.GetObject(outputId, OpenMode.ForWrite, false) as TinSurface;
                if (output == null) throw new InvalidOperationException("Civil 3D did not return the named TIN output surface.");
                if (!styleId.IsNull) output.StyleId = styleId;

                int merged = 0;
                foreach (SurfaceChoice source in sources)
                {
                    if (source == null || source.Id.IsNull || source.Id == outputId) continue;
                    DBObject sourceObject = transaction.GetObject(source.Id, OpenMode.ForRead, false);
                    if (sourceObject == null) continue;
                    if (!TryMergeTinSurface(output, sourceObject, source.Id))
                    {
                        throw new InvalidOperationException(
                            "The installed Civil 3D API exposed no supported TIN merge operation for source '" + source.Name + "'.");
                    }
                    merged++;
                }
                if (merged == 0) throw new InvalidOperationException("No source surface could be merged.");
                output.Rebuild();
                TrySetDescription(output, "CE Tools production merge: " + string.Join(", ", sources.Select(item => item.Name).ToArray()));
                transaction.Commit();
            }
        }

        private static bool TryMergeTinSurface(TinSurface target, DBObject source, ObjectId sourceId)
        {
            foreach (MethodInfo method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, "MergeTinSurface", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(method.Name, "MergeSurface", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(method.Name, "Merge", StringComparison.OrdinalIgnoreCase)) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1) continue;
                object argument;
                if (parameters[0].ParameterType == typeof(ObjectId)) argument = sourceId;
                else if (parameters[0].ParameterType.IsInstanceOfType(source)) argument = source;
                else continue;
                try
                {
                    method.Invoke(target, new[] { argument });
                    return true;
                }
                catch (TargetInvocationException exception)
                {
                    if (exception.InnerException != null &&
                        exception.InnerException.Message.IndexOf("not supported", StringComparison.OrdinalIgnoreCase) < 0)
                        throw exception.InnerException;
                }
            }

            // A small API fallback keeps the output useful on versions where the
            // merge method is internal: copy source TIN vertices into the output.
            TinSurface sourceTin = source as TinSurface;
            if (sourceTin == null) return false;
            var points = new Point3dCollection();
            foreach (TinSurfaceVertex vertex in sourceTin.Vertices)
                if (vertex != null && vertex.IsValid) points.Add(vertex.Location);
            if (points.Count == 0) return false;
            target.AddVertices(points);
            return true;
        }

        private static void EraseNamedOutput(Document document, CivilDocument civil, string name)
        {
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetSurfaceIds())
                {
                    CivilSurface surface = transaction.GetObject(id, OpenMode.ForWrite, false) as CivilSurface;
                    if (surface == null || !string.Equals(surface.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                    surface.Erase();
                }
                transaction.Commit();
            }
        }

        private static List<SurfaceChoice> PromptSurfaceSelection(Document document, string message)
        {
            var result = new List<SurfaceChoice>();
            PromptSelectionOptions options = new PromptSelectionOptions
            {
                MessageForAdding = message,
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = false
            };
            PromptSelectionResult selection = document.Editor.GetSelection(options);
            if (selection.Status != PromptStatus.OK) return result;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds())
                {
                    CivilSurface surface = transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
                    if (surface == null) continue;
                    result.Add(new SurfaceChoice(id, surface.Name, surface.StyleId));
                }
            }
            return result;
        }

        private static List<SurfaceChoice> ReadSurfaces(Document document, CivilDocument civil)
        {
            var result = new List<SurfaceChoice>();
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetSurfaceIds())
                {
                    CivilSurface surface = transaction.GetObject(id, OpenMode.ForRead, false) as CivilSurface;
                    if (surface == null) continue;
                    result.Add(new SurfaceChoice(id, surface.Name, surface.StyleId));
                }
            }
            return result;
        }

        private static bool IsRoadSurface(string name, bool top)
        {
            string value = (name ?? string.Empty).Trim();
            return value.StartsWith(top ? "TOP-RD-" : "BOTTOM-RD-", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRoadTopOrGrading(string name)
        {
            string value = (name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value) || IsNamedOutput(value)) return false;
            if (value.StartsWith("TOP-RD-", StringComparison.OrdinalIgnoreCase)) return true;
            string upper = value.ToUpperInvariant();
            return upper.Contains("GRAD") || upper.Contains("PLATFORM") || upper.Contains("PAD") ||
                   upper.Contains("DESIGN") || upper.Contains("FINISH") || upper.Contains("FGL");
        }

        private static bool IsNamedOutput(string name)
        {
            return string.Equals(name, TopOutput, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, BottomOutput, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, FinalOutput, StringComparison.OrdinalIgnoreCase);
        }

        private static void TrySetDescription(TinSurface surface, string description)
        {
            try { surface.Description = description ?? string.Empty; }
            catch { }
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }

        private sealed class SurfaceChoice
        {
            internal SurfaceChoice(ObjectId id, string name, ObjectId styleId)
            {
                Id = id;
                Name = string.IsNullOrWhiteSpace(name) ? id.Handle.ToString() : name.Trim();
                StyleId = styleId;
            }

            internal SurfaceChoice(ObjectId id, string name)
                : this(id, name, ObjectId.Null) { }

            internal ObjectId Id { get; private set; }
            internal string Name { get; private set; }
            internal ObjectId StyleId { get; private set; }
        }
    }
}
