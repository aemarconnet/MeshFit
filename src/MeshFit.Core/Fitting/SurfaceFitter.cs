using System;
using System.Collections.Generic;
using System.Linq;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;

namespace MeshFit.Core.Fitting
{
    public enum FitRequest { Auto, Plane, Sphere, Cylinder, Cone, Torus, Freeform }

    public sealed class FitOptions
    {
        public FitRequest Request { get; set; } = FitRequest.Auto;
        /// <summary>Acceptable deviation in model units (also drives automatic type selection).</summary>
        public double Tolerance { get; set; } = 0.1;
        /// <summary>Discard points far from the first fit (painting spill-over, scan noise) and refit.</summary>
        public bool RejectOutliers { get; set; } = true;
        /// <summary>Points used by the analytic fitters (the region is subsampled above this).</summary>
        public int MaxPoints { get; set; } = 20000;
        public FreeformOptions Freeform { get; set; } = new FreeformOptions();
    }

    public sealed class FitStatistics
    {
        public int PointCount { get; set; }
        public double Rms { get; set; }
        public double MaxAbs { get; set; }
        public double Mean { get; set; }
        public double P95 { get; set; }
        /// <summary>Fraction of points within the tolerance.</summary>
        public double WithinTolerance { get; set; }
    }

    public sealed class FitResult
    {
        public FittedSurface Surface { get; set; }
        public FitStatistics Stats { get; set; }
        public string Warning { get; set; }
        /// <summary>All candidates evaluated in Auto mode, best first.</summary>
        public List<FitResult> Alternatives { get; set; } = new List<FitResult>();
        public override string ToString() => Surface == null ? "(failed)" :
            $"{Surface.Kind}: rms {Stats.Rms:G4}, max {Stats.MaxAbs:G4}";
    }

    /// <summary>Entry point: fits one or all surface types to a set of mesh triangles.</summary>
    public static class SurfaceFitter
    {
        private static readonly SurfaceKind[] AutoOrder =
            { SurfaceKind.Plane, SurfaceKind.Sphere, SurfaceKind.Cylinder, SurfaceKind.Cone, SurfaceKind.Torus, SurfaceKind.Freeform };

        public static FitResult Fit(TriMesh mesh, IEnumerable<int> triangles, FitOptions opt)
        {
            var verts = mesh.VerticesOf(triangles);
            if (verts.Length < 4) throw new ArgumentException("The region needs more triangles before it can be fitted.");
            var pts = verts.Select(i => mesh.Vertices[i]).ToArray();
            var nrm = verts.Select(i => mesh.VertexNormals[i]).ToArray();
            return Fit(pts, nrm, opt);
        }

        public static FitResult Fit(Vec3[] allPts, Vec3[] allNormals, FitOptions opt)
        {
            Subsample(allPts, allNormals, opt.MaxPoints, out var pts, out var nrm);
            if (opt.Request != FitRequest.Auto)
            {
                var kind = (SurfaceKind)Enum.Parse(typeof(SurfaceKind), opt.Request.ToString());
                var r = FitKind(kind, pts, nrm, allPts, opt);
                if (r == null) throw new InvalidOperationException($"{kind} fit failed for this region.");
                return r;
            }

            var results = new List<FitResult>();
            foreach (var kind in AutoOrder)
            {
                try
                {
                    var r = FitKind(kind, pts, nrm, allPts, opt);
                    if (r != null) results.Add(r);
                }
                catch (ArgumentException) { /* not enough data for this type */ }
            }
            if (results.Count == 0) throw new InvalidOperationException("No surface type could be fitted to this region.");

            // Simplest surface whose 95th percentile deviation meets the tolerance; otherwise the most accurate.
            var ok = results.Where(r => r.Stats.P95 <= opt.Tolerance && IsSane(r.Surface, allPts)).ToList();
            FitResult best = ok.Count > 0
                ? ok.OrderBy(r => Array.IndexOf(AutoOrder, r.Surface.Kind)).First()
                : results.OrderBy(r => r.Stats.Rms).First();
            best.Alternatives = results.OrderBy(r => r == best ? 0 : 1).ThenBy(r => r.Stats.Rms).ToList();
            return best;
        }

