using System;
using System.Collections.Generic;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace MeshFit.SolidWorks
{
    /// <summary>Glue between the PropertyManager pages (Region Groups, modeling wizards, Mesh Sketch) and the add-in.</summary>
    public sealed partial class SwAddin
    {
        private readonly Dictionary<string, PageBase> _pages = new Dictionary<string, PageBase>();
        private bool _pageRefreshQueued;

        internal RegionGroupsPage RegionGroups => GetPage("regions", () => new RegionGroupsPage(this));
        internal WizardPage Wizard(WizardKind kind) => GetPage("wizard" + kind, () => new WizardPage(this, kind));
        internal MeshSketchPage MeshSketch => GetPage("sketch", () => new MeshSketchPage(this));

        private T GetPage<T>(string key, Func<T> create) where T : PageBase
        {
            if (!_pages.TryGetValue(key, out var p))
            {
                p = create();
                _pages[key] = p;
                if (_pages.Count == 1) Session.Changed += QueuePageRefresh;
            }
            return (T)p;
        }

        /// <summary>Opens a PropertyManager page; a failure is reported instead of looking like a dead button.</summary>
        internal void ShowPage(PageBase page)
        {
            try
            {
                Guarded.Write("Opening " + page.Title);
                page.Show();
            }
            catch (Exception ex)
            {
                Guarded.Write(page.Title + " failed: " + ex);
                MessageBox.Show(MainWindow, $"The {page.Title} page could not be opened:\n{ex.Message}", "MeshFit",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>Session.Changed fires for every brush dab: coalesce page refreshes onto the UI thread.</summary>
        private void QueuePageRefresh()
        {
            if (_pageRefreshQueued) return;
            bool any = false;
            foreach (var p in _pages.Values) any |= p.IsOpen;
            if (!any) return;
            _pageRefreshQueued = true;
            Post(() =>
            {
                _pageRefreshQueued = false;
                foreach (var p in _pages.Values) if (p.IsOpen) p.Refresh();
            });
        }

        /// <summary>Runs an action later on the SOLIDWORKS UI thread (after the current callback returns).</summary>
        internal void Post(Action a)
        {
            Action safe = () => Guarded.Run("posted action", a);
            if (_control != null && _control.IsHandleCreated) _control.BeginInvoke(safe);
            else safe();
        }

        internal void Do(Cmd c) => _control?.Execute(c);

        internal TaskPaneControl Control => _control;

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
    }
}
