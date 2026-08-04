using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: CommandClass(typeof(CETools.Civil3D.CommandCatalogueCommands))]

namespace CETools.Civil3D
{
    /// <summary>
    /// Makes the complete loaded CE Tools command surface visible, auditable and
    /// exportable without maintaining a second hand-written command register.
    /// </summary>
    public sealed class CommandCatalogueCommands
    {
        [CommandMethod("CE_TOOLS", "CE_COMMANDCENTER", CommandFlags.Modal)]
        public void OpenCommandCentre()
        {
            FloatingToolsCommands.ShowWindow();
        }

        [CommandMethod("CE_TOOLS", "CE_COMMANDREPORT", CommandFlags.Modal)]
        public void ShowCommandReport()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            List<FloatingToolDefinition> tools = LoadTools();
            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Complete Command Catalogue",
                tools.Count.ToString(CultureInfo.InvariantCulture) +
                " unique loaded commands. Ribbon descriptions are retained; " +
                "specialist commands are discovered from CommandMethod declarations.",
                new List<string> { "COMMAND", "NAME", "PANEL", "MODULE", "DESCRIPTION" },
                tools.Select(item => (IList<string>)new List<string>
                {
                    item.Command.Trim(),
                    item.Text,
                    item.Panel,
                    item.Group,
                    item.ToolTip
                }).ToList(),
                "CE TOOLS COMMAND CATALOGUE");
        }

        [CommandMethod("CE_TOOLS", "CE_COMMANDAUDIT", CommandFlags.Modal)]
        public void AuditCommands()
        {
            Document document = ActiveDocument();
            if (document == null) return;

            List<FloatingToolDefinition> declared =
                FloatingToolsCommands.ReadDeclaredCommands().ToList();
            List<FloatingToolDefinition> tools = LoadTools();
            int catalogueOnly = tools.Count(item => string.Equals(
                item.Panel,
                "Command Catalogue",
                StringComparison.OrdinalIgnoreCase));
            List<IGrouping<string, FloatingToolDefinition>> duplicates = declared
                .GroupBy(item => item.Command.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .ToList();

            var rows = new List<IList<string>>
            {
                Row("Unique commands", tools.Count, "Merged by global command name"),
                Row("Command declarations", declared.Count, "CommandMethod attributes in the loaded assembly"),
                Row("Ribbon-described commands", tools.Count - catalogueOnly, "Commands with curated ribbon metadata"),
                Row("Catalogue-only commands", catalogueOnly, "Still searchable and launchable from Ctrl+F"),
                Row("Duplicate declarations", duplicates.Count, duplicates.Count == 0
                    ? "Pass"
                    : string.Join(", ", duplicates.Select(item => item.Key)))
            };
            foreach (IGrouping<string, FloatingToolDefinition> panel in tools
                .GroupBy(item => item.Panel, StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                rows.Add(Row("Panel: " + panel.Key, panel.Count(), "Unique commands"));

            GridReportPresenter.ShowReportAndOfferTable(
                document,
                "CE Tools - Command Surface Audit",
                duplicates.Count == 0
                    ? "PASS: every loaded command has a unique global name."
                    : "REVIEW: duplicate global command declarations were detected.",
                new List<string> { "CHECK", "COUNT", "RESULT" },
                rows,
                "CE TOOLS COMMAND SURFACE AUDIT");
        }

        [CommandMethod("CE_TOOLS", "CE_COMMANDEXPORT", CommandFlags.Modal)]
        public void ExportCommandCsv()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            string path;
            if (!PromptSavePath(
                    document.Editor,
                    "CSV files (*.csv)|*.csv",
                    "CE-Tools-Command-Catalogue.csv",
                    ".csv",
                    out path))
                return;

            try
            {
                var lines = new List<string>
                {
                    "Command,Name,Panel,Module,Description"
                };
                lines.AddRange(LoadTools().Select(item => string.Join(",", new[]
                {
                    Csv(item.Command.Trim()), Csv(item.Text), Csv(item.Panel),
                    Csv(item.Group), Csv(item.ToolTip)
                })));
                File.WriteAllLines(path, lines, new UTF8Encoding(true));
                document.Editor.WriteMessage(
                    "\nCE Tools command catalogue exported: {0}",
                    path);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE Tools command export failed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_COMMANDHTML", CommandFlags.Modal)]
        public void ExportCommandHtml()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            string path;
            if (!PromptSavePath(
                    document.Editor,
                    "HTML files (*.html)|*.html",
                    "CE-Tools-Command-Catalogue.html",
                    ".html",
                    out path))
                return;

            try
            {
                string html = BuildHtml(LoadTools());
                File.WriteAllText(path, html, new UTF8Encoding(true));
                document.Editor.WriteMessage(
                    "\nCE Tools searchable command reference exported: {0}",
                    path);
                try
                {
                    Process.Start(new ProcessStartInfo(path)
                    {
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // The file remains available when local policy blocks launching it.
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE Tools HTML export failed. {0}",
                    exception.Message);
            }
        }

        [CommandMethod("CE_TOOLS", "CE_RIBBONREFRESH", CommandFlags.Modal)]
        public void RefreshRibbonAndCatalogue()
        {
            Document document = ActiveDocument();
            if (document == null) return;
            try
            {
                bool created = RibbonBuilder.EnsureCreated();
                FloatingToolsCommands.ReloadWindow();
                document.Editor.WriteMessage(created
                    ? "\nCE Tools ribbon and complete command catalogue refreshed."
                    : "\nThe Autodesk ribbon is not available yet. Run CE_RIBBONREFRESH after the drawing UI finishes loading.");
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage(
                    "\nCE Tools ribbon refresh failed. {0}",
                    exception.Message);
            }
        }

        private static List<FloatingToolDefinition> LoadTools()
        {
            return FloatingToolsCommands.ReadCurrentRibbonTools()
                .OrderBy(item => item.Command.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IList<string> Row(string check, int count, string result)
        {
            return new List<string>
            {
                check,
                count.ToString(CultureInfo.InvariantCulture),
                result
            };
        }

        private static bool PromptSavePath(
            Editor editor,
            string filter,
            string initialName,
            string extension,
            out string path)
        {
            var options = new PromptSaveFileOptions("\nChoose the output file path: ")
            {
                Filter = filter,
                DialogCaption = "CE Tools Command Catalogue",
                InitialFileName = initialName
            };
            PromptFileNameResult result = editor.GetFileNameForSave(options);
            path = result.Status == PromptStatus.OK
                ? result.StringResult
                : string.Empty;
            if (result.Status != PromptStatus.OK)
                return false;
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                path += extension;
            return true;
        }

        private static string Csv(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        private static string BuildHtml(IList<FloatingToolDefinition> tools)
        {
            var html = new StringBuilder();
            html.AppendLine("<!doctype html><html><head><meta charset='utf-8'>");
            html.AppendLine("<meta name='viewport' content='width=device-width,initial-scale=1'>");
            html.AppendLine("<title>CE Tools Command Catalogue</title>");
            html.AppendLine("<style>body{font-family:Segoe UI,Arial;margin:0;background:#f4f6f8;color:#17202a}header{position:sticky;top:0;background:#18344a;color:white;padding:18px 24px;box-shadow:0 2px 8px #0003}input{width:min(720px,90%);padding:10px;border:0;border-radius:4px;font-size:16px}main{padding:20px 24px}.card{background:white;border-left:5px solid #2894c7;margin:9px 0;padding:12px 16px;box-shadow:0 1px 3px #0002}.code{font-family:Consolas,monospace;font-weight:bold;color:#075985}.meta{color:#5f6b76;font-size:13px}.desc{margin-top:5px}</style></head><body>");
            html.Append("<header><h1>CE Tools Command Catalogue</h1><p>")
                .Append(tools.Count.ToString(CultureInfo.InvariantCulture))
                .AppendLine(" unique commands</p><input id='q' autofocus placeholder='Search command, module or description'></header><main id='items'>");
            foreach (FloatingToolDefinition item in tools)
            {
                string search = item.Command + " " + item.Text + " " + item.Panel + " " + item.Group + " " + item.ToolTip;
                html.Append("<article class='card' data-search='").Append(Html(search.ToLowerInvariant())).Append("'>")
                    .Append("<div class='code'>").Append(Html(item.Command.Trim())).Append("</div>")
                    .Append("<strong>").Append(Html(item.Text)).Append("</strong>")
                    .Append("<div class='meta'>").Append(Html(item.Panel)).Append(" / ").Append(Html(item.Group)).Append("</div>")
                    .Append("<div class='desc'>").Append(Html(item.ToolTip)).AppendLine("</div></article>");
            }
            html.AppendLine("</main><script>const q=document.getElementById('q'),cards=[...document.querySelectorAll('.card')];q.oninput=()=>{const s=q.value.toLowerCase().trim();cards.forEach(c=>c.hidden=s&&!c.dataset.search.includes(s));};</script></body></html>");
            return html.ToString();
        }

        private static string Html(string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");
        }

        private static Document ActiveDocument()
        {
            return AcApplication.DocumentManager.MdiActiveDocument;
        }
    }
}
