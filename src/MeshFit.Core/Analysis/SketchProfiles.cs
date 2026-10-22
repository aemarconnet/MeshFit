using System;
using System.Collections.Generic;
using System.Linq;
using MeshFit.Core.Fitting;
using MeshFit.Core.Geometry;
using MeshFit.Core.Mesh;

namespace MeshFit.Core.Analysis
{
    /// <summary>A line or circular arc of a 2D sketch profile (sketch-plane coordinates, model units).</summary>
    public sealed class ProfileSegment
    {
        public bool IsArc { get; set; }
        public double X0 { get; set; }
        public double Y0 { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        /// <summary>Arc centre and radius (arcs only).</summary>
        public double Cx { get; set; }
        public double Cy { get; set; }
        public double Radius { get; set; }
        /// <summary>Arc runs counter-clockwise from start to end (arcs only).</summary>
        public bool CounterClockwise { get; set; }
        public override string ToString() => IsArc ? $"Arc R{Radius:G4}" : $"Line L{Math.Sqrt((X1 - X0) * (X1 - X0) + (Y1 - Y0) * (Y1 - Y0)):G4}";
    }

    /// <summary>Lines-and-arcs profile on a sketch plane.</summary>
    public class SketchProfile
    {
        /// <summary>Sketch plane: origin, X and Y axes (in-plane), Z = normal.</summary>
        public Frame Plane { get; set; }
        public List<ProfileSegment> Segments { get; set; } = new List<ProfileSegment>();
        public bool Closed { get; set; }
        /// <summary>Largest / RMS distance of the region's points (collapsed onto the plane) from the profile.</summary>
        public double MaxDeviation { get; set; }
        public double RmsDeviation { get; set; }
        public Vec3 To3D(double x, double y) => Plane.ToWorld(x, y, 0);

        public string Describe(double k, string unit)
        {
            int lines = Segments.Count(s => !s.IsArc), arcs = Segments.Count - lines;
            var radii = Segments.Where(s => s.IsArc).Select(s => (s.Radius * k).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            return $"{lines} line(s), {arcs} arc(s){(arcs > 0 ? " R " + string.Join(", ", radii) + " " + unit : "")}; " +
                   $"profile deviation max {(MaxDeviation * k):0.###} {unit}, RMS {(RmsDeviation * k):0.###} {unit}";
        }
    }

    public sealed class ExtrusionProfile : SketchProfile
    {
        /// <summary>Extrusion direction (the sketch plane normal).</summary>
        public Vec3 Direction { get; set; }
        public double Depth { get; set; }
        /// <summary>RMS of n·d over the region: 0 for a perfect extrusion.</summary>
        public double DirectionResidual { get; set; }
    }

    public sealed class RevolutionProfile : SketchProfile
    {
        public Vec3 AxisPoint { get; set; }
        public Vec3 Axis { get; set; }
        /// <summary>Angle to revolve from the sketch plane (radians); 2π = full revolution.</summary>
        public double Sweep { get; set; }
        /// <summary>RMS distance of the region's normal lines from the axis.</summary>
        public double AxisResidual { get; set; }
    }

    /// <summary>
    /// Breaks a region into a sketch (lines and arcs only) plus an extrusion or a revolution: the region is
    /// collapsed along the extrusion direction / around the axis onto a plane, the point band is ordered
    /// into a centre line, and the fewest lines and arcs within tolerance are fitted to it.
    /// </summary>
    public static class SketchProfiles
    {
        // ------------------------------------------------------------------ extrusion

        /// <param name="snapDegrees">Snap the direction to the nearest X/Y/Z axis when within this angle (0 = never).</param>
        public static ExtrusionProfile Extrusion(TriMesh m, IList<int> tris, double tolerance, double margin = 0.05, double snapDegrees = 1)
        {
            var verts = m.VerticesOf(tris);
            if (verts.Length < 10) throw new ArgumentException("The region is too small.");
            var pts = verts.Select(v => m.Vertices[v]).ToArray();
            var nrm = verts.Select(v => m.VertexNormals[v]).ToArray();

            // Normals of an extruded surface are all perpendicular to the extrusion direction.
            AnalyticFitters.Pca(nrm, Vec3.Zero, out _, out var ev, centered: false);
            var d = Snap(ev[0], snapDegrees);
            double res = Math.Sqrt(nrm.Average(n => n.Dot(d) * n.Dot(d)));

            double h0 = pts.Min(p => p.Dot(d)), h1 = pts.Max(p => p.Dot(d));
            double ext = (h1 - h0) * margin;
            h0 -= ext; h1 += ext;
            var c = AnalyticFitters.Centroid(pts);
            var origin = c + d * (h0 - c.Dot(d));
            // In-plane X along the main spread of the profile.
            var f0 = Frame.FromAxis(origin, d);
            var flat = pts.Select(p => f0.ToLocal(p)).ToArray();
            double sxx = flat.Sum(p => p.X * p.X) - flat.Length * Sq(flat.Average(p => p.X));
            double syy = flat.Sum(p => p.Y * p.Y) - flat.Length * Sq(flat.Average(p => p.Y));
            double sxy = flat.Sum(p => p.X * p.Y) - flat.Length * flat.Average(p => p.X) * flat.Average(p => p.Y);
            double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            var xdir = f0.XAxis * Math.Cos(ang) + f0.YAxis * Math.Sin(ang);
            var plane = new Frame(origin, d, xdir);
            var p2 = pts.Select(p => { var l = plane.ToLocal(p); return (l.X, l.Y); }).ToList();

            var prof = new ExtrusionProfile { Plane = plane, Direction = d, Depth = h1 - h0, DirectionResidual = res };
            BuildProfile(prof, p2, m.AverageEdgeLength, tolerance);
            return prof;
        }

        // ------------------------------------------------------------------ revolution

