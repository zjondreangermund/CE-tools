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
    /// Civil 3D 2023 compatibility bridge used by the sewer engineering audit.
    /// Reference surface/rule-set assignments are committed first. Rules are then
    /// evaluated in isolated post-commit transactions so Autodesk's external pipe
    /// rule scripts see committed object references and one bad part cannot poison
    /// the rest of the network update.
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

            try
            {
                Database database = document.Database;
                var pipesToApply = new List<ObjectId>();
                var structuresToApply = new List<ObjectId>();
                string surfaceName = "<Surface>";

                using (DocumentLock documentLock = document.LockDocument())
                {
                    // Phase 1: persist all reference-surface and rule-set assignments.
                    // Civil 3D's CoverAndSlope .NET rule DLL can fail when ApplyRules
                    // is invoked before those assignments have been committed.
                    using (Transaction transaction = database.TransactionManager.StartTransaction())
                    {
                        CivilNetwork network = transaction.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                        if (network == null)
                            throw new InvalidOperationException("The selected object is not a Civil 3D gravity network.");

                        CivilSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                        if (surface == null)
                            throw new InvalidOperationException("The selected reference object is not a Civil 3D surface.");
                        surfaceName = SafeName(surface.Name, "<Surface>");

                        foreach (ObjectId pipeId in network.GetPipeIds())
                        {
                            try
                            {
                                CivilPipe pipe = transaction.GetObject(pipeId, OpenMode.ForWrite, false) as CivilPipe;
                                if (pipe == null || pipe.IsReferenceObject) continue;
                                pipe.RefSurfaceId = surface.Id;
                                if (!pipeRuleSetId.IsNull)
                                    pipe.RuleSetStyleId = pipeRuleSetId;
                                pipesToApply.Add(pipeId);
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
                                structuresToApply.Add(structureId);
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

                    pipeCount = pipesToApply.Count;
                    structureCount = structuresToApply.Count;

                    // Phase 2: apply rules only after Phase 1 has committed.  Each
                    // part gets its own transaction so a failing Autodesk rule DLL
                    // is rolled back for that part without losing successful parts.
                    foreach (ObjectId pipeId in pipesToApply)
                    {
                        ApplyPipeRules(
                            database,
                            pipeId,
                            surfaceName,
                            reportRows,
                            ref ruleFailureCount);
                    }

                    foreach (ObjectId structureId in structuresToApply)
                    {
                        ApplyStructureRules(
                            database,
                            structureId,
                            surfaceName,
                            reportRows,
                            ref ruleFailureCount);
                    }
                }

                return true;
            }
            catch (System.Exception exception)
            {
                pipeCount = 0;
                structureCount = 0;
                error = exception.Message;
                return false;
            }
        }

        private static void ApplyPipeRules(
            Database database,
            ObjectId pipeId,
            string surfaceName,
            List<IList<string>> reportRows,
            ref int ruleFailureCount)
        {
            string partName = pipeId.Handle.ToString();
            string ruleSet = "<No pipe rule set>";
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilPipe pipe = transaction.GetObject(pipeId, OpenMode.ForWrite, false) as CivilPipe;
                    if (pipe == null)
                        throw new InvalidOperationException("Pipe is no longer available.");

                    partName = SafeName(pipe.Name, partName);
                    ruleSet = ReadName(transaction, pipe.RuleSetStyleId, ruleSet);
                    bool applied = pipe.ApplyRules();
                    if (!applied)
                    {
                        ruleFailureCount++;
                        reportRows.Add(new List<string>
                        {
                            "Pipe",
                            partName,
                            ruleSet,
                            surfaceName,
                            "Failed: Civil 3D returned false; review the rule DLL/Event Viewer"
                        });
                        return;
                    }

                    transaction.Commit();
                    reportRows.Add(new List<string>
                    {
                        "Pipe",
                        partName,
                        ruleSet,
                        surfaceName,
                        "Applied after committed surface/rule-set assignment"
                    });
                }
            }
            catch (System.Exception exception)
            {
                ruleFailureCount++;
                reportRows.Add(new List<string>
                {
                    "Pipe",
                    partName,
                    ruleSet,
                    surfaceName,
                    "Failed during post-commit rule evaluation: " + OneLine(exception.Message)
                });
            }
        }

        private static void ApplyStructureRules(
            Database database,
            ObjectId structureId,
            string surfaceName,
            List<IList<string>> reportRows,
            ref int ruleFailureCount)
        {
            string partName = structureId.Handle.ToString();
            string ruleSet = "<No structure rule set>";
            try
            {
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilStructure structure = transaction.GetObject(structureId, OpenMode.ForWrite, false) as CivilStructure;
                    if (structure == null)
                        throw new InvalidOperationException("Structure is no longer available.");

                    partName = SafeName(structure.Name, partName);
                    ruleSet = ReadName(transaction, structure.RuleSetStyleId, ruleSet);
                    bool applied = structure.ApplyRules();
                    if (!applied)
                    {
                        ruleFailureCount++;
                        reportRows.Add(new List<string>
                        {
                            "Structure",
                            partName,
                            ruleSet,
                            surfaceName,
                            "Failed: Civil 3D returned false; review the rule DLL/Event Viewer"
                        });
                        return;
                    }

                    transaction.Commit();
                    reportRows.Add(new List<string>
                    {
                        "Structure",
                        partName,
                        ruleSet,
                        surfaceName,
                        "Applied after committed surface/rule-set assignment"
                    });
                }
            }
            catch (System.Exception exception)
            {
                ruleFailureCount++;
                reportRows.Add(new List<string>
                {
                    "Structure",
                    partName,
                    ruleSet,
                    surfaceName,
                    "Failed during post-commit rule evaluation: " + OneLine(exception.Message)
                });
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
