using System;
using CETools.Civil3D;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
using Civil = Autodesk.Civil.DatabaseServices;

// Executes the production helpers against deliberately detached band collections
// and a setter-only style Name override. These are API-contract regressions, not
// a substitute for compiling/running against Autodesk's native Civil 3D runtime.
internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }

    private static void Main()
    {
        var database = new Database();
        var styles = Autodesk.Civil.ApplicationServices.CivilDocument.GetCivilDocument(database).Styles.FeatureLineStyles;
        var basicId = styles.Add("Basic");
        var transaction = new Transaction();
        var basic = (FeatureLineStyle)transaction.GetObject(basicId, OpenMode.ForWrite, false);
        basic.Plan.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 5);
        basic.Model.Color = basic.Plan.Color;
        Check(!basic.GetType().GetProperty("Name").CanRead, "Fixture must reproduce Civil 3D's setter-only style Name.");
        Check(CivilStyleNames.Get(basic) == "Basic", "Inherited style name getter must work.");

        var line = new Civil.FeatureLine { StyleName = "Basic" };
        var yellowId = FeatureLineColourService.Prepare(database, line, 2, transaction);
        Check(FeatureLineColourService.Assign(line, yellowId, new Transaction()), "Assign the prepared style, not just entity colour.");
        Check(line.StyleName == "CE-FL-ACI-2-Basic", "The line must no longer use Basic.");
        int colour;
        Check(FeatureLineColourService.ReadDisplayColour(transaction.GetObject(yellowId, OpenMode.ForRead, false), out colour) && colour == 2,
            "Both plan and model components must use the requested colour.");
        Check(basic.Plan.Color.ColorIndex == 5 && basic.Model.Color.ColorIndex == 5, "Do not recolour the shared Basic style.");
        var cyanId = FeatureLineColourService.Prepare(database, line, 4, transaction);
        FeatureLineColourService.Assign(line, cyanId, transaction);
        var yellowAgain = FeatureLineColourService.Prepare(database, line, 2, transaction);
        Check(yellowAgain.Equals(yellowId), "Recolouring must reuse the original colour style instead of nesting names.");
        var styleless = new Civil.FeatureLine { StyleName = "" };
        Check(!FeatureLineColourService.Prepare(database, styleless, 1, transaction).IsNull, "A styleless line still needs a visible colour style.");
        bool badColour = false;
        try { FeatureLineColourService.Prepare(database, line, 256, transaction); }
        catch (ArgumentOutOfRangeException) { badColour = true; }
        Check(badColour, "Reject invalid explicit colour indices.");

        // Negative control: modifying a retrieved band collection must NOT save
        // anything. This is the behaviour the old implementation overlooked.
        var view = MakeView();
        using (var copy = view.Bands.GetBottomBandItems()) copy[0].ShowLabels = true;
        using (var saved = view.Bands.GetBottomBandItems())
            Check(!saved[0].ShowLabels, "Fixture must return detached band collections.");

        int found, enabled;
        ProfileViewBandPersistence.EnableLabels(view, out found, out enabled);
        Check(found == 3 && enabled == 3, "Persist both top and bottom labels and count fresh readback.");
        using (var saved = view.Bands.GetBottomBandItems())
        {
            Check(saved[0].Gap == 7 && saved[0].MajorInterval == 20, "Preserve layout gaps and intervals.");
            Check(saved[0].DataSourceId == 42 && saved[1].BandStyleName == "Chainages", "Preserve network binding, styles and row order.");
        }
        int sourceReadback = 0;
        ProfileViewBandPersistence.Update(view, item => item.Profile1Id = 123,
            item => { if (item.Profile1Id == 123) sourceReadback++; });
        Check(sourceReadback == 3, "Source IDs must survive collection write-back.");

        var ignoringView = MakeView();
        ignoringView.Bands.IgnoreWrites = true;
        ProfileViewBandPersistence.EnableLabels(ignoringView, out found, out enabled);
        Check(enabled == 0, "Never count unsaved wrapper changes as success.");

        var readOnlyView = MakeView();
        readOnlyView.IsWriteEnabled = false;
        bool refused = false;
        try { ProfileViewBandPersistence.EnableLabels(readOnlyView, out found, out enabled); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused && readOnlyView.Bands.Writes == 0, "Reject read-only owner before native collection writes.");

        var failingView = MakeView();
        failingView.Bands.FailWrites = true;
        bool failed = false;
        try { ProfileViewBandPersistence.EnableLabels(failingView, out found, out enabled); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed, "Native write errors must reach the command, not be swallowed.");

        int total = 0;
        for (int index = 0; index < 11; index++)
        {
            ProfileViewBandPersistence.EnableLabels(MakeView(), out found, out enabled);
            total += enabled;
        }
        Check(total == 33, "Batch views must not share or discard edited collections.");
        Console.WriteLine("Civil API contract regressions passed: " + checks + " checks.");
    }

    private static Civil.ProfileView MakeView()
    {
        var view = new Civil.ProfileView { IsWriteEnabled = true };
        view.Bands.Top.Add(new Civil.ProfileViewBandItem { BandStyleName = "Ground" });
        view.Bands.Bottom.Add(new Civil.ProfileViewBandItem { BandStyleName = "Invert", Gap = 7, MajorInterval = 20, DataSourceId = 42 });
        view.Bands.Bottom.Add(new Civil.ProfileViewBandItem { BandStyleName = "Chainages" });
        return view;
    }
}