        private sealed class AxisState : ILsqState
        {
            public Vec3 C, A;
            public Dictionary<Vec3, Vec3> NormalOf;
            public int ParameterCount => 4;
            public ILsqState Step(double[] d)
            {
                var e1 = A.AnyPerpendicular(); var e2 = A.Cross(e1);
                return new AxisState { A = (A + e1 * d[0] + e2 * d[1]).Normalized(), C = C + e1 * d[2] + e2 * d[3], NormalOf = NormalOf };
            }
            public double Residual(Vec3 p)
            {
                // The normal line through p must meet the axis.
                // Plücker condition (p - c)·(a × n) = 0, unnormalised: normals parallel to the axis (flat
                // annuli) carry no axis information and must not amplify noise.
                return (p - C).Dot(A.Cross(NormalOf[p]));
            }
        }

        /// <param name="hint">Fitted surface of the region; its axis (cylinder, cone, torus) is used as the start.</param>
        public static RevolutionProfile Revolution(TriMesh m, IList<int> tris, double tolerance, FittedSurface hint = null,
                                                   double marginDegrees = 2, double snapDegrees = 1)
        {
            var verts = m.VerticesOf(tris);
            if (verts.Length < 10) throw new ArgumentException("The region is too small.");
            var pts = verts.Select(v => m.Vertices[v]).ToArray();
            var map = new Dictionary<Vec3, Vec3>();
            foreach (int v in verts) map[m.Vertices[v]] = m.VertexNormals[v];
            pts = map.Keys.ToArray();
            double scale = AnalyticFitters.Scale(pts);

            var starts = new List<AxisState>();
            switch (hint)
            {
                case CylinderSurface cy: starts.Add(new AxisState { C = cy.AxisPoint, A = cy.Axis, NormalOf = map }); break;
                case ConeSurface co: starts.Add(new AxisState { C = co.Center, A = co.Axis, NormalOf = map }); break;
                case TorusSurface to: starts.Add(new AxisState { C = to.Center, A = to.Axis, NormalOf = map }); break;
            }
            var cen = AnalyticFitters.Centroid(pts);
            var pott = PottmannAxis(pts, map, cen);
            if (pott != null) starts.Insert(0, new AxisState { C = pott.Value.c, A = pott.Value.a, NormalOf = map });
            AnalyticFitters.Pca(pts, cen, out _, out var pv);
            foreach (var a in pv)
            {
                // Start each candidate axis through the least-squares meeting point of the normal lines.
                var st = new AxisState { C = cen, A = a, NormalOf = map };
                starts.Add(st);
            }
            AxisState best = null; double bestCost = double.MaxValue;
            var r = new double[pts.Length];
            foreach (var s0 in starts)
            {
                var st = (AxisState)LevenbergMarquardt.Minimize(s0, pts, null, scale, 60);
                double cost = LevenbergMarquardt.Evaluate(st, pts, null, r);
                if (cost < bestCost && st.A.IsFinite && st.C.IsFinite) { bestCost = cost; best = st; }
            }
            var axis = Snap(best.A, snapDegrees);
            var c = best.C;

            // Angular range of the region around the axis and the (radius, height) profile.
            var f = Frame.FromAxis(c, axis);
            var ang = new double[pts.Length];
            var rh = new List<(double, double)>(pts.Length);
            for (int i = 0; i < pts.Length; i++)
            {
                var l = f.ToLocal(pts[i]);
                ang[i] = Math.Atan2(l.Y, l.X);
                rh.Add((Math.Sqrt(l.X * l.X + l.Y * l.Y), l.Z));
            }
            AngleUtil.CoveringArc(ang, out double s0a, out double span);
            double sweep;
            if (span >= 2 * Math.PI - 1e-9) { sweep = 2 * Math.PI; s0a = 0; }
            else
            {
                double mg = marginDegrees * Math.PI / 180;
                s0a -= mg;
                sweep = Math.Min(2 * Math.PI, span + 2 * mg);
            }
            // Sketch plane contains the axis, at the start angle: X = radial, Y = axis.
            var radial = f.XAxis * Math.Cos(s0a) + f.YAxis * Math.Sin(s0a);
            var plane = new Frame(c, radial.Cross(axis), radial);   // Z = X × Y with Y = axis
            if (plane.YAxis.Dot(axis) < 0) plane = new Frame(c, axis.Cross(radial), radial);

            var prof = new RevolutionProfile
            {
                Plane = plane, AxisPoint = c, Axis = axis, Sweep = sweep,
                AxisResidual = Math.Sqrt(bestCost / pts.Length)
            };
            // Profile coordinates: x = radius, y = height along the axis (plane Y is ±axis).
            double sign = plane.YAxis.Dot(axis) >= 0 ? 1 : -1;
            BuildProfile(prof, rh.Select(p => (p.Item1, sign * p.Item2)).ToList(), m.AverageEdgeLength, tolerance);
            return prof;
        }

        /// <summary>
        /// Axis of a surface of revolution from the linear line complex of its normals (Pottmann): every
        /// normal line (n, p×n) meets the axis (a, ā), i.e. a·(p×n) + ā·n = 0. Smallest eigenvector of a 6×6 matrix.
        /// </summary>
        private static (Vec3 c, Vec3 a)? PottmannAxis(Vec3[] pts, Dictionary<Vec3, Vec3> normalOf, Vec3 centre)
        {
            var M = new double[6, 6];
            var v = new double[6];
            foreach (var p0 in pts)
            {
                var n = normalOf[p0];
                var p = p0 - centre;
                var m = p.Cross(n);
                v[0] = m.X; v[1] = m.Y; v[2] = m.Z; v[3] = n.X; v[4] = n.Y; v[5] = n.Z;
                for (int i = 0; i < 6; i++) for (int j = 0; j < 6; j++) M[i, j] += v[i] * v[j];
            }
            LinearAlgebra.SymmetricEigen(M, out _, out var vecs);
            var e = vecs[0];
            var a = new Vec3(e[0], e[1], e[2]);
            var abar = new Vec3(e[3], e[4], e[5]);
            double l2 = a.LengthSquared;
            if (l2 < 1e-12) return null;
            var c = a.Cross(abar) / l2;
            return (c + centre, a.Normalized());
        }

        // ------------------------------------------------------------------ profile fitting

