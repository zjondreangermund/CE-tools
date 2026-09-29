using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace CETools.Civil3D
{
    // Reads the installed palette definitions, including country-kit assemblies.
    // Only AeccDbAssembly tools are accepted: lanes/daylights are subassemblies.
    internal sealed class AssemblyTemplate
    {
        internal string Name;
        internal string CatalogPath;
        internal string ItemId;
        internal string DrawingPath;
        internal string Units;
        internal string DisplayName;
    }

    internal static class AssemblyTemplateCatalog
    {
        internal static IList<AssemblyTemplate> ReadCatalog(string path)
        {
            var result = new List<AssemblyTemplate>();
            var document = new XmlDocument { XmlResolver = null };
            using (XmlReader reader = XmlReader.Create(path, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            })) document.Load(reader);
            foreach (XmlNode tool in document.SelectNodes("//*[local-name()='Tool']"))
            {
                XmlNode assembly = tool.SelectSingleNode(".//*[local-name()='AeccDbAssembly']");
                XmlNode item = tool.SelectSingleNode("./*[local-name()='ItemID']");
                Guid itemId;
                if (assembly == null || item == null || item.Attributes == null ||
                    !Guid.TryParse(item.Attributes["idValue"] == null ? null : item.Attributes["idValue"].Value, out itemId))
                    continue;
                XmlNode drawing = assembly.SelectSingleNode(".//*[local-name()='ExternalDrawing']");
                XmlNode label = tool.SelectSingleNode(".//*[local-name()='ItemName']");
                XmlNode units = tool.SelectSingleNode(".//*[local-name()='Units']");
                string drawingPath = drawing == null ? string.Empty : drawing.InnerText.Trim();
                // ATC paths always use Windows separators, even in portable tests.
                string name = label == null ? string.Empty : label.InnerText.Trim();
                if (name.Length == 0) name = Path.GetFileNameWithoutExtension(drawingPath.Replace('\\', '/'));
                if (string.IsNullOrWhiteSpace(name)) continue;
                result.Add(new AssemblyTemplate
                {
                    Name = name, CatalogPath = path, ItemId = itemId.ToString("D"),
                    DrawingPath = drawingPath, Units = units == null ? string.Empty : units.InnerText.Trim()
                });
            }
            return result;
        }

        internal static List<AssemblyTemplate> Discover(IEnumerable<string> roots)
        {
            var result = new List<AssemblyTemplate>();
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
                CollectCatalogs(root, files);
            foreach (string file in files.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                try { result.AddRange(ReadCatalog(file)); }
                catch (XmlException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            result = result.GroupBy(item => item.ItemId + "|" + item.Units, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AssemblyTemplate item in result)
            {
                string root = item.Name + (string.IsNullOrWhiteSpace(item.Units) ? string.Empty : " [" + item.Units + "]");
                string name = root;
                int suffix = 2;
                while (!usedNames.Add(name)) name = root + " (" + suffix++ + ")";
                item.DisplayName = name;
            }
            return result;
        }

        private static void CollectCatalogs(string root, ISet<string> files)
        {
            try
            {
                foreach (string file in Directory.GetFiles(root, "*.atc")) files.Add(file);
                foreach (string child in Directory.GetDirectories(root))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        CollectCatalogs(child, files);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
