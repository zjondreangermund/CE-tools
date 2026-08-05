#!/usr/bin/env python3
"""Apply sewer style/runtime, annotation height, project popup and drawing-register fixes."""

from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "CE.Tools.Civil3D"


def read(name: str) -> str:
    return (SRC / name).read_text(encoding="utf-8-sig")


def write(name: str, text: str) -> None:
    (SRC / name).write_text(text, encoding="utf-8")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise RuntimeError(f"Missing replacement marker: {label}")
    return text.replace(old, new, 1)


def replace_regex(text: str, pattern: str, replacement: str, label: str, flags=0) -> str:
    updated, count = re.subn(pattern, replacement, text, count=1, flags=flags)
    if count != 1:
        raise RuntimeError(f"Expected one regex replacement for {label}; found {count}")
    return updated


# ---------------------------------------------------------------------------
# CivilStyleCatalogV2: category-first style reading/resolution that never
# dereferences Civil 3D collection properties at the command call site.
# ---------------------------------------------------------------------------
style = read("CivilStyleCatalogV2.cs")
marker = "        internal static IList<object> Enumerate(object collection)\n"
insert = r'''        internal static IList<string> ReadNames(
            Database database,
            CivilDocument civilDocument,
            string category)
        {
            var names = new List<string>();
            if (database == null || civilDocument == null ||
                string.IsNullOrWhiteSpace(category))
                return names;

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ReadCategoryObjectIds(
                    database,
                    civilDocument,
                    category,
                    transaction))
                {
                    StyleBase item = OpenStyle(id, transaction);
                    if (item != null &&
                        !string.IsNullOrWhiteSpace(item.Name) &&
                        !LooksLikeRuntimeClassName(item.Name))
                        names.Add(item.Name.Trim());
                }
            }

            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        internal static ObjectId ResolveStyleId(
            Database database,
            CivilDocument civilDocument,
            string category,
            string requested,
            Transaction transaction,
            out string actualName)
        {
            actualName = string.Empty;
            if (database == null || civilDocument == null || transaction == null)
                throw new InvalidOperationException(
                    "The active Civil 3D drawing is unavailable while resolving " + category + ".");

            IList<ObjectId> ids = ReadCategoryObjectIds(
                database,
                civilDocument,
                category,
                transaction);
            if (ids.Count == 0)
                throw new InvalidOperationException(
                    "The drawing contains no compatible " + category + ". Import the approved source styles first.");

            bool useDefault = string.IsNullOrWhiteSpace(requested) ||
                string.Equals(
                    requested,
                    DrawingDefault,
                    StringComparison.OrdinalIgnoreCase);
            ObjectId first = ObjectId.Null;
            string firstName = string.Empty;
            foreach (ObjectId id in ids)
            {
                StyleBase item = OpenStyle(id, transaction);
                if (item == null || string.IsNullOrWhiteSpace(item.Name)) continue;
                string name = item.Name.Trim();
                if (LooksLikeRuntimeClassName(name)) continue;
                if (first.IsNull)
                {
                    first = id;
                    firstName = name;
                }
                if (!useDefault && string.Equals(
                        name,
                        requested.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    actualName = name;
                    return id;
                }
            }

            if (useDefault && !first.IsNull)
            {
                actualName = firstName;
                return first;
            }

            throw new InvalidOperationException(
                "The selected " + category + " '" + requested +
                "' is no longer available in this drawing. Reopen the settings popup and select an installed style.");
        }

        private static IList<ObjectId> ReadCategoryObjectIds(
            Database database,
            CivilDocument civilDocument,
            string category,
            Transaction transaction)
        {
            var result = new HashSet<ObjectId>();
            string[] paths;
            if (KnownPaths.TryGetValue(category ?? string.Empty, out paths))
            {
                object stylesRoot = ReadProperty(civilDocument, "Styles");
                foreach (string path in paths)
                {
                    object collection = ReadPropertyPath(stylesRoot, path);
                    if (collection == null) continue;
                    CollectStyleIds(
                        collection,
                        transaction,
                        0,
                        new HashSet<object>(ReferenceComparer.Instance),
                        result);
                }
            }

            // Civil 3D 2023 can expose a collection property whose metadata exists
            // but whose getter is unavailable in the current host. Fall back to
            // the real StyleBase records stored in the DWG dictionaries instead
            // of invoking that missing getter.
            if (result.Count == 0 && database != null)
            {
                ScanStyleDictionary(
                    database.NamedObjectsDictionaryId,
                    string.Empty,
                    0,
                    category,
                    transaction,
                    new HashSet<ObjectId>(),
                    result);
            }

            return result
                .Where(id => !id.IsNull && !id.IsErased)
                .OrderBy(id => id.Handle.Value)
                .ToList();
        }

        private static void ScanStyleDictionary(
            ObjectId dictionaryId,
            string path,
            int depth,
            string category,
            Transaction transaction,
            ISet<ObjectId> visited,
            ISet<ObjectId> result)
        {
            if (dictionaryId.IsNull || depth > 16 || visited.Contains(dictionaryId))
                return;
            visited.Add(dictionaryId);

            DBDictionary dictionary;
            try
            {
                dictionary = transaction.GetObject(
                    dictionaryId,
                    OpenMode.ForRead,
                    false) as DBDictionary;
            }
            catch
            {
                return;
            }
            if (dictionary == null) return;

            foreach (DBDictionaryEntry entry in dictionary)
            {
                string childPath = string.IsNullOrWhiteSpace(path)
                    ? entry.Key
                    : path + "." + entry.Key;
                DBObject value;
                try
                {
                    value = transaction.GetObject(
                        entry.Value,
                        OpenMode.ForRead,
                        false);
                }
                catch
                {
                    continue;
                }

                DBDictionary child = value as DBDictionary;
                if (child != null)
                {
                    ScanStyleDictionary(
                        child.ObjectId,
                        childPath,
                        depth + 1,
                        category,
                        transaction,
                        visited,
                        result);
                    continue;
                }

                StyleBase item = value as StyleBase;
                if (item == null || string.IsNullOrWhiteSpace(item.Name)) continue;
                string mapped = MapCategory(
                    childPath + "." + value.GetType().Name);
                if (string.Equals(mapped, category, StringComparison.OrdinalIgnoreCase))
                    result.Add(item.ObjectId);
            }
        }

        private static string MapCategory(string source)
        {
            string value = (source ?? string.Empty)
                .Replace("_", string.Empty)
                .Replace(" ", string.Empty)
                .ToUpperInvariant();
            if (value.Contains("PROFILEVIEW") && value.Contains("BAND")) return "Profile View Band Set Style";
            if (value.Contains("SECTIONVIEW") && value.Contains("BAND")) return "Section View Band Set Style";
            if (value.Contains("ALIGNMENT") && value.Contains("LABELSET")) return "Alignment Label Set Style";
            if (value.Contains("PROFILE") && value.Contains("LABELSET")) return "Profile Label Set Style";
            if (value.Contains("SECTION") && value.Contains("LABELSET")) return "Section Label Set Style";
            if (value.Contains("STRUCTURE") && value.Contains("RULE")) return "Structure Rule Set";
            if (value.Contains("PIPE") && value.Contains("RULE")) return "Pipe Rule Set";
            if (value.Contains("STRUCTURE") && value.Contains("LABEL")) return "Structure Label Style";
            if (value.Contains("PRESSURE") && value.Contains("PIPE") && value.Contains("LABEL")) return "Pressure Pipe Label Style";
            if (value.Contains("PIPE") && value.Contains("LABEL")) return "Pipe Label Style";
            if (value.Contains("PROFILEVIEW")) return "Profile View Style";
            if (value.Contains("PROFILE") && value.Contains("LABEL")) return "Profile Label Style";
            if (value.Contains("PROFILE")) return "Profile Style";
            if (value.Contains("ALIGNMENT") && value.Contains("LABEL")) return "Alignment Label Style";
            if (value.Contains("ALIGNMENT")) return "Alignment Style";
            if (value.Contains("STRUCTURE")) return "Structure Style";
            if (value.Contains("PIPE")) return "Pipe Style";
            if (value.Contains("CODESET")) return "Code Set Style";
            if (value.Contains("ASSEMBLY")) return "Assembly Style";
            if (value.Contains("CORRIDOR")) return "Corridor Style";
            if (value.Contains("SURFACE")) return "Surface Style";
            if (value.Contains("POINT")) return "Point Style";
            return string.Empty;
        }

'''
style = replace_once(style, marker, insert + marker, "CivilStyleCatalogV2 category resolver")
write("CivilStyleCatalogV2.cs", style)


