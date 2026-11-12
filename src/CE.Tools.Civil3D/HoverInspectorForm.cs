using System;
using System.Windows.Forms;
namespace CETools.Civil3D
{
    // Owned modeless inspectors must not steal drawing focus when first shown.
    // Interactive selectors can still be clicked; no global cursor or graphics settings change.
    internal sealed class HoverInspectorForm : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        private bool _queued;
        private string _latest;
        private Label _target;
        internal HoverInspectorForm() { ShowInTaskbar = false; }
        internal void UpdateReport(Label target, string report)
        {
            if (IsDisposed || !IsHandleCreated || target == null || target.IsDisposed) return;
            _latest = report; _target = target;
            if (_queued) return;
            _queued = true;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    _queued = false;
                    if (IsDisposed || _target == null || _target.IsDisposed) return;
                    if (_target.Text != _latest) _target.Text = _latest;
                }));
            }
            catch (InvalidOperationException) { _queued = false; }
        }
    }
}
