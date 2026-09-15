using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.DatabaseServices;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace CETools.Civil3D
{
    /// <summary>
    /// Small Civil 3D 2023 compatibility bridge used by the September 10 sewer
    /// engineering audit. It links every editable gravity-network pipe/structure
    /// to the selected surface, optionally assigns explicit rule-set styles, then
    /// applies the current Civil 3D rules in a second transaction after those
    /// assignments have been committed. The two-phase handoff is important in
    /// Civil 3D 2023 because the native PipeNetworkRule macros can fail when a
    /// rule is invoked before its reference surface/rule-set write is committed.
    /// </summary>
    internal static class September10SewerAuditRuntime
    {
        internal static bool LinkExistingPartsToSurface(
            Document document,
            ObjectId networkId,
            ObjectId surfaceId,
            ObjectId pipeRuleSetId,
            ObjectId structureRuleSetId,
            out int pipeCount,
            out int structureCount,
            out int ruleFailureCount,
            out string error)
        {
            List<IList<string>> ignored;
            return LinkExistingPartsToSurface(
                document,
                networkId,
                surfaceId,
                pipeRuleSetId,
                structureRuleSetId,
                out pipeCount,
                out structureCount,
                out ruleFailureCount,
                out error,
                out ignored);
        }

        internal static bool LinkExistingPartsToSurface(
            Document document,
            ObjectId networkId,
            ObjectId surfaceId,
            ObjectId pipeRuleSetId,
            ObjectId structureRuleSetId,
            out int pipeCount,
            out int structureCount,
            out int ruleFailureCount,
            out string error,
            out List<IList<string>> reportRows)
        {
            pipeCount = 0;
            structureCount = 0;
            ruleFailureCount = 0;
            error = string.Empty;
            reportRows = new List<IList<string>>();

            if (document == null)
            {
                error = "No active Civil 3D document.";
                return false;
            }
            if (networkId.IsNull)
            {
                error = "No sewer network was selected.";
                return false;
            }
            if (surfaceId.IsNull)
            {
                error = "No reference surface was selected.";
                return false;
            }

            var preparedPipes = new HashSet<ObjectId>();
            var preparedStructures = new HashSet<ObjectId>();
            string surfaceName = "<Surface>";

            try
            {
                Database database = document.Database;
                using (DocumentLock documentLock = document.LockDocument())
                {
                    // Phase 1: assign the reference surface and rule-set styles only.
                    // Do not enter the native rule DLL while these values are still
                    // uncommitted; Civil 3D 2023 can otherwise report
                    // C3DPipeNetworkRules.dll::ApplyRule macro failures.
                    using (Transaction transaction = database.TransactionManager.StartTransaction())
                    {
                        CivilNetwork network = transaction.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                        if (network == null)
                            throw new InvalidOperationException("The selected object is not a Civil 3D gravity network.");

                        CivilSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                        if (surface == null)
                            throw new InvalidOperationException("The selected reference object is not a Civil 3D surface.");
                        surfaceName = SafeName(surface.Name, surface.Handle.ToString());

                        foreach (ObjectId pipeId in network.GetPipeIds())
                        {
                            try
                            {
                                CivilPipe pipe = transaction.GetObject(pipeId, OpenMode.ForWrite, false) as CivilPipe;
                                if (pipe == null || pipe.IsReferenceObject) continue;

                                pipe.RefSurfaceId = surface.Id;
                                if (!pipeRuleSetId.IsNull)
                                    pipe.RuleSetStyleId = pipeRuleSetId;
                                pipeCount++;
                                preparedPipes.Add(pipeId);
                            }
                            catch (System.Exception exception)
                            {
                                ruleFailureCount++;
                                reportRows.Add(new List<string>
                                {
                                    "Pipe",
                                    pipeId.Handle.ToString(),
                                    "-",
                                    surfaceName,
                                    "Failed while assigning surface/rule set: " + OneLine(exception.Message)
                                });
                            }
                        }

                        foreach (ObjectId structureId in network.GetStructureIds())
                        {
                            try
                            {
                                CivilStructure structure = transaction.GetObject(structureId, OpenMode.ForWrite, false) as CivilStructure;
                                if (structure == null || structure.IsReferenceObject) continue;

                                structure.RefSurfaceId = surface.Id;
                                if (!structureRuleSetId.IsNull)
                                    structure.RuleSetStyleId = structureRuleSetId;
                                structureCount++;
                                preparedStructures.Add(structureId);
                            }
                            catch (System.Exception exception)
                            {
                                ruleFailureCount++;
                                reportRows.Add(new List<string>
                                {
                                    "Structure",
                                    structureId.Handle.ToString(),
                                    "-",
                                    surfaceName,
                                    "Failed while assigning surface/rule set: " + OneLine(exception.Message)
                                });
                            }
                        }

                        transaction.Commit();
                    }

                    // Force the host to see the committed part assignments before
                    // entering the native rule engine. Regen failure is non-fatal;
                    // the second transaction still gives Civil 3D a clean database
                    // boundary compared with the previous single-transaction path.
                    try { document.Editor.Regen(); } catch { }

                    // Phase 2: reopen only the parts that were successfully prepared
                    // and apply their native Civil 3D rules. Rule failures are
                    // reported per part without rolling back the committed surface
                    // and rule-set assignments.
                    try
                    {
                        using (Transaction transaction = database.TransactionManager.StartTransaction())
                        {
                            foreach (ObjectId pipeId in preparedPipes)
                            {
                                try
                                {
                                    CivilPipe pipe = transaction.GetObject(pipeId, OpenMode.ForWrite, false) as CivilPipe;
                                    if (pipe == null || pipe.IsReferenceObject) continue;

                                    string ruleSet = ReadName(
                                        transaction,
                                        pipe.RuleSetStyleId,
                                        "<No pipe rule set>");
                                    string status;
                                    try
                                    {
                                        bool applied = pipe.ApplyRules();
                                        if (applied)
                                        {
                                            status = "Applied after committed surface/rule-set assignment";
                                        }
                                        else
                                        {
                                            ruleFailureCount++;
                                            status = "Failed: Civil 3D returned false after committed assignment; review the rule DLL/Event Viewer";
                                        }
                                    }
                                    catch (System.Exception exception)
                                    {
                                        ruleFailureCount++;
                                        status = "Failed after committed assignment: " + OneLine(exception.Message);
                                    }
                                    reportRows.Add(new List<string>
                                    {
                                        "Pipe",
                                        SafeName(pipe.Name, pipe.Handle.ToString()),
                                        ruleSet,
                                        surfaceName,
                                        status
                                    });
                                }
                                catch (System.Exception exception)
                                {
                                    ruleFailureCount++;
                                    reportRows.Add(new List<string>
                                    {
                                        "Pipe",
                                        pipeId.Handle.ToString(),
                                        "-",
                                        surfaceName,
                                        "Failed while reopening prepared part: " + OneLine(exception.Message)
                                    });
                                }
                            }

                            foreach (ObjectId structureId in preparedStructures)
                            {
                                try
                                {
                                    CivilStructure structure = transaction.GetObject(structureId, OpenMode.ForWrite, false) as CivilStructure;
                                    if (structure == null || structure.IsReferenceObject) continue;

                                    string ruleSet = ReadName(
                                        transaction,
                                        structure.RuleSetStyleId,
                                        "<No structure rule set>");
                                    string status;
                                    try
                                    {
                                        bool applied = structure.ApplyRules();
                                        if (applied)
                                        {
                                            status = "Applied after committed surface/rule-set assignment";
                                        }
                                        else
                                        {
                                            ruleFailureCount++;
                                            status = "Failed: Civil 3D returned false after committed assignment; review the rule DLL/Event Viewer";
                                        }
                                    }
                                    catch (System.Exception exception)
                                    {
                                        ruleFailureCount++;
                                        status = "Failed after committed assignment: " + OneLine(exception.Message);
                                    }
                                    reportRows.Add(new List<string>
                                    {
                                        "Structure",
                                        SafeName(structure.Name, structure.Handle.ToString()),
                                        ruleSet,
                                        surfaceName,
                                        status
                                    });
                                }
                                catch (System.Exception exception)
                                {
                                    ruleFailureCount++;
                                    reportRows.Add(new List<string>
                                    {
                                        "Structure",
                                        structureId.Handle.ToString(),
                                        "-",
                                        surfaceName,
                                        "Failed while reopening prepared part: " + OneLine(exception.Message)
                                    });
                                }
                            }

                            transaction.Commit();
                        }
                    }
                    catch (System.Exception exception)
                    {
                        // Phase 1 is intentionally already committed. Keep those
                        // valid assignments and report the native rule phase failure
                        // rather than claiming that the entire operation rolled back.
                        ruleFailureCount++;
                        reportRows.Add(new List<string>
                        {
                            "Network",
                            networkId.Handle.ToString(),
                            "-",
                            surfaceName,
                            "Rule phase failed after surface/rule-set commit: " + OneLine(exception.Message)
                        });
                    }
                }

                return true;
            }
            catch (System.Exception exception)
            {
                // False is reserved for failure before the preparation transaction
                // commits, so callers may still truthfully state that no partial
                // surface/rule assignment was committed on this path.
                pipeCount = 0;
                structureCount = 0;
                ruleFailureCount = 0;
                reportRows.Clear();
                error = exception.Message;
                return false;
            }
        }

        private static string ReadName(
            Transaction transaction,
            ObjectId id,
            string fallback)
        {
            if (transaction == null || id.IsNull || !id.IsValid || id.IsErased)
                return fallback;
            try
            {
                DBObject value = transaction.GetObject(id, OpenMode.ForRead, false);
                PropertyInfo property = value == null
                    ? null
                    : value.GetType().GetProperty(
                        "Name",
                        BindingFlags.Instance | BindingFlags.Public);
                string name = property == null || !property.CanRead
                    ? string.Empty
                    : Convert.ToString(
                        property.GetValue(value, null),
                        CultureInfo.CurrentCulture);
                return string.IsNullOrWhiteSpace(name) ? fallback : name;
            }
            catch
            {
                return fallback;
            }
        }

        private static string SafeName(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string OneLine(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "Unknown Civil 3D rule error"
                : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }
}
