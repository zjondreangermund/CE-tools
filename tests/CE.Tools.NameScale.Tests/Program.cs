using CETools.Civil3D;
using Autodesk.AutoCAD.DatabaseServices;

void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
var text = new MText { Rotation = 0.75, Location = 42, PaperOrientation = true };
text.Contexts["old"] = (0, 0);
NameAnnotationScaleOrientation.Synchronize(text, new object[] { "new" });
Check(text.Contexts.Count == 2, "Existing scales retained and requested scales added");
Check(text.Contexts.Values.All(x => x == (42d, 0.75)), "Old horizontal and new representations inherit final placement");
Check(!text.PaperOrientation && text.GraphicsModified, "Layout orientation disabled and graphics invalidated");
Check(text.Rotation == 0.75 && text.Location == 42, "Native orientation changes do not overwrite intended geometry");
text.Rotation = 0;
NameAnnotationScaleOrientation.Synchronize(text, Array.Empty<object>());
Check(text.Contexts.Values.All(x => x.Item2 == 0), "Intentional horizontal text remains horizontal at every scale");
Console.WriteLine("Host API contract tests; native rendering still requires Civil 3D.");

namespace Autodesk.AutoCAD.DatabaseServices
{
    // Contract double: contexts snapshot active geometry; resetting propagates it.
    public sealed class MText
    {
        public double Location, Rotation;
        public bool PaperOrientation, GraphicsModified;
        public readonly Dictionary<string, (double, double)> Contexts = new();
        public void SetPaperOrientation(bool value) { PaperOrientation = value; Rotation = 0; Location = -1; }
        public void ResetScaleDependentProperties()
        { foreach (var key in Contexts.Keys.ToArray()) Contexts[key] = (Location, Rotation); }
        public void RecordGraphicsModified(bool value) { GraphicsModified = value; }
    }
}
namespace CETools.Civil3D
{
    internal static class AnnotationScaleSyncManager
    {
        internal static int AddUsedDrawingScaleContexts(MText text, IEnumerable<object> scales)
        {
            foreach (var scale in scales) text.Contexts.TryAdd((string)scale, (text.Location, text.Rotation));
            return text.Contexts.Count;
        }
    }
}
