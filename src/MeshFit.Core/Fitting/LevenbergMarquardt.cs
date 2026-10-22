using System;
using MeshFit.Core.Geometry;

namespace MeshFit.Core.Fitting
{
    /// <summary>
    /// Model state for non-linear least squares. States are immutable; <see cref="Step"/> returns a new
    /// state displaced by a local parameter increment (lets each model use a well-conditioned local
    /// parameterisation, e.g. axis tilts in the tangent plane of the current axis).
    /// </summary>
    public interface ILsqState
    {
        int ParameterCount { get; }
        ILsqState Step(double[] delta);
        double Residual(Vec3 p);
    }

    public static class LevenbergMarquardt
    {
        /// <param name="scale">Characteristic length of the data, used for finite difference steps.</param>
        public static ILsqState Minimize(ILsqState state, Vec3[] pts, double[] weights, double scale,
                                         int maxIterations = 100)
        {
            int m = pts.Length, n = state.ParameterCount;
            var r = new double[m];
            double cost = Evaluate(state, pts, weights, r);
            if (double.IsNaN(cost)) return state;
            double lambda = 1e-3;
            var J = new double[m, n];
            var rj = new double[m];
            double h = 1e-7 * Math.Max(scale, 1e-12);

            for (int iter = 0; iter < maxIterations; iter++)
            {
                for (int j = 0; j < n; j++)
                {
                    var d = new double[n];
                    d[j] = h;
                    var sj = state.Step(d);
                    Evaluate(sj, pts, weights, rj);
                    for (int i = 0; i < m; i++) J[i, j] = (rj[i] - r[i]) / h;
                }
                var A = new double[n, n];
                var g = new double[n];
                for (int i = 0; i < m; i++)
                    for (int a = 0; a < n; a++)
                    {
                        double ja = J[i, a];
                        g[a] += ja * r[i];
                        for (int b = 0; b <= a; b++) A[a, b] += ja * J[i, b];
                    }
                for (int a = 0; a < n; a++) for (int b = 0; b < a; b++) A[b, a] = A[a, b];

                bool improved = false;
                for (int tries = 0; tries < 12; tries++)
                {
                    var M = (double[,])A.Clone();
                    var rhs = new double[n];
                    for (int a = 0; a < n; a++) { M[a, a] += lambda * Math.Max(A[a, a], 1e-12); rhs[a] = -g[a]; }
                    var delta = LinearAlgebra.SolveSymmetric(M, rhs);
                    if (delta == null) { lambda *= 10; continue; }
                    var cand = state.Step(delta);
                    double c2 = Evaluate(cand, pts, weights, rj);
                    if (!double.IsNaN(c2) && c2 < cost)
                    {
                        double rel = (cost - c2) / Math.Max(cost, 1e-300);
                        state = cand; Array.Copy(rj, r, m); cost = c2;
                        lambda = Math.Max(lambda / 3, 1e-12);
                        improved = true;
                        if (rel < 1e-12) return state;
                        break;
                    }
                    lambda *= 4;
                }
                if (!improved) break;
            }
            return state;
        }

        public static double Evaluate(ILsqState s, Vec3[] pts, double[] w, double[] r)
        {
            double c = 0;
            for (int i = 0; i < pts.Length; i++)
            {
                double ri = s.Residual(pts[i]);
                if (w != null) ri *= Math.Sqrt(w[i]);
                r[i] = ri;
                c += ri * ri;
            }
            return c;
        }
    }
}
