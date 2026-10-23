using System;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace MeshFit.SolidWorks
{
    /// <summary>Glue between the Reverse Engineer PropertyManager page and the task pane / session.</summary>
    public sealed partial class SwAddin
    {
        private ReverseEngineerPage _page;
        private bool _pageRefreshQueued;

        /// <summary>Opens the Reverse Engineer PropertyManager (paint → fit → ✔ create).</summary>
        internal void ShowReverseEngineerPage()
        {
            if (_page == null)
            {
                _page = new ReverseEngineerPage(this);
                Session.Changed += QueuePageRefresh;
            }
            try
            {
                Guarded.Write("Opening the Reverse Engineer page");
                _page.Show();
                Guarded.Write("Reverse Engineer page open");
            }
            catch (Exception ex)
            {
                Guarded.Write("Reverse Engineer page failed: " + ex);
                // Never fail silently: the ribbon button would look unwired.
                MessageBox.Show(MainWindow, "The Reverse Engineer page could not be opened:\n" + ex.Message, "MeshFit",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>Session.Changed fires for every brush dab: coalesce page refreshes onto the UI thread.</summary>
        private void QueuePageRefresh()
        {
            if (_page == null || !_page.IsOpen || _pageRefreshQueued) return;
            _pageRefreshQueued = true;
            Post(() => { _pageRefreshQueued = false; _page?.Refresh(); });
        }

        /// <summary>Runs an action later on the SOLIDWORKS UI thread (after the current callback returns).</summary>
        internal void Post(Action a)
        {
            Action safe = () => Guarded.Run("posted action", a);
            if (_control != null && _control.IsHandleCreated) _control.BeginInvoke(safe);
            else safe();
        }

        internal void Do(Cmd c) => _control?.Execute(c);

        /// <summary>The SOLIDWORKS main window: owner for MeshFit windows so they stay in front of it.</summary>
        internal IWin32Window MainWindow
        {
            get
            {
                try
                {
                    long h = ((IFrame)_app.Frame()).GetHWndx64();
                    if (h != 0) return new Win32Window(new IntPtr(h));
                }
                catch { /* fall back to the task pane */ }
                return _control;
            }
        }

        private sealed class Win32Window : IWin32Window
        {
            public Win32Window(IntPtr h) { Handle = h; }
            public IntPtr Handle { get; }
        }

        /// <summary>Mirrors MeshFit messages in the SOLIDWORKS status bar (visible even when the task pane is closed).</summary>
        internal void StatusBar(string msg)
        {
            try { ((IFrame)_app?.Frame())?.SetStatusBarText("MeshFit: " + msg); } catch { }
        }

        /// <summary>Opens the MeshFit task pane.</summary>
        internal void ShowTaskPane()
        {
            try { _pane?.ShowView(); } catch { }
        }

        internal void SetToolFromPage(PaintTool t) => _control?.SetToolExternal(t);

        internal void DeleteActiveRegion() => _control?.DeleteActiveRegion();

        /// <summary>Sets the fit tolerance (metres).</summary>
        internal void SetTolerance(double metres) => _control?.SetTolerance(metres);

        /// <summary>Creates the active region's feature; <paramref name="done"/> reports success (possibly later).</summary>
        internal void CreateFromPage(ReverseEngineerPage.CreateKind kind, bool next, Action<bool> done)
        {
            if (_control == null) { done?.Invoke(false); return; }
            _control.CreateForPage(kind, next, done);
        }
    }
}
