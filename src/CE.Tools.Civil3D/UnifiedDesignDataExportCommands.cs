using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CivilNetwork = Autodesk.Civil.DatabaseServices.Network;
using CivilPipe = Autodesk.Civil.DatabaseServices.Pipe;
using CivilStructure = Autodesk.Civil.DatabaseServices.Structure;

[assembly: CommandClass(typeof(CETools.Civil3D.UnifiedDesignDataExportCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Shared numbered pipe/structure design-data Excel export.
    /// Uses the exact Civil 3D source-network entities and sorts numeric
    /// components naturally: P1.2 before P1.10. Never modifies the drawing.
    /// </summary>
    public sealed class UnifiedDesignDataExportCommands
    {
        public void ExportDesignData()
        {
            var options = new ProductionSettingsDialogModel(
                "CE Tools - Design Data Export",
                "Export Water and/or Sewer pipe and structure design data, including native sizes and types, sorted by branch and numeric part sequence.");
            options.AddChoice("Discipline", "01 Network", "Network discipline", "Water and Sewer",
                "Choose the network discipline to include in the workbook.",
                new[] { "Water and Sewer", "Water", "Sewer" });
            if (!DisciplineWorkflowDialogs.EditSettings(options)) return;
            Export(options.Text("Discipline"));
        }

        public void ExportSewerDesignData() { Export("Sewer"); }

        internal static void Export(string discipline)
        {
            Document doc = AcApplication.DocumentManager.MdiActiveDocument;
            CivilDocument civil = CivilApplication.ActiveDocument;
            if (doc == null || civil == null) return;

            var pipeRows = new List<IList<string>>
            {
                new List<string> { "NETWORK", "PIPE", "TYPE", "SIZE", "START X", "START Y", "START Z",
                    "END X", "END Y", "END Z", "LENGTH 3D (drawing units)", "INNER DIAMETER (drawing units)" }
            };
            var structureRows = new List<IList<string>>
            {
                new List<string> { "NETWORK", "STRUCTURE", "TYPE", "SIZE", "X", "Y", "Z" }
            };

            var pipes = new List<NamedRow>();
            var structures = new List<NamedRow>();
            Func<double, string> fmt = x => x.ToString("0.###", CultureInfo.InvariantCulture);

            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId networkId in civil.GetPipeNetworkIds())
                {
                    CivilNetwork network = tr.GetObject(networkId, OpenMode.ForRead, false) as CivilNetwork;
                    if (network == null || !MatchesNetwork(network.Name, discipline)) continue;

                    foreach (ObjectId pipeId in network.GetPipeIds())
                    {
                        CivilPipe pipe = tr.GetObject(pipeId, OpenMode.ForRead, false) as CivilPipe;
                        if (pipe == null) continue;
                        var a = pipe.StartPoint;
                        var b = pipe.EndPoint;
                        pipes.Add(new NamedRow(network.Name, pipe.Name, new List<string>
                        {
                            network.Name, pipe.Name,
                            ReadString(pipe, "PartFamilyName", "Description", "PartType"),
                            ReadString(pipe, "PartSizeName", "SizeName"),
                            fmt(a.X), fmt(a.Y), fmt(a.Z), fmt(b.X), fmt(b.Y), fmt(b.Z),
                            fmt((b - a).Length), ReadString(pipe, "InnerDiameterOrWidth", "NominalDiameter")
                        }));
                    }

                    foreach (ObjectId structureId in network.GetStructureIds())
                    {
                        CivilStructure structure = tr.GetObject(structureId, OpenMode.ForRead, false) as CivilStructure;
                        if (structure == null) continue;
                        var position = structure.Position;
                        structures.Add(new NamedRow(network.Name, structure.Name, new List<string>
                        {
                            network.Name, structure.Name,
                            ReadString(structure, "PartFamilyName", "Description", "PartType"),
                            ReadString(structure, "PartSizeName", "SizeName"),
                            fmt(position.X), fmt(position.Y), fmt(position.Z)
                        }));
                    }
                }
            }

            foreach (var row in pipes.OrderBy(x => NaturalSortKey(x.Network), StringComparer.OrdinalIgnoreCase)
                                     .ThenBy(x => NaturalSortKey(x.Name), StringComparer.OrdinalIgnoreCase))
                pipeRows.Add(row.Cells);
            foreach (var row in structures.OrderBy(x => NaturalSortKey(x.Network), StringComparer.OrdinalIgnoreCase)
                                          .ThenBy(x => NaturalSortKey(x.Name), StringComparer.OrdinalIgnoreCase))
                structureRows.Add(row.Cells);
            if (pipes.Count == 0 && structures.Count == 0)
            {
                System.Windows.Forms.MessageBox.Show("No matching Water/Sewer networks or parts found.",
                    "CE Tools - Design Data Export");
                return;
            }

            using (var save = new System.Windows.Forms.SaveFileDialog())
            {
                save.Title = "Save CE Tools Design Data";
                save.Filter = "Excel Workbook (*.xlsx)|*.xlsx";
                save.DefaultExt = "xlsx";
                save.AddExtension = true;
                save.FileName = "CE-Tools-" + discipline.Replace(" ", "-") + "-Design-Data.xlsx";
                if (save.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                try
                {
                    SimpleXlsxWriter.Write(save.FileName, new List<XlsxSheet>
                    {
                        new XlsxSheet("Pipes", pipeRows),
                        new XlsxSheet("Structures", structureRows)
                    });
                    doc.Editor.WriteMessage("\nCE_DESIGNDATAEXPORT: {0} pipes, {1} structures exported: {2}",
                        pipes.Count, structures.Count, save.FileName);
                }
                catch (System.Exception e)
                {
                    System.Windows.Forms.MessageBox.Show(e.Message, "CE Tools - Design Data Export");
                }
            }
        }

        private static bool MatchesNetwork(string networkName, string requested)
        {
            string name = networkName ?? string.Empty;
            bool sewer = name.IndexOf("Sewer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         name.StartsWith("S-", StringComparison.OrdinalIgnoreCase);
            bool water = !sewer && (name.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.StartsWith("W-", StringComparison.OrdinalIgnoreCase));
            return (water && !string.Equals(requested, "Sewer", StringComparison.OrdinalIgnoreCase)) ||
                   (sewer && !string.Equals(requested, "Water", StringComparison.OrdinalIgnoreCase));
        }

        private static string ReadString(object part, params string[] properties)
        {
            foreach (string name in properties)
            {
                try
                {
                    var property = part.GetType().GetProperty(name);
                    if (property == null) continue;
                    object raw = property.GetValue(part, null);
                    string text = Convert.ToString(raw, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
                catch { }
            }
            return string.Empty;
        }

        private static string NaturalSortKey(string value)
        {
            return Regex.Replace(value ?? string.Empty, @"\d+", match =>
            {
                long n;
                return long.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)
                    ? n.ToString("D12", CultureInfo.InvariantCulture)
                    : match.Value;
            });
        }

        private sealed class NamedRow
        {
            internal readonly string Network;
            internal readonly string Name;
            internal readonly IList<string> Cells;
            internal NamedRow(string network, string name, IList<string> cells)
            {
                Network = network ?? string.Empty;
                Name = name ?? string.Empty;
                Cells = cells;
            }
        }
    }
}
