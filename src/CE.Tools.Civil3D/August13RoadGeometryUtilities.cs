using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
[assembly: CommandClass(typeof(CETools.Civil3D.August13RoadGeometryUtilities))]
namespace CETools.Civil3D
{
 public sealed class August13RoadGeometryUtilities
 {
  private const double Tol=1e-7;
  private const string JunctionLayer="CE-ROAD-JUNCTION";

  [CommandMethod("CE_TOOLS","CE_SELECTPOLYSHORTER",CommandFlags.Modal|CommandFlags.Redraw|CommandFlags.UsePickSet)]
  public void SelectShorter()
  {
   Document d=Active();if(d==null)return;
   PromptDoubleResult p=d.Editor.GetDouble(new PromptDoubleOptions("\nSelect polylines shorter than length: "){AllowNegative=false,AllowZero=false,AllowNone=false});
   if(p.Status!=PromptStatus.OK)return;
   List<ObjectId> ids=FindPolylines(d,delegate(Curve c){double len;return Length(c,out len)&&len<p.Value-Tol;});
   d.Editor.SetImpliedSelection(ids.ToArray());
   d.Editor.WriteMessage("\nCE_SELECTPOLYSHORTER: selected {0} polyline(s) shorter than {1:N3}.",ids.Count,p.Value);
  }

  [CommandMethod("CE_TOOLS","CE_SELECTPOLYSAMELENGTH",CommandFlags.Modal|CommandFlags.Redraw|CommandFlags.UsePickSet)]
  public void SelectSameLength()
  {
   Document d=Active();if(d==null)return;
   PromptEntityResult p=d.Editor.GetEntity(new PromptEntityOptions("\nSelect reference polyline: "));
   if(p.Status!=PromptStatus.OK)return;
   double reference;
   using(Transaction tr=d.Database.TransactionManager.StartTransaction())
   {
    Curve c=tr.GetObject(p.ObjectId,OpenMode.ForRead,false) as Curve;
    if(!IsPolyline(c)||!Length(c,out reference)){d.Editor.WriteMessage("\nSelect a 2D, 3D or lightweight polyline.");return;}
   }
   PromptDoubleResult t=d.Editor.GetDouble(new PromptDoubleOptions("\nLength tolerance <0.001>: "){DefaultValue=0.001,UseDefaultValue=true,AllowNegative=false,AllowZero=true,AllowNone=true});
   if(t.Status==PromptStatus.Cancel)return;
   double tolerance=t.Status==PromptStatus.OK?t.Value:0.001;
   List<ObjectId> ids=FindPolylines(d,delegate(Curve c){double len;return Length(c,out len)&&Math.Abs(len-reference)<=Math.Max(tolerance,Tol);});
   d.Editor.SetImpliedSelection(ids.ToArray());
   d.Editor.WriteMessage("\nCE_SELECTPOLYSAMELENGTH: reference={0:N3}; tolerance={1:N4}; selected={2}.",reference,tolerance,ids.Count);
  }

  [CommandMethod("CE_TOOLS","CE_SETTINGOUTSWAPXY",CommandFlags.Modal|CommandFlags.Redraw|CommandFlags.UsePickSet)]
  public void SwapXy()
  {
   Document d=Active();if(d==null)return;
   PromptSelectionResult s=d.Editor.SelectImplied();
   if(s.Status!=PromptStatus.OK||s.Value==null||s.Value.Count==0)
    s=d.Editor.GetSelection(new PromptSelectionOptions{MessageForAdding="\nSelect setting-out table(s) whose X and Y values must be exchanged: ",AllowDuplicates=false,RejectObjectsFromNonCurrentSpace=true});
   if(s.Status!=PromptStatus.OK||s.Value==null)return;
   int tables=0,rows=0;
   using(DocumentLock l=d.LockDocument())
   using(Transaction tr=d.Database.TransactionManager.StartTransaction())
   {
    foreach(ObjectId id in s.Value.GetObjectIds())
    {
     Table table;try{table=tr.GetObject(id,OpenMode.ForWrite,false) as Table;}catch{continue;}if(table==null)continue;
     int hr,xc,yc;if(!Columns(table,out hr,out xc,out yc))continue;
     int changed=0;
     for(int r=hr+1;r<table.Rows.Count;r++)
     {
      string x=Cell(table,r,xc),y=Cell(table,r,yc);if(string.IsNullOrWhiteSpace(x)&&string.IsNullOrWhiteSpace(y))continue;
      try{table.Cells[r,xc].TextString=y;table.Cells[r,yc].TextString=x;changed++;}catch{}
     }
     if(changed>0){tables++;rows+=changed;}
    }
    tr.Commit();
   }
   d.Editor.Regen();
   d.Editor.WriteMessage("\nCE_SETTINGOUTSWAPXY complete. Tables={0}; rows swapped={1}. Drawing/source geometry was not transformed.",tables,rows);
  }

