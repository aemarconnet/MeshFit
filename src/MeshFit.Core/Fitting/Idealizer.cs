using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MeshFit.Core.Geometry;

namespace MeshFit.Core.Fitting
{
    public sealed class IdealizeOptions
    {
        /// <summary>Acceptable deviation (model units). A constraint is kept only while the fit stays within it.</summary>
        public double Tolerance { get; set; } = 0.1;
        /// <summary>Directions within this angle of a reference are snapped (degrees).</summary>
        public double SnapAngleDeg { get; set; } = 1.0;
        public bool SnapToAxes { get; set; } = true;
        /// <summary>Parallel / perpendicular to the other fitted features, coaxial / concentric with them.</summary>
        public bool SnapToFeatures { get; set; } = true;
        public bool Tangency { get; set; } = true;
        public bool RoundDimensions { get; set; } = true;
        /// <summary>Length rounding steps, coarse to fine (model units). Default 1, 0.5, 0.1, 0.05, 0.01 mm for metre models.</summary>
        public double[] LengthSteps { get; set; } = { 0.001, 0.0005, 0.0001, 0.00005, 0.00001 };
        /// <summary>Angle rounding steps, coarse to fine (degrees).</summary>
        public double[] AngleStepsDeg { get; set; } = { 1, 0.5, 0.1, 0.05 };
        /// <summary>How close two axes / centres must be to be merged (model units). 0 = 3 × tolerance.</summary>
        public double PositionSnap { get; set; }
        /// <summary>Unit name and scale for the report (e.g. 1000, "mm").</summary>
        public double ReportScale { get; set; } = 1000;
        public string ReportUnit { get; set; } = "mm";
        public int MaxPoints { get; set; } = 4000;
    }

    public sealed class IdealizeResult
    {
        public FittedSurface Surface { get; set; }
        public FitStatistics Stats { get; set; }
        /// <summary>Constraints that were applied, in order (e.g. "axis ∥ Z", "R = 25 mm").</summary>
        public List<string> Applied { get; } = new List<string>();
    }

    /// <summary>
    /// Turns a best-fit analytic surface into the "design intent" surface: directions snapped to the
    /// coordinate axes or to other features (parallel / perpendicular), coaxial and concentric with existing
    /// axes, fillet cylinders exactly tangent to neighbouring planes, and dimensions rounded to the coarsest
    /// step the data allows. Every constraint is followed by a constrained least-squares refit of the remaining
    /// parameters and kept only if the 95th percentile deviation stays within the tolerance.
    /// </summary>
    public static class Idealizer
    {
        // ------------------------------------------------------------------ parameter model

        /// <summary>Generic least-squares state over a full parameter vector with a mask of free entries.</summary>
        private sealed class Masked : ILsqState
        {
            public double[] Full;
            public bool[] Free;
            public Func<double[], Vec3, double> Res;
            public int ParameterCount => Free.Count(f => f);
            public ILsqState Step(double[] d)
            {
                var f = (double[])Full.Clone();
                int k = 0;
                for (int i = 0; i < f.Length; i++) if (Free[i]) f[i] += d[k++];
                return new Masked { Full = f, Free = Free, Res = Res };
            }
            public double Residual(Vec3 p) => Res(Full, p);
        }

        /// <summary>
        /// Axis-type surface (plane normal, cylinder/cone/torus axis) as base geometry + small corrections:
        /// direction = normalize(A0 + E1 t1 + E2 t2), point = C0 + E1 c1 + E2 c2 (+ A0 h for spheres/tori).
        /// </summary>
        private sealed class Model
        {
            public SurfaceKind Kind;
            public Vec3 A0, C0, E1, E2, XHint;
            public double R0, r0, Alpha0;

            public Vec3 Dir(double[] p) => Kind == SurfaceKind.Sphere ? A0 : (A0 + E1 * p[0] + E2 * p[1]).Normalized();
            public Vec3 Point(double[] p) => C0 + E1 * p[2] + E2 * p[3] + A0 * p[4];

            // Parameter layout: 0 t1, 1 t2, 2 c1, 3 c2, 4 h (along A0), 5 R, 6 r (torus) / alpha (cone)
            public const int T1 = 0, T2 = 1, C1 = 2, C2 = 3, H = 4, Rad = 5, Extra = 6;

