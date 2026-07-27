$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Normalize-Newlines {
    param([string]$Value)
    if ($null -eq $Value) { return $Value }
    return $Value.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Read-Source {
    param([string]$RelativePath)
    $path = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path $path)) {
        throw "Compile-fix source was not found: $path"
    }
    return @{
        Path = $path
        Text = Normalize-Newlines ([System.IO.File]::ReadAllText($path))
    }
}

function Write-Source {
    param($Source)
    [System.IO.File]::WriteAllText($Source.Path, $Source.Text, $utf8)
}

function Replace-Required {
    param(
        [string]$RelativePath,
        [string]$OldText,
        [string]$NewText,
        [string]$Description
    )
    $source = Read-Source $RelativePath
    $oldValue = Normalize-Newlines $OldText
    $newValue = Normalize-Newlines $NewText
    if (-not $source.Text.Contains($oldValue)) {
        if ($source.Text.Contains($newValue)) {
            Write-Host "  already fixed: $Description" -ForegroundColor DarkGray
            return
        }
        throw "Could not apply compile fix '$Description' in '$RelativePath'."
    }
    $source.Text = $source.Text.Replace($oldValue, $newValue)
    Write-Source $source
    Write-Host "  fixed: $Description" -ForegroundColor Green
}

Write-Host "Applying Civil 3D 2023 integration compile fixes..." -ForegroundColor Cyan

# Project presentation uses Point3d for drawing extents.
Replace-Required `
    "src\CE.Tools.Civil3D\ProjectPresentationCommands.cs" `
    "using Autodesk.AutoCAD.EditorInput;" `
    "using Autodesk.AutoCAD.EditorInput;`nusing Autodesk.AutoCAD.Geometry;" `
    "add the AutoCAD Geometry namespace to project presentation"

# Older and newer report call shapes are both supported. The five-argument
# form treats the first row as the table heading row.
$gridOverload = @'
        public static void ShowReportAndOfferTable(
            Document document,
            string title,
            string note,
            IList<IList<string>> rowsWithHeader,
            string tableTitle)
        {
            var columns = new List<string>();
            var rows = new List<IList<string>>();
            if (rowsWithHeader != null && rowsWithHeader.Count > 0)
            {
                IList<string> header = rowsWithHeader[0];
                if (header != null)
                {
                    foreach (string value in header)
                        columns.Add(value ?? string.Empty);
                }
                for (int index = 1; index < rowsWithHeader.Count; index++)
                    rows.Add(rowsWithHeader[index] ?? new List<string>());
            }
            ShowReportAndOfferTable(
                document,
                title,
                note,
                columns,
                rows,
                tableTitle);
        }

'@
Replace-Required `
    "src\CE.Tools.Civil3D\GridReportPresenter.cs" `
    "        public static void ShowReportAndOfferTable(`n            Document document,`n            string title,`n            string note,`n            IList<string> columns," `
    ($gridOverload + "        public static void ShowReportAndOfferTable(`n            Document document,`n            string title,`n            string note,`n            IList<string> columns,") `
    "support report rows with an embedded heading row"

# Preserve the Phase 7 source call shape while retaining the current core API.
$deckOverload = @'
        public PresentationDeck(
            string title,
            string author,
            string company,
            string subject,
            IEnumerable<PresentationSlide> slides)
            : this(title, subject, author, company, DateTime.UtcNow, slides)
        {
        }

'@
Replace-Required `
    "src\CE.Tools.Core\SimplePresentationPackage.cs" `
    "        public PresentationDeck(string title, string subject, string author, string company, DateTime createdUtc, IEnumerable<PresentationSlide> slides)" `
    ($deckOverload + "        public PresentationDeck(string title, string subject, string author, string company, DateTime createdUtc, IEnumerable<PresentationSlide> slides)") `
    "add the five-argument presentation deck compatibility constructor"

$slideOverload = @'
        public PresentationSlide(
            string title,
            string subtitle,
            IEnumerable<PresentationMetric> metrics,
            IEnumerable<string> bullets)
            : this(title, subtitle, bullets, metrics)
        {
        }

'@
Replace-Required `
    "src\CE.Tools.Core\SimplePresentationPackage.cs" `
    "        public PresentationSlide(string title, string subtitle, IEnumerable<string> bullets, IEnumerable<PresentationMetric> metrics)" `
    ($slideOverload + "        public PresentationSlide(string title, string subtitle, IEnumerable<string> bullets, IEnumerable<PresentationMetric> metrics)") `
    "support the metric-first presentation slide call shape"

