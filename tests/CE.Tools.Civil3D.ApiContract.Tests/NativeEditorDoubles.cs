using System;
using Autodesk.AutoCAD.DatabaseServices;

namespace Autodesk.AutoCAD.Runtime
{
    public enum ErrorStatus { UserBreak, InvalidInput }
    public sealed class Exception : System.Exception
    {
        public ErrorStatus ErrorStatus { get; }
        public Exception(ErrorStatus status) { ErrorStatus = status; }
    }
}

namespace Autodesk.AutoCAD.EditorInput
{
    public sealed class Editor
    {
        internal ObjectId[] ImpliedSelection = new ObjectId[0];
        internal Action<object[]> Execute;
        public void SetImpliedSelection(ObjectId[] ids) { ImpliedSelection = ids; }
        public void Command(params object[] arguments) { Execute(arguments); }
    }
}

namespace Autodesk.AutoCAD.ApplicationServices
{
    public sealed class CommandEventArgs : EventArgs
    {
        public string GlobalCommandName { get; }
        internal CommandEventArgs(string name) { GlobalCommandName = name; }
    }

    public sealed class Document
    {
        public EditorInput.Editor Editor { get; } = new EditorInput.Editor();
        public event EventHandler<CommandEventArgs> CommandEnded;
        public event EventHandler<CommandEventArgs> CommandCancelled;
        public event EventHandler<CommandEventArgs> CommandFailed;
        internal int ListenerCount => (CommandEnded?.GetInvocationList().Length ?? 0) +
            (CommandCancelled?.GetInvocationList().Length ?? 0) +
            (CommandFailed?.GetInvocationList().Length ?? 0);
        internal void End(string command) { CommandEnded?.Invoke(this, new CommandEventArgs(command)); }
        internal void Cancel(string command) { CommandCancelled?.Invoke(this, new CommandEventArgs(command)); }
        internal void Fail(string command) { CommandFailed?.Invoke(this, new CommandEventArgs(command)); }
    }
}
