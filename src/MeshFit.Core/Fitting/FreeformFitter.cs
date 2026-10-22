using System;
using System.Collections.Generic;
using MeshFit.Core.Geometry;

namespace MeshFit.Core.Fitting
{
    public sealed class FreeformOptions
    {
        public int Degree { get; set; } = 3;
        /// <summary>Control points in U / V. 0 = choose automatically to meet <see cref="Tolerance"/>.</summary>
        public int ControlPointsU { get; set; }
        public int ControlPointsV { get; set; }
        public int MaxControlPoints { get; set; } = 40;
        /// <summary>Bending energy weight (dimensionless). Larger = smoother, less faithful.</summary>
        public double Smoothing { get; set; } = 1e-10;
        /// <summary>Extension of the patch beyond the painted region, as a fraction of its size.</summary>
        public double Margin { get; set; } = 0.05;
        /// <summary>Target deviation (model units) used by the automatic control point selection.</summary>
        public double Tolerance { get; set; } = 0.1;
        /// <summary>Parameter correction (re-projection) passes.</summary>
        public int ParameterCorrections { get; set; } = 3;

        public FreeformOptions Clone() => (FreeformOptions)MemberwiseClone();
    }

    /// <summary>
    /// Least-squares B-spline surface approximation with thin-plate style smoothing and parameter
    /// correction. Points are initially parameterised by projection on a base plane, so a single patch
    /// must be a height field over that plane (true for typical body panel regions).
    /// </summary>
    public static class FreeformFitter
    {
        public static FreeformSurface Fit(Vec3[] pts, Vec3[] normals, FreeformOptions opt, out string warning)
        {
            warning = null;
            if (pts.Length < 16) throw new ArgumentException("At least 16 points are needed for a freeform fit.");
            var frame = BaseFrame(pts, normals, out double minDot);
            if (minDot < 0.15)
                warning = "Region folds over relative to its base plane; split it into smaller regions for a reliable freeform fit.";

            var uv = new double[pts.Length, 2];
            double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
            for (int i = 0; i < pts.Length; i++)
            {
                var l = frame.ToLocal(pts[i]);
                uv[i, 0] = l.X; uv[i, 1] = l.Y;
                u0 = Math.Min(u0, l.X); u1 = Math.Max(u1, l.X); v0 = Math.Min(v0, l.Y); v1 = Math.Max(v1, l.Y);
            }
            double du = Math.Max(u1 - u0, 1e-9), dv = Math.Max(v1 - v0, 1e-9);
            double mu = Math.Max(opt.Margin * du, 1e-6 * du), mv = Math.Max(opt.Margin * dv, 1e-6 * dv);
            u0 -= mu; u1 += mu; v0 -= mv; v1 += mv;

            int p = Math.Max(1, Math.Min(5, opt.Degree));
            FreeformSurface result = null;
            if (opt.ControlPointsU > 0 && opt.ControlPointsV > 0)
            {
                result = FitFixed(pts, (double[,])uv.Clone(), frame, p, Math.Max(p + 1, opt.ControlPointsU),
                    Math.Max(p + 1, opt.ControlPointsV), u0, u1, v0, v1, opt);
            }
            else
            {
                // Grow the control net until the tolerance is met.
                double aspect = (u1 - u0) / (v1 - v0);
                double baseN = p + 1;
                for (int step = 0; step < 12; step++)
                {
                    double n = baseN * Math.Pow(1.35, step);
                    int nu = (int)Math.Round(n * Math.Sqrt(Math.Max(aspect, 1)));
                    int nv = (int)Math.Round(n / Math.Sqrt(Math.Min(aspect, 1)));
                    nu = Math.Max(p + 1, Math.Min(opt.MaxControlPoints, nu));
                    nv = Math.Max(p + 1, Math.Min(opt.MaxControlPoints, nv));
                    // Don't use more unknowns than the data supports.
                    if (nu * nv > pts.Length / 2 && result != null) break;
                    result = FitFixed(pts, (double[,])uv.Clone(), frame, p, nu, nv, u0, u1, v0, v1, opt);
                    double max = 0;
                    foreach (var q in pts) max = Math.Max(max, Math.Abs(result.Distance(q)));
                    if (max <= opt.Tolerance) break;
                    if (nu >= opt.MaxControlPoints && nv >= opt.MaxControlPoints) break;
                }
            }
            return result;
        }

        /// <summary>
        /// Base plane: average normal when the normals agree (height-field direction), otherwise PCA.
        /// X axis along the principal direction of the projected points for a tight parameter rectangle.
        /// </summary>
        public static Frame BaseFrame(Vec3[] pts, Vec3[] normals, out double minDot)
        {
            var c = AnalyticFitters.Centroid(pts);
            AnalyticFitters.Pca(pts, c, out _, out var pv);
            Vec3 z = pv[0];
            if (normals != null && normals.Length > 0)
            {
                Vec3 avg = Vec3.Zero;
                foreach (var n in normals) avg += n;
                avg /= normals.Length;
                if (avg.Length > 0.5) z = avg.Normalized();
                else if (avg.Dot(z) < 0) z = -z;
            }
            minDot = 1;
            if (normals != null) foreach (var n in normals) minDot = Math.Min(minDot, n.Dot(z));

            var f0 = Frame.FromAxis(c, z);
            double sxx = 0, sxy = 0, syy = 0;
            foreach (var q in pts)
            {
                var l = f0.ToLocal(q);
                sxx += l.X * l.X; sxy += l.X * l.Y; syy += l.Y * l.Y;
            }
            double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            var xdir = f0.XAxis * Math.Cos(ang) + f0.YAxis * Math.Sin(ang);
            return new Frame(c, z, xdir);
        }

