using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class AutoDesignScanValidation
{
    internal static IEnumerable<string> FindErrors(SyntaxTree tree)
    {
        if (!tree.FilePath.EndsWith("AutoDesignCommands.cs", StringComparison.OrdinalIgnoreCase)) yield break;
        foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var member = call.Expression as MemberAccessExpressionSyntax;
            string name = member?.Name.Identifier.ValueText ?? (call.Expression as IdentifierNameSyntax)?.Identifier.ValueText ?? "";
            if (name != "ComputeFingerPrint" && name != "GetProperties" && name != "AppendDictionary") continue;
            int line = tree.GetLineSpan(call.Span).StartLinePosition.Line + 1;
            yield return $"{tree.FilePath}({line}): AutoDesign analysis must use revision tracking, not native fingerprint/property/dictionary scanning ({name}).";
        }
    }

    internal static void RunRegressionChecks()
    {
        Check(true, "x.ComputeFingerPrint();", "AutoDesignCommands.cs");
        Check(true, "x.GetType().GetProperties();", "AutoDesignCommands.cs");
        Check(true, "AppendDictionary(x);", "AutoDesignCommands.cs");
        Check(false, "AutoDesignDrawingChanges.For(db);", "AutoDesignCommands.cs");
        Check(false, "x.GetType().GetProperties();", "ExistingManualCommand.cs");
    }

    private static void Check(bool expected, string body, string path)
    {
        var tree = CSharpSyntaxTree.ParseText("class C { void M() { " + body + " } }", path: path);
        if (FindErrors(tree).Any() != expected) throw new InvalidOperationException("AutoDesign scan safety regression failed.");
    }
}
