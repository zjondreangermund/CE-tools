"""Execute the production C# geometry methods with CAD-independent XY test doubles.
Run: python scripts/test_skew_junction_geometry.py /path/to/dotnet
"""
from pathlib import Path
import tempfile, subprocess, sys
root = Path(__file__).resolve().parents[1]
source = (root / 'src/CE.Tools.Civil3D/September16FieldCommentCompletionCommands.cs').read_text()
def method(name):
    import re
    match = re.search(r'        private static [^\n]+ '+name+r'\(', source)
    start = match.start()
    body = source.index('{', start)
    depth = 1; end = body + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}'); end += 1
    return source[start:end]
code = r'''
using System;
using System.Collections.Generic;
struct Vector3d {
 public double X,Y,Z; public Vector3d(double x,double y,double z){X=x;Y=y;Z=z;}
 public double Length => Math.Sqrt(X*X+Y*Y+Z*Z);
 public Vector3d GetNormal()=>this*(1/Length);
 public double DotProduct(Vector3d b)=>X*b.X+Y*b.Y+Z*b.Z;
 public Vector3d CrossProduct(Vector3d b)=>new Vector3d(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
 public static Vector3d operator *(Vector3d a,double b)=>new Vector3d(a.X*b,a.Y*b,a.Z*b);
 public static Vector3d operator -(Vector3d a)=>a*(-1);
 public static Vector3d operator +(Vector3d a,Vector3d b)=>new Vector3d(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
}
struct Point3d {
 public double X,Y,Z; public Point3d(double x,double y,double z){X=x;Y=y;Z=z;}
 public double DistanceTo(Point3d b)=>(this-b).Length;
 public static Point3d operator +(Point3d a,Vector3d b)=>new Point3d(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
 public static Vector3d operator -(Point3d a,Point3d b)=>new Vector3d(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
}
class Program {
 const double Tol=1e-8;
 static Vector3d Plan(Vector3d v)=>new Vector3d(v.X,v.Y,0);
 struct ReturnDefinition { public Point3d Centre,Start,Mid,End; public int MainSign,SideSign; }
 static void Near(double a,double b){if(Math.Abs(a-b)>1e-7)throw new Exception($"{a} != {b}");}
 static void Limit(Point3d a,Point3d b,Vector3d d,double width){
  var pairs=new List<Tuple<Point3d,Point3d,string>>();
  AddPerpendicularLimit(pairs,a,b,d,"LIMIT");
  var cap=pairs[pairs.Count-1];
  Near((cap.Item2-cap.Item1).DotProduct(d),0);
  Near(cap.Item1.DistanceTo(cap.Item2),width);
  foreach(var p in pairs){
   if(p.Item3.EndsWith("EDGE")){
    Near((p.Item2-p.Item1).CrossProduct(d).Length,0);
    if((p.Item2-p.Item1).DotProduct(d)<-1e-8)throw new Exception("Inward connector");
   }
  }
 }
 static void Main(){
  int count=0;
  foreach(double rotation in new[]{0.0,0.37,2.1})
  foreach(double angle in new[]{30.0,60.0,75.0,90.0,105.0,120.0,150.0,-60.0,-105.0}){
   var m=new Vector3d(Math.Cos(rotation),Math.Sin(rotation),0);
   double t=rotation+angle*Math.PI/180;
   var s=new Vector3d(Math.Cos(t),Math.Sin(t),0);
   var origin=new Point3d(12345,67890,0);
   var returns=new Dictionary<string,ReturnDefinition>();
   foreach(int ms in new[]{-1,1})foreach(int ss in new[]{-1,1}){
    var r=BuildReturn(origin,m,s,ms,ss,3.7,4.5,10);
    returns[ms+","+ss]=r;
    Near(r.Centre.DistanceTo(r.Start),10);Near(r.Centre.DistanceTo(r.End),10);Near(r.Centre.DistanceTo(r.Mid),10);
    Near((r.Start-r.Centre).DotProduct(s),0);Near((r.End-r.Centre).DotProduct(m),0);
    Near(Math.Abs((r.Start-origin).CrossProduct(s).Z),4.5);
    Near(Math.Abs((r.End-origin).CrossProduct(m).Z),3.7);
    count++;
   }
   foreach(int sign in new[]{-1,1}){
    Limit(returns[sign+",-1"].End,returns[sign+",1"].End,m*sign,7.4);
    Limit(returns["-1,"+sign].Start,returns["1,"+sign].Start,s*sign,9);
   }
   // T uses the same two returns on sideSign=+1: its front stays on the main edge.
   Near((returns["1,1"].End-returns["-1,1"].End).CrossProduct(m).Length,0);
  }
  Console.WriteLine($"PASS: {count} returns; 108 perpendicular arm limits; skew T/cross, rotations, reversed handedness, unequal widths.");
 }
'''
code += method('BuildReturn') + '\n' + method('AddPerpendicularLimit') + '\n}'
with tempfile.TemporaryDirectory() as tmp:
    p=Path(tmp)
    (p/'Test.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>')
    (p/'Program.cs').write_text(code)
    (p/'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>')
    subprocess.run([sys.argv[1] if len(sys.argv)>1 else 'dotnet','run','--project',str(p/'Test.csproj')],check=True)
