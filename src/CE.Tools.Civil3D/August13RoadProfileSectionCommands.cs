using System;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication=Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilAlignment=Autodesk.Civil.DatabaseServices.Alignment;
using CivilProfileView=Autodesk.Civil.DatabaseServices.ProfileView;
[assembly:CommandClass(typeof(CETools.Civil3D.August13RoadProfileSectionCommands))]
namespace CETools.Civil3D{
public sealed class August13RoadProfileSectionCommands{
[CommandMethod("CE_TOOLS","CE_ROADPROFILEVIEWSPLIT",CommandFlags.Modal|CommandFlags.Redraw)]
public void SplitProfileViews(){
Document d=AcApplication.DocumentManager.MdiActiveDocument;CivilDocument cd=CivilApplication.ActiveDocument;if(d==null||cd==null)return;
var o=new PromptEntityOptions("\nSelect an existing road profile view as template: ");o.AddAllowedClass(typeof(CivilProfileView),false);PromptEntityResult p=d.Editor.GetEntity(o);if(p.Status!=PromptStatus.OK)return;
ObjectId aid,style;double a0,a1;string name;using(Transaction tr=d.Database.TransactionManager.StartTransaction()){CivilProfileView t=tr.GetObject(p.ObjectId,OpenMode.ForRead,false) as CivilProfileView;if(t==null)return;aid=t.AlignmentId;style=t.StyleId;CivilAlignment a=tr.GetObject(aid,OpenMode.ForRead,false) as CivilAlignment;if(a==null)return;a0=a.StartingStation;a1=a.EndingStation;name=a.Name;}
var m=new ProductionSettingsDialogModel("CE Tools - Split Road Profile Views","Create consecutive profile views such as 0.000-750.000, 750.000-1500.000 and continue to alignment end.");m.AddDouble("Start","01 Stations","Start station",a0,"First station.");m.AddDouble("End","01 Stations","End station",a1,"Last station.");m.AddPositiveDouble("Length","01 Stations","Section length",750.0,"Length per view.");m.AddPositiveDouble("Spacing","02 Placement","Horizontal spacing",250.0,"Drawing-unit spacing.");if(!DisciplineWorkflowDialogs.EditSettings(m))return;
double start=Math.Max(a0,m.Double("Start",a0)),end=Math.Min(a1,m.Double("End",a1)),len=Math.Max(.001,m.Double("Length",750)),spacing=Math.Max(.001,m.Double("Spacing",250));if(end<=start)return;PromptPointResult ip=d.Editor.GetPoint("\nPick insertion point for first section profile view: ");if(ip.Status!=PromptStatus.OK)return;int made=0;
using(DocumentLock l=d.LockDocument())using(Transaction tr=d.Database.TransactionManager.StartTransaction()){int i=0;for(double s=start;s<end-.000001;s+=len){double e=Math.Min(s+len,end);ObjectId id=CivilProfileView.Create(aid,new Point3d(ip.Value.X+i*spacing,ip.Value.Y,ip.Value.Z));CivilProfileView v=tr.GetObject(id,OpenMode.ForWrite,false) as CivilProfileView;if(v==null)continue;v.StyleId=style;v.StationRangeMode=StationRangeType.UserSpecified;v.StationStart=s;v.StationEnd=e;try{v.Name=string.Format(CultureInfo.InvariantCulture,"{0}-STA-{1:0.000}-{2:0.000}",name,s,e);}catch{}made++;i++;}tr.Commit();}d.Editor.Regen();d.Editor.WriteMessage("\nCE_ROADPROFILEVIEWSPLIT complete. Section profile views={0}.",made);
}}}
