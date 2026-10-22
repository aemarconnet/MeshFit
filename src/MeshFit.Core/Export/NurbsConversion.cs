using System;
using System.Collections.Generic;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;

namespace MeshFit.Core.Export
{
    /// <summary>Exact NURBS representations of the fitted analytic surfaces over given extents.</summary>
    public static class NurbsConversion
    {
        public static NurbsSurface ToNurbs(FittedSurface s, Extents e)
        {
            switch (s)
            {
                case FreeformSurface f: return f.Nurbs;
                case PlaneSurface p: return Plane(p, e);
                case CylinderSurface c:
                    return Revolve(c.Frame, e.S0, e.S1, Line(c.Radius, e.T0, c.Radius, e.T1));
                case ConeSurface k:
                    return Revolve(k.Frame, e.S0, e.S1, Line(k.RadiusAt(e.T0), e.T0, k.RadiusAt(e.T1), e.T1));
                case SphereSurface sp:
                    return Revolve(sp.Frame, e.S0, e.S1, Arc(0, 0, sp.Radius, e.T0, e.T1));
                case TorusSurface t:
                    return Revolve(t.Frame, e.S0, e.S1, Arc(t.MajorRadius, 0, t.MinorRadius, e.T0, e.T1));
                default: throw new NotSupportedException(s.GetType().Name);
            }
        }

        private static NurbsSurface Plane(PlaneSurface p, Extents e)
        {
            var c = new Vec3[2, 2];
            c[0, 0] = p.PointAt(e.S0, e.T0); c[1, 0] = p.PointAt(e.S1, e.T0);
            c[0, 1] = p.PointAt(e.S0, e.T1); c[1, 1] = p.PointAt(e.S1, e.T1);
            return new NurbsSurface(1, 1, new double[] { 0, 0, 1, 1 }, new double[] { 0, 0, 1, 1 }, c);
        }

        /// <summary>2D rational curve in the (radius, height) half plane.</summary>
        public sealed class Profile
        {
            public int Degree;
            public double[] Knots;
            public double[] Rho, Z, W;
        }

        public static Profile Line(double rho0, double z0, double rho1, double z1) => new Profile
        {
            Degree = 1,
            Knots = new double[] { 0, 0, 1, 1 },
            Rho = new[] { rho0, rho1 },
            Z = new[] { z0, z1 },
            W = new[] { 1.0, 1.0 }
        };

        /// <summary>Circular arc (center (cr, cz), radius r) from angle a0 to a1 measured from the +rho direction.</summary>
        public static Profile Arc(double cr, double cz, double r, double a0, double a1)
        {
            UnitArc(a0, a1, out var x, out var y, out var w, out var k);
            var pr = new Profile { Degree = 2, Knots = k, Rho = new double[x.Length], Z = new double[x.Length], W = w };
            for (int i = 0; i < x.Length; i++) { pr.Rho[i] = cr + r * x[i]; pr.Z[i] = cz + r * y[i]; }
            return pr;
        }

        /// <summary>Rational quadratic unit-circle arc, split into segments of at most 90°.</summary>
        public static void UnitArc(double a0, double a1, out double[] x, out double[] y, out double[] w, out double[] knots)
        {
            double span = a1 - a0;
            int nseg = Math.Max(1, (int)Math.Ceiling(Math.Abs(span) / (Math.PI / 2) - 1e-9));
            double d = span / nseg;
            int n = 2 * nseg + 1;
            x = new double[n]; y = new double[n]; w = new double[n];
            double wm = Math.Cos(d / 2);
            for (int s = 0; s < nseg; s++)
            {
                double as0 = a0 + s * d, am = as0 + d / 2;
                x[2 * s] = Math.Cos(as0); y[2 * s] = Math.Sin(as0); w[2 * s] = 1;
                x[2 * s + 1] = Math.Cos(am) / wm; y[2 * s + 1] = Math.Sin(am) / wm; w[2 * s + 1] = wm;
            }
            x[n - 1] = Math.Cos(a1); y[n - 1] = Math.Sin(a1); w[n - 1] = 1;
            knots = new double[n + 3];
            for (int i = 0; i < 3; i++) { knots[i] = 0; knots[n + i] = 1; }
            for (int s = 1; s < nseg; s++) { knots[1 + 2 * s] = knots[2 + 2 * s] = (double)s / nseg; }
        }

        /// <summary>Surface of revolution of a profile about the frame Z axis between angles s0 and s1.</summary>
        public static NurbsSurface Revolve(Frame f, double s0, double s1, Profile prof)
        {
            UnitArc(s0, s1, out var ax, out var ay, out var aw, out var ak);
            int nu = ax.Length, nv = prof.Rho.Length;
            var ctrl = new Vec3[nu, nv];
            var wts = new double[nu, nv];
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    ctrl[i, j] = f.ToWorld(prof.Rho[j] * ax[i], prof.Rho[j] * ay[i], prof.Z[j]);
                    wts[i, j] = aw[i] * prof.W[j];
                }
            return new NurbsSurface(2, prof.Degree, ak, prof.Knots, ctrl, wts);
        }
    }
}
