using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
[assembly: CommandClass(typeof(CETools.Civil3D.August13JunctionFallbackCommands))]
namespace CETools.Civil3D
{
 public sealed class August13JunctionFallbackCommands
 {
  [CommandMethod("CE_TOOLS","CE_JUNCTIONSTEPPEDOFFSETWORKFLOW",CommandFlags.Modal)]
  public void Run()
  {
   Document d=AcApplication.DocumentManager.MdiActiveDocument;if(d==null)return;
   DisciplineWorkflowDialogs.SelectAndRun(d,"CE Tools - Junction Stepped Offset Fallback","Use this when a corridor junction cannot be completed reliably. Keep the junction controls on a dedicated site/surface where available.",new List<DisciplineWorkflowAction>
   {
    new DisciplineWorkflowAction("1. Bellmouths to Feature Lines","CE_FLCREATE","Create feature lines from the bellmouth control strings.","01 Controls"),
    new DisciplineWorkflowAction("2. Gutter Edge","CE_FLRELCREATE","Batch: select multiple source feature lines in one selection set and create the same linked stepped gutter offset for every selected junction string.","02 Kerb and Gutter"),
    new DisciplineWorkflowAction("3. Bottom of Kerb","CE_FLRELCREATE","Batch: select multiple source feature lines and create the same bottom-of-kerb stepped control for every selected junction string.","02 Kerb and Gutter"),
    new DisciplineWorkflowAction("4. Top of Kerb","CE_FLRELCREATE","Batch: select multiple source feature lines and create the same top-of-kerb stepped control for every selected junction string.","02 Kerb and Gutter"),
    new DisciplineWorkflowAction("5. Sidewalk / Shoulder Edge","CE_FLRELCREATE","Batch: select multiple source feature lines and create the same sidewalk/shoulder stepped control for every selected junction string.","03 Outside"),
    new DisciplineWorkflowAction("6. Junction Bellmouths - Grade to Surface","CE_JUNCTIONGRADETOSURFACE","Batch-grade all selected junction feature lines to one surface. Specify Cut and Fill slopes/grades and optionally draw persistent cut/fill slope projection lines from every source vertex to its daylight point.","03 Outside"),
    new DisciplineWorkflowAction("7. Grading & Slopes","CE_GRADINGSLOPETOOLS","Open the wider grading, cut/fill daylight, constant-grade and slope/crossfall toolbox after the bellmouth grading is complete.","03 Outside"),
    new DisciplineWorkflowAction("8. Join / Close Stepped Strings","CE_FLSTEPJOIN","Join pieces, close gaps and add endpoint vertices.","04 Close and Infill"),
    new DisciplineWorkflowAction("9. Junction Surface / Infill","CE_SURFTOOLS","Create or review the dedicated junction surface and add closed controls.","04 Close and Infill"),
    new DisciplineWorkflowAction("10. Refresh Linked Model Data","CE_REFRESHALL","Refresh dependent model data after the fallback.","05 Refresh")
   });
  }
 }
}
