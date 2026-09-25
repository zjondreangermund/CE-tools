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
    new DisciplineWorkflowAction("2. Gutter Edge","CE_FLOFFSET","Select one or more source feature lines and create linked stepped gutter offsets.","02 Kerb and Gutter"),
    new DisciplineWorkflowAction("3. Bottom of Kerb","CE_FLOFFSET","Select one or more source feature lines and create bottom-of-kerb controls.","02 Kerb and Gutter"),
    new DisciplineWorkflowAction("4. Top of Kerb","CE_FLOFFSET","Select one or more source feature lines and create top-of-kerb controls.","02 Kerb and Gutter"),
    new DisciplineWorkflowAction("5. Sidewalk / Shoulder Edge","CE_FLOFFSET","Select one or more source feature lines and create outer sidewalk or shoulder controls.","03 Outside"),
    new DisciplineWorkflowAction("6. Daylight to Selected Surface","CE_PLATFORMDRAPE","Use the selected target surface for outer/daylight levels where applicable.","03 Outside"),
    new DisciplineWorkflowAction("7. Join / Close Stepped Strings","CE_FLSTEPJOIN","Join pieces, close gaps and add endpoint vertices.","04 Close and Infill"),
    new DisciplineWorkflowAction("8. Junction Surface / Infill","CE_SURFTOOLS","Create or review the dedicated junction surface and add closed controls.","04 Close and Infill"),
    new DisciplineWorkflowAction("9. Refresh Linked Model Data","CE_REFRESHALL","Refresh dependent model data after the fallback.","05 Refresh")
   });
  }
 }
}
