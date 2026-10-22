using System;
using System.Collections.Generic;
using System.Linq;
using MeshFit.Core.Export;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using SolidWorks.Interop.sldworks;

namespace MeshFit.SolidWorks
{
    /// <summary>
    /// Turns fitted surfaces into SOLIDWORKS surface bodies/features. Analytic types become true
    /// planes, cylinders, cones, spheres and tori (so they behave like native faces for mates, sketches
    /// and dimensions); freeform fits become B-spline surfaces. Every created surface is checked against
    /// the fit by closest point queries, and alternative API conventions are tried when the check fails,
    /// falling back to an exact rational B-spline of the analytic surface.
    /// </summary>
    internal sealed class SwSurfaceBuilder
    {
        private const int Periodic = 13701, PeriodicNonSmooth = 13736;
        private readonly IModeler _modeler;

        public SwSurfaceBuilder(ISldWorks app)
        {
            _modeler = (IModeler)app.GetModeler();
        }

        /// <summary>Creates the surface feature in the part and returns it (renamed after the region).</summary>
        public Feature CreateFeature(IPartDoc part, FittedSurface surface, Vec3[] regionPoints, double margin, string name, out string note)
        {
            var body = CreateSheet(surface, regionPoints, margin, out note);
            if (body == null) throw new InvalidOperationException("SOLIDWORKS could not create a sheet body for this surface.");
            var feat = part.CreateFeatureFromBody3(body, true, 0) as Feature;
            if (feat == null) throw new InvalidOperationException("SOLIDWORKS refused to insert the surface body as a feature.");
            try { feat.Name = name; } catch { /* duplicate names are kept as generated */ }
            return feat;
        }

        public Body2 CreateSheet(FittedSurface s, Vec3[] regionPoints, double margin, out string note)
        {
            var projected = regionPoints.Select(s.Project).ToArray();
            double scale = Math.Max(AnalyticFitters.Scale(projected), 1e-6);
            if (s is AnalyticSurface a)
            {
                foreach (var surf in AnalyticCandidates(a))
                {
                    if (surf == null || !Matches(surf, projected, scale)) continue;
                    var range = UvRange(surf, projected, margin);
                    if (range == null) continue;
                    var body = _modeler.CreateSheetFromSurface(surf, range) as Body2;
                    if (body != null) { note = "analytic " + a.Kind.ToString().ToLowerInvariant(); return body; }
                }
                // Fallback: exact rational B-spline over the region extents.
                var ext = SurfaceExtents.Compute(a, projected, margin);
                var nb = CreateBspline(NurbsConversion.ToNurbs(a, ext), scale);
                note = "rational B-spline (exact " + a.Kind.ToString().ToLowerInvariant() + ")";
                return nb == null ? null : SheetOverFullRange(nb);
            }
            var ff = (FreeformSurface)s;
            var bs = CreateBspline(ff.Nurbs, scale);
            note = $"B-spline {ff.Nurbs.CountU}x{ff.Nurbs.CountV}";
            return bs == null ? null : SheetOverFullRange(bs);
        }

        // ------------------------------------------------------------------ analytic surfaces

        private IEnumerable<Surface> AnalyticCandidates(AnalyticSurface a)
        {
            var f = a.Frame;
            switch (a)
            {
                case PlaneSurface p:
                    yield return _modeler.CreatePlanarSurface2(p.Origin.ToArray(), p.Normal.ToArray(), f.XAxis.ToArray()) as Surface;
                    break;
                case CylinderSurface c:
                    yield return _modeler.CreateCylindricalSurface2(c.AxisPoint.ToArray(), c.Axis.ToArray(), f.XAxis.ToArray(), c.Radius) as Surface;
                    break;
                case ConeSurface k:
                    yield return _modeler.CreateConicalSurface2(k.Center.ToArray(), k.Axis.ToArray(), f.XAxis.ToArray(), k.Radius, k.HalfAngle) as Surface;
                    yield return _modeler.CreateConicalSurface2(k.Center.ToArray(), (-k.Axis).ToArray(), f.XAxis.ToArray(), k.Radius, k.HalfAngle) as Surface;
                    yield return _modeler.CreateConicalSurface2(k.Center.ToArray(), (-k.Axis).ToArray(), f.XAxis.ToArray(), k.Radius, -k.HalfAngle) as Surface;
                    break;
                case SphereSurface sp:
                    yield return _modeler.CreateSphericalSurface2(sp.Center.ToArray(), f.ZAxis.ToArray(), f.XAxis.ToArray(), sp.Radius) as Surface;
                    break;
                case TorusSurface t:
                    yield return _modeler.CreateToroidalSurface(t.Center.ToArray(), t.Axis.ToArray(), f.XAxis.ToArray(), t.MajorRadius, t.MinorRadius) as Surface;
                    break;
            }
        }

        /// <summary>True when the SOLIDWORKS surface passes through the sample points.</summary>
        private static bool Matches(Surface surf, Vec3[] pts, double scale)
        {
            double tol = Math.Max(1e-7, 1e-6 * scale);
            int step = Math.Max(1, pts.Length / 40);
            for (int i = 0; i < pts.Length; i += step)
            {
                var c = surf.GetClosestPointOn(pts[i].X, pts[i].Y, pts[i].Z) as double[];
                if (c == null || c.Length < 3) return false;
                if (new Vec3(c[0], c[1], c[2]).DistanceTo(pts[i]) > tol) return false;
            }
            return true;
        }