        /// <summary>Rejects analytic fits that only match because they are degenerate (e.g. huge radii).</summary>
        private static bool IsSane(FittedSurface s, Vec3[] pts)
        {
            double size = AnalyticFitters.Scale(pts);
            switch (s)
            {
                case SphereSurface sp: return sp.Radius < 50 * size;
                case CylinderSurface cy: return cy.Radius < 50 * size;
                case ConeSurface co: return co.HalfAngle > 0.5 * Math.PI / 180 && Math.Abs(co.Radius) < 50 * size;
                case TorusSurface to: return to.MajorRadius > 0.01 * size && to.MajorRadius < 50 * size && to.MinorRadius < 50 * size;
                default: return true;
            }
        }

        private static FitResult FitKind(SurfaceKind kind, Vec3[] pts, Vec3[] nrm, Vec3[] allPts, FitOptions opt)
        {
            string warning = null;
            FittedSurface s;
            if (kind == SurfaceKind.Freeform)
            {
                var fo = opt.Freeform;
                fo.Tolerance = opt.Tolerance;
                s = FreeformFitter.Fit(pts, nrm, fo, out warning);
            }
            else
            {
                s = FitAnalytic(kind, pts, nrm);
                if (s != null && opt.RejectOutliers && pts.Length > 20)
                {
                    var res = pts.Select(p => Math.Abs(s.Distance(p))).ToArray();
                    double mad = Median(res) * 1.4826;
                    double thr = Math.Max(3 * mad, 0.5 * opt.Tolerance);
                    var keep = Enumerable.Range(0, pts.Length).Where(i => res[i] <= thr).ToArray();
                    if (keep.Length >= pts.Length / 2 && keep.Length < pts.Length)
                    {
                        var s2 = FitAnalytic(kind, keep.Select(i => pts[i]).ToArray(), keep.Select(i => nrm[i]).ToArray());
                        if (s2 != null) s = s2;
                    }
                }
            }
            if (s == null) return null;
            return new FitResult { Surface = s, Stats = Evaluate(s, allPts, opt.Tolerance), Warning = warning };
        }

        private static FittedSurface FitAnalytic(SurfaceKind kind, Vec3[] pts, Vec3[] nrm)
        {
            switch (kind)
            {
                case SurfaceKind.Plane: return AnalyticFitters.FitPlane(pts, nrm);
                case SurfaceKind.Sphere: return AnalyticFitters.FitSphere(pts);
                case SurfaceKind.Cylinder: return AnalyticFitters.FitCylinder(pts, nrm);
                case SurfaceKind.Cone: return AnalyticFitters.FitCone(pts, nrm);
                case SurfaceKind.Torus: return AnalyticFitters.FitTorus(pts, nrm);
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        public static FitStatistics Evaluate(FittedSurface s, IReadOnlyList<Vec3> pts, double tolerance)
        {
            var d = new double[pts.Count];
            double sum = 0, sum2 = 0, max = 0;
            int within = 0;
            for (int i = 0; i < d.Length; i++)
            {
                double e = s.Distance(pts[i]);
                d[i] = Math.Abs(e);
                sum += e; sum2 += e * e; max = Math.Max(max, d[i]);
                if (d[i] <= tolerance) within++;
            }
            Array.Sort(d);
            int n = Math.Max(1, d.Length);
            return new FitStatistics
            {
                PointCount = d.Length,
                Rms = Math.Sqrt(sum2 / n),
                Mean = sum / n,
                MaxAbs = max,
                P95 = d.Length == 0 ? 0 : d[Math.Min(d.Length - 1, (int)(0.95 * d.Length))],
                WithinTolerance = (double)within / n
            };
        }

        private static double Median(double[] v)
        {
            var c = (double[])v.Clone();
            Array.Sort(c);
            return c.Length == 0 ? 0 : c[c.Length / 2];
        }

        private static void Subsample(Vec3[] pts, Vec3[] nrm, int max, out Vec3[] p, out Vec3[] n)
        {
            if (max <= 0 || pts.Length <= max) { p = pts; n = nrm; return; }
            double step = (double)pts.Length / max;
            p = new Vec3[max]; n = new Vec3[max];
            for (int i = 0; i < max; i++)
            {
                int k = (int)(i * step);
                p[i] = pts[k]; n[i] = nrm[k];
            }
        }
    }
}