        private static void BuildProfile(SketchProfile prof, List<(double x, double y)> cloud, double edge, double tol)
        {
            var path = OrderBand(cloud, edge, out bool closed);
            if (path.Count < 3) throw new InvalidOperationException("Could not trace a profile — is the region an extrusion / revolution?");
            var index = new CloudIndex(cloud, Math.Max(edge * 3, 1e-9));
            prof.Closed = closed;
            prof.Segments = Refine(LinesAndArcs(path, closed, tol), cloud, index, tol, closed);
            if (!closed) ExtendEnds(prof.Segments, cloud, tol);
            prof.Segments = MergeRedundant(prof.Segments, cloud, tol, closed);
            // Deviation of every collapsed point from the profile.
            double max = 0, sum2 = 0;
            foreach (var (x, y) in cloud)
            {
                double d = prof.Segments.Min(s => DistanceToSegment(s, x, y));
                max = Math.Max(max, d); sum2 += d * d;
            }
            prof.MaxDeviation = max;
            prof.RmsDeviation = Math.Sqrt(sum2 / Math.Max(1, cloud.Count));
        }

        /// <summary>Uniform grid over 2D points for radius queries.</summary>
        private sealed class CloudIndex
        {
            private readonly Dictionary<(long, long), List<int>> _cells = new Dictionary<(long, long), List<int>>();
            private readonly double _h;
            public readonly List<(double x, double y)> Pts;
            public CloudIndex(List<(double x, double y)> pts, double h)
            {
                Pts = pts; _h = h;
                for (int i = 0; i < pts.Count; i++)
                {
                    var k = ((long)Math.Floor(pts[i].x / h), (long)Math.Floor(pts[i].y / h));
                    if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new List<int>();
                    l.Add(i);
                }
            }
            public IEnumerable<int> Near(double x, double y, double r)
            {
                long x0 = (long)Math.Floor((x - r) / _h), x1 = (long)Math.Floor((x + r) / _h);
                long y0 = (long)Math.Floor((y - r) / _h), y1 = (long)Math.Floor((y + r) / _h);
                double r2 = r * r;
                for (long i = x0; i <= x1; i++)
                    for (long j = y0; j <= y1; j++)
                        if (_cells.TryGetValue((i, j), out var l))
                            foreach (int k in l)
                            {
                                double dx = Pts[k].x - x, dy = Pts[k].y - y;
                                if (dx * dx + dy * dy <= r2) yield return k;
                            }
            }
        }

        /// <summary>Moves each centre-line sample onto the local least-squares line of the point band (removes zig-zag).</summary>
        private static List<(double x, double y)> ProjectOntoBand(List<(double x, double y)> path, CloudIndex idx, double r)
        {
            var outp = new List<(double x, double y)>(path.Count);
            foreach (var (x, y) in path)
            {
                double sx = 0, sy = 0; int n = 0;
                var near = idx.Near(x, y, r).ToList();
                foreach (int k in near) { sx += idx.Pts[k].x; sy += idx.Pts[k].y; n++; }
                if (n < 5) { outp.Add((x, y)); continue; }
                double mx = sx / n, my = sy / n, cxx = 0, cxy = 0, cyy = 0;
                foreach (int k in near) { double dx = idx.Pts[k].x - mx, dy = idx.Pts[k].y - my; cxx += dx * dx; cxy += dx * dy; cyy += dy * dy; }
                double ang = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
                double ux = Math.Cos(ang), uy = Math.Sin(ang);
                double t = (x - mx) * ux + (y - my) * uy;
                outp.Add((mx + t * ux, my + t * uy));
            }
            return outp;
        }

