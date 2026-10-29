using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.BranchTreeSortCommands))]
namespace CETools.Civil3D
{
    public sealed class BranchTreeSortCommands
    {
        [CommandMethod("CE_TOOLS", "CE_BRANCHSORT", CommandFlags.Modal)]
        public void Sort()
        {
            var doc = AcApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            int sorted = 0;
            EnumProc callback = (window, parameter) =>
            {
                var name = new StringBuilder(256); GetClassName(window, name, name.Capacity);
                if (name.ToString() == "SysTreeView32")
                {
                    IntPtr root = SendMessage(window, 0x110A, new IntPtr(0), IntPtr.Zero); // TVGN_ROOT
                    Visit(window, root, ref sorted, 0);
                }
                return true;
            };
            EnumChildWindows(AcApplication.MainWindow.Handle, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            doc.Editor.WriteMessage(sorted > 0 ? "\nBranch collections sorted numerically: " + sorted + ". Object names and geometry were not changed." :
                "\nNo populated native branch tree was found. Expand Centerline Alignments in Toolspace and run CE_BRANCHSORT again. This host may use a different tree control.");
        }
        private static void Visit(IntPtr tree, IntPtr item, ref int sorted, int depth)
        {
            if (depth > 30) return;
            for (var current = item; current != IntPtr.Zero; current = SendMessage(tree, 0x110A, new IntPtr(1), current))
            {
                var child = SendMessage(tree, 0x110A, new IntPtr(4), current); // TVGN_CHILD
                if (child == IntPtr.Zero) continue;
                var items = new List<Tuple<IntPtr, string>>();
                for (var node = child; node != IntPtr.Zero; node = SendMessage(tree, 0x110A, new IntPtr(1), node))
                    items.Add(Read(tree, node));
                if (items.Count >= 2 && items.Count(x => BranchNumber(x.Item2) != int.MaxValue) >= 2 &&
                    items.Select(x => x.Item1).Distinct().Count() == items.Count)
                {
                    var names = items.ToDictionary(x => x.Item1, x => x.Item2);
                    CompareProc compare = (a, b, ignored) =>
                    {
                        string first, second;
                        if (!names.TryGetValue(a, out first) || !names.TryGetValue(b, out second)) return 0;
                        int result = BranchNumber(first).CompareTo(BranchNumber(second));
                        return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(first, second);
                    };
                    var sort = new SortInfo { Parent = current, Compare = Marshal.GetFunctionPointerForDelegate(compare), Parameter = IntPtr.Zero };
                    if (SortMessage(tree, 0x1115, IntPtr.Zero, ref sort) != IntPtr.Zero) sorted++;
                    GC.KeepAlive(compare);
                    child = SendMessage(tree, 0x110A, new IntPtr(4), current);
                }
                Visit(tree, child, ref sorted, depth + 1);
            }
        }
        internal static int BranchNumber(string name)
        {
            return BranchLabelRepeatPlan.BranchNumber(name);
        }
        private static Tuple<IntPtr, string> Read(IntPtr tree, IntPtr handle)
        {
            IntPtr buffer = Marshal.AllocHGlobal(2048);
            try
            {
                var item = new TreeItem { Mask = 1 | 4, Handle = handle, Text = buffer, TextMax = 1024 };
                if (ItemMessage(tree, 0x113E, IntPtr.Zero, ref item) == IntPtr.Zero) return Tuple.Create(IntPtr.Zero, "");
                return Tuple.Create(item.Parameter, Marshal.PtrToStringUni(buffer) ?? "");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct TreeItem
        {
            public uint Mask; public IntPtr Handle; public uint State, StateMask; public IntPtr Text;
            public int TextMax, Image, SelectedImage, Children; public IntPtr Parameter;
        }
        [StructLayout(LayoutKind.Sequential)] private struct SortInfo { public IntPtr Parent, Compare, Parameter; }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int CompareProc(IntPtr a, IntPtr b, IntPtr data);
        private delegate bool EnumProc(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr ItemMessage(IntPtr window, uint message, IntPtr wParam, ref TreeItem item);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SortMessage(IntPtr window, uint message, IntPtr wParam, ref SortInfo sort);
    }
}
