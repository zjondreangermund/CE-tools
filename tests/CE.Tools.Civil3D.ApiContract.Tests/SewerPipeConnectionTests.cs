using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CETools.Civil3D;
using Civil = Autodesk.Civil.DatabaseServices;

internal static class SewerPipeConnectionTests
{
    internal static void Run(Action<bool, string> check)
    {
        var junction = new Civil.Structure { Name = "MH1.2" };
        var upstream = new Civil.Structure { Name = "MH9.1" };
        var reverse = new Civil.Pipe
        {
            Name = "P9.1", StartStructureId = new ObjectId { Value = junction },
            EndStructureId = new ObjectId { Value = upstream },
            StartPoint = new Point3d(90), EndPoint = new Point3d(100),
            InnerHeight = .4, InnerDiameterOrWidth = 2, OuterHeight = .5,
            FlowDirectionMethod = Civil.FlowDirectionMethodType.StartToEnd
        };
        var forward = new Civil.Pipe { Name = "legacy", StartPoint = new Point3d(110), EndPoint = new Point3d(109), InnerHeight = 1 };
        var flat = new Civil.Pipe { Name = "unknown", StartPoint = new Point3d(100), EndPoint = new Point3d(100), InnerHeight = .6 };
        foreach (var pipe in new[] { reverse, forward, flat }) junction.Pipes.Add(new ObjectId { Value = pipe });
        check(SewerPipeConnections.PipeIds(junction).SequenceEqual(junction.Pipes), "Enumerate every indexed connected pipe.");
        check(SewerPipeConnections.PipeIds(new Civil.Structure()).Count == 0, "An isolated manhole has no fictitious inlet.");
        bool direction;
        var tr = new Transaction();
        check(SewerPipeConnections.TryForward(reverse, tr, out direction) && !direction,
            "CE sequence must recognize an incoming pipe whose start/end coordinates are reversed.");
        check(Math.Abs(SewerPipeConnections.Invert(reverse, true) - 89.8) < 1e-9,
            "Inside invert uses vertical inner height, not outer height or horizontal width.");
        check(SewerPipeConnections.TryForward(forward, tr, out direction) && direction, "Legacy sloped pipe direction can come from geometry.");
        check(!SewerPipeConnections.TryForward(flat, tr, out direction), "Flat pipes with no sequence/direction must remain unresolved.");
        flat.FlowDirectionMethod = Civil.FlowDirectionMethodType.EndToStart;
        check(SewerPipeConnections.TryForward(flat, tr, out direction) && !direction, "Explicit direction resolves a flat legacy pipe.");
        flat.FlowDirectionMethod = Civil.FlowDirectionMethodType.StartToEnd;
        check(SewerPipeConnections.TryForward(flat, tr, out direction) && direction, "Both native explicit flow directions are supported.");
    }
}

namespace Autodesk.AutoCAD.Geometry
{
    public struct Point3d
    {
        public double Z { get; }
        public Point3d(double z) { Z = z; }
    }
}

namespace Autodesk.Civil.DatabaseServices
{
    public enum FlowDirectionMethodType { BySlope, StartToEnd, EndToStart }
    public sealed class Pipe : DBObject
    {
        public ObjectId StartStructureId { get; set; }
        public ObjectId EndStructureId { get; set; }
        public Point3d StartPoint { get; set; }
        public Point3d EndPoint { get; set; }
        public double InnerHeight { get; set; }
        public double OuterHeight { get; set; }
        public double InnerDiameterOrWidth { get; set; }
        public FlowDirectionMethodType FlowDirectionMethod { get; set; }
    }
    public sealed class Structure : DBObject
    {
        internal List<ObjectId> Pipes { get; } = new List<ObjectId>();
        public int ConnectedPipesCount => Pipes.Count;
        public ObjectId get_ConnectedPipe(int index) => Pipes[index];
    }
}
