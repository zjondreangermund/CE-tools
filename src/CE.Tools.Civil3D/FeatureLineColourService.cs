using System;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;
using CivilFeatureLine = Autodesk.Civil.DatabaseServices.FeatureLine;

namespace CETools.Civil3D
{
    internal static class FeatureLineColourService
    {
        internal static ObjectId Prepare(
            Database database,
            CivilFeatureLine featureLine,
            int colourIndex,
            Transaction transaction)
        {
            if (colourIndex < 1 || colourIndex > 255)
                throw new ArgumentOutOfRangeException(nameof(colourIndex));

            CivilDocument civilDocument = CivilDocument.GetCivilDocument(database);
            FeatureLineStyleCollection styles = civilDocument.Styles.FeatureLineStyles;
            // FeatureLine.StyleId has no getter. StyleName does; do not reflect
            // the setter-only StyleId or StyleBase.Name overrides.
            string currentName = featureLine.StyleName;
            ObjectId currentId = !string.IsNullOrWhiteSpace(currentName) && styles.Contains(currentName)
                ? styles[currentName] : ObjectId.Null;
            FeatureLineStyle current = currentId.IsNull ? null :
                (FeatureLineStyle)transaction.GetObject(currentId, OpenMode.ForWrite, false);
            string baseName = current == null ? "Basic" : CivilStyleNames.Get(current);
            // Recolouring an already recoloured line must reuse its base name.
            while (baseName.StartsWith("CE-FL-ACI-", StringComparison.Ordinal))
            {
                int separator = baseName.IndexOf('-', "CE-FL-ACI-".Length);
                if (separator < 0) { baseName = "Basic"; break; }
                baseName = baseName.Substring(separator + 1);
            }
            string safeBase = new string(baseName.Where(character =>
                char.IsLetterOrDigit(character) || character == '-' || character == '_').ToArray());
            if (string.IsNullOrWhiteSpace(safeBase)) safeBase = "Basic";
            if (safeBase.Length > 50) safeBase = safeBase.Substring(0, 50);
            string targetName = "CE-FL-ACI-" + colourIndex.ToString(CultureInfo.InvariantCulture) + "-" + safeBase;
            ObjectId styleId = styles.Contains(targetName) ? styles[targetName] :
                current == null ? styles.Add(targetName) : current.CopyAsSibling(targetName);
            if (styleId.IsNull)
                throw new InvalidOperationException("Civil 3D did not create colour style '" + targetName + "'.");
            ApplyFeatureLineStyleColour(transaction, styleId, colourIndex);
            return styleId;
        }

        private static void ApplyFeatureLineStyleColour(
            Transaction transaction,
            ObjectId styleId,
            int colourIndex)
        {
            FeatureLineStyle style = (FeatureLineStyle)transaction.GetObject(
                styleId, OpenMode.ForWrite, false);
            Color colour = Color.FromColorIndex(ColorMethod.ByAci, (short)colourIndex);
            // These are live display components, not detached band collections.
            // The documented Color setter is sufficient; no guessed setters.
            DisplayStyle plan = style.GetFeatureLineDisplayStylePlan();
            plan.Color = colour;
            plan.Visible = true;
            DisplayStyle model = style.GetFeatureLineDisplayStyleModel();
            model.Color = colour;
            model.Visible = true;
            style.GetDisplayStyleProfile(FeatureLineDisplayStyleProfileType.FeatureLine).Color = colour;
        }

        internal static bool ReadDisplayColour(DBObject style, out int colourIndex)
        {
            colourIndex = -1;
            FeatureLineStyle typedStyle = style as FeatureLineStyle;
            if (typedStyle == null) return false;
            DisplayStyle plan = typedStyle.GetFeatureLineDisplayStylePlan();
            DisplayStyle model = typedStyle.GetFeatureLineDisplayStyleModel();
            if (!plan.Visible || !model.Visible ||
                plan.Color.ColorMethod != ColorMethod.ByAci ||
                model.Color.ColorMethod != ColorMethod.ByAci ||
                plan.Color.ColorIndex != model.Color.ColorIndex)
                return false;
            colourIndex = plan.Color.ColorIndex;
            return true;
        }

        internal static bool Assign(
            CivilFeatureLine featureLine,
            ObjectId styleId,
            Transaction transaction)
        {
            FeatureLineStyle style = (FeatureLineStyle)transaction.GetObject(
                styleId, OpenMode.ForRead, false);
            string expectedStyleName = CivilStyleNames.Get(style);
            if (string.IsNullOrWhiteSpace(expectedStyleName))
                throw new InvalidOperationException("The colour style has no readable name.");
            featureLine.StyleId = styleId;
            return string.Equals(featureLine.StyleName, expectedStyleName,
                StringComparison.OrdinalIgnoreCase);
        }

    }
}