            public double Residual(double[] p, Vec3 q)
            {
                var a = Dir(p); var c = Point(p);
                var d = q - c;
                switch (Kind)
                {
                    case SurfaceKind.Plane: return d.Dot(a);
                    case SurfaceKind.Sphere: return d.Length - p[Rad];
                    case SurfaceKind.Cylinder: return (d - a * d.Dot(a)).Length - p[Rad];
                    case SurfaceKind.Cone:
                    {
                        double h = d.Dot(a), rho = (d - a * h).Length, al = p[Extra];
                        return (rho - p[Rad]) * Math.Cos(al) - h * Math.Sin(al);
                    }
                    case SurfaceKind.Torus:
                    {
                        double h = d.Dot(a), rho = (d - a * h).Length, x = rho - p[Rad];
                        return Math.Sqrt(x * x + h * h) - p[Extra];
                    }
                }
                return 0;
            }

            public FittedSurface Build(double[] p)
            {
                var a = Dir(p); var c = Point(p);
                switch (Kind)
                {
                    case SurfaceKind.Plane: return new PlaneSurface(c, a, XHint);
                    case SurfaceKind.Sphere: return new SphereSurface(c, p[Rad], new Frame(c, A0, XHint));
                    case SurfaceKind.Cylinder: return new CylinderSurface(c, a, p[Rad], XHint);
                    case SurfaceKind.Cone: return new ConeSurface(c, a, p[Rad], p[Extra], XHint);
                    case SurfaceKind.Torus: return new TorusSurface(c, a, p[Rad], p[Extra], XHint);
                }
                return null;
            }

            public static Model From(FittedSurface s)
            {
                switch (s)
                {
                    case PlaneSurface pl: return Make(SurfaceKind.Plane, pl.Normal, pl.Origin, pl.Frame.XAxis, 0, 0, 0);
                    case SphereSurface sp: return Make(SurfaceKind.Sphere, sp.Frame.ZAxis, sp.Center, sp.Frame.XAxis, sp.Radius, 0, 0);
                    case CylinderSurface cy: return Make(SurfaceKind.Cylinder, cy.Axis, cy.AxisPoint, cy.Frame.XAxis, cy.Radius, 0, 0);
                    case ConeSurface co: return Make(SurfaceKind.Cone, co.Axis, co.Center, co.Frame.XAxis, co.Radius, 0, co.HalfAngle);
                    case TorusSurface to: return Make(SurfaceKind.Torus, to.Axis, to.Center, to.Frame.XAxis, to.MajorRadius, to.MinorRadius, 0);
                }
                return null;
            }

            private static Model Make(SurfaceKind k, Vec3 a, Vec3 c, Vec3 x, double R, double r, double al)
            {
                var e1 = a.AnyPerpendicular();
                return new Model { Kind = k, A0 = a.Normalized(), C0 = c, E1 = e1, E2 = a.Normalized().Cross(e1), XHint = x, R0 = R, r0 = r, Alpha0 = al };
            }

            public double[] Initial() => new double[] { 0, 0, 0, 0, 0, R0, Kind == SurfaceKind.Cone ? Alpha0 : r0 };

            /// <summary>Re-bases the model on the current parameters (so later corrections start from zero).</summary>
            public Model Rebase(double[] p)
            {
                var m = (Model)MemberwiseClone();
                m.A0 = Dir(p); m.C0 = Point(p);
                m.R0 = p[Rad];
                if (Kind == SurfaceKind.Cone) m.Alpha0 = p[Extra]; else m.r0 = p[Extra];
                m.E1 = m.A0.AnyPerpendicular(); m.E2 = m.A0.Cross(m.E1);
                return m;
            }

            /// <summary>Free entries for this kind (unused entries are always fixed).</summary>
            public bool[] AllFree()
            {
                var f = new bool[7];
                switch (Kind)
                {
                    case SurfaceKind.Plane: f[T1] = f[T2] = true; f[H] = true; break;
                    case SurfaceKind.Sphere: f[C1] = f[C2] = f[H] = f[Rad] = true; break;
                    case SurfaceKind.Cylinder: f[T1] = f[T2] = f[C1] = f[C2] = f[Rad] = true; break;
                    case SurfaceKind.Cone: f[T1] = f[T2] = f[C1] = f[C2] = f[Rad] = f[Extra] = true; break;
                    case SurfaceKind.Torus: f[T1] = f[T2] = f[C1] = f[C2] = f[H] = f[Rad] = f[Extra] = true; break;
                }
                return f;
            }
        }