        /// <summary>
        /// Refits every segment to the scan points it owns (lines by least squares, arcs by geometric circle
        /// fit) and rebuilds the joints: tangent joints are made exactly tangent, others become intersections.
        /// Falls back to the original joint when the refit would move it by more than a few tolerances.
        /// </summary>
        private static List<ProfileSegment> Refine(List<ProfileSegment> segs, List<(double x, double y)> cloud, CloudIndex idx, double tol, bool closed)
        {
            int n = segs.Count;
            if (n == 0) return segs;
            // Owned points: nearest segment, excluding points near the joints (they belong to both).
            var owned = new List<(double x, double y)>[n];
            for (int i = 0; i < n; i++) owned[i] = new List<(double x, double y)>();
            double jointGap = 3 * tol + 1e-12;
            foreach (var (x, y) in cloud)
            {
                int best = -1; double bd = double.MaxValue;
                for (int i = 0; i < n; i++) { double d = DistanceToSegment(segs[i], x, y); if (d < bd) { bd = d; best = i; } }
                if (bd > 4 * tol) continue;
                var s = segs[best];
                double l = Math.Min(Dist(x - s.X0, y - s.Y0), Dist(x - s.X1, y - s.Y1));
                if (l < Math.Max(jointGap, 0.1 * SegmentLength(s))) continue;
                owned[best].Add((x, y));
            }
            // Refit the carrier geometry of each segment.
            var lines = new (double px, double py, double ux, double uy)?[n];
            var circles = new (double cx, double cy, double r)?[n];
            for (int i = 0; i < n; i++)
            {
                var pts = owned[i];
                if (pts.Count < 6) continue;
                if (!segs[i].IsArc)
                {
                    double mx = pts.Average(p => p.x), my = pts.Average(p => p.y), cxx = 0, cxy = 0, cyy = 0;
                    foreach (var p in pts) { double dx = p.x - mx, dy = p.y - my; cxx += dx * dx; cxy += dx * dy; cyy += dy * dy; }
                    double ang = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
                    lines[i] = (mx, my, Math.Cos(ang), Math.Sin(ang));
                }
                else if (AnalyticFitters.FitCircle2D(pts.Select(p => p.x).ToList(), pts.Select(p => p.y).ToList(), out double cx, out double cy, out double r))
                {
                    // Geometric refinement (a few Gauss-Newton steps).
                    for (int it = 0; it < 10; it++)
                    {
                        double a11 = 0, a12 = 0, a13 = 0, a22 = 0, a23 = 0, a33 = 0, b1 = 0, b2 = 0, b3 = 0;
                        foreach (var p in pts)
                        {
                            double dx = p.x - cx, dy = p.y - cy, d = Dist(dx, dy);
                            if (d < 1e-15) continue;
                            double j1 = -dx / d, j2 = -dy / d, e = d - r;
                            a11 += j1 * j1; a12 += j1 * j2; a13 -= j1; a22 += j2 * j2; a23 -= j2; a33 += 1;
                            b1 -= j1 * e; b2 -= j2 * e; b3 += e;
                        }
                        var sol = LinearAlgebra.SolveSymmetric(new[,] { { a11, a12, a13 }, { a12, a22, a23 }, { a13, a23, a33 } }, new[] { b1, b2, b3 });
                        if (sol == null) break;
                        cx += sol[0]; cy += sol[1]; r += sol[2];
                    }
                    if (Math.Abs(r - segs[i].Radius) < 0.2 * segs[i].Radius) circles[i] = (cx, cy, r);
                }
            }
            // Tangent snapping between a line and a neighbouring arc: move the circle centre so its distance to the line is R.
            for (int i = 0; i < n; i++)
            {
                if (!circles[i].HasValue) continue;
                foreach (int k in new[] { i - 1, i + 1 })
                {
                    int kk = closed ? (k + n) % n : k;
                    if (kk < 0 || kk >= n || !lines[kk].HasValue) continue;
                    var (cx, cy, r) = circles[i].Value;
                    var (px, py, ux, uy) = lines[kk].Value;
                    double nx = -uy, ny = ux, sd = (cx - px) * nx + (cy - py) * ny;
                    if (Math.Abs(Math.Abs(sd) - r) < 2 * tol)
                    {
                        double shift = Math.Sign(sd) * r - sd;
                        circles[i] = (cx + nx * shift, cy + ny * shift, r);
                    }
                }
            }
            // New joints.
            var result = segs.Select(Clone).ToList();
            int joints = closed ? n : n - 1;
            for (int j = 0; j < joints; j++)
            {
                int a = j, b = (j + 1) % n;
                var old = (x: segs[a].X1, y: segs[a].Y1);
                var nj = Joint(lines[a], circles[a], lines[b], circles[b], old);
                if (!nj.HasValue || Dist(nj.Value.x - old.x, nj.Value.y - old.y) > 5 * tol + 0.02 * SegmentLength(segs[a])) continue;
                result[a].X1 = nj.Value.x; result[a].Y1 = nj.Value.y;
                result[b].X0 = nj.Value.x; result[b].Y0 = nj.Value.y;
            }
            // Open ends: project onto the refitted carriers.
            if (!closed)
            {
                ProjectEnd(result[0], lines[0], circles[0], start: true);
                ProjectEnd(result[n - 1], lines[n - 1], circles[n - 1], start: false);
            }
            for (int i = 0; i < n; i++)
                if (result[i].IsArc && circles[i].HasValue)
                {
                    var (cx, cy, r) = circles[i].Value;
                    // Keep the arc if its end points sit on the refitted circle; otherwise keep the original arc.
                    if (Math.Abs(Dist(result[i].X0 - cx, result[i].Y0 - cy) - r) < 2 * tol && Math.Abs(Dist(result[i].X1 - cx, result[i].Y1 - cy) - r) < 2 * tol)
                    { result[i].Cx = cx; result[i].Cy = cy; result[i].Radius = r; }
                    else result[i] = segs[i];
                }
            return result;
        }

        /// <summary>
        /// Removes segments that the neighbouring segments can absorb (typically a short transition piece
        /// at a tangent arc/line joint): the neighbours are re-joined at their tangent point / intersection and
        /// the change is kept when every nearby scan point stays within tolerance.
        /// </summary>
        private static List<ProfileSegment> MergeRedundant(List<ProfileSegment> segs, List<(double x, double y)> cloud, double tol, bool closed)
        {
            var list = segs.Select(Clone).ToList();
            bool changed = true;
            while (changed && list.Count > 1)
            {
                changed = false;
                // Shortest segments first.
                foreach (int i in Enumerable.Range(0, list.Count).OrderBy(k => SegmentLength(list[k])).ToList())
                {
                    int n = list.Count;
                    int a = i - 1, b = i + 1;
                    if (closed) { a = (a + n) % n; b %= n; }
                    if (a < 0 || b >= n || a == b || a == i) continue;
                    var sa = list[a]; var sb = list[b];
                    // Points of a, i and b, split between a and b by the nearer carrier; carriers refitted to them.
                    var pa = new List<(double x, double y)>(); var pb = new List<(double x, double y)>();
                    foreach (var q in cloud)
                    {
                        double da = DistanceToSegment(sa, q.x, q.y), db = DistanceToSegment(sb, q.x, q.y), di = DistanceToSegment(list[i], q.x, q.y);
                        if (Math.Min(da, Math.Min(db, di)) > 3 * tol) continue;
                        if (di < da && di < db)
                        {
                            var ca0 = Carrier(sa); var cb0 = Carrier(sb);
                            if (CarrierDistance(ca0, q) <= CarrierDistance(cb0, q)) pa.Add(q); else pb.Add(q);
                        }
                        else if (da <= db) pa.Add(q); else pb.Add(q);
                    }
                    var ca = Refit(sa, pa); var cb = Refit(sb, pb);
                    var joint = Joint(ca.line, ca.circle, cb.line, cb.circle, ((list[i].X0 + list[i].X1) / 2, (list[i].Y0 + list[i].Y1) / 2));
                    if (!joint.HasValue) continue;
                    var na = Clone(sa); na.X1 = joint.Value.x; na.Y1 = joint.Value.y;
                    var nb = Clone(sb); nb.X0 = joint.Value.x; nb.Y0 = joint.Value.y;
                    if (ca.circle.HasValue) { na.Cx = ca.circle.Value.cx; na.Cy = ca.circle.Value.cy; na.Radius = ca.circle.Value.r; }
                    if (cb.circle.HasValue) { nb.Cx = cb.circle.Value.cx; nb.Cy = cb.circle.Value.cy; nb.Radius = cb.circle.Value.r; }
                    if (ca.line.HasValue) ProjectEnd(na, ca.line, null, start: true);
                    if (cb.line.HasValue) ProjectEnd(nb, cb.line, null, start: false);
                    // Every point currently explained by a, i or b must stay within tolerance of a' ∪ b'.
                    bool ok = true;
                    foreach (var (x, y) in cloud)
                    {
                        double before = Math.Min(DistanceToSegment(list[i], x, y), Math.Min(DistanceToSegment(sa, x, y), DistanceToSegment(sb, x, y)));
                        if (before > 3 * tol) continue;
                        if (Math.Min(DistanceToSegment(na, x, y), DistanceToSegment(nb, x, y)) > Math.Max(tol, before)) { ok = false; break; }
                    }
                    if (!ok) continue;
                    list[a] = na; list[b] = nb;
                    list.RemoveAt(i);
                    changed = true;
                    break;
                }
            }
            return list;
        }

