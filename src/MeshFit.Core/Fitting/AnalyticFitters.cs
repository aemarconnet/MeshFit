using System;
using System.Collections.Generic;
using System.Linq;
using MeshFit.Core.Geometry;

namespace MeshFit.Core.Fitting
{
    /// <summary>Geometric (orthogonal distance) least-squares fitters for analytic surfaces.</summary>
    public static class AnalyticFitters
    {
        // ------------------------------------------------------------ helpers

        public static Vec3 Centroid(Vec3[] pts)
        {
            Vec3 c = Vec3.Zero;
            foreach (var p in pts) c += p;
            return c / Math.Max(1, pts.Length);
        }

        /// <summary>Covariance eigen decomposition (ascending eigenvalues).</summary>
        public static void Pca(Vec3[] pts, Vec3 center, out double[] values, out Vec3[] vectors, bool centered = true)
        {
            var c = new double[3, 3];
            foreach (var p0 in pts)
            {
                var p = centered ? p0 - center : p0;
                c[0, 0] += p.X * p.X; c[0, 1] += p.X * p.Y; c[0, 2] += p.X * p.Z;
                c[1, 1] += p.Y * p.Y; c[1, 2] += p.Y * p.Z; c[2, 2] += p.Z * p.Z;
            }
            c[1, 0] = c[0, 1]; c[2, 0] = c[0, 2]; c[2, 1] = c[1, 2];
            LinearAlgebra.SymmetricEigen3(c, out values, out vectors);
        }

        public static double Scale(Vec3[] pts)
        {
            var bb = BoundingBox.Empty;
            foreach (var p in pts) bb.Add(p);
            return Math.Max(bb.Diagonal, 1e-12);
        }

        /// <summary>Algebraic (Kåsa) circle fit in 2D. Returns false if degenerate.</summary>
        public static bool FitCircle2D(IList<double> xs, IList<double> ys, out double cx, out double cy, out double r)
        {
            int n = xs.Count;
            double mx = xs.Average(), my = ys.Average();
            var rows = new double[n][];
            var rhs = new double[n];
            for (int i = 0; i < n; i++)
            {
                double x = xs[i] - mx, y = ys[i] - my;
                rows[i] = new[] { x, y, 1.0 };
                rhs[i] = -(x * x + y * y);
            }
            var s = LinearAlgebra.LeastSquares(rows, rhs, 3);
            cx = cy = r = 0;
            if (s == null) return false;
            cx = -s[0] / 2; cy = -s[1] / 2;
            double r2 = cx * cx + cy * cy - s[2];
            if (r2 <= 0 || double.IsNaN(r2)) return false;
            r = Math.Sqrt(r2);
            cx += mx; cy += my;
            return true;
        }

        /// <summary>Unit vector perpendicular to the axis pointing from the axis toward the data.</summary>
        private static Vec3 TowardData(Vec3[] pts, Vec3 axisPoint, Vec3 axis)
        {
            var d = Centroid(pts) - axisPoint;
            d -= axis * d.Dot(axis);
            return d.Length > 1e-12 ? d.Normalized() : axis.AnyPerpendicular();
        }

        private static (Vec3 e1, Vec3 e2) Tangents(Vec3 a)
        {
            var e1 = a.AnyPerpendicular();
            return (e1, a.Cross(e1));
        }

        private static Vec3 Tilt(Vec3 a, double d1, double d2)
        {
            var (e1, e2) = Tangents(a);
            return (a + e1 * d1 + e2 * d2).Normalized();
        }

        // ------------------------------------------------------------ plane

        public static PlaneSurface FitPlane(Vec3[] pts, Vec3[] normals = null)
        {
            var c = Centroid(pts);
            Pca(pts, c, out _, out var vecs);
            var n = vecs[0];
            if (normals != null && normals.Length > 0)
            {
                Vec3 avg = Vec3.Zero;
                foreach (var nn in normals) avg += nn;
                if (avg.Dot(n) < 0) n = -n; // orient like the mesh
            }
            return new PlaneSurface(c, n, vecs[2]);
        }

        // ------------------------------------------------------------ sphere

        private sealed class SphereState : ILsqState
        {
            public Vec3 C; public double R;
            public int ParameterCount => 4;
            public ILsqState Step(double[] d) => new SphereState { C = C + new Vec3(d[0], d[1], d[2]), R = R + d[3] };
            public double Residual(Vec3 p) => (p - C).Length - R;
        }