        // ------------------------------------------------------------------ driver

        public static IdealizeResult Idealize(FittedSurface surface, IReadOnlyList<Vec3> regionPoints,
                                              IEnumerable<(string name, FittedSurface surface)> others, IdealizeOptions o)
        {
            var res = new IdealizeResult { Surface = surface };
            var model = Model.From(surface);
            var pts = Subsample(regionPoints, o.MaxPoints);
            res.Stats = SurfaceFitter.Evaluate(surface, pts, o.Tolerance);
            if (model == null) return res;   // freeform: fairness is handled by the freeform fitter

            double baseP95 = res.Stats.P95;
            double limit = Math.Max(o.Tolerance, 1.05 * baseP95);
            double cosSnap = Math.Cos(o.SnapAngleDeg * Math.PI / 180), sinSnap = Math.Sin(o.SnapAngleDeg * Math.PI / 180);
            double posSnap = o.PositionSnap > 0 ? o.PositionSnap : 3 * o.Tolerance;
            var otherList = (others ?? Enumerable.Empty<(string, FittedSurface)>()).Where(x => x.surface != null && !ReferenceEquals(x.surface, surface)).ToList();
            var fixedMask = new bool[7];          // entries pinned so far (in the current basis)
            var p = model.Initial();
            FittedSurface current = surface;
            string F(double v) => (v * o.ReportScale).ToString("0.####", CultureInfo.InvariantCulture);

            // Tries a candidate: sets entries, pins them, refits the rest; keeps it when within the limit.
            bool Try(Model m, double[] start, Action<double[]> set, int[] pin, string label, out Model mOut, out double[] pOut)
            {
                mOut = m; pOut = start;
                var q = (double[])start.Clone();
                set?.Invoke(q);
                var free = m.AllFree();
                for (int i = 0; i < 7; i++) if (fixedMask[i]) free[i] = false;
                foreach (int i in pin) free[i] = false;
                var st = new Masked { Full = q, Free = free, Res = m.Residual };
                if (st.ParameterCount > 0) st = (Masked)LevenbergMarquardt.Minimize(st, pts, null, Math.Max(1e-9, AnalyticFitters.Scale(pts)), 60);
                var cand = m.Build(st.Full);
                if (cand == null || !Valid(cand)) return false;
                var stats = SurfaceFitter.Evaluate(cand, pts, o.Tolerance);
                if (stats.P95 > limit) return false;
                foreach (int i in pin) fixedMask[i] = true;
                mOut = m; pOut = st.Full;
                current = cand; res.Stats = stats; res.Applied.Add(label);
                return true;
            }

            bool hasDir = model.Kind != SurfaceKind.Sphere;
            var axes = new[] { ("X", Vec3.UnitX), ("Y", Vec3.UnitY), ("Z", Vec3.UnitZ) };

            // 1. Direction: parallel to a coordinate axis or another feature's direction; else perpendicular to one.
            if (hasDir)
            {
                var refs = new List<(string label, Vec3 dir, bool isAxis)>();
                if (o.SnapToAxes) foreach (var (n, a) in axes) refs.Add((n, a, true));
                if (o.SnapToFeatures)
                    foreach (var (name, s) in otherList)
                    {
                        var d = DirectionOf(s);
                        if (d.HasValue) refs.Add((name, d.Value, false));
                    }
                var dir = model.Dir(p);
                bool done = false;
                foreach (var (label, rd, isAxis) in refs.OrderBy(r => -Math.Abs(r.dir.Dot(dir))))
                {
                    if (Math.Abs(dir.Dot(rd)) < cosSnap) break;
                    var target = dir.Dot(rd) > 0 ? rd : -rd;
                    var m2 = model.Rebase(p);
                    m2.A0 = target;
                    // Coordinate-aligned directions get coordinate-aligned position offsets (clean rounding later).
                    if (isAxis) { var other = axes.Where(a => Math.Abs(a.Item2.Dot(target)) < 0.5).Select(a => a.Item2).ToArray(); m2.E1 = other[0]; m2.E2 = other[1]; }
                    else { m2.E1 = target.AnyPerpendicular(); m2.E2 = target.Cross(m2.E1); }
                    var p2 = m2.Initial();
                    string what = model.Kind == SurfaceKind.Plane ? "normal" : "axis";
                    if (Try(m2, p2, null, new[] { Model.T1, Model.T2 }, isAxis ? $"{what} ∥ {label}" : $"{what} ∥ {label}", out model, out p)) { done = true; break; }
                }
                if (!done)
                    foreach (var (label, rd, _) in refs.OrderBy(r => Math.Abs(r.dir.Dot(dir))))
                    {
                        if (Math.Abs(dir.Dot(rd)) > sinSnap) break;
                        var m2 = model.Rebase(p);
                        var target = (dir - rd * dir.Dot(rd)).Normalized();
                        m2.A0 = target; m2.E1 = rd; m2.E2 = target.Cross(rd);
                        string what = model.Kind == SurfaceKind.Plane ? "normal" : "axis";
                        if (Try(m2, m2.Initial(), null, new[] { Model.T1 }, $"{what} ⟂ {label}", out model, out p)) break;
                    }
            }

            // 2. Coaxial / concentric with other features.
            if (o.SnapToFeatures)
            {
                foreach (var (name, s) in otherList)
                {
                    var axis = AxisOf(s);
                    if (axis.HasValue && hasDir && model.Kind != SurfaceKind.Plane)
                    {
                        var (ap, ad) = axis.Value;
                        var dir = model.Dir(p); var pt = model.Point(p);
                        var gap = pt - ap; gap -= ad * gap.Dot(ad);
                        if (Math.Abs(dir.Dot(ad)) >= cosSnap && gap.Length <= posSnap)
                        {
                            var m2 = model.Rebase(p);
                            m2.A0 = dir.Dot(ad) > 0 ? ad : -ad;
                            m2.C0 = ap + m2.A0 * (pt - ap).Dot(m2.A0);
                            m2.E1 = m2.A0.AnyPerpendicular(); m2.E2 = m2.A0.Cross(m2.E1);
                            if (Try(m2, m2.Initial(), null, new[] { Model.T1, Model.T2, Model.C1, Model.C2 }, $"coaxial with {name}", out model, out p)) break;
                        }
                    }
                    var centre = CentreOf(s);
                    if (centre.HasValue && (model.Kind == SurfaceKind.Sphere || model.Kind == SurfaceKind.Torus))
                    {
                        var pt = model.Point(p);
                        if (pt.DistanceTo(centre.Value) <= posSnap)
                        {
                            var m2 = model.Rebase(p);
                            m2.C0 = centre.Value;
                            if (Try(m2, m2.Initial(), null, new[] { Model.C1, Model.C2, Model.H }, $"concentric with {name}", out model, out p)) break;
                        }
                    }
                }
            }

            // 3. Tangency of a fillet cylinder with neighbouring planes.
            if (o.Tangency && model.Kind == SurfaceKind.Cylinder)
            {
                var cyl = (CylinderSurface)current;
                var planes = otherList.Where(x => x.surface is PlaneSurface).Select(x => (x.name, pl: (PlaneSurface)x.surface))
                    .Where(x => Math.Abs(x.pl.Normal.Dot(cyl.Axis)) <= sinSnap * 2 &&
                                Math.Abs(Math.Abs(x.pl.Distance(cyl.AxisPoint)) - cyl.Radius) <= posSnap)
                    .OrderBy(x => Math.Abs(Math.Abs(x.pl.Distance(cyl.AxisPoint)) - cyl.Radius)).ToList();
                var pair = planes.Count >= 2 ? FindNonParallel(planes) : null;
                Func<double, CylinderSurface> factory = null;
                if (pair != null)
                {
                    var t = TangentToTwoPlanes(cyl, pair.Value.a.pl, pair.Value.b.pl, pts, out factory);
                    if (t == null || !Accept(t, $"tangent to {pair.Value.a.name} and {pair.Value.b.name}")) factory = null;
                }
                else if (planes.Count >= 1)
                {
                    var t = TangentToPlane(cyl, planes[0].pl, pts, null);
                    var pl0 = planes[0].pl;
                    if (t != null && Accept(t, $"tangent to {planes[0].name}")) factory = R => TangentToPlane((CylinderSurface)current, pl0, pts, R);
                }
                if (factory != null)
                {
                    // Round the fillet radius while keeping it tangent.
                    if (o.RoundDimensions)
                    {
                        double cur = ((CylinderSurface)current).Radius;
                        foreach (double step in o.LengthSteps)
                        {
                            double v = Math.Round(cur / step) * step;
                            var c = v > 0 ? factory(v) : null;
                            if (c != null && Accept(c, $"R = {F(v)} {o.ReportUnit}")) break;
                        }
                    }
                    res.Surface = current;
                    return res;
                }
            }

            // 4. Round dimensions (coarsest step that still fits).
            if (o.RoundDimensions)
            {
                RoundLength(Model.Rad, model.Kind == SurfaceKind.Plane ? null : model.Kind == SurfaceKind.Torus ? "R" : model.Kind == SurfaceKind.Cone ? null : "R");
                if (model.Kind == SurfaceKind.Torus) RoundLength(Model.Extra, "r");
                if (model.Kind == SurfaceKind.Cone && !fixedMask[Model.Extra])
                {
                    double al = p[Model.Extra];
                    foreach (double stepDeg in o.AngleStepsDeg)
                    {
                        double step = stepDeg * Math.PI / 180, v = Math.Round(al / step) * step;
                        if (v <= 0) continue;
                        if (Try(model, p, q => q[Model.Extra] = v, new[] { Model.Extra }, $"half angle {(v * 180 / Math.PI).ToString("0.###", CultureInfo.InvariantCulture)}°", out model, out p)) break;
                    }
                }
                // Positions along coordinate axes: plane offsets and axis locations.
                var dirNow = model.Dir(p);
                bool axisAligned = axes.Any(a => Math.Abs(Math.Abs(a.Item2.Dot(dirNow)) - 1) < 1e-12);
                if (model.Kind == SurfaceKind.Plane && axisAligned)
                {
                    // Offset = position of the plane along its (coordinate) normal.
                    double off = model.Point(p).Dot(dirNow);
                    foreach (double step in o.LengthSteps)
                    {
                        double v = Math.Round(off / step) * step;
                        double dh = v - off;
                        if (Try(model, p, q => q[Model.H] += dh * (1 / Math.Max(1e-12, model.A0.Dot(dirNow))), new[] { Model.H },
                                $"offset {F(v)} {o.ReportUnit}", out model, out p)) break;
                    }
                }
                else if (axisAligned && model.Kind != SurfaceKind.Sphere && !fixedMask[Model.C1])
                {
                    foreach (int idx in new[] { Model.C1, Model.C2 })
                    {
                        var e = idx == Model.C1 ? model.E1 : model.E2;
                        if (axes.All(a => Math.Abs(Math.Abs(a.Item2.Dot(e)) - 1) > 1e-12)) continue;
                        double cur = model.Point(p).Dot(e);
                        foreach (double step in o.LengthSteps)
                        {
                            double v = Math.Round(cur / step) * step, dv = v - cur;
                            if (Try(model, p, q => q[idx] += dv, new[] { idx }, $"axis at {AxisName(e)} = {F(v)} {o.ReportUnit}", out model, out p)) break;
                        }
                    }
                }
                else if (model.Kind == SurfaceKind.Sphere)
                {
                    // Round the centre coordinates one at a time, keeping those already rounded.
                    var rounded = new List<Vec3>();
                    foreach (var (n, e) in axes)
                    {
                        double cur = model.Point(p).Dot(e);
                        foreach (double step in o.LengthSteps)
                        {
                            double v = Math.Round(cur / step) * step;
                            var m2 = model.Rebase(p);
                            m2.C0 = m2.C0 + e * (v - cur);
                            var rest = axes.Select(x => x.Item2).Where(x => x.Dot(e) == 0).ToArray();
                            m2.A0 = e; m2.E1 = rest[0]; m2.E2 = rest[1];
                            var pins = new List<int> { Model.H };
                            if (rounded.Contains(rest[0])) pins.Add(Model.C1);
                            if (rounded.Contains(rest[1])) pins.Add(Model.C2);
                            var saved = (bool[])fixedMask.Clone();
                            if (Try(m2, m2.Initial(), null, pins.ToArray(), $"centre {n} = {F(v)} {o.ReportUnit}", out model, out p))
                            {
                                rounded.Add(e);
                                fixedMask = saved;     // the basis changes for the next coordinate
                                break;
                            }
                            fixedMask = saved;
                        }
                    }
                }
            }

            res.Surface = current;
            return res;

            // ---- local helpers
            void RoundLength(int idx, string name)
            {
                if (name == null || fixedMask[idx]) return;
                double cur = p[idx];
                foreach (double step in o.LengthSteps)
                {
                    double v = Math.Round(cur / step) * step;
                    if (v <= 0) continue;
                    if (Try(model, p, q => q[idx] = v, new[] { idx }, $"{name} = {F(v)} {o.ReportUnit}", out model, out p)) break;
                }
            }

            bool Accept(FittedSurface cand, string label)
            {
                if (!Valid(cand)) return false;
                var stats = SurfaceFitter.Evaluate(cand, pts, o.Tolerance);
                if (stats.P95 > limit) return false;
                current = cand; res.Stats = stats; res.Applied.Add(label);
                return true;
            }
        }

        private static string AxisName(Vec3 e) => Math.Abs(e.X) > 0.5 ? "X" : Math.Abs(e.Y) > 0.5 ? "Y" : "Z";

        private static bool Valid(FittedSurface s)
        {
            switch (s)
            {
                case CylinderSurface c: return c.Radius > 0 && c.AxisPoint.IsFinite;
                case ConeSurface k: return k.HalfAngle > 0 && k.HalfAngle < Math.PI / 2 && k.Center.IsFinite;
                case SphereSurface sp: return sp.Radius > 0 && sp.Center.IsFinite;
                case TorusSurface t: return t.MinorRadius > 0 && t.MajorRadius > 0 && t.Center.IsFinite;
                case PlaneSurface p: return p.Origin.IsFinite;
            }
            return true;
        }

        public static Vec3? DirectionOf(FittedSurface s)
        {
            switch (s)
            {
                case PlaneSurface p: return p.Normal;
                case CylinderSurface c: return c.Axis;
                case ConeSurface k: return k.Axis;
                case TorusSurface t: return t.Axis;
            }
            return null;
        }

        private static (Vec3 p, Vec3 d)? AxisOf(FittedSurface s)
        {
            switch (s)
            {
                case CylinderSurface c: return (c.AxisPoint, c.Axis);
                case ConeSurface k: return (k.Center, k.Axis);
                case TorusSurface t: return (t.Center, t.Axis);
            }
            return null;
        }

        private static Vec3? CentreOf(FittedSurface s)
        {
            switch (s)
            {
                case SphereSurface sp: return sp.Center;
                case TorusSurface t: return t.Center;
            }
            return null;
        }

        private static ((string name, PlaneSurface pl) a, (string name, PlaneSurface pl) b)? FindNonParallel(List<(string name, PlaneSurface pl)> planes)
        {
            for (int i = 0; i < planes.Count; i++)
                for (int j = i + 1; j < planes.Count; j++)
                    if (planes[i].pl.Normal.Cross(planes[j].pl.Normal).Length > 0.05) return (planes[i], planes[j]);
            return null;
        }

        /// <summary>Fillet between two planes: axis ∥ n1 × n2 at distance R from both; only R is fitted.</summary>
        private static CylinderSurface TangentToTwoPlanes(CylinderSurface cyl, PlaneSurface a, PlaneSurface b, Vec3[] pts, out Func<double, CylinderSurface> factory)
        {
            var dir = a.Normal.Cross(b.Normal).Normalized();
            if (dir.Dot(cyl.Axis) < 0) dir = -dir;
            double sa = Math.Sign(a.Distance(cyl.AxisPoint)), sb = Math.Sign(b.Distance(cyl.AxisPoint));
            if (sa == 0) sa = 1; if (sb == 0) sb = 1;
            CylinderSurface Make(double R)
            {
                // Point on both offset planes nearest the original axis point.
                double da = a.Normal.Dot(a.Origin) + sa * R, db = b.Normal.Dot(b.Origin) + sb * R;
                var n1 = a.Normal; var n2 = b.Normal;
                // Solve n1·x = da, n2·x = db, dir·x = dir·c0.
                var m = new[,] { { n1.X, n1.Y, n1.Z }, { n2.X, n2.Y, n2.Z }, { dir.X, dir.Y, dir.Z } };
                var rhs = new[] { da, db, dir.Dot(cyl.AxisPoint) };
                var x = Solve3(m, rhs);
                return x.HasValue ? new CylinderSurface(x.Value, dir, R, cyl.Frame.XAxis) : null;
            }
            double Cost(double R)
            {
                var c = Make(R);
                if (c == null) return double.MaxValue;
                double s = 0; foreach (var p in pts) { double e = c.Distance(p); s += e * e; }
                return s;
            }
            // Golden section around the fitted radius.
            double lo = cyl.Radius * 0.5, hi = cyl.Radius * 1.5, g = (Math.Sqrt(5) - 1) / 2;
            double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo), f1 = Cost(x1), f2 = Cost(x2);
            for (int i = 0; i < 80; i++)
            {
                if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - g * (hi - lo); f1 = Cost(x1); }
                else { lo = x1; x1 = x2; f1 = f2; x2 = lo + g * (hi - lo); f2 = Cost(x2); }
            }
            factory = Make;
            return Make(0.5 * (lo + hi));
        }

