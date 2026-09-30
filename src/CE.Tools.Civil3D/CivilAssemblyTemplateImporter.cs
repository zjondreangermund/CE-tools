using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using CivilAssembly = Autodesk.Civil.DatabaseServices.Assembly;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    internal static class CivilAssemblyTemplateImporter
    {
        internal const string Empty = "<Empty custom assembly>";
        internal const string Browse = "<Browse assembly DWG...>";

        private static readonly object DiscoverySync = new object();
        private static string _discoveryKey = string.Empty;
        private static List<AssemblyTemplate> _discoveryCache;

        internal static List<AssemblyTemplate> Discover()
        {
            var roots = new List<string>();
            var broadFallbackRoots = new List<string>();
            string location = typeof(CivilDocument).Assembly.Location;
            Match year = Regex.Match(location, @"20\d{2}");

            // Prefer the actual Tool Palettes folders instead of recursively
            // scanning the whole Civil 3D application-data tree. The latter can
            // contain thousands of unrelated files and made CE_ASSEMBLYCREATE
            // appear to freeze before its settings dialog opened.
            if (year.Success)
            {
                foreach (Environment.SpecialFolder folder in new[]
                {
                    Environment.SpecialFolder.CommonApplicationData,
                    Environment.SpecialFolder.ApplicationData
                })
                {
                    string baseRoot = Path.Combine(
                        Environment.GetFolderPath(folder),
                        "Autodesk",
                        "C3D " + year.Value);
                    broadFallbackRoots.Add(baseRoot);
                    roots.Add(Path.Combine(baseRoot, "enu", "Tool Palettes"));
                    roots.Add(Path.Combine(baseRoot, "Tool Palettes"));
                    roots.Add(Path.Combine(baseRoot, "enu", "ToolPalette"));
                }
            }

            string configured = string.Empty;
            try
            {
                configured = Convert.ToString(AcApplication.GetSystemVariable("TOOLPALETTEPATH")) ?? string.Empty;
                roots.AddRange(configured.Split(';')
                    .Select(value => Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')))
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            }
            catch { }

            roots = roots.Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string key = (year.Success ? year.Value : string.Empty) + "|" +
                configured + "|" + string.Join("|", roots.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

            lock (DiscoverySync)
            {
                if (_discoveryCache != null &&
                    string.Equals(_discoveryKey, key, StringComparison.Ordinal))
                    return new List<AssemblyTemplate>(_discoveryCache);
            }

            List<AssemblyTemplate> templates = AssemblyTemplateCatalog.Discover(roots);

            // Localized or non-standard installs may not use the common enu path.
            // Only if the targeted palette scan found nothing do we fall back to
            // the broader C3D folder scan.
            if (templates.Count == 0)
                templates = AssemblyTemplateCatalog.Discover(
                    broadFallbackRoots.Where(Directory.Exists)
                        .Distinct(StringComparer.OrdinalIgnoreCase));

            lock (DiscoverySync)
            {
                _discoveryKey = key;
                _discoveryCache = new List<AssemblyTemplate>(templates);
            }
            return new List<AssemblyTemplate>(templates);
        }

        internal static AssemblyTemplate BrowseDrawing(Document document)
        {
            var options = new PromptOpenFileOptions("Select an assembly drawing") { Filter = "Drawing (*.dwg)|*.dwg" };
            PromptFileNameResult file = document.Editor.GetFileNameForOpen(options);
            if (file.Status != PromptStatus.OK) return null;
            return new AssemblyTemplate { DrawingPath = file.StringResult, Name = Path.GetFileNameWithoutExtension(file.StringResult) };
        }

        internal static ObjectId Import(CivilDocument civil, Document document,
            AssemblyTemplate template, string name, Point3d location)
        {
            if (!string.IsNullOrWhiteSpace(template.CatalogPath))
                return civil.AssemblyCollection.ImportAssembly(name, template.CatalogPath, template.ItemId, location);

            // Read a saved DWG in a detached database; never lock a second live document.
            using (var source = new Database(false, true))
            {
                source.ReadDwgFile(template.DrawingPath, FileOpenMode.OpenForReadAndAllShare, true, null);
                source.CloseInput(true);
                var names = new List<string>();
                using (Transaction read = source.TransactionManager.StartTransaction())
                {
                    var space = (BlockTableRecord)read.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(source), OpenMode.ForRead);
                    foreach (ObjectId id in space)
                    {
                        var assembly = read.GetObject(id, OpenMode.ForRead, false) as CivilAssembly;
                        if (assembly != null) names.Add(assembly.Name);
                    }
                }
                if (names.Count == 0) throw new InvalidOperationException("The selected DWG contains no Civil 3D assemblies.");
                string selected = names[0];
                if (names.Count > 1)
                {
                    var settings = new ProductionSettingsDialogModel("CE Tools - Assembly in Drawing", "Choose the assembly to import with its subassemblies.");
                    settings.AddChoice("Assembly", "Assembly", "Source assembly", selected, "Assembly in the selected DWG.", names);
                    if (!DisciplineWorkflowDialogs.EditSettings(settings)) return ObjectId.Null;
                    selected = settings.Text("Assembly");
                }
                return civil.AssemblyCollection.ImportAssembly(name, source, selected, location);
            }
        }
    }
}
