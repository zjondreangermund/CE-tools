using System;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using CETools.Civil3D;

internal static class ProfileViewNativeEditorTests
{
    internal static void Run(Action<bool, string> check)
    {
        var source = new ObjectId { Value = new DBObject() };
        var targets = Enumerable.Range(0, 10).Select(_ => new ObjectId { Value = new DBObject() }).ToArray();

        foreach (string nativeName in new[] { "EDITGRAPHPROPERTIES", "AeccEditGraphProperties" })
        {
            var document = new Document();
            document.Editor.SetImpliedSelection(new[] { source }.Concat(targets).ToArray());
            bool nativeReturned = false;
            int applications = 0;
            ObjectId[] appliedTargets = null;
            document.Editor.Execute = arguments =>
            {
                check(document.Editor.ImpliedSelection.Length == 0,
                    "Explicit graph selection must also work with PICKFIRST disabled or cleared.");
                check(arguments.Length == 2 && Equals(arguments[0], "_.EditGraphProperties") &&
                    Equals(arguments[1], source),
                    "The graph prompt must receive the source entity, never the batch apply command.");
                // Native editing consumes/changes the pickset. The captured batch
                // must still contain all ten targets when the dialog is closed.
                document.Editor.SetImpliedSelection(new[] { source });
                document.End("REGEN");
                check(applications == 0, "Unrelated nested commands must not apply the batch.");
                document.End(nativeName);
                check(applications == 0, "Do not write target views inside the native CommandEnded event.");
                nativeReturned = true;
            };

            bool applied = ProfileViewNativeEditor.EditAndApply(document, source, () =>
            {
                check(nativeReturned, "Apply only after the native editor has returned.");
                check(document.ListenerCount == 0, "Detach native listeners before applying target edits.");
                applications++;
                appliedTargets = targets.ToArray();
            });
            check(applied && applications == 1 && appliedTargets.SequenceEqual(targets),
                "Complete once for the entire captured batch, regardless of the native pickset.");
            document.End(nativeName);
            check(applications == 1 && document.ListenerCount == 0,
                "Later commands must not repeat a completed batch.");
        }

        Action<Document>[] aborts =
        {
            doc => doc.Cancel("EDITGRAPHPROPERTIES"),
            doc => doc.Fail("AECCEDITGRAPHPROPERTIES"),
            doc => { doc.Cancel("EDITGRAPHPROPERTIES"); doc.End("EDITGRAPHPROPERTIES"); },
            doc => doc.End("REGEN"),
            doc => { }, // No successful native completion must not count as an edit.
            doc => { throw new Autodesk.AutoCAD.Runtime.Exception(ErrorStatus.UserBreak); }
        };
        foreach (Action<Document> abort in aborts)
        {
            var document = new Document();
            document.Editor.Execute = _ => abort(document);
            int applications = 0;
            bool applied = ProfileViewNativeEditor.EditAndApply(document, source, () => applications++);
            check(!applied && applications == 0, "Cancelled, failed or incomplete native edits must not update targets.");
            check(document.ListenerCount == 0, "Every exit path must remove the native event listeners.");
            document.End("EDITGRAPHPROPERTIES");
            check(applications == 0, "A cancelled edit must not leak into a later command.");
        }

        var failing = new Document();
        failing.Editor.Execute = _ => { throw new Autodesk.AutoCAD.Runtime.Exception(ErrorStatus.InvalidInput); };
        bool surfaced = false;
        int unexpectedApplications = 0;
        try { ProfileViewNativeEditor.EditAndApply(failing, source, () => unexpectedApplications++); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { surfaced = true; }
        check(surfaced && unexpectedApplications == 0 && failing.ListenerCount == 0,
            "Unexpected native errors must reach the command and release all listeners.");
    }
}