        private static FreeformSurface FitFixed(Vec3[] pts, double[,] uv, Frame frame, int p, int nu, int nv,
                                                double u0, double u1, double v0, double v1, FreeformOptions opt)
        {
            var ku = BSplineBasis.ClampedUniformKnots(nu, p, u0, u1);
            var kv = BSplineBasis.ClampedUniformKnots(nv, p, v0, v1);
            FreeformSurface surf = null;
            for (int pass = 0; pass <= Math.Max(0, opt.ParameterCorrections); pass++)
            {
                var ctrl = Solve(pts, uv, p, nu, nv, ku, kv, u0, u1, v0, v1, opt.Smoothing);
                if (ctrl == null) break;
                surf = new FreeformSurface(new NurbsSurface(p, p, ku, kv, ctrl), frame);
                if (pass == opt.ParameterCorrections) break;
                for (int i = 0; i < pts.Length; i++)
                {
                    double u = uv[i, 0], v = uv[i, 1];
                    surf.Nurbs.Project(pts[i], ref u, ref v, 6);
                    uv[i, 0] = u; uv[i, 1] = v;
                }
            }
            return surf;
        }

        private static Vec3[,] Solve(Vec3[] pts, double[,] uv, int p, int nu, int nv, double[] ku, double[] kv,
                                     double u0, double u1, double v0, double v1, double smoothing)
        {
            int n = nu * nv;
            int reach = Math.Max(p, 2);
            var A = new BandedSymmetricMatrix(n, reach * nv + reach);
            var bx = new double[n]; var by = new double[n]; var bz = new double[n];
            var idx = new int[(p + 1) * (p + 1)];
            var val = new double[(p + 1) * (p + 1)];
            double invN = 1.0 / pts.Length;

            for (int s = 0; s < pts.Length; s++)
            {
                double u = uv[s, 0], v = uv[s, 1];
                int su = BSplineBasis.FindSpan(nu - 1, p, u, ku);
                int sv = BSplineBasis.FindSpan(nv - 1, p, v, kv);
                var bu = BSplineBasis.DersBasisFuns(su, u, p, 0, ku);
                var bv = BSplineBasis.DersBasisFuns(sv, v, p, 0, kv);
                int c = 0;
                for (int i = 0; i <= p; i++)
                    for (int j = 0; j <= p; j++)
                    {
                        idx[c] = (su - p + i) * nv + (sv - p + j);
                        val[c] = bu[0, i] * bv[0, j];
                        c++;
                    }
                var q = pts[s];
                for (int a = 0; a < c; a++)
                {
                    double wa = val[a] * invN;
                    bx[idx[a]] += wa * q.X; by[idx[a]] += wa * q.Y; bz[idx[a]] += wa * q.Z;
                    // Symmetric storage: each unordered pair of basis functions is added once.
                    for (int b = 0; b <= a; b++) A.Add(idx[a], idx[b], wa * val[b]);
                }
            }
            // Bending energy on the control net (discrete thin plate), resolution independent.
            double hu = (u1 - u0) / (nu - 1), hv = (v1 - v0) / (nv - 1);
            double area = (u1 - u0) * (v1 - v0);
            double lam = smoothing * area;
            double wuu = lam * hv / (hu * hu * hu), wvv = lam * hu / (hv * hv * hv), wuv = lam * 2 / (hu * hv);
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    if (i + 2 < nu) AddStencil(A, new[] { K(i, j, nv), K(i + 1, j, nv), K(i + 2, j, nv) }, new[] { 1.0, -2, 1 }, wuu);
                    if (j + 2 < nv) AddStencil(A, new[] { K(i, j, nv), K(i, j + 1, nv), K(i, j + 2, nv) }, new[] { 1.0, -2, 1 }, wvv);
                    if (i + 1 < nu && j + 1 < nv)
                        AddStencil(A, new[] { K(i, j, nv), K(i + 1, j, nv), K(i, j + 1, nv), K(i + 1, j + 1, nv) },
                                   new[] { 1.0, -1, -1, 1 }, wuv);
                }
            A.AddDiagonal(1e-12 * A.Trace() / n + 1e-300);
            if (!A.Factor()) return null;
            A.Solve(bx); A.Solve(by); A.Solve(bz);
            var ctrl = new Vec3[nu, nv];
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++) ctrl[i, j] = new Vec3(bx[K(i, j, nv)], by[K(i, j, nv)], bz[K(i, j, nv)]);
            return ctrl;
        }

        private static int K(int i, int j, int nv) => i * nv + j;

        private static void AddStencil(BandedSymmetricMatrix A, int[] k, double[] c, double w)
        {
            for (int a = 0; a < k.Length; a++)
                for (int b = 0; b < k.Length; b++)
                    if (k[a] >= k[b]) A.Add(k[a], k[b], w * c[a] * c[b]);
        }
    }
}
