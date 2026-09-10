using System;
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
            pipeCount = 0;
            structureCount = 0;
            ruleFailureCount = 0;
            error = string.Empty;

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

                            try { pipe.ApplyRules(); }
                            catch { ruleFailureCount++; }
                        }
                        catch
                        {
                            ruleFailureCount++;
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

                            try { structure.ApplyRules(); }
                            catch { ruleFailureCount++; }
                        }
                        catch
                        {
                            ruleFailureCount++;
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
    }
}