# AutoCAD table collections expose LINQ Count() rather than a Count property
# in the Civil 3D 2023 managed API.
Replace-Required `
    "src\CE.Tools.Civil3D\ProjectPresentationCommands.cs" `
    "            snapshot.LayerCount = layers.Count;" `
    "            snapshot.LayerCount = layers.Cast<ObjectId>().Count();" `
    "count layer records through LINQ"
Replace-Required `
    "src\CE.Tools.Civil3D\ProjectPresentationCommands.cs" `
    "            snapshot.BlockDefinitionCount = blocks.Count;" `
    "            snapshot.BlockDefinitionCount = blocks.Cast<ObjectId>().Count();" `
    "count block definitions through LINQ"

# Disambiguate System.Exception from Autodesk.AutoCAD.Runtime.Exception.
Replace-Required `
    "src\CE.Tools.Civil3D\EngineeringAssetLibraryCommands.cs" `
    "            catch (Exception exception)" `
    "            catch (System.Exception exception)" `
    "disambiguate the engineering asset catalog exception"

# Layout plot configuration naming differs by host release. Use the existing
# reflection helper so both ConfigName and PlotConfigurationName are accepted.
Replace-Required `
    "src\CE.Tools.Civil3D\ModelDesignAuditCommands.cs" `
    "                    layout.CanonicalMediaName,`n                    layout.ConfigName));" `
    "                    layout.CanonicalMediaName,`n                    Convert.ToString(`n                        ReadProperty(layout, \"ConfigName\") ??`n                        ReadProperty(layout, \"PlotConfigurationName\"),`n                        CultureInfo.CurrentCulture)));" `
    "read the layout plot configuration through version-tolerant reflection"

# Entity visibility differs between AutoCAD managed API versions. Avoid a
# direct compile-time dependency on either the bool or enum property shape.
Replace-Required `
    "src\CE.Tools.Civil3D\FloodResultReviewCommands.cs" `
    "                        entity.Visibility = show ? Visibility.Visible : Visibility.Invisible;" `
    "                        SetEntityVisibility(entity, show);" `
    "set imported flood-result visibility through reflection"

$visibilityHelper = @'
        private static void SetEntityVisibility(Entity entity, bool visible)
        {
            if (entity == null) return;
            Type type = entity.GetType();
            System.Reflection.PropertyInfo property = type.GetProperty(
                "Visible",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
            if (property != null && property.CanWrite && property.PropertyType == typeof(bool))
            {
                property.SetValue(entity, visible, null);
                return;
            }

            property = type.GetProperty(
                "Visibility",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
            if (property != null && property.CanWrite && property.PropertyType.IsEnum)
            {
                object value = Enum.Parse(
                    property.PropertyType,
                    visible ? "Visible" : "Invisible",
                    true);
                property.SetValue(entity, value, null);
            }
        }

'@
Replace-Required `
    "src\CE.Tools.Civil3D\FloodResultReviewCommands.cs" `
    "        private static List<IList<string>> BuildPropertySummaryRows(FloodAnalysisResult analysis)" `
    ($visibilityHelper + "        private static List<IList<string>> BuildPropertySummaryRows(FloodAnalysisResult analysis)") `
    "add version-tolerant entity visibility helper"

# Definite assignment for the chained settings validation.
Replace-Required `
    "src\CE.Tools.Civil3D\SewerExcavationCommentCommands.cs" `
    "            double units;`n            double side;`n            double width;`n            double bedding;`n            double cover;" `
    "            double units = 0.0;`n            double side = 0.0;`n            double width = 0.0;`n            double bedding = 0.0;`n            double cover = 0.0;" `
    "initialise sewer excavation settings before chained validation"

# C# cannot pass properties as out parameters. Resolve each style name into a
# local variable and assign the result back to the settings object.
$oldRoadStyles = @'
            result.AlignmentStyleId = ResolveStyle(
                document.Database,
                ReadPropertyPath(civilDocument, "Styles", "AlignmentStyles"),
                Value(selection, "Alignment Style"),
                out result.AlignmentStyleName);
            result.AlignmentLabelSetId = ResolveStyle(
                document.Database,
                ReadPropertyPath(civilDocument, "Styles", "LabelSetStyles", "AlignmentLabelSetStyles"),
                Value(selection, "Alignment Label Set Style"),
                out result.AlignmentLabelSetName);
            result.ProfileStyleId = ResolveStyle(
                document.Database,
                ReadPropertyPath(civilDocument, "Styles", "ProfileStyles"),
                Value(selection, "Profile Style"),
                out result.ProfileStyleName);
            result.ProfileLabelSetId = ResolveStyle(
                document.Database,
                ReadPropertyPath(civilDocument, "Styles", "LabelSetStyles", "ProfileLabelSetStyles"),
                Value(selection, "Profile Label Set Style"),
                out result.ProfileLabelSetName);
            result.ProfileViewStyleId = ResolveStyle(
                document.Database,
                ReadPropertyPath(civilDocument, "Styles", "ProfileViewStyles"),
                Value(selection, "Profile View Style"),
                out result.ProfileViewStyleName);
            object bandCollection = ReadPropertyPath(civilDocument, "Styles", "BandSetStyles", "ProfileViewBandSetStyles") ??
                                    ReadPropertyPath(civilDocument, "Styles", "ProfileViewBandSetStyles");
            result.BandSetStyleId = ResolveStyle(
                document.Database,
                bandCollection,
                Value(selection, "Profile View Band Set Style"),
                out result.BandSetStyleName);
'@
$newRoadStyles = @'
            string alignmentStyleName;
            result.AlignmentStyleId = ResolveStyle(
                document.Database,
                ReadPropertyPath(civilDocument, "Styles", "AlignmentStyles"),
                Value(selection, "Alignment Style"),
                out alignmentStyleName);
            result.AlignmentStyleName = alignmentStyleName;

            string alignmentLabelSetName;
            result.AlignmentLabelSetId = ResolveStyle(
                document.Database,
                ReadPropertyPath(civilDocument, "Styles", "LabelSetStyles", "AlignmentLabelSetStyles"),
                Value(selection, "Alignment Label Set Style"),
                out alignmentLabelSetName);
            result.AlignmentLabelSetName = alignmentLabelSetName;

            string profileStyleName;
            result.ProfileStyleId = ResolveStyle(
                document.Database,
                ReadPropertyPath(civilDocument, "Styles", "ProfileStyles"),
                Value(selection, "Profile Style"),
                out profileStyleName);
            result.ProfileStyleName = profileStyleName;

            string profileLabelSetName;
            result.ProfileLabelSetId = ResolveStyle(
                document.Database,
                ReadPropertyPath(civilDocument, "Styles", "LabelSetStyles", "ProfileLabelSetStyles"),
                Value(selection, "Profile Label Set Style"),
                out profileLabelSetName);
            result.ProfileLabelSetName = profileLabelSetName;

            string profileViewStyleName;
            result.ProfileViewStyleId = ResolveStyle(
                document.Database,
                ReadPropertyPath(civilDocument, "Styles", "ProfileViewStyles"),
                Value(selection, "Profile View Style"),
                out profileViewStyleName);
            result.ProfileViewStyleName = profileViewStyleName;

            object bandCollection = ReadPropertyPath(civilDocument, "Styles", "BandSetStyles", "ProfileViewBandSetStyles") ??
                                    ReadPropertyPath(civilDocument, "Styles", "ProfileViewBandSetStyles");
            string bandSetStyleName;
            result.BandSetStyleId = ResolveStyle(
                document.Database,
                bandCollection,
                Value(selection, "Profile View Band Set Style"),
                out bandSetStyleName);
            result.BandSetStyleName = bandSetStyleName;
'@
Replace-Required `
    "src\CE.Tools.Civil3D\RoadProductionCommentCommands.cs" `
    $oldRoadStyles `
    $newRoadStyles `
    "resolve road production style names through local out variables"

# CivilDocument.GetSurfaceIds returns AutoCAD's non-generic ObjectIdCollection
# in Civil 3D 2023, so build the name set through an explicit loop.
$oldSurfaceNames = @'
                var existingNames = new HashSet<string>(
                    civilDocument.GetSurfaceIds()
                        .Select(id => transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false))
                        .Select(ReadName),
                    StringComparer.OrdinalIgnoreCase);
'@
$newSurfaceNames = @'
                var existingNames = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId surfaceId in civilDocument.GetSurfaceIds())
                {
                    DBObject existingSurface = transaction.GetObject(
                        surfaceId,
                        OpenMode.ForRead,
                        false);
                    existingNames.Add(ReadName(existingSurface));
                }
'@
Replace-Required `
    "src\CE.Tools.Civil3D\SurfaceSpikeHoleRepairCommands.cs" `
    $oldSurfaceNames `
    $newSurfaceNames `
    "enumerate Civil 3D surface identifiers without generic LINQ"

Write-Host "Civil 3D 2023 integration compile fixes completed." -ForegroundColor Green
