using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace CETools.Civil3D
{
    /// <summary>
    /// Small, host-safe helpers shared by the August 20 field-stability layer.
    /// Surface choices are read before a production popup opens so commands do not
    /// interrupt the workflow with a second command-line entity prompt.
    /// </summary>
    internal static class August20SurfaceChoice
    {
        internal const string None = "<None>";

        internal static List<string> ReadSurfaceNames(Document document)
        {
            var names = new List<string>();
            if (document == null || document.Database == null) return names;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (civil == null) return names;

            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetSurfaceIds())
                {
                    CivilSurface surface;
                    try
                    {
                        surface = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as CivilSurface;
                    }
                    catch
                    {
                        continue;
                    }
                    if (surface != null && !string.IsNullOrWhiteSpace(surface.Name))
                        names.Add(surface.Name.Trim());
                }
            }

            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static ObjectId ResolveSurfaceId(Document document, string name)
        {
            if (document == null || document.Database == null ||
                string.IsNullOrWhiteSpace(name) ||
                string.Equals(name, None, StringComparison.OrdinalIgnoreCase))
                return ObjectId.Null;

            CivilDocument civil = CivilApplication.ActiveDocument;
            if (civil == null) return ObjectId.Null;
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in civil.GetSurfaceIds())
                {
                    CivilSurface surface;
                    try
                    {
                        surface = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as CivilSurface;
                    }
                    catch
                    {
                        continue;
                    }
                    if (surface != null && string.Equals(
                        surface.Name,
                        name,
                        StringComparison.OrdinalIgnoreCase))
                        return id;
                }
            }
            return ObjectId.Null;
        }
    }

    /// <summary>
    /// Applies user-selected presentation overrides to the CE annotative copy of
    /// a drawing dimension style.  The source style remains untouched.
    /// </summary>
    internal static class August20DimensionPresentation
    {
        internal const string FromSelectedStyle = "<From selected style>";

        internal static readonly string[] ArrowChoices =
        {
            FromSelectedStyle,
            "Closed filled",
            "Closed",
            "Closed blank",
            "Architectural tick",
            "Oblique",
            "Open",
            "Open 30",
            "Open 90",
            "Dot",
            "Dot small",
            "Dot blank",
            "Origin indicator",
            "Origin indicator 2",
            "Small blank dot",
            "Box",
            "Box filled",
            "Datum triangle",
            "Datum triangle filled",
            "Integral",
            "None"
        };

        internal static void ReadSizes(
            Database database,
            string styleName,
            out double arrowSize,
            out double textHeight)
        {
            arrowSize = 2.5;
            textHeight = 2.5;
            if (database == null || string.IsNullOrWhiteSpace(styleName)) return;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                DimStyleTable table = transaction.GetObject(
                    database.DimStyleTableId,
                    OpenMode.ForRead,
                    false) as DimStyleTable;
                if (table == null || !table.Has(styleName)) return;
                DimStyleTableRecord record = transaction.GetObject(
                    table[styleName],
                    OpenMode.ForRead,
                    false) as DimStyleTableRecord;
                if (record == null) return;
                try
                {
                    if (record.Dimasz > 0.0 && !double.IsNaN(record.Dimasz) && !double.IsInfinity(record.Dimasz))
                        arrowSize = record.Dimasz;
                }
                catch { }
                try
                {
                    if (record.Dimtxt > 0.0 && !double.IsNaN(record.Dimtxt) && !double.IsInfinity(record.Dimtxt))
                        textHeight = record.Dimtxt;
                }
                catch { }
            }
        }

        internal static void Apply(
            Database database,
            Transaction transaction,
            ObjectId styleId,
            string arrow1,
            string arrow2,
            double arrowSize,
            double textHeight)
        {
            if (database == null || transaction == null || styleId.IsNull) return;
            DimStyleTableRecord style = transaction.GetObject(
                styleId,
                OpenMode.ForWrite,
                false) as DimStyleTableRecord;
            if (style == null) return;

            ObjectId arrowId;
            bool separate = false;
            if (TryResolveArrowId(database, transaction, arrow1, true, out arrowId))
            {
                style.Dimblk1 = arrowId;
                separate = true;
            }
            if (TryResolveArrowId(database, transaction, arrow2, false, out arrowId))
            {
                style.Dimblk2 = arrowId;
                separate = true;
            }
            if (separate) style.Dimsah = true;

            if (!double.IsNaN(arrowSize) && !double.IsInfinity(arrowSize) && arrowSize > 0.0)
                style.Dimasz = Math.Max(arrowSize, 0.001);
            if (!double.IsNaN(textHeight) && !double.IsInfinity(textHeight) && textHeight > 0.0)
                style.Dimtxt = Math.Max(textHeight, 0.001);

            PaperAnnotationScale.SetAnnotative(style);
            try { style.Dimscale = 0.0; } catch { }
        }

        private static bool TryResolveArrowId(
            Database database,
            Transaction transaction,
            string choice,
            bool first,
            out ObjectId id)
        {
            id = ObjectId.Null;
            if (string.IsNullOrWhiteSpace(choice) ||
                string.Equals(choice, FromSelectedStyle, StringComparison.OrdinalIgnoreCase))
                return false;

            string token = ArrowToken(choice);
            if (token == null) return false;
            if (token.Length == 0)
            {
                id = ObjectId.Null;
                return true;
            }

            // If AutoCAD has already materialized the built-in arrow block, use it
            // directly without touching drawing system variables.
            try
            {
                BlockTable blocks = transaction.GetObject(
                    database.BlockTableId,
                    OpenMode.ForRead,
                    false) as BlockTable;
                if (blocks != null && blocks.Has(token))
                {
                    id = blocks[token];
                    return !id.IsNull;
                }
            }
            catch { }

            // Built-in DIMBLK names are created by AutoCAD on demand.  Briefly set
            // the appropriate drawing variable, capture the resulting ObjectId,
            // then restore the user's drawing variables immediately.
            string variable = first ? "DIMBLK1" : "DIMBLK2";
            object previousArrow = null;
            object previousSah = null;
            try
            {
                previousArrow = AcApplication.GetSystemVariable(variable);
                previousSah = AcApplication.GetSystemVariable("DIMSAH");
                AcApplication.SetSystemVariable("DIMSAH", 1);
                AcApplication.SetSystemVariable(variable, token);
                id = first ? database.Dimblk1 : database.Dimblk2;
            }
            catch
            {
                id = ObjectId.Null;
            }
            finally
            {
                if (previousArrow != null)
                {
                    try { AcApplication.SetSystemVariable(variable, previousArrow); }
                    catch { }
                }
                if (previousSah != null)
                {
                    try { AcApplication.SetSystemVariable("DIMSAH", previousSah); }
                    catch { }
                }
            }
            return !id.IsNull;
        }

        private static string ArrowToken(string choice)
        {
            if (string.Equals(choice, "Closed filled", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            if (string.Equals(choice, "Closed", StringComparison.OrdinalIgnoreCase)) return "_CLOSED";
            if (string.Equals(choice, "Closed blank", StringComparison.OrdinalIgnoreCase)) return "_CLOSEDBLANK";
            if (string.Equals(choice, "Architectural tick", StringComparison.OrdinalIgnoreCase)) return "_ARCHTICK";
            if (string.Equals(choice, "Oblique", StringComparison.OrdinalIgnoreCase)) return "_OBLIQUE";
            if (string.Equals(choice, "Open", StringComparison.OrdinalIgnoreCase)) return "_OPEN";
            if (string.Equals(choice, "Open 30", StringComparison.OrdinalIgnoreCase)) return "_OPEN30";
            if (string.Equals(choice, "Open 90", StringComparison.OrdinalIgnoreCase)) return "_OPEN90";
            if (string.Equals(choice, "Dot", StringComparison.OrdinalIgnoreCase)) return "_DOT";
            if (string.Equals(choice, "Dot small", StringComparison.OrdinalIgnoreCase)) return "_DOTSMALL";
            if (string.Equals(choice, "Dot blank", StringComparison.OrdinalIgnoreCase)) return "_DOTBLANK";
            if (string.Equals(choice, "Origin indicator", StringComparison.OrdinalIgnoreCase)) return "_ORIGIN";
            if (string.Equals(choice, "Origin indicator 2", StringComparison.OrdinalIgnoreCase)) return "_ORIGIN2";
            if (string.Equals(choice, "Small blank dot", StringComparison.OrdinalIgnoreCase)) return "_SMALL";
            if (string.Equals(choice, "Box", StringComparison.OrdinalIgnoreCase)) return "_BOXBLANK";
            if (string.Equals(choice, "Box filled", StringComparison.OrdinalIgnoreCase)) return "_BOXFILLED";
            if (string.Equals(choice, "Datum triangle", StringComparison.OrdinalIgnoreCase)) return "_DATUMBLANK";
            if (string.Equals(choice, "Datum triangle filled", StringComparison.OrdinalIgnoreCase)) return "_DATUMFILLED";
            if (string.Equals(choice, "Integral", StringComparison.OrdinalIgnoreCase)) return "_INTEGRAL";
            if (string.Equals(choice, "None", StringComparison.OrdinalIgnoreCase)) return "_NONE";
            return null;
        }
    }
}