        private static (double px, double py, double ux, double uy)? FitLine(List<(double x, double y)> pts)
        {
            if (pts.Count < 2) return null;
            double mx = pts.Average(p => p.x), my = pts.Average(p => p.y), cxx = 0, cxy = 0, cyy = 0;
            foreach (var p in pts) { double dx = p.x - mx, dy = p.y - my; cxx += dx * dx; cxy += dx * dy; cyy += dy * dy; }
            double ang = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
            return (mx, my, Math.Cos(ang), Math.Sin(ang));
        }

        private static (double cx, double cy, double r)? FitCircle(List<(double x, double y)> pts, double cx, double cy, double r)
        {
            if (pts.Count < 3) return null;
            for (int it = 0; it < 12; it++)
            {
                double a11 = 0, a12 = 0, a13 = 0, a22 = 0, a23 = 0, a33 = 0, b1 = 0, b2 = 0, b3 = 0;
                foreach (var p in pts)
                {
                    double dx = p.x - cx, dy = p.y - cy, d = Dist(dx, dy);
                    if (d < 1e-15) continue;
                    double j1 = -dx / d, j2 = -dy / d, e = d - r;
                    a11 += j1 * j1; a12 += j1 * j2; a13 -= j1; a22 += j2 * j2; a23 -= j2; a33 += 1;
                    b1 -= j1 * e; b2 -= j2 * e; b3 += e;
                }
                var sol = LinearAlgebra.SolveSymmetric(new[,] { { a11, a12, a13 }, { a12, a22, a23 }, { a13, a23, a33 } }, new[] { b1, b2, b3 });
                if (sol == null) return null;
                cx += sol[0]; cy += sol[1]; r += sol[2];
                if (Math.Abs(sol[0]) + Math.Abs(sol[1]) + Math.Abs(sol[2]) < 1e-12 * (1 + r)) break;
            }
            return r > 0 ? (cx, cy, r) : ((double, double, double)?)null;
        }

        /// <summary>Stretches the open ends of the profile so it covers every scan point beyond them.</summary>
        private static void ExtendEnds(List<ProfileSegment> segs, List<(double x, double y)> cloud, double tol)
        {
            var first = segs[0]; var last = segs[segs.Count - 1];
            foreach (var (seg, start) in new[] { (first, true), (last, false) })
            {
                double ex = start ? seg.X0 : seg.X1, ey = start ? seg.Y0 : seg.Y1;
                if (!seg.IsArc)
                {
                    double dx = seg.X1 - seg.X0, dy = seg.Y1 - seg.Y0, l = Dist(dx, dy);
                    if (l < 1e-15) continue;
                    double ux = dx / l, uy = dy / l;
                    double best = 0; // extension beyond the end along the line
                    foreach (var (x, y) in cloud)
                    {
                        double t = (x - seg.X0) * ux + (y - seg.Y0) * uy;          // 0..l inside
                        double off = Math.Abs((x - seg.X0) * uy - (y - seg.Y0) * ux);
                        if (off > 3 * tol) continue;
                        double beyond = start ? -t : t - l;
                        if (beyond > best && beyond < 0.5 * l) best = beyond;
                    }
                    if (start) { seg.X0 -= ux * best; seg.Y0 -= uy * best; }
                    else { seg.X1 += ux * best; seg.Y1 += uy * best; }
                }
                else
                {
                    double sw = Sweep(seg), extra = 0;
                    foreach (var (x, y) in cloud)
                    {
                        if (Math.Abs(Dist(x - seg.Cx, y - seg.Cy) - seg.Radius) > 3 * tol) continue;
                        double a = AngleFromStart(seg, x, y);
                        if (a <= sw) continue;
                        // Beyond the end (a slightly above sweep) or before the start (a slightly below 2π).
                        double beyond = start ? 2 * Math.PI - a : a - sw;
                        if (beyond > 0 && beyond < 0.5 && beyond > extra) extra = beyond;
                    }
                    if (extra <= 0) continue;
                    double s0 = Math.Atan2(seg.Y0 - seg.Cy, seg.X0 - seg.Cx), s1 = Math.Atan2(seg.Y1 - seg.Cy, seg.X1 - seg.Cx);
                    double dir = seg.CounterClockwise ? 1 : -1;
                    if (start) { s0 -= dir * extra; seg.X0 = seg.Cx + seg.Radius * Math.Cos(s0); seg.Y0 = seg.Cy + seg.Radius * Math.Sin(s0); }
                    else { s1 += dir * extra; seg.X1 = seg.Cx + seg.Radius * Math.Cos(s1); seg.Y1 = seg.Cy + seg.Radius * Math.Sin(s1); }
                }
            }
        }

        private static ((double px, double py, double ux, double uy)? line, (double cx, double cy, double r)? circle) Refit(ProfileSegment s, List<(double x, double y)> pts)
        {
            if (s.IsArc) return (null, FitCircle(pts, s.Cx, s.Cy, s.Radius) ?? (s.Cx, s.Cy, s.Radius));
            return (FitLine(pts) ?? Carrier(s).line, null);
        }

        private static double CarrierDistance(((double px, double py, double ux, double uy)? line, (double cx, double cy, double r)? circle) c, (double x, double y) q)
        {
            if (c.circle.HasValue) return Math.Abs(Dist(q.x - c.circle.Value.cx, q.y - c.circle.Value.cy) - c.circle.Value.r);
            if (c.line.HasValue) return Math.Abs((q.x - c.line.Value.px) * c.line.Value.uy - (q.y - c.line.Value.py) * c.line.Value.ux);
            return double.MaxValue;
        }

