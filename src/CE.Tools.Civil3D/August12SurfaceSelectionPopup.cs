using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Drawing-local CE popup for choosing Civil 3D surfaces. Surface choices
    /// are deliberately not persisted across drawings because ObjectIds and
    /// available surface names are drawing specific.
    /// </summary>
    internal static class August12SurfaceSelectionPopup
    {
        internal static bool TrySelectOne(
            Document document,
            string title,
            string note,
            string label,
            out ObjectId surfaceId)
        {
            surfaceId = ObjectId.Null;
            if (document == null) return false;

            List<SurfaceChoice> choices = ReadSurfaceChoices(document);
            if (choices.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE Tools: no Civil 3D surfaces were found in the active drawing.");
                return false;
            }

            var labels = choices.Select(item => item.Label).ToList();
            var model = new ProductionSettingsDialogModel(
                string.IsNullOrWhiteSpace(title)
                    ? "CE Tools - Select Surface"
                    : title,
                string.IsNullOrWhiteSpace(note)
                    ? "Choose a Civil 3D surface from the active drawing."
                    : note);
            model.AddChoice(
                "Surface",
                "01 Surface",
                string.IsNullOrWhiteSpace(label) ? "Surface" : label,
                labels[0],
                "Select from the Civil 3D surfaces in this drawing.",
                labels);

            var window = new ProductionSettingsWindow(model);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted) return false;

            SurfaceChoice selected = FindChoice(choices, model.Text("Surface"));
            if (selected == null)
            {
                document.Editor.WriteMessage(
                    "\nCE Tools: the selected surface is no longer available.");
                return false;
            }

            surfaceId = selected.ObjectId;
            return true;
        }

        internal static bool TrySelectPair(
            Document document,
            string title,
            string note,
            string firstLabel,
            string secondLabel,
            out ObjectId firstSurfaceId,
            out ObjectId secondSurfaceId)
        {
            firstSurfaceId = ObjectId.Null;
            secondSurfaceId = ObjectId.Null;
            if (document == null) return false;

            List<SurfaceChoice> choices = ReadSurfaceChoices(document);
            if (choices.Count < 2)
            {
                document.Editor.WriteMessage(
                    "\nCE Tools: at least two Civil 3D surfaces are required for comparison.");
                return false;
            }

            var labels = choices.Select(item => item.Label).ToList();
            var model = new ProductionSettingsDialogModel(
                string.IsNullOrWhiteSpace(title)
                    ? "CE Tools - Select Surfaces"
                    : title,
                string.IsNullOrWhiteSpace(note)
                    ? "Choose two different Civil 3D surfaces from the active drawing."
                    : note);
            model.AddChoice(
                "FirstSurface",
                "01 Surfaces",
                string.IsNullOrWhiteSpace(firstLabel) ? "Base surface" : firstLabel,
                labels[0],
                "Select from the Civil 3D surfaces in this drawing.",
                labels);
            model.AddChoice(
                "SecondSurface",
                "01 Surfaces",
                string.IsNullOrWhiteSpace(secondLabel) ? "Comparison surface" : secondLabel,
                labels[1],
                "Select a different Civil 3D surface for comparison.",
                labels);

            var window = new ProductionSettingsWindow(model);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted) return false;

            SurfaceChoice first = FindChoice(choices, model.Text("FirstSurface"));
            SurfaceChoice second = FindChoice(choices, model.Text("SecondSurface"));
            if (first == null || second == null)
            {
                document.Editor.WriteMessage(
                    "\nCE Tools: one or both selected surfaces are no longer available.");
                return false;
            }
            if (first.ObjectId == second.ObjectId)
            {
                System.Windows.MessageBox.Show(
                    "Select two different Civil 3D surfaces.",
                    "CE Tools - Surface Selection",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return false;
            }

            firstSurfaceId = first.ObjectId;
            secondSurfaceId = second.ObjectId;
            return true;
        }

        private static List<SurfaceChoice> ReadSurfaceChoices(Document document)
        {
            var result = new List<SurfaceChoice>();
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null) return result;

            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId objectId in civilDocument.GetSurfaceIds())
                {
                    CivilSurface surface = transaction.GetObject(
                        objectId,
                        OpenMode.ForRead,
                        false) as CivilSurface;
                    if (surface == null) continue;
                    string name = string.IsNullOrWhiteSpace(surface.Name)
                        ? "Surface"
                        : surface.Name.Trim();
                    result.Add(new SurfaceChoice(objectId, name));
                }
            }

            foreach (IGrouping<string, SurfaceChoice> duplicate in
                result.GroupBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                      .Where(group => group.Count() > 1))
            {
                foreach (SurfaceChoice item in duplicate)
                {
                    item.Label = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} [{1}]",
                        item.Label,
                        item.ObjectId.Handle.ToString());
                }
            }

            return result
                .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static SurfaceChoice FindChoice(
            IEnumerable<SurfaceChoice> choices,
            string label)
        {
            return choices.FirstOrDefault(item =>
                string.Equals(
                    item.Label,
                    label ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase));
        }

        private sealed class SurfaceChoice
        {
            internal SurfaceChoice(ObjectId objectId, string label)
            {
                ObjectId = objectId;
                Label = label ?? string.Empty;
            }

            internal ObjectId ObjectId { get; private set; }
            internal string Label { get; set; }
        }
    }
}