# ---------------------------------------------------------------------------
# Sewer alignment: use the safe category resolver, never a direct getter or
# reflected Name getter on a questionable Civil collection wrapper.
# ---------------------------------------------------------------------------
align = read("SewerBranchAlignmentCommands.cs")
old = '''                ObjectId alignmentStyleId = ResolveStyleId(
                    civilDocument.Styles.AlignmentStyles,
                    productionSettings.AlignmentStyle,
                    "alignment style",
                    transaction);
                ObjectId labelSetStyleId = ResolveStyleId(
                    civilDocument.Styles.LabelSetStyles.AlignmentLabelSetStyles,
                    productionSettings.AlignmentLabelSetStyle,
                    "alignment label-set style",
                    transaction);'''
new = '''                string alignmentStyleName;
                ObjectId alignmentStyleId = CivilStyleCatalogV2.ResolveStyleId(
                    database,
                    civilDocument,
                    "Alignment Style",
                    productionSettings.AlignmentStyle,
                    transaction,
                    out alignmentStyleName);
                string labelSetStyleName;
                ObjectId labelSetStyleId = CivilStyleCatalogV2.ResolveStyleId(
                    database,
                    civilDocument,
                    "Alignment Label Set Style",
                    productionSettings.AlignmentLabelSetStyle,
                    transaction,
                    out labelSetStyleName);'''
align = replace_once(align, old, new, "sewer alignment style resolution")
align = replace_regex(
    align,
    r'''\n        private static ObjectId ResolveStyleId\(\n            IEnumerable<ObjectId> styleIds,.*?\n        private static ObjectId AddSourcePolyline\(''',
    '\n        private static ObjectId AddSourcePolyline(',
    "remove unsafe sewer alignment resolver",
    flags=re.S)
write("SewerBranchAlignmentCommands.cs", align)


# ---------------------------------------------------------------------------
# Sewer settings/profile/format: callers identify a category and let the safe
# resolver obtain the collection or dictionary fallback.
# ---------------------------------------------------------------------------
sewer = read("SewerProductionCommands.cs")
replacements = {
'''CivilStyleCatalogV2.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.AlignmentStyles, "Alignment Style")''':
'''CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Alignment Style")''',
'''CivilStyleCatalogV2.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.LabelSetStyles.AlignmentLabelSetStyles, "Alignment Label Set Style")''':
'''CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Alignment Label Set Style")''',
'''CivilStyleCatalogV2.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.ProfileStyles, "Profile Style")''':
'''CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Profile Style")''',
'''CivilStyleCatalogV2.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.LabelSetStyles.ProfileLabelSetStyles, "Profile Label Set Style")''':
'''CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Profile Label Set Style")''',
'''CivilStyleCatalogV2.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.ProfileViewStyles, "Profile View Style")''':
'''CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Profile View Style")''',
'''CivilStyleCatalogV2.ReadNames(document.Database, civilDocument == null ? null : (object)civilDocument.Styles.ProfileViewBandSetStyles, "Profile View Band Set Style")''':
'''CivilStyleCatalogV2.ReadNames(document.Database, civilDocument, "Profile View Band Set Style")''',
}
for old_text, new_text in replacements.items():
    sewer = replace_once(sewer, old_text, new_text, "safe sewer settings catalogue")

sewer = replace_once(
    sewer,
    '''            settings.LabelHeight = model.Double("LabelHeight", settings.LabelHeight);''',
    '''            settings.LabelHeight = PaperAnnotationScale.NormalizeConfiguredPaperHeight(
                model.Double("LabelHeight", settings.LabelHeight));''',
    "normalize sewer paper height")

old_format = '''                    string actualStyle;
                    ObjectId styleId = ResolveStyleId(
                        civilDocument.Styles.AlignmentStyles,
                        settings.AlignmentStyle,
                        "alignment style",
                        transaction,
                        out actualStyle);'''
new_format = '''                    string actualStyle;
                    ObjectId styleId = CivilStyleCatalogV2.ResolveStyleId(
                        database,
                        civilDocument,
                        "Alignment Style",
                        settings.AlignmentStyle,
                        transaction,
                        out actualStyle);'''
sewer = replace_once(sewer, old_format, new_format, "safe sewer format style")

profile_blocks = [
('''                ObjectId profileStyleId = ResolveStyleId(
                    civilDocument.Styles.ProfileStyles,
                    settings.ProfileStyle,
                    "profile style",
                    transaction,
                    out profileStyleName);''',
 '''                ObjectId profileStyleId = CivilStyleCatalogV2.ResolveStyleId(
                    database,
                    civilDocument,
                    "Profile Style",
                    settings.ProfileStyle,
                    transaction,
                    out profileStyleName);'''),
('''                ObjectId profileLabelId = ResolveStyleId(
                    civilDocument.Styles.LabelSetStyles.ProfileLabelSetStyles,
                    settings.ProfileLabelSetStyle,
                    "profile label-set style",
                    transaction,
                    out profileLabelName);''',
 '''                ObjectId profileLabelId = CivilStyleCatalogV2.ResolveStyleId(
                    database,
                    civilDocument,
                    "Profile Label Set Style",
                    settings.ProfileLabelSetStyle,
                    transaction,
                    out profileLabelName);'''),
('''                ObjectId viewStyleId = ResolveStyleId(
                    civilDocument.Styles.ProfileViewStyles,
                    settings.ProfileViewStyle,
                    "profile-view style",
                    transaction,
                    out viewStyleName);''',
 '''                ObjectId viewStyleId = CivilStyleCatalogV2.ResolveStyleId(
                    database,
                    civilDocument,
                    "Profile View Style",
                    settings.ProfileViewStyle,
                    transaction,
                    out viewStyleName);'''),
('''                ObjectId bandId = ResolveStyleId(
                    civilDocument.Styles.ProfileViewBandSetStyles,
                    settings.ProfileViewBandSetStyle,
                    "profile-view band-set style",
                    transaction,
                    out bandName);''',
 '''                ObjectId bandId = CivilStyleCatalogV2.ResolveStyleId(
                    database,
                    civilDocument,
                    "Profile View Band Set Style",
                    settings.ProfileViewBandSetStyle,
                    transaction,
                    out bandName);'''),
]
for old_text, new_text in profile_blocks:
    sewer = replace_once(sewer, old_text, new_text, "safe sewer profile style")
write("SewerProductionCommands.cs", sewer)


