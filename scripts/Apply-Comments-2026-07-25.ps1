[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$changedFiles = New-Object System.Collections.Generic.List[string]

function Replace-ExactText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RelativePath,

        [Parameter(Mandatory = $true)]
        [string]$OldText,

        [Parameter(Mandatory = $true)]
        [string]$NewText,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $path = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path $path)) {
        throw "Comment source file was not found: $RelativePath"
    }

    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $oldNormalised = $OldText.Replace("`r`n", "`n")
    $newNormalised = $NewText.Replace("`r`n", "`n")

    if ($text.Contains($newNormalised) -and -not $text.Contains($oldNormalised)) {
        return
    }

    if (-not $text.Contains($oldNormalised)) {
        throw "Could not apply comment change '$Description' in '$RelativePath'. The expected source text was not found."
    }

    $updated = $text.Replace($oldNormalised, $newNormalised)
    [System.IO.File]::WriteAllText($path, $updated, $utf8NoBom)
    if (-not $changedFiles.Contains($RelativePath)) {
        $changedFiles.Add($RelativePath)
    }
}

$projectFile = "src\CE.Tools.Civil3D\ProjectSetupCommands.cs"
$oldProjectInput = @'
            ProjectMetadata existing = ReadProjectMetadata(
                document.Database,
                ProjectRecordName);
            var proposed = new ProjectMetadata();

            for (int index = 0; index < FieldOrder.Length; index++)
            {
                string field = FieldOrder[index];
                string currentValue = existing.Get(field);
                if (string.IsNullOrWhiteSpace(currentValue) &&
                    string.Equals(field, "Units", StringComparison.OrdinalIgnoreCase))
                {
                    currentValue = "Metric";
                }

                PromptResult result = PromptForValue(editor, field, currentValue);
                if (result.Status != PromptStatus.OK)
                {
                    editor.WriteMessage(
                        "\nCE_PROJECTSETUP cancelled. Existing project metadata was not changed.");
                    return;
                }

                proposed.Set(field, result.StringResult == null
                    ? string.Empty
                    : result.StringResult.Trim());
            }
'@
$newProjectInput = @'
            ProjectMetadata existing = ReadProjectMetadata(
                document.Database,
                ProjectRecordName);
            var initialValues = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < FieldOrder.Length; index++)
            {
                string field = FieldOrder[index];
                string currentValue = existing.Get(field);
                if (string.IsNullOrWhiteSpace(currentValue) &&
                    string.Equals(field, "Units", StringComparison.OrdinalIgnoreCase))
                {
                    currentValue = "Metric";
                }

                initialValues[field] = currentValue ?? string.Empty;
            }

            var setupWindow = new ProjectSetupPopupWindow(
                FieldOrder,
                initialValues);
            AcApplication.ShowModalWindow(setupWindow);
            if (!setupWindow.Accepted)
            {
                editor.WriteMessage(
                    "\nCE_PROJECTSETUP cancelled. Existing project metadata was not changed.");
                return;
            }

            var proposed = new ProjectMetadata();
            for (int index = 0; index < FieldOrder.Length; index++)
            {
                string field = FieldOrder[index];
                proposed.Set(field, setupWindow.GetValue(field));
            }
'@
Replace-ExactText `
    -RelativePath $projectFile `
    -OldText $oldProjectInput `
    -NewText $newProjectInput `
    -Description "replace separate project prompts with one project setup popup"

$parkingFile = "src\CE.Tools.Civil3D\ParkingCommands.cs"
Replace-ExactText `
    -RelativePath $parkingFile `
    -OldText 'CreateSingleRow(document);' `
    -NewText 'ClosedParkingBayWorkflow.CreateSingleRow(document);' `
    -Description "route single parking rows to closed bay polyline generation"
Replace-ExactText `
    -RelativePath $parkingFile `
    -OldText 'CreateDoubleRow(document);' `
    -NewText 'ClosedParkingBayWorkflow.CreateDoubleRow(document);' `
    -Description "route double parking rows to closed bay polyline generation"

