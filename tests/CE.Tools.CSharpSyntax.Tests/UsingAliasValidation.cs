using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class UsingAliasValidation
{
    internal static IReadOnlyList<Diagnostic> FindConflicts(IEnumerable<SyntaxTree> trees)
    {
        // Alias name/scope conflicts do not depend on the aliased Autodesk type.
        // Compile only file/global aliases, using Object for their targets so
        // this real compiler check can run without proprietary CAD assemblies.
        // Namespace-local aliases have their own scope and may shadow globals.
        var headers = trees.Select(tree =>
        {
            var root = (CompilationUnitSyntax)tree.GetRoot();
            var aliases = root.Usings.Where(item => item.Alias != null).Select(item =>
                item.WithName(SyntaxFactory.ParseName("global::System.Object")));
            return CSharpSyntaxTree.Create(
                SyntaxFactory.CompilationUnit().WithUsings(SyntaxFactory.List(aliases)),
                (CSharpParseOptions)tree.Options, tree.FilePath);
        });
        var compilation = CSharpCompilation.Create("CivilUsingAliasValidation", headers,
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return compilation.GetDiagnostics().Where(item => item.Id == "CS1537").ToArray();
    }

    internal static void RunRegressionChecks()
    {
        const string global = "global using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;";
        const string local = "using DBObject = Autodesk.AutoCAD.DatabaseServices.DBObject;";
        Check(true, global, local); // Reported Civil 3D 2023 build failure.
        Check(true, global, global); // Two shared alias declarations also conflict.
        Check(false, local, local); // Same local name in separate files is legal.
        Check(false, global, "using CivilObject = Autodesk.Civil.DatabaseServices.DBObject;");
    }

    private static void Check(bool expectedConflict, params string[] sources)
    {
        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = sources.Select((source, index) => CSharpSyntaxTree.ParseText(source, options,
            path: "AliasRegression" + index + ".cs"));
        if ((FindConflicts(trees).Count > 0) != expectedConflict)
            throw new InvalidOperationException("Shared-alias compiler regression failed.");
    }
}
