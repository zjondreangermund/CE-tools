using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.Settings;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.NamibiaCoordinateRuntimeCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Namibia survey-grid conversion for Schwarzeck / Lo22 zones. Autodesk
    /// GeoLocationData is still used as the fallback for non-Namibia coordinate
    /// systems. The implementation follows the EPSG Lo22 Transverse Mercator
    /// (South Orientated) definition and the Schwarzeck to WGS84 3-parameter
    /// transformation.
    /// </summary>
    public sealed class NamibiaCoordinateRuntimeCommands
    {
        [CommandMethod("CE_TOOLS", "CE_NAMIBIALO", CommandFlags.Modal | CommandFlags.Redraw)]
        public void NamibiaLoTools()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            int inferred;
            NamibiaCoordinateRuntime.TryInferLoZone(out inferred);
            if (inferred <= 0) inferred = 17;

            var model = new ProductionSettingsDialogModel(
                "CE Tools - Namibia LO / WGS84 Survey Conversion",
                "Convert between WGS84 and the Namibia Schwarzeck Lo22 survey grid. Survey Y is westing and Survey X is southing; CE drawing X stores Survey Y and drawing Y stores Survey X.");
            model.AddChoice(
                "Action", "01 Conversion", "Action", "Pick drawing point -> WGS84 / LO",
                "Pick a point, convert entered WGS84 decimal/DMS values, or convert entered LO survey coordinates.",
                new[] { "Pick drawing point -> WGS84 / LO", "WGS84 -> LO / Drawing XY", "LO / Drawing XY -> WGS84" });
            model.AddChoice(
                "Zone", "01 Conversion", "LO central meridian", inferred.ToString(CultureInfo.InvariantCulture),
                "Odd-degree Namibia survey-grid central meridian.",
                new[] { "11", "13", "15", "17", "19", "21", "23", "25" });
            model.AddText("Latitude", "02 WGS84", "Latitude (decimal or DMS)", "-22.5609", "Examples: -22.5609 or 22°33'39.24\" S.");
            model.AddText("Longitude", "02 WGS84", "Longitude (decimal or DMS)", "17.0658", "Examples: 17.0658 or 17°03'56.88\" E.");
            model.AddText("SurveyY", "03 Namibia LO", "Survey Y / westing / Drawing X", "0.000", "Westing-positive survey Y coordinate. East of the central meridian is normally negative.");
            model.AddText("SurveyX", "03 Namibia LO", "Survey X / southing / Drawing Y", "0.000", "Southing-positive survey X coordinate. North of latitude -22 is normally negative.");
            model.AddChoice("Map", "04 Map", "Open converted WGS84 point", "Do not open map", "Optionally open the converted point after calculation.", new[] { "Do not open map", "Google Maps", "Google Earth" });
            if (!DisciplineWorkflowDialogs.EditSettings(model)) return;

            int zone;
            if (!int.TryParse(model.Text("Zone"), NumberStyles.Integer, CultureInfo.InvariantCulture, out zone)) zone = inferred;
            string action = model.Text("Action");
            Point3d drawing;
            double latitude;
            double longitude;
            double surveyY;
            double surveyX;

            if (action.StartsWith("Pick", StringComparison.OrdinalIgnoreCase))
            {
                PromptPointResult picked = document.Editor.GetPoint("\nPick the drawing/survey point to convert: ");
                if (picked.Status != PromptStatus.OK) return;
                drawing = picked.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
                surveyY = drawing.X;
                surveyX = drawing.Y;
                if (!NamibiaLoProjection.TryLoToWgs84(zone, surveyY, surveyX, drawing.Z, out latitude, out longitude))
                {
                    document.Editor.WriteMessage("\nCE_NAMIBIALO could not convert the selected point.");
                    return;
                }
            }
            else if (action.StartsWith("WGS84", StringComparison.OrdinalIgnoreCase))
            {
                if (!NamibiaLoProjection.TryParseAngle(model.Text("Latitude"), true, out latitude) ||
                    !NamibiaLoProjection.TryParseAngle(model.Text("Longitude"), false, out longitude))
                {
                    document.Editor.WriteMessage("\nCE_NAMIBIALO: enter valid WGS84 decimal or DMS latitude/longitude values.");
                    return;
                }
                if (!NamibiaLoProjection.TryWgs84ToLo(zone, latitude, longitude, 0.0, out surveyY, out surveyX))
                {
                    document.Editor.WriteMessage("\nCE_NAMIBIALO could not project the WGS84 point.");
                    return;
                }
                drawing = new Point3d(surveyY, surveyX, 0.0);
            }
            else
            {
                if (!TryNumber(model.Text("SurveyY"), out surveyY) || !TryNumber(model.Text("SurveyX"), out surveyX))
                {
                    document.Editor.WriteMessage("\nCE_NAMIBIALO: enter valid Survey Y and Survey X values.");
                    return;
                }
                drawing = new Point3d(surveyY, surveyX, 0.0);
                if (!NamibiaLoProjection.TryLoToWgs84(zone, surveyY, surveyX, 0.0, out latitude, out longitude))
                {
                    document.Editor.WriteMessage("\nCE_NAMIBIALO could not convert the LO point.");
                    return;
                }
            }

            var rows = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("LO central meridian", zone.ToString(CultureInfo.InvariantCulture) + "°E"),
                new KeyValuePair<string, string>("Survey Y / westing", surveyY.ToString("N3", CultureInfo.CurrentCulture)),
                new KeyValuePair<string, string>("Survey X / southing", surveyX.ToString("N3", CultureInfo.CurrentCulture)),
                new KeyValuePair<string, string>("Drawing X", drawing.X.ToString("N3", CultureInfo.CurrentCulture)),
                new KeyValuePair<string, string>("Drawing Y", drawing.Y.ToString("N3", CultureInfo.CurrentCulture)),
                new KeyValuePair<string, string>("Latitude", latitude.ToString("0.00000000", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Longitude", longitude.ToString("0.00000000", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Latitude DMS", NamibiaLoProjection.FormatDms(latitude, true)),
                new KeyValuePair<string, string>("Longitude DMS", NamibiaLoProjection.FormatDms(longitude, false))
            };
            PopupTablePresenter.ShowReview(
                "CE Tools - Namibia Coordinate Result",
                "Schwarzeck / Lo22 survey-grid conversion. Drawing X = Survey Y (westing); Drawing Y = Survey X (southing).",
                rows,
                "Close");
            OpenMap(model.Text("Map"), latitude, longitude);
        }

        [CommandMethod("CE_TOOLS", "CE_COORDPICKMAP", CommandFlags.Modal | CommandFlags.Redraw)]
        public void PickMapCoordinate()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            PromptPointResult picked = document.Editor.GetPoint("\nPick a drawing point for live WGS84/LO coordinate review: ");
            if (picked.Status != PromptStatus.OK) return;
            Point3d point = picked.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
            Point3d geo;
            string error;
            if (!NamibiaCoordinateRuntime.TryDrawingToWgs84(document.Database, point, out geo, out error))
            {
                document.Editor.WriteMessage("\nCE_COORDPICKMAP stopped. {0}", error);
                return;
            }
            int zone;
            NamibiaCoordinateRuntime.TryInferLoZone(out zone);
            var rows = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Drawing X / Survey Y", point.X.ToString("N3", CultureInfo.CurrentCulture)),
                new KeyValuePair<string, string>("Drawing Y / Survey X", point.Y.ToString("N3", CultureInfo.CurrentCulture)),
                new KeyValuePair<string, string>("Latitude", geo.Y.ToString("0.00000000", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Longitude", geo.X.ToString("0.00000000", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("Latitude DMS", NamibiaLoProjection.FormatDms(geo.Y, true)),
                new KeyValuePair<string, string>("Longitude DMS", NamibiaLoProjection.FormatDms(geo.X, false)),
                new KeyValuePair<string, string>("Detected LO", zone > 0 ? "LO" + zone.ToString(CultureInfo.InvariantCulture) : "GeoData fallback")
            };
            PopupTablePresenter.ShowReview("CE Tools - Picked Coordinate", "Coordinate values were calculated from the point you selected in this drawing.", rows, "Close");
        }

        private static bool TryNumber(string text, out double value)
        {
            return (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value) ||
                    double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value)) &&
                   !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static void OpenMap(string provider, double latitude, double longitude)
        {
            if (string.IsNullOrWhiteSpace(provider) || provider.StartsWith("Do not", StringComparison.OrdinalIgnoreCase)) return;
            string lat = latitude.ToString("0.########", CultureInfo.InvariantCulture);
            string lon = longitude.ToString("0.########", CultureInfo.InvariantCulture);
            string url = string.Equals(provider, "Google Earth", StringComparison.OrdinalIgnoreCase)
                ? "https://earth.google.com/web/@" + lat + "," + lon + ",1000a,1000d,35y,0h,0t,0r"
                : "https://www.google.com/maps/search/?api=1&query=" + lat + "%2C" + lon;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }
    }

    internal static class NamibiaCoordinateRuntime
    {
        internal static bool TryInferLoZone(out int zone)
        {
            zone = 0;
            try
            {
                CivilDocument civil = CivilApplication.ActiveDocument;
                if (civil == null) return false;
                string code = civil.Settings.DrawingSettings.UnitZoneSettings.CoordinateSystemCode ?? string.Empty;
                string description = string.Empty;
                try
                {
                    SettingsCoordinateSystem settings = SettingsUnitZone.GetCoordinateSystemByCode(code);
                    if (settings != null)
                        description = string.Join(" ", settings.Description, settings.Category, settings.Projection, settings.Datum);
                }
                catch { }
                string combined = code + " " + description;
                bool namibia = combined.IndexOf("Schwarzeck", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               combined.IndexOf("South West African", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               combined.IndexOf("Lo22", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               combined.IndexOf("LO", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!namibia) return false;
                foreach (int candidate in new[] { 11, 13, 15, 17, 19, 21, 23, 25 })
                {
                    string token = candidate.ToString(CultureInfo.InvariantCulture);
                    if (combined.IndexOf("LO" + token, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        combined.IndexOf("/" + token, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Regex.IsMatch(combined, @"(?:^|[^0-9])" + token + @"(?:[^0-9]|$)"))
                    {
                        zone = candidate;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        internal static bool TryDrawingToWgs84(Database database, Point3d drawingPoint, out Point3d geographic, out string error)
        {
            int zone;
            if (TryInferLoZone(out zone))
            {
                double latitude;
                double longitude;
                if (NamibiaLoProjection.TryLoToWgs84(zone, drawingPoint.X, drawingPoint.Y, drawingPoint.Z, out latitude, out longitude))
                {
                    geographic = new Point3d(longitude, latitude, drawingPoint.Z);
                    error = string.Empty;
                    return true;
                }
            }
            return GeoCoordinateTransform.TryDrawingToWgs84(database, drawingPoint, out geographic, out error);
        }

        internal static bool TryWgs84ToDrawing(Database database, double latitude, double longitude, double elevation, out Point3d drawingPoint, out string error)
        {
            int zone;
            if (TryInferLoZone(out zone))
            {
                double surveyY;
                double surveyX;
                if (NamibiaLoProjection.TryWgs84ToLo(zone, latitude, longitude, elevation, out surveyY, out surveyX))
                {
                    drawingPoint = new Point3d(surveyY, surveyX, elevation);
                    error = string.Empty;
                    return true;
                }
            }
            return GeoCoordinateTransform.TryWgs84ToDrawing(database, latitude, longitude, elevation, out drawingPoint, out error);
        }
    }

    internal static class NamibiaLoProjection
    {
        private const double WgsA = 6378137.0;
        private const double WgsInvF = 298.257223563;
        private const double SchwarzeckA = 6377483.86528042;
        private const double SchwarzeckInvF = 299.1528128;
        private const double GermanLegalMetre = 1.0000135965;
        private const double Dx = 616.0;
        private const double Dy = 97.0;
        private const double Dz = -251.0;
        private const double LatitudeOriginDegrees = -22.0;

        internal static bool TryWgs84ToLo(int centralMeridian, double latitude, double longitude, double height, out double surveyY, out double surveyX)
        {
            surveyY = 0.0;
            surveyX = 0.0;
            if (!ValidZone(centralMeridian) || latitude < -90.0 || latitude > 90.0 || longitude < -180.0 || longitude > 180.0) return false;
            Vector3d wgs = GeodeticToEcef(latitude, longitude, height, WgsA, WgsInvF);
            Vector3d schwar = new Vector3d(wgs.X - Dx, wgs.Y - Dy, wgs.Z - Dz);
            GeodeticCoordinate geographic = EcefToGeodetic(schwar, SchwarzeckA, SchwarzeckInvF);
            double easting;
            double northing;
            TransverseMercatorForward(geographic.Latitude, geographic.Longitude, centralMeridian, out easting, out northing);
            surveyY = -easting / GermanLegalMetre;
            surveyX = -northing / GermanLegalMetre;
            return IsFinite(surveyY) && IsFinite(surveyX);
        }

        internal static bool TryLoToWgs84(int centralMeridian, double surveyY, double surveyX, double height, out double latitude, out double longitude)
        {
            latitude = 0.0;
            longitude = 0.0;
            if (!ValidZone(centralMeridian) || !IsFinite(surveyY) || !IsFinite(surveyX)) return false;
            double easting = -surveyY * GermanLegalMetre;
            double northing = -surveyX * GermanLegalMetre;
            double schwarLatitude;
            double schwarLongitude;
            TransverseMercatorInverse(easting, northing, centralMeridian, out schwarLatitude, out schwarLongitude);
            Vector3d schwar = GeodeticToEcef(schwarLatitude, schwarLongitude, height, SchwarzeckA, SchwarzeckInvF);
            Vector3d wgs = new Vector3d(schwar.X + Dx, schwar.Y + Dy, schwar.Z + Dz);
            GeodeticCoordinate geographic = EcefToGeodetic(wgs, WgsA, WgsInvF);
            latitude = geographic.Latitude;
            longitude = geographic.Longitude;
            return IsFinite(latitude) && IsFinite(longitude);
        }

        internal static bool TryParseAngle(string text, bool latitude, out double value)
        {
            value = 0.0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string source = text.Trim().ToUpperInvariant();
            double decimalValue;
            if (double.TryParse(source, NumberStyles.Float, CultureInfo.InvariantCulture, out decimalValue) ||
                double.TryParse(source, NumberStyles.Float, CultureInfo.CurrentCulture, out decimalValue))
            {
                value = decimalValue;
                return latitude ? value >= -90.0 && value <= 90.0 : value >= -180.0 && value <= 180.0;
            }
            bool negativeHemisphere = source.Contains("S") || source.Contains("W");
            MatchCollection matches = Regex.Matches(source, @"[-+]?\d+(?:[\.,]\d+)?");
            if (matches.Count == 0) return false;
            double degrees;
            double minutes = 0.0;
            double seconds = 0.0;
            if (!double.TryParse(matches[0].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out degrees)) return false;
            if (matches.Count > 1) double.TryParse(matches[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out minutes);
            if (matches.Count > 2) double.TryParse(matches[2].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
            double sign = degrees < 0.0 || negativeHemisphere ? -1.0 : 1.0;
            value = sign * (Math.Abs(degrees) + Math.Abs(minutes) / 60.0 + Math.Abs(seconds) / 3600.0);
            return latitude ? value >= -90.0 && value <= 90.0 : value >= -180.0 && value <= 180.0;
        }

        internal static string FormatDms(double value, bool latitude)
        {
            string hemisphere = latitude ? (value < 0.0 ? "S" : "N") : (value < 0.0 ? "W" : "E");
            double absolute = Math.Abs(value);
            int degrees = (int)Math.Floor(absolute);
            double minuteValue = (absolute - degrees) * 60.0;
            int minutes = (int)Math.Floor(minuteValue);
            double seconds = (minuteValue - minutes) * 60.0;
            return string.Format(CultureInfo.InvariantCulture, "{0}°{1:00}'{2:00.000}\" {3}", degrees, minutes, seconds, hemisphere);
        }

        private static void TransverseMercatorForward(double latitudeDegrees, double longitudeDegrees, int centralMeridian, out double easting, out double northing)
        {
            double f = 1.0 / SchwarzeckInvF;
            double e2 = f * (2.0 - f);
            double ep2 = e2 / (1.0 - e2);
            double phi = DegreesToRadians(latitudeDegrees);
            double lambda = DegreesToRadians(longitudeDegrees);
            double lambda0 = DegreesToRadians(centralMeridian);
            double phi0 = DegreesToRadians(LatitudeOriginDegrees);
            double sin = Math.Sin(phi);
            double cos = Math.Cos(phi);
            double tan = Math.Tan(phi);
            double n = SchwarzeckA / Math.Sqrt(1.0 - e2 * sin * sin);
            double t = tan * tan;
            double c = ep2 * cos * cos;
            double a = (lambda - lambda0) * cos;
            double m = MeridianArc(phi, SchwarzeckA, e2);
            double m0 = MeridianArc(phi0, SchwarzeckA, e2);
            easting = n * (a + (1.0 - t + c) * Math.Pow(a, 3.0) / 6.0 +
                (5.0 - 18.0 * t + t * t + 72.0 * c - 58.0 * ep2) * Math.Pow(a, 5.0) / 120.0);
            northing = (m - m0) + n * tan * (a * a / 2.0 +
                (5.0 - t + 9.0 * c + 4.0 * c * c) * Math.Pow(a, 4.0) / 24.0 +
                (61.0 - 58.0 * t + t * t + 600.0 * c - 330.0 * ep2) * Math.Pow(a, 6.0) / 720.0);
        }

        private static void TransverseMercatorInverse(double easting, double northing, int centralMeridian, out double latitude, out double longitude)
        {
            double f = 1.0 / SchwarzeckInvF;
            double e2 = f * (2.0 - f);
            double ep2 = e2 / (1.0 - e2);
            double e4 = e2 * e2;
            double e6 = e4 * e2;
            double phi0 = DegreesToRadians(LatitudeOriginDegrees);
            double m0 = MeridianArc(phi0, SchwarzeckA, e2);
            double m = m0 + northing;
            double mu = m / (SchwarzeckA * (1.0 - e2 / 4.0 - 3.0 * e4 / 64.0 - 5.0 * e6 / 256.0));
            double e1 = (1.0 - Math.Sqrt(1.0 - e2)) / (1.0 + Math.Sqrt(1.0 - e2));
            double phi1 = mu +
                (3.0 * e1 / 2.0 - 27.0 * Math.Pow(e1, 3.0) / 32.0) * Math.Sin(2.0 * mu) +
                (21.0 * e1 * e1 / 16.0 - 55.0 * Math.Pow(e1, 4.0) / 32.0) * Math.Sin(4.0 * mu) +
                (151.0 * Math.Pow(e1, 3.0) / 96.0) * Math.Sin(6.0 * mu) +
                (1097.0 * Math.Pow(e1, 4.0) / 512.0) * Math.Sin(8.0 * mu);
            double sin1 = Math.Sin(phi1);
            double cos1 = Math.Cos(phi1);
            double tan1 = Math.Tan(phi1);
            double n1 = SchwarzeckA / Math.Sqrt(1.0 - e2 * sin1 * sin1);
            double r1 = SchwarzeckA * (1.0 - e2) / Math.Pow(1.0 - e2 * sin1 * sin1, 1.5);
            double t1 = tan1 * tan1;
            double c1 = ep2 * cos1 * cos1;
            double d = easting / n1;
            double phi = phi1 - (n1 * tan1 / r1) *
                (d * d / 2.0 - (5.0 + 3.0 * t1 + 10.0 * c1 - 4.0 * c1 * c1 - 9.0 * ep2) * Math.Pow(d, 4.0) / 24.0 +
                 (61.0 + 90.0 * t1 + 298.0 * c1 + 45.0 * t1 * t1 - 252.0 * ep2 - 3.0 * c1 * c1) * Math.Pow(d, 6.0) / 720.0);
            double lambda = DegreesToRadians(centralMeridian) +
                (d - (1.0 + 2.0 * t1 + c1) * Math.Pow(d, 3.0) / 6.0 +
                 (5.0 - 2.0 * c1 + 28.0 * t1 - 3.0 * c1 * c1 + 8.0 * ep2 + 24.0 * t1 * t1) * Math.Pow(d, 5.0) / 120.0) / cos1;
            latitude = RadiansToDegrees(phi);
            longitude = RadiansToDegrees(lambda);
        }

        private static double MeridianArc(double phi, double a, double e2)
        {
            double e4 = e2 * e2;
            double e6 = e4 * e2;
            return a * ((1.0 - e2 / 4.0 - 3.0 * e4 / 64.0 - 5.0 * e6 / 256.0) * phi -
                (3.0 * e2 / 8.0 + 3.0 * e4 / 32.0 + 45.0 * e6 / 1024.0) * Math.Sin(2.0 * phi) +
                (15.0 * e4 / 256.0 + 45.0 * e6 / 1024.0) * Math.Sin(4.0 * phi) -
                (35.0 * e6 / 3072.0) * Math.Sin(6.0 * phi));
        }

        private static Vector3d GeodeticToEcef(double latitude, double longitude, double height, double a, double invF)
        {
            double f = 1.0 / invF;
            double e2 = f * (2.0 - f);
            double phi = DegreesToRadians(latitude);
            double lambda = DegreesToRadians(longitude);
            double sin = Math.Sin(phi);
            double cos = Math.Cos(phi);
            double n = a / Math.Sqrt(1.0 - e2 * sin * sin);
            return new Vector3d(
                (n + height) * cos * Math.Cos(lambda),
                (n + height) * cos * Math.Sin(lambda),
                (n * (1.0 - e2) + height) * sin);
        }

        private static GeodeticCoordinate EcefToGeodetic(Vector3d value, double a, double invF)
        {
            double f = 1.0 / invF;
            double e2 = f * (2.0 - f);
            double longitude = Math.Atan2(value.Y, value.X);
            double p = Math.Sqrt(value.X * value.X + value.Y * value.Y);
            double latitude = Math.Atan2(value.Z, p * (1.0 - e2));
            double height = 0.0;
            for (int i = 0; i < 20; i++)
            {
                double sin = Math.Sin(latitude);
                double n = a / Math.Sqrt(1.0 - e2 * sin * sin);
                double cos = Math.Cos(latitude);
                height = Math.Abs(cos) < 1e-15 ? 0.0 : p / cos - n;
                double next = Math.Atan2(value.Z, p * (1.0 - e2 * n / Math.Max(1.0, n + height)));
                if (Math.Abs(next - latitude) < 1e-13) { latitude = next; break; }
                latitude = next;
            }
            return new GeodeticCoordinate(RadiansToDegrees(latitude), RadiansToDegrees(longitude), height);
        }

        private static bool ValidZone(int zone) { return new[] { 11, 13, 15, 17, 19, 21, 23, 25 }.Contains(zone); }
        private static double DegreesToRadians(double value) { return value * Math.PI / 180.0; }
        private static double RadiansToDegrees(double value) { return value * 180.0 / Math.PI; }
        private static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        private sealed class GeodeticCoordinate
        {
            internal GeodeticCoordinate(double latitude, double longitude, double height) { Latitude = latitude; Longitude = longitude; Height = height; }
            internal double Latitude { get; private set; }
            internal double Longitude { get; private set; }
            internal double Height { get; private set; }
        }
    }
}