$alignmentFile = "src\CE.Tools.Civil3D\AlignmentCommands.cs"
Replace-ExactText `
    -RelativePath $alignmentFile `
    -OldText 'PlaceStationOffsetLabel(document);' `
    -NewText 'document.SendStringToExecute("CE_ALLABELX ", true, false, true);' `
    -Description "route alignment labels to shared 1.8, 2.0 and 5.0 annotation settings"

$ribbonFile = "src\CE.Tools.Civil3D\PluginEntry.cs"
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText '                Title = title.ToUpperInvariant()' `
    -NewText '                Title = PrefixRibbonText(title).ToUpperInvariant()' `
    -Description "prefix CE Tools ribbon panel names"
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText '                Text = text,' `
    -NewText '                Text = PrefixRibbonText(text),' `
    -Description "prefix CE Tools ribbon menu names"
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText '                Text = definition.Text,' `
    -NewText '                Text = PrefixRibbonText(definition.Text),' `
    -Description "prefix CE Tools ribbon command names"

$oldPrefixInsertion = @'
        private static RibbonCommandDefinition Cmd(string text, string command, string toolTip)
'@
$newPrefixInsertion = @'
        private static string PrefixRibbonText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "CE";

            string trimmed = text.TrimStart();
            if (trimmed.StartsWith("CE -", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("CE TOOLS", StringComparison.OrdinalIgnoreCase))
            {
                return text;
            }

            return "CE - " + text;
        }

        private static RibbonCommandDefinition Cmd(string text, string command, string toolTip)
'@
$ribbonPath = Join-Path $repositoryRoot $ribbonFile
$ribbonSource = [System.IO.File]::ReadAllText($ribbonPath)
if (-not $ribbonSource.Contains("private static string PrefixRibbonText(string text)")) {
    Replace-ExactText `
        -RelativePath $ribbonFile `
        -OldText $oldPrefixInsertion `
        -NewText $newPrefixInsertion `
        -Description "add shared CE ribbon-name prefixing"
}

$oldFloatingEntry = @'
                    Cmd("Restore Cleared Information", "CE_PROJECTRESTORE ", "Restore the values saved before the last project clear.")),
'@
$newFloatingEntry = @'
                    Cmd("Restore Cleared Information", "CE_PROJECTRESTORE ", "Restore the values saved before the last project clear."),
                    Cmd("Floating Tools Window", "CE_TOOLSPALETTE ", "Open all current CE Tools ribbon commands as individual buttons in a draggable modeless window.")),
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldFloatingEntry `
    -NewText $newFloatingEntry `
    -Description "add the floating CE Tools launcher to Project Setup"

$oldProjectCommands = @'
                    Cmd("Restore Cleared Information", "CE_PROJECTRESTORE ", "Restore the values saved before the last project clear."),
                    Cmd("Floating Tools Window", "CE_TOOLSPALETTE ", "Open all current CE Tools ribbon commands as individual buttons in a draggable modeless window.")),
'@
$newProjectCommands = @'
                    Cmd("Restore Cleared Information", "CE_PROJECTRESTORE ", "Restore the values saved before the last project clear."),
                    Cmd("Project Style Centre", "CE_PROJECTSTYLES ", "Select and store project alignment, profile, point, corridor, surface and network styles."),
                    Cmd("Project Style Information", "CE_PROJECTSTYLEINFO ", "Review stored project style selections and optionally place a table."),
                    Cmd("Clear Project Style Selections", "CE_PROJECTSTYLECLEAR ", "Clear only stored choices without deleting Civil 3D styles."),
                    Cmd("Enable Full Undo Recording", "CE_UNDOSETTINGS ", "Enable AutoCAD full undo recording for CE Tools workflows."),
                    Cmd("Undo One Step", "CE_UNDO ", "Undo one AutoCAD operation."),
                    Cmd("Redo One Step", "CE_REDO ", "Redo the last undone AutoCAD operation while REDO remains available."),
                    Cmd("Floating Tools Window", "CE_TOOLSPALETTE ", "Open all current CE Tools ribbon commands as individual buttons in a draggable modeless window.")),
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldProjectCommands `
    -NewText $newProjectCommands `
    -Description "add project styles and undo controls to the Project ribbon menu"