# ---------------------------------------------------------------------------
# Annotation paper height: derive 500 from the actual CANNOSCALE name 1:500.
# CANNOSCALEVALUE is not a reliable drawing-units/paper-units multiplier in
# metre-based Civil drawings.
# ---------------------------------------------------------------------------
paper = read("PaperAnnotationScale.cs")
paper = replace_regex(
    paper,
    r'''        private static double CurrentAnnotationScale\(Database database\)\n        \{.*?\n        \}\n\n        private static double DrawingUnitsPerMillimetre''',
    r'''        private static double CurrentAnnotationScale(Database database)
        {
            double scale = ReadNamedAnnotationScale();
            if (IsValidScale(scale)) return scale;

            scale = ReadDatabaseAnnotationScale(database);
            if (IsValidScale(scale)) return scale;

            try
            {
                scale = Convert.ToDouble(
                    Autodesk.AutoCAD.ApplicationServices.Core.Application
                        .GetSystemVariable("CANNOSCALEVALUE"),
                    CultureInfo.InvariantCulture);
            }
            catch
            {
                scale = 0.0;
            }
            if (IsValidScale(scale) && scale >= 10.0) return scale;

            if (database != null && IsValidScale(database.Dimscale))
                return database.Dimscale;
            return 1.0;
        }

        private static double ReadNamedAnnotationScale()
        {
            try
            {
                string text = Convert.ToString(
                    Autodesk.AutoCAD.ApplicationServices.Core.Application
                        .GetSystemVariable("CANNOSCALE"),
                    CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(text)) return 0.0;
                text = text.Trim();
                int separator = text.IndexOf(':');
                if (separator > 0 && separator < text.Length - 1)
                {
                    double paper;
                    double drawing;
                    if (double.TryParse(
                            text.Substring(0, separator).Trim(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out paper) &&
                        double.TryParse(
                            text.Substring(separator + 1).Trim(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out drawing) &&
                        paper > 0.0 && drawing > 0.0)
                        return drawing / paper;
                }
            }
            catch
            {
                // Continue to the database annotation-scale object.
            }
            return 0.0;
        }

        private static double ReadDatabaseAnnotationScale(Database database)
        {
            if (database == null) return 0.0;
            try
            {
                PropertyInfo property = database.GetType().GetProperty(
                    "Cannoscale",
                    BindingFlags.Public | BindingFlags.Instance);
                object context = property == null || property.GetGetMethod() == null
                    ? null
                    : property.GetValue(database, null);
                if (context == null) return 0.0;
                double paper = ReadDouble(context, "PaperUnits");
                double drawing = ReadDouble(context, "DrawingUnits");
                return paper > 0.0 && drawing > 0.0
                    ? drawing / paper
                    : 0.0;
            }
            catch
            {
                return 0.0;
            }
        }

        private static double ReadDouble(object value, string propertyName)
        {
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (property == null || property.GetGetMethod() == null) return 0.0;
                return Convert.ToDouble(
                    property.GetValue(value, null),
                    CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0.0;
            }
        }

        private static bool IsValidScale(double value)
        {
            return value > 0.0 &&
                !double.IsNaN(value) &&
                !double.IsInfinity(value);
        }

        private static double DrawingUnitsPerMillimetre''',
    "annotation scale parsing",
    flags=re.S)
write("PaperAnnotationScale.cs", paper)

label = read("SewerBranchLabelPlacement.cs")
label = replace_once(
    label,
    '''            label.TextHeight = PaperAnnotationScale.ModelTextHeight(
                database,
                paperHeight);''',
    '''            paperHeight = PaperAnnotationScale.NormalizeConfiguredPaperHeight(
                paperHeight);
            label.TextHeight = PaperAnnotationScale.ModelTextHeight(
                database,
                paperHeight);''',
    "normalize branch paper height")
write("SewerBranchLabelPlacement.cs", label)


# ---------------------------------------------------------------------------
# Project Setup: use the existing modal editor instead of one GetString prompt
# per field. Expand the shared title/register metadata fields.
# ---------------------------------------------------------------------------
project = read("ProjectSetupCommands.cs")
old_fields = '''        private static readonly string[] FieldOrder =
        {
            "Project Name",
            "Client",
            "Country",
            "Town",
            "Coordinate System",
            "Standards",
            "Drawing Template",
            "Units"
        };'''
new_fields = '''        internal static readonly string[] FieldOrder =
        {
            "Project Name",
            "Project Number",
            "Client",
            "Company",
            "Country",
            "Town",
            "Coordinate System",
            "Standards",
            "Drawing Template",
            "Units",
            "Project Stage",
            "Revision",
            "Issue Date",
            "Drawing Number Prefix",
            "Designed By",
            "Drawn By",
            "Checked By",
            "Approved By"
        };'''
project = replace_once(project, old_fields, new_fields, "expanded project fields")
project = replace_regex(
    project,
    r'''        private static void SetupProject\(Document document\)\n        \{.*?\n        \}\n\n        private static void ReportProjectInfo''',
    r'''        private static void SetupProject(Document document)
        {
            Editor editor = document.Editor;
            ProjectMetadata existing = ReadProjectMetadata(
                document.Database,
                ProjectRecordName);
            var initialValues = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string field in FieldOrder)
            {
                string value = existing.Get(field);
                if (string.IsNullOrWhiteSpace(value) &&
                    string.Equals(field, "Units", StringComparison.OrdinalIgnoreCase))
                    value = "Metric";
                if (string.IsNullOrWhiteSpace(value) &&
                    string.Equals(field, "Issue Date", StringComparison.OrdinalIgnoreCase))
                    value = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                initialValues[field] = value ?? string.Empty;
            }

            var window = new ProjectSetupPopupWindow(
                FieldOrder,
                initialValues);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted)
            {
                editor.WriteMessage(
                    "\nCE_PROJECTSETUP cancelled. Existing project metadata was not changed.");
                return;
            }

            var proposed = new ProjectMetadata();
            foreach (string field in FieldOrder)
                proposed.Set(field, window.GetValue(field));

            if (!PopupTablePresenter.ShowReview(
                "CE Tools - Project Setup",
                "Review the project information before it is saved inside this drawing and linked to title blocks and drawing registers.",
                BuildRows(proposed),
                "Save"))
            {
                editor.WriteMessage(
                    "\nCE_PROJECTSETUP cancelled. Existing project metadata was not changed.");
                return;
            }

            try
            {
                WriteProjectMetadata(document.Database, proposed, clearBackup: true);
                RefreshInformationTables(document);
                editor.WriteMessage(
                    "\nCE_PROJECTSETUP complete. Project metadata saved inside this DWG.");
                PopupTablePresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Project Information",
                    "Project setup is complete and is now the shared source for drawing titles and registers.",
                    BuildRows(proposed),
                    "CE Tools Project Information");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\nCE_PROJECTSETUP cancelled. Existing metadata was not replaced. {0}",
                    exception.Message);
            }
        }

        internal static IDictionary<string, string> ReadSharedProjectMetadata(
            Database database)
        {
            ProjectMetadata metadata = ReadProjectMetadata(
                database,
                ProjectRecordName);
            var result = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string field in FieldOrder)
                result[field] = metadata.Get(field);
            return result;
        }

        internal static void MergeSharedProjectMetadata(
            Database database,
            IDictionary<string, string> values)
        {
            ProjectMetadata metadata = ReadProjectMetadata(
                database,
                ProjectRecordName);
            foreach (string field in FieldOrder)
            {
                string value;
                if (values != null && values.TryGetValue(field, out value))
                    metadata.Set(field, value ?? string.Empty);
            }
            metadata.Exists = true;
            WriteProjectMetadata(database, metadata, clearBackup: false);
        }

        private static void ReportProjectInfo''',
    "project setup popup",
    flags=re.S)
