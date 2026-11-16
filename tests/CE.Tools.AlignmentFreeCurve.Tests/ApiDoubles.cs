// Contract doubles follow the documented managed API signatures. They do not
// reproduce Autodesk's native tangent solver; run the documented DWG checks too.
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
namespace Autodesk.AutoCAD.Geometry {
 public struct Point2d { public double X,Y; public Point2d(double x,double y){X=x;Y=y;} public double GetDistanceTo(Point2d b)=>(this-b).Length; public static Vector2d operator -(Point2d a,Point2d b)=>new Vector2d(a.X-b.X,a.Y-b.Y); }
 public struct Vector2d { public double X,Y; public Vector2d(double x,double y){X=x;Y=y;} public double Length=>Math.Sqrt(X*X+Y*Y); public Vector2d GetNormal()=>new Vector2d(X/Length,Y/Length); public double DotProduct(Vector2d b)=>X*b.X+Y*b.Y; }
}
namespace Autodesk.AutoCAD.Runtime {
 [Flags] public enum CommandFlags { Modal=1,UsePickSet=2,Redraw=4 }
 [AttributeUsage(AttributeTargets.Assembly,AllowMultiple=true)] public class CommandClassAttribute:Attribute { public CommandClassAttribute(Type t){} }
 public class CommandMethodAttribute:Attribute { public CommandMethodAttribute(string a,string b,CommandFlags f){} }
}
namespace Autodesk.AutoCAD.DatabaseServices {
 public struct ObjectId { public int Value; public ObjectId(int v){Value=v;} }
 public enum OpenMode { ForRead,ForWrite } public enum DxfCode { Start }
 public class TypedValue { public TypedValue(int c,object v){} }
 public class Database { public Dictionary<ObjectId,Autodesk.Civil.DatabaseServices.Alignment> Items=new(); public TransactionManager TransactionManager; public Database(){TransactionManager=new TransactionManager(this);} }
 public class TransactionManager { Database db; public int Commits,Rollbacks; public TransactionManager(Database d){db=d;} public Transaction StartTransaction()=>new Transaction(db,this); }
 public class Transaction:IDisposable {
  Database db; TransactionManager manager; bool committed; Dictionary<ObjectId,Autodesk.Civil.DatabaseServices.Alignment> saved;
  public Transaction(Database d,TransactionManager m){db=d;manager=m;saved=d.Items.ToDictionary(x=>x.Key,x=>x.Value.Clone());}
  public object GetObject(ObjectId id,OpenMode mode,bool erased)=>db.Items[id];
  public void Commit(){committed=true;manager.Commits++;}
  public void Dispose(){if(!committed){db.Items=saved;manager.Rollbacks++;}}
 }
}
namespace Autodesk.AutoCAD.EditorInput {
 public enum PromptStatus { OK,None,Cancel } public class PromptResult {public PromptStatus Status;public string StringResult;}
 public class PromptKeywordOptions {public PromptKeywordOptions(string s){} public List<string> Keywords=new();public bool AllowNone;}
 public class PromptSelectionOptions {public string MessageForAdding;}
 public class SelectionFilter {public SelectionFilter(TypedValue[] v){}}
 public class SelectionSet {public ObjectId[] Ids;public int Count=>Ids.Length;public ObjectId[] GetObjectIds()=>Ids;}
 public class PromptSelectionResult {public PromptStatus Status;public SelectionSet Value;}
 public class Editor {
  public Queue<PromptResult> Answers=new();public ObjectId[] Selected=Array.Empty<ObjectId>(); public List<string> Messages=new();
  public PromptResult GetKeywords(PromptKeywordOptions o)=>Answers.Dequeue();
  public PromptSelectionResult SelectImplied()=>new PromptSelectionResult{Status=PromptStatus.OK,Value=new SelectionSet{Ids=Selected}};
  public PromptSelectionResult GetSelection(PromptSelectionOptions o,SelectionFilter f)=>SelectImplied();
  public void SetImpliedSelection(ObjectId[] ids){} public void WriteMessage(string s,params object[] args)=>Messages.Add(string.Format(s,args)); public void Regen(){}
 }
}
namespace Autodesk.AutoCAD.ApplicationServices.Core {
 public static class Application {public static Manager DocumentManager=new();}
 public class Manager {public Document MdiActiveDocument;}
 public class Document {public Database Database=new(); public Autodesk.AutoCAD.EditorInput.Editor Editor=new();}
}
namespace Autodesk.Civil.ApplicationServices {
 public class CivilDocument {Database db;public CivilDocument(Database d){db=d;}public static CivilDocument GetCivilDocument(Database db)=>new CivilDocument(db);public IEnumerable<ObjectId> GetAlignmentIds()=>db.Items.Keys;}
}
namespace Autodesk.Civil.DatabaseServices {
 public enum AlignmentEntityConstraintType {Fixed,FloatOnPrev,FloatOnNext,Free}
 public enum CurveParamType {Radius} public enum CurveType {Compound,Reverse}
 public class AlignmentEntity {public int EntityId,EntityBefore,EntityAfter;public AlignmentEntityConstraintType Constraint1{get;internal set;} public AlignmentEntity Clone()=>(AlignmentEntity)MemberwiseClone();}
 public class AlignmentLine:AlignmentEntity {public Point2d StartPoint,EndPoint;}
 public class AlignmentArc:AlignmentEntity {public Point2d StartPoint,EndPoint,CenterPoint;public double Radius;public bool Clockwise,GreaterThan180;public double Length=>Radius*(GreaterThan180?4:Math.PI/2);}
 public class Alignment {public string Name="ROAD-1";public bool IsReferenceObject;public AlignmentEntityCollection Entities=new();public Alignment Clone(){var a=(Alignment)MemberwiseClone();a.Entities=Entities.Clone();return a;}}
 public class AlignmentEntityCollection:IEnumerable<AlignmentEntity> {
  public List<AlignmentEntity> Items=new();public string Failure;public int RemoveCalls,AddCalls;public int Count=>Items.Count;
  public AlignmentEntityCollection Clone(){var c=(AlignmentEntityCollection)MemberwiseClone();c.Items=Items.Select(x=>x.Clone()).ToList();return c;}
  public AlignmentEntity EntityAtId(int id)=>Items.Single(x=>x.EntityId==id);
  public void Remove(AlignmentEntity e){RemoveCalls++;Items.Remove(e);}
  public AlignmentArc AddFreeCurve(int before,int after,double radius,CurveParamType param,bool greater,CurveType type){
   AddCalls++;if(Failure=="solve")throw new InvalidOperationException("native solver rejected radius");
   var a=new AlignmentArc{EntityId=100,EntityBefore=before,EntityAfter=after,Constraint1=AlignmentEntityConstraintType.Free,Radius=radius,GreaterThan180=greater,StartPoint=new Point2d(0,0),EndPoint=new Point2d(10,10),CenterPoint=new Point2d(0,10)};
   if(Failure=="radius")a.Radius++;
   if(Failure=="turn")a.Clockwise=true;
   if(Failure=="tangent")a.CenterPoint=new Point2d(1,9);
   if(Failure=="gap")a.StartPoint=new Point2d(0.5,0);
   EntityAtId(before).EntityAfter=a.EntityId;EntityAtId(after).EntityBefore=a.EntityId;Items.Add(a);return a;
  }
  public IEnumerator<AlignmentEntity> GetEnumerator()=>Items.GetEnumerator();System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
 }
}