        /// <summary>UV box [u0, u1, v0, v1] covering the points, with periodic directions unwrapped.</summary>
        private static double[] UvRange(Surface surf, Vec3[] pts, double margin)
        {
            var par = surf.Parameterization2();
            int step = Math.Max(1, pts.Length / 500);
            var us = new List<double>(); var vs = new List<double>();
            for (int i = 0; i < pts.Length; i += step)
            {
                var uv = surf.ReverseEvaluate(pts[i].X, pts[i].Y, pts[i].Z) as double[];
                if (uv == null || uv.Length < 2) continue;
                us.Add(uv[0]); vs.Add(uv[1]);
            }
            if (us.Count < 3) return null;
            bool pu = par.UMinBoundType == Periodic || par.UMinBoundType == PeriodicNonSmooth;
            bool pv = par.VMinBoundType == Periodic || par.VMinBoundType == PeriodicNonSmooth;
            Span(us, pu, par.UMin, par.UMax, margin, out double u0, out double u1);
            Span(vs, pv, par.VMin, par.VMax, margin, out double v0, out double v1);
            return new[] { u0, u1, v0, v1 };
        }

        private static void Span(List<double> vals, bool periodic, double min, double max, double margin, out double a, out double b)
        {
            if (periodic)
            {
                double period = max - min;
                var ang = vals.Select(x => (x - min) / period * 2 * Math.PI).ToList();
                AngleUtil.CoveringArc(ang, out double start, out double span);
                double ext = span * margin;
                if (span + 2 * ext >= 2 * Math.PI) { a = min; b = max; return; }
                a = min + (start - ext) / (2 * Math.PI) * period;
                b = min + (start + span + ext) / (2 * Math.PI) * period;
                return;
            }
            a = vals.Min(); b = vals.Max();
            double m = Math.Max((b - a) * margin, 1e-9);
            a -= m; b += m;
            // Bounded (non-infinite) parameter ranges, e.g. sphere latitude, must not be exceeded.
            if (!double.IsInfinity(min) && Math.Abs(min) < 1e30) a = Math.Max(a, min);
            if (!double.IsInfinity(max) && Math.Abs(max) < 1e30) b = Math.Min(b, max);
        }

        // ------------------------------------------------------------------ B-splines

        private Body2 SheetOverFullRange(Surface surf)
        {
            var p = surf.Parameterization2();
            return _modeler.CreateSheetFromSurface(surf, new[] { p.UMin, p.UMax, p.VMin, p.VMax }) as Body2;
        }

        /// <summary>
        /// IModeler::CreateBsplineSurface. The documented Props are 8 integers packed into 4 doubles:
        /// (UOrder, VOrder), (numV, numU), (UPeriodic, VPeriodic), (dimension, 0). The control point
        /// ordering and rational weight convention are not spelled out, so the variants are tried and
        /// the first one that reproduces the surface is kept.
        /// </summary>
        private Surface CreateBspline(NurbsSurface n, double scale)
        {
            var check = new List<Vec3>();
            for (int i = 0; i <= 6; i++)
                for (int j = 0; j <= 6; j++)
                    check.Add(n.Evaluate(n.UMin + (n.UMax - n.UMin) * i / 6, n.VMin + (n.VMax - n.VMin) * j / 6));
            var checkArr = check.ToArray();

            foreach (bool vFastest in new[] { true, false })
                foreach (bool swapCounts in new[] { false, true })
                    foreach (bool premultiplied in n.IsRational ? new[] { false, true } : new[] { false })
                    {
                        Surface s;
                        try { s = TryBspline(n, vFastest, swapCounts, premultiplied); }
                        catch { continue; }
                        if (s != null && Matches(s, checkArr, scale)) return s;
                    }
            return null;
        }

        private Surface TryBspline(NurbsSurface n, bool vFastest, bool swapCounts, bool premultiplied)
        {
            int nu = n.CountU, nv = n.CountV, dim = n.IsRational ? 4 : 3;
            var props = new[]
            {
                Pack(n.DegreeU + 1, n.DegreeV + 1),
                swapCounts ? Pack(nu, nv) : Pack(nv, nu),
                Pack(0, 0),
                Pack(dim, 0)
            };
            var coords = new double[nu * nv * dim];
            int o = 0;
            for (int a = 0; a < (vFastest ? nu : nv); a++)
                for (int b = 0; b < (vFastest ? nv : nu); b++)
                {
                    int i = vFastest ? a : b, j = vFastest ? b : a;
                    var p = n.ControlPoints[i, j];
                    double w = n.IsRational ? n.Weights[i, j] : 1;
                    double k = premultiplied ? w : 1;
                    coords[o++] = p.X * k; coords[o++] = p.Y * k; coords[o++] = p.Z * k;
                    if (dim == 4) coords[o++] = w;
                }
            return _modeler.CreateBsplineSurface(props, n.KnotsU, n.KnotsV, coords) as Surface;
        }

        /// <summary>Two 32-bit integers in one double, first integer in the low word (SOLIDWORKS packing).</summary>
        private static double Pack(int lo, int hi) =>
            BitConverter.Int64BitsToDouble(((long)hi << 32) | (uint)lo);
    }
}
