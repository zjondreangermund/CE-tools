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

        internal static List<AssemblyTemplate> Discover()
        {
            var roots = new List<string>();
            string location = typeof(CivilDocument).Assembly.Location;
            Match year = Regex.Match(location, @"20\d{2}");
            if (year.Success)
            {
                foreach (Environment.SpecialFolder folder in new[]
                {
                    Environment.SpecialFolder.CommonApplicationData,
                    Environment.SpecialFolder.ApplicationData
                }) roots.Add(Path.Combine(Environment.GetFolderPath(folder), "Autodesk", "C3D " + year.Value));
            }
            // Respect customized palette paths as well as the stock installation.
            try
            {
                string configured = Convert.ToString(AcApplication.GetSystemVariable("TOOLPALETTEPATH"));
                roots.AddRange((configured ?? string.Empty).Split(';')
                    .Select(value => Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'))));
            }
            catch { }
            return AssemblyTemplateCatalog.Discover(roots);
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
