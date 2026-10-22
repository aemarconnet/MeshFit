using System;
using System.Collections.Generic;
using System.Globalization;
using MeshFit.Core.Geometry;

namespace MeshFit.Core.Fitting
{
    public enum SurfaceKind { Plane, Sphere, Cylinder, Cone, Torus, Freeform }

    /// <summary>A surface produced by a fitter. Distances are signed where the surface has a natural side.</summary>
    public abstract class FittedSurface
    {
        public abstract SurfaceKind Kind { get; }
        /// <summary>Number of free parameters (used to prefer simpler surfaces).</summary>
        public abstract int ParameterCount { get; }
        public abstract double Distance(Vec3 p);
        public abstract Vec3 Project(Vec3 p);
        /// <summary>Human readable description. <paramref name="lengthScale"/> converts model units to display units.</summary>
        public abstract string Describe(double lengthScale, string unit);

        protected static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        protected static string Deg(double rad) => (rad * 180 / Math.PI).ToString("0.###", CultureInfo.InvariantCulture) + "°";
    }

    /// <summary>
    /// Analytic surface with a natural 2D parameterisation (s, t). Angular parameters are in radians,
    /// linear parameters in model units. Used for extents, previews and NURBS export.
    /// </summary>
    public abstract class AnalyticSurface : FittedSurface
    {
        public abstract Frame Frame { get; }
        public abstract void ParamOf(Vec3 p, out double s, out double t);
        public abstract Vec3 PointAt(double s, double t);
        public abstract bool PeriodicS { get; }
        public abstract bool PeriodicT { get; }
        public abstract AnalyticSurface WithFrame(Frame f);
    }

    public sealed class PlaneSurface : AnalyticSurface
    {
        public Vec3 Origin { get; }
        public Vec3 Normal { get; }
        private readonly Frame _frame;
        public PlaneSurface(Vec3 origin, Vec3 normal, Vec3? xHint = null)
        {
            Origin = origin; Normal = normal.Normalized();
            _frame = xHint.HasValue ? new Frame(origin, Normal, xHint.Value) : Frame.FromAxis(origin, Normal);
        }
        public override SurfaceKind Kind => SurfaceKind.Plane;
        public override int ParameterCount => 3;
        public override Frame Frame => _frame;
        public override double Distance(Vec3 p) => (p - Origin).Dot(Normal);
        public override Vec3 Project(Vec3 p) => p - Normal * Distance(p);
        public override void ParamOf(Vec3 p, out double s, out double t) { var l = _frame.ToLocal(p); s = l.X; t = l.Y; }
        public override Vec3 PointAt(double s, double t) => _frame.ToWorld(s, t, 0);
        public override bool PeriodicS => false;
        public override bool PeriodicT => false;
        public override AnalyticSurface WithFrame(Frame f) => new PlaneSurface(f.Origin, Normal, f.XAxis);
        public override string Describe(double k, string u) =>
            $"Plane  normal {Normal}  through {Origin * k} {u}";
    }

    public sealed class SphereSurface : AnalyticSurface
    {
        public Vec3 Center { get; }
        public double Radius { get; }
        private readonly Frame _frame;
        public SphereSurface(Vec3 center, double radius, Frame? frame = null)
        {
            Center = center; Radius = Math.Abs(radius);
            _frame = frame.HasValue ? new Frame(center, frame.Value.ZAxis, frame.Value.XAxis) : Frame.FromAxis(center, Vec3.UnitZ);
        }
        public override SurfaceKind Kind => SurfaceKind.Sphere;
        public override int ParameterCount => 4;
        public override Frame Frame => _frame;
        public override double Distance(Vec3 p) => (p - Center).Length - Radius;
        public override Vec3 Project(Vec3 p) => Center + (p - Center).Normalized() * Radius;
        /// <summary>s = longitude about the frame Z axis, t = latitude.</summary>
        public override void ParamOf(Vec3 p, out double s, out double t)
        {
            var l = _frame.ToLocal(p);
            s = Math.Atan2(l.Y, l.X);
            t = Math.Atan2(l.Z, Math.Sqrt(l.X * l.X + l.Y * l.Y));
        }
        public override Vec3 PointAt(double s, double t) =>
            _frame.ToWorld(Radius * Math.Cos(t) * Math.Cos(s), Radius * Math.Cos(t) * Math.Sin(s), Radius * Math.Sin(t));
        public override bool PeriodicS => true;
        public override bool PeriodicT => false;
        public override AnalyticSurface WithFrame(Frame f) => new SphereSurface(Center, Radius, f);
        public override string Describe(double k, string u) =>
            $"Sphere  center {Center * k} {u}  R {F(Radius * k)} {u}";
    }

