using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.RoadNameAnnotationCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Places one readable, centred road-name label above each selected/all
    /// Civil 3D road alignment. Re-running the command updates the existing
    /// CE label linked to that alignment instead of creating duplicates.
    /// </summary>
    public sealed class RoadNameAnnotationCommands
    {
        private const string AppName = "CE_ROAD_NAME_ANNOTATION";
        private const string SettingsKey = "CE_ROAD_NAME_ANNOTATION_SETTINGS";
        private const string DefaultLayer = "CE-ROAD-NAME";
        private const double GeometryTolerance = 1e-8;

        [CommandMethod(
            "CE_TOOLS",
            "CE_ROADNAMEANNOTATE",
            CommandFlags.Modal |
            CommandFlags.UsePickSet |
            CommandFlags.Redraw)]
        public void AnnotateRoadNames()
        {
            Document document =
                AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civilDocument =
                CivilApplication.ActiveDocument;
            if (document == null ||
                civilDocument == null)
                return;

            RoadNameSettings settings =
                RoadNameSettings.Read(
                    document.Database,
                    SettingsKey);

            var model =
                new ProductionSettingsDialogModel(
                    "CE Tools - Road Names Above Roads",
                    "Place one road name centred on each road alignment and offset it above the road. Use every road in the drawing or only multiple selected road alignments/corridors.");

            model.AddChoice(
                "Scope",
                "01 Roads",
                "Roads to label",
                settings.Scope,
                "All roads uses road corridor baselines plus recognised RD/ROAD alignments. Selected roads accepts multiple Civil 3D alignments or corridors.",
                new[]
                {
                    "All road alignments",
                    "Selected road alignments"
                });
            model.AddChoice(
                "NameSource",
                "01 Roads",
                "Road-name source",
                settings.NameSource,
                "Use the Civil 3D alignment name, or use its Description when one is present.",
                new[]
                {
                    "Alignment name",
                    "Description (fallback to name)"
                });
            model.AddText(
                "Layer",
                "02 Presentation",
                "Road-name layer",
                settings.Layer,
                "Layer used for the generated road-name MText labels.");
            model.AddPaperHeight(
                "TextHeight",
                "02 Presentation",
                "Road-name paper height",
                settings.TextHeight,
                "Annotative paper text height for every generated road name.");
            model.AddPositiveDouble(
                "Offset",
                "02 Presentation",
                "Offset above centreline (paper mm)",
                settings.OffsetPaper,
                "Perpendicular paper distance from the alignment centreline. CE Tools automatically chooses the visually upper side of the road in plan.");
            model.AddPositiveInteger(
                "Colour",
                "02 Presentation",
                "Text colour (ACI)",
                settings.ColourIndex,
                "AutoCAD colour index from 1 to 255. The default is yellow (2).");
            model.AddChoice(
                "Background",
                "02 Presentation",
                "Background mask",
                settings.BackgroundMask ? "Yes" : "No",
                "Use a background mask so the road name remains readable over corridor linework.",
                new[] { "Yes", "No" });

            if (!DisciplineWorkflowDialogs.EditSettings(model))
                return;

            settings.Scope =
                NormalizeScope(
                    model.Text("Scope"));
            settings.NameSource =
                NormalizeNameSource(
                    model.Text("NameSource"));
            settings.Layer =
                SafeLayerName(
                    model.Text("Layer"),
                    DefaultLayer);
            settings.TextHeight =
                PaperAnnotationScale.NormalizeConfiguredPaperHeight(
                    model.Double(
                        "TextHeight",
                        settings.TextHeight));
            settings.OffsetPaper =
                Math.Max(
                    0.01,
                    model.Double(
                        "Offset",
                        settings.OffsetPaper));
            settings.ColourIndex =
                Math.Max(
                    1,
                    Math.Min(
                        255,
                        model.Integer(
                            "Colour",
                            settings.ColourIndex)));
            settings.BackgroundMask =
                string.Equals(
                    model.Text("Background"),
                    "Yes",
                    StringComparison.OrdinalIgnoreCase);
            settings.Write(
                document.Database,
                SettingsKey);

            List<ObjectId> alignmentIds;
            bool allRoads =
                string.Equals(
                    settings.Scope,
                    "All road alignments",
                    StringComparison.OrdinalIgnoreCase);

            if (allRoads)
            {
                alignmentIds =
                    ReadAllRoadAlignmentIds(
                        document.Database,
                        civilDocument);
            }
            else
            {
                alignmentIds =
                    ReadSelectedRoadAlignmentIds(
                        document,
                        civilDocument);
            }

            if (alignmentIds.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nCE_ROADNAMEANNOTATE: no road alignments were found for the chosen scope.");
                return;
            }

            int created = 0;
            int updated = 0;
            int skipped = 0;
            int removedStale = 0;

            using (DocumentLock documentLock =
                document.LockDocument())
            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                EnsureRegApp(
                    document.Database,
                    transaction);

                ObjectId layerId =
                    GetOrCreateLayer(
                        document.Database,
                        transaction,
                        settings.Layer);

                BlockTableRecord modelSpace =
                    transaction.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(
                            document.Database),
                        OpenMode.ForWrite,
                        false) as BlockTableRecord;
                if (modelSpace == null)
                    return;

                Dictionary<string, MText> existing =
                    ReadExistingLabels(
                        modelSpace,
                        transaction);

                var activeHandles =
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);

                foreach (ObjectId alignmentId in
                    alignmentIds.Distinct())
                {
                    Alignment alignment = null;
                    try
                    {
                        alignment =
                            transaction.GetObject(
                                alignmentId,
                                OpenMode.ForRead,
                                false) as Alignment;
                    }
                    catch { }

                    if (alignment == null ||
                        alignment.IsReferenceObject)
                    {
                        skipped++;
                        continue;
                    }

                    string roadName =
                        ResolveRoadName(
                            alignment,
                            settings.NameSource);
                    if (string.IsNullOrWhiteSpace(roadName))
                    {
                        skipped++;
                        continue;
                    }

                    Point3d centre;
                    double rotation;
                    if (!TryResolveCentredPlacement(
                            alignment,
                            out centre,
                            out rotation))
                    {
                        skipped++;
                        continue;
                    }

                    Vector3d above =
                        AboveNormal(rotation);
                    double offset =
                        PaperAnnotationScale.ModelDistance(
                            document.Database,
                            settings.OffsetPaper);
                    Point3d location =
                        centre +
                        (above * offset);

                    string handle =
                        alignmentId.Handle.ToString();
                    activeHandles.Add(handle);

                    MText label;
                    if (existing.TryGetValue(
                            handle,
                            out label) &&
                        label != null &&
                        !label.IsErased)
                    {
                        if (!label.IsWriteEnabled)
                            label.UpgradeOpen();
                        updated++;
                    }
                    else
                    {
                        label = new MText();
                        label.SetDatabaseDefaults(
                            document.Database);
                        modelSpace.AppendEntity(label);
                        transaction.AddNewlyCreatedDBObject(
                            label,
                            true);
                        created++;
                    }

                    label.LayerId =
                        layerId;
                    label.Location =
                        location;
                    label.Attachment =
                        AttachmentPoint.BottomCenter;
                    label.Rotation =
                        rotation;
                    label.Contents =
                        roadName;
                    label.Annotative =
                        AnnotativeStates.True;
                    label.TextHeight =
                        PaperAnnotationScale.ModelTextHeight(
                            document.Database,
                            settings.TextHeight);
                    label.Color =
                        Color.FromColorIndex(
                            ColorMethod.ByAci,
                            (short)settings.ColourIndex);
                    label.BackgroundFill =
                        settings.BackgroundMask;
                    label.UseBackgroundColor =
                        settings.BackgroundMask;
                    try
                    {
                        label.BackgroundScaleFactor =
                            1.15;
                    }
                    catch { }

                    WriteLabelLink(
                        label,
                        handle);
                    try
                    {
                        label.RecordGraphicsModified(
                            true);
                    }
                    catch { }
                }

                if (allRoads)
                {
                    foreach (KeyValuePair<string, MText> pair in
                        existing)
                    {
                        if (activeHandles.Contains(pair.Key))
                            continue;

                        MText stale =
                            pair.Value;
                        if (stale == null ||
                            stale.IsErased)
                            continue;
                        try
                        {
                            if (!stale.IsWriteEnabled)
                                stale.UpgradeOpen();
                            stale.Erase();
                            removedStale++;
                        }
                        catch { }
                    }
                }

                transaction.Commit();
            }

            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_ROADNAMEANNOTATE complete. Roads={0}; labels created={1}; updated={2}; stale removed={3}; skipped={4}.",
                alignmentIds.Count,
                created,
                updated,
                removedStale,
                skipped);
        }

        private static List<ObjectId> ReadAllRoadAlignmentIds(
            Database database,
            CivilDocument civilDocument)
        {
            var result =
                new List<ObjectId>();
            if (database == null ||
                civilDocument == null)
                return result;

            var corridorAlignmentIds =
                new HashSet<ObjectId>();

            using (Transaction transaction =
                database.TransactionManager.StartTransaction())
            {
                try
                {
                    foreach (ObjectId corridorId in
                        civilDocument.CorridorCollection)
                    {
                        Corridor corridor =
                            transaction.GetObject(
                                corridorId,
                                OpenMode.ForRead,
                                false) as Corridor;
                        if (corridor == null)
                            continue;

                        foreach (Baseline baseline in
                            corridor.Baselines)
                        {
                            if (baseline == null)
                                continue;
                            ObjectId alignmentId =
                                baseline.AlignmentId;
                            if (!alignmentId.IsNull)
                                corridorAlignmentIds.Add(
                                    alignmentId);
                        }
                    }
                }
                catch { }

                foreach (ObjectId alignmentId in
                    civilDocument.GetAlignmentIds())
                {
                    Alignment alignment = null;
                    try
                    {
                        alignment =
                            transaction.GetObject(
                                alignmentId,
                                OpenMode.ForRead,
                                false) as Alignment;
                    }
                    catch { }

                    if (alignment == null ||
                        alignment.IsReferenceObject)
                        continue;

                    if (corridorAlignmentIds.Contains(
                            alignmentId) ||
                        LooksLikeRoadAlignment(
                            alignment))
                    {
                        result.Add(
                            alignmentId);
                    }
                }
            }

            return result
                .Distinct()
                .ToList();
        }

        private static List<ObjectId> ReadSelectedRoadAlignmentIds(
            Document document,
            CivilDocument civilDocument)
        {
            var result =
                new List<ObjectId>();
            if (document == null ||
                civilDocument == null)
                return result;

            PromptSelectionResult selection =
                document.Editor.SelectImplied();
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
            {
                selection =
                    document.Editor.GetSelection(
                        new PromptSelectionOptions
                        {
                            MessageForAdding =
                                "\nSelect multiple road alignments or corridors to label: ",
                            AllowDuplicates = false,
                            RejectObjectsFromNonCurrentSpace = true
                        });
            }

            if (selection.Status != PromptStatus.OK ||
                selection.Value == null)
                return result;

            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in
                    selection.Value.GetObjectIds()
                        .Distinct())
                {
                    Alignment alignment = null;
                    try
                    {
                        alignment =
                            transaction.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as Alignment;
                    }
                    catch { }

                    if (alignment != null)
                    {
                        result.Add(
                            alignment.ObjectId);
                        continue;
                    }

                    Corridor corridor = null;
                    try
                    {
                        corridor =
                            transaction.GetObject(
                                id,
                                OpenMode.ForRead,
                                false) as Corridor;
                    }
                    catch { }

                    if (corridor == null)
                        continue;

                    foreach (Baseline baseline in
                        corridor.Baselines)
                    {
                        if (baseline == null ||
                            baseline.AlignmentId.IsNull)
                            continue;
                        result.Add(
                            baseline.AlignmentId);
                    }
                }
            }

            document.Editor.SetImpliedSelection(
                new ObjectId[0]);

            return result
                .Where(id =>
                    !id.IsNull)
                .Distinct()
                .ToList();
        }

        private static bool LooksLikeRoadAlignment(
            Alignment alignment)
        {
            if (alignment == null)
                return false;

            string name =
                alignment.Name ??
                string.Empty;
            string description =
                alignment.Description ??
                string.Empty;

            if (description.IndexOf(
                    "CE sewer alignment",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.StartsWith(
                    "BRANCH-",
                    StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(
                    "SEWER",
                    StringComparison.OrdinalIgnoreCase))
                return false;

            return
                name.StartsWith(
                    "RD",
                    StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(
                    "ROAD",
                    StringComparison.OrdinalIgnoreCase) ||
                description.IndexOf(
                    "ROAD",
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ResolveRoadName(
            Alignment alignment,
            string nameSource)
        {
            if (alignment == null)
                return string.Empty;

            string name =
                (alignment.Name ??
                 string.Empty)
                    .Trim();
            string description =
                (alignment.Description ??
                 string.Empty)
                    .Trim();

            if (string.Equals(
                    nameSource,
                    "Description (fallback to name)",
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(
                    description))
                return description;

            return name;
        }

        private static bool TryResolveCentredPlacement(
            Alignment alignment,
            out Point3d point,
            out double rotation)
        {
            point =
                Point3d.Origin;
            rotation =
                0.0;
            if (alignment == null)
                return false;

            double start =
                alignment.StartingStation;
            double end =
                alignment.EndingStation;
            double length =
                Math.Abs(
                    end - start);
            if (length <=
                GeometryTolerance)
                return false;

            double station =
                start +
                ((end - start) * 0.5);
            double delta =
                Math.Max(
                    0.05,
                    Math.Min(
                        length * 0.01,
                        1.0));
            double before =
                Math.Max(
                    Math.Min(start, end),
                    station - delta);
            double after =
                Math.Min(
                    Math.Max(start, end),
                    station + delta);

            double x = 0.0;
            double y = 0.0;
            double x1 = 0.0;
            double y1 = 0.0;
            double x2 = 0.0;
            double y2 = 0.0;

            try
            {
                alignment.PointLocation(
                    station,
                    0.0,
                    ref x,
                    ref y);
                alignment.PointLocation(
                    before,
                    0.0,
                    ref x1,
                    ref y1);
                alignment.PointLocation(
                    after,
                    0.0,
                    ref x2,
                    ref y2);
            }
            catch
            {
                return false;
            }

            double dx =
                x2 - x1;
            double dy =
                y2 - y1;
            if (Math.Abs(dx) <=
                    GeometryTolerance &&
                Math.Abs(dy) <=
                    GeometryTolerance)
                return false;

            rotation =
                NormalizeReadableRotation(
                    Math.Atan2(
                        dy,
                        dx));
            point =
                new Point3d(
                    x,
                    y,
                    0.0);
            return true;
        }

        private static Vector3d AboveNormal(
            double readableRotation)
        {
            Vector3d normal =
                new Vector3d(
                    -Math.Sin(readableRotation),
                    Math.Cos(readableRotation),
                    0.0);

            // "Above" means the WCS +Y side whenever that is visually
            // meaningful. For a near-vertical road there is no useful +Y
            // component, so keep the label on the left side of readable text.
            if (normal.Y <
                    -GeometryTolerance ||
                (Math.Abs(normal.Y) <=
                     GeometryTolerance &&
                 normal.X > 0.0))
                normal =
                    -normal;

            return normal.Length <=
                   GeometryTolerance
                ? Vector3d.YAxis
                : normal.GetNormal();
        }

        private static double NormalizeReadableRotation(
            double rotation)
        {
            while (rotation >
                   Math.PI)
                rotation -=
                    Math.PI * 2.0;
            while (rotation <=
                   -Math.PI)
                rotation +=
                    Math.PI * 2.0;

            if (rotation >
                Math.PI / 2.0)
                rotation -=
                    Math.PI;
            else if (rotation <
                     -Math.PI / 2.0)
                rotation +=
                    Math.PI;

            return rotation;
        }

        private static Dictionary<string, MText> ReadExistingLabels(
            BlockTableRecord modelSpace,
            Transaction transaction)
        {
            var result =
                new Dictionary<string, MText>(
                    StringComparer.OrdinalIgnoreCase);
            if (modelSpace == null ||
                transaction == null)
                return result;

            foreach (ObjectId id in
                modelSpace)
            {
                MText text = null;
                try
                {
                    text =
                        transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as MText;
                }
                catch { }

                if (text == null)
                    continue;

                string handle =
                    ReadLabelAlignmentHandle(
                        text);
                if (string.IsNullOrWhiteSpace(handle) ||
                    result.ContainsKey(handle))
                    continue;

                result[handle] =
                    text;
            }

            return result;
        }

        private static string ReadLabelAlignmentHandle(
            MText text)
        {
            if (text == null)
                return string.Empty;

            ResultBuffer data = null;
            try
            {
                data =
                    text.GetXDataForApplication(
                        AppName);
                if (data == null)
                    return string.Empty;

                TypedValue[] values =
                    data.AsArray();
                if (values.Length < 2)
                    return string.Empty;

                return Convert.ToString(
                           values[1].Value,
                           CultureInfo.InvariantCulture) ??
                       string.Empty;
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                if (data != null)
                    data.Dispose();
            }
        }

        private static void WriteLabelLink(
            MText text,
            string alignmentHandle)
        {
            if (text == null)
                return;

            text.XData =
                new ResultBuffer(
                    new TypedValue(
                        (int)DxfCode.ExtendedDataRegAppName,
                        AppName),
                    new TypedValue(
                        (int)DxfCode.ExtendedDataAsciiString,
                        alignmentHandle ??
                        string.Empty));
        }

        private static void EnsureRegApp(
            Database database,
            Transaction transaction)
        {
            RegAppTable table =
                transaction.GetObject(
                    database.RegAppTableId,
                    OpenMode.ForRead,
                    false) as RegAppTable;
            if (table == null ||
                table.Has(AppName))
                return;

            table.UpgradeOpen();
            var record =
                new RegAppTableRecord
                {
                    Name = AppName
                };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(
                record,
                true);
        }

        private static ObjectId GetOrCreateLayer(
            Database database,
            Transaction transaction,
            string requested)
        {
            string name =
                SafeLayerName(
                    requested,
                    DefaultLayer);
            LayerTable layers =
                transaction.GetObject(
                    database.LayerTableId,
                    OpenMode.ForRead,
                    false) as LayerTable;
            if (layers == null)
                return ObjectId.Null;
            if (layers.Has(name))
                return layers[name];

            layers.UpgradeOpen();
            var record =
                new LayerTableRecord
                {
                    Name = name,
                    Color =
                        Color.FromColorIndex(
                            ColorMethod.ByAci,
                            2)
                };
            ObjectId id =
                layers.Add(record);
            transaction.AddNewlyCreatedDBObject(
                record,
                true);
            return id;
        }

        private static string SafeLayerName(
            string value,
            string fallback)
        {
            string result =
                string.IsNullOrWhiteSpace(value)
                    ? fallback
                    : value.Trim();
            foreach (char invalid in
                new[]
                {
                    '<', '>', '/', '\\', '"',
                    ':', ';', '?', '*', '|',
                    '=', ','
                })
                result =
                    result.Replace(
                        invalid,
                        '-');

            return string.IsNullOrWhiteSpace(result)
                ? fallback
                : result;
        }

        private static string NormalizeScope(
            string value)
        {
            return string.Equals(
                       value,
                       "Selected road alignments",
                       StringComparison.OrdinalIgnoreCase)
                ? "Selected road alignments"
                : "All road alignments";
        }

        private static string NormalizeNameSource(
            string value)
        {
            return string.Equals(
                       value,
                       "Description (fallback to name)",
                       StringComparison.OrdinalIgnoreCase)
                ? "Description (fallback to name)"
                : "Alignment name";
        }

        private sealed class RoadNameSettings
        {
            internal string Scope { get; set; } =
                "All road alignments";
            internal string NameSource { get; set; } =
                "Alignment name";
            internal string Layer { get; set; } =
                DefaultLayer;
            internal double TextHeight { get; set; } =
                5.0;
            internal double OffsetPaper { get; set; } =
                8.0;
            internal int ColourIndex { get; set; } =
                2;
            internal bool BackgroundMask { get; set; } =
                true;

            internal static RoadNameSettings Read(
                Database database,
                string key)
            {
                var result =
                    new RoadNameSettings();
                if (database == null ||
                    string.IsNullOrWhiteSpace(key))
                    return result;

                try
                {
                    using (Transaction transaction =
                        database.TransactionManager.StartTransaction())
                    {
                        DBDictionary named =
                            transaction.GetObject(
                                database.NamedObjectsDictionaryId,
                                OpenMode.ForRead,
                                false) as DBDictionary;
                        if (named == null ||
                            !named.Contains(key))
                            return result;

                        Xrecord record =
                            transaction.GetObject(
                                named.GetAt(key),
                                OpenMode.ForRead,
                                false) as Xrecord;
                        if (record == null ||
                            record.Data == null)
                            return result;

                        foreach (TypedValue value in
                            record.Data)
                        {
                            if (value.TypeCode !=
                                (int)DxfCode.Text)
                                continue;

                            string item =
                                value.Value as string;
                            int separator =
                                string.IsNullOrWhiteSpace(item)
                                    ? -1
                                    : item.IndexOf('=');
                            if (separator <= 0)
                                continue;

                            string name =
                                item.Substring(
                                    0,
                                    separator);
                            string text =
                                item.Substring(
                                    separator + 1);
                            ApplySetting(
                                result,
                                name,
                                text);
                        }
                    }
                }
                catch { }

                return result;
            }

            internal void Write(
                Database database,
                string key)
            {
                if (database == null ||
                    string.IsNullOrWhiteSpace(key))
                    return;

                try
                {
                    using (Transaction transaction =
                        database.TransactionManager.StartTransaction())
                    {
                        DBDictionary named =
                            transaction.GetObject(
                                database.NamedObjectsDictionaryId,
                                OpenMode.ForWrite,
                                false) as DBDictionary;
                        if (named == null)
                            return;

                        Xrecord record;
                        if (named.Contains(key))
                        {
                            record =
                                transaction.GetObject(
                                    named.GetAt(key),
                                    OpenMode.ForWrite,
                                    false) as Xrecord;
                        }
                        else
                        {
                            record =
                                new Xrecord();
                            named.SetAt(
                                key,
                                record);
                            transaction.AddNewlyCreatedDBObject(
                                record,
                                true);
                        }

                        if (record != null)
                        {
                            record.Data =
                                new ResultBuffer(
                                    Setting(
                                        "Scope",
                                        Scope),
                                    Setting(
                                        "NameSource",
                                        NameSource),
                                    Setting(
                                        "Layer",
                                        Layer),
                                    Setting(
                                        "TextHeight",
                                        TextHeight.ToString(
                                            "R",
                                            CultureInfo.InvariantCulture)),
                                    Setting(
                                        "OffsetPaper",
                                        OffsetPaper.ToString(
                                            "R",
                                            CultureInfo.InvariantCulture)),
                                    Setting(
                                        "ColourIndex",
                                        ColourIndex.ToString(
                                            CultureInfo.InvariantCulture)),
                                    Setting(
                                        "BackgroundMask",
                                        BackgroundMask
                                            ? "Yes"
                                            : "No"));
                        }

                        transaction.Commit();
                    }
                }
                catch { }
            }

            private static TypedValue Setting(
                string name,
                string value)
            {
                return new TypedValue(
                    (int)DxfCode.Text,
                    name +
                    "=" +
                    (value ?? string.Empty));
            }

            private static void ApplySetting(
                RoadNameSettings settings,
                string name,
                string value)
            {
                if (settings == null)
                    return;

                if (string.Equals(
                        name,
                        "Scope",
                        StringComparison.OrdinalIgnoreCase))
                    settings.Scope =
                        NormalizeScope(value);
                else if (string.Equals(
                             name,
                             "NameSource",
                             StringComparison.OrdinalIgnoreCase))
                    settings.NameSource =
                        NormalizeNameSource(value);
                else if (string.Equals(
                             name,
                             "Layer",
                             StringComparison.OrdinalIgnoreCase))
                    settings.Layer =
                        SafeLayerName(
                            value,
                            DefaultLayer);
                else if (string.Equals(
                             name,
                             "TextHeight",
                             StringComparison.OrdinalIgnoreCase))
                {
                    double number;
                    if (double.TryParse(
                            value,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out number) &&
                        number > 0.0)
                        settings.TextHeight =
                            number;
                }
                else if (string.Equals(
                             name,
                             "OffsetPaper",
                             StringComparison.OrdinalIgnoreCase))
                {
                    double number;
                    if (double.TryParse(
                            value,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out number) &&
                        number > 0.0)
                        settings.OffsetPaper =
                            number;
                }
                else if (string.Equals(
                             name,
                             "ColourIndex",
                             StringComparison.OrdinalIgnoreCase))
                {
                    int number;
                    if (int.TryParse(
                            value,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out number))
                        settings.ColourIndex =
                            Math.Max(
                                1,
                                Math.Min(
                                    255,
                                    number));
                }
                else if (string.Equals(
                             name,
                             "BackgroundMask",
                             StringComparison.OrdinalIgnoreCase))
                    settings.BackgroundMask =
                        string.Equals(
                            value,
                            "Yes",
                            StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
