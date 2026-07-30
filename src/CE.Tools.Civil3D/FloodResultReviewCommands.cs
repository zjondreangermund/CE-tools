using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CETools.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.FloodResultReviewCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Reviews point-based specialist flood results imported by CE_MODELRESULTIMPORT.
    /// Outputs remain sample-based screening and are not continuous flood surfaces,
    /// legal flood lines, property damage assessments or certified depth/velocity hazard.
    /// </summary>
    public sealed class FloodResultReviewCommands
    {
        private const string ImportRegApp = "CE_MODEL_RESULT_IMPORT";
        private const int MaximumResultPoints = 250000;
        private const int MaximumProperties = 5000;
        private const int MaximumHtmlPoints = 200000;

        [CommandMethod("CE_TOOLS", "CE_FLOODRESULTTOOLS", CommandFlags.Modal)]
        public void FloodResultTools()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            var options = new PromptKeywordOptions(
                "\nImported flood-result tools [Properties/Frame/Reset/Animation] <Properties>: ")
            {
                AllowNone = true
            };
            foreach (string keyword in new[] { "Properties", "Frame", "Reset", "Animation" })
                options.Keywords.Add(keyword);
            PromptResult result = document.Editor.GetKeywords(options);
            if (result.Status == PromptStatus.Cancel) return;
            string choice = result.Status == PromptStatus.OK ? result.StringResult : "Properties";
            string command = Equal(choice, "Frame") ? "CE_FLOODFRAMESET " :
                Equal(choice, "Reset") ? "CE_FLOODFRAMERESET " :
                Equal(choice, "Animation") ? "CE_FLOODANIMATIONHTML " :
                "CE_FLOODPROPERTYREPORT ";
            document.SendStringToExecute(command, true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_FLOODPROPERTYREPORT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void AffectedPropertyReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<FloodProperty> properties;
            if (!PromptProperties(document, true, out properties)) return;
            double minimumDepth;
            if (!PromptNonNegativeDouble(
                    document.Editor,
                    "Minimum depth counted as affected (m)",
                    0.05,
                    out minimumDepth))
                return;

            try
            {
                List<FloodResultEntity> entities = ReadImportedResults(document.Database);
                FloodAnalysisResult analysis = FloodResultAnalyzer.Analyse(
                    properties,
                    entities.Select(item => item.Point),
                    minimumDepth);
                List<IList<string>> summary = BuildPropertySummaryRows(analysis);
                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Affected Property Flood-Point Review",
                    string.Format(
                        CultureInfo.CurrentCulture,
                        "Properties={0}; imported points={1}; scenario/time frames={2}; affected threshold={3:N3} m. Point samples are not continuous flood surfaces.",
                        analysis.Properties.Count,
                        entities.Count,
                        analysis.Frames.Count,
                        minimumDepth),
                    new List<string>
                    {
                        "PROPERTY", "AFFECTED FRAMES", "MAX DEPTH (m)",
                        "MAX VELOCITY (m/s)", "MAX WATER LEVEL (m)",
                        "MAX HAZARD INDEX", "FIRST AFFECTED", "PEAK FRAME"
                    },
                    summary,
                    "CE TOOLS AFFECTED PROPERTY FLOOD REVIEW");

                if (PromptYesNo(document.Editor, "Export property and frame details to Excel", true))
                {
                    string path;
                    if (PromptSavePath(
                            document.Editor,
                            "Excel Workbook (*.xlsx)|*.xlsx",
                            "CE-Tools-Affected-Property-Flood-Review.xlsx",
                            ".xlsx",
                            out path))
                    {
                        SimpleXlsxWriter.Write(path, "Property Flood Review", BuildDetailedRows(analysis));
                        document.Editor.WriteMessage(
                            "\nCE_FLOODPROPERTYREPORT workbook created: {0}",
                            path);
                    }
                }
                document.Editor.WriteMessage(
                    "\nCE_FLOODPROPERTYREPORT complete. Properties={0}; affected properties={1}; frames={2}.",
                    analysis.PropertySummaries.Count,
                    analysis.PropertySummaries.Count(item => item.AffectedFrameCount > 0),
                    analysis.Frames.Count);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_FLOODPROPERTYREPORT failed. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_FLOODFRAMESET", CommandFlags.Modal | CommandFlags.Redraw)]
        public void SetFloodFrame()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            try
            {
                List<FloodResultEntity> entities = ReadImportedResults(document.Database);
                List<FloodFrameKey> frames = entities
                    .Select(item => new FloodFrameKey(item.Point.Scenario, item.Point.Time))
                    .Distinct()
                    .OrderBy(item => item.Scenario, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(item => item.SortTime)
                    .ThenBy(item => item.Time, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                if (frames.Count == 0)
                {
                    document.Editor.WriteMessage("\nCE_FLOODFRAMESET: no imported scenario/time frames were found.");
                    return;
                }
                var rows = frames.Select((frame, index) => (IList<string>)new List<string>
                {
                    (index + 1).ToString(CultureInfo.InvariantCulture),
                    frame.Scenario,
                    frame.Time,
                    entities.Count(item => Equal(item.Point.Scenario, frame.Scenario) && Equal(item.Point.Time, frame.Time))
                        .ToString(CultureInfo.InvariantCulture)
                }).ToList();
                GridReportPresenter.ShowReportAndOfferTable(
                    document,
                    "CE Tools - Imported Flood Result Frames",
                    "Choose a frame number at the command line. Only CE Tools imported result markers are hidden/shown.",
                    new List<string> { "NO.", "SCENARIO", "TIME", "POINTS" },
                    rows,
                    "CE TOOLS FLOOD RESULT FRAMES");

                int selected;
                if (!PromptIndex(document.Editor, frames.Count, out selected)) return;
                FloodFrameKey key = frames[selected];
                int visible = SetFrameVisibility(document.Database, key);
                document.Editor.Regen();
                document.Editor.WriteMessage(
                    "\nCE_FLOODFRAMESET complete. Scenario={0}; time={1}; visible markers={2}; other imported frames hidden.",
                    key.Scenario,
                    key.Time,
                    visible);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_FLOODFRAMESET failed. {0}", exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_FLOODFRAMERESET", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ResetFloodFrames()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            int visible = SetFrameVisibility(document.Database, null);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_FLOODFRAMERESET complete. All imported specialist-result markers restored visible={0}.",
                visible);
        }

        [CommandMethod("CE_TOOLS", "CE_FLOODANIMATIONHTML", CommandFlags.Modal)]
        public void ExportFloodAnimation()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            List<FloodProperty> properties;
            if (!PromptProperties(document, false, out properties)) return;
            double minimumDepth;
            if (!PromptNonNegativeDouble(
                    document.Editor,
                    "Minimum displayed depth (m)",
                    0.01,
                    out minimumDepth))
                return;
            string path;
            if (!PromptSavePath(
                    document.Editor,
                    "HTML Document (*.html)|*.html",
                    "CE-Tools-Flood-Result-Animation.html",
                    ".html",
                    out path))
                return;
            if (File.Exists(path))
            {
                document.Editor.WriteMessage(
                    "\nCE_FLOODANIMATIONHTML stopped. Existing HTML files are not overwritten.");
                return;
            }

            try
            {
                List<FloodResultEntity> entities = ReadImportedResults(document.Database);
                if (entities.Count > MaximumHtmlPoints)
                    throw new InvalidOperationException(
                        "The imported result set exceeds the 200,000-point HTML animation limit. Filter the source results before import.");
                FloodAnalysisResult analysis = FloodResultAnalyzer.Analyse(
                    properties,
                    entities.Select(item => item.Point),
                    minimumDepth);
                string html = BuildAnimationHtml(analysis);
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
                File.WriteAllText(path, html, new UTF8Encoding(false));
                document.Editor.WriteMessage(
                    "\nCE_FLOODANIMATIONHTML complete. Frames={0}; points={1}; properties={2}; file={3}.",
                    analysis.Frames.Count,
                    entities.Count,
                    properties.Count,
                    path);
                document.Editor.WriteMessage(
                    "\nThe HTML is an interactive point-sample animation, not a solved 2D hydraulic model or certified flood hazard map.");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_FLOODANIMATIONHTML failed. {0}", exception.Message);
            }
        }

        private static List<FloodResultEntity> ReadImportedResults(Database database)
        {
            var results = new List<FloodResultEntity>();
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (space == null) return results;
                foreach (ObjectId id in space)
                {
                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || entity.IsErased) continue;
                    ResultBuffer data = entity.GetXDataForApplication(ImportRegApp);
                    if (data == null) continue;
                    Dictionary<string, string> values = ParseXData(data);
                    double? x = Number(values, "X");
                    double? y = Number(values, "Y");
                    if (!x.HasValue || !y.HasValue)
                    {
                        Point3d centre;
                        if (!TryCentre(entity, out centre)) continue;
                        x = centre.X; y = centre.Y;
                    }
                    results.Add(new FloodResultEntity(
                        id,
                        new FloodResultPoint(
                            x.Value,
                            y.Value,
                            Number(values, "Z"),
                            Number(values, "Depth"),
                            Number(values, "Velocity"),
                            Number(values, "WaterLevel"),
                            Number(values, "HazardIndex"),
                            Text(values, "Scenario"),
                            Text(values, "Time"),
                            entity.Handle.ToString())));
                    if (results.Count > MaximumResultPoints)
                        throw new InvalidOperationException(
                            "Imported flood result markers exceed the 250,000-point safety limit.");
                }
            }
            if (results.Count == 0)
                throw new InvalidOperationException(
                    "No CE_MODEL_RESULT_IMPORT markers were found in the current drawing space.");
            return results;
        }

        private static bool PromptProperties(
            Document document,
            bool required,
            out List<FloodProperty> properties)
        {
            properties = new List<FloodProperty>();
            var options = new PromptSelectionOptions
            {
                MessageForAdding = required
                    ? "\nSelect closed property/erf boundary polylines: "
                    : "\nSelect optional property/erf boundary polylines or press Enter for none: ",
                AllowDuplicates = false,
                RejectObjectsFromNonCurrentSpace = true
            };
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
            });
            PromptSelectionResult result = document.Editor.GetSelection(options, filter);
            if (result.Status == PromptStatus.Cancel) return false;
            if (result.Status != PromptStatus.OK)
                return !required;
            ObjectId[] ids = result.Value.GetObjectIds().Distinct().ToArray();
            if (ids.Length > MaximumProperties)
            {
                document.Editor.WriteMessage(
                    "\nFlood property review stopped. Property count exceeds the {0}-boundary safety limit.",
                    MaximumProperties);
                return false;
            }
            using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    Polyline polyline = transaction.GetObject(id, OpenMode.ForRead, false) as Polyline;
                    if (polyline == null || !polyline.Closed || polyline.NumberOfVertices < 3) continue;
                    var polygon = new ParkingPolygon(Enumerable.Range(0, polyline.NumberOfVertices)
                        .Select(index => polyline.GetPoint2dAt(index))
                        .Select(point => new ParkingPoint(point.X, point.Y)));
                    try { polygon.Validate("property " + polyline.Handle); }
                    catch { continue; }
                    properties.Add(new FloodProperty(
                        string.IsNullOrWhiteSpace(polyline.Layer)
                            ? polyline.Handle.ToString()
                            : polyline.Layer + "-" + polyline.Handle,
                        polygon));
                }
            }
            if (required && properties.Count == 0)
            {
                document.Editor.WriteMessage(
                    "\nFlood property review stopped. No valid closed property polylines were selected.");
                return false;
            }
            return true;
        }

        private static int SetFrameVisibility(Database database, FloodFrameKey frame)
        {
            int visible = 0;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTableRecord space = transaction.GetObject(
                    database.CurrentSpaceId,
                    OpenMode.ForRead,
                    false) as BlockTableRecord;
                if (space != null)
                {
                    foreach (ObjectId id in space)
                    {
                        Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                        if (entity == null || entity.GetXDataForApplication(ImportRegApp) == null) continue;
                        Dictionary<string, string> values = ParseXData(entity.GetXDataForApplication(ImportRegApp));
                        bool show = frame == null ||
                            (Equal(Text(values, "Scenario"), frame.Scenario) &&
                             Equal(Text(values, "Time"), frame.Time));
                        entity.UpgradeOpen();
                        SetEntityVisibility(entity, show);
                        if (show) visible++;
                    }
                }
                transaction.Commit();
            }
            return visible;
        }

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

        private static List<IList<string>> BuildPropertySummaryRows(FloodAnalysisResult analysis)
        {
            return analysis.PropertySummaries
                .OrderByDescending(item => item.MaximumHazardIndex ?? -1.0)
                .ThenByDescending(item => item.MaximumDepthMetres ?? -1.0)
                .ThenBy(item => item.PropertyId, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => (IList<string>)new List<string>
                {
                    item.PropertyId,
                    item.AffectedFrameCount.ToString(CultureInfo.InvariantCulture),
                    Format(item.MaximumDepthMetres),
                    Format(item.MaximumVelocityMetresPerSecond),
                    Format(item.MaximumWaterLevelMetres),
                    Format(item.MaximumHazardIndex),
                    item.FirstAffectedFrame == null ? string.Empty : item.FirstAffectedFrame.ToString(),
                    item.PeakFrame == null ? string.Empty : item.PeakFrame.ToString()
                }).ToList();
        }

        private static List<IList<string>> BuildDetailedRows(FloodAnalysisResult analysis)
        {
            var rows = new List<IList<string>>
            {
                new List<string>
                {
                    "PROPERTY", "SCENARIO", "TIME", "POINTS", "WET POINTS",
                    "MAX DEPTH (m)", "AVG DEPTH (m)", "MAX VELOCITY (m/s)",
                    "MAX WATER LEVEL (m)", "MAX HAZARD INDEX", "AFFECTED"
                }
            };
            foreach (FloodPropertyFrameSummary item in analysis.PropertyFrames
                .OrderBy(row => row.PropertyId, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Frame.Scenario, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Frame.SortTime)
                .ThenBy(row => row.Frame.Time, StringComparer.CurrentCultureIgnoreCase))
            {
                rows.Add(new List<string>
                {
                    item.PropertyId, item.Frame.Scenario, item.Frame.Time,
                    item.PointCount.ToString(CultureInfo.InvariantCulture),
                    item.WetPointCount.ToString(CultureInfo.InvariantCulture),
                    Format(item.MaximumDepthMetres), Format(item.AverageDepthMetres),
                    Format(item.MaximumVelocityMetresPerSecond),
                    Format(item.MaximumWaterLevelMetres), Format(item.MaximumHazardIndex),
                    item.Affected ? "Yes" : "No"
                });
            }
            rows.Add(new List<string>
            {
                "BOUNDARY", "Point-sample review only", string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty,
                "Do not use as a legal flood line, property damage assessment or certified hazard result."
            });
            return rows;
        }

        private static string BuildAnimationHtml(FloodAnalysisResult analysis)
        {
            var html = new StringBuilder();
            html.AppendLine("<!doctype html><html><head><meta charset='utf-8'>");
            html.AppendLine("<meta name='viewport' content='width=device-width,initial-scale=1'>");
            html.AppendLine("<title>CE Tools Flood Result Animation</title>");
            html.AppendLine("<style>body{margin:0;font-family:Arial;background:#111;color:#eee}header{padding:12px;background:#1d1d1d}#controls{display:flex;gap:10px;align-items:center;flex-wrap:wrap}canvas{display:block;width:100vw;height:calc(100vh - 110px);background:#f4f4f4}button,select,input{padding:6px}small{color:#bbb}</style></head><body>");
            html.AppendLine("<header><div id='controls'><button id='play'>Play</button><select id='scenario'></select><input id='frame' type='range' min='0' value='0'><strong id='label'></strong></div><small>CE Tools point-sample animation. Not a solved 2D hydraulic model, legal flood line or certified hazard map.</small></header><canvas id='map'></canvas><script>");
            html.Append("const bounds=").Append(JsonBounds(analysis.Bounds)).AppendLine(";");
            html.Append("const properties=").Append(JsonProperties(analysis.Properties)).AppendLine(";");
            html.Append("const frames=").Append(JsonFrames(analysis.Frames, analysis.MinimumDepthMetres)).AppendLine(";");
            html.AppendLine(@"
const canvas=document.getElementById('map'),ctx=canvas.getContext('2d'),slider=document.getElementById('frame'),scenario=document.getElementById('scenario'),label=document.getElementById('label'),play=document.getElementById('play');
let playing=false,timer=null,current=[];
function resize(){canvas.width=window.innerWidth*devicePixelRatio;canvas.height=(window.innerHeight-110)*devicePixelRatio;draw();}
function transform(x,y){const pad=35*devicePixelRatio,w=canvas.width-2*pad,h=canvas.height-2*pad,s=Math.min(w/Math.max(1e-9,bounds.maxX-bounds.minX),h/Math.max(1e-9,bounds.maxY-bounds.minY));return [pad+(x-bounds.minX)*s,canvas.height-pad-(y-bounds.minY)*s];}
function colour(depth,velocity,hazard){if(hazard!=null&&hazard>=1.5)return '#d7191c';if(hazard!=null&&hazard>=0.75)return '#fdae61';if(depth>=1)return '#542788';if(depth>=.5)return '#2c7bb6';if(depth>=.15)return '#00a6ca';return '#7fcdbb';}
function populate(){const names=[...new Set(frames.map(f=>f.scenario))];scenario.innerHTML=names.map(n=>`<option>${n}</option>`).join('');refresh();}
function refresh(){current=frames.filter(f=>f.scenario===scenario.value);slider.max=Math.max(0,current.length-1);slider.value=Math.min(+slider.value,+slider.max);draw();}
function draw(){ctx.clearRect(0,0,canvas.width,canvas.height);ctx.lineWidth=1.5*devicePixelRatio;ctx.strokeStyle='#555';ctx.fillStyle='rgba(180,180,180,.08)';for(const p of properties){ctx.beginPath();p.points.forEach((q,i)=>{const t=transform(q[0],q[1]);i?ctx.lineTo(t[0],t[1]):ctx.moveTo(t[0],t[1]);});ctx.closePath();ctx.fill();ctx.stroke();const c=transform(p.cx,p.cy);ctx.fillStyle='#222';ctx.fillText(p.id,c[0],c[1]);ctx.fillStyle='rgba(180,180,180,.08)';}if(!current.length){label.textContent='No frames';return;}const f=current[+slider.value];label.textContent=f.scenario+' | '+f.time+' | points '+f.points.length;for(const p of f.points){const t=transform(p.x,p.y);ctx.beginPath();ctx.fillStyle=colour(p.d,p.v,p.h);ctx.arc(t[0],t[1],Math.max(2,4*devicePixelRatio),0,Math.PI*2);ctx.fill();}}
slider.oninput=draw;scenario.onchange=refresh;play.onclick=()=>{playing=!playing;play.textContent=playing?'Pause':'Play';clearInterval(timer);if(playing)timer=setInterval(()=>{slider.value=(+slider.value+1)%Math.max(1,current.length);draw();},500);};window.addEventListener('resize',resize);populate();resize();
");
            html.AppendLine("</script></body></html>");
            return html.ToString();
        }

        private static string JsonBounds(FloodBounds bounds)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{{\"minX\":{0:R},\"minY\":{1:R},\"maxX\":{2:R},\"maxY\":{3:R}}}",
                bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
        }

        private static string JsonProperties(IEnumerable<FloodProperty> properties)
        {
            return "[" + string.Join(",", properties.Select(property =>
                "{\"id\":\"" + Json(property.Id) + "\",\"cx\":" + property.Polygon.Centroid.X.ToString("R", CultureInfo.InvariantCulture) +
                ",\"cy\":" + property.Polygon.Centroid.Y.ToString("R", CultureInfo.InvariantCulture) +
                ",\"points\":[" + string.Join(",", property.Polygon.Vertices.Select(point =>
                    "[" + point.X.ToString("R", CultureInfo.InvariantCulture) + "," + point.Y.ToString("R", CultureInfo.InvariantCulture) + "]")) + "]}")) + "]";
        }

        private static string JsonFrames(IEnumerable<FloodFrame> frames, double minimumDepth)
        {
            return "[" + string.Join(",", frames.Select(frame =>
                "{\"scenario\":\"" + Json(frame.Key.Scenario) + "\",\"time\":\"" + Json(frame.Key.Time) + "\",\"points\":[" +
                string.Join(",", frame.Points
                    .Where(point => point.DepthMetres.HasValue && point.DepthMetres.Value >= minimumDepth)
                    .Select(point => string.Format(
                        CultureInfo.InvariantCulture,
                        "{{\"x\":{0:R},\"y\":{1:R},\"d\":{2},\"v\":{3},\"h\":{4}}}",
                        point.X, point.Y, JsonNumber(point.DepthMetres), JsonNumber(point.VelocityMetresPerSecond), JsonNumber(point.HazardIndex)))) + "]}")) + "]";
        }

        private static string JsonNumber(double? value)
        {
            return value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : "null";
        }

        private static string Json(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static Dictionary<string, string> ParseXData(ResultBuffer buffer)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (TypedValue value in buffer)
            {
                string text = value.Value as string;
                if (string.IsNullOrWhiteSpace(text)) continue;
                int equals = text.IndexOf('=');
                if (equals <= 0) continue;
                values[text.Substring(0, equals)] = text.Substring(equals + 1);
            }
            return values;
        }

        private static bool TryCentre(Entity entity, out Point3d point)
        {
            Circle circle = entity as Circle;
            if (circle != null) { point = circle.Center; return true; }
            DBPoint dbPoint = entity as DBPoint;
            if (dbPoint != null) { point = dbPoint.Position; return true; }
            try
            {
                Extents3d extents = entity.GeometricExtents;
                point = new Point3d(
                    (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
                    (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5,
                    (extents.MinPoint.Z + extents.MaxPoint.Z) * 0.5);
                return true;
            }
            catch { point = Point3d.Origin; return false; }
        }

        private static double? Number(IDictionary<string, string> values, string key)
        {
            double value;
            return double.TryParse(Text(values, key), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !double.IsNaN(value) && !double.IsInfinity(value) ? (double?)value : null;
        }

        private static string Text(IDictionary<string, string> values, string key)
        {
            string value; return values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static bool PromptIndex(Editor editor, int count, out int index)
        {
            var options = new PromptIntegerOptions("\nSelect frame number <1>: ")
            {
                AllowNone = true, AllowNegative = false, AllowZero = false,
                LowerLimit = 1, UpperLimit = count, DefaultValue = 1, UseDefaultValue = true
            };
            PromptIntegerResult result = editor.GetInteger(options);
            index = (result.Status == PromptStatus.OK ? result.Value : 1) - 1;
            return result.Status != PromptStatus.Cancel && index >= 0 && index < count;
        }

        private static bool PromptNonNegativeDouble(Editor editor, string label, double defaultValue, out double value)
        {
            var options = new PromptDoubleOptions("\n" + label + " <" + defaultValue.ToString(CultureInfo.CurrentCulture) + ">: ")
            { AllowNone = true, AllowNegative = false, AllowZero = true, DefaultValue = defaultValue };
            PromptDoubleResult result = editor.GetDouble(options);
            value = result.Status == PromptStatus.OK ? result.Value : defaultValue;
            return result.Status != PromptStatus.Cancel && value >= 0.0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool PromptYesNo(Editor editor, string label, bool defaultValue)
        {
            var options = new PromptKeywordOptions("\n" + label + " [Yes/No] <" + (defaultValue ? "Yes" : "No") + ">: ") { AllowNone = true };
            options.Keywords.Add("Yes"); options.Keywords.Add("No");
            PromptResult result = editor.GetKeywords(options);
            return result.Status != PromptStatus.Cancel &&
                (result.Status == PromptStatus.None ? defaultValue : Equal(result.StringResult, "Yes"));
        }

        private static bool PromptSavePath(Editor editor, string filter, string initialName, string extension, out string path)
        {
            var options = new PromptSaveFileOptions("\nChoose the output file path: ")
            { Filter = filter, DialogCaption = "CE Tools Flood Result Output", InitialFileName = initialName };
            PromptFileNameResult result = editor.GetFileNameForSave(options);
            path = result.Status == PromptStatus.OK
                ? (result.StringResult.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? result.StringResult : result.StringResult + extension)
                : string.Empty;
            return result.Status == PromptStatus.OK;
        }

        private static string Format(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.###", CultureInfo.CurrentCulture) : string.Empty;
        }

        private static bool Equal(string first, string second)
        {
            return string.Equals(
                string.IsNullOrWhiteSpace(first) ? "<Unspecified>" : first.Trim(),
                string.IsNullOrWhiteSpace(second) ? "<Unspecified>" : second.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }

    internal sealed class FloodResultEntity
    {
        public FloodResultEntity(ObjectId objectId, FloodResultPoint point)
        { ObjectId = objectId; Point = point; }
        public ObjectId ObjectId { get; private set; }
        public FloodResultPoint Point { get; private set; }
    }
}