    public sealed class CylinderSurface : AnalyticSurface
    {
        public Vec3 AxisPoint { get; }
        public Vec3 Axis { get; }
        public double Radius { get; }
        private readonly Frame _frame;
        public CylinderSurface(Vec3 axisPoint, Vec3 axis, double radius, Vec3? xHint = null)
        {
            AxisPoint = axisPoint; Axis = axis.Normalized(); Radius = Math.Abs(radius);
            _frame = xHint.HasValue ? new Frame(axisPoint, Axis, xHint.Value) : Frame.FromAxis(axisPoint, Axis);
        }
        public override SurfaceKind Kind => SurfaceKind.Cylinder;
        public override int ParameterCount => 5;
        public override Frame Frame => _frame;
        public override double Distance(Vec3 p)
        {
            var d = p - AxisPoint;
            return (d - Axis * d.Dot(Axis)).Length - Radius;
        }
        public override Vec3 Project(Vec3 p)
        {
            var d = p - AxisPoint;
            double h = d.Dot(Axis);
            var radial = d - Axis * h;
            return AxisPoint + Axis * h + radial.Normalized() * Radius;
        }
        /// <summary>s = angle about the axis, t = height along the axis.</summary>
        public override void ParamOf(Vec3 p, out double s, out double t)
        {
            var l = _frame.ToLocal(p);
            s = Math.Atan2(l.Y, l.X); t = l.Z;
        }
        public override Vec3 PointAt(double s, double t) => _frame.ToWorld(Radius * Math.Cos(s), Radius * Math.Sin(s), t);
        public override bool PeriodicS => true;
        public override bool PeriodicT => false;
        public override AnalyticSurface WithFrame(Frame f) => new CylinderSurface(f.Origin, Axis, Radius, f.XAxis);
        public override string Describe(double k, string u) =>
            $"Cylinder  axis {Axis}  through {AxisPoint * k} {u}  R {F(Radius * k)} {u}";
    }

    /// <summary>Cone with radius <see cref="Radius"/> at <see cref="Center"/>, growing along <see cref="Axis"/> with half angle.</summary>
    public sealed class ConeSurface : AnalyticSurface
    {
        public Vec3 Center { get; }
        public Vec3 Axis { get; }
        public double Radius { get; }
        public double HalfAngle { get; }
        private readonly Frame _frame;
        public ConeSurface(Vec3 center, Vec3 axis, double radius, double halfAngle, Vec3? xHint = null)
        {
            // Canonical form: half angle >= 0 (radius grows along the axis).
            if (halfAngle < 0) { axis = -axis; halfAngle = -halfAngle; }
            Center = center; Axis = axis.Normalized(); Radius = radius; HalfAngle = halfAngle;
            _frame = xHint.HasValue ? new Frame(center, Axis, xHint.Value) : Frame.FromAxis(center, Axis);
        }
        public override SurfaceKind Kind => SurfaceKind.Cone;
        public override int ParameterCount => 6;
        public override Frame Frame => _frame;
        public Vec3 Apex => Center - Axis * (Radius / Math.Tan(HalfAngle));
        private void Local(Vec3 p, out double rho, out double h, out Vec3 radialDir)
        {
            var d = p - Center;
            h = d.Dot(Axis);
            var radial = d - Axis * h;
            rho = radial.Length;
            radialDir = rho > 1e-300 ? radial / rho : _frame.XAxis;
        }
        public override double Distance(Vec3 p)
        {
            Local(p, out double rho, out double h, out _);
            return (rho - Radius) * Math.Cos(HalfAngle) - h * Math.Sin(HalfAngle);
        }
        public override Vec3 Project(Vec3 p)
        {
            Local(p, out double rho, out double h, out Vec3 dir);
            double d = (rho - Radius) * Math.Cos(HalfAngle) - h * Math.Sin(HalfAngle);
            double rho2 = rho - d * Math.Cos(HalfAngle), h2 = h + d * Math.Sin(HalfAngle);
            return Center + Axis * h2 + dir * rho2;
        }
        /// <summary>s = angle about the axis, t = height along the axis from the center.</summary>
        public override void ParamOf(Vec3 p, out double s, out double t)
        {
            var q = Project(p);
            var l = _frame.ToLocal(q);
            s = Math.Atan2(l.Y, l.X); t = l.Z;
        }
        public double RadiusAt(double t) => Radius + t * Math.Tan(HalfAngle);
        public override Vec3 PointAt(double s, double t)
        {
            double r = RadiusAt(t);
            return _frame.ToWorld(r * Math.Cos(s), r * Math.Sin(s), t);
        }
        public override bool PeriodicS => true;
        public override bool PeriodicT => false;
        public override AnalyticSurface WithFrame(Frame f)
        {
            // Move the reference center along the axis to the new origin.
            double h = (f.Origin - Center).Dot(Axis);
            return new ConeSurface(Center + Axis * h, Axis, RadiusAt(h), HalfAngle, f.XAxis);
        }
        public override string Describe(double k, string u) =>
            $"Cone  axis {Axis}  half-angle {Deg(HalfAngle)}  R {F(Radius * k)} {u} at {Center * k} {u}";
    }