# Needed for CultureInfo in the new popup defaults.
if "using System.Globalization;" not in project:
    project = project.replace("using System.Collections.Generic;\n", "using System.Collections.Generic;\nusing System.Globalization;\n", 1)
write("ProjectSetupCommands.cs", project)


# ---------------------------------------------------------------------------
# Shared editable drawing register, popup, persistent metadata and optional
# approved title-block DWG attribute population.
# ---------------------------------------------------------------------------
register_source = r'''using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ProductionDrawingRegisterCommands))]

namespace CETools.Civil3D
{
    public sealed class ProductionDrawingRegisterCommands
    {
        [CommandMethod("CE_TOOLS", "CE_DRAWINGREGISTEREDIT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void EditDrawingRegister()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            ProductionDrawingRegisterData result;
            EditForProduction(
                document,
                ReadLayoutSeeds(document.Database),
                "Save Register",
                out result);
        }

        internal static bool EditForProduction(
            Document document,
            IEnumerable<ProductionDrawingSeed> seeds,
            string actionText,
            out ProductionDrawingRegisterData result)
        {
            result = null;
            if (document == null) return false;
            ProductionDrawingRegisterData data = ProductionDrawingRegisterStore.Read(
                document.Database);
            IDictionary<string, string> project =
                ProjectSetupCommands.ReadSharedProjectMetadata(document.Database);
            data.ApplyProjectDefaults(project);
            data.MergeSeeds(seeds ?? Enumerable.Empty<ProductionDrawingSeed>());
            data.ApplyRowDefaults();

            var window = new ProductionDrawingRegisterWindow(
                data,
                string.IsNullOrWhiteSpace(actionText)
                    ? "Save"
                    : actionText);
            AcApplication.ShowModalWindow(window);
            if (!window.Accepted) return false;

            result = window.BuildResult();
            result.ApplyRowDefaults();
            ProductionDrawingRegisterStore.Write(document.Database, result);
            ProjectSetupCommands.MergeSharedProjectMetadata(
                document.Database,
                result.Headers);
            ProjectSetupCommands.RefreshInformationTables(document);
            document.Editor.WriteMessage(
                "\nCE drawing register saved. Rows={0}; title metadata is linked to production layouts and exports.",
                result.Rows.Count);
            return true;
        }

        internal static List<ProductionDrawingSeed> ReadLayoutSeeds(Database database)
        {
            var result = new List<ProductionDrawingSeed>();
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                DBDictionary layouts = transaction.GetObject(
                    database.LayoutDictionaryId,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (layouts == null) return result;
                foreach (DBDictionaryEntry entry in layouts)
                {
                    Layout layout = transaction.GetObject(
                        entry.Value,
                        OpenMode.ForRead,
                        false) as Layout;
                    if (layout == null || layout.ModelType) continue;
                    result.Add(new ProductionDrawingSeed(
                        layout.LayoutName,
                        layout.LayoutName,
                        "Project drawing",
                        "Existing",
                        "As shown"));
                }
            }
            return result;
        }
    }

    internal sealed class ProductionDrawingSeed
    {
        internal ProductionDrawingSeed(
            string layout,
            string title,
            string purpose,
            string paper,
            string scale)
        {
            Layout = layout ?? string.Empty;
            Title = title ?? string.Empty;
            Purpose = purpose ?? string.Empty;
            Paper = paper ?? string.Empty;
            Scale = scale ?? string.Empty;
        }
        internal string Layout { get; private set; }
        internal string Title { get; private set; }
        internal string Purpose { get; private set; }
        internal string Paper { get; private set; }
        internal string Scale { get; private set; }
    }

    internal sealed class ProductionDrawingRegisterRow
    {
        public string DrawingNumber { get; set; }
        public string Layout { get; set; }
        public string Title { get; set; }
        public string Purpose { get; set; }
        public string Paper { get; set; }
        public string Scale { get; set; }
        public string Stage { get; set; }
        public string Revision { get; set; }
        public string IssueDate { get; set; }

        internal ProductionDrawingRegisterRow Clone()
        {
            return new ProductionDrawingRegisterRow
            {
                DrawingNumber = DrawingNumber ?? string.Empty,
                Layout = Layout ?? string.Empty,
                Title = Title ?? string.Empty,
                Purpose = Purpose ?? string.Empty,
                Paper = Paper ?? string.Empty,
                Scale = Scale ?? string.Empty,
                Stage = Stage ?? string.Empty,
                Revision = Revision ?? string.Empty,
                IssueDate = IssueDate ?? string.Empty
            };
        }
    }

    internal sealed class ProductionDrawingRegisterData
    {
        internal static readonly string[] HeaderFields =
        {
            "Project Name",
            "Project Number",
            "Client",
            "Company",
            "Project Stage",
            "Revision",
            "Issue Date",
            "Drawing Number Prefix",
            "Designed By",
            "Drawn By",
            "Checked By",
            "Approved By",
            "Title Block Source"
        };

        internal ProductionDrawingRegisterData()
        {
            Headers = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string field in HeaderFields) Headers[field] = string.Empty;
            Rows = new List<ProductionDrawingRegisterRow>();
        }

        internal IDictionary<string, string> Headers { get; private set; }
        internal List<ProductionDrawingRegisterRow> Rows { get; private set; }

        internal string Header(string name)
        {
            string value;
            return Headers.TryGetValue(name, out value)
                ? value ?? string.Empty
                : string.Empty;
        }

        internal void ApplyProjectDefaults(IDictionary<string, string> project)
        {
            foreach (string field in HeaderFields)
            {
                if (string.Equals(field, "Title Block Source", StringComparison.OrdinalIgnoreCase))
                    continue;
                string existing = Header(field);
                string value;
                if (string.IsNullOrWhiteSpace(existing) &&
                    project != null && project.TryGetValue(field, out value))
                    Headers[field] = value ?? string.Empty;
            }
            if (string.IsNullOrWhiteSpace(Header("Issue Date")))
                Headers["Issue Date"] = DateTime.Today.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(Header("Drawing Number Prefix")))
                Headers["Drawing Number Prefix"] = "CE";
            if (string.IsNullOrWhiteSpace(Header("Title Block Source")))
            {
                string bundled = ProductionTitleBlockManager.FindBundledSource();
                if (!string.IsNullOrWhiteSpace(bundled))
                    Headers["Title Block Source"] = bundled;
            }
        }

        internal void MergeSeeds(IEnumerable<ProductionDrawingSeed> seeds)
        {
            foreach (ProductionDrawingSeed seed in seeds)
            {
                if (seed == null || string.IsNullOrWhiteSpace(seed.Layout)) continue;
                ProductionDrawingRegisterRow row = Find(seed.Layout);
                if (row == null)
                {
                    row = new ProductionDrawingRegisterRow
                    {
                        Layout = seed.Layout,
                        Title = seed.Title,
                        Purpose = seed.Purpose,
                        Paper = seed.Paper,
                        Scale = seed.Scale
                    };
                    Rows.Add(row);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(row.Title)) row.Title = seed.Title;
                    if (string.IsNullOrWhiteSpace(row.Purpose)) row.Purpose = seed.Purpose;
                    if (string.IsNullOrWhiteSpace(row.Paper)) row.Paper = seed.Paper;
                    if (string.IsNullOrWhiteSpace(row.Scale)) row.Scale = seed.Scale;
                }
            }
        }

        internal void ApplyRowDefaults()
        {
            string prefix = Header("Drawing Number Prefix");
            string stage = Header("Project Stage");
            string revision = Header("Revision");
            string issueDate = Header("Issue Date");
            int next = 1;
            foreach (ProductionDrawingRegisterRow row in Rows)
            {
                if (string.IsNullOrWhiteSpace(row.DrawingNumber))
                    row.DrawingNumber = (string.IsNullOrWhiteSpace(prefix) ? "CE" : prefix) +
                        "-" + next.ToString("000", CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(row.Title)) row.Title = row.Layout;
                if (string.IsNullOrWhiteSpace(row.Purpose)) row.Purpose = "Project drawing";
                if (string.IsNullOrWhiteSpace(row.Scale)) row.Scale = "As shown";
                if (string.IsNullOrWhiteSpace(row.Stage)) row.Stage = stage;
                if (string.IsNullOrWhiteSpace(row.Revision)) row.Revision = revision;
                if (string.IsNullOrWhiteSpace(row.IssueDate)) row.IssueDate = issueDate;
                next++;
            }
        }

        internal ProductionDrawingRegisterRow Find(string layout)
        {
            return Rows.FirstOrDefault(row => string.Equals(
                row.Layout,
                layout,
                StringComparison.OrdinalIgnoreCase));
        }

        internal ProductionDrawingRegisterData Clone()
        {
            var result = new ProductionDrawingRegisterData();
            foreach (KeyValuePair<string, string> pair in Headers)
                result.Headers[pair.Key] = pair.Value ?? string.Empty;
            result.Rows.Clear();
            result.Rows.AddRange(Rows.Select(row => row.Clone()));
            return result;
        }
    }

    internal static class ProductionDrawingRegisterStore
    {
        private const string RootName = "CE_TOOLS";
        private const string RecordName = "DRAWING_REGISTER_METADATA";

        internal static ProductionDrawingRegisterData Read(Database database)
        {
            var result = new ProductionDrawingRegisterData();
            if (database == null) return result;
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                DBDictionary named = transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (named == null || !named.Contains(RootName)) return result;
                DBDictionary root = transaction.GetObject(
                    named.GetAt(RootName),
                    OpenMode.ForRead,
                    false) as DBDictionary;
                if (root == null || !root.Contains(RecordName)) return result;
                Xrecord record = transaction.GetObject(
                    root.GetAt(RecordName),
                    OpenMode.ForRead,
                    false) as Xrecord;
                if (record == null || record.Data == null) return result;
                foreach (TypedValue value in record.Data)
                {
                    string text = value.Value as string;
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    string[] parts = text.Split('|');
                    if (parts.Length == 3 && parts[0] == "H")
                        result.Headers[Decode(parts[1])] = Decode(parts[2]);
                    else if (parts.Length == 10 && parts[0] == "R")
                    {
                        result.Rows.Add(new ProductionDrawingRegisterRow
                        {
                            DrawingNumber = Decode(parts[1]),
                            Layout = Decode(parts[2]),
                            Title = Decode(parts[3]),
                            Purpose = Decode(parts[4]),
                            Paper = Decode(parts[5]),
                            Scale = Decode(parts[6]),
                            Stage = Decode(parts[7]),
                            Revision = Decode(parts[8]),
                            IssueDate = Decode(parts[9])
                        });
                    }
                }
            }
            return result;
        }

        internal static void Write(
            Database database,
            ProductionDrawingRegisterData data)
        {
            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                DBDictionary named = transaction.GetObject(
                    database.NamedObjectsDictionaryId,
                    OpenMode.ForWrite,
                    false) as DBDictionary;
                DBDictionary root;
                if (named.Contains(RootName))
                    root = transaction.GetObject(
                        named.GetAt(RootName),
                        OpenMode.ForWrite,
                        false) as DBDictionary;
                else
                {
                    root = new DBDictionary();
                    named.SetAt(RootName, root);
                    transaction.AddNewlyCreatedDBObject(root, true);
                }
                Xrecord record;
                if (root.Contains(RecordName))
                    record = transaction.GetObject(
                        root.GetAt(RecordName),
                        OpenMode.ForWrite,
                        false) as Xrecord;
                else
                {
                    record = new Xrecord();
                    root.SetAt(RecordName, record);
                    transaction.AddNewlyCreatedDBObject(record, true);
                }
                var values = new List<TypedValue>
                {
                    new TypedValue((int)DxfCode.Text, "SCHEMA|1")
                };
                foreach (string field in ProductionDrawingRegisterData.HeaderFields)
                    values.Add(new TypedValue(
                        (int)DxfCode.Text,
                        "H|" + Encode(field) + "|" + Encode(data.Header(field))));
                foreach (ProductionDrawingRegisterRow row in data.Rows)
                {
                    values.Add(new TypedValue(
                        (int)DxfCode.Text,
                        string.Join("|", new[]
                        {
                            "R",
                            Encode(row.DrawingNumber),
                            Encode(row.Layout),
                            Encode(row.Title),
                            Encode(row.Purpose),
                            Encode(row.Paper),
                            Encode(row.Scale),
                            Encode(row.Stage),
                            Encode(row.Revision),
                            Encode(row.IssueDate)
                        })));
                }
                record.Data = new ResultBuffer(values.ToArray());
                transaction.Commit();
            }
        }

        private static string Encode(string value)
        {
            return Convert.ToBase64String(
                Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Decode(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(
                    Convert.FromBase64String(value ?? string.Empty));
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    internal sealed class ProductionDrawingRegisterWindow : Window
    {
        private readonly IDictionary<string, TextBox> _headers =
            new Dictionary<string, TextBox>(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<ProductionDrawingRegisterRow> _rows;
        private readonly DataGrid _grid;

        internal ProductionDrawingRegisterWindow(
            ProductionDrawingRegisterData source,
            string actionText)
        {
            Title = "CE Tools - Drawing Titles and Register";
            Width = 1180;
            Height = 760;
            MinWidth = 860;
            MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResizeWithGrip;

            _rows = new ObservableCollection<ProductionDrawingRegisterRow>(
                source.Rows.Select(row => row.Clone()));
            var root = new DockPanel { Margin = new Thickness(14) };
            Content = root;

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            var add = Button("Add Drawing", 105);
            add.Click += delegate
            {
                _rows.Add(new ProductionDrawingRegisterRow
                {
                    Stage = Value("Project Stage"),
                    Revision = Value("Revision"),
                    IssueDate = Value("Issue Date"),
                    Scale = "As shown"
                });
            };
            buttons.Children.Add(add);
            var remove = Button("Remove Selected", 125);
            remove.Margin = new Thickness(6, 0, 0, 0);
            remove.Click += delegate
            {
                ProductionDrawingRegisterRow row =
                    _grid.SelectedItem as ProductionDrawingRegisterRow;
                if (row != null) _rows.Remove(row);
            };
            buttons.Children.Add(remove);
            var cancel = Button("Cancel", 90);
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(18, 0, 0, 0);
            cancel.Click += delegate { DialogResult = false; };
            buttons.Children.Add(cancel);
            var save = Button(actionText, 145);
            save.IsDefault = true;
            save.Margin = new Thickness(6, 0, 0, 0);
            save.Click += delegate
            {
                _grid.CommitEdit(DataGridEditingUnit.Cell, true);
                _grid.CommitEdit(DataGridEditingUnit.Row, true);
                if (_rows.Any(row => string.IsNullOrWhiteSpace(row.Layout)))
                {
                    MessageBox.Show(
                        "Every drawing-register row must have a layout name.",
                        "CE Tools",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                Accepted = true;
                DialogResult = true;
            };
            buttons.Children.Add(save);

            var heading = new TextBlock
            {
                Text = "Drawing titles, title block information and drawing register",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
            DockPanel.SetDock(heading, Dock.Top);
            root.Children.Add(heading);
            var note = new TextBlock
            {
                Text = "Edit project issue data and every sheet in one popup. The saved values drive drawing titles, title-block attributes, on-sheet registers and Excel indexes.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(note, Dock.Top);
            root.Children.Add(note);

            var headerGrid = BuildHeaderGrid(source);
            var headerScroll = new ScrollViewer
            {
                Content = headerGrid,
                Height = 215,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 10)
            };
            DockPanel.SetDock(headerScroll, Dock.Top);
            root.Children.Add(headerScroll);

            _grid = new DataGrid
            {
                ItemsSource = _rows,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All
            };
            AddColumn("Drawing No.", "DrawingNumber", 110);
            AddColumn("Layout", "Layout", 145);
            AddColumn("Title", "Title", 220);
            AddColumn("Purpose / Discipline", "Purpose", 155);
            AddColumn("Paper", "Paper", 75);
            AddColumn("Scale", "Scale", 85);
            AddColumn("Stage", "Stage", 105);
            AddColumn("Revision", "Revision", 75);
            AddColumn("Issue Date", "IssueDate", 100);
            root.Children.Add(_grid);
        }

        internal bool Accepted { get; private set; }

        internal ProductionDrawingRegisterData BuildResult()
        {
            var result = new ProductionDrawingRegisterData();
            foreach (string field in ProductionDrawingRegisterData.HeaderFields)
                result.Headers[field] = Value(field);
            result.Rows.Clear();
            result.Rows.AddRange(_rows.Select(row => row.Clone()));
            return result;
        }

        private Grid BuildHeaderGrid(ProductionDrawingRegisterData source)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(175)
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            int row = 0;
            foreach (string field in ProductionDrawingRegisterData.HeaderFields)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock
                {
                    Text = field,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 3, 10, 3)
                };
                Grid.SetRow(label, row);
                grid.Children.Add(label);
                var editor = new TextBox
                {
                    Text = source.Header(field),
                    Margin = new Thickness(0, 2, 0, 2),
                    Padding = new Thickness(4, 2, 4, 2)
                };
                _headers[field] = editor;
                if (string.Equals(field, "Title Block Source", StringComparison.OrdinalIgnoreCase))
                {
                    var panel = new DockPanel();
                    var browse = Button("Browse...", 85);
                    DockPanel.SetDock(browse, Dock.Right);
                    browse.Margin = new Thickness(6, 2, 0, 2);
                    browse.Click += delegate
                    {
                        var dialog = new Microsoft.Win32.OpenFileDialog
                        {
                            Title = "Select CE Tools title-block source DWG",
                            Filter = "AutoCAD drawing (*.dwg)|*.dwg|All files (*.*)|*.*",
                            CheckFileExists = true,
                            Multiselect = false
                        };
                        if (dialog.ShowDialog() == true)
                            editor.Text = dialog.FileName;
                    };
                    panel.Children.Add(browse);
                    panel.Children.Add(editor);
                    Grid.SetRow(panel, row);
                    Grid.SetColumn(panel, 1);
                    grid.Children.Add(panel);
                }
                else
                {
                    Grid.SetRow(editor, row);
                    Grid.SetColumn(editor, 1);
                    grid.Children.Add(editor);
                }
                row++;
            }
            return grid;
        }

        private void AddColumn(string header, string path, double width)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path)
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
                },
                Width = new DataGridLength(width)
            });
        }

        private string Value(string name)
        {
            TextBox editor;
            return _headers.TryGetValue(name, out editor)
                ? (editor.Text ?? string.Empty).Trim()
                : string.Empty;
        }

        private static Button Button(string text, double width)
        {
            return new Button
            {
                Content = text,
                MinWidth = width,
                Padding = new Thickness(8, 4, 8, 4)
            };
        }
    }

    internal static class ProductionTitleBlockManager
    {
        internal static string FindBundledSource()
        {
            try
            {
                string folder = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                string path = Path.GetFullPath(Path.Combine(
                    folder,
                    "..",
                    "..",
                    "Resources",
                    "TitleBlocks",
                    "CE TOOLS - TITLE BLOCKS.dwg"));
                return File.Exists(path) ? path : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static ObjectId TryInsert(
            Database destination,
            Transaction transaction,
            BlockTableRecord paperSpace,
            string sourcePath,
            string paperName,
            Point3d insertion,
            ProductionDrawingRegisterData register,
            ProductionDrawingRegisterRow row,
            out string diagnostic)
        {
            diagnostic = string.Empty;
            if (destination == null || transaction == null || paperSpace == null ||
                string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                diagnostic = "No readable title-block source DWG was selected.";
                return ObjectId.Null;
            }

            try
            {
                string blockName;
                using (var source = new Database(false, true))
                {
                    source.ReadDwgFile(sourcePath, FileShare.Read, true, string.Empty);
                    source.CloseInput(true);
                    ObjectId sourceBlockId = FindBestBlock(
                        source,
                        paperName,
                        out blockName);
                    if (sourceBlockId.IsNull)
                    {
                        diagnostic = "No compatible " + paperName +
                            " attributed block definition was found in the selected DWG.";
                        return ObjectId.Null;
                    }
                    var ids = new ObjectIdCollection();
                    ids.Add(sourceBlockId);
                    var mapping = new IdMapping();
                    source.WblockCloneObjects(
                        ids,
                        destination.BlockTableId,
                        mapping,
                        DuplicateRecordCloning.Replace,
                        false);
                }

                BlockTable blockTable = transaction.GetObject(
                    destination.BlockTableId,
                    OpenMode.ForRead,
                    false) as BlockTable;
                if (blockTable == null || !blockTable.Has(blockName))
                {
                    diagnostic = "The title-block definition could not be cloned into the active drawing.";
                    return ObjectId.Null;
                }

                ObjectId definitionId = blockTable[blockName];
                var reference = new BlockReference(insertion, definitionId);
                reference.SetDatabaseDefaults(destination);
                paperSpace.AppendEntity(reference);
                transaction.AddNewlyCreatedDBObject(reference, true);

                BlockTableRecord definition = transaction.GetObject(
                    definitionId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                IDictionary<string, string> values = BuildAttributeValues(register, row);
                foreach (ObjectId id in definition)
                {
                    AttributeDefinition attribute = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as AttributeDefinition;
                    if (attribute == null || attribute.Constant) continue;
                    var value = new AttributeReference();
                    value.SetAttributeFromBlock(attribute, reference.BlockTransform);
                    value.TextString = ResolveAttributeValue(
                        attribute.Tag,
                        attribute.TextString,
                        values);
                    reference.AttributeCollection.AppendAttribute(value);
                    transaction.AddNewlyCreatedDBObject(value, true);
                }
                diagnostic = "Title block inserted from " + Path.GetFileName(sourcePath) + ".";
                return reference.ObjectId;
            }
            catch (System.Exception exception)
            {
                diagnostic = "Title-block source could not be inserted: " + exception.Message;
                return ObjectId.Null;
            }
        }

        private static ObjectId FindBestBlock(
            Database source,
            string paperName,
            out string blockName)
        {
            blockName = string.Empty;
            ObjectId best = ObjectId.Null;
            int bestScore = int.MinValue;
            using (Transaction transaction =
                source.TransactionManager.StartTransaction())
            {
                BlockTable blocks = transaction.GetObject(
                    source.BlockTableId,
                    OpenMode.ForRead,
                    false) as BlockTable;
                foreach (ObjectId id in blocks)
                {
                    BlockTableRecord block = transaction.GetObject(
                        id,
                        OpenMode.ForRead,
                        false) as BlockTableRecord;
                    if (block == null || block.IsLayout || block.IsAnonymous ||
                        block.IsFromExternalReference) continue;
                    int attributes = 0;
                    foreach (ObjectId entityId in block)
                    {
                        if (transaction.GetObject(
                                entityId,
                                OpenMode.ForRead,
                                false) is AttributeDefinition)
                            attributes++;
                    }
                    int score = attributes * 4;
                    string name = block.Name ?? string.Empty;
                    if (name.IndexOf(paperName ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 100;
                    if (name.IndexOf("TITLE", StringComparison.OrdinalIgnoreCase) >= 0)
                        score += 25;
                    if (score > bestScore && attributes > 0)
                    {
                        bestScore = score;
                        best = id;
                        blockName = name;
                    }
                }
            }
            return best;
        }

        private static IDictionary<string, string> BuildAttributeValues(
            ProductionDrawingRegisterData data,
            ProductionDrawingRegisterRow row)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "PROJECT", data.Header("Project Name") },
                { "PROJECTNAME", data.Header("Project Name") },
                { "PROJECTNO", data.Header("Project Number") },
                { "PROJECTNUMBER", data.Header("Project Number") },
                { "CLIENT", data.Header("Client") },
                { "COMPANY", data.Header("Company") },
                { "DRAWINGNO", row.DrawingNumber },
                { "DRAWINGNUMBER", row.DrawingNumber },
                { "DWGNO", row.DrawingNumber },
                { "TITLE", row.Title },
                { "DRAWINGTITLE", row.Title },
                { "SHEETTITLE", row.Title },
                { "PURPOSE", row.Purpose },
                { "DISCIPLINE", row.Purpose },
                { "SCALE", row.Scale },
                { "STAGE", row.Stage },
                { "STATUS", row.Stage },
                { "REV", row.Revision },
                { "REVISION", row.Revision },
                { "DATE", row.IssueDate },
                { "ISSUEDATE", row.IssueDate },
                { "DESIGNED", data.Header("Designed By") },
                { "DESIGNEDBY", data.Header("Designed By") },
                { "DRAWN", data.Header("Drawn By") },
                { "DRAWNBY", data.Header("Drawn By") },
                { "CHECKED", data.Header("Checked By") },
                { "CHECKEDBY", data.Header("Checked By") },
                { "APPROVED", data.Header("Approved By") },
                { "APPROVEDBY", data.Header("Approved By") },
                { "LAYOUT", row.Layout },
                { "SHEET", row.Layout }
            };
            return result;
        }

        private static string ResolveAttributeValue(
            string tag,
            string fallback,
            IDictionary<string, string> values)
        {
            string key = NormalizeTag(tag);
            string value;
            if (values.TryGetValue(key, out value)) return value ?? string.Empty;
            foreach (KeyValuePair<string, string> pair in values)
            {
                if (key.Contains(pair.Key) || pair.Key.Contains(key))
                    return pair.Value ?? string.Empty;
            }
            return fallback ?? string.Empty;
        }

        private static string NormalizeTag(string value)
        {
            return new string((value ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());
        }
    }
}
'''
write("ProductionDrawingRegisterCommands.cs", register_source)


