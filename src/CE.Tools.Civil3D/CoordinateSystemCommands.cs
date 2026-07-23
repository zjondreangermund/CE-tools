using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.Settings;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.CoordinateSystemCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Civil 3D drawing coordinate-system reporting, search, assignment and clearing tools.
    /// These commands never transform existing drawing geometry.
    /// </summary>
    public sealed class CoordinateSystemCommands
    {
        private const string NoCoordinateSystemCode = ".";
        private const int MaximumSearchResults = 25;

        [CommandMethod(
            "CE_TOOLS",
            "CE_COORDSYS",
            CommandFlags.Modal | CommandFlags.Redraw)]
        public void CoordinateSystemMenu()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                return;
            }

            var options = new PromptKeywordOptions(
                "\nCoordinate Systems [Info/Assign/Search/Clear] <Info>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Info");
            options.Keywords.Add("Assign");
            options.Keywords.Add("Search");
            options.Keywords.Add("Clear");

            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel)
            {
                return;
            }

            string mode = result.Status == PromptStatus.None
                ? "Info"
                : result.StringResult;

            if (string.Equals(mode, "Assign", StringComparison.OrdinalIgnoreCase))
            {
                AssignCoordinateSystem(document);
            }
            else if (string.Equals(mode, "Search", StringComparison.OrdinalIgnoreCase))
            {
                SearchCoordinateSystems(document);
            }
            else if (string.Equals(mode, "Clear", StringComparison.OrdinalIgnoreCase))
            {
                ClearCoordinateSystem(document);
            }
            else
            {
                ReportCoordinateSystem(document);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_COORDSYSINFO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CoordinateSystemInfo()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                ReportCoordinateSystem(document);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_COORDSYSASSIGN", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CoordinateSystemAssign()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                AssignCoordinateSystem(document);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_COORDSYSSEARCH", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CoordinateSystemSearch()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                SearchCoordinateSystems(document);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_COORDSYSCLEAR", CommandFlags.Modal | CommandFlags.Redraw)]
        public void CoordinateSystemClear()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document != null)
            {
                ClearCoordinateSystem(document);
            }
        }

        private static void ReportCoordinateSystem(Document document)
        {
            Editor editor = document.Editor;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                editor.WriteMessage("\nCE_COORDSYSINFO cancelled. No active Civil 3D document is available.");
                return;
            }

            try
            {
                SettingsDrawing drawingSettings = civilDocument.Settings.DrawingSettings;
                SettingsUnitZone unitZone = drawingSettings.UnitZoneSettings;
                string code = NormalizeCode(unitZone.CoordinateSystemCode);

                editor.WriteMessage("\nCE Tools Coordinate System Information");
                WriteCoordinateSystemDetails(editor, code, "Assigned system");
                editor.WriteMessage(
                    "\n  Drawing units: {0}\n  Angular units: {1}\n  Drawing scale: {2:N3}" +
                    "\n  Imperial-to-metric conversion: {3}\n  Scale inserted objects: {4}" +
                    "\n  Transformation settings active: {5}",
                    unitZone.DrawingUnits,
                    unitZone.AngularUnits,
                    unitZone.DrawingScale,
                    unitZone.ImperialToMetricConversion,
                    unitZone.ScaleObjectsFromOtherDrawings ? "Yes" : "No",
                    drawingSettings.ApplyTransformSettings ? "Yes" : "No");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_COORDSYSINFO cancelled. {0}", exception.Message);
            }
        }

        private static void AssignCoordinateSystem(Document document)
        {
            Editor editor = document.Editor;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                editor.WriteMessage("\nCE_COORDSYSASSIGN cancelled. No active Civil 3D document is available.");
                return;
            }

            PromptResult codeResult = editor.GetString(
                new PromptStringOptions(
                    "\nEnter Autodesk coordinate-system code, or run CE_COORDSYSSEARCH first: ")
                {
                    AllowSpaces = false
                });
            if (codeResult.Status != PromptStatus.OK)
            {
                return;
            }

            string requestedCode = FindCanonicalCode(codeResult.StringResult);
            if (string.IsNullOrWhiteSpace(requestedCode) ||
                IsNoCoordinateSystem(requestedCode))
            {
                editor.WriteMessage(
                    "\nCE_COORDSYSASSIGN cancelled. The entered coordinate-system code is not valid. " +
                    "Use CE_COORDSYSSEARCH to find an available code.");
                return;
            }

            SettingsUnitZone unitZone = civilDocument.Settings.DrawingSettings.UnitZoneSettings;
            string originalCode = NormalizeCode(unitZone.CoordinateSystemCode);

            if (string.Equals(originalCode, requestedCode, StringComparison.OrdinalIgnoreCase))
            {
                editor.WriteMessage("\nCE_COORDSYSASSIGN: {0} is already assigned.", requestedCode);
                WriteCoordinateSystemDetails(editor, requestedCode, "Current system");
                return;
            }

            editor.WriteMessage("\nCoordinate-system assignment preview");
            WriteCoordinateSystemDetails(editor, originalCode, "Current system");
            WriteCoordinateSystemDetails(editor, requestedCode, "Proposed system");
            editor.WriteMessage(
                "\n  WARNING: Assigning a coordinate system does not move, rotate, scale or transform existing geometry.");

            if (!Confirm(editor, "Assign the proposed coordinate system"))
            {
                editor.WriteMessage("\nCE_COORDSYSASSIGN cancelled. The drawing coordinate system was not changed.");
                return;
            }

            try
            {
                unitZone.CoordinateSystemCode = requestedCode;
                editor.WriteMessage(
                    "\nCE_COORDSYSASSIGN complete. Drawing coordinate system assigned: {0}.",
                    requestedCode);
            }
            catch (System.Exception exception)
            {
                TryRestoreCode(unitZone, originalCode);
                editor.WriteMessage(
                    "\nCE_COORDSYSASSIGN cancelled. The original coordinate system was retained where possible. {0}",
                    exception.Message);
            }
        }

        private static void SearchCoordinateSystems(Document document)
        {
            Editor editor = document.Editor;
            PromptResult searchResult = editor.GetString(
                new PromptStringOptions(
                    "\nSearch coordinate systems by code, description, category, projection or datum: ")
                {
                    AllowSpaces = true
                });
            if (searchResult.Status != PromptStatus.OK)
            {
                return;
            }

            string searchText = (searchResult.StringResult ?? string.Empty).Trim();
            if (searchText.Length == 0)
            {
                editor.WriteMessage("\nCE_COORDSYSSEARCH cancelled. Enter search text.");
                return;
            }

            string[] codes;
            try
            {
                codes = SettingsUnitZone.GetAllCodes();
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nCE_COORDSYSSEARCH cancelled. {0}", exception.Message);
                return;
            }

            Array.Sort(codes, StringComparer.OrdinalIgnoreCase);
            var matches = new List<CoordinateSystemSummary>();
            int totalMatches = 0;

            foreach (string code in codes)
            {
                if (IsNoCoordinateSystem(code))
                {
                    continue;
                }

                SettingsCoordinateSystem coordinateSystem;
                try
                {
                    coordinateSystem = SettingsUnitZone.GetCoordinateSystemByCode(code);
                }
                catch
                {
                    continue;
                }

                if (!Matches(coordinateSystem, searchText))
                {
                    continue;
                }

                totalMatches++;
                if (matches.Count < MaximumSearchResults)
                {
                    matches.Add(new CoordinateSystemSummary(coordinateSystem));
                }
            }

            if (totalMatches == 0)
            {
                editor.WriteMessage(
                    "\nCE_COORDSYSSEARCH: no coordinate systems matched \"{0}\".",
                    searchText);
                return;
            }

            editor.WriteMessage(
                "\nCE_COORDSYSSEARCH results for \"{0}\". Showing {1} of {2} match(es):",
                searchText,
                matches.Count,
                totalMatches);

            foreach (CoordinateSystemSummary match in matches)
            {
                editor.WriteMessage(
                    "\n  {0} — {1}; Category={2}; Projection={3}; Datum={4}; Unit={5}",
                    match.Code,
                    ValueOrNotSet(match.Description),
                    ValueOrNotSet(match.Category),
                    ValueOrNotSet(match.Projection),
                    ValueOrNotSet(match.Datum),
                    ValueOrNotSet(match.Unit));
            }

            if (totalMatches > matches.Count)
            {
                editor.WriteMessage(
                    "\n  Refine the search text to reduce the result list.");
            }
        }

        private static void ClearCoordinateSystem(Document document)
        {
            Editor editor = document.Editor;
            CivilDocument civilDocument = CivilApplication.ActiveDocument;
            if (civilDocument == null)
            {
                editor.WriteMessage("\nCE_COORDSYSCLEAR cancelled. No active Civil 3D document is available.");
                return;
            }

            SettingsUnitZone unitZone = civilDocument.Settings.DrawingSettings.UnitZoneSettings;
            string originalCode = NormalizeCode(unitZone.CoordinateSystemCode);
            if (IsNoCoordinateSystem(originalCode))
            {
                editor.WriteMessage("\nCE_COORDSYSCLEAR: this drawing already has no selected coordinate-system zone.");
                return;
            }

            WriteCoordinateSystemDetails(editor, originalCode, "Current system");
            editor.WriteMessage(
                "\n  WARNING: Clearing the coordinate-system assignment does not transform existing geometry.");

            if (!Confirm(editor, "Clear the assigned coordinate system"))
            {
                editor.WriteMessage("\nCE_COORDSYSCLEAR cancelled. The drawing coordinate system was not changed.");
                return;
            }

            try
            {
                unitZone.CoordinateSystemCode = NoCoordinateSystemCode;
                editor.WriteMessage("\nCE_COORDSYSCLEAR complete. No coordinate-system zone is now selected.");
            }
            catch (System.Exception exception)
            {
                TryRestoreCode(unitZone, originalCode);
                editor.WriteMessage(
                    "\nCE_COORDSYSCLEAR cancelled. The original coordinate system was retained where possible. {0}",
                    exception.Message);
            }
        }

        private static string FindCanonicalCode(string enteredCode)
        {
            string candidate = (enteredCode ?? string.Empty).Trim();
            if (candidate.Length == 0)
            {
                return null;
            }

            string[] codes;
            try
            {
                codes = SettingsUnitZone.GetAllCodes();
            }
            catch
            {
                return null;
            }

            foreach (string code in codes)
            {
                if (string.Equals(code, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return code;
                }
            }

            return null;
        }

        private static bool Matches(SettingsCoordinateSystem coordinateSystem, string searchText)
        {
            return Contains(coordinateSystem.Code, searchText) ||
                   Contains(coordinateSystem.Description, searchText) ||
                   Contains(coordinateSystem.Category, searchText) ||
                   Contains(coordinateSystem.Projection, searchText) ||
                   Contains(coordinateSystem.Datum, searchText) ||
                   Contains(coordinateSystem.Unit, searchText);
        }

        private static bool Contains(object value, string searchText)
        {
            string text = value == null ? string.Empty : value.ToString();
            return text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void WriteCoordinateSystemDetails(
            Editor editor,
            string code,
            string heading)
        {
            if (IsNoCoordinateSystem(code))
            {
                editor.WriteMessage("\n  {0}: <No coordinate-system zone selected>", heading);
                return;
            }

            try
            {
                SettingsCoordinateSystem coordinateSystem =
                    SettingsUnitZone.GetCoordinateSystemByCode(code);
                editor.WriteMessage(
                    "\n  {0}: {1}" +
                    "\n    Description: {2}" +
                    "\n    Category: {3}" +
                    "\n    Projection: {4}" +
                    "\n    Datum: {5}" +
                    "\n    Unit: {6}",
                    heading,
                    coordinateSystem.Code,
                    ValueOrNotSet(coordinateSystem.Description),
                    ValueOrNotSet(coordinateSystem.Category),
                    ValueOrNotSet(coordinateSystem.Projection),
                    ValueOrNotSet(coordinateSystem.Datum),
                    ValueOrNotSet(coordinateSystem.Unit));
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage(
                    "\n  {0}: {1}; details unavailable: {2}",
                    heading,
                    code,
                    exception.Message);
            }
        }

        private static bool Confirm(Editor editor, string message)
        {
            var options = new PromptKeywordOptions(
                "\n" + message + "? [Yes/No] <No>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("Yes");
            options.Keywords.Add("No");

            PromptResult result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK &&
                   string.Equals(result.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
        }

        private static void TryRestoreCode(SettingsUnitZone unitZone, string originalCode)
        {
            try
            {
                unitZone.CoordinateSystemCode = IsNoCoordinateSystem(originalCode)
                    ? NoCoordinateSystemCode
                    : originalCode;
            }
            catch
            {
                // The original exception is more useful to the user than a restore failure.
            }
        }

        private static string NormalizeCode(string code)
        {
            string value = (code ?? string.Empty).Trim();
            return value.Length == 0 ? NoCoordinateSystemCode : value;
        }

        private static bool IsNoCoordinateSystem(string code)
        {
            string value = (code ?? string.Empty).Trim();
            return value.Length == 0 || value == NoCoordinateSystemCode;
        }

        private static string ValueOrNotSet(object value)
        {
            string text = value == null ? string.Empty : value.ToString();
            return string.IsNullOrWhiteSpace(text) ? "<Not set>" : text;
        }

        private sealed class CoordinateSystemSummary
        {
            public CoordinateSystemSummary(SettingsCoordinateSystem coordinateSystem)
            {
                Code = coordinateSystem.Code;
                Description = ValueOrNotSet(coordinateSystem.Description);
                Category = ValueOrNotSet(coordinateSystem.Category);
                Projection = ValueOrNotSet(coordinateSystem.Projection);
                Datum = ValueOrNotSet(coordinateSystem.Datum);
                Unit = ValueOrNotSet(coordinateSystem.Unit);
            }

            public string Code { get; }

            public string Description { get; }

            public string Category { get; }

            public string Projection { get; }

            public string Datum { get; }

            public string Unit { get; }
        }
    }
}