$oldDrawingCommands = @'
                    Cmd("Annotation Settings", "CE_ANNOTSETTINGS ", "Select 1.8, 2.0 or 5.0 height, marker circles and MLeader/MText/COGO output."),
                    Cmd("Colour 250 - Geometry or Annotation", "CE_COLOR250 ", "Choose geometry only or geometry plus annotation and change accepted objects to colour 250."),
                    Cmd("Polyline Direction Arrows", "CE_PLDIR ", "Add, replace or clear linked direction arrows.")),
'@
$newDrawingCommands = @'
                    Cmd("Annotation Settings", "CE_ANNOTSETTINGS ", "Select 1.8, 2.0 or 5.0 height, marker circles and MLeader/MText/COGO output."),
                    Cmd("Presentation and Dynamic Tools", "CE_PRESENTATIONTOOLS ", "Open shared annotative, table scaling, overlap, reverse and refresh workflows."),
                    Cmd("Make Selected Annotation Annotative", "CE_MAKEANNOTATIVE ", "Set supported text, dimensions, leaders and tables to the CE annotation height and annotative mode where supported."),
                    Cmd("Scale Selected Tables", "CE_TABLESCALE ", "Resize table rows, columns and text relative to the CE annotation height."),
                    Cmd("Resolve Annotation Overlaps", "CE_OVERLAPFIX ", "Displace selected overlapping text, dimensions, leaders and tables without changing their content."),
                    Cmd("Colour 250 - Geometry or Annotation", "CE_COLOR250 ", "Choose geometry only or geometry plus annotation and change accepted objects to colour 250."),
                    Cmd("Polyline Direction Arrows", "CE_PLDIR ", "Add, replace or clear linked direction arrows."),
                    Cmd("Reverse Multiple Polylines", "CE_PLREVERSE ", "Reverse multiple supported curves and queue their linked arrows and points for refresh.")),
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldDrawingCommands `
    -NewText $newDrawingCommands `
    -Description "add shared presentation and multiple-polyline reverse commands"

Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText '                    Cmd("PURGE Only", "CE_DRAWCLEAN Purge ", "Purge unused named objects.")),' `
    -NewText '                    Cmd("PURGE Only", "CE_DRAWCLEAN Purge ", "Purge unused named objects."),`n                    Cmd("Cleanup Manager Window", "CE_CLEANUPUI ", "Choose cleanup operations in a CE Tools popup window.")),' `
    -Description "add the cleanup manager popup command"
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText '                    Cmd("Send Hatches Behind Linework", "CE_HATCHBACK ", "Move selected hatches to the back of draw order."))))' `
    -NewText '                    Cmd("Send Hatches Behind Linework", "CE_HATCHBACK ", "Move selected hatches to the back of draw order."),`n                    Cmd("Hatch Settings Window", "CE_HATCHUI ", "Choose hatch creation and editing actions in a CE Tools popup window."))))' `
    -Description "add the hatch settings popup command"

$oldDynamicSectionCommands = @'
                    Cmd("Detach Dynamic Cross Section", "CE_XSDETACH ", "Remove the link and keep or delete generated section geometry."),
                    Cmd("Dynamic-section Monitor", "CE_XSMONITOR ", "Report automatic update-manager and pending-refresh status."))))
'@
$newDynamicSectionCommands = @'
                    Cmd("Detach Dynamic Cross Section", "CE_XSDETACH ", "Remove the link and keep or delete generated section geometry."),
                    Cmd("Dynamic-section Monitor", "CE_XSMONITOR ", "Report automatic update-manager and pending-refresh status."),
                    Cmd("Refresh All Dynamic Data", "CE_REFRESHALL ", "Refresh linked coordinate followers, coordinate tables and BOQs and rebuild Civil surfaces and corridors."),
                    Cmd("Rebuild All Civil Objects", "CE_REBUILDALL ", "Rebuild all accessible surfaces and corridors in the current Civil 3D drawing."),
                    Cmd("Automatic Linked Refresh", "CE_AUTOREFRESH ", "Turn automatic linked coordinate and BOQ refresh on or off and show its status."),
                    Cmd("Dynamic Refresh Status", "CE_REFRESHSTATUS ", "Show linked table, follower, pending and last-refresh information."))))
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldDynamicSectionCommands `
    -NewText $newDynamicSectionCommands `
    -Description "add shared automatic refresh and rebuild commands to Analysis"

