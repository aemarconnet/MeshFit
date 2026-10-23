using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshFit.Core.Selection;
using Region = MeshFit.Core.Selection.Region;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// Watches the active region and, shortly after the user stops editing it, explores alternative
    /// selections on a worker thread (<see cref="SelectionOptimizer"/>). When a clearly better selection
    /// exists it raises <see cref="Suggested"/>. Any further edit cancels the running exploration.
    /// </summary>
    internal sealed class SuggestionEngine : IDisposable
    {
        private readonly Session _s;
        private readonly System.Windows.Forms.Timer _debounce = new System.Windows.Forms.Timer { Interval = 700 };
        private CancellationTokenSource _cts;
        private string _fingerprint;

        public bool Enabled { get; set; } = true;
        public bool Running { get; private set; }
        /// <summary>Raised on the UI thread: region, ranked candidates (best first). Empty list = nothing better.</summary>
        public event Action<Region, List<SelectionCandidate>> Suggested;
        public event Action<bool> RunningChanged;

        public SuggestionEngine(Session s)
        {
            _s = s;
            _debounce.Tick += (o, e) => Guarded.Run("suggestions", () => { _debounce.Stop(); Start(force: false); });
        }

        private string Fingerprint(Region r) =>
            r == null ? null : $"{r.Id}|{r.TriangleCount}|{r.Request}|{_s.FitOptions.Tolerance}|{_s.StopAtFillets && _s.Curvature != null}|{_s.BlendRadius}";

        /// <summary>Call whenever the session changes; restarts the debounce timer if the active region changed.</summary>
        public void Poke()
        {
            if (!Enabled || _s.IsStroking || _s.Busy) return;
            var fp = Fingerprint(_s.ActiveRegion);
            if (fp == _fingerprint) return;
            Cancel();
            _debounce.Stop();
            _debounce.Start();
        }

        public void Cancel()
        {
            _cts?.Cancel();
            _cts = null;
            if (Running) SetRunning(false);
        }

        /// <summary>Runs the exploration now (force = even if nothing changed since the last run).</summary>
        public void Start(bool force)
        {
            var r = _s.ActiveRegion;
            if (!_s.HasMesh || r == null || r.TriangleCount < 3) return;
            var fp = Fingerprint(r);
            if (!force && fp == _fingerprint) return;
            _fingerprint = fp;
            Cancel();
            var cts = new CancellationTokenSource();
            _cts = cts;
            var mesh = _s.Mesh;
            var owners = (int[])_s.Regions.Owners.Clone();
            var opt = _s.FitOptions.Clone();
            var curvature = _s.StopAtFillets ? _s.Curvature : null;
            double blend = _s.BlendRadius;
            opt.Request = r.Request;
            var ui = SynchronizationContext.Current;
            SetRunning(true);
            Task.Run(() => SelectionOptimizer.Explore(mesh, owners, r.Id, opt, cts.Token, curvature, blend), cts.Token)
                .ContinueWith(t =>
                {
                    void Done()
                    {
                        if (_cts == cts) { _cts = null; SetRunning(false); }
                        if (t.IsCanceled || t.IsFaulted || cts.IsCancellationRequested) return;
                        // Ignore results for a selection that has changed meanwhile.
                        if (Fingerprint(_s.ActiveRegion) != fp) return;
                        var list = t.Result;
                        Suggested?.Invoke(r, IsBetter(r, list) || force ? list : new List<SelectionCandidate>());
                    }
                    if (ui != null) ui.Post(_ => Guarded.Run("suggestions", Done), null); else Guarded.Run("suggestions", Done);
                });
        }

        /// <summary>True when the best candidate differs meaningfully from the painted selection.</summary>
        private static bool IsBetter(Region r, List<SelectionCandidate> list)
        {
            if (list.Count == 0) return false;
            var best = list[0];
            var painted = list.FirstOrDefault(c => c.Description == "As painted");
            if (!best.Valid || best == painted) return false;
            if (painted == null || !painted.Valid) return true;
            double change = Math.Abs(best.Triangles.Length - painted.Triangles.Length);
            return change > Math.Max(20, 0.02 * painted.Triangles.Length)
                   || best.Fit.Stats.Rms < 0.8 * painted.Fit.Stats.Rms;
        }

        private void SetRunning(bool v)
        {
            Running = v;
            RunningChanged?.Invoke(v);
        }

        public void Dispose()
        {
            Cancel();
            _debounce.Dispose();
        }
    }
}