  [CommandMethod("CE_TOOLS","CE_JUNCTIONSETTINGOUT4FIX",CommandFlags.Modal|CommandFlags.Redraw|CommandFlags.UsePickSet)]
  public void JunctionFourReturns()
  {
   Document d=Active();if(d==null)return;
   var m=new ProductionSettingsDialogModel("CE Tools - Ordered Junction Setting-Out (4 Returns)","Spatial grouping identifies each junction; a return-count cap prevents a nearby junction being chained into the same setting-out group.");
   m.AddChoice("Scope","01 Junctions","Junction geometry","All","Use all CE junction returns or selected curves.",new[]{"All","Selected"});
   m.AddPositiveDouble("Grouping","02 Order","Junction grouping distance",30.0,"Maximum spatial distance for curves in one junction. This is only a tolerance.");
   m.AddPositiveInteger("Returns","02 Order","Bellmouth/return curves per junction",4,"Keep this at 4 for the requested four-bellmouth junction workflow.");
   if(!DisciplineWorkflowDialogs.EditSettings(m))return;
   List<ObjectId> ids=JunctionCurves(d,m.Text("Scope"));if(ids.Count==0){d.Editor.WriteMessage("\nNo CE-ROAD-JUNCTION curves found.");return;}
   List<Centre> values=Centres(d.Database,ids);
   List<List<Centre>> groups=Group(values,Math.Max(0.1,m.Double("Grouping",30.0)),Math.Max(1,m.Integer("Returns",4)));
   ObjectId[] ordered=groups.OrderByDescending(g=>g.Average(v=>v.P.Y)).ThenBy(g=>g.Average(v=>v.P.X)).SelectMany(Order).Select(v=>v.Id).ToArray();
   d.Editor.SetImpliedSelection(ordered);
   d.Editor.WriteMessage("\nCE_JUNCTIONSETTINGOUT4FIX: junction groups={0}; ordered return curves={1}; max returns/junction={2}. The 30.0 default is the grouping tolerance, not the bellmouth count.",groups.Count,ordered.Length,Math.Max(1,m.Integer("Returns",4)));
   d.SendStringToExecute("CE_ROADJUNCTIONSETTINGOUT ",true,false,true);
  }