        private static ((double px, double py, double ux, double uy)? line, (double cx, double cy, double r)? circle) Carrier(ProfileSegment s)
        {
            if (s.IsArc) return (null, (s.Cx, s.Cy, s.Radius));
            double dx = s.X1 - s.X0, dy = s.Y1 - s.Y0, l = Dist(dx, dy);
            if (l < 1e-15) return (null, null);
            return ((s.X0, s.Y0, dx / l, dy / l), null);
        }

        private static (double x, double y)? Joint((double px, double py, double ux, double uy)? la, (double cx, double cy, double r)? ca,
                                                  (double px, double py, double ux, double uy)? lb, (double cx, double cy, double r)? cb,
                                                  (double x, double y) old)
        {
            if (la.HasValue && lb.HasValue)
            {
                var (p1x, p1y, u1x, u1y) = la.Value; var (p2x, p2y, u2x, u2y) = lb.Value;
                double den = u1x * u2y - u1y * u2x;
                if (Math.Abs(den) < 1e-9) return null;
                double t = ((p2x - p1x) * u2y - (p2y - p1y) * u2x) / den;
                return (p1x + t * u1x, p1y + t * u1y);
            }
            if (la.HasValue && cb.HasValue) return LineCircle(la.Value, cb.Value, old);
            if (ca.HasValue && lb.HasValue) return LineCircle(lb.Value, ca.Value, old);
            if (ca.HasValue && cb.HasValue)
            {
                var (x1, y1, r1) = ca.Value; var (x2, y2, r2) = cb.Value;
                double d = Dist(x2 - x1, y2 - y1);
                if (d < 1e-12) return null;
                // Tangent arcs: joint on the centre line; otherwise the nearer intersection.
                if (Math.Abs(d - (r1 + r2)) < 0.01 * d || Math.Abs(d - Math.Abs(r1 - r2)) < 0.01 * d)
                {
                    double s = (d - (r1 + r2)) < 0.01 * d && Math.Abs(d - (r1 + r2)) < 0.01 * d ? r1 / d : (r1 > r2 ? r1 / d : -r1 / d);
                    return (x1 + (x2 - x1) * s, y1 + (y2 - y1) * s);
                }
                double a = (r1 * r1 - r2 * r2 + d * d) / (2 * d), h2 = r1 * r1 - a * a;
                if (h2 < 0) return null;
                double h = Math.Sqrt(h2), mx = x1 + a * (x2 - x1) / d, my = y1 + a * (y2 - y1) / d;
                var i1 = (x: mx + h * (y2 - y1) / d, y: my - h * (x2 - x1) / d);
                var i2 = (x: mx - h * (y2 - y1) / d, y: my + h * (x2 - x1) / d);
                return Dist(i1.x - old.x, i1.y - old.y) < Dist(i2.x - old.x, i2.y - old.y) ? i1 : i2;
            }
            return null;
        }

        private static (double x, double y)? LineCircle((double px, double py, double ux, double uy) l, (double cx, double cy, double r) c, (double x, double y) old)
        {
            var (px, py, ux, uy) = l; var (cx, cy, r) = c;
            // Foot of the centre on the line: the tangent point when the line is tangent.
            double t0 = (cx - px) * ux + (cy - py) * uy;
            double fx = px + t0 * ux, fy = py + t0 * uy, dist = Dist(cx - fx, cy - fy);
            if (Math.Abs(dist - r) < 1e-6 * Math.Max(r, 1e-9) || dist > r) return (fx, fy);
            double h = Math.Sqrt(r * r - dist * dist);
            var a = (x: fx + h * ux, y: fy + h * uy); var b = (x: fx - h * ux, y: fy - h * uy);
            return Dist(a.x - old.x, a.y - old.y) < Dist(b.x - old.x, b.y - old.y) ? a : b;
        }

        private static void ProjectEnd(ProfileSegment s, (double px, double py, double ux, double uy)? l, (double cx, double cy, double r)? c, bool start)
        {
            double x = start ? s.X0 : s.X1, y = start ? s.Y0 : s.Y1;
            if (!s.IsArc && l.HasValue)
            {
                var (px, py, ux, uy) = l.Value;
                double t = (x - px) * ux + (y - py) * uy;
                x = px + t * ux; y = py + t * uy;
            }
            else if (s.IsArc && c.HasValue)
            {
                var (cx, cy, r) = c.Value;
                double d = Dist(x - cx, y - cy);
                if (d > 1e-12) { x = cx + (x - cx) * r / d; y = cy + (y - cy) * r / d; }
            }
            if (start) { s.X0 = x; s.Y0 = y; } else { s.X1 = x; s.Y1 = y; }
        }

        private static ProfileSegment Clone(ProfileSegment s) => new ProfileSegment
        {
            IsArc = s.IsArc, X0 = s.X0, Y0 = s.Y0, X1 = s.X1, Y1 = s.Y1, Cx = s.Cx, Cy = s.Cy, Radius = s.Radius, CounterClockwise = s.CounterClockwise
        };

        private static double SegmentLength(ProfileSegment s) =>
            s.IsArc ? s.Radius * Sweep(s) : Dist(s.X1 - s.X0, s.Y1 - s.Y0);