# ---------------------------------------------------------------------------
# Drawing books: popup is mandatory; one register drives layout titles,
# title-block attributes, on-sheet registers and Excel export.
# ---------------------------------------------------------------------------
production = read("ProductionReportCommands.cs")
production = replace_regex(
    production,
    r'''        \[CommandMethod\("CE_TOOLS", "CE_DRAWINGBOOK", CommandFlags.Modal \| CommandFlags.Redraw\)\]\n        public void CreateDrawingBook\(\)\n        \{.*?\n        \}\n\n        \[CommandMethod\("CE_TOOLS", "CE_BOOKINDEX"''',
    r'''        [CommandMethod("CE_TOOLS", "CE_DRAWINGBOOK", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CreateDrawingBook()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            ProjectSnapshot snapshot = BuildSnapshot(
                document.Database,
                ReportDiscipline.All);
            List<BookPackage> packages = StandardBookPackages();
            var seeds = packages.Select(package => new ProductionDrawingSeed(
                package.LayoutName,
                package.Purpose,
                package.Purpose,
                package.PaperName,
                "As shown")).ToList();
            foreach (LayoutSnapshot layout in snapshot.Layouts)
            {
                if (seeds.Any(seed => string.Equals(
                        seed.Layout,
                        layout.Name,
                        StringComparison.OrdinalIgnoreCase)))
                    continue;
                seeds.Add(new ProductionDrawingSeed(
                    layout.Name,
                    layout.Name,
                    "Project drawing",
                    "Existing",
                    "As shown"));
            }

            ProductionDrawingRegisterData drawingRegister;
            if (!ProductionDrawingRegisterCommands.EditForProduction(
                    document,
                    seeds,
                    "Save & Generate",
                    out drawingRegister))
                return;

            try
            {
                int created = 0;
                int refreshed = 0;
                foreach (BookPackage package in packages)
                {
                    bool wasCreated = CreateOrRefreshBookLayout(
                        document.Database,
                        package,
                        snapshot,
                        drawingRegister);
                    if (wasCreated) created++;
                    else refreshed++;
                }
                document.Editor.WriteMessage(
                    "\nCE_DRAWINGBOOK complete. Layouts created={0}; refreshed={1}. Titles, title blocks and the drawing register use the saved popup values.",
                    created,
                    refreshed);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_DRAWINGBOOK failed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_BOOKINDEX"''',
    "drawing book popup",
    flags=re.S)