  private static Document Active(){return AcApplication.DocumentManager.MdiActiveDocument;}
  private static bool IsPolyline(Curve c){return c is Polyline||c is Polyline2d||c is Polyline3d;}
  private static bool Length(Curve c,out double len){len=0;if(c==null)return false;try{len=Math.Abs(c.GetDistanceAtParameter(c.EndParam)-c.GetDistanceAtParameter(c.StartParam));return !double.IsNaN(len)&&!double.IsInfinity(len);}catch{return false;}}
  private static List<ObjectId> FindPolylines(Document d,Func<Curve,bool> predicate)
  {
   var result=new List<ObjectId>();using(Transaction tr=d.Database.TransactionManager.StartTransaction())
   {BlockTableRecord space=tr.GetObject(d.Database.CurrentSpaceId,OpenMode.ForRead,false) as BlockTableRecord;if(space==null)return result;foreach(ObjectId id in space){Curve c;try{c=tr.GetObject(id,OpenMode.ForRead,false) as Curve;}catch{continue;}if(IsPolyline(c)){try{if(predicate(c))result.Add(id);}catch{}}}}
   return result;
  }
  private static bool Columns(Table t,out int hr,out int xc,out int yc)
  {
   hr=xc=yc=-1;for(int r=0;r<Math.Min(t.Rows.Count,8);r++){int x=-1,y=-1;for(int c=0;c<t.Columns.Count;c++){string s=(Cell(t,r,c)??"").Replace("\\P"," ").Trim().ToUpperInvariant();if(s=="X"||s=="EASTING"||s=="E")x=c;if(s=="Y"||s=="NORTHING"||s=="N")y=c;}if(x>=0&&y>=0&&x!=y){hr=r;xc=x;yc=y;return true;}}return false;
  }
  private static string Cell(Table t,int r,int c){try{return t.Cells[r,c].TextString??"";}catch{return "";}}
  private static List<ObjectId> JunctionCurves(Document d,string scope)
  {
   IEnumerable<ObjectId> source;
   if(string.Equals(scope,"Selected",StringComparison.OrdinalIgnoreCase))
   {PromptSelectionResult s=d.Editor.SelectImplied();if(s.Status!=PromptStatus.OK||s.Value==null||s.Value.Count==0)s=d.Editor.GetSelection(new PromptSelectionOptions{MessageForAdding="\nSelect junction return curves: ",AllowDuplicates=false,RejectObjectsFromNonCurrentSpace=true});if(s.Status!=PromptStatus.OK||s.Value==null)return new List<ObjectId>();source=s.Value.GetObjectIds();}
   else using(Transaction tr=d.Database.TransactionManager.StartTransaction()){BlockTableRecord space=tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(d.Database),OpenMode.ForRead,false) as BlockTableRecord;source=space==null?new ObjectId[0]:space.Cast<ObjectId>().ToArray();}
   var result=new List<ObjectId>();using(Transaction tr=d.Database.TransactionManager.StartTransaction()){foreach(ObjectId id in source){Curve c;try{c=tr.GetObject(id,OpenMode.ForRead,false) as Curve;}catch{continue;}if(c!=null&&string.Equals(c.Layer,JunctionLayer,StringComparison.OrdinalIgnoreCase))result.Add(id);}}return result;
  }
  private static List<Centre> Centres(Database db,IEnumerable<ObjectId> ids)
  {
   var result=new List<Centre>();using(Transaction tr=db.TransactionManager.StartTransaction())foreach(ObjectId id in ids){Curve c;try{c=tr.GetObject(id,OpenMode.ForRead,false) as Curve;}catch{continue;}if(c==null)continue;try{Extents3d e=c.GeometricExtents;result.Add(new Centre(id,new Point2d((e.MinPoint.X+e.MaxPoint.X)/2.0,(e.MinPoint.Y+e.MaxPoint.Y)/2.0)));}catch{}}
   return result;
  }
  private static List<List<Centre>> Group(List<Centre> input,double distance,int count)
  {
   var rem=input.OrderByDescending(v=>v.P.Y).ThenBy(v=>v.P.X).ToList();var groups=new List<List<Centre>>();
   while(rem.Count>0){Centre seed=rem[0];rem.RemoveAt(0);var g=new List<Centre>{seed};while(g.Count<count&&rem.Count>0){Point2d c=new Point2d(g.Average(v=>v.P.X),g.Average(v=>v.P.Y));Centre n=rem.OrderBy(v=>v.P.GetDistanceTo(c)).First();if(n.P.GetDistanceTo(c)>distance)break;g.Add(n);rem.Remove(n);}groups.Add(g);}return groups;
  }
  private static IEnumerable<Centre> Order(List<Centre> g){Point2d c=new Point2d(g.Average(v=>v.P.X),g.Average(v=>v.P.Y));return g.OrderBy(v=>Angle(Math.Atan2(v.P.Y-c.Y,v.P.X-c.X)));}
  private static double Angle(double a){while(a<0)a+=Math.PI*2;while(a>=Math.PI*2)a-=Math.PI*2;return a;}
  private sealed class Centre{internal Centre(ObjectId id,Point2d p){Id=id;P=p;}internal ObjectId Id{get;private set;}internal Point2d P{get;private set;}}
 }
}
