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
    /// to the selected surface, optionally assigns explicit rule-set styles, and
    /// applies the current Civil 3D rules before cover/slope/drop values are read.
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
                using (DocumentLock documentLock = document.LockDocument())
                using (Transaction transaction = database.TransactionManager.StartTransaction())
                {
                    CivilNetwork network = transaction.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null)
                        throw new InvalidOperationException("The selected object is not a Civil 3D gravity network.");

                    CivilSurface surface = transaction.GetObject(surfaceId, OpenMode.ForRead, false) as CivilSurface;
                    if (surface == null)
                        throw new InvalidOperationException("The selected reference object is not a Civil 3D surface.");

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

                            string ruleSet = ReadName(
                                transaction,
                                pipe.RuleSetStyleId,
                                "<No pipe rule set>");
                            string status;
                            try
                            {
                                bool applied = pipe.ApplyRules();
                                if (applied) status = "Applied";
                                else
                                {
                                    ruleFailureCount++;
                                    status = "Failed: Civil 3D returned false; review the rule DLL/Event Viewer";
                                }
                            }
                            catch (System.Exception exception)
                            {
                                ruleFailureCount++;
                                status = "Failed: " + OneLine(exception.Message);
                            }
                            reportRows.Add(new List<string>
                            {
                                "Pipe",
                                SafeName(pipe.Name, pipe.Handle.ToString()),
                                ruleSet,
                                surface.Name,
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
                                surface.Name,
                                "Failed before rule evaluation: " + OneLine(exception.Message)
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

                            string ruleSet = ReadName(
                                transaction,
                                structure.RuleSetStyleId,
                                "<No structure rule set>");
                            string status;
                            try
                            {
                                bool applied = structure.ApplyRules();
                                if (applied) status = "Applied";
                                else
                                {
                                    ruleFailureCount++;
                                    status = "Failed: Civil 3D returned false; review the rule DLL/Event Viewer";
                                }
                            }
                            catch (System.Exception exception)
                            {
                                ruleFailureCount++;
                                status = "Failed: " + OneLine(exception.Message);
                            }
                            reportRows.Add(new List<string>
                            {
                                "Structure",
                                SafeName(structure.Name, structure.Handle.ToString()),
                                ruleSet,
                                surface.Name,
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
                                surface.Name,
                                "Failed before rule evaluation: " + OneLine(exception.Message)
                            });
                        }
                    }

                    transaction.Commit();
                }

                return true;
            }
            catch (System.Exception exception)
            {
                pipeCount = 0;
                structureCount = 0;
                ruleFailureCount = 0;
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