        /// <summary>
        /// Orders a noisy band of 2D points into its centre line: grid-averaged nodes, a minimum spanning
        /// tree over near neighbours and its longest path. Detects closed profiles.
        /// </summary>
        public static List<(double x, double y)> OrderBand(List<(double x, double y)> cloud, double edge, out bool closed)
        {
            double h = Math.Max(edge * 1.5, 1e-9);
            var cells = new Dictionary<(long, long), (double sx, double sy, int n)>();
            foreach (var (x, y) in cloud)
            {
                var key = ((long)Math.Floor(x / h), (long)Math.Floor(y / h));
                cells.TryGetValue(key, out var c);
                cells[key] = (c.sx + x, c.sy + y, c.n + 1);
            }
            var nodes = cells.Values.Where(c => c.n > 0).Select(c => (x: c.sx / c.n, y: c.sy / c.n)).ToList();
            // Coarsen very large bands so the MST stays fast.
            while (nodes.Count > 3000)
            {
                h *= 1.5;
                var c2 = new Dictionary<(long, long), (double sx, double sy, int n)>();
                foreach (var (x, y) in nodes)
                {
                    var key = ((long)Math.Floor(x / h), (long)Math.Floor(y / h));
                    c2.TryGetValue(key, out var c);
                    c2[key] = (c.sx + x, c.sy + y, c.n + 1);
                }
                nodes = c2.Values.Select(c => (x: c.sx / c.n, y: c.sy / c.n)).ToList();
            }
            int n = nodes.Count;
            closed = false;
            if (n < 3) return nodes;

            // Prim's MST on the complete graph restricted to neighbours within 4h (fallback: all).
            var inTree = new bool[n];
            var best = Enumerable.Repeat(double.MaxValue, n).ToArray();
            var parent = Enumerable.Repeat(-1, n).ToArray();
            best[0] = 0;
            var adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>();
            for (int it = 0; it < n; it++)
            {
                int u = -1;
                for (int i = 0; i < n; i++) if (!inTree[i] && (u < 0 || best[i] < best[u])) u = i;
                inTree[u] = true;
                if (parent[u] >= 0) { adj[u].Add(parent[u]); adj[parent[u]].Add(u); }
                for (int v = 0; v < n; v++)
                {
                    if (inTree[v]) continue;
                    double dx = nodes[u].x - nodes[v].x, dy = nodes[u].y - nodes[v].y, d2 = dx * dx + dy * dy;
                    if (d2 < best[v]) { best[v] = d2; parent[v] = u; }
                }
            }
            // Longest path in the tree (two farthest searches).
            int a = Farthest(nodes, adj, 0, out _);
            int b = Farthest(nodes, adj, a, out var prev);
            var path = new List<(double x, double y)>();
            for (int v = b; v >= 0; v = prev[v]) path.Add(nodes[v]);

            // Smooth the zig-zag of the grid averaging, then decide whether the profile closes on itself.
            var smooth = FeatureCurves.Taubin(path.Select(p => new Vec3(p.x, p.y, 0)).ToList(), false, 20);
            double len = 0;
            for (int i = 1; i < smooth.Count; i++) len += smooth[i].DistanceTo(smooth[i - 1]);
            closed = smooth[0].DistanceTo(smooth[smooth.Count - 1]) < 4 * h && len > 20 * h;
            double spacing = Math.Max(h, len / 160);
            var res = FeatureCurves.Resample(smooth, closed, spacing);
            return res.Select(p => (p.X, p.Y)).ToList();
        }

        private static int Farthest(List<(double x, double y)> nodes, List<int>[] adj, int src, out int[] prev)
        {
            int n = nodes.Count;
            var dist = Enumerable.Repeat(double.MaxValue, n).ToArray();
            prev = Enumerable.Repeat(-1, n).ToArray();
            dist[src] = 0;
            var stack = new Stack<int>();
            stack.Push(src);
            while (stack.Count > 0)
            {
                int u = stack.Pop();
                foreach (int v in adj[u])
                {
                    if (dist[v] != double.MaxValue) continue;
                    double dx = nodes[u].x - nodes[v].x, dy = nodes[u].y - nodes[v].y;
                    dist[v] = dist[u] + Math.Sqrt(dx * dx + dy * dy);
                    prev[v] = u;
                    stack.Push(v);
                }
            }
            int best = src;
            for (int i = 0; i < n; i++) if (dist[i] != double.MaxValue && dist[i] > dist[best]) best = i;
            return best;
        }

        /// <summary>
        /// Fewest lines and arcs (consecutive, sharing end points) within tolerance of the ordered points,
        /// by dynamic programming over break points. Lines win ties.
        /// </summary>
        public static List<ProfileSegment> LinesAndArcs(List<(double x, double y)> p, bool closed, double tol)
        {
            var q = new List<(double x, double y)>(p);
            if (closed) q.Add(q[0]);
            int n = q.Count;
            var cost = Enumerable.Repeat(double.MaxValue, n).ToArray();
            var from = new int[n];
            var seg = new ProfileSegment[n];
            cost[0] = 0;
            for (int j = 1; j < n; j++)
            {
                for (int i = j - 1; i >= 0; i--)
                {
                    if (cost[i] == double.MaxValue) continue;
                    var s = TrySegment(q, i, j, tol);
                    if (s == null)
                    {
                        // Longer spans won't fit as lines/arcs once a short one fails badly; keep scanning a bit.
                        if (j - i > 8 && !ArcPossible(q, i, j, tol * 6)) break;
                        continue;
                    }
                    double c = cost[i] + (s.IsArc ? 1.05 : 1.0);
                    if (c < cost[j]) { cost[j] = c; from[j] = i; seg[j] = s; }
                }
                if (cost[j] == double.MaxValue) { cost[j] = cost[j - 1] + 1; from[j] = j - 1; seg[j] = Line(q, j - 1, j); }
            }
            var list = new List<ProfileSegment>();
            for (int j = n - 1; j > 0; j = from[j]) list.Add(seg[j]);
            list.Reverse();
            return list;
        }

        private static ProfileSegment TrySegment(List<(double x, double y)> q, int i, int j, double tol)
        {
            var line = Line(q, i, j);
            if (MaxLineDeviation(q, i, j) <= tol) return line;
            if (j - i < 3) return null;
            var arc = Arc(q, i, j);
            if (arc == null) return null;
            for (int k = i + 1; k < j; k++)
                if (Math.Abs(Dist(q[k].x - arc.Cx, q[k].y - arc.Cy) - arc.Radius) > tol) return null;
            // The points must progress monotonically around the arc (no folding back).
            double prev = 0, sweep = Sweep(arc);
            for (int k = i; k <= j; k++)
            {
                double t = AngleFromStart(arc, q[k].x, q[k].y);
                if (t + 1e-6 < prev || t > sweep + 1e-6) return null;
                prev = t;
            }
            return arc;
        }