        public static SphereSurface FitSphere(Vec3[] pts, double[] weights = null)
        {
            var m = Centroid(pts);
            var rows = new double[pts.Length][];
            var rhs = new double[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                var p = pts[i] - m;
                rows[i] = new[] { p.X, p.Y, p.Z, 1.0 };
                rhs[i] = -p.LengthSquared;
            }
            var s = LinearAlgebra.LeastSquares(rows, rhs, 4);
            if (s == null) return null;
            var c = new Vec3(-s[0] / 2, -s[1] / 2, -s[2] / 2);
            double r2 = c.LengthSquared - s[3];
            if (r2 <= 0) return null;
            var st = new SphereState { C = c + m, R = Math.Sqrt(r2) };
            st = (SphereState)LevenbergMarquardt.Minimize(st, pts, weights, Scale(pts));
            if (!st.C.IsFinite || double.IsNaN(st.R)) return null;
            // Orient the frame so the region sits around longitude 0 / latitude 0 (away from the poles).
            var toData = (Centroid(pts) - st.C).Normalized();
            var axis = toData.AnyPerpendicular();
            return new SphereSurface(st.C, st.R, new Frame(st.C, axis, toData));
        }

        // ------------------------------------------------------------ cylinder

        private sealed class CylinderState : ILsqState
        {
            public Vec3 P, A; public double R;
            public int ParameterCount => 5;
            public ILsqState Step(double[] d)
            {
                var (e1, e2) = Tangents(A);
                return new CylinderState { A = Tilt(A, d[0], d[1]), P = P + e1 * d[2] + e2 * d[3], R = R + d[4] };
            }
            public double Residual(Vec3 p)
            {
                var d = p - P;
                return (d - A * d.Dot(A)).Length - R;
            }
        }

        private static CylinderState CylinderFromAxis(Vec3[] pts, Vec3 axis)
        {
            var f = Frame.FromAxis(Centroid(pts), axis);
            var xs = new double[pts.Length]; var ys = new double[pts.Length];
            for (int i = 0; i < pts.Length; i++) { var l = f.ToLocal(pts[i]); xs[i] = l.X; ys[i] = l.Y; }
            if (!FitCircle2D(xs, ys, out double cx, out double cy, out double r)) return null;
            return new CylinderState { P = f.ToWorld(cx, cy, 0), A = f.ZAxis, R = r };
        }

        /// <summary>Candidate axis directions from the normal field and the point distribution.</summary>
        private static List<Vec3> AxisCandidates(Vec3[] pts, Vec3[] normals, bool centeredNormals)
        {
            var cands = new List<Vec3>();
            if (normals != null && normals.Length >= 3)
            {
                Pca(normals, Centroid(normals), out _, out var nv, centeredNormals);
                cands.Add(nv[0]);
            }
            Pca(pts, Centroid(pts), out _, out var pv);
            cands.Add(pv[2]); cands.Add(pv[1]);
            return cands;
        }

        public static CylinderSurface FitCylinder(Vec3[] pts, Vec3[] normals, double[] weights = null)
        {
            var st = FitCylinderState(pts, normals, weights, out _);
            if (st == null) return null;
            return new CylinderSurface(st.P, st.A, st.R, TowardData(pts, st.P, st.A));
        }

        private static CylinderState FitCylinderState(Vec3[] pts, Vec3[] normals, double[] weights, out double bestCost)
        {
            double scale = Scale(pts);
            CylinderState best = null;
            bestCost = double.PositiveInfinity;
            var r = new double[pts.Length];
            foreach (var axis in AxisCandidates(pts, normals, false))
            {
                var st = CylinderFromAxis(pts, axis);
                if (st == null) continue;
                st = (CylinderState)LevenbergMarquardt.Minimize(st, pts, weights, scale);
                double c = LevenbergMarquardt.Evaluate(st, pts, weights, r);
                if (c < bestCost && st.R < 1e4 * scale && st.P.IsFinite) { bestCost = c; best = st; }
            }
            return best;
        }

        // ------------------------------------------------------------ cone

        private sealed class ConeState : ILsqState
        {
            public Vec3 C, A; public double R, Alpha;
            public int ParameterCount => 6;
            public ILsqState Step(double[] d)
            {
                var (e1, e2) = Tangents(A);
                return new ConeState { A = Tilt(A, d[0], d[1]), C = C + e1 * d[2] + e2 * d[3], R = R + d[4], Alpha = Alpha + d[5] };
            }
            public double Residual(Vec3 p)
            {
                var d = p - C;
                double h = d.Dot(A);
                double rho = (d - A * h).Length;
                return (rho - R) * Math.Cos(Alpha) - h * Math.Sin(Alpha);
            }
        }

