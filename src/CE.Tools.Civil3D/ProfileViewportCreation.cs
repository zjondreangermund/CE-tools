using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using CETools.Core;

namespace CETools.Civil3D
{
    public sealed partial class October03ViewportPlotProfileCommands
    {
        private static void AddViewportCreationSettings(ProductionSettingsDialogModel settings)
        {
            settings.AddChoice("ViewportMode", "02 Layout", "Viewport source", "Use existing viewports",
                "Keep existing viewport fitting, or create a new stack of profile viewports at a picked paper-space position. Existing viewports are kept when creating new ones.",
                new[] { "Use existing viewports", "Create profile viewports" });
            settings.AddChoice("ScaleMode", "02 Scale", "Viewport scale", "Automatic fit",
                "Automatic retains the current fit behaviour. Specified scale preserves 1:N; views that are too tall or lack enough width are reported as not fitted.",
                new[] { "Automatic fit", "Specified scale" });
            settings.AddPositiveDouble("ScaleDenominator", "02 Scale", "Scale denominator (1:N)", 1000,
                "For 1:1000 enter 1000. Used only for Specified scale.");
            settings.AddPositiveDouble("ModelUnitsPerMetre", "02 Scale", "Model units per metre", 1,
                "Use 1 for metre drawings or 1000 for millimetre drawings.");
            settings.AddChoice("PaperUnits", "02 Scale", "Layout paper units", "Millimetres",
                "Match the target layout's paper-space coordinate units. This also converts new viewport sizes from millimetres.",
                new[] { "Millimetres", "Inches" });
            settings.AddPositiveDouble("NewViewportWidth", "02 New viewports", "New viewport width (mm)", 300,
                "Used only when creating profile viewports. Long profiles can split across several viewports.");
            settings.AddPositiveDouble("NewViewportHeight", "02 New viewports", "New viewport height (mm)", 70,
                "Includes the profile bands and configured clearance. Increase this if the specified scale cannot fit the full height.");
            settings.AddPositiveDouble("NewViewportGap", "02 New viewports", "Gap between new viewports (mm)", 5,
                "Viewports are placed from top to bottom, starting at the picked top-left corner.");
        }

        private static double ReadFixedViewportScale(ProductionSettingsDialogModel settings)
        {
            if (!string.Equals(settings.Text("ScaleMode"), "Specified scale", StringComparison.OrdinalIgnoreCase)) return 0;
            return ProfileViewportMath.CustomScale(ReadViewportValue(settings, "ScaleDenominator"),
                ReadViewportValue(settings, "ModelUnitsPerMetre"), PaperUnitsPerMetre(settings));
        }

        private static double ReadViewportValue(ProductionSettingsDialogModel settings, string key)
        {
            double value;
            if (!ProductionSettingsDialogModel.TryDouble(settings.Text(key), out value) ||
                double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                throw new InvalidOperationException("Viewport dimensions, scale and unit conversion must be finite and greater than zero.");
            return value;
        }

        private static double PaperUnitsPerMetre(ProductionSettingsDialogModel settings)
        {
            return string.Equals(settings.Text("PaperUnits"), "Inches", StringComparison.OrdinalIgnoreCase)
                ? 1000.0 / 25.4 : 1000.0;
        }

        private static List<ViewportSlot> CreateProfileViewports(Document document, Transaction transaction,
            string layoutName, IList<ProfileSource> profiles, ProductionSettingsDialogModel settings,
            double horizontalClearance, double verticalClearance, bool splitLong, double fixedScale)
        {
            double paperConversion = PaperUnitsPerMetre(settings) / 1000;
            double width = ReadViewportValue(settings, "NewViewportWidth") * paperConversion;
            double height = ReadViewportValue(settings, "NewViewportHeight") * paperConversion;
            double gap = ReadViewportValue(settings, "NewViewportGap") * paperConversion;
            int count = 0;
            foreach (ProfileSource profile in profiles)
            {
                int required = ProfileViewportMath.RequiredSlots(profile.Max.X - profile.Min.X,
                    Math.Max(Tol, profile.Max.Y - profile.Min.Y + 2 * verticalClearance),
                    width, height, horizontalClearance, splitLong, fixedScale);
                if (required == 0)
                    throw new InvalidOperationException("Profile '" + profile.Name +
                        "' cannot fit at the specified scale. Increase the viewport size or enable long-branch splitting.");
                count += required;
                if (count > 1000) throw new InvalidOperationException("More than 1000 new viewports are required. Use larger viewports or fewer profiles.");
            }
            Layout layout = ReadLayouts(document.Database, transaction).FirstOrDefault(item =>
                !item.ModelType && string.Equals(item.LayoutName, layoutName, StringComparison.OrdinalIgnoreCase));
            if (layout == null) throw new InvalidOperationException("The selected layout no longer exists.");

            LayoutManager.Current.CurrentLayout = layoutName;
            document.Editor.SwitchToPaperSpace();
            PromptPointResult pick = document.Editor.GetPoint("\nPick top-left corner of the new profile viewport stack: ");
            if (pick.Status != PromptStatus.OK) return null;
            Point3d topLeft = pick.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
            BlockTableRecord paper = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
            LayerTable layers = (LayerTable)transaction.GetObject(document.Database.LayerTableId, OpenMode.ForRead);
            const string layerName = "CE-PROFILE-VIEWPORTS";
            ObjectId layerId;
            if (layers.Has(layerName)) layerId = layers[layerName];
            else
            {
                layers.UpgradeOpen();
                var layer = new LayerTableRecord { Name = layerName, IsPlottable = false };
                layerId = layers.Add(layer);
                transaction.AddNewlyCreatedDBObject(layer, true);
            }

            var slots = new List<ViewportSlot>();
            for (int index = 0; index < count; index++)
            {
                var viewport = new Viewport();
                viewport.SetDatabaseDefaults(document.Database);
                viewport.LayerId = layerId;
                viewport.CenterPoint = new Point3d(topLeft.X + width / 2,
                    topLeft.Y - height / 2 - index * (height + gap), 0);
                viewport.Width = width;
                viewport.Height = height;
                ObjectId id = paper.AppendEntity(viewport);
                transaction.AddNewlyCreatedDBObject(viewport, true);
                viewport.On = true;
                viewport.GridOn = false;
                slots.Add(new ViewportSlot { Id = id, Center = viewport.CenterPoint, Width = width, Height = height });
            }
            return slots;
        }
    }
}
