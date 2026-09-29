using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace CETools.Civil3D
{
    internal static class AugustGlobalShortcutManager
    {
        private static ShortcutMessageFilter _filter;
        private static bool _installed;

        internal static void Initialize()
        {
            if (_installed) return;
            _filter = new ShortcutMessageFilter();
            System.Windows.Forms.Application.AddMessageFilter(_filter);
            _installed = true;
        }

        internal static void Terminate()
        {
            if (!_installed) return;
            if (_filter != null)
                System.Windows.Forms.Application.RemoveMessageFilter(_filter);
            _filter = null;
            _installed = false;
        }

        private sealed class ShortcutMessageFilter : IMessageFilter
        {
            private const int WmKeyDown = 0x0100;
            private const int WmSysKeyDown = 0x0104;

            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WmKeyDown && m.Msg != WmSysKeyDown)
                    return false;

                Keys key = (Keys)(int)m.WParam & Keys.KeyCode;
                if (key != Keys.F || (Control.ModifierKeys & Keys.Control) != Keys.Control)
                    return false;

                try
                {
                    Document doc = AcApplication.DocumentManager.MdiActiveDocument;
                    if (doc == null) return false;
                    doc.SendStringToExecute("CE_TOOLSPALETTE ", true, false, true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
