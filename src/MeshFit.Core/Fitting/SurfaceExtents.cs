using System;
using System.Collections.Generic;
using MeshFit.Core.Geometry;

namespace MeshFit.Core.Fitting
{
    /// <summary>Parameter-space rectangle of an analytic surface covering a region (plus margin).</summary>
    public struct Extents
    {
        public double S0, S1, T0, T1;
        public override string ToString() => $"s[{S0:G5}, {S1:G5}] t[{T0:G5}, {T1:G5}]";
    }

    public static class SurfaceExtents
    {
        /// <param name="margin">Extension as a fraction of each parameter range.</param>
        public static Extents Compute(AnalyticSurface surf, IReadOnlyList<Vec3> pts, double margin)
        {
            var ss = new double[pts.Count];
            var ts = new double[pts.Count];
            for (int i = 0; i < pts.Count; i++) surf.ParamOf(pts[i], out ss[i], out ts[i]);
            Range(ss, surf.PeriodicS, margin, out double s0, out double s1);
            Range(ts, surf.PeriodicT, margin, out double t0, out double t1);

            if (surf is SphereSurface)
            {
                t0 = Math.Max(t0, -Math.PI / 2); t1 = Math.Min(t1, Math.PI / 2);
            }
            else if (surf is ConeSurface cone)
            {
                // Stop short of the apex.
                double tApex = -cone.Radius / Math.Tan(Math.Max(cone.HalfAngle, 1e-12));
                double minT = tApex + 0.02 * Math.Abs(t1 - tApex);
                t0 = Math.Max(t0, minT);
            }
            return new Extents { S0 = s0, S1 = s1, T0 = t0, T1 = t1 };
        }

        private static void Range(double[] v, bool periodic, double margin, out double a, out double b)
        {
            if (v.Length == 0) { a = b = 0; return; }
            if (periodic)
            {
                AngleUtil.CoveringArc(v, out double start, out double span);
                if (span >= 2 * Math.PI - 1e-9) { a = -Math.PI; b = Math.PI; return; }
                double ext = Math.Min(span * margin, (2 * Math.PI - span) / 2 * 0.99);
                a = start - ext; b = start + span + ext;
                return;
            }
            a = double.MaxValue; b = double.MinValue;
            foreach (var x in v) { a = Math.Min(a, x); b = Math.Max(b, x); }
            double m = Math.Max((b - a) * margin, 1e-9);
            a -= m; b += m;
        }
    }
}
