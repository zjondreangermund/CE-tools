using System;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    /// <summary>
    /// Civil 3D 2023 occasionally leaves freshly appended managed entities out of
    /// the display list until AUDIT/PURGE/OVERKILL forces a redraw. This manager
    /// records append/modify activity while a CE command is active and performs one
    /// graphics-queue flush + normal REGEN + UpdateScreen after the command stack is
    /// empty.
    ///
    /// It does not alter drawing geometry, run repair/cleanup commands, or inject a
    /// command with SendStringToExecute. Keeping refresh work inside the graphics API
    /// avoids creating an extra command-history boundary that can interfere with
    /// AutoCAD UNDO/REDO.
    /// </summary>
    internal static class August21GraphicsRefreshManager
    {
        private static Database _database;
        private static bool _initialised;
        private static bool _dirty;
        private static bool _busy;

        internal static void Initialize()
        {
            if (_initialised) return;
            _initialised = true;
            AcApplication.Idle += OnIdle;
        }

        internal static void Terminate()
        {
            if (!_initialised) return;
            AcApplication.Idle -= OnIdle;
            DetachDatabase();
            _initialised = false;
            _dirty = false;
        }

        internal static void MarkDirty()
        {
            _dirty = true;
        }

        private static void OnIdle(object sender, EventArgs eventArgs)
        {
            Document document = AcApplication.DocumentManager.MdiActiveDocument;
            AttachDatabase(document == null ? null : document.Database);
            if (document == null || !_dirty || _busy) return;

            string commands = SafeCommandNames();
            if (!string.IsNullOrWhiteSpace(commands)) return;

            _busy = true;
            try
            {
                using (DocumentLock documentLock = document.LockDocument())
                {
                    try
                    {
                        document.Database.TransactionManager.QueueForGraphicsFlush();
                    }
                    catch { }
                    document.Editor.Regen();
                    AcApplication.UpdateScreen();
                }
                _dirty = false;
            }
            catch
            {
                // Keep the dirty flag so the next idle cycle can retry after Civil
                // finishes any delayed database/display work.
            }
            finally
            {
                _busy = false;
            }
        }

        private static void AttachDatabase(Database database)
        {
            if (ReferenceEquals(_database, database)) return;
            DetachDatabase();
            _database = database;
            if (_database == null) return;
            _database.ObjectAppended += OnObjectChanged;
            _database.ObjectModified += OnObjectChanged;
        }

        private static void DetachDatabase()
        {
            if (_database != null)
            {
                _database.ObjectAppended -= OnObjectChanged;
                _database.ObjectModified -= OnObjectChanged;
            }
            _database = null;
        }

        private static void OnObjectChanged(object sender, ObjectEventArgs eventArgs)
        {
            if (_busy) return;
            string commands = SafeCommandNames();
            if (commands.IndexOf("CE_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                commands.IndexOf("CE-", StringComparison.OrdinalIgnoreCase) >= 0)
                _dirty = true;
        }

        private static string SafeCommandNames()
        {
            try
            {
                return Convert.ToString(
                    AcApplication.GetSystemVariable("CMDNAMES"),
                    CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch { return string.Empty; }
        }
    }
}
