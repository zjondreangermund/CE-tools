using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August18SurveyGoogleEarthBoundaryCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Survey-production Google Earth handoff. Closed AutoCAD polyline boundaries
    /// are converted from the current CE/Namibia drawing coordinate system to
    /// WGS84 and written as a temporary KML file for Google Earth.
    /// </summary>
    public sealed class August18SurveyGoogleEarthBoundaryCommands
    {
        [CommandMethod(
            "CE_TOOLS",
            "CE_SURVEYGOOGLEEARTHBOUNDARY",
            CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void PlotPolylineBoundaryInGoogleEarth()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            Editor editor = document.Editor;
            PromptSelectionResult selection = editor.SelectImplied();
            if (selection.Status != PromptStatus.OK ||
                selection.Value == null ||
                selection.Value.Count == 0)
            {
                selection = editor.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding =
                            "\nSelect one or more CLOSED polyline boundaries to plot in Google Earth: ",
                        AllowDuplicates = false,
                        RejectObjectsFromNonCurrentSpace = true
                    });
            }
            if (selection.Status != PromptStatus.OK || selection.Value == null)
                return;

            var boundaries = new List<GoogleEarthBoundary>();
            int rejected = 0;
            string conversionError = string.Empty;

            using (Transaction transaction =
                document.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selection.Value.GetObjectIds().Distinct())
                {
                    Entity entity;
                    try
                    {
                        entity = transaction.GetObject(
                            id,
                            OpenMode.ForRead,
                            false) as Entity;
                    }
                    catch
                    {
                        rejected++;
                        continue;
                    }

                    List<Point3d> drawingPoints;
                    if (!TryReadClosedPolyline(entity, transaction, out drawingPoints))
                    {
                        rejected++;
                        continue;
                    }

                    var geographicPoints = new List<Point3d>();
                    bool converted = true;
                    foreach (Point3d drawingPoint in drawingPoints)
                    {
                        Point3d geographic;
                        string error;
                        if (!NamibiaCoordinateRuntime.TryDrawingToWgs84(
                                document.Database,
                                drawingPoint,
                                out geographic,
                                out error))
                        {
                            conversionError = error;
                            converted = false;
                            break;
                        }
                        geographicPoints.Add(geographic);
                    }
                    if (!converted || geographicPoints.Count < 3)
                    {
                        rejected++;
                        continue;
                    }

                    if (geographicPoints[0].DistanceTo(
                            geographicPoints[geographicPoints.Count - 1]) > 1e-10)
                    {
                        geographicPoints.Add(geographicPoints[0]);
                    }

                    boundaries.Add(new GoogleEarthBoundary(
                        BuildBoundaryName(entity, boundaries.Count + 1),
                        geographicPoints));
                }
            }

            if (boundaries.Count == 0)
            {
                editor.WriteMessage(
                    "\nCE_SURVEYGOOGLEEARTHBOUNDARY stopped. Select closed AutoCAD polylines. {0}",
                    string.IsNullOrWhiteSpace(conversionError)
                        ? "No usable boundary was found."
                        : conversionError);
                return;
            }

            string path = Path.Combine(
                Path.GetTempPath(),
                "CE-Tools-Survey-Boundaries-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                ".kml");
            File.WriteAllText(path, BuildKml(boundaries), new UTF8Encoding(false));

            bool opened = OpenInGoogleEarth(path);
            editor.WriteMessage(
                "\nCE_SURVEYGOOGLEEARTHBOUNDARY complete. Boundaries={0}; rejected={1}; Google Earth launched={2}; KML={3}",
                boundaries.Count,
                rejected,
                opened ? "Yes" : "No - open the KML manually",
                path);
        }

        private static bool TryReadClosedPolyline(
            Entity entity,
            Transaction transaction,
            out List<Point3d> points)
        {
            points = new List<Point3d>();
            Polyline lwPolyline = entity as Polyline;
            if (lwPolyline != null)
            {
                if (!lwPolyline.Closed || lwPolyline.NumberOfVertices < 3)
                    return false;
                for (int index = 0; index < lwPolyline.NumberOfVertices; index++)
                    points.Add(lwPolyline.GetPoint3dAt(index));
                return points.Count >= 3;
            }

            Polyline2d polyline2d = entity as Polyline2d;
            if (polyline2d != null)
            {
                if (!polyline2d.Closed) return false;
                foreach (ObjectId vertexId in polyline2d)
                {
                    Vertex2d vertex;
                    try
                    {
                        vertex = transaction.GetObject(
                            vertexId,
                            OpenMode.ForRead,
                            false) as Vertex2d;
                    }
                    catch
                    {
                        vertex = null;
                    }
                    if (vertex != null) points.Add(vertex.Position);
                }
                return points.Count >= 3;
            }

            Polyline3d polyline3d = entity as Polyline3d;
            if (polyline3d != null)
            {
                if (!polyline3d.Closed) return false;
                foreach (ObjectId vertexId in polyline3d)
                {
                    PolylineVertex3d vertex;
                    try
                    {
                        vertex = transaction.GetObject(
                            vertexId,
                            OpenMode.ForRead,
                            false) as PolylineVertex3d;
                    }
                    catch
                    {
                        vertex = null;
                    }
                    if (vertex != null) points.Add(vertex.Position);
                }
                return points.Count >= 3;
            }

            return false;
        }

        private static string BuildBoundaryName(Entity entity, int number)
        {
            string layer = string.Empty;
            try { layer = entity.Layer; } catch { }
            return string.IsNullOrWhiteSpace(layer)
                ? "Survey Boundary " + number.ToString(CultureInfo.InvariantCulture)
                : layer + " - Boundary " + number.ToString(CultureInfo.InvariantCulture);
        }

        private static string BuildKml(IList<GoogleEarthBoundary> boundaries)
        {
            var builder = new StringBuilder();
            builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            builder.AppendLine("<kml xmlns=\"http://www.opengis.net/kml/2.2\">");
            builder.AppendLine("  <Document>");
            builder.AppendLine("    <name>CE Tools Survey Boundaries</name>");
            foreach (GoogleEarthBoundary boundary in boundaries)
            {
                builder.AppendLine("    <Placemark>");
                builder.Append("      <name>")
                    .Append(EscapeXml(boundary.Name))
                    .AppendLine("</name>");
                builder.AppendLine("      <Polygon>");
                builder.AppendLine("        <tessellate>1</tessellate>");
                builder.AppendLine("        <outerBoundaryIs>");
                builder.AppendLine("          <LinearRing>");
                builder.AppendLine("            <coordinates>");
                foreach (Point3d point in boundary.Points)
                {
                    builder.Append("              ")
                        .Append(point.X.ToString("0.##########", CultureInfo.InvariantCulture))
                        .Append(',')
                        .Append(point.Y.ToString("0.##########", CultureInfo.InvariantCulture))
                        .Append(',')
                        .Append(point.Z.ToString("0.###", CultureInfo.InvariantCulture))
                        .AppendLine();
                }
                builder.AppendLine("            </coordinates>");
                builder.AppendLine("          </LinearRing>");
                builder.AppendLine("        </outerBoundaryIs>");
                builder.AppendLine("      </Polygon>");
                builder.AppendLine("    </Placemark>");
            }
            builder.AppendLine("  </Document>");
            builder.AppendLine("</kml>");
            return builder.ToString();
        }

        private static string EscapeXml(string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private static bool OpenInGoogleEarth(string path)
        {
            string[] executables =
            {
                @"C:\Program Files\Google\Google Earth Pro\client\googleearth.exe",
                @"C:\Program Files (x86)\Google\Google Earth Pro\client\googleearth.exe"
            };
            foreach (string executable in executables)
            {
                if (!File.Exists(executable)) continue;
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = "\"" + path + "\"",
                        UseShellExecute = true
                    });
                    return true;
                }
                catch { }
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private sealed class GoogleEarthBoundary
        {
            internal GoogleEarthBoundary(string name, IList<Point3d> points)
            {
                Name = name ?? string.Empty;
                Points = points == null
                    ? new List<Point3d>()
                    : new List<Point3d>(points);
            }

            internal string Name { get; private set; }
            internal IList<Point3d> Points { get; private set; }
        }
    }
}