    public sealed class TorusSurface : AnalyticSurface
    {
        public Vec3 Center { get; }
        public Vec3 Axis { get; }
        public double MajorRadius { get; }
        public double MinorRadius { get; }
        private readonly Frame _frame;
        public TorusSurface(Vec3 center, Vec3 axis, double major, double minor, Vec3? xHint = null)
        {
            Center = center; Axis = axis.Normalized(); MajorRadius = major; MinorRadius = Math.Abs(minor);
            _frame = xHint.HasValue ? new Frame(center, Axis, xHint.Value) : Frame.FromAxis(center, Axis);
        }
        public override SurfaceKind Kind => SurfaceKind.Torus;
        public override int ParameterCount => 7;
        public override Frame Frame => _frame;
        private Vec3 SpinePoint(Vec3 p)
        {
            var d = p - Center;
            var radial = d - Axis * d.Dot(Axis);
            var dir = radial.Length > 1e-300 ? radial.Normalized() : _frame.XAxis;
            return Center + dir * MajorRadius;
        }
        public override double Distance(Vec3 p) => (p - SpinePoint(p)).Length - MinorRadius;
        public override Vec3 Project(Vec3 p)
        {
            var c = SpinePoint(p);
            return c + (p - c).Normalized() * MinorRadius;
        }
        /// <summary>s = major angle about the axis, t = minor angle around the tube (0 = outer equator).</summary>
        public override void ParamOf(Vec3 p, out double s, out double t)
        {
            var l = _frame.ToLocal(p);
            s = Math.Atan2(l.Y, l.X);
            double rho = Math.Sqrt(l.X * l.X + l.Y * l.Y);
            t = Math.Atan2(l.Z, rho - MajorRadius);
        }
        public override Vec3 PointAt(double s, double t)
        {
            double r = MajorRadius + MinorRadius * Math.Cos(t);
            return _frame.ToWorld(r * Math.Cos(s), r * Math.Sin(s), MinorRadius * Math.Sin(t));
        }
        public override bool PeriodicS => true;
        public override bool PeriodicT => true;
        public override AnalyticSurface WithFrame(Frame f) => new TorusSurface(Center, Axis, MajorRadius, MinorRadius, f.XAxis);
        public override string Describe(double k, string u) =>
            $"Torus  axis {Axis}  center {Center * k} {u}  R {F(MajorRadius * k)} {u}  r {F(MinorRadius * k)} {u}";
    }

    /// <summary>Freeform B-spline surface parameterised over a base plane (u, v = local x, y).</summary>
    public sealed class FreeformSurface : FittedSurface
    {
        public NurbsSurface Nurbs { get; }
        public Frame BaseFrame { get; }
        public FreeformSurface(NurbsSurface nurbs, Frame baseFrame) { Nurbs = nurbs; BaseFrame = baseFrame; }
        public override SurfaceKind Kind => SurfaceKind.Freeform;
        public override int ParameterCount => 3 * Nurbs.CountU * Nurbs.CountV;

        public Vec3 Project(Vec3 p, out double u, out double v)
        {
            var l = BaseFrame.ToLocal(p);
            u = l.X; v = l.Y;
            return Nurbs.Project(p, ref u, ref v);
        }
        public override Vec3 Project(Vec3 p) => Project(p, out _, out _);
        public override double Distance(Vec3 p)
        {
            var q = Project(p, out double u, out double v);
            Nurbs.Evaluate(u, v, out _, out Vec3 su, out Vec3 sv);
            var n = su.Cross(sv);
            double d = (p - q).Length;
            return (p - q).Dot(n) < 0 ? -d : d;
        }
        public override string Describe(double k, string u) =>
            $"Freeform B-spline  degree {Nurbs.DegreeU}x{Nurbs.DegreeV}  {Nurbs.CountU}x{Nurbs.CountV} control points";
    }

    public static class AngleUtil
    {
        /// <summary>
        /// Smallest arc [start, start+span] (radians) covering all given angles. span is 2π when the angles
        /// cover the whole circle (largest gap smaller than <paramref name="fullCircleGap"/>).
        /// </summary>
        public static void CoveringArc(IList<double> angles, out double start, out double span, double fullCircleGap = 0.2)
        {
            if (angles.Count == 0) { start = 0; span = 0; return; }
            var a = new double[angles.Count];
            for (int i = 0; i < a.Length; i++) a[i] = Normalize(angles[i]);
            Array.Sort(a);
            double bestGap = a[0] + 2 * Math.PI - a[a.Length - 1];
            int bestIdx = 0; // arc starts at a[bestIdx]
            for (int i = 1; i < a.Length; i++)
            {
                double gap = a[i] - a[i - 1];
                if (gap > bestGap) { bestGap = gap; bestIdx = i; }
            }
            if (bestGap < fullCircleGap) { start = -Math.PI; span = 2 * Math.PI; return; }
            start = a[bestIdx];
            span = 2 * Math.PI - bestGap;
        }

        /// <summary>Normalises to (-π, π].</summary>
        public static double Normalize(double a)
        {
            a %= 2 * Math.PI;
            if (a > Math.PI) a -= 2 * Math.PI;
            if (a <= -Math.PI) a += 2 * Math.PI;
            return a;
        }
    }
}
