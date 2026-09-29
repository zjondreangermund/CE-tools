using System;
using System.IO;
using System.Linq;
using System.Xml;
using CETools.Civil3D;

internal static class Program
{
    private static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ce-atc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string basicId = Guid.NewGuid().ToString("D");
            string namedId = Guid.NewGuid().ToString("D");
            string catalog = Path.Combine(directory, "Metric.atc");
            File.WriteAllText(catalog, "<Catalog xmlns='urn:autodesk:catalog'><Tools>" +
                Tool(basicId, "", "meter", "AeccDbAssembly") +
                Tool(namedId, "Primary Road Full Section", "meter", "AeccDbAssembly") +
                Tool(Guid.NewGuid().ToString(), "Lane", "meter", "AeccDbSubassembly") +
                Tool("not-a-guid", "Invalid", "meter", "AeccDbAssembly") + "</Tools></Catalog>");
            var parsed = AssemblyTemplateCatalog.ReadCatalog(catalog);
            Check(parsed.Count == 2, "Only real assembly tools with valid ids may be imported.");
            Check(parsed[0].Name == "Basic Assembly", "Unnamed tools use their external DWG's name.");
            Check(parsed[0].ItemId == basicId, "Native ImportAssembly requires a GUID without braces.");
            Check(parsed[1].Name == "Primary Road Full Section", "The palette's display name must be preserved.");

            File.WriteAllText(Path.Combine(directory, "Other.atc"), "<Catalog>" +
                Tool(basicId, "Duplicate Basic", "meter", "AeccDbAssembly") +
                Tool(basicId, "Basic Assembly", "foot", "AeccDbAssembly") +
                Tool(Guid.NewGuid().ToString(), "Basic Assembly", "meter", "AeccDbAssembly") + "</Catalog>");
            File.WriteAllText(Path.Combine(directory, "Broken.atc"), "<malformed");
            var discovered = AssemblyTemplateCatalog.Discover(new[] { directory, directory, Path.Combine(directory, "missing") });
            Check(discovered.Count == 4, "Repeated catalogs are deduplicated while unit variants and distinct templates remain available.");
            Check(discovered.Select(item => item.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4,
                "Duplicate display names must still select distinct native tools.");
            Check(discovered.Any(item => item.DisplayName.Contains("[foot]")), "Imperial templates must show their units.");

            string unsafeCatalog = Path.Combine(directory, "Dtd.atc");
            File.WriteAllText(unsafeCatalog, "<!DOCTYPE Catalog [<!ENTITY content SYSTEM 'file:///etc/hostname'>]><Catalog>&content;</Catalog>");
            bool rejected = false;
            try { AssemblyTemplateCatalog.ReadCatalog(unsafeCatalog); } catch (XmlException) { rejected = true; }
            Check(rejected, "Palette XML must not expand external entities.");
            Console.WriteLine("8 assembly catalog behavior checks passed.");
            return 0;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static string Tool(string id, string name, string units, string kind)
    {
        return "<Tool><ItemID idValue='{" + id + "}'/><Properties><ItemName>" + name +
            "</ItemName></Properties><Data><" + kind + "><ExternalDrawing>%AECCCONTENT_DIR%\\Assemblies\\Metric\\Basic Assembly.dwg</ExternalDrawing></" +
            kind + "><Units>" + units + "</Units></Data></Tool>";
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
