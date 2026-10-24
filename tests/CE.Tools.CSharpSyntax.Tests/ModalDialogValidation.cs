using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class ModalDialogValidation
{
    private const string CoreApplication = "Autodesk.AutoCAD.ApplicationServices.Core.Application";

    // Core.Application has no WinForms ShowModalDialog API. Check the owner as
    // well as syntax, without needing proprietary Autodesk reference assemblies.
    internal static IEnumerable<string> FindErrors(SyntaxTree tree)
    {
        var root = tree.GetRoot();
        var coreAliases = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(x => x.Alias != null && x.Name?.ToString().Replace("global::", "") == CoreApplication)
            .Select(x => x.Alias!.Name.Identifier.ValueText).ToHashSet();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member || member.Name.Identifier.ValueText != "ShowModalDialog") continue;
            string owner = member.Expression.ToString().Replace("global::", "");
            if (owner != CoreApplication && !coreAliases.Contains(owner)) continue;
            int line = tree.GetLineSpan(member.Span).StartLinePosition.Line + 1;
            yield return $"{tree.FilePath}({line}): Core.Application does not define ShowModalDialog; use Autodesk.AutoCAD.ApplicationServices.Application for WinForms dialogs.";
        }
    }

    internal static void RunRegressionChecks()
    {
        Check(true, "using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application; class C { void M() { AcApplication.ShowModalDialog(form); } }");
        Check(true, "class C { void M() { Autodesk.AutoCAD.ApplicationServices.Core.Application.ShowModalDialog(form); } }");
        Check(false, "using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application; class C { void M() { AcApplication.ShowModalDialog(form); } }");
        Check(false, "using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application; class C { void M() { Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(form); } }");
    }

    private static void Check(bool expectedError, string source)
    {
        if (FindErrors(CSharpSyntaxTree.ParseText(source)).Any() != expectedError)
            throw new InvalidOperationException("AutoCAD modal dialog owner regression failed.");
    }
}