        public static ConeSurface FitCone(Vec3[] pts, Vec3[] normals, double[] weights = null)
        {
            double scale = Scale(pts);
            var starts = new List<ConeState>();
            var cyl = FitCylinderState(pts, normals, weights, out _);
            if (cyl != null)
            {
                var c0 = cyl.P + cyl.A * (Centroid(pts) - cyl.P).Dot(cyl.A);
                starts.Add(new ConeState { C = c0, A = cyl.A, R = cyl.R, Alpha = 0.02 });
                starts.Add(new ConeState { C = c0, A = cyl.A, R = cyl.R, Alpha = -0.02 });
            }
            // Normal based start: n·a is constant on a cone, and every tangent plane contains the apex.
            if (normals != null && normals.Length >= 6)
            {
                Pca(normals, Centroid(normals), out _, out var nv);
                var a = nv[0];
                var rows = new double[normals.Length][];
                var rhs = new double[normals.Length];
                for (int i = 0; i < normals.Length; i++)
                {
                    rows[i] = new[] { normals[i].X, normals[i].Y, normals[i].Z };
                    rhs[i] = normals[i].Dot(pts[Math.Min(i, pts.Length - 1)]);
                }
                var apex = normals.Length == pts.Length ? LinearAlgebra.LeastSquares(rows, rhs, 3) : null;
                if (apex != null)
                {
                    var v = new Vec3(apex[0], apex[1], apex[2]);
                    var cen = Centroid(pts);
                    if ((cen - v).Dot(a) < 0) a = -a;
                    double hc = (cen - v).Dot(a);
                    double sumAng = 0;
                    foreach (var p in pts)
                    {
                        var d = p - v; double h = d.Dot(a);
                        sumAng += Math.Atan2((d - a * h).Length, h);
                    }
                    double alpha = sumAng / pts.Length;
                    if (hc > 0 && alpha > 0 && alpha < 1.5)
                        starts.Add(new ConeState { C = v + a * hc, A = a, R = hc * Math.Tan(alpha), Alpha = alpha });
                }
            }
            ConeState best = null;
            double bestCost = double.PositiveInfinity;
            var r = new double[pts.Length];
            foreach (var s0 in starts)
            {
                var st = (ConeState)LevenbergMarquardt.Minimize(s0, pts, weights, scale);
                double c = LevenbergMarquardt.Evaluate(st, pts, weights, r);
                if (c < bestCost && Math.Abs(st.Alpha) < 1.55 && st.C.IsFinite) { bestCost = c; best = st; }
            }
            if (best == null) return null;
            var a1 = best.A; double alpha1 = best.Alpha;
            if (alpha1 < 0) { a1 = -a1; alpha1 = -alpha1; }
            // Re-center the reference circle at the data centroid height.
            double hcen = (Centroid(pts) - best.C).Dot(a1);
            var center = best.C + a1 * hcen;
            double rad = best.R + hcen * Math.Tan(alpha1);
            return new ConeSurface(center, a1, rad, alpha1, TowardData(pts, center, a1));
        }

        // ------------------------------------------------------------ torus

        private sealed class TorusState : ILsqState
        {
            public Vec3 C, A; public double R, r;
            public int ParameterCount => 7;
            public ILsqState Step(double[] d) =>
                new TorusState { A = Tilt(A, d[0], d[1]), C = C + new Vec3(d[2], d[3], d[4]), R = R + d[5], r = r + d[6] };
            public double Residual(Vec3 p)
            {
                var d = p - C;
                double h = d.Dot(A);
                double rho = (d - A * h).Length;
                double x = rho - R;
                return Math.Sqrt(x * x + h * h) - r;
            }
        }

        public static TorusSurface FitTorus(Vec3[] pts, Vec3[] normals, double[] weights = null)
        {
            if (normals == null || normals.Length != pts.Length) return null;
            double scale = Scale(pts);
            TorusState bestStart = null;
            double bestCost = double.PositiveInfinity;
            var q = new Vec3[pts.Length];
            var xs = new double[pts.Length]; var ys = new double[pts.Length];
            // Offsetting the points inward by the minor radius puts them on the spine circle.
            for (int k = 0; k < 40; k++)
            {
                double rr = scale * 0.01 * Math.Pow(300.0, k / 39.0);
                foreach (int sign in new[] { 1, -1 })
                {
                    for (int i = 0; i < pts.Length; i++) q[i] = pts[i] - normals[i] * (sign * rr);
                    var c = Centroid(q);
                    Pca(q, c, out _, out var v);
                    var f = Frame.FromAxis(c, v[0]);
                    for (int i = 0; i < q.Length; i++) { var l = f.ToLocal(q[i]); xs[i] = l.X; ys[i] = l.Y; }
                    if (!FitCircle2D(xs, ys, out double cx, out double cy, out double R)) continue;
                    var st = new TorusState { C = f.ToWorld(cx, cy, 0), A = f.ZAxis, R = R, r = rr };
                    double cost = 0;
                    foreach (var p in pts) { double e = st.Residual(p); cost += e * e; }
                    if (cost < bestCost) { bestCost = cost; bestStart = st; }
                }
            }
            if (bestStart == null) return null;
            var best = (TorusState)LevenbergMarquardt.Minimize(bestStart, pts, weights, scale);
            if (!best.C.IsFinite || best.r <= 0) return null;
            return new TorusSurface(best.C, best.A, best.R, best.r, TowardData(pts, best.C, best.A));
        }
    }
}