production = replace_regex(
    production,
    r'''        public void ExportDrawingBookIndex\(\)\n        \{.*?\n        \}\n\n        private static void ShowReport''',
    r'''        public void ExportDrawingBookIndex()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            ProjectSnapshot snapshot = BuildSnapshot(
                document.Database,
                ReportDiscipline.All);
            var seeds = StandardBookPackages()
                .Select(package => new ProductionDrawingSeed(
                    package.LayoutName,
                    package.Purpose,
                    package.Purpose,
                    package.PaperName,
                    "As shown"))
                .ToList();
            foreach (LayoutSnapshot layout in snapshot.Layouts)
                seeds.Add(new ProductionDrawingSeed(
                    layout.Name,
                    layout.Name,
                    "Project drawing",
                    "Existing",
                    "As shown"));

            ProductionDrawingRegisterData register;
            if (!ProductionDrawingRegisterCommands.EditForProduction(
                    document,
                    seeds,
                    "Save & Export Index",
                    out register))
                return;

            string path;
            if (!PromptExcelPath(
                document.Editor,
                "CE-Tools-Drawing-Book-Index.xlsx",
                out path)) return;
            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    "CE TOOLS DRAWING BOOK INDEX", string.Empty, string.Empty,
                    string.Empty, string.Empty, string.Empty, string.Empty,
                    string.Empty, string.Empty
                },
                new List<string>
                {
                    "DRAWING NO.", "LAYOUT", "TITLE", "PURPOSE / DISCIPLINE",
                    "PAPER", "SCALE", "STAGE", "REVISION", "ISSUE DATE"
                }
            };
            foreach (ProductionDrawingRegisterRow row in register.Rows)
            {
                rows.Add(new List<string>
                {
                    row.DrawingNumber,
                    row.Layout,
                    row.Title,
                    row.Purpose,
                    row.Paper,
                    row.Scale,
                    row.Stage,
                    row.Revision,
                    row.IssueDate
                });
            }
            try
            {
                SimpleXlsxWriter.Write(path, "Drawing Book Index", rows);
                document.Editor.WriteMessage(
                    "\nCE_BOOKINDEX complete. Drawings listed={0}; workbook={1}",
                    register.Rows.Count,
                    path);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE_BOOKINDEX failed. {0}",
                    exception.Message);
            }
        }

        private static void ShowReport''',
    "drawing book index popup",
    flags=re.S)

