using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

string repositoryRoot = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Directory.GetCurrentDirectory();
string sourceRoot = Path.Combine(repositoryRoot, "src", "CE.Tools.Civil3D");

if (!Directory.Exists(sourceRoot))
{
    Console.Error.WriteLine($"Civil 3D source folder was not found: {sourceRoot}");
    return 1;
}

string[] sourceFiles = Directory.GetFiles(
    sourceRoot,
    "*.cs",
    SearchOption.AllDirectories);
var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
var failures = new List<string>();

foreach (string sourceFile in sourceFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
{
    string text = File.ReadAllText(sourceFile);
    SyntaxTree tree = CSharpSyntaxTree.ParseText(
        text,
        parseOptions,
        path: sourceFile);

    foreach (Diagnostic diagnostic in tree.GetDiagnostics()
        .Where(item => item.Severity == DiagnosticSeverity.Error))
    {
        FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
        int line = span.StartLinePosition.Line + 1;
        int column = span.StartLinePosition.Character + 1;
        string relative = Path.GetRelativePath(repositoryRoot, sourceFile);
        failures.Add($"{relative}({line},{column}): {diagnostic.Id}: {diagnostic.GetMessage()}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("Civil 3D C# syntax validation failed:");
    foreach (string failure in failures)
        Console.Error.WriteLine($"- {failure}");
    return 1;
}

Console.WriteLine($"Civil 3D C# syntax validation passed for {sourceFiles.Length} source files.");
return 0;
