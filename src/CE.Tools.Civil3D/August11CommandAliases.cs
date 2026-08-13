using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.August11CommandAliases))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Compatibility aliases for workflow command names that were exposed during
    /// the August field-review cycle. Keep these aliases small and forward only to
    /// established CE Tools commands so old workflow buttons/scripts remain valid.
    /// </summary>
    public sealed class August11CommandAliases
    {
        [CommandMethod("CE_TOOLS", "CE_SURFTOOLS", CommandFlags.Modal)]
        public void SurfaceToolsAlias()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            document.SendStringToExecute("CE_SFTOOLS ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_BULKWATERTOOLS", CommandFlags.Modal)]
        public void BulkWaterToolsAlias()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            document.SendStringToExecute("CE_BULKWATERPRODUCTIONCENTRE ", true, false, true);
        }

        [CommandMethod("CE_TOOLS", "CE_FLOFFSET", CommandFlags.Modal)]
        public void FeatureLineOffsetAlias()
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            document.SendStringToExecute("CE_FLRELCREATE ", true, false, true);
        }
    }
}