$oldInitialise = @'
            DynamicSectionUpdateManager.Initialize();
            DynamicIntersectionUpdateManager.Initialize();
            AcApplication.Idle += OnApplicationIdle;
'@
$newInitialise = @'
            DynamicSectionUpdateManager.Initialize();
            DynamicIntersectionUpdateManager.Initialize();
            CommentAutoRefreshManager.Initialize();
            AcApplication.Idle += OnApplicationIdle;
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldInitialise `
    -NewText $newInitialise `
    -Description "initialise automatic linked coordinate and BOQ refresh"

$oldTerminate = @'
            AcApplication.Idle -= OnApplicationIdle;
            DynamicIntersectionUpdateManager.Terminate();
            DynamicSectionUpdateManager.Terminate();
'@
$newTerminate = @'
            AcApplication.Idle -= OnApplicationIdle;
            CommentAutoRefreshManager.Terminate();
            DynamicIntersectionUpdateManager.Terminate();
            DynamicSectionUpdateManager.Terminate();
'@
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText $oldTerminate `
    -NewText $newTerminate `
    -Description "terminate automatic linked refresh cleanly"

$surveyFile = "src\CE.Tools.Civil3D\SurveyCoordinateWorkflowCommands.cs"
$oldCoordinateText = @'
        private static string BuildMTextCoordinate(Point3d point)
        {
            return string.Join(
                "\P",
                "Y / N: " + point.Y.ToString("N3", CultureInfo.CurrentCulture),
                "X / E: " + point.X.ToString("N3", CultureInfo.CurrentCulture),
                "Z: " + point.Z.ToString("N3", CultureInfo.CurrentCulture));
        }

        private static string BuildPlainCoordinate(Point3d point)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                "Y {0:N3}; X {1:N3}; Z {2:N3}",
                point.Y,
                point.X,
                point.Z);
        }
'@
$newCoordinateText = @'
        private static string BuildMTextCoordinate(Point3d point)
        {
            return string.Join(
                "\P",
                "X: " + point.X.ToString("N3", CultureInfo.CurrentCulture),
                "Y: " + point.Y.ToString("N3", CultureInfo.CurrentCulture),
                "Z: " + point.Z.ToString("N3", CultureInfo.CurrentCulture));
        }

        private static string BuildPlainCoordinate(Point3d point)
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                "X {0:N3}; Y {1:N3}; Z {2:N3}",
                point.X,
                point.Y,
                point.Z);
        }
'@
Replace-ExactText `
    -RelativePath $surveyFile `
    -OldText $oldCoordinateText `
    -NewText $newCoordinateText `
    -Description "use Point Name and X Y Z coordinate wording without N or E"

Replace-ExactText `
    -RelativePath $surveyFile `
    -OldText '                Math.Max(textHeight * 0.75, 0.001));' `
    -NewText '                Math.Max(textHeight * 0.25, 0.001));' `
    -Description "reduce coordinate marker circle relative to text height"

$oldCoordinateLinkCall = @'
                ObjectId tableId = ApplyRegisterTarget(
                    document,
                    register,
                    new List<ObjectId> { sourceId },
                    settings.TextHeight);
'@
$newCoordinateLinkCall = @'
                ObjectId tableId = ApplyRegisterTarget(
                    document,
                    register,
                    new List<ObjectId> { sourceId },
                    settings.TextHeight);
                DynamicCoordinateLinkStore.LinkGeneratedObjects(
                    document.Database,
                    sourceId,
                    created);
                DynamicCoordinateLinkStore.Refresh(document);
