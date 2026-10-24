using System;
using System.IO;
using System.Text;

namespace CETools.Civil3D
{
    /// <summary>Best-effort, synchronously closed log surviving a native process shutdown.</summary>
    internal sealed class AutoDesignDiagnostics
    {
        internal string LogPath { get; private set; }
        internal void Start(string drawingName, string diagnosticFolder = null)
        {
            try
            {
                string folder = diagnosticFolder ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CE Tools", "Diagnostics");
                Directory.CreateDirectory(folder);
                LogPath = Path.Combine(folder, "AutoDesign-last-run.log");
                if (File.Exists(LogPath)) File.Copy(LogPath, Path.Combine(folder, "AutoDesign-previous-run.log"), true);
                File.WriteAllText(LogPath, "CE_AUTODESIGN diagnostic v2\r\nDrawing: " + drawingName + "\r\n", new UTF8Encoding(false));
                Write("START");
            }
            catch { LogPath = null; }
        }

        internal void Write(string message)
        {
            if (LogPath == null) return;
            try
            {
                using (var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("O") + " " + message + "\r\n");
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
            }
            catch { /* Logging cannot interrupt a drawing command. */ }
        }
    }
}
