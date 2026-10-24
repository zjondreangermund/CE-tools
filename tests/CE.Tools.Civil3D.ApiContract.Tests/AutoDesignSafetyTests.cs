using System;
using System.IO;
using CETools.Civil3D;
using Autodesk.AutoCAD.DatabaseServices;

internal static class AutoDesignSafetyTests
{
    internal static void Run(Action<bool, string> check)
    {
        var database = new Database();
        var changes = AutoDesignDrawingChanges.For(database);
        string stamp = changes.Stamp;
        check(AutoDesignDrawingChanges.For(database).Stamp == stamp, "Same drawing keeps its session and no-op revision.");
        database.NotifyModified();
        check(changes.Stamp != stamp, "Source modification invalidates completed work.");
        stamp = changes.Stamp; database.NotifyAppended();
        check(changes.Stamp != stamp, "Added objects invalidate work.");
        stamp = changes.Stamp; database.NotifyErased();
        check(changes.Stamp != stamp, "Deleted/unerased objects invalidate work.");
        stamp = changes.Stamp;
        var checkpoint = changes.IgnoreCheckpointWrites();
        using (changes.IgnoreCheckpointWrites()) { database.NotifyModified(); database.NotifyAppended(); database.NotifyErased(); }
        database.NotifyModified();
        check(changes.Stamp == stamp, "Nested checkpoint transactions never invalidate themselves.");
        checkpoint.Dispose(); checkpoint.Dispose();
        database.NotifyModified();
        check(changes.Stamp != stamp, "After idempotent scope disposal real source edits remain tracked.");
        check(AutoDesignDrawingChanges.For(new Database()).Stamp != changes.Stamp, "New drawing/session cannot bless stale persisted checkpoints.");

        string folder = Path.Combine(Path.GetTempPath(), "ce-autodesign-diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new AutoDesignDiagnostics();
            log.Start("test.dwg", folder);
            log.Write("STAGE before native rebuild");
            check(File.ReadAllText(log.LogPath).Contains("before native rebuild"), "Stage is on disk before the native operation starts.");
            var next = new AutoDesignDiagnostics(); next.Start("next.dwg", folder);
            check(File.ReadAllText(Path.Combine(folder, "AutoDesign-previous-run.log")).Contains("before native rebuild"), "Prior interrupted run is preserved before a new run starts.");
            check(File.ReadAllText(next.LogPath).Contains("next.dwg"), "Current run uses a separate fresh log.");
            string blocked = Path.Combine(folder, "not-a-directory"); File.WriteAllText(blocked, "test");
            var unavailable = new AutoDesignDiagnostics(); unavailable.Start("test.dwg", blocked); unavailable.Write("ignored");
            check(unavailable.LogPath == null, "Unavailable diagnostics cannot crash the drawing command.");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