        /// <summary>Cylinder tangent to a plane: axis parallel to the plane at distance R; direction, offset and R fitted.</summary>
        private static CylinderSurface TangentToPlane(CylinderSurface cyl, PlaneSurface pl, Vec3[] pts, double? fixedR)
        {
            var n = pl.Normal;
            double side = Math.Sign(pl.Distance(cyl.AxisPoint)); if (side == 0) side = 1;
            var d0 = (cyl.Axis - n * cyl.Axis.Dot(n)).Normalized();
            var w0 = n.Cross(d0);
            var q0 = cyl.AxisPoint - n * pl.Distance(cyl.AxisPoint);   // foot on the plane
            var st = new Masked
            {
                Full = new[] { 0.0, 0.0, fixedR ?? cyl.Radius },
                Free = new[] { true, true, !fixedR.HasValue },
                Res = (p, q) =>
                {
                    var d = (d0 + w0 * p[0]).Normalized();
                    var c = q0 + n.Cross(d) * p[1] + n * (side * p[2]);
                    var v = q - c;
                    return (v - d * v.Dot(d)).Length - p[2];
                }
            };
            st = (Masked)LevenbergMarquardt.Minimize(st, pts, null, AnalyticFitters.Scale(pts), 60);
            var dd = (d0 + w0 * st.Full[0]).Normalized();
            var cc = q0 + n.Cross(dd) * st.Full[1] + n * (side * st.Full[2]);
            return st.Full[2] > 0 ? new CylinderSurface(cc, dd, st.Full[2], cyl.Frame.XAxis) : null;
        }

        private static Vec3? Solve3(double[,] m, double[] b)
        {
            double det = m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
            if (Math.Abs(det) < 1e-12) return null;
            double Dx(int c)
            {
                var t = (double[,])m.Clone();
                for (int r = 0; r < 3; r++) t[r, c] = b[r];
                return t[0, 0] * (t[1, 1] * t[2, 2] - t[1, 2] * t[2, 1]) - t[0, 1] * (t[1, 0] * t[2, 2] - t[1, 2] * t[2, 0]) + t[0, 2] * (t[1, 0] * t[2, 1] - t[1, 1] * t[2, 0]);
            }
            return new Vec3(Dx(0) / det, Dx(1) / det, Dx(2) / det);
        }

        private static Vec3[] Subsample(IReadOnlyList<Vec3> pts, int max)
        {
            if (pts.Count <= max) return pts.ToArray();
            double step = (double)pts.Count / max;
            return Enumerable.Range(0, max).Select(i => pts[(int)(i * step)]).ToArray();
        }
    }
}
