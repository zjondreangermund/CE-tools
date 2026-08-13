using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using CivilAlignment = Autodesk.Civil.DatabaseServices.Alignment;

namespace CETools.Civil3D
{
    internal static class August13RoadProfileSplitEngine
    {
        private const double Epsilon = 1e-9;

        internal static void Execute(Document document, CivilDocument civilDocument, IList<ObjectId> selectedIds, double firstRequested, double lastRequested, double interval, double spacing, bool horizontal, bool finalize)
        {
            int sourceCount = 0;
            int sectionCount = 0;
            int newCount = 0;
            var warnings = new List<string>();
            try
            {
                using (Transaction transaction = document.Database.TransactionManager.StartTransaction())
                {
                    ObjectId viewStyleId;
                    ObjectId bandSetId;
                    August13RoadProfileSplitSupport.ResolveRoadStyles(document.Database, civilDocument, transaction, out viewStyleId, out bandSetId);
                    HashSet<string> reserved = August13RoadProfileSplitSupport.ReadNames(document.Database, transaction);

                    foreach (ObjectId id in selectedIds)
                    {
                        ProfileView source = transaction.GetObject(id, OpenMode.ForWrite, false) as ProfileView;
                        if (source == null) continue;
                        ObjectId alignmentId = August13RoadProfileSplitSupport.ReadObjectId(source, "AlignmentId");
                        CivilAlignment alignment = alignmentId.IsNull ? null : transaction.GetObject(alignmentId, OpenMode.ForRead, false) as CivilAlignment;
                        if (alignment == null)
                        {
                            warnings.Add("Profile view " + source.Handle + ": alignment could not be resolved.");
                            continue;
                        }

                        double alignmentStart = August13RoadProfileSplitSupport.ReadDouble(alignment, 0.0, "StartingStation", "StartStation");
                        double alignmentEnd = August13RoadProfileSplitSupport.ReadDouble(alignment, alignmentStart, "EndingStation", "EndStation");
                        double viewStart = August13RoadProfileSplitSupport.ReadDouble(source, alignmentStart, "StationStart");
                        double viewEnd = August13RoadProfileSplitSupport.ReadDouble(source, alignmentEnd, "StationEnd");
                        if (viewEnd <= viewStart + Epsilon) { viewStart = alignmentStart; viewEnd = alignmentEnd; }

                        double first = Math.Max(Math.Max(viewStart, alignmentStart), firstRequested);
                        double availableEnd = Math.Min(viewEnd, alignmentEnd);
                        double last = lastRequested > first + Epsilon ? Math.Min(lastRequested, availableEnd) : availableEnd;
                        if (last <= first + Epsilon)
                        {
                            warnings.Add((August13RoadProfileSplitSupport.ReadString(source, "Name") ?? source.Handle.ToString()) + ": no valid split range remains.");
                            continue;
                        }

                        List<August13ProfileSection> sections = August13RoadProfileSplitSupport.BuildSections(first, last, interval);
                        if (sections.Count == 0) continue;
                        Point3d basePoint = August13RoadProfileSplitSupport.ReadLocation(source);
                        string sourceName = August13RoadProfileSplitSupport.ReadString(source, "Name");
                        if (string.IsNullOrWhiteSpace(sourceName)) sourceName = "Road Profile View";

                        August13RoadProfileSplitSupport.SetRange(source, sections[0]);
                        August13RoadProfileSplitSupport.ApplyStyles(source, viewStyleId, bandSetId);
                        sourceCount++;
                        sectionCount++;

                        for (int index = 1; index < sections.Count; index++)
                        {
                            August13ProfileSection section = sections[index];
                            Point3d insertion = horizontal
                                ? basePoint + new Vector3d(spacing * index, 0.0, 0.0)
                                : basePoint + new Vector3d(0.0, -spacing * index, 0.0);
                            string requestedName = string.Format(CultureInfo.InvariantCulture, "{0} | {1:0.000}-{2:0.000}", sourceName, section.Start, section.End);
                            string name = August13RoadProfileSplitSupport.UniqueName(requestedName, reserved);
                            ObjectId createdId = August13RoadProfileSplitSupport.CreateProfileView(name, alignmentId, insertion, bandSetId, viewStyleId);
                            ProfileView created = transaction.GetObject(createdId, OpenMode.ForWrite, false) as ProfileView;
                            if (created == null) throw new InvalidOperationException("Civil 3D did not return the created split profile view.");
                            August13RoadProfileSplitSupport.SetRange(created, section);
                            August13RoadProfileSplitSupport.ApplyStyles(created, viewStyleId, bandSetId);
                            newCount++;
                            sectionCount++;
                        }
                    }
                    transaction.Commit();
                }

                document.Editor.Regen();
                document.Editor.WriteMessage("\nCE_ROADPROFILEVIEWSPLIT complete. Source views={0}; total sections={1}; new views={2}; section length={3:N3}.", sourceCount, sectionCount, newCount, interval);
                foreach (string warning in warnings.Take(8)) document.Editor.WriteMessage("\n  Warning: {0}", warning);
                if (finalize && sectionCount > 0) document.SendStringToExecute("CE_ROADPROFILEVIEWFINAL ", true, false, true);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nCE_ROADPROFILEVIEWSPLIT failed. No partial transaction was committed. {0}", exception.Message);
            }
        }
    }
}
