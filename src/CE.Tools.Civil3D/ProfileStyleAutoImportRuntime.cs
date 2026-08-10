using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices.Styles;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.ProfileStyleAutoImportCommands))]

namespace CETools.Civil3D
{
    public sealed class ProfileStyleAutoImportCommands
    {
        [CommandMethod("CE_TOOLS", "CE_PROFILESTYLEAUTOIMPORT", CommandFlags.Modal | CommandFlags.Redraw)]
        public void ImportIfMissing()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            int imported;
            string message;
            bool changed = ProfileStyleAutoImportRuntime.EnsureBundledProfileStyles(document, out imported, out message);
            document.Editor.Regen();
            document.Editor.WriteMessage(
                "\nCE_PROFILESTYLEAUTOIMPORT: {0} Source styles processed={1}.",
                message,
                imported);
        }
    }

    internal static class ProfileStyleAutoImportRuntime
    {
        private static readonly HashSet<Database> Attempted = new HashSet<Database>();
        private const string RoadDefaultBandSet = "Road-Single-Band Set 1-Full Grid";

        internal static bool EnsureBundledProfileStyles(Document document, out int imported, out string message)
        {
            imported = 0;
            message = string.Empty;
            if (document == null || CivilApplication.ActiveDocument == null)
            {
                message = "No active Civil 3D document.";
                return false;
            }

            if (HasExpectedBandLibrary(document.Database, CivilApplication.ActiveDocument))
            {
                message = "Profile/band style library already present; no import required.";
                return false;
            }

            lock (Attempted)
            {
                if (Attempted.Contains(document.Database))
                {
                    message = "Automatic style import was already attempted for this drawing; use CE_PROJECTSTYLEIMPORT if a project-specific source is still required.";
                    return false;
                }
                Attempted.Add(document.Database);
            }

            try
            {
                Type centreType = typeof(ProjectStyleCenterCommands);
                MethodInfo findSources = centreType.GetMethod(
                    "FindBundledStyleSources",
                    BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo export = centreType.GetMethod(
                    "ExportStylesFromSource",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(Database), typeof(StyleConflictResolverType) },
                    null);
                if (findSources == null || export == null)
                {
                    message = "Bundled style import helpers are unavailable in this build.";
                    return false;
                }

                object sourceResult = findSources.Invoke(null, null);
                IEnumerable sources = sourceResult as IEnumerable;
                if (sources == null)
                {
                    message = "No bundled CE style sources were discovered.";
                    return false;
                }

                var paths = new List<string>();
                foreach (object source in sources)
                {
                    string path = ReadStringProperty(source, "FilePath");
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) paths.Add(path);
                }
                paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (paths.Count == 0)
                {
                    message = "The bundled CE project-style DWGs were not found; use CE_PROJECTSTYLEIMPORT to browse to a project source.";
                    return false;
                }

                foreach (string path in paths)
                {
                    object value = export.Invoke(
                        null,
                        new object[]
                        {
                            path,
                            document.Database,
                            StyleConflictResolverType.Rename
                        });
                    if (value is int) imported += (int)value;
                }

                message = HasExpectedBandLibrary(document.Database, CivilApplication.ActiveDocument)
                    ? "Missing profile/band styles were imported automatically from the supplied CE style sources."
                    : "Bundled styles were processed, but the expected road band-set name is still not present; the command will continue with the best available project/drawing style.";
                return imported > 0;
            }
            catch (TargetInvocationException exception)
            {
                Exception inner = exception.InnerException ?? exception;
                message = "Automatic style import warning: " + inner.Message;
                return false;
            }
            catch (Exception exception)
            {
                message = "Automatic style import warning: " + exception.Message;
                return false;
            }
        }

        private static bool HasExpectedBandLibrary(Database database, CivilDocument civilDocument)
        {
            if (database == null || civilDocument == null) return false;
            object collection;
            try { collection = civilDocument.Styles.ProfileViewBandSetStyles; }
            catch { return false; }
            IList<object> values = CivilStyleDiscovery.Enumerate(collection);
            if (values == null || values.Count == 0) return false;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                int usable = 0;
                foreach (object item in values)
                {
                    if (!(item is ObjectId)) continue;
                    ObjectId id = (ObjectId)item;
                    if (id.IsNull) continue;
                    DBObject style;
                    try { style = transaction.GetObject(id, OpenMode.ForRead, false); }
                    catch { continue; }
                    string name = ReadStringProperty(style, "Name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    usable++;
                    if (string.Equals(name, RoadDefaultBandSet, StringComparison.OrdinalIgnoreCase)) return true;
                }
                // A substantial user/project band library is also considered
                // available even if it intentionally renamed the road default.
                return usable >= 8;
            }
        }

        private static string ReadStringProperty(object target, string name)
        {
            if (target == null) return string.Empty;
            try
            {
                PropertyInfo property = target.GetType().GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property == null || !property.CanRead) return string.Empty;
                return Convert.ToString(property.GetValue(target, null), CultureInfo.CurrentCulture) ?? string.Empty;
            }
            catch { return string.Empty; }
        }
    }
}
