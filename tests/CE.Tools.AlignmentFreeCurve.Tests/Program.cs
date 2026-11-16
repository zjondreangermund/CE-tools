using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using CETools.Civil3D;

static Alignment Setup(string failure=null) {
 var a=new Alignment();a.Entities.Failure=failure;
 a.Entities.Items.Add(new AlignmentLine{EntityId=1,EntityAfter=2,StartPoint=new Point2d(-20,0),EndPoint=new Point2d(0,0)});
 a.Entities.Items.Add(new AlignmentArc{EntityId=2,EntityBefore=1,EntityAfter=3,Radius=10,StartPoint=new Point2d(0,0),EndPoint=new Point2d(10,10),CenterPoint=new Point2d(0,10)});
 a.Entities.Items.Add(new AlignmentLine{EntityId=3,EntityBefore=2,StartPoint=new Point2d(10,10),EndPoint=new Point2d(10,30)});
 return a;
}
static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
static Document Run(string scope,string confirmation,string failure=null) {
 var d=new Document(); Application.DocumentManager.MdiActiveDocument=d;
 d.Database.Items[new ObjectId(1)]=Setup(failure);d.Database.Items[new ObjectId(2)]=Setup();
 d.Database.Items[new ObjectId(3)]=Setup();d.Database.Items[new ObjectId(3)].IsReferenceObject=true;
 d.Editor.Selected=new[]{new ObjectId(1)};
 d.Editor.Answers.Enqueue(new PromptResult{Status=PromptStatus.OK,StringResult=scope});
 d.Editor.Answers.Enqueue(new PromptResult{Status=PromptStatus.OK,StringResult=confirmation});
 new AlignmentFreeCurveCommands().ConvertCurves();return d;
}
var selected=Run("Selected","Yes");
Assert(selected.Database.Items[new ObjectId(1)].Entities.EntityAtId(100).Constraint1==AlignmentEntityConstraintType.Free,"selected curve converted");
Assert(selected.Database.Items[new ObjectId(2)].Entities.EntityAtId(2)!=null,"unselected alignment retained");
var all=Run("All","Yes");
Assert(all.Database.TransactionManager.Commits==2,"all editable alignments converted");
Assert(all.Database.Items[new ObjectId(3)].Entities.EntityAtId(2)!=null,"reference alignment retained");
var no=Run("All","No");Assert(no.Database.TransactionManager.Commits==0,"cancel must not modify");
foreach(string failure in new[]{"solve","radius","turn","tangent","gap"}){
 var d=Run("Selected","Yes",failure);var ents=d.Database.Items[new ObjectId(1)].Entities;
 Assert(d.Database.TransactionManager.Commits==0,"invalid geometry committed: "+failure);
 Assert(ents.Count==3 && ents.EntityAtId(2).EntityBefore==1 && ents.EntityAtId(1).EntityAfter==2,"rollback lost original geometry");
 Assert(d.Editor.Messages.Any(x=>x.Contains("failed/rolled back=1")),"failure not reported");
}
var free=Setup();var arc=(AlignmentArc)free.Entities.EntityAtId(2);arc.Constraint1=AlignmentEntityConstraintType.Free;
Assert(AlignmentFreeCurveCommands.Eligibility(free.Entities,arc)=="already Free","already free skipped");
arc.Constraint1=AlignmentEntityConstraintType.Fixed;free.Entities.EntityAtId(1).Constraint1=AlignmentEntityConstraintType.FloatOnPrev;
Assert(AlignmentFreeCurveCommands.Eligibility(free.Entities,arc)!=null,"dependent neighbour skipped");
free=Setup();arc=(AlignmentArc)free.Entities.EntityAtId(2);arc.EntityAfter=999;
Assert(AlignmentFreeCurveCommands.Eligibility(free.Entities,arc)!=null,"terminal curve skipped");
Console.WriteLine("PASS: selection/all, references, cancellation, successful conversion, five rollback failures, existing Free and invalid-neighbour guards. Native CAD solver requires DWG testing.");
