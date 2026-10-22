using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshFit.Core.Fitting;
using MeshFit.Core.Mesh;

namespace MeshFit.Core.Selection
{
    /// <summary>One alternative selection for a region, with its fit.</summary>
    public sealed class SelectionCandidate
    {
        public string Description { get; set; }
        public int[] Triangles { get; set; }
        public FitResult Fit { get; set; }
        public double Area { get; set; }
        /// <summary>True when the fit meets the tolerance (95% of points within it).</summary>
        public bool Valid { get; set; }
        public override string ToString() => $"{Description}: {Triangles.Length} tris, {Fit}";
    }

    /// <summary>
    /// Explores alternative selections for a region and ranks them: the best selection is the largest
    /// area whose fit stays within tolerance. Candidates include
    /// <list type="bullet">
    /// <item>growing the painted triangles over each surface type (a few brush dots on a cylinder
    /// grow to the whole cylindrical face, like a CMM fitting a cylinder from a handful of probe points);</item>
    /// <item>shrinking the boundary first (drops fillet run-outs that bias the fit);</item>
    /// <item>trimming triangles that deviate from the fit, then growing;</item>
    /// <item>growing with tighter and looser tolerances.</item>
    /// </list>
    /// Works on a snapshot of the triangle ownership, so it can run in the background; candidates are
    /// evaluated in parallel.
    /// </summary>
    public static class SelectionOptimizer
    {
        private static readonly FitRequest[] AutoTypes =
            { FitRequest.Plane, FitRequest.Cylinder, FitRequest.Sphere, FitRequest.Cone, FitRequest.Torus };

        /// <param name="owners">Snapshot of triangle ownership (not modified).</param>
        /// <returns>Candidates, best first. The first entry describing the current selection is "As painted".</returns>
        /// <param name="curvature">When given with <paramref name="blendRadius"/>, growth stops at adjacent fillets.</param>
        public static List<SelectionCandidate> Explore(TriMesh mesh, int[] owners, int regionId, FitOptions options,
                                                       CancellationToken ct = default, Curvature curvature = null, double blendRadius = 0)
        {
            var baseMask = new bool[owners.Length];
            int baseCount = 0;
            for (int t = 0; t < owners.Length; t++) if (owners[t] == regionId) { baseMask[t] = true; baseCount++; }
            if (baseCount < 3) return new List<SelectionCandidate>();
            Func<int, bool> available = t => owners[t] == RegionSet.None || owners[t] == regionId;
            if (curvature != null && blendRadius > 0)
            {
                var feature = FeatureTools.IsFeature(curvature, curvature.MedianKMax(MaskOps.ToList(baseMask)), blendRadius);
                var free = available;
                available = t => free(t) && !feature(t);
            }
            double tol = options.Tolerance;

            var types = options.Request == FitRequest.Auto ? AutoTypes : new[] { options.Request };

            // Phase 1: the painted selection as is, and grown over each candidate surface type.
            var phase1 = new List<Func<SelectionCandidate>>
            {
                () => Evaluate(mesh, (bool[])baseMask.Clone(), options, options.Request, "As painted"),
            };
            foreach (var type in types)
            {
                var ty = type;
                phase1.Add(() => GrowLoop(mesh, (bool[])baseMask.Clone(), available, options, ty, tol, $"Grow as {ty}", ct));
            }
            var results = RunAll(phase1, ct);

            // Phase 2: refine only the two most promising types (boundary shrink, outlier trim, tolerance).
            var top = Rank(results.Where(r => r.Description != "As painted"), tol)
                      .Select(r => (FitRequest)Enum.Parse(typeof(FitRequest), r.Fit.Surface.Kind.ToString()))
                      .Distinct().Take(2).ToList();
            var phase2 = new List<Func<SelectionCandidate>>();
            foreach (var type in top)
            {
                var ty = type;
                if (baseCount > 200)
                {
                    phase2.Add(() =>
                    {
                        var m = (bool[])baseMask.Clone();
                        MaskOps.Shrink(mesh, m);
                        return GrowLoop(mesh, m, available, options, ty, tol, $"Shrink edge + grow as {ty}", ct);
                    });
                    phase2.Add(() => TrimThenGrow(mesh, baseMask, available, options, ty, tol, ct));
                }
            }
            if (top.Count > 0)
            {
                var best = top[0];
                foreach (double f in new[] { 0.5, 2.0 })
                {
                    double ff = f;
                    phase2.Add(() => GrowLoop(mesh, (bool[])baseMask.Clone(), available, options, best, tol * ff,
                        $"Grow as {best} with {ff}× tolerance", ct));
                }
            }
            results.AddRange(RunAll(phase2, ct));
            return Rank(results, tol);
        }