production = replace_once(
    production,
    '''        private static bool CreateOrRefreshBookLayout(
            Database database,
            BookPackage package,
            ProjectSnapshot snapshot)''',
    '''        private static bool CreateOrRefreshBookLayout(
            Database database,
            BookPackage package,
            ProjectSnapshot snapshot,
            ProductionDrawingRegisterData drawingRegister)''',
    "drawing book method signature")

production = replace_once(
    production,
    '''                var generated = new List<string>();

                var frame = new Polyline();''',
    '''                var generated = new List<string>();
                ProductionDrawingRegisterRow registerRow =
                    drawingRegister.Find(package.LayoutName) ??
                    new ProductionDrawingRegisterRow
                    {
                        DrawingNumber = package.LayoutName,
                        Layout = package.LayoutName,
                        Title = package.Purpose,
                        Purpose = package.Purpose,
                        Paper = package.PaperName,
                        Scale = "As shown",
                        Stage = drawingRegister.Header("Project Stage"),
                        Revision = drawingRegister.Header("Revision"),
                        IssueDate = drawingRegister.Header("Issue Date")
                    };

                string titleBlockDiagnostic;
                ObjectId titleBlockId = ProductionTitleBlockManager.TryInsert(
                    database,
                    transaction,
                    paperSpace,
                    drawingRegister.Header("Title Block Source"),
                    package.PaperName,
                    Point3d.Origin,
                    drawingRegister,
                    registerRow,
                    out titleBlockDiagnostic);
                if (!titleBlockId.IsNull)
                    generated.Add(titleBlockId.Handle.ToString());

                var frame = new Polyline();''',
    "drawing book title block insertion")