        private static bool ArcPossible(List<(double x, double y)> q, int i, int j, double looseTol)
        {
            var arc = Arc(q, i, j);
            if (arc == null) return MaxLineDeviation(q, i, j) <= looseTol;
            for (int k = i + 1; k < j; k++)
                if (Math.Abs(Dist(q[k].x - arc.Cx, q[k].y - arc.Cy) - arc.Radius) > looseTol) return false;
            return true;
        }

        private static ProfileSegment Line(List<(double x, double y)> q, int i, int j) =>
            new ProfileSegment { X0 = q[i].x, Y0 = q[i].y, X1 = q[j].x, Y1 = q[j].y };

        private static double MaxLineDeviation(List<(double x, double y)> q, int i, int j)
        {
            double ax = q[i].x, ay = q[i].y, dx = q[j].x - ax, dy = q[j].y - ay, len = Dist(dx, dy);
            double max = 0;
            for (int k = i + 1; k < j; k++)
            {
                double px = q[k].x - ax, py = q[k].y - ay;
                double d = len < 1e-15 ? Dist(px, py) : Math.Abs(px * dy - py * dx) / len;
                // Points must lie between the end points too.
                double t = len < 1e-15 ? 0 : (px * dx + py * dy) / (len * len);
                if (t < -0.02 || t > 1.02) d = Math.Max(d, Math.Min(Dist(px, py), Dist(q[k].x - q[j].x, q[k].y - q[j].y)));
                max = Math.Max(max, d);
            }
            return max;
        }

        /// <summary>Least-squares circle through the two end points (centre on their bisector).</summary>
        private static ProfileSegment Arc(List<(double x, double y)> q, int i, int j)
        {
            double ax = q[i].x, ay = q[i].y, bx = q[j].x, by = q[j].y;
            double mx = (ax + bx) / 2, my = (ay + by) / 2, dx = bx - ax, dy = by - ay, half = Dist(dx, dy) / 2;
            if (half < 1e-12) return null;
            double wx = -dy / (2 * half), wy = dx / (2 * half);       // unit normal of the chord
            // Start from the circle through the end points and the middle point.
            var mid = q[(i + j) / 2];
            double t = Circumcentre(ax, ay, mid.x, mid.y, bx, by, wx, wy, mx, my);
            if (double.IsNaN(t)) return null;
            for (int it = 0; it < 8; it++)
            {
                double cx = mx + t * wx, cy = my + t * wy, r = Math.Sqrt(half * half + t * t);
                double g = 0, hh = 0;
                for (int k = i + 1; k < j; k++)
                {
                    double ux = q[k].x - cx, uy = q[k].y - cy, dk = Dist(ux, uy);
                    if (dk < 1e-15) continue;
                    double e = dk - r;
                    double de = -(ux * wx + uy * wy) / dk - t / r;   // d(e)/dt
                    g += e * de; hh += de * de;
                }
                if (hh < 1e-30) break;
                double step = g / hh;
                t -= step;
                if (Math.Abs(step) < 1e-12 * (1 + Math.Abs(t))) break;
            }
            if (Math.Abs(t) > 1e6 * half) return null; // effectively a line
            double ccx = mx + t * wx, ccy = my + t * wy;
            // Direction: counter-clockwise if the middle point is to the left of start→end … measured via cross product.
            double cross = (q[(i + j) / 2].x - ax) * dy - (q[(i + j) / 2].y - ay) * dx;
            return new ProfileSegment
            {
                IsArc = true, X0 = ax, Y0 = ay, X1 = bx, Y1 = by, Cx = ccx, Cy = ccy,
                Radius = Math.Sqrt(half * half + t * t), CounterClockwise = cross > 0
            };
        }

        private static double Circumcentre(double ax, double ay, double px, double py, double bx, double by,
                                           double wx, double wy, double mx, double my)
        {
            // Centre = m + t w with |c - a| = |c - p|.
            double ux = mx - ax, uy = my - ay, vx = mx - px, vy = my - py;
            double den = 2 * ((vx - ux) * wx + (vy - uy) * wy);
            if (Math.Abs(den) < 1e-15) return double.NaN;
            return ((ux * ux + uy * uy) - (vx * vx + vy * vy)) / den;
        }

        private static double Sweep(ProfileSegment a)
        {
            double s = Math.Atan2(a.Y0 - a.Cy, a.X0 - a.Cx), e = Math.Atan2(a.Y1 - a.Cy, a.X1 - a.Cx);
            double sw = a.CounterClockwise ? e - s : s - e;
            while (sw < 0) sw += 2 * Math.PI;
            return sw;
        }

        private static double AngleFromStart(ProfileSegment a, double x, double y)
        {
            double s = Math.Atan2(a.Y0 - a.Cy, a.X0 - a.Cx), p = Math.Atan2(y - a.Cy, x - a.Cx);
            double t = a.CounterClockwise ? p - s : s - p;
            while (t < -1e-9) t += 2 * Math.PI;
            return t;
        }

        public static double DistanceToSegment(ProfileSegment s, double x, double y)
        {
            if (!s.IsArc)
            {
                double dx = s.X1 - s.X0, dy = s.Y1 - s.Y0, l2 = dx * dx + dy * dy;
                double t = l2 > 0 ? Math.Max(0, Math.Min(1, ((x - s.X0) * dx + (y - s.Y0) * dy) / l2)) : 0;
                return Dist(x - (s.X0 + t * dx), y - (s.Y0 + t * dy));
            }
            double ang = AngleFromStart(s, x, y);
            if (ang <= Sweep(s)) return Math.Abs(Dist(x - s.Cx, y - s.Cy) - s.Radius);
            return Math.Min(Dist(x - s.X0, y - s.Y0), Dist(x - s.X1, y - s.Y1));
        }

        private static Vec3 Snap(Vec3 d, double deg)
        {
            d = d.Normalized();
            if (deg <= 0) return d;
            foreach (var a in new[] { Vec3.UnitX, Vec3.UnitY, Vec3.UnitZ })
                if (Math.Abs(d.Dot(a)) > Math.Cos(deg * Math.PI / 180)) return d.Dot(a) > 0 ? a : -a;
            return d;
        }

        private static double Dist(double x, double y) => Math.Sqrt(x * x + y * y);
        private static double Sq(double x) => x * x;
    }
}