        private static List<SelectionCandidate> RunAll(List<Func<SelectionCandidate>> jobs, CancellationToken ct)
        {
            var results = new SelectionCandidate[jobs.Count];
            Parallel.For(0, jobs.Count, new ParallelOptions { CancellationToken = ct }, i =>
            {
                try { results[i] = jobs[i](); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { results[i] = null; } // a type that can't fit this data
            });
            ct.ThrowIfCancellationRequested();
            return results.Where(r => r != null).ToList();
        }

        /// <summary>Valid candidates by area (simpler type wins near-ties), then invalid ones by RMS.</summary>
        public static List<SelectionCandidate> Rank(IEnumerable<SelectionCandidate> cands, double tol)
        {
            var list = cands.ToList();
            foreach (var c in list) c.Valid = c.Fit.Stats.P95 <= tol;
            var valid = list.Where(c => c.Valid).ToList();
            var ordered = new List<SelectionCandidate>();
            while (valid.Count > 0)
            {
                double maxArea = valid.Max(c => c.Area);
                // Within 2% of the largest area, prefer the simplest surface, then the best balance of
                // coverage and accuracy (area weighted by how far the RMS stays below the tolerance).
                var best = valid.Where(c => c.Area >= 0.98 * maxArea)
                                .OrderBy(c => c.Fit.Surface.ParameterCount)
                                .ThenByDescending(c => c.Area * (1 - Math.Min(1, c.Fit.Stats.Rms / tol))).First();
                ordered.Add(best);
                valid.Remove(best);
            }
            ordered.AddRange(list.Where(c => !c.Valid).OrderBy(c => c.Fit.Stats.Rms));
            return ordered;
        }

        private static SelectionCandidate Evaluate(TriMesh mesh, bool[] mask, FitOptions options, FitRequest type, string name)
        {
            var o = options.Clone();
            o.Request = type;
            var tris = MaskOps.ToList(mask);
            var fit = SurfaceFitter.Fit(mesh, tris, o);
            return new SelectionCandidate { Description = name, Triangles = tris.ToArray(), Fit = fit, Area = MaskOps.Area(mesh, mask) };
        }

        /// <summary>Fit → grow → refit until the selection stops growing.</summary>
        private static SelectionCandidate GrowLoop(TriMesh mesh, bool[] mask, Func<int, bool> available, FitOptions options,
                                                   FitRequest type, double growTol, string name, CancellationToken ct)
        {
            var o = options.Clone();
            o.Request = type;
            FitResult fit = null;
            for (int pass = 0; pass < 8; pass++)
            {
                ct.ThrowIfCancellationRequested();
                var tris = MaskOps.ToList(mask);
                if (tris.Count < 3) return null;
                fit = SurfaceFitter.Fit(mesh, tris, o);
                int added = MaskOps.GrowToSurface(mesh, mask, available, fit.Surface, growTol);
                if (added <= Math.Max(3, tris.Count / 200)) break;
            }
            var final = MaskOps.ToList(mask);
            fit = SurfaceFitter.Fit(mesh, final, o);
            return new SelectionCandidate { Description = name, Triangles = final.ToArray(), Fit = fit, Area = MaskOps.Area(mesh, mask) };
        }

        /// <summary>Drop triangles with a vertex beyond 2.5σ of the first fit, keep the main piece, then grow.</summary>
        private static SelectionCandidate TrimThenGrow(TriMesh mesh, bool[] baseMask, Func<int, bool> available, FitOptions options,
                                                       FitRequest type, double tol, CancellationToken ct)
        {
            var o = options.Clone();
            o.Request = type;
            var fit = SurfaceFitter.Fit(mesh, MaskOps.ToList(baseMask), o);
            double limit = Math.Max(2.5 * fit.Stats.Rms, 0.25 * tol);
            var mask = (bool[])baseMask.Clone();
            for (int t = 0; t < mask.Length; t++)
            {
                if (!mask[t]) continue;
                for (int c = 0; c < 3; c++)
                    if (Math.Abs(fit.Surface.Distance(mesh.P(t, c))) > limit) { mask[t] = false; break; }
            }
            var comps = MaskOps.Components(mesh, mask);
            if (comps.Count == 0) return null;
            // Keep pieces of meaningful size (several separate picks are legitimate).
            int min = Math.Max(1, comps[0].Count / 20);
            var kept = new bool[mask.Length];
            foreach (var c in comps) if (c.Count >= min) foreach (int t in c) kept[t] = true;
            return GrowLoop(mesh, kept, available, options, type, tol, $"Trim outliers + grow as {type}", ct);
        }
    }
}
