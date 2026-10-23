using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// Tooltips shown explicitly just below the hovered control. The automatic WinForms placement uses the
    /// top-level form, which inside a SOLIDWORKS task pane is not ours, and often lands above the screen.
    /// </summary>
    internal sealed class HoverTips : IDisposable
    {
        private readonly ToolTip _tip = new ToolTip { UseAnimation = false, UseFading = false };
        private readonly Dictionary<Control, string> _text = new Dictionary<Control, string>();

        public void SetToolTip(Control c, string text)
        {
            if (c == null) return;
            bool known = _text.ContainsKey(c);
            _text[c] = text;
            if (known) return;
            Hook(c, c);
            // Composite controls (NumericUpDown, ComboBox) get the mouse on their child windows.
            foreach (Control child in c.Controls) Hook(child, c);
        }

        private void Hook(Control source, Control owner)
        {
            source.MouseHover += (s, e) => Guarded.Run("tooltip", () =>
            {
                if (!_text.TryGetValue(owner, out var t) || string.IsNullOrEmpty(t) || !owner.IsHandleCreated || owner.IsDisposed) return;
                _tip.Show(t, owner, 0, owner.Height + 2, 12000);
            });
            source.MouseLeave += (s, e) => Guarded.Run("tooltip", () => _tip.Hide(owner));
            source.MouseDown += (s, e) => Guarded.Run("tooltip", () => _tip.Hide(owner));
        }

        public void Dispose() => _tip.Dispose();
    }
}