'@
Replace-ExactText `
    -RelativePath $surveyFile `
    -OldText $oldCoordinateLinkCall `
    -NewText $newCoordinateLinkCall `
    -Description "link coordinate annotations markers and crosses dynamically to their source points"

Replace-ExactText `
    -RelativePath $surveyFile `
    -OldText '                "\nPick insertion point for the compact Y-X-Z vertex table: ");' `
    -NewText '                "\nPick insertion point for the compact X-Y-Z vertex table: ");' `
    -Description "rename the polyline vertex table prompt to X Y Z"

$oldPolylineSettings = @'
            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, false, out settings)) return;

            var locations = new Point3dCollection();
'@
$newPolylineSettings = @'
            AnnotationOptions settings;
            if (!AnnotationSettingsStore.Prepare(document, false, out settings)) return;
            bool followPolyline = PromptYesNo(
                editor,
                "Keep created COGO points dynamically linked to polyline vertices",
                true);

            var locations = new Point3dCollection();
'@
Replace-ExactText `
    -RelativePath $surveyFile `
    -OldText $oldPolylineSettings `
    -NewText $newPolylineSettings `
    -Description "offer dynamic polyline vertex point following"

$oldPolylineTable = @'
                ObjectId tableId = CreateLinkedTable(
                    document.Database,
                    ToWorld(editor, insertion.Value),
                    createdIds,
                    settings.TextHeight,
                    "POLYLINE VERTEX POINTS — Y / X / Z");

                editor.WriteMessage(
'@
$newPolylineTable = @'
                ObjectId tableId = CreateLinkedTable(
                    document.Database,
                    ToWorld(editor, insertion.Value),
                    createdIds,
                    settings.TextHeight,
                    "POLYLINE VERTEX POINTS — X / Y / Z");
                if (followPolyline)
                {
                    DynamicCoordinateLinkStore.LinkPolylineVertices(
                        document.Database,
                        entityResult.ObjectId,
                        createdIds);
                }
                DynamicCoordinateLinkStore.Refresh(document);

                editor.WriteMessage(
'@
Replace-ExactText `
    -RelativePath $surveyFile `
    -OldText $oldPolylineTable `
    -NewText $newPolylineTable `
    -Description "link polyline vertex COGO points and their table dynamically"

$oldCoordinateTable = @'
            const int columns = 5;
            table.SetSize(rows.Count + 2, columns);
            double height = NormalizeHeight(textHeight);
            table.SetRowHeight(Math.Max(height * 1.65, 3.0));
            table.SetColumnWidth(Math.Max(height * 5.5, 12.0));
            table.Cells[0, 0].TextString = title;
            table.MergeCells(CellRange.Create(table, 0, 0, 0, columns - 1));

            string[] headings =
            {
                "POINT",
                "POINT NAME",
                "Y / NORTHING",
                "X / EASTING",
                "Z / ELEVATION"
            };
            for (int column = 0; column < headings.Length; column++)
            {
                table.Cells[1, column].TextString = headings[column];
            }

            for (int index = 0; index < rows.Count; index++)
            {
                CoordinateRow row = rows[index];
                int tableRow = index + 2;
                table.Cells[tableRow, 0].TextString = row.Point;
                table.Cells[tableRow, 1].TextString = row.PointName;
                table.Cells[tableRow, 2].TextString = row.Y.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[tableRow, 3].TextString = row.X.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[tableRow, 4].TextString = row.Z.ToString("N3", CultureInfo.CurrentCulture);
            }
'@
$newCoordinateTable = @'
            const int columns = 4;
            table.SetSize(rows.Count + 2, columns);
            double height = NormalizeHeight(textHeight);
            table.SetRowHeight(Math.Max(height * 1.65, height * 1.65));
            table.SetColumnWidth(Math.Max(height * 7.0, height * 7.0));
            table.Cells[0, 0].TextString = title;
            table.MergeCells(CellRange.Create(table, 0, 0, 0, columns - 1));

            string[] headings =
            {
                "POINT NAME",
                "X",
                "Y",
                "Z"
            };
            for (int column = 0; column < headings.Length; column++)
            {
                table.Cells[1, column].TextString = headings[column];
                table.Cells[1, column].TextHeight = height;
            }

            for (int index = 0; index < rows.Count; index++)
            {
                CoordinateRow row = rows[index];
                int tableRow = index + 2;
                table.Cells[tableRow, 0].TextString = row.PointName;
                table.Cells[tableRow, 1].TextString = row.X.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[tableRow, 2].TextString = row.Y.ToString("N3", CultureInfo.CurrentCulture);
                table.Cells[tableRow, 3].TextString = row.Z.ToString("N3", CultureInfo.CurrentCulture);
                for (int column = 0; column < columns; column++)
                    table.Cells[tableRow, column].TextHeight = height;
            }
'@
Replace-ExactText `
    -RelativePath $surveyFile `
    -OldText $oldCoordinateTable `
    -NewText $newCoordinateTable `
    -Description "remove point-number column and use Point Name X Y Z coordinate columns"

Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText 'Create a compact linked Y-X-Z table from selected COGO or AutoCAD points.' `
    -NewText 'Create a compact linked Point Name, X, Y, Z table from selected COGO or AutoCAD points.' `
    -Description "update coordinate table ribbon wording"
Replace-ExactText `
    -RelativePath $ribbonFile `
    -OldText 'Create sequential COGO points in polyline direction and a linked Point Name, Y, X, Z table.' `
    -NewText 'Create sequential dynamic COGO points in polyline direction and a linked Point Name, X, Y, Z table.' `
    -Description "update polyline vertex table ribbon wording"

$boqFile = "src\CE.Tools.Civil3D\BillOfQuantitiesCommands.cs"
$oldLengthFallback = @'
            return TryReadDoubleProperty(
                databaseObject,
                out length,
                "Length3DCenterToCenter",
                "Length2DCenterToCenter",
                "Length3D",
                "Length2D",
                "Length");
        }

        private static bool TryGetArea(Entity entity, out double area)
'@
$newLengthFallback = @'
            if (TryReadDoubleProperty(
                databaseObject,
                out length,
                "Length3DCenterToCenter",
                "Length2DCenterToCenter",
                "Length3D",
                "Length2D",
                "Length"))
            {
                return true;
            }

            return TryGetEndpointDistance(databaseObject, out length);
        }

        private static bool TryGetEndpointDistance(
            object value,
            out double length)
        {
            length = 0.0;
            string[,] pairs =
            {
                { "StartPoint", "EndPoint" },
                { "StartPointLocation", "EndPointLocation" },
                { "StartLocation", "EndLocation" },
                { "StartPosition", "EndPosition" }
            };
            for (int index = 0; index < pairs.GetLength(0); index++)
            {
                Point3d start;
                Point3d end;
                if (TryReadPointProperty(value, pairs[index, 0], out start) &&
                    TryReadPointProperty(value, pairs[index, 1], out end))
                {
                    length = start.DistanceTo(end);
                    if (IsFinitePositive(length)) return true;
                }
            }
            return false;
        }

        private static bool TryReadPointProperty(
            object value,
            string propertyName,
            out Point3d point)
        {
            point = Point3d.Origin;
            if (value == null) return false;
            try
            {
                PropertyInfo property = value.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Public | BindingFlags.Instance);
                if (property == null || property.GetIndexParameters().Length != 0)
                    return false;
                object raw = property.GetValue(value, null);
                if (!(raw is Point3d)) return false;
                point = (Point3d)raw;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetArea(Entity entity, out double area)
'@
Replace-ExactText `
    -RelativePath $boqFile `
    -OldText $oldLengthFallback `
    -NewText $newLengthFallback `
    -Description "read actual pipe and service lengths from endpoint properties before falling back to counts"

if ($changedFiles.Count -eq 0) {
    Write-Host "25 July 2026 comment corrections are already applied." -ForegroundColor DarkGray
}
else {
    Write-Host "Applied 25 July 2026 comment corrections:" -ForegroundColor Green
    foreach ($file in $changedFiles) {
        Write-Host "  $file"
    }
}
