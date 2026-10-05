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
    new DisciplineWorkflowAction("2. Gutter Edge","CE_FLRELCREATEBATCH","Batch: select multiple source feature lines together, then apply one horizontal offset plus Elevation difference, Grade (%) or Slope (H:V) rule to every selected junction string.","02 Kerb and Gutter"),
    new DisciplineWorkflowAction("3. Bottom of Kerb","CE_FLRELCREATEBATCH","Batch: select multiple source feature lines together and apply one stepped bottom-of-kerb rule to each source. Vertical control can be elevation difference, Grade (%) or Slope (H:V).","02 Kerb and Gutter"),
    new DisciplineWorkflowAction("4. Top of Kerb","CE_FLRELCREATEBATCH","Batch: select multiple source feature lines together and apply one stepped top-of-kerb rule to each source. Vertical control can be elevation difference, Grade (%) or Slope (H:V).","02 Kerb and Gutter"),
    new DisciplineWorkflowAction("5. Sidewalk / Shoulder Edge","CE_FLRELCREATEBATCH","Batch: select multiple source feature lines together and apply one stepped sidewalk/shoulder rule to each source, with selectable offset side and elevation/grade/H:V slope control.","03 Outside"),
    new DisciplineWorkflowAction("6. Junction Bellmouths - Grade to Surface","CE_JUNCTIONGRADETOSURFACE","Grade all matching or multiple selected sidewalk/shoulder bellmouth edge feature lines to one surface, with saved cut/fill and presentation settings.","03 Outside"),
    new DisciplineWorkflowAction("7. Grading & Slopes","CE_GRADINGSLOPETOOLS","Open the wider grading, cut/fill daylight, constant-grade and slope/crossfall toolbox after the bellmouth grading is complete.","03 Outside"),
    new DisciplineWorkflowAction("8. Join / Close Stepped Strings","CE_FLSTEPJOIN","Join pieces, close gaps and add endpoint vertices.","04 Close and Infill"),
    new DisciplineWorkflowAction("9. Junction Closed Feature-Line Infill","CE_JUNCTIONINFILL","Create native infill for all recognised closed junction feature lines or multiple selected closed junction feature lines. CE Tools keeps the source and grading group in the same Site.","04 Close and Infill"),
    new DisciplineWorkflowAction("10. Refresh Linked Model Data","CE_REFRESHALL","Refresh dependent model data after the fallback.","05 Refresh")
   });
  }
 }
}
