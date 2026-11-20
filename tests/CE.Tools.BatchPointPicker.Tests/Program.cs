using CETools.Civil3D;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var editor = new Editor();
var stages = new List<string>();
Check(BatchCrossSectionPointPicker.TryPick(editor, new IntPtr(123), stages.Add, out var point), "Pick must succeed");
Check(point.X == 101 && point.Y == 202, "UCS point must be transformed to WCS exactly once");
Check(!editor.Active && editor.Disposed == 1 && editor.Owner == new IntPtr(123), "Owner and disposal");
Check(stages.Last() == "Point pick: interaction scope exited", "Exit must be logged");

editor = new Editor { Status = PromptStatus.Cancel };
Check(!BatchCrossSectionPointPicker.TryPick(editor, new IntPtr(1), stages.Add, out point), "Cancel is not a picked point");
Check(!editor.Active && editor.Disposed == 1 && editor.TransformReads == 0, "Cancel restores focus without reading UCS");

foreach (bool failTransform in new[] { false, true })
{
    editor = new Editor { FailPick = !failTransform, FailTransform = failTransform };
    bool threw = false;
    try { BatchCrossSectionPointPicker.TryPick(editor, new IntPtr(1), stages.Add, out point); }
    catch (InvalidOperationException) { threw = true; }
    Check(threw && !editor.Active && editor.Disposed == 1, "Managed failure must dispose interaction");
}

editor = new Editor();
bool rejected = false;
try { BatchCrossSectionPointPicker.TryPick(editor, IntPtr.Zero, stages.Add, out point); }
catch (InvalidOperationException) { rejected = true; }
Check(rejected && editor.Picks == 0 && editor.Owner == IntPtr.Zero, "Reject missing owner before native calls");
for (int i = 0; i < 3; i++)
    Check(BatchCrossSectionPointPicker.TryPick(editor, new IntPtr(1), stages.Add, out point), "Repeated pick");
Check(editor.Disposed == 3 && !editor.Active, "Repeated picks must not leak interaction scopes");
Console.WriteLine("PASS: modal ownership, WCS conversion, cancellation, exceptions, invalid owner and repeated picks");

namespace Autodesk.AutoCAD.Geometry
{
    public struct Matrix3d { }
    public struct Point3d
    {
        public double X, Y;
        public Point3d(double x, double y) { X = x; Y = y; }
        public Point3d TransformBy(Matrix3d matrix) { return new Point3d(X + 100, Y + 200); }
    }
}

namespace Autodesk.AutoCAD.EditorInput
{
    public enum PromptStatus { OK, Cancel }
    public sealed class PromptPointResult
    {
        public PromptStatus Status;
        public Point3d Value = new Point3d(1, 2);
    }
    public sealed class EditorUserInteraction : IDisposable
    {
        private readonly Editor editor;
        public EditorUserInteraction(Editor editor) { this.editor = editor; editor.Active = true; }
        public void Dispose() { editor.Active = false; editor.Disposed++; }
    }
    public sealed class Editor
    {
        public bool Active, FailPick, FailTransform;
        public int Disposed, TransformReads, Picks;
        public IntPtr Owner;
        public PromptStatus Status = PromptStatus.OK;
        public EditorUserInteraction StartUserInteraction(IntPtr owner)
        {
            if (Active) throw new Exception("Nested interaction");
            Owner = owner;
            return new EditorUserInteraction(this);
        }
        public PromptPointResult GetPoint(string message)
        {
            if (!Active) throw new Exception("Point acquisition outside modal interaction");
            Picks++;
            if (FailPick) throw new InvalidOperationException("Simulated managed pick error");
            return new PromptPointResult { Status = Status };
        }
        public Matrix3d CurrentUserCoordinateSystem
        {
            get
            {
                if (!Active) throw new Exception("UCS read after interaction ended");
                TransformReads++;
                if (FailTransform) throw new InvalidOperationException("Simulated UCS read error");
                return new Matrix3d();
            }
        }
    }
}
