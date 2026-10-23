using System;

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
            _page.Show();
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
            if (_control != null && _control.IsHandleCreated) _control.BeginInvoke(a);
            else a();
        }

        internal void Do(Cmd c) => _control?.Execute(c);

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