old_title = '''                title.Contents = string.Join(
                    "\\P",
                    ValueOrNotSet(snapshot.Project.Get("Project Name")),
                    package.Purpose.ToUpperInvariant(),
                    package.PaperName + " | " + package.Width.ToString("N0", CultureInfo.InvariantCulture) +
                        " x " + package.Height.ToString("N0", CultureInfo.InvariantCulture) + " mm");'''
new_title = '''                title.Contents = string.Join(
                    "\\P",
                    registerRow.DrawingNumber + "  |  " + registerRow.Title.ToUpperInvariant(),
                    ValueOrNotSet(drawingRegister.Header("Project Name")) +
                        "  |  " + ValueOrNotSet(drawingRegister.Header("Client")),
                    registerRow.Paper + " | Scale " + registerRow.Scale +
                        " | Stage " + registerRow.Stage +
                        " | Rev " + registerRow.Revision +
                        " | " + registerRow.IssueDate);'''
production = replace_once(production, old_title, new_title, "linked drawing title")

production = replace_once(
    production,
    '''                    package,
                    snapshot,
                    titleHeight * 0.5);''',
    '''                    package,
                    snapshot,
                    drawingRegister,
                    titleHeight * 0.5);''',
    "drawing register call")

production = replace_once(
    production,
    '''                    "CE Tools created the true-size A-series paper-space frame and drawing register. " +
                    "Assign the office-approved PC3, CTB/STB and canonical media before publishing. " +
                    "Client books use A4/A3; construction sets use A1/A0.";''',
    '''                    "Drawing title and register data are linked to CE_DRAWINGREGISTEREDIT. " +
                    titleBlockDiagnostic + " Assign the office-approved PC3, CTB/STB and canonical media before publishing.";''',
    "drawing book linked note")

production = replace_once(
    production,
    '''        private static Table BuildBookRegister(
            Database database,
            Point3d position,
            BookPackage package,
            ProjectSnapshot snapshot,
            double textHeight)''',
    '''        private static Table BuildBookRegister(
            Database database,
            Point3d position,
            BookPackage package,
            ProjectSnapshot snapshot,
            ProductionDrawingRegisterData drawingRegister,
            double textHeight)''',
    "book register signature")

production = replace_regex(
    production,
    r'''            List<LayoutSnapshot> layouts = snapshot.Layouts.*?            return table;\n        \}\n\n        private static void AddBookGenerated''',
    r'''            List<ProductionDrawingRegisterRow> rows = drawingRegister.Rows
                .Take(package.PaperName == "A4" ? 10 : 24)
                .ToList();
            if (rows.Count == 0)
            {
                rows.Add(new ProductionDrawingRegisterRow
                {
                    DrawingNumber = "-",
                    Layout = package.LayoutName,
                    Title = "No drawings registered",
                    Purpose = package.Purpose,
                    Revision = drawingRegister.Header("Revision")
                });
            }

            var table = new Table();
            table.SetDatabaseDefaults(database);
            table.TableStyle = database.Tablestyle;
            table.Position = position;
            table.SetSize(rows.Count + 2, 5);
            table.SetRowHeight(textHeight * 2.0);
            double available = package.Width * 0.82;
            table.Columns[0].Width = available * 0.14;
            table.Columns[1].Width = available * 0.24;
            table.Columns[2].Width = available * 0.38;
            table.Columns[3].Width = available * 0.12;
            table.Columns[4].Width = available * 0.12;
            table.MergeCells(CellRange.Create(table, 0, 0, 0, 4));
            table.Cells[0, 0].TextString = "DRAWING BOOK REGISTER";
            string[] headings =
            {
                "DRAWING NO.", "LAYOUT", "TITLE", "SCALE", "REV"
            };
            for (int column = 0; column < headings.Length; column++)
                table.Cells[1, column].TextString = headings[column];
            for (int index = 0; index < rows.Count; index++)
            {
                int rowIndex = index + 2;
                ProductionDrawingRegisterRow item = rows[index];
                table.Cells[rowIndex, 0].TextString = item.DrawingNumber;
                table.Cells[rowIndex, 1].TextString = item.Layout;
                table.Cells[rowIndex, 2].TextString = item.Title;
                table.Cells[rowIndex, 3].TextString = item.Scale;
                table.Cells[rowIndex, 4].TextString = item.Revision;
            }
            for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                for (int column = 0; column < table.Columns.Count; column++)
                    table.Cells[rowIndex, column].TextHeight = textHeight;
            return table;
        }

        private static void AddBookGenerated''',
    "linked on-sheet drawing register",
    flags=re.S)
write("ProductionReportCommands.cs", production)


# Add the register editor to the production and print popup menus.
centre = read("ProductionCommentCommands.cs")
centre = replace_once(
    centre,
    '''                    new ProductionChoice("Export client-book register to Excel", "CE_CLIENTBOOKINDEX "),
                    new ProductionChoice("Create A4/A3 client and A1/A0 construction layouts", "CE_DRAWINGBOOK "),''',
    '''                    new ProductionChoice("Export client-book register to Excel", "CE_CLIENTBOOKINDEX "),
                    new ProductionChoice("Edit drawing titles and drawing register", "CE_DRAWINGREGISTEREDIT "),
                    new ProductionChoice("Create A4/A3 client and A1/A0 construction layouts", "CE_DRAWINGBOOK "),''',
    "production register menu")
centre = replace_once(
    centre,
    '''                    new ProductionChoice("Create/refresh A-series drawing-book layouts", "CE_DRAWINGBOOK "),''',
    '''                    new ProductionChoice("Edit drawing titles and drawing register", "CE_DRAWINGREGISTEREDIT "),
                    new ProductionChoice("Create/refresh A-series drawing-book layouts", "CE_DRAWINGBOOK "),''',
    "print register menu")
write("ProductionCommentCommands.cs", centre)


print("Applied sewer alignment, paper height, project popup and production drawing-register fixes.")
